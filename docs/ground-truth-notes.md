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
map.turnTick(); turnLock = false; ui.checkData(); EventManager.turnTick(map);` autosave every
`autosavePeriod` turns. **Synchronous on the main thread** → end_turn tool = one dispatcher
job with a long timeout; compare `map.turn` before/after; on no-advance, report which guard hit.

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

## Entity id scheme (decided)

Native indices exist for **locations, persons, social groups** → ids `L<index>`, `P<index>`,
`SG<index>` resolved by scanning the map lists for a matching `index` field. Units and
challenges have no stable native id → session-scoped registry ids `U<n>`, `C<n>` (weak refs).

## Misc

- The game ships `Newtonsoft.Json.dll` in Managed (unused by us; JsonUtility is what the
  game itself uses for mod files).
- `ReasonMsg` has `msg`/`value` fields — used for utility breakdowns.
- Player's faction: `map.soc_dark`; commandable check is `unit.isCommandable()`.
