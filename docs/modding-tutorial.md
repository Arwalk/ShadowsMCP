# Modding Shadows of Forbidden Gods — a practical tutorial

This tutorial takes you from zero to a working code mod, and ends with a walkthrough of a
real one: **ShadowsMCP**, the mod in this repository, which embeds an HTTP server inside
the game. Everything here was verified against game version 2.0's decompiled code; the
per-class reference lives in [game-data-model.md](game-data-model.md).

## 0. How modding works in this game

Shadows of Forbidden Gods loads two kinds of mod content:

- **Data/narrative content**: JSON events, images, cultures, scenarios.
- **Code**: a compiled .NET DLL. The game scans each mod folder for `*.dll`, loads it with
  `Assembly.LoadFrom`, finds every class extending `Assets.Code.Modding.ModKernel`,
  instantiates it, and calls your overridden hook methods at the right moments.

A minimal installed code mod is just a folder:

```
<game>\data\optionalData\MyMod\
├── MyMod.dll          ← your compiled code
├── mod_desc.json      ← metadata (required)
└── mod_config.json    ← user-facing options (optional)
```

`mod_desc.json` (parsed with Unity's JsonUtility — all five fields expected):

```json
{
  "displayedName": "My Mod",
  "prefix": "mymod",
  "description": "What it does.",
  "versionsSupported": ["2.0"],
  "modCredit": "you"
}
```

`versionsSupported` is compared **verbatim** against the game's `versionNumber.subversionNumber`
("2.0" today). A mismatch shows an "incompatible" warning but the mod still loads. You can
also ship per-version content in a `v2.0/` subfolder — if a folder named after the current
version exists, the game loads DLLs/content from there instead.

## 1. Toolchain setup

You need the .NET SDK (any recent version — the *project* targets the old framework, the
SDK just drives the build) on Windows, macOS or Linux. No Visual Studio required.

Create a class library targeting **.NET Framework 4.7.2** (what the game's Unity/Mono
runtime supports) and reference the game's assemblies:

```xml
<!-- MyMod.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- lets non-Windows machines build net472 -->
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net472"
                      Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(GameDir)\ShadowsOfForbiddenGods_Data\Managed\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>   <!-- never copy game DLLs into your mod -->
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(GameDir)\ShadowsOfForbiddenGods_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`dotnet build -c Release` produces `MyMod.dll`. Copy it plus `mod_desc.json` into
`<game>\data\optionalData\MyMod\` and enable the mod in the game's mod menu.

Language note: you can use C# 9 syntax (the compiler lowers it), but not features that
need newer runtime types — no `record`, no `init` setters, no default interface methods.

## 2. Lesson 1 — the smallest possible mod

```csharp
using Assets.Code;
using Assets.Code.Modding;

namespace MyMod
{
    public class MyKernel : ModKernel
    {
        public override void onModsInitiallyLoaded()
        {
            World.log("MyMod: hello from the main menu");
        }

        public override void onTurnStart(Map map)
        {
            World.log("MyMod: turn " + map.turn + " begins");
        }
    }
}
```

Two things to know about the loader (both verified in the decompiled loader code):

1. **`onModsInitiallyLoaded` can fire more than once** (once when your DLL loads, again
   when all mods finish loading). Guard one-time initialization with a static flag.
2. **Exceptions escaping your hooks during load mark the whole mod "failed to load"** —
   wrap risky code in try/catch.

`World.log` output (and `UnityEngine.Debug.Log`) lands in
`%USERPROFILE%\AppData\LocalLow\...\Player.log` — your best friend while modding.

### The hook catalog

`ModKernel` has ~50 virtual hooks. The load-bearing ones:

| Hook | When |
|---|---|
| `onModsInitiallyLoaded()` | main menu, after mods load |
| `onStartGamePresssed(Map, List<God>)` | new game clicked; add custom gods to the list (note the triple-s typo — it's real) |
| `beforeMapGen(Map)` / `afterMapGenBeforeHistorical` / `afterMapGenAfterHistorical` | world generation stages |
| `afterLoading(Map)` | a save was loaded |
| `onTurnStart(Map)` / `onTurnEnd(Map)` | bracketing each turn's processing |
| `populatingChallenges(Location, List<Challenge>)` | add challenges to a location |
| `populatingNationalActions` / `populatingLocalActions` | add AI/ruler actions |
| `onPlayerStartsChallenge(UA, Challenge)` / `onChallengeComplete(...)` / `interceptChallengeCompletion(...)` | challenge lifecycle |
| `onPersonDeath_StartOfProcess` / `interceptDeath` / `onPersonDeath_EndOfProcess` | death pipeline (interceptors can veto) |
| `onAgentBattleVictory` / `onAgentAttackAboutToBePerformed` / … | agent combat |
| `onWarDeclared` / `onPeaceDeclared` | diplomacy |
| `unitAgentAI(...)`, `rulerAI(...)`, `sovereignAI(...)` | bias AI utility scoring |
| `onCheatEntered(string)` | text entered in the cheat console — great for debug commands |
| `receiveModConfigOpts_int/_bool(string, value)` | user changed your mod's config |
| `mapMask_*` | draw custom map overlay modes |

Since the Aug 2025 game update mods can also add whole new **map layers** (grid z-levels):
`Assets.Code.Modding.ModMapGenTools.addLayer(map)` appends an empty layer and returns its z
index, and `ModMapGenTools.populateLayer(map, layerZ, connectToGround)` scatters connected
locations across it (it ends by re-running `map.checkConnectivity()` and
`map.recomputeStepDistMap()`). Call them from a mapgen hook such as
`afterMapGenBeforeHistorical`. The cheat console command `addLayer` demos the pair.

## 3. Lesson 2 — reading game state

Everything hangs off the `Map` you receive in hooks (see
[game-data-model.md](game-data-model.md) for the full reference):

```csharp
public override void onTurnStart(Map map)
{
    foreach (Unit u in map.units)
    {
        if (!u.isCommandable() || u.isDead) continue;   // just my agents
        World.log(u.getName() + " at " + u.location.getName()
            + " task=" + (u.task != null ? u.task.getShort() : "idle"));
    }

    foreach (SocialGroup sg in map.socialGroups)
    {
        if (sg.isAtWar()) World.log(sg.getName() + " is at war");
    }
}
```

Conventions that save you time:

- Iterate copies (`.ToList()`) if your loop can kill/spawn units or change lists.
- Names come from `getName()` methods, not fields, and can compose location context.
- Indices are ids: `Location.index`, `Person.index`, `SocialGroup.index` are stable;
  units have no id (track them by reference).

## 4. Lesson 3 — acting on the world

The golden rule: **do what the game's own UI code does, guards included.** The decompiled
`UIInputs.cs` / `UA.cs` / `World.cs` show the exact sequences; skipping their guard checks
is how mods corrupt games. Three canonical actions:

**Move a unit** (from `UIInputs.rightClickOnHex`):

```csharp
if (u.isCommandable()
    && !(u.engagedBy != null && u.turnLastEngaged == map.turn)   // not under attack
    && !(u.task is Task_Disrupted))
{
    u.task = new Task_GoToLocation(destination);
    if (u.movesTaken < u.getMaxMoves())
        u.task.turnTick(u);                  // consume remaining moves right now
}
```

**Start a challenge** (from `UA.playerTriesToStartChallenge` — it's `internal`, so
replicate it): check `c.valid()`, `c.validFor(ua)`, disruption and `claimedBy`; then

```csharp
ua.task = new Task_PerformChallenge(c);
c.claimedBy = ua;
foreach (ModKernel m in map.mods) m.onPlayerStartsChallenge(ua, c);
c.onImmediateBegin(ua);
map.world.ui.checkData();                    // refresh the UI
```

**Cast a god power** (from `Sel_CastPower`): check `map.overmind.power >= p.getCost()`
and `p.validTarget(x)`, then `p.cast(x)` — the base `castCommon` deducts the cost.

### User-facing configuration

Ship a `mod_config.json` next to your DLL:

```json
{
  "name": "My Mod",
  "options": [
    { "name": "Aggression", "description": "How mean the AI is",
      "defaultValue": 5, "minValue": 0, "maxValue": 10, "isInteger": "true" },
    { "name": "Verbose logging", "description": "Spam Player.log",
      "defaultBoolValue": "false" }
  ]
}
```

The game renders a config UI for it and calls your
`receiveModConfigOpts_int("Aggression", v)` / `receiveModConfigOpts_bool(...)` when the
player applies changes (and again on game start). If the player never opens the config,
the callbacks never fire — make your compiled-in defaults match the JSON.

## 5. Lesson 4 — persistence, or: why `public` matters

Saves serialize the **whole `Map` object graph** with FullSerializer — including
`map.mods`, i.e. your kernel instance. This gives you persistence for free and one sharp
edge:

- Mod state you want saved → public instance fields (on your kernel, or on objects you
  attach to the game graph, like custom Properties/Challenges).
- Runtime machinery (threads, sockets, UI handles, caches) → **static fields only**. On
  load, your kernel is *re-instantiated by the deserializer* and `afterLoading(map)` runs
  on the new instance; statics are the only state that survives the swap.
- A save made with your mod enabled references your types — players who disable the mod
  can't load that save. Warn them.

## 6. Case study — ShadowsMCP (this repo)

ShadowsMCP embeds an MCP (Model Context Protocol) server in the game so AI assistants can
query the game and command agents over HTTP. It exercises nearly every lesson above, plus
the two hard problems of "live service inside a game": threading and lifetime.

**Layering.** `src/Core/` is a self-contained MCP/JSON-RPC/HTTP stack with zero game
references — it compiles both into the mod and into a Linux console host
(`src/TestHost/`) so the protocol can be smoke-tested without the game
(`tools/smoke-test.sh`, 22 checks). `src/Mod/` contains everything that touches
`Assets.Code`: one file for serialization (`Summaries.cs`), one for actions
(`Tools/ActionTools.cs`), so every game-API touchpoint is auditable against
`docs/ground-truth-notes.md`.

**Threading.** HTTP requests arrive on background threads, but game state is only safe on
Unity's main thread. The bridge is a classic dispatcher:

```
HTTP thread:  enqueue job ──▶ block on event (with timeout)
main thread:  McpBridgeBehaviour.Update() ──▶ dispatcher.Pump() ──▶ run job ──▶ set event
```

The `MonoBehaviour` lives on a `DontDestroyOnLoad` GameObject created in
`onModsInitiallyLoaded`, so it pumps in menus and across scene loads. Nothing ever blocks
the main thread; a stuck game yields a timeout error to the HTTP client instead of a
deadlock.

**Lifetime.** All server state is static (`ModCore` has zero instance fields — see
Lesson 4 for why). Map tracking is defensive: every hook that receives a `Map` calls the
same `OnMapSeen`, and when the reference changes (new game, load), session entity ids are
reset. One subtlety: loading a save *recreates* the `Map` object for the same logical
game, so per-game one-shot state (shown-once tips and boilerplate) is keyed to `map.seed`
— set once at worldgen and serialized — rather than to the `Map` instance. A reload keeps
that state; a genuinely new game (different seed) clears it.

**Fidelity.** Action tools replicate the UI's guard+commit sequences verbatim — the same
engagement/disruption checks before moving, the same claim bookkeeping before challenges,
the same `bEndTurn` (with its `forceThrough` semantics) for ending turns — returning API
errors where the game would pop dialogs.

## 7. Building, installing, debugging

```bash
./build.sh                 # this repo: smoke test + build + assemble dist/ShadowsMCP/
# copy dist/ShadowsMCP/ → <game>\data\optionalData\ShadowsMCP\
```

Debug loop:

1. `Player.log` (`%USERPROFILE%\AppData\LocalLow\` → game folder) — Debug.Log/World.log
   output, mod-loader lines ("Found an assembly", "Assembly is subtype …"), exceptions.
2. The in-game cheat console + your `onCheatEntered` override = interactive poking.
3. Decompile the game (`tools/decompile.sh`, needs `ilspycmd`) and read what the UI does
   before writing any state-mutating code.

## 8. Publishing to the Steam Workshop

The game has a built-in uploader. It uploads a mod from the game install's
**`modUploadFolder/`** (next to the game exe) — *not* from `data/optionalData/`.
Assemble this layout there:

```
<game>\modUploadFolder\MyMod\
├── mod.json               ← Workshop listing: {"title", "description", "tags":[...]}
├── preview.png            ← square thumbnail (optional — item has no thumbnail without it)
└── content\               ← the payload that gets uploaded
    ├── MyMod.dll
    ├── mod_desc.json
    └── mod_config.json
```

`mod.json` (the Workshop page) is a different file from `content\mod_desc.json` (the
in-game mod descriptor) — both are required. Then, in-game: **Workshop menu → User Mods →
publish**. The first publish creates the item; the game records its `PublishedFileId`
locally, so every later publish **updates the same item**. Subscribers get the mod
auto-loaded; local folders in `data/optionalData/` always work for development.

For this repo, `./build.sh` assembles the exact layout the uploader expects at
`dist/upload/ShadowsMCP/` (from `mod/mod.json` and `mod/preview.png`) — copy that folder
into the game's `modUploadFolder/` and publish.

## 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Mod not listed in-game | folder not under `data/optionalData/`, or `mod_desc.json` missing/invalid (all five fields are checked) |
| "Mod incompatible with this version" | `versionsSupported` doesn't contain the exact version string (main menu shows it); still loads |
| "Mod failed to load" popup | exception during `Assembly.LoadFrom`/instantiation or inside `onModsInitiallyLoaded` — check Player.log |
| Works until reload, then state gone | you kept state in private/static fields expecting it saved — see Lesson 4 |
| Save won't load without the mod | by design: the save references your types |
| Random crashes after mutating state | skipped the UI's guard sequence — reread Lesson 3 |
