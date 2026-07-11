# Shadows of Forbidden Gods — game data model

A reference to the game's internal C# data model, written for mod authors. Everything below
was verified against the decompiled `Assembly-CSharp.dll` of game version **2.0** (see
*Regenerating this reference* at the end). Names are given exactly as they appear in code.

The game's code lives in the `Assets.Code` namespace (modding hooks in
`Assets.Code.Modding`). It follows a few conventions worth internalizing early:

- **State lives in public fields**, not properties. The save system serializes public
  members of the whole object graph, so "public field" ≈ "persistent game state".
- **Behavior lives in virtual methods** with lowercase names (`getName()`, `turnTick()`,
  `valid()`), overridden by dozens of small subclasses. Prefix families tell you what
  something is: `Ch_` challenge, `Rt_` ritual, `Pr_` location property, `P_` god power,
  `T_` trait, `I_` item, `Set_` settlement, `Sub_` subsettlement, `H_` holy tenet,
  `Mg_` god-specific (magic) challenge, `UA*/UM*` units, `SG_` special social groups,
  `AN_` national actions, `Act_` local ruler actions, `MF_` megafauna, `God_` playable gods.
- **Cross-references are often int indices**, not object pointers: units store `locIndex`
  and `personID`, persons store `rulerOf` (a location index), societies store `capital`
  (a location index). Object-typed properties (`unit.location`, `unit.person`) resolve
  these indices for you.

## 1. The two roots: `World` and `Map`

### `World` (one per process)

The Unity-side application object: UI, options, save/load, mod kernels.

| Member | Type | Meaning |
|---|---|---|
| `World.self` | static `World` | THE world instance |
| `World.staticMap` | static `Map` | the current game's map, null in menu |
| `World.log(string)` | static | write to the game's log (gated by `World.logging`) |
| `versionNumber` / `subversionNumber` | static int | game version, e.g. 2 / 0 → "2.0" |
| `ui` | `UIMaster` | UI root; `ui.blocker` non-null while a modal dialog is open |
| `turnLock` | bool | true while a turn is processing |
| `selector` | `Selector` | active click-target selector (e.g. power targeting) |
| `loadedModKernels` | `List<ModKernel>` | all mod kernels, loaded at the main menu |
| `bEndTurn(bool forceThrough)` | method | the end-turn button; runs `map.turnTick()` synchronously |

### `Map` (one per game)

The entire game state. Serialized wholesale into save files.

| Member | Type | Meaning |
|---|---|---|
| `turn` | int | current turn number |
| `locations` | `List<Location>` | every location on the map |
| `units` | `List<Unit>` | every unit (agents and armies) |
| `persons` | `List<Person>` | every person, alive or dead |
| `socialGroups` | `List<SocialGroup>` | every faction |
| `wars` | `List<War>` | active wars |
| `overmind` | `Overmind` | the player: god, power resource, agents |
| `soc_dark` | `Society` | the player's shadow faction |
| `soc_neutral` | `SG_AgentWanderers` | the neutral wanderers group |
| `grid` | `Hex[][][]` | hex grid, `grid[z][x][y]`; z=0 surface, z=1 underground |
| `world` | `World` | back-reference (nulled briefly during save) |
| `param` | `Params` | hundreds of tuning constants (`ch_*`, `mapGen_*`, …) |
| `worldPanic` | double | global panic level |
| `awarenessOfUnderground` | double | how aware humanity is of the underground |
| `data_victoryProgess` | double | victory progress (sic — note the typo) |
| `mods` | `List<ModKernel>` | mod kernels — **this is serialized into saves!** |
| `opt_*` | bool/int/double | game options chosen at setup |
| `megafauna`, `species_*`, `options`, `stats`, `*Manager` | | subsystems (trade, awareness, population, narrative…) |

Key methods:

- `turnTick()` — processes one full turn: bumps `turn`, fires `onTurnEnd` mod hooks, ticks
  every hex, unit, person, battle, war and social group.
- `getPathTo(Location a, Location b, Unit u = null, bool safeMove = false): Location[]` —
  path including the start location, or null. `safeMove` avoids dangerous territory.
  There is also a `(Location, SocialGroup, …)` overload for "path to nearest holding".
- `adjacentMoveTo(Unit u, Location next)` — the primitive one-step move.

### The turn loop

The end-turn button (`World.bEndTurn`) is synchronous on the Unity main thread:

