# ShadowsMCP — an MCP server inside Shadows of Forbidden Gods

A mod for [Shadows of Forbidden Gods](https://store.steampowered.com/app/1741640/Shadows_of_Forbidden_Gods/)
that embeds a [Model Context Protocol](https://modelcontextprotocol.io) server in the running game.
Any MCP client (Claude Code, Claude Desktop, custom scripts) can then **query the live game state**
— locations, units, persons, societies, your god — and **command your agents**: move them, perform
challenges, use powers, end the turn.

```
Claude Code (Mac/PC) ──HTTP──▶  ShadowsMCP.dll inside the game (Windows PC)
        tools/call                      │ marshalled to Unity's main thread
                                        ▼
                                   live Map object
```

## Quick start

1. **Install the mod**: copy `dist/ShadowsMCP/` into the game's local mod folder:
   `C:\Program Files (x86)\Steam\steamapps\common\Shadows of Forbidden Gods\data\optionalData\ShadowsMCP\`
2. **Enable it** in the game's mod menu and start (or load) a game.
3. **Windows Firewall**: allow the game when prompted (the server listens on the LAN by default).
4. **Connect a client** from any machine on your LAN:
   ```bash
   claude mcp add --transport http shadows http://<game-pc-ip>:8017/mcp
   ```
   Or verify with curl first:
   ```bash
   curl -X POST http://<game-pc-ip>:8017/mcp -H 'Content-Type: application/json' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"game_overview","arguments":{}}}'
   ```

See `docs/manual-test-checklist.md` for a full end-to-end test script.

## Tools

| Tool | What it does |
|---|---|
| `game_overview` | Turn & **time budget** (endless flag, maxTurns / turnsRemaining — null in an endless game), your god, victory mode & progress, **panic breakdown**, war & investigation counts, world counts, pending decision |
| `get_threats` | The game's Threats panel: heroes hunting your agents, the Chosen One's prophecy, incoming wars, holy-order mood (sorted by severity) |
| `list_locations` / `get_location` | The world map. Detail adds **settlement economy** (population, prosperity, food, ruler/heir, holy order, current action), property influences, and the **clues** heroes hold there |
| `list_units` / `get_unit` | Units (default scope: yours); kind, position, hp, task. Detail adds menace/profile (with their **sticky floors** and **hunt radius**), **agent internals** (minions, combat, corruption/fatigue) and the **investigation** building against it |
| `list_persons` / `get_person` | People: rulers, nobles. Detail is the full sheet — stats, XP, kills, traits/items (with descriptions), alerts, relationships, house curses |
| `list_social_groups` / `get_social_group` | Societies and factions, military, wars. Detail adds diplomacy, the **national action** underway, and a **holy-order** block for religions |
| `list_wars` | Every active war: attacker, defender, the attacker's objective, start & projected end |
| `list_investigations` | Detection dashboard: every clue pointing at your agents, with investigators, weight and location |
| `list_holy_orders` | Religions: enshadowment, prophet, temples, worshippers, divine entity, and each **tenet** with its status, range and which way it can currently be influenced. `orderId`/`verbose` add descriptions and example calls |
| `get_recent_events` | The game's turn message log — what changed recently (newest first) |
| `get_player_state` | Your god, power, agents, recruitment capacity, powers, and the **win-condition sheet** (seal thresholds, agent-cap curve, victory text) |
| `list_recruitable_agents` | Recruitment capacity, enthrallable archetypes, and corruptible heroes |
| `list_powers` | Your god's powers and whether each is castable now |
| `list_challenges` | Challenges available to one of your agents where it stands |
| `get_tips` | Curated, agent-facing explanations of game mechanics (infiltration, politics, magic, economy, gods, factions). No args = index; `id`/`category` for one tip or a topic. The same tips also surface in `game_overview`/`end_turn` under `tips`, and the core mechanics are in the server's `initialize` instructions |
| `inspect` | **Query ANY element** by path, e.g. `map.locations[4].settlement` (read-only reflection) |
| `move_unit` | Send one of your agents toward a location |
| `cancel_task` | Clear an agent's current order |
| `perform_challenge` | Have an agent start a challenge |
| `use_power` | Cast one of your god's powers |
| `recruit_agent` | Spend a recruitment point to enthrall a new agent (archetype onto a location, or corrupt an eligible hero in place) |
| `command_army` | A commandable military unit's special orders: raze the settlement it stands on, drive back an enemy hero, or attack an enemy army on its tile |
| `command_agent` | An agent's actions against another agent on its tile: **attack** a hostile hero (starting the duel cancels their ritual for good, even if you flee), **rob** a weaker one, **trade** items with one of your own, or **follow** a merchant. Available orders are listed under `orders` in `get_unit`/`list_units` |
| `influence_holy_order_tenet` | Spend a religion's banked Elder influence to shift one of its tenets (`toward_elder` / `toward_human`) — the holy-order screen's doctrine lever |
| `oppose_divinity` | Undermine (−1 power, −10 strength) or exile the divine entity behind a religion |
| `get_pending_decision` / `resolve_decision` | Read and answer a pending decision popup (level-up trait pick, event choice, agent-death notice) |
| `end_turn` | Advance the turn; if a decision popup blocks it, returns the options and accepts `resolveOptionIndex` to answer them (so decisions can be resolved without the two tools above). Always returns a **`digest`** of what happened across every turn advanced — popups force-dismissed (named, not just counted), the turn's notable news (razing, battles, deaths, wars), and **`lost`**: your own units that died, which also stops a batch with `stopReason:"unitLost"`. Once the game is over (`endOfGameAchieved`), returns `gameOver` with the outcome and does not advance |
| `new_game` | **Start a fresh game headlessly** — from the main menu or over a running one (`confirm:true` required then; the old game is abandoned unsaved). Pick the god (or `random`), seed, map size, difficulty and turn limit; slow (~30–120 s of map gen + burn-in), returns the seed used and a full `game_overview`-style summary |

### Entity ids

Locations use the game's own index (`L3`). Units/persons/social groups/challenges get
session-scoped ids (`U17`, `P42`, `SG5`, `C8`) assigned on first serialization.
**Ids are stable within one game session only** — after loading a save or starting a new
game, re-query instead of reusing old ids.

## Configuration

Defaults: listen on **all interfaces**, port **8017** (retries 8018…8026 if busy).
Options (port, LAN vs localhost-only) are exposed through the game's per-mod config.

> **Security note:** the server has no authentication — anyone on your network can read your
> game and move your agents. That's the intended scope (home LAN). Switch to localhost-only
> if the machine is on an untrusted network.

> **Runs in the background:** MCP requests are processed on the game's main thread, which Unity
> pauses when the window loses focus. So the mod forces `Application.runInBackground = true`
> (and downgrades exclusive fullscreen to borderless, which would otherwise minimize and pause
> on focus loss). This keeps MCP calls responsive while you work in another window, at the cost
> of the game continuing to run/render when unfocused.

## Capturing MCP traffic

To analyse what a real session costs (response sizes / token usage), route the client through the
logging proxy in `tools/` — MCP clients have no built-in way to capture request/response bodies over
HTTP transport. On the machine running the client:

```bash
node tools/mcp-tee.mjs 9017 http://<game-pc-ip>:8017/mcp game-log.jsonl   # forward to the game + tee to JSONL
claude mcp remove shadows && claude mcp add --transport http shadows http://localhost:9017/mcp
# …play a session, then:
tools/mcp-log-report.sh game-log.jsonl                                    # ranked per-tool byte/token cost (needs jq)
```

`mcp-tee.mjs` forwards every call unchanged and logs each exchange with `reqBytes`/`resBytes`;
`mcp-log-report.sh` ranks response bytes per tool. Both are self-documented in their headers.

## Building from source

Needs the .NET SDK (8+) on any OS, plus the game's assemblies:

1. Copy `<game>/ShadowsOfForbiddenGods_Data/Managed/` → `lib/Managed/` (gitignored, never redistribute).
2. `./build.sh` — runs the protocol smoke test, builds the mod (Debug + Release), and assembles
   both `dist/ShadowsMCP/` (local install) and `dist/upload/ShadowsMCP/` (Workshop upload).

Repo map: `src/Core/` = game-independent MCP/JSON/HTTP layer (also compiled into
`src/TestHost/`, a Linux console host used by `tools/smoke-test.sh`); `src/Mod/` = the
game-facing mod layer; `docs/` = game data-model reference, modding tutorial, test checklist.

## Publishing to the Steam Workshop

The game uploads a mod from its **`modUploadFolder/`** (next to the game exe) — *not* from
`data/optionalData/`. `build.sh` assembles the exact layout it expects at
`dist/upload/ShadowsMCP/`:

```
ShadowsMCP/
├── mod.json       Workshop listing (title, description, tags) — from mod/mod.json
├── preview.png    thumbnail — from mod/preview.png (optional)
└── content/       the payload that gets uploaded (mod_desc.json + mod_config.json + DLL)
```

`mod.json` (the Workshop page) is a different file from `content/mod_desc.json` (the in-game mod
descriptor) — both are required. To publish:

1. `./build.sh`
2. Copy `dist/upload/ShadowsMCP/` into the game's `modUploadFolder/`.
3. In-game: **Workshop menu → User Mods → publish**. The first publish creates the item; the game
   records its `PublishedFileId` locally, so every later publish **updates the same item**.

## Releasing

The release version lives in **one place**: `<Version>` in `src/Mod/ShadowsMCP.csproj`. It flows
automatically into the DLL, into `serverInfo.version` (MCP `initialize`), into the `modVersion`
field of the `game_overview` tool (so a connected client can confirm which build it's talking to),
and into the Workshop description (`build.sh` stamps `Build X.Y.Z`). Per release, while pre-1.0:

1. Bump `<Version>` in `src/Mod/ShadowsMCP.csproj` (semver: `0.MINOR.PATCH`).
2. `./build.sh` and sanity-check.
3. Commit, then tag: `git tag vX.Y.Z && git push --tags`.
4. Publish to the Workshop (above).

## Documentation

- [`docs/game-data-model.md`](docs/game-data-model.md) — the game's internal data model, class by class
- [`docs/modding-tutorial.md`](docs/modding-tutorial.md) — how to mod Shadows of Forbidden Gods, from zero to this mod
- [`docs/manual-test-checklist.md`](docs/manual-test-checklist.md) — end-to-end test script for the mod
