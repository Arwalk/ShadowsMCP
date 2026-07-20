# Ground-truth notes (from decompiled Assembly-CSharp, game v2.0)

Every game API the mod touches, verified in the decompiled sources (`tools/decompile.sh` →
`decompiled/Assets/Code/...`). File references are to the decompiled tree.

## Game version

`World.versionNumber = 2`, `subversionNumber = 0` → version string **"2.0"** (`World.cs:103`).
`mod_desc.json.versionsSupported` entries are compared by **exact string equality** to
`versionNumber + "." + subversionNumber` (`EventManager.loadModSurface`). A mismatch only pops
an "incompatible" warning and flags the mod — it still loads. If a folder named `v2.0` exists
inside the mod folder, content is loaded from there instead (per-version packaging).

## Mod loading pipeline (EventManager.cs, UIMainMenu.cs, World.cs)

- Local mods live in `<game>/data/optionalData/<ModName>/` (scanned by
  `EventManager.loadModSurfaces("./data/coreData", "./data/optionalData")`, `World.cs:161`).
- `mod_desc.json` parsed via `JsonUtility.FromJson<EventModData>`: fields `displayedName`,
  `prefix`, `modCredit`, `description`, `versionsSupported` (all required by `validate()`).
- DLL loading (`EventManager.loadModContents` ~line 515): every `*.dll` in the mod folder gets
  `Assembly.LoadFrom`; every type subclassing `ModKernel` is instantiated via
  `Activator.CreateInstance` and added to `World.self.loadedModKernels`;
  **`onModsInitiallyLoaded()` is called immediately**, and *again* by `UIMainMenu.Update` once
  all mods finish loading → **the hook fires more than once; boot must be idempotent**.
- Exceptions during DLL load are swallowed → popup "Mod failed to load". **Never throw from
  onModsInitiallyLoaded**; catch and log instead.
- `map.mods = loadedModKernels` at game start (`World.startup`, `World.cs:451`).

## Mod config (ModConfigOptList.cs, PopupModConfig.cs)

- `mod_config.json` in the mod folder: `{ "name": ..., "options": [ {name, description,
  defaultValue, minValue, maxValue, isInteger ("true"/absent), defaultBoolValue} ] }`.
  `isInteger != "true"` (or absent) → bool option.
- User values persist to `<saveFolder>/modConfig_<title_lowercase_underscores>.mcfg`.
- Values are pushed to kernels via `receiveModConfigOpts_int/_bool(name, value)`:
  (a) when the player applies the mod-config popup, (b) at game start —
  `PopupModConfig.loadModConfigFromFile(modsLoaded, informMod: true)` in `UIMainMenu.bStart`.
  If the player never touched the config, the callbacks never fire → compiled-in defaults
  must equal the json defaults.

## Save / load (World.cs:870-1000) — CRITICAL for mod design

- Saves = FullSerializer (`fsSerializer.TrySerialize(typeof(Map), map)`) → compressed JSON.
  It serializes the **whole Map object graph including `map.mods`** (the ModKernel list!).
- On load, `map.mods` is **re-created by deserialization** (a *new* ModCore instance), then
  `mod.afterLoading(map)` is invoked on it.
- ⇒ The kernel class must hold **zero instance state**; all runtime state (server, dispatcher,
  registry, config) lives in statics. Never let game objects reference mod runtime objects.
- During save, `map.world` is temporarily nulled (single-threaded; harmless to us since all
  game access is main-thread marshalled).

## Static accessors

- `World.staticMap` (Map), `World.self` (World). `Map.world`, `map.overmind`, `map.world.ui`.
- Logging: `World.log(string)` gated by `World.logging`; `UnityEngine.Debug.Log` → Player.log.

## Map (Map.cs)

`units: List<Unit>` (:121), `locations: List<Location>` (:123), `socialGroups: List<SocialGroup>`
(:125), `persons: List<Person>` (:133), `majorLocations` (:77), `turn: int` (:145),
`overmind: Overmind` (:103), `param: Params` (:101), `world: World` (:93), `soc_dark: Society`
(:255), `wars: List<War>` (:259), `mods: List<ModKernel>` (:267), `worldPanic: double` (:175),
`awarenessOfUnderground: double` (:177), `data_victoryProgess: double` (:163),
`grid: Hex[][][]` (:91), many `opt_*`/`param_*` fields.

- `turnTick()` (:3717): increments `turn`, fires `onTurnEnd` mods hook, processes everything.
- `getPathTo(Location a, Location b, Unit u = null, bool safeMove = false): Location[]` (:4469)
  — returns full path incl. start, or null. Also `(Location, SocialGroup, ...)` overload (:4519).
- `adjacentMoveTo(Unit, Location)` — single-step move used by tasks.

## End turn (World.cs:640 `bEndTurn(bool forceThrough = false)`)