```
bEndTurn(force):
  guards: turnLock, open dialog (ui.blocker), active selector,
          agent engaged in combat, unspent skill points, idle-agent alert
          (force=true auto-resolves battles, auto-spends skill points, skips alerts)
  turnLock = true
  map.turnTick():
      turn++
      mods.onTurnEnd(map)
      every hex .turnTickInitial() then .turnTick()
      processSpecial / processUnits / processPeople / processBattles / processWars
      every social group .turnTick()          (includes AI)
      … overmind/god tick, victory checks …
      mods.onTurnStart(map)                    (start of the *next* player turn)
  turnLock = false
  EventManager.turnTick(map); autosave every autosavePeriod turns
```

## 2. Geometry: `Hex`, `Location`, `Link`

The world is a hex grid with two layers (`z`: 0 = surface, 1 = underground), but gameplay
happens on a sparse graph of **Locations** connected by **Links** — the white lines in-game.
Moving along one link costs one move.

### `Hex`

`x`, `y`, `z`, `terrain` (enum), `locationIndex` (-1 if no location), `territoryOf`
(social group index), `isMountain`, purity/habitability data.

### `Location`

| Member | Type | Meaning |
|---|---|---|
| `index` | int | **stable unique id** |
| `hex` | `Hex` | position |
| `name`, `shortName` | string | `getName()` composes the display name |
| `soc` | `SocialGroup` | owner (null = unclaimed) |
| `settlement` | `Settlement` | what's built here (null = empty) |
| `properties` | `List<Property>` | modifiers on the location (`Pr_*`) |
| `units` | `List<Unit>` | units standing here |
| `links` | `List<Link>` | connections; `getNeighbours()` gives the other ends |
| `isOcean`, `isCoastal`, `isMajor` | bool | terrain/importance flags |
| `province` | `Province` | province grouping |
| `culture` | `Culture` | culture of the area |

Challenge plumbing: `GetChallenges()` returns the location's current challenge list;
`populateStandardChallenges()` rebuilds it from properties + settlement + subsettlements +
units + mod hook `populatingChallenges(location, list)`.

### `Property` (`Pr_*`, 83 subclasses)

Location modifiers: infiltration, wards, devastation, shadow, cults… Fields: `charge`
(strength, decays for many), `influences`. API: `getName()`, `getDesc()`, `turnTick()`,
optional `getChallenges()`. Examples: `Pr_Devastation`, `Pr_Ward`, `Pr_DeepOneCult`,
`Pr_Opulence`.

## 3. Settlements

`Settlement` (abstract): `name`, `shadow` (0–1 enshadowment), `defences`, `isHuman`,
`isInfiltrated`, `subs: List<Subsettlement>`, `getChallenges()`, `turnTick()`.

- `SettlementHuman` adds population/prosperity mechanics and `ruler` (a `Person`, via
  `rulerIndex`). Human types: `Set_City`, `Set_MinorHuman`, `Set_DwarvenCity`,
  `Set_ElvenCity`, `Set_DwarvenOutpost`…
- Non-human: `Set_OrcCamp`, `Set_CityRuins`, `Set_TombOfGods`, `Set_DeepOneAbyssalCity`,
  `Set_DeepOneSanctum`, `Set_VinervaManifestation`…
- `Subsettlement` (`Sub_*`, ~30): districts inside a settlement — `Sub_Docks`,
  `Sub_Catacombs`, `Sub_Temple`… each can contribute challenges.

## 4. Factions: `SocialGroup` and `Society`

### `SocialGroup` (abstract)

| Member | Type | Meaning |
|---|---|---|
| `index` | int | **stable unique id** |
| `name` / `getName()` | string | display name |
| `relations` | `Dictionary<SocialGroup, DipRel>` | diplomacy; `getRel(other)` lazily creates |
| `isAtWar()` | bool | any relation in state war |
| `currentMilitary` / `maxMilitary` / `militaryRegen` | double | army capacity |
| `menace` | double | how threatening this group looks |
| `turnTick()` | | per-turn AI + upkeep |

`DipRel`: `status` (-1…1 attitude), `state` (`dipState`: none/war/…), `war: War`.

### `Society : SocialGroup` — human(ish) nations

`posture` (introverted/defensive/offensive), `capital` (location index, `getCapital()`),
`isRebellion`, `isDarkEmpire`, `isAlliance`, `isOphanimControlled`, national actions
(`AN_*` — declare war, quarantine, raise armies…) with `actionUnderway`/`actionProgress`.
The sovereign is the ruler of the capital's human settlement.

Special groups: `map.soc_dark` (the player, a `Society`), `SG_AgentWanderers` (neutral
agents), `SG_ActionTakingMonster` subclasses (`SG_DeepOnes`, `SG_Orcs_*`…), `HolyOrder`
(+`HolyOrder_*`) — religions with tenets (`H_*` HolyTenet subclasses) that mods can
influence via `adjustHolyInfluenceGood/Dark`.

## 5. People: `Person`, `Trait`, `Item`, `House`

