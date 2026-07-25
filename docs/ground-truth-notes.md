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

## Headless new-game start (LifecycleTools.new_game; UIMainMenu.cs, World.cs, PopupGameOptions.cs)

- The human path: `UIMainMenu.bStart` (guards `ui.blocker == null && starting == 0`) does
  `world.map = new Map(world.param); World.staticMap = world.map` → `startProper()`:
  `PopupModConfig.loadModConfigFromFile(modsLoaded, informMod: true)`, fires
  `onStartGamePresssed(map, godList)` on every kernel (mods may add gods), appends
  `God_Eternity`/`God_Cards`/`God_Underground`, then **`god.setup(world.map)` on every god**
  (this builds the god's power list — skip it and `list_powers`/`use_power` break) → god click
  sets `world.chosenGod` → `PopupGameOptions.startGame()` → `world.startup(options)`.
- `World.startup(opts)` (World.cs:445) is fully synchronous: sets `map.mods = loadedModKernels`,
  copies options onto the map, `map.gen()`, rewinds `map.turn` by `mapGen_burnInSteps` (150) and
  `turnTick()`s them back (the burn-in history sim), applies difficulty multipliers,
  `ui.setToWorld()`, fires `afterMapGenAfterHistorical` on every kernel (→ ModCore.OnMapSeen
  picks up the map), then one real `turnTick()`. Takes ~30-120 s wall-clock.
- **`GameOptions.turnLimit` is INVERTED**: `true` ⇒ `map.opt_endless = true` (World.cs:459 —
  no time-out defeat); the options popup confirms it (`tEndless.isOn = !options.turnLimit`,
  PopupGameOptions.cs:283). The `new_game` tool exposes the intuitive polarity
  (`turnLimit:true` = limit enforced) and negates internally.
- In-place map replacement over a running game **without scene reload or cleanup is supported** —
  the game's own `WHILE_TRUE_RESTART` debug loop (World.cs:386-404) does exactly that repeatedly.
  Only caveat: null `GraphicalMap.selectedUnit`/`selectedHex` first, or `ui.checkData()` inside
  startup can touch units of the discarded map.
- `Eleven.random` is reseeded from `opts.seed` inside `startup` — never consume it pre-start
  when picking random tool defaults (use a fresh `System.Random`), or determinism breaks.
- `new_game` runs as ONE main-thread dispatcher job (server-thread registration, own
  `NewGameTimeoutMs` budget). A started job cannot be cancelled: if the tool call times out the
  game still finishes starting — the tool description tells the agent to check `game_overview`
  instead of retrying.

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

## Agent-vs-agent actions — Attack / Rob / Trade / Follow (UA.cs:950-1105, UIScroll_Unit.cs:331-380)

The same shape one level down: `UIScroll_Unit` walks `ua.location.units` for the selected agent and adds a
cosmetic `UIE_Challenge` box per other `UA` on the tile, wired to a `UA.playerTriesTo*` method. Not
`Challenge`s, not `Power`s. Replicated by `command_agent`, advertised by `Summaries.UnitOrders` →
`AgentOrders`. Every one of them first refuses if `engagedBy != null && turnLastEngaged == map.turn`
("must resolve this combat before taking action").

- **Attack** — `playerTriesToAttack(UA other)` (UA.cs:950). Offered when `!other.isCommandable()`. Further
  gates: `other` not itself engaged this turn; no `UA` on the tile whose `Task_Bodyguard.target == other`;
  `task is Task_Disrupted` blocks. A `Task_PerformChallenge` with >4 turns of progress pops
  `popConfirmOrder` first (the mod's `force` flag). Commit: `other.task = null;
  new BattleAgents(this, other)` + `prefabStore.popBattle(battle)` → the `PopupBattleAgent` the mod already
  drives. **`BattleAgents.setupBattle()` (BattleAgents.cs:69) nulls a `Task_PerformChallenge` on BOTH sides**
  before round 1, so starting the fight destroys the target's ritual permanently — win, lose, flee, or
  retreat. That is the sanctioned counter to the Chosen One's ritual, and it costs the attacker its own
  in-progress challenge.
- **Rob** — `playerTriesToRob(UA other)` (UA.cs:1039). Offered for a non-commandable `UAG`/`UAA`, disabled
  unless `other.person.level < person.level`. Also gated on `map.turn - turnLastDidRobbery >= 5` (unless
  `turnLastDidRobbery == 0`) and `!(task is Task_Disrupted)`. Commit order:
  `addProfile(param.ua_robProfileGain)` (5), `addMenace(param.ua_robMenaceGain)` (15),
  `turnLastDidRobbery = map.turn`, `popItemTrade(person, other.person, "Stealing Items")` — **the cost is
  paid before the window opens**, so closing it empty-handed still costs profile and menace.
- **Trade** — `playerTriesToTrade(UA other)` (UA.cs:1004). Offered when `other.isCommandable()`. Only the
  engagement gate — notably it does **not** check `Task_Disrupted`. Commit: `popItemTrade(person,
  other.person)`.
- **Follow** — `playerTriesToFollow(Unit other)` (UA.cs:1019). Offered only when `this is UAE_Harvester` and
  `other is UAG`. Commit: `task = new Task_Follow(this, other)`, then a `popMsg(..., force:true)`
  confirmation the mod deliberately skips (a blocker with nothing to decide).
- **Disrupt** — `playerTriesToDisrupt(UA)` (UA.cs:1069) exists and `UIE_Challenge.setToDisrupt` exists, but
  **nothing wires them**: no UI path reaches it, so `command_agent` does not expose it.

Player-initiated attacks are always **same-tile and immediate**. The AI's travel-then-attack `Task_AttackUnit`
is not a player verb, and on a commandable attacker it resolves the duel through `BattleAgents.automatic()`
(Task_AttackUnit.cs:114) — fought to the death with no flee/retreat menu — which is why `command_agent` has
no "pursue" mode.

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
  option note): item-trading (drag), mod-config (text/toggle), the horizontal god carousel
  (`PopupXScroll`/`PopupXBoxGodSelectMsg`), text-entry (`PopupSaveDialog`/`PopupMsgRenameAgent`/options),
  and the stepwise `PopupBattleAgent`. Some buttons (`UIE_GodPower.bCast`, `UIE_AgentSelect.bCast`) set
  `world.selector` instead of closing — the resolve result flags `openedSelector`.
- **Selection carousels** — `PopupScrollSet` (`PopupScrollSet.cs`), built by
  `PrefabStore.getScrollSetText`/`getScrollSetAgents`/`getScrollSet`. In-game callers: the Cause Scandal
  victim pick (`Rt_CauseScandal.complete` → `Sel2_CauseScandal`), `Ch_GuardRuins` (minion to assign),
  `P_ForIdleHands`/`P_DevilMakesWork` (tag to like/dislike) and `Overmind_Automatic`. State: a public
  `List<PopupScrollable> scrollables` (display order) plus a public `int index` = the highlighted entry;
  `bSelect()` does `removeBlocker` then `scrollables[index].clicked(map)`, and `bCancel()` notifies
  `cancelReceiver` then closes. So **assigning `index` and calling `bSelect()` is exactly a human click**
  on that entry — the mod's `PopupScrollSetHandler` (registered before the fallback) does that, listing
  the entries themselves as `options` with the current one flagged `selected` and `selectedIndex` echoed.
  Before it existed, the button sweep only exposed next/prev/select/cancel, so an agent committed blind
  (a playthrough picked its scandal victim by luck). Each box also stores its own receiver-side `index`
  and passes it in `clicked()`, so direct assignment stays correct under `invertOrder`. **Label trap**:
  read labels off the `PopupScrollable` interface, never `getTextElement()` — `UIE_SelectableText` (what
  `getScrollSetText` creates) puts its words in `body` and leaves the `title` that `getTextElement()`
  returns empty, while `PopupBoxText` is the mirror (empty `getTitle()`, label in `getBody()`); the
  handler takes `getTitle()` and falls back to `getBody()`. `PopupXScroll` (god select, `World
  .bStartGameOptions`) stays on the generic path: it only exists at the main menu, and every decision
  tool needs a live `ctx.Map`.
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
  leaving it open and flagged for `resolve_decision` — never silently answered. And it should rarely
  get the chance to stop there: since v0.4.3 `end_turn` denies force to `bEndTurn` whenever a
  non-informational popup is already open (`DecisionRegistry.HardChoiceBlockerOpen`), because
  `bEndTurn(forceThrough=true)` bypasses its own `ui.blocker` guard (`World.cs:642`) and would tick
  the turn with the popup still open, unanswered — which is exactly how force used to blow past a
  live event or level-up. The rule: force only passes popups marked informational (a pure "Dismiss"
  notice). `bEndTurn`'s force path still **auto-spends** each commandable agent's skill point when no
  popup is open (`spendSkillPoint()` AI-picks a trait; `World.cs:689-697`), and
  `PopupLevelupHandler.IsInformational` still flips a *stale* level-up popup (point already spent, or
  no traits left) informational so `AutoDismissInformational` can clear it rather than leaving it
  banner-flagged across every subsequent forced end-turn. Note the Unity gotcha the handlers guard against: after `removeBlocker`
  nulls `ui.blocker` and `DestroyImmediate`s the popup, `ui.blocker != blocker` reads **false**
  (Unity's `==` treats a destroyed object as equal to null), so success is checked as
  `blocker == null || ui.blocker != blocker`.
- **The `end_turn` digest** (why auto-dismiss is safe to leave on): a bare count of dismissed popups
  is an information blackout — a real session lost a game because a razing and an army battle that
  killed a key unit were reported only as "N popups dismissed", and a `count>1` batch used to keep
  just the **last** turn's `autoDismissed` object, silently dropping every earlier turn. So
  `end_turn` now returns a `digest` accumulated over the whole call, from three sources:
  - `digest.dismissed` — `AutoDismissInformational`'s `items`: `{turn, kind, popupType?, title?}` per
    popup, titled via one `IDecisionHandler.Describe` call taken **before** `Resolve` destroys the
    blocker (`DecisionRegistry.DescribeForLog`). `PopupMsgUnified` is deliberately **excluded**:
    `Map.addUnifiedMessage` appends to `turnUnifiedMessages` *before* calling `popMsgUnified`
    (`Map.cs:3898`/`:3923`), so every such popup is already in `digest.events` — listing it in both
    would duplicate. It still counts toward `autoDismissed.count`.
  - `digest.events` — `Summaries.NotableTurnEvents`, a filtered view of the same
    `map.turnUnifiedMessages` that `RecentEventLog.SnapshotTurn` archives, so anything here also
    appears in `get_recent_events`. Kept: messages touching one of your commandable units or a
    location one stands on (tagged `mine:true`), plus a fixed high-severity type whitelist (deaths,
    battles/armies, razing, war, the seal/prophecy clock, exposure, being hunted). Dropped as noise:
    `AGENT_IDLE` (already the `idleAgents` decision), `TUTORIAL`, `UNIT_ARRIVES`, `TASK_CANCELLED`.
    Read only when the turn actually advanced, so a non-advancing call cannot re-report it.
  - `digest.lost` — `Summaries.ComputeOwnedRoster` / `EvaluateUnitLoss`, a before/after diff of every
    `isCommandable()` unit. This deliberately covers **`UM` military units**, which
    `ComputeAgentSafety` (UA-only) never sees, so a dying army is finally visible. A loss also stops
    a batch (`stopReason:"unitLost"`), checked *before* the threat scan so a death is never masked by
    a warning about an agent that is merely in danger.

  Each stream is capped (20 entries) with a `truncated` count rather than a silent cut;
  `get_recent_events` always holds the unabridged log. `autoDismissed` keeps its original shape
  (`count`/`dismissed` kinds/`remaining`/`cappedOut`) but in a batch its `count` is now the total
  over every turn advanced, not the final turn's.
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
  `nWorshippingRulers`, `reserves`, `influenceElder`/`influenceHuman`, `influenceElderReq`/
  `influenceHumanReq` (both scale with `tenet_dogmatic.status`), `computeInfluenceDark(null)` (per-turn
  gain), `worshipsThePlayer`, `prophet` (`UA`), `divinity` (`DivineEntity`), `tenet_alignment`, and
  `tenets` (`List<HolyTenet>` → `getName()`/`getDesc()`/`status`/`getMaxNegativeInfluence()`/
  `getMaxPositiveInfluence()`/`structuralTenet()`). `updateData()` recomputes the derived counts.
  `DivineEntity`: `getName()`, `getMoodDesc()`, `strength`, `anger`, `exiled`, `presences`
  (`List<Pr_EntityPresence>` → `corrupted`).
- **Holy-order screen parity** (`influence_holy_order_tenet`, `oppose_divinity`) — the mod reproduces
  `PopupHolyOrder` / `UIE_HolyTenet`, which are otherwise click-only:
  - *Tenet change* (`UIE_HolyTenet.bInfluencePositively` / `bInfluenceNegatively`) is the whole commit:
    `tenet.status±1`, then `order.influenceElder = 0`. The mod then calls `updateData()` (as
    `PopupHolyOrder.setTo` does) and refreshes the UI. `toward_human` = `status++`, `toward_elder` =
    `status--`.
  - *Screen gate* (`UIE_HolyTenet.setTo`): `order.influenceElder >= order.influenceElderReq`.
    `HolyOrder.debugInfluence` is deliberately ignored.
  - *Per-direction eligibility*, mirrored verbatim in `Summaries.TenetEligibility`:
    `toward_human` iff `status < getMaxPositiveInfluence()`; `toward_elder` iff
    `status > getMaxNegativeInfluence()` **and not**
    `(!(t is H_Alignment) && !t.structuralTenet() && t.status <= 0 && t.status <= order.tenet_alignment.status)`.
    That last clause is the strategic gate the UI expresses only by hiding a button: an ordinary tenet
    cannot be darkened until `Alignment Status` (range −3..+3, starts at +3) has been driven below it.
  - *Influence economy*: `HolyOrder.turnTick` adds `computeInfluenceDark(null)` per turn and **clamps at
    the requirement**, so influence banked past it is discarded; `receiveFunding` adds half an agent's
    funding. `influenceHuman` is spent by the game itself (`humanAIExpenditure`) and is read-only here.
  - *The tenet list is dynamic*: `opt_holyOrderSubsetting` drops half the non-structural tenets at
    worldgen, gods add their own (`H_SectOfTheSerpent`/`H_Indulgences`/`H_MaddeningInsight`),
    `HolyOrder_Witches` adds three more, and `Ch_HungersPromise` appends `H_TheFeast` mid-game — so
    tenets are always resolved against `order.tenets` as it stands, never a fixed table.
  - *Divinity* (`PopupHolyOrder.bUndermine` / `bExile`): undermine needs `overmind.power >= 1`, then
    `power -= 1`, `strength -= 10` (floored at 0), `anger += param.holy_entityAngerGain`, and on the
    first use anywhere sets `hasStartedWarInHeaven`, adds `0.1` to `panicTemporaryChange` and pops
    `anw.warInHeaven`. Exile sets `exiled` and drives every `UAA` of that order to `sanity = 0`,
    `shadow = 1`, then pops `anw.exiledDivinity`. `bExile` itself only re-checks `strength == 0`, but the
    button is shown only when *all* presences are corrupted too — the mod enforces the stricter, visible
    condition.
- **God win-condition sheet** — `God`: `getMaxTurns()`, `getMaxPower()`, `getSealLevels()`,
  `getAgentCaps()`, `powerLevelReqs`, `getDetailedMechanics()`, `getSealDesc()`, `powerIncreaseText()`,
  and `getVictoryMessage(mode)`. `Map.opt_endless` gates `maxTurns` and `turnsRemaining` (both null when endless —
  `getMaxTurns()` still returns a number the game ignores; `Overmind.computeVictoryProgress` only ends the game by
  time-out when `!opt_endless`) and is surfaced as the `endless` boolean. `Overmind.victoryMode` defaults
  to `0` (the C# field default — `-1` is only a transient inside `victory()` before a mode 0–5 is chosen)
  and `defeat()` never touches it, so it and the mode-keyed victory message are surfaced **only** once
  `victoryAchieved` — gating on `endOfGameAchieved` would show a mode-0 SHADOW victory blurb on a defeat.
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

## Agent "storing" does NOT exist as a game action (UAE_StoredAgent.cs, verified v2.0)

- `UAE_StoredAgent` is only the SUMMON-BACK side: `createAgent()` (UAE_StoredAgent.cs:17-27) returns a
  stored agent to the map with `inner_menace /= 2`, `inner_profile /= 2` and both floors
  (`inner_menaceMin`/`inner_profileMin`) zeroed. Stored agents would sit in
  `map.overmind.agentsUnique`/`agentsGeneric` (the recruit pool) and be summoned via the generic
  `UIE_AgentSelect.bCast` code==0 path.
- **No store trigger exists anywhere**: no `bStore` UI handler in Assembly-CSharp.dll (strings-heap scan;
  the only `bStore` hits are substrings of `PrefabStore` etc.), and no caller of `new UAE_StoredAgent`
  in the full decompiled `Assets.Code`. It is dormant/cut content.
- Consequence: the mod's `agent_exposed` tip (TipCatalog.cs) must NOT recommend "store the agent" — that
  was a false promise (fixed in 0.4.5; menace/profile floors are permanent, exposure management is
  preventive only). Decision on 0.4.5: do NOT synthesize a `store_agent` tool from first principles.

## Challenge heat, market stalls, silent cancellations (verified 0.5.1)

- **`Challenge.getMenace()`/`getProfile()` are NOT heat gains.** They are the engine AI's
  utility-scoring inputs (the `_aiMenace`/`_aiProfile` params; `UA.getChallengeUtility` uses
  `getMenace()` as base utility, `UA.cs:1123` uses `getProfile()/10` as a travel gate). They can be
  negative (Ch_FuelTheFire: `locationUnrest - 75` ≈ -73) or 6-25× the applied heat
  (Ch_DangerousKnowledge: ai 50/50 vs applied 8/2). The heat actually applied on completion is
  `getCompletionMenaceAfterDifficulty()` (= `getCompletionMenace() * difficultyMult_growWithDifficulty`,
  1.0 at base difficulty) and `getCompletionProfile()` — see `Task_PerformChallenge.cs:71-72,199-200,262`;
  the in-game UI shows these (UISideChallengeDetails.cs:41,183). The MCP `menaceGain`/`profileGain`
  fields serialize the completion pair since 0.5.1. `Challenge.isIndefinite()` marks per-turn
  challenges (Ch_LayLow: completion values 0; `turnTick` does `addProfile(-x)`/`addMenace(-x)`).
- **`Sub_Market` builds exactly 3 `Ch_BuyItem`** (Sub_Market.cs:23-26), all named "Buy Item From
  Market" (`getName()` is constant; the sold item lives in the public `Ch_BuyItem.onSale` field,
  `Item.getName()`/`getShortDesc()`). A name-derived challenge id therefore collides — the mod salts
  `ChallengeId` with the onSale item name for `Ch_BuyItem` (0.5.1) and emits `itemForSale` in summaries.
- **`Task_GoToPerformChallenge.turnTick` nulls the task with NO UnifiedMessage** in two paths
  (challenge no longer at location, `moveTowards` failed) — the only truly silent cancellation.
  `Task_PerformChallenge.turnTick` (lines 36-57) emits a `TASK_CANCELLED` UnifiedMessage on mid-cast
  invalidation but ONLY when `unit.isCommandable()`; public field `challenge` on both tasks. The mod
  synthesizes digest events for the travel case (`Summaries.EvaluateTaskLoss`, 0.5.1) and no longer
  filters `TASK_CANCELLED` out of the end_turn digest.
- **`PopupItemTrading.bTakeAll()` silently skips items when side A has no free slot** (inner loop
  rotates A full-circle looking for a null slot; item stays on B) — gold still transfers via
  `bSwapGoldA1()`. No return value/message; detection is diffing `getItems()`/`getGold()` around the
  click (0.5.1 handler does this).
- **Event outcomes are silent unless described**: `EventManager.chooseOutcome` (weighted pick, runs
  effects, no text) + `PopupEvent.dismiss()` (PopupEvent.cs:497-506) pops a `PopupMsg`
  (`prefabStore.popMsg(desc, force:true)`, public `PopupMsg.text`) ONLY when `outcome.description` is
  non-empty. Which branch fired is otherwise unreadable without patching. `PopupMsgUnified` is a
  separate class (NOT a PopupMsg subclass), so consuming PopupMsg blockers doesn't touch unified
  messages.
- **`PopupChallengeComplete`** re-evaluates `bRepeat.gameObject.SetActive(...)` EVERY frame
  (`Update()`: challenge valid+unclaimed+validFor, unit `task==null`, alive) → generic button
  enumeration yields 2 or 3 options with shifting indices. Public members: `unit`, `ch`, `textBody`,
  `textFlavour`, `bRepeat`, `dismiss()`, `dismissGoto()` (does NOT close when unit is dead),
  `dismissRepeat()` (silently degrades to plain dismiss when ineligible). Bespoke handler since 0.5.1.
- **Latent base-game bug (not fixable mod-side)**: `Rt_DarkEmpire.complete()` guards its whole effect
  block on `shadow == 1.0` EXACTLY while `validFor` accepts `> 0.99` — a cast finishing at 0.991-0.999
  "completes" with zero effect.

## Agent-battle flee, game identity, Iastur endgame, treadmill signals (verified 0.8.0)

- **Flee legality (PopupBattleAgent.cs:191-217)**: the retreat buttons activate only when
  `battle.round > 1 && battle.state == 0` (left side also checks `outcome == OUTCOME_UNRESOLVED`);
  the round-1 label is "Unable to flee until end of round 1" and the round-2 label is "If you flee
  you will lose all your minions" (safe from round 3+). The mod's `fleeAsap` combat option loops
  `bStep()` until exactly that condition holds, then clicks `bRetreatLeft/Right()` by side
  (`fledLostMinions` at round 2, `retreated` at 3+, `fleeAsapEndedFirst` if the battle
  closes/decides first).
- **`Map.seed` is the stable identity of a game** (`public long seed`, Map.cs:149): set once at
  worldgen, serialized, and never touched again — a save/load recreates the `Map` object but keeps
  the seed. Since 0.8.0 `ModCore.OnMapSeen` clears one-shot state (ShownTips, boilerplate counts,
  seen event titles) only when the seed CHANGES; entity-id epochs still bump on every map swap.
- **`Person.isInsane()` = carries `T_Insane`** (Person.cs:617). Insanity does not strip stats,
  minions, or the quest AI — an insane hero can keep hunting agents (the `insane_heroes_hunt` tip's
  trigger scans living non-commandable UAs for it).
- **Iastur endgame (God_LaughingKing.cs:143, Pr_Iastur.cs, Ch_WavesOfMadness.cs, Params.cs:1638-1640)**:
  awakening lays "Iastur's Soul" bare at the Elder Tomb as a `Pr_Iastur` property (exists nowhere
  before that — its presence IS the endgame signal, used by the `iastur_soul` tip). The game's own
  text: modifier at 0% → Iastur dies (loss), 300% → win. `Ch_WavesOfMadness` charges it and applies
  `ch_strengthenIasturMenace`/`ch_strengthenIasturProfile` = **40/40** to the performer.
- **Treadmill / Alliance signals**: `SHADOW_DRIVEN_BACK` UnifiedMessages come from
  `Ch_DriveBackShadow.cs:134,138` and `Ch_Consacrate.cs:91`; the mod's `RecentEventLog.CountSince`
  (0.8.0) counts them per turn-window for the `shadow_treadmill` tip (3+ in 20 turns).
  `Society.isAlliance` is a plain public bool (Society.cs:38); Alliance razing of enshadowed
  settlements raises `ALLIANCE_OUTPOST` (Society.cs:653).

## Ritual placement, unique archetypes, channelled heat, mid-challenge events (verified 0.9.0)

- **A ritual's stored location is dead data**: item rituals are constructed against
  `map.locations[0]` (`I_LaughingTome.cs:17-18`) and the game never reads it — `UA.
  playerTriesToStartChallenge` (UA.cs:870) starts rituals with no location check, the UI admits
  them regardless of tile (`UIScroll_Unit.cs:427`: `c2 is Ritual || c2.location == ua.location`),
  and even `Task_GoToPerformChallenge.turnTick` short-circuits `challenge is Ritual` to
  perform-in-place (Task_GoToPerformChallenge.cs:45). `Rti_DropTome.complete` uses `u.location`
  (Rti_DropTome.cs:93-95). The mod's perform_challenge skips the travel branch for `c is Ritual`
  since 0.9.0.
- **Unique archetypes are consumed**: recruit codes are compile-time constants
  (`UAE_Abstraction.cs:8-44`; -4..-1 generic/repeatable, 1..15 unique), but `createAgent` runs
  `map.overmind.agentsUnique.Remove(this)` for every unique (e.g. Seeker at
  `UAE_Abstraction.cs:1195-1199`) — so a previously-valid positive code legitimately stops
  resolving. The lists are built once in `Overmind.addDefaultElements` (Overmind.cs:220-258);
  Buccaneer/Shaman exist only with orcs enabled.
- **Channelled heat lands at cast START**: `Task_PerformChallenge.cs:66-74` applies
  `getCompletionMenaceAfterDifficulty()`/`getCompletionProfile()` on the first tick when
  `isChannelled()`, and the completion block at `:166-202` explicitly skips it for channelled
  challenges. `Ch_WavesOfMadness.isChannelled()` is true (Ch_WavesOfMadness.cs:151-154).
- **`Settlement.isInfiltrated` is the orc-takeover flag** (Settlement.cs:24): set only by orc /
  claim paths (`Ch_Orcs_Expand.cs:143`, `Rt_Orcs_ClaimTerritory.cs:152`, …), never by human-city
  infiltration (`Ch_Infiltrate.cs:157-162` sets the SUB flag). `Settlement.infiltration`
  (Settlement.cs:56-83) is the computed fraction of infiltratable subs infiltrated and forces 1.0
  when the flag is set — so flag false + fraction 1.0 is a normal fully-infiltrated human city.
  The mod emits derived `fullyInfiltrated` since 0.9.0.
- **Mid-challenge events** ("Watched", "Life Continues", "Merchant of Antiquities", …) are JSON
  `MIDCHALLENGE` events under `data/coreData/` selected by `Task_PerformChallenge.cs:239-336`:
  gated on `map.opt_evMidCh` (Map.cs:207, from the `eventsMidChallenge` game option), skipped for
  channelled / Lay Low / Rest / ExploreRuins, a 50% coin flip per tick plus
  `param.ch_midchallengePeriod`, then weighted roulette over matching events. No auto-resolve
  exists game-side; the mod's `RoutineEvents` whitelist (0.9.0) answers three curated titles under
  the `passRoutineEvents` opt-in.
- **A settlement can gain districts silently mid-game**: `Ch_H_BuildTemple.complete`
  (Ch_H_BuildTemple.cs:95-134) adds a `Sub_Temple` with no UnifiedMessage (auto-infiltrated if the
  settlement is already >= 50% infiltrated or the builder is yours); the City Palace (`Sub_City`)
  infiltrate is gated in `Ch_Infiltrate.valid()` (Ch_Infiltrate.cs:112-129) on every OTHER
  infiltratable sub being done — so a new district re-locks it.
- **The "now likes X …" confirmation string is game text**: `Sel2_ForIdleHands.cs:51` /
  `Sel2_DevilMakesWork.cs:51`, verbatim, including the broken grammar.