The UI end-turn button → `world.bEndTurn()`. Guards (early return, silent): `map.automatic`
off + `turnLock` / `ui.blocker != null` / `selector != null`; commandable unit engaged this
turn (pops battle unless forceThrough → auto-resolves); pending skill points (pops level-up
unless forceThrough → auto-spends); idle-agent alert (`option_idleAlert` && task == null &&
movesTaken == 0 → selects unit, first pass assigns `Task_PassTurn`). Then: `turnLock = true;
map.turnTick(); turnLock = false; ui.checkData(); EventManager.turnTick(map);` then
`popAutosave()` every `autosavePeriod` (15) turns. **Synchronous on the main thread** → end_turn tool =
one dispatcher job with a long timeout; compare `map.turn` before/after; on no-advance, report which guard hit.
Note `popAutosave` only *raises* the `PopupAutosaveDialog` (via `ui.addBlocker`); the actual `world.save(...)`
runs inside that popup's `Update()` on the **next Unity frame**. A forced `end_turn` creates and destroys the
popup within one dispatcher job (no frame between), so the mod flushes the save before dismissing it — see the
autosave note under "Headless auto-dismiss" below.

## Units (Unit.cs, UA.cs, UM.cs)

Unit fields: `map`, `personID` (persons index, -1 if none), `homeLocation`, `locIndex`
(location index; `location` is a property), `society: SocialGroup`, `task: Task`, `hp`,
`maxHp`, `movesTaken`, `isDead`, `turnLastEngaged`, `engagedBy: Unit`, `engaging: Unit`,
`moveType` (NORMAL/DESERT/ORC), `rituals: List<Challenge>` (:49), properties `location`,
`person`, `menace`, `profile`. Methods: `getName()` (:508), `isCommandable()` (:604),
`getMaxMoves()` (:247).

- `UA : Unit` — agents. `playerTriesToStartChallenge(Challenge)` is **internal** (UA.cs:870)
  → replicate its guard+commit sequence (below). Subclass families: UAG (heroes), UAA
  (acolytes), UAE (player agents), UAEN (neutral).
- `UM : Unit` — military. `playerTriesToStartChallenge` is **public** (UM.cs:69) with a
  simpler sequence; `playerOrdersAttack(UM)` (:102).

Task subtypes decoded by `Summaries.TaskDetail`: `Task_PerformChallenge` (`challenge`,
`progress`, `turnsTaken`), `Task_GoToLocation` (`target: Location`), and the enemy-intent
tasks — `Task_AttackUnit` (`target: Unit`, `turnsRemaining: int`; Task_AttackUnit.cs:24,26),
`Task_DisruptUA` (`other: UA`, `turnsLeft: int`; Task_DisruptUA.cs:8,10), `Task_Bodyguard`
(`target: Unit`, `turnsRemaining: int`, `targetChallenge: Challenge`; Task_Bodyguard.cs:7,9,13).

## Player movement (UIInputs.cs:630 `rightClickOnHex`)

Guards: `isCommandable()`; `engagedBy != null && turnLastEngaged == map.turn` → blocked
("under attack"); `task is Task_Disrupted` → blocked. If already moving to a location and
clicked again on the unit's own location → `task = null` (cancel). Commit:
```csharp
u.task = new Task_GoToLocation(loc);
if (u.movesTaken < u.getMaxMoves()) u.task.turnTick(u);   // move immediately with remaining moves
```
Warns (confirm dialog) when abandoning a `Task_PerformChallenge` whose progress >4 turns
(`progress / max(1, challenge.getProgressPerTurn(ua, null)) > 4` and
`!challenge.ignoreInterruptionWarning()`).

`Task_GoToLocation(Location loc)` (single-arg ctor); self-cancels when no path; uses
`getPathTo(unit.location, target, unit, !unit.society.isAtWar())` then falls back to
`safeMove` field.

## Challenges (Challenge.cs, Location.cs:98, UA.cs:870, Task_PerformChallenge.cs)

- `location.GetChallenges()` returns `standardChallenges`; refreshed by
  `location.populateStandardChallenges()` (properties + settlement + subsettlements + units +
  `mod.populatingChallenges` + stale-claim cleanup).
- Unit-carried rituals: `unit.rituals` (List<Challenge>, `Ritual : Challenge` subclass).
- Challenge API: `getName()`, `getDesc()`, `valid()`, `validFor(UA)`, `validFor(UM)`,
  `getMenace()`, `getProfile()`, `getDanger()`, `getComplexity()`,
  `getProgressPerTurn(UA, List<ReasonMsg>)`, `claimedBy: Unit`, `location` (property via
  `locationIndex`), `allowMultipleUsers()`, `onImmediateBegin(Unit)`,
  `ignoreInterruptionWarning()`, `isIndefinite()`.
- **UA start sequence** (replicated from internal `playerTriesToStartChallenge`):
  guards: isCommandable; engaged-this-turn; `c.valid()`; `c.validFor(ua)`;
  `task is Task_Disrupted`; claim conflict (`!allowMultipleUsers && claimedBy` at location
  performing it). Commit: clear own claims on `location.GetChallenges()` and own `rituals`;
  `task = new Task_PerformChallenge(c); c.claimedBy = ua;` foreach mod
  `onPlayerStartsChallenge(ua, c)`; `c.onImmediateBegin(ua)`; `ui.checkData()`.