### `Person`

| Member | Type | Meaning |
|---|---|---|
| `index` | int | **stable unique id** (units reference it as `personID`) |
| `getName()` / `getFullName()` | string | display names |
| `society` | `Society` | allegiance |
| `house` | `House` | noble house |
| `unit` | `Unit` | embodiment on the map, or null |
| `rulerOf` | int | location index of their seat, -1 if none |
| `state` | `personState` | normal / enthralled / dead… |
| `isDead`, `age`, `gold`, `prestige` | | vitals |
| `shadow` | double | 0–1 corruption by the player |
| `awareness` | double | 0–1 awareness of the shadow |
| `sanity` / `maxSanity` | double/int | madness track |
| `stat_might`, `stat_lore`, `stat_intrigue`, `stat_command` | int | the four stats |
| `level`, `XP`, `skillPoints` | int | progression |
| `traits` | `List<Trait>` | `T_*` subclasses (94), `getName()`/effects via hooks |
| `items` | `Item[3]` | `I_*` subclasses (37), can carry rituals |

The player's own "person-level" mechanics — enthrallment, corruption — run through
`shadow`, `state`, and `map.overmind.enthralled`.

## 6. Units: agents and armies

```
Unit (abstract)
├── UA (agent; person-backed, fights in agent battles)
│   ├── UAG   heroes fighting for humanity (UAG_Warrior, UAG_Mage, …)
│   ├── UAA   acolytes
│   └── UAE   "evil"/recruitable agents (UAE_Warlock, UAE_Baroness, ~25 types)
│       └── UAEN  neutral monsters (UAEN_Vampire, UAEN_DeepOne, UAEN_OrcUpstart, …)
└── UM (military/monster unit; strength = hp)
    UM_HumanArmy, UM_OrcArmy, UM_RavenousDead, UM_DeepOnes, UM_Shoggoth, …
```

### `Unit` (abstract)

| Member | Type | Meaning |
|---|---|---|
| `location` | `Location` (property over `locIndex`) | where it stands |
| `person` | `Person` (property over `personID`) | who it is (null for many UM) |
| `society` | `SocialGroup` | owner faction |
| `task` | `Task` | current order (null = idle) |
| `hp` / `maxHp` | int | health; for armies this is the army strength |
| `movesTaken` / `getMaxMoves()` | int | movement budget per turn |
| `isCommandable()` | bool | **true = the player can command it** |
| `isDead` | bool | dead units awaiting cleanup |
| `engagedBy` / `engaging` / `turnLastEngaged` | | agent combat engagement |
| `menace` / `profile` | double (property) | how alarming / how visible |
| `rituals` | `List<Challenge>` | rituals this unit can perform |
| `turnTick(Map)` | | per-turn behavior |

### The `Task` system (25 types)

A unit's `task` executes in its `turnTick`. The important ones:

- `Task_GoToLocation(Location)` — pathfinds and walks; clears itself on arrival.
- `Task_GoToPerformChallenge(Challenge)` — travel, then start the challenge.
- `Task_PerformChallenge(Challenge)` — accumulate `progress` each turn by
  `challenge.getProgressPerTurn(unit, …)` until complete → `challenge.complete(unit)`.
- `Task_Disrupted` — the unit was disrupted; blocks orders this turn.
- `Task_AttackUnit` / `Task_DisruptUA` / `Task_Bodyguard` / `Task_EscortUA` — agent vs agent.
- Military: `Task_CaptureLocation`, `Task_RazeLocation`, `Task_RaidLocation`, `Task_Recruit`.
- Misc: `Task_PassTurn`, `Task_Wander`, `Task_InHiding`, `Task_InBattle`.

**Ordering a unit around (exactly what the game UI does):**

```csharp
// guards: unit.isCommandable(), not engaged this turn, task is not Task_Disrupted
unit.task = new Task_GoToLocation(destination);
if (unit.movesTaken < unit.getMaxMoves())
    unit.task.turnTick(unit);        // start moving immediately with the moves left
```

## 7. The player: `Overmind`, `God`, `Power`

### `Overmind`

| Member | Type | Meaning |
|---|---|---|
| `god` | `God` | which god you play |
| `power` | double | your mana; powers deduct their `getCost()` from it |
| `agents` | `List<Unit>` | your agents |
| `enthralled` | `Person` | currently-enthralling target |
| `availableEnthrallments` / `nEnthralled` | int | recruitment capacity |
| `sealsBroken` / `sealProgress` | int | the seals gating your power level |
| `victoryMode` | int | `VICTORY_MODE_SHADOW/INSANITY/DARK_EMPIRE/RUIN/…` |
| `victoryAchieved`, `endOfGameAchieved` | bool | end states |
| `panicFrom*` | double | panic bookkeeping |

