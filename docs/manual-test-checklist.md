# ShadowsMCP — manual test checklist

The mod can't be exercised outside the game, so this script is the acceptance test.
Run it on the Windows PC that has the game (steps 1–3) and any LAN machine (steps 4+).
If anything fails, collect the two logs listed at the bottom and the exact command + output.

## 1. Install

- [ ] Copy `dist/ShadowsMCP/` → `<game>\data\optionalData\ShadowsMCP\`
      (default: `C:\Program Files (x86)\Steam\steamapps\common\Shadows of Forbidden Gods\data\optionalData\ShadowsMCP\`)
- [ ] The folder contains `ShadowsMCP.dll`, `mod_desc.json` and `mod_config.json`
- [ ] Launch the game → mod menu → **Shadows MCP Server** appears and is enabled
- [ ] If the mod is greyed out / missing: the game version may not match `versionsSupported`
      in `mod_desc.json` — edit it to your game version (shown in the main menu) and restart

## 2. Server comes up

- [ ] Start a **new game** (any god, smallest map is fine)
- [ ] Open `Player.log` and find a line like `[ShadowsMCP] listening on http://*:8017/mcp`
      - `%USERPROFILE%\AppData\LocalLow\` → look for the game studio's folder → `Shadows of Forbidden Gods\Player.log`
- [ ] Windows Firewall prompt appeared → allowed (or add an inbound rule for the game, TCP 8017)

## 3. Local smoke (on the game PC, PowerShell)

```powershell
curl.exe -X POST http://localhost:8017/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"ping"}'
```
- [ ] Returns `{"jsonrpc":"2.0","id":1,"result":{}}`

## 4. LAN reachability (from the Mac)

Find the PC's LAN IP (`ipconfig` → IPv4 Address), then:

```bash
PC=<game-pc-ip>
curl -X POST http://$PC:8017/mcp -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
```
- [ ] Response contains `"serverInfo":{"name":"shadows-mcp"` and `"tools":{}`
- If connection refused: firewall rule, or the port moved (check Player.log for the actual port)

## 5. Query tools

```bash
mcp() { curl -s -X POST http://$PC:8017/mcp -H 'Content-Type: application/json' \
  -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"$1\",\"arguments\":${2:-{\}}}}"; echo; }

mcp game_overview
mcp list_units
mcp list_locations '{"limit":5}'
mcp get_player_state
mcp inspect '{"path":"map.turn"}'
mcp inspect '{"path":"map.locations[0]","depth":2}'
```
- [ ] `game_overview` shows the right turn number and your god's name
- [ ] `list_units` shows your agents with `"commandable": true` and correct locations
- [ ] `inspect map.turn` matches the in-game turn counter
- [ ] With **no game loaded** (exit to main menu): `mcp game_overview` returns a clear
      `isError` message ("No game in progress"), not a hang or crash

## 6. Action tools (visual confirmation in-game after each)

```bash
mcp list_units                                  # pick one of your agents, e.g. U1, and note its location
mcp get_location '{"locationId":"L12"}'         # pick a neighbouring location id from list_locations
mcp move_unit '{"unitId":"U1","locationId":"L12"}'
```
- [ ] In-game: the agent now shows a movement order toward that location
- [ ] `mcp end_turn` → returns the new turn number; in-game the turn advanced and the agent moved
- [ ] `mcp list_challenges '{"unitId":"U1"}'` lists what the agent's location offers
- [ ] `mcp perform_challenge '{"unitId":"U1","challengeId":"C3"}'` → agent starts it (check in-game)
- [ ] `mcp list_powers` → `mcp use_power` with a castable power and a valid target → visible effect
- [ ] Invalid moves fail cleanly: moving an enemy unit, a bad location id, a stale id after reload

## 7. Save-game safety

- [ ] Save the game, load the save → no errors in Player.log, game state intact
- [ ] After the load, old entity ids are rejected with "stale id" style errors; re-query works

## 8. Real MCP client

```bash
claude mcp add --transport http shadows http://$PC:8017/mcp
claude "Using the shadows MCP server: what turn is it, where are my agents, and what's the closest location none of them occupy? Move one agent there."
```
- [ ] Tools are discovered, queries answer correctly, the move shows up in-game

## If something fails, collect:

1. `Player.log` (path in step 2)
2. `ShadowsMCP.log` — in the same folder as Player.log (the game's persistent data path)
3. The exact curl command + full response