- **UM start sequence** (public UM.playerTriesToStartChallenge): isCommandable, valid,
  validFor(um), claim conflict; clear own claims; `task = new Task_PerformChallenge(c);
  c.claimedBy = um; ui.checkData()`.
- Remote challenge: `Task_GoToPerformChallenge(Challenge c)` (used by AI & UI flows).

## Commandable-military special orders — Raze / Drive Back / Attack (UM.cs, UIScroll_Unit.cs:456-499)

A **third action category** beside challenges and powers: direct command methods on `UM`, which the game UI
hand-builds as buttons for a selected commandable military unit. They are **not** `Challenge`s (never in
`location.GetChallenges()` — the UI wraps them in cosmetic `UIE_Challenge` boxes with `special=1/2/3`) and
**not** `Power`s, so they surface through no `getChallenges`/`getPowers` accessor. Replicated by the mod's
`command_army` tool and advertised by `Summaries.UnitOrders` (get_unit/list_units `orders`).

- **Raze settlement** — `UM.playerCommandsRazeSettlement()` (UM.cs:174). Gate: `isCommandable()` AND
  `um.location.settlement is SettlementHuman` AND `!(task is Task_InBattle)`. **No target arg** — acts on the
  unit's own tile. Commit: `task = new Task_RazeLocation{ ignorePeace = true }` (drains `settlement.defences`
  each turn until `fallIntoRuin`). This is how an awakened `God_Snake` / `UM_SheWhoWillFeast` wins.
- **Drive back a hero** — `UM.playerCommandsDriveBack(UA)` (UM.cs:187). Target: a `UA` in `um.location.units`
  with `!target.isCommandable()`. Gate: `!(task is Task_InBattle)`. Forces the hero to drop its task and
  retreat to a neighbour.
- **Attack an army** — `UM.playerOrdersAttack(UM)` (UM.cs:102, virtual). Target: a `UM` in
  `um.location.units` with `!target.isCommandable() && target.society != um.society`. Gate:
  `!(task is Task_InBattle)` AND `movesTaken < getMaxMoves()`. Starts a `BattleArmy`.