### `God` (12 playable + utility)

`God_Eternity`, `God_LaughingKing`, `God_Vinerva`, `God_Mammon`, `God_Cards`, `God_Snake`,
`God_Ophanim`, `God_Underground`, plus tutorial/scenario/mapgen gods. API: `getName()`,
`getPowers(): List<Power>`, `powerLevelReqs`, `getMaxTurns()`, victory/UI virtuals.
Mods add gods by appending to the `List<God>` in the `onStartGamePresssed` hook.

### `Power` (`P_*`, 91 subclasses)

`getName()`, `getDesc()`, `getCost()`, `validTarget(Unit)`, `validTarget(Location)`,
`cast(Unit)`, `cast(Location)`, `isPassiveOnly()`, `getRestrictionText()`.
`castCommon` deducts the cost: `map.overmind.power -= getCost()`.
The UI casts with exactly: affordability check → `validTarget(x)` → `cast(x)`.

## 8. Challenges and rituals

`Challenge` (194 `Ch_*` + 59 `Rt_*` + 26 `Mg_*`): anything an agent can *do* at a location
— explore ruins, embezzle funds, infiltrate a court, perform dark rituals.

| Member | Meaning |
|---|---|
| `location` | property over `locationIndex` — where it is performed |
| `claimedBy` | unit currently performing/heading to it |
| `getName()`, `getDesc()`, `getRestriction()` | display |
| `valid()` | is it available at all right now |
| `validFor(UA)` / `validFor(UM)` | can *this* unit do it |
| `getProgressPerTurn(unit, msgs)` | speed; total needed is `getComplexity()` |
| `getMenace()`, `getProfile()` | per-turn menace/profile while performing |
| `getCompletionMenace()`, `getCompletionProfile()` | one-off on completion |
| `getDanger()` | damage risk per turn |
| `complete(UA/UM)` | effect on completion |
| `allowMultipleUsers()`, `isIndefinite()`, `isChannelled()` | behavior flags |

`Ritual : Challenge` — carried by a unit (`unit.rituals`) rather than offered by a location.

**Starting a challenge as the player** (from `UA.playerTriesToStartChallenge`, internal):
after the guards (`valid()`, `validFor`, not disrupted, not claimed by someone else),
the commit is:

```csharp
// release anything this unit had previously claimed, then:
unit.task = new Task_PerformChallenge(challenge);
challenge.claimedBy = unit;
foreach (ModKernel m in map.mods) m.onPlayerStartsChallenge(unit, challenge);
challenge.onImmediateBegin(unit);
map.world.ui.checkData();
```

## 9. Menace, profile, awareness, panic (the detection economy)

- Each unit accumulates `menace` (how dangerous it seems) and `profile` (how visible it
  is); challenges add per-turn and on-completion amounts.
- People have `awareness` (they know something is wrong) and `shadow` (they belong to you).
- `map.worldPanic` aggregates humanity's alarm; `ManagerAwareness` runs discovery;
  `map.awarenessOfUnderground` gates the underground layer's exposure.
- Mods can inject reasons via `populatingWorldPanicReasons` and threats via
  `populatingThreats`.

## 10. Save system — what mods must know

- Saving is `fsSerializer.TrySerialize(typeof(Map), map)` (FullSerializer → compressed
  JSON). **Everything reachable from `Map` through public members is saved** — including
  `map.mods`, i.e. your `ModKernel` instance itself.
- On load the whole graph (your kernel included) is **re-created by deserialization**,
  then `afterLoading(map)` runs on the new instance.
- Consequences:
  1. Public instance fields on your kernel/classes = persisted in saves (by design — use
     it for mod state you *want* saved).
  2. Runtime-only machinery (threads, sockets, caches) must live in **static** fields.
  3. Never make a game object reference a type the game can't deserialize without your
     mod — that save would break if the mod is disabled.
- Save files: `%USERPROFILE%\AppData\Roaming\ShadowsForbiddenGodsSaves` (i.e.
  `Environment.SpecialFolder.ApplicationData` + `ShadowsForbiddenGodsSaves`).

## 11. Regenerating this reference

```bash
# from the repo root, with the game's Managed folder in lib/Managed/
./tools/decompile.sh          # ilspycmd → decompiled/, one .cs per type
grep -n "public " decompiled/Assets/Code/Map.cs | grep -v "("   # fields of any class
ls decompiled/Assets/Code/Ch_*.cs                               # enumerate a family
```

The `inspect` tool of the ShadowsMCP mod is the live counterpart: point it at any path
(e.g. `map.locations[4].settlement`) and it reflects over the running game.