All three end with `map.world.ui.checkData()`. The UI only offers drive-back/attack against units **sharing
the commander's tile** (it iterates `um.location.units`), so `command_army` enforces `target.location ==
um.location`.

## Powers (Power.cs, God.cs, Overmind.cs, UIE_GodPower.cs, Sel_CastPower.cs)

- `overmind.power: double` is the resource; `overmind.god.getPowers(): List<Power>`;
  `god.powerLevelReqs: List<int>`.
- Power API: `getName()`, `getDesc()`, `getCost()`, `validTarget(Unit)`,
  `validTarget(Location)`, `cast(Unit)`, `cast(Location)`, `isPassiveOnly()`,
  `getRestrictionText()`. `castCommon` **deducts the cost** (`overmind.power -= getCost()`).
- UI flow: castable iff `overmind.power >= getCost()`; then a target selector calls
  `power.validTarget(unit) → power.cast(unit)` else `validTarget(hex.location) →
  cast(location)` (Sel_CastPower.onClick).

## People & societies

- `Person` (Person.cs): `index` (**stable native id**), `society: Society`, `house: House`,
  `unit: Unit`, `traits: List<Trait>`, `firstName`, `getName()`, `getFullName()`, `prestige`,
  `shadow`, `awareness`, `sanity`/`maxSanity`, `state` (personState), `isDead`, `rulerOf`
  (location index, -1 if none), `age`, `gold`, `level`, `XP`, `skillPoints`, `stat_might`,
  `stat_lore`, `stat_intrigue`, `stat_command`, `items: Item[3]`.
- `SocialGroup` (SocialGroup.cs): `index` (**stable native id**), `name`, `getName()` (:296),
  `map`, `relations: Dictionary<SocialGroup, DipRel>`, `getRel(SocialGroup)` (:256),
  `isAtWar()` (:184), `menace`, `currentMilitary`/`maxMilitary`.
- `Society : SocialGroup`: `posture`, `capital` (location index), `isRebellion`,
  `isDarkEmpire`, `isAlliance`, `actionUnderway: AN`.
- `DipRel`: `status: double`, `state: dipState` (enum incl. war), `war: War`.

## Locations

`Location` (Location.cs): `index` (**stable native id**), `hex: Hex`, `soc: SocialGroup`,
`settlement: Settlement`, `map`, `name`, `shortName`, `isCoastal`, `isOcean`, `isMajor`,
`province`, `culture`, `links: List<Link>`, `getNeighbours(): List<Location>` (:173),
`properties: List<Property>` (:46), `units: List<Unit>` (:48),
`getName(bool incLocation = true)` (:76), `GetChallenges()` (:98).
`Settlement`: `name`, `shadow`, `defences`, `isHuman`, `isInfiltrated`, `subs`
(subsettlements), `getChallenges()`. `Hex`: `x`, `y`, `z` (layer), `terrain`, `locationIndex`.

## Overmind (Overmind.cs)

`power: double`, `god: God`, `agents: List<Unit>`, `enthralled: Person`,
`availableEnthrallments`, `nEnthralled`, `sealsBroken`, `sealProgress`, `victoryMode`
(+ VICTORY_MODE_* consts), `victoryAchieved`, `endOfGameAchieved`, `panicFrom*` fields.

`getThreats(): List<MsgEvent>` (Overmind.cs:740) — the game's built-in threats panel;
surfaced by `get_threats`. `MsgEvent` (MsgEvent.cs): `msg: string`, `priority: double`
(severity; higher = more pressing), `beneficial: bool`, `hex: Hex` (target; `Hex.locationIndex`
== -1 when none, else `Hex.location`). Note: `getThreats()` has benign side effects (writes
`SocialGroup.data_highestAttackThreat`, calls `getAttackUtility`) — same as the in-game panel.

## Recruitment / enthrallment (Overmind.cs, UAE_Abstraction.cs, Sel_CreateAgent.cs, PopupAgentCreation.cs)

Recruiting agents is **not** a castable `Power` for most gods — it goes through a popup + map selector,
which the mod replicates directly (`recruit_agent` in `ActionTools.cs`; `list_recruitable_agents` in
`QueryTools.cs`).

- **Points**: `overmind.availableEnthrallments` (starts 2, `Overmind.cs:623`); regenerates every
  `param.overmind_enthrallmentUseRegainPeriod` turns up to `param.overmind_maxBankedEnthrallments`
  (`Overmind.cs:107-111`). **Cap**: `overmind.getAgentCap()` = `god.getAgentCaps()[sealsBroken]`
  (`Overmind.cs:200-203`; default `{2,3,4,5,6}`, `God.cs:128`), grows as seals break.
- **`calculateAgentsUsed()`** (`Overmind.cs:574`) recomputes `agents`/`nEnthralled` from `map.units`; call
  it before reading `nEnthralled` or the cap.
- **Templates**: `overmind.agentsGeneric` / `agentsUnique` are `List<UAE_Abstraction>` — one per archetype
  (`UAE_Abstraction.code`, consts `CODE_WARLOCK=-3`…`CODE_SEEKER=15`, `code==0` = corrupt an existing hero
  in place). API: `getName()`, `getDesc()`, `getRestrictions()`, `getStat{Might,Intrigue,Lore,Command}()`,
  `validTarget(Location)` (rejects when `nEnthralled >= getAgentCap()`), `createAgent(Location)`.
- **`createAgent(loc)`** (`UAE_Abstraction.cs:1063`) constructs the concrete `UAE_*` unit, appends to
  `map.units` / `overmind.agents`, sets `person.shadow=1`, `state=enthralled`, `skillPoints++`, decrements
  `availableEnthrallments`, and (when `!map.automatic`) pops a `PopupAgentLevelup` for the new skill point.
  For `code==0` it instead flips the hero's `corrupted=true`, adds it to `agents`, and reuses its own
  location (no new unit appended).
- **Commit sequence** (from `Sel_CreateAgent.onClick`, `Sel_CreateAgent.cs:27-41`): `validTarget(loc)` →
  `createAgent(loc)` → `foreach mod: mod.onAgentCreated(map.units[last])`, guarded by `!map.tutorial`.
- **Corruptible-hero scan** (`PopupAgentCreation.populate`, `PopupAgentCreation.cs:174-195`, mirrored by
  `Summaries.IsCorruptibleHero`): live `UA` that is `UAG`/`UAA`, not commandable, no `T_ChosenOne` trait,
  `person.shadow >= 0.98 || person.isInsane()`.

## End of game (Overmind.cs)

Losing all your agents is **not** a loss — you are the god, and points regenerate. The game ends only when
`overmind.endOfGameAchieved` is set: by `Overmind.victory()` (`:981`, also sets `victoryAchieved`, sets
`victoryMode` 0-5) or `Overmind.defeat(msg)` (`:1116`, heroes reforge the seals / fulfil the prophecy, monster
hearts slain, etc. — never from unit count). The mod surfaces `endOfGameAchieved` + `defeated`/`victoryMode`
in `game_overview` / `get_player_state`, and `end_turn` returns `gameOver` (with outcome) without advancing
once it is set (`ActionTools.EndTurn`). Agent death itself is never a game-over. It surfaces in one of two
ways: a natural/challenge death raises the informational `PopupMsgAgentsDeath` notice (kind:`death`,
`IsInformational` → auto-dismissable under `force`), while losing an agent **in battle** raises a
`PopupEvent` "Defeat" (kind:`event`) — a narrative event, so it is preserved under `force` like any event
and must be answered via `resolveOptionIndex`/`resolve_decision`, not auto-dismissed.

## Decision windows / popups (UIMaster.cs, PopupEvent.cs, PopupAgentLevelup.cs, ModKernel.cs)

The game is single-threaded UI: it "waits for the player" whenever a **modal blocker** is open.

- **The signal**: `map.world.ui.blocker` (`GameObject`, `UIMaster.cs:49`) is non-null while a modal
  popup is showing; queued popups sit in `ui.blockerQueue`/`blockerQueueDelayed` and are promoted
  by `checkBlockerQueue`/`removeBlocker`. This is the same field `bEndTurn` guards on.
- **Push hook**: `ModKernel.onUIFullscreenBlockerUpdate(GameObject blocker)` (`ModKernel.cs:181`)
  is called on every mod on each blocker change (open/close/promote). The mod overrides it only to
  log; the decision tools read `ui.blocker` live instead (no state to go stale across save/load).
- **Identify the popup**: `blocker.GetComponent<PopupEvent>()` / `GetComponent<PopupAgentLevelup>()`
  etc. `~40 Popup*` MonoBehaviours exist; all shown via `ui.addBlocker*`.
- **Narrative events** — `PopupEvent` (`PopupEvent.cs`): `Button[] options` bound to
  `EventData.choices` (`EventData.cs`; `Choice{name,description,condition,outcomes}`). `populate`
  labels each active button (`GetComponentInChildren<Text>().text`), stores per-option help in
  `optDescs[4]`, **greys condition-failed choices to colour (0,0,0,0.5) and wires no listener**,
  and wires enabled ones to `dismiss(choice, ctx)` → `EventManager.chooseOutcome` + `ui.removeBlocker`.
  The context isn't stored on the popup, so **answering = invoking the chosen button's `onClick`**
  (replays the captured `dismiss(choice, ctx)`); success shows up as `ui.blocker` changing.
- **Level-ups** — `PopupAgentLevelup` (`PopupAgentLevelup.cs`): public `UA unit`; options are
  `Trait.getAvailableTraits(unit)` (`Trait.getName()`/`getDesc()`); public `choose(Trait)` does
  `person.skillPoints--; person.receiveTrait(t); ui.removeBlocker(...)`. `dismiss()` closes without
  spending. Triggered from `bEndTurn` via `prefabStore.popAgentLevelUp` when `skillPoints > 0`.
- **Every other popup — the generic path**: almost all of the ~51 `Popup*` classes wire their
  clicks through Unity Inspector `Button.onClick` → a `bXxx()`/`dismiss()` method (only `PopupEvent`
  and `PopupMinionDismissal` wire clicks in code). So the mod's `GenericButtonHandler` (registry
  fallback) drives any popup by `blocker.GetComponentsInChildren<Button>()`, listing each
  interactable button (label from its child `Text`/`TMP_Text`, or the `UIE_*` data object —
  `UIE_Trait.trait`, `UIE_GodPower.power`, `UIE_AgentSelect.abstraction`) and committing the chosen
  one with `button.onClick.Invoke()`. `force=true` dismisses: `dismissKeyHit()` if the popup is a
  `UI_Dismissable` (11 do — `PopupMsg`, `PopupConfirmOrder`, …), else `ui.removeBlocker(blocker)`.
  Exceptions whose main interaction isn't a button (still dismiss/cancel-able, flagged in the
  option note): item-trading (drag), mod-config (text/toggle), carousels (`PopupScrollSet`/
  `PopupXScroll`/`PopupBox*`), text-entry (`PopupSaveDialog`/`PopupMsgRenameAgent`/options), and the
  stepwise `PopupBattleAgent`. Some buttons (`UIE_GodPower.bCast`, `UIE_AgentSelect.bCast`) set
  `world.selector` instead of closing — the resolve result flags `openedSelector`.
- **Agent-death notice** — `PopupMsgAgentsDeath` (`PopupMsgAgentsDeath.cs`): raised inside
  `map.turnTick()` when one of your agents dies (`PrefabStore.popMsgAgentDeath → ui.addBlocker`, the
  immediate queue). Purely informational — `bDismiss`/`bDismissAgentA` both call `ui.removeBlocker`
  (the second pans the camera first). The mod's bespoke `PopupMsgAgentsDeathHandler` (registered
  before the generic fallback) labels the two buttons and answers by invoking `dismiss()` /
  `dismissAgentA()` directly. Because `bEndTurn` opens it *during* `turnTick` and returns (never
  blocking the main thread), the turn still advances; the popup just sits on `ui.blocker` afterward.
- **Headless auto-dismiss**: `end_turn(force:true)` calls `DecisionRegistry.AutoDismissInformational`,
  which force-dismisses purely-informational popups (deaths, `PopupMsg*`, autosave — the
  `IDecisionHandler.IsInformational` whitelist) in a loop so an unattended `end_turn(force)` never
  stalls on a notice. The autosave popup is special-cased in `GenericButtonHandler.Dismiss`
  (`FlushPendingAutosave`): its disk write lives in `PopupAutosaveDialog.Update()` (next frame), which never
  ticks during the same-job create+destroy, so the mod runs that save synchronously before dismissing —
  otherwise every forced batch would skip the game's 15-turn autosave. It stops at the first popup carrying a real choice (`PopupEvent`, or a
  `PopupAgentLevelup` *that still has an unspent skill point and traits to pick*) or any unknown popup,
  leaving it open and flagged for `resolve_decision` — never silently answered. Note the level-up
  subtlety: `bEndTurn(forceThrough=true)` bypasses the `ui.blocker` guard and **auto-spends** each
  commandable agent's skill point (`spendSkillPoint()` AI-picks a trait; `World.cs:688-691`) but does
  **not** close a level-up popup a prior non-force `end_turn` already opened. So by the time
  `AutoDismissInformational` runs, that popup's unit has `skillPoints == 0` →
  `PopupLevelupHandler.IsInformational` returns true → it is dismissed (rather than lingering
  banner-flagged across every subsequent forced end-turn, which was the pre-fix behaviour). Note the Unity gotcha the handlers guard against: after `removeBlocker`
  nulls `ui.blocker` and `DestroyImmediate`s the popup, `ui.blocker != blocker` reads **false**
  (Unity's `==` treats a destroyed object as equal to null), so success is checked as
  `blocker == null || ui.blocker != blocker`.
- **Non-modal blocks (no `ui.blocker`)**: some things stop `end_turn` without any popup. The
  **idle-agent alert** (`bEndTurn`, `World.cs:699`): with `option_idleAlert` on (default), a
  commandable `UA` whose `task == null && movesTaken == 0` makes `bEndTurn` just select the unit and
  return — no blocker. Resolve by ordering the agent or assigning `new Task_PassTurn()` (its
  `turnTick` clears itself). The mod models these as `INonModalDecision` (idle = `IdleAgentsDecision`),
  checked by `DecisionRegistry` only when `ui.blocker == null`, and surfaced/answered through the
  same `pendingDecision` / `get_pending_decision` / `resolve_decision` path as modal popups.
  The mod treats idle as a hard block even under `force` (mirroring combat): `AdvanceOneTurn` passes
  `force && !combatEngaged && !idleBlocks` to `bEndTurn` (idle detected directly via `AnyAgentIdle`, so a
  message/death popup on top can't mask it and let force slip), and `IdleAgentsDecision.Resolve` passes only
  on explicit `optionIndex 0`, never `force` — so a forced `end_turn` never silently wastes an idle agent's
  turn. `end_turn`'s `passIdleAgents:true` is the one deliberate escape: it assigns `Task_PassTurn` to every
  idle agent each turn and suppresses the re-raised idle decision, so an intentional multi-turn fast-forward
  keeps advancing.

Mod wrapping: `src/Mod/Tools/Decisions/` (handler per popup family + `DecisionRegistry`) and
`src/Mod/Tools/DecisionTools.cs` (`get_pending_decision`, `resolve_decision`). A pending decision
is also reported **in full** (options + indices) in `game_overview.pendingDecision`, banner-stamped
on every tool result (`GameToolHost`), and — because some MCP clients leave the two decision tools
deferred and never load them — resolvable straight through **`end_turn`**: it returns the pending
decision when blocked and takes `resolveOptionIndex` to answer it (then continues ending the turn),
so an agent that only ever loaded `game_overview` + `end_turn` can still see and resolve every popup.
`PopupEvent` also accepts `force=true` as a last-resort escape (takes the first available choice). Requires the `UnityEngine.UI` + `Unity.TextMeshPro` references (Button/Text/Image,
TMP_Text). All UI-field access is confined to `Tools/Decisions/`.

## Entity id scheme (decided)

Native indices exist for **locations, persons, social groups** → ids `L<index>`, `P<index>`,
`SG<index>` resolved by scanning the map lists for a matching `index` field. Units and
challenges have no stable native id → session-scoped registry ids `U<n>`, `C<n>` (weak refs).

## Extended detail views & analysis tools (game members touched)

On top of the base surface, the enriched `*Detail` serializers and the four analysis tools
(`list_wars`, `list_investigations`, `list_holy_orders`, `get_recent_events`) read these members —
all verified public in v2.0. Field access stays confined to `Summaries.cs` (builders) and the tool
bodies in `QueryTools.cs`.

- **Settlement economy** — `SettlementHuman`: `population`, `prosperity`, `growingPop`,
  `foodLastTurn`/`foodLocal`/`foodImported`, `heir` (prop over `heirIndex`), `supportedMilitary`
  (`UM_HumanArmy`), `order` (`HolyOrder`), `shadowPolicy` (`shadowResponse` enum). Base `Settlement`:
  `actionUnderway` (`Action.getName()`), `actionProgress`. `Property.influences` (`List<ReasonMsg>`,
  each `msg`/`value`) alongside `charge`.
- **Person sheet** — `Person`: `XP`, `XPForNextLevel`, `statistic_kills`, `targetPrestige`,
  `watched`, `species` (`Species.name()`), `house` (`House.name`, `House.curses` → `Curse.getName()`),
  `alert_maxShadow`/`alert_halfShadow`/`alert_aware`, relationships `likes`/`hates`/`extremeLikes`/
  `extremeHates` (`List<int>` person indices, resolved via new `Summaries.FindPerson`).
  `Trait.getDesc()` / `Item.getShortDesc()` back the `{name,desc}` trait/item objects (was name-only).
- **Agent internals** — `UA`: `corrupted`, `getStatAttack()`, `challengesSinceRest`, `turnsIdle`,
  `disruptionExhaustion`, `minions` (`Minion[3]`; `Minion.hp`/`defence`/`isDead`).
- **Detection economy** — `Location.evidence` (`List<Evidence>`): `Evidence.pointsTo`/`pointsToPerson`/
  `assignedInvestigator`/`weight`/`rumourCounter`/`turnDropped`/`locationFound`/`reportedToSociety`.
  "Against my interests" mirrors get_threats: `pointsTo.isCommandable() || pointsTo is UAE`.
- **Diplomacy / NPC intent** — `Society`: `offensiveTarget`/`defensiveTarget`,
  `data_highestInternationalTension`, `actionUnderway` (`AN.getName()`/`getShortDesc()`/
  `getTurnsRequired()`), `actionProgress`. `DipRel.war` / `Map.wars` (`War`): `att`/`def`/`startTurn`/
  `attackerObjective` (`warType`)/`canTimeOut`/`turnOfEnd()`.
- **Religion** — `HolyOrder` (a `SocialGroup`): `enshadowment`, `nAcolytes`/`nTemples`/`nWorshippers`/
  `nWorshippingRulers`, `reserves`, `influenceElder`/`influenceHuman`, `worshipsThePlayer`, `prophet`
  (`UA`), `divinity` (`DivineEntity.getName()`), `tenets` (`List<HolyTenet>` → `getName()`).
- **God win-condition sheet** — `God`: `getMaxTurns()`, `getMaxPower()`, `getSealLevels()`,
  `getAgentCaps()`, `powerLevelReqs`, `getDetailedMechanics()`, `getSealDesc()`, `powerIncreaseText()`,
  and `getVictoryMessage(mode)`. `Map.opt_endless` gates `turnsRemaining`. `Overmind.victoryMode` is `-1`
  until a win is recorded (set 0–5 in `victory()`), so both it and the mode-keyed victory message are
  surfaced **only** once `endOfGameAchieved`.
- **Panic breakdown** — `Overmind`: `panicFromPowerUse`, `panicFromCluesDiscovered`, `panicHeroesFallen`,
  `panicTemporaryChange` (surfaced in `game_overview.panic` / `get_player_state.panic`).
- **Recent events** — `get_recent_events` reads the mod-owned `RecentEventLog` (held on `GameContext`, so
  it never touches saves), **not** `Map.turnUnifiedMessages` directly. That collection is wiped at the top
  of every `turnTick()` and only carries `addUnifiedMessage` output, so read on its own it yields an empty,
  single-turn feed — the death/level-up/narrative popups go through separate `PrefabStore.pop*` blockers
  that append to no list. Instead `end_turn` snapshots each turn's `turnUnifiedMessages` (`title`,
  `message`, `msgType`/`customMsgType`) into the log before the wipe (`RecentEventLog.SnapshotTurn`), and
  the decision layer appends the agent-death, level-up and narrative-event popups it dismisses/resolves
  (`DecisionRegistry`, kinds `death`/`levelUp`/`event` — which never call `addUnifiedMessage`, so they
  duplicate nothing). Items are `{turn, type, title, message?, resolution?}`, newest-first; the log is
  bounded and cleared on new game/load alongside the entity registry.
- **Combat odds / risk (`Summaries.ComputeAgentSafety`)** — `UA.getDangerEstimate()` (`UA.cs:467`, int =
  `hp + defence + attack + Σ minion`; the engine's own unit-vs-unit strength, differenced at `UA.cs:708`),
  `UA.getMaxDefence()` (`UA.cs:263`), `UA.getAttackUtility(Unit other, List<ReasonMsg> reasons, bool
  includeDangerousFoe = true)` (`UA.cs:668`), `Map.getStepDist(Location, Location)` (`Map.cs:4307`), and the
  `Task_InHiding` task type (`Task_InHiding.cs:3`). The scan mirrors the hunt loop inside
  `Overmind.getThreats` (`Overmind.cs:784-818`): per commandable UA it ranks hostile heroes (skipping
  `isCommandable()` and `UAEN`) within `profile / 5` steps by `getAttackUtility`, normalising the positive vs
  negative `ReasonMsg.value`s into a motivation %. The **isHuntable** flag is the human-ruler assassination
  trigger `unit is UA { profile: >=50.0, menace: >25.0 }` from `SettlementHuman.getLocalActions()`
  (`SettlementHuman.cs:432`, spawns `Act_AttackAgent`). Surfaced as `get_threats.agentSafety`,
  `get_unit.combat`, the `game_overview.threats` breadcrumb, and the `end_turn.threatAlert` before/after diff.
- **Challenge lock reason** — `Challenge.getRestriction()` (`Challenge.cs:59`, free-text hint e.g. "Requires
  100% Infiltration. Cannot perform if Ward is higher than 50%"; shown in-game at
  `UISideChallengeDetails.cs:55`). Surfaced as `restriction` on every challenge and appended to
  `perform_challenge`'s `valid()`/`validFor()` rejection messages.
- **Infiltration detail** — `Settlement.infiltration` (`Settlement.cs:56`, computed 0..1 fraction of
  infiltrated infiltratable subs; 1.0 when `isInfiltrated`) plus per-district `Subsettlement.infiltrated`
  (`Subsettlement.cs:11`) and `Subsettlement.menace` (`Subsettlement.cs:13`). `get_location`'s
  `subsettlements` entries became `{name, infiltrated}` objects (were bare `sub.getName()` strings); also on
  `world_summary`. These back the Enshadow / Desecrate `restriction` gates.
- **World map (`world_summary` / `Summaries.WorldSummaryRow`)** — walks `map.locations` and, per location,
  reuses the `LocationDetail` reads (`Location.hex.x/y/z`, `Location.soc`, `Settlement` essentials) plus a
  capital flag `((Society)Location.soc).capital == Location.index` (the exact test at `Sub_City.cs:116`).
  Agent inventory (`Person.items`, `Item.getName()`/`getShortDesc()`) now also appears on `get_unit` as
  `items`, not only on `get_person`.
- **Victory breakdown** — `Overmind.computeVictoryProgress()` (`Overmind.cs:368`) returns the full
  human-readable 8-category scoring sheet; `get_victory_breakdown` returns it verbatim, plus
  `Map.data_avrgEnshadowment` (`Map.cs:157`) also added to `game_overview`. **Display-safe**: the game's own
  HUD/tooltip calls it every refresh (`UITopRight.cs:215`, `PopupVictoryStats.cs:53`). Caveat — it recomputes
  and writes the `map.data_*` victory fields and, only at threshold, would call `victory()`/`defeat()` (the
  same as the next `turnTick` would); harmless to call for display, and all tools are main-thread-marshalled.
- **Seal countdown (`Summaries.SealTiming`)** — combines `Overmind.sealsBroken`/`sealProgress` with
  `God.getSealLevels()` (already listed) to derive `nextSealAt = getSealLevels()[sealsBroken]` and
  `turnsToNextSeal = nextSealAt - sealProgress`. Surfaced flat on `game_overview.seals` /
  `get_player_state.seals`. (Meaningful for gods using conventional seals — `God.usesConventionalSeals()`.)
- **Mechanics tips (`src/Mod/Tips/TipCatalog.cs`)** — the curated `get_tips` catalog and the contextual `tips[]`
  on `game_overview`/`end_turn` read these (all public in v2.0; unlike the rest of the surface, this field access
  lives in `TipCatalog.cs`, not `Summaries.cs`/tool bodies). Trigger predicates: `Map.worldPanic` (≥0.1, mirrors
  the game's WORLD_PANIC hint at `Map.cs:3804`), `Map.awarenessOfUnderground` (≥0.1 — a **mod** design threshold;
  the game's AWARENESS hint is event-driven with no numeric gate), `Map.wars` (existence), a `Map.units` scan for
  `Unit.isCommandable()` + `Unit.task is Task_PerformChallenge` whose `.challenge is Ch_Infiltrate` and
  `Location.settlement.getSecurity(null) > 5` (mirrors the HIGH_SEC trigger at `Task_PerformChallenge.cs:58`),
  `Overmind.god is God_Vinerva/God_Ophanim/God_LaughingKing` (**Iastur IS the Laughing King — there is no
  `God_Iastur` type**), and a `Map.socialGroups` scan for `Society.isDarkEmpire` / `SG_DeepOnes` (or
  `Overmind.deepOnesRiseUp`) / `Soc_Elven`. Two tip bodies interpolate live params —
  `Map.param.utility_person_FromLiking`/`utility_person_FromExtremeLiking` (tags) and
  `Map.param.prop_opha_faithWorldShadowReq`/`prop_opha_faithOwnShadowReq` (Ophanim faith) — falling back to
  `World.staticMap` (then to number-free wording) so `get_tips` works from the main menu. Shown-once state is the
  mod-owned `GameContext.ShownTips` (`HashSet<string>`, the analogue of `HintSystem.hasShown[]`), cleared on new
  game/load in `ModCore.OnMapSeen` alongside the entity registry and event log.

## Misc

- The game ships `Newtonsoft.Json.dll` in Managed (unused by us; JsonUtility is what the
  game itself uses for mod files).
- **Wire format (token efficiency):** tool JSON payloads are serialized compact (no indentation) and
  **omit null-valued object keys** — `ToolResult.Ok(JsonValue)` → `JsonWriter.Write(payload,
  pretty:false, omitNull:true)`. Absent ≡ null for a consumer. Array elements (including nulls) are
  kept to preserve index alignment; only object members are pruned. `inspect` opts out
  (`ToolResult.Ok(payload, omitNull:false)`) so reflection still shows a field that is null.
- `ReasonMsg` has `msg`/`value` fields — used for utility breakdowns.
- Player's faction: `map.soc_dark`; commandable check is `unit.isCommandable()`.
