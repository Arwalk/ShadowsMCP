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
- [ ] `game_overview` shows the right turn number and your god's name, plus `availableEnthrallments`,
      `agentCap`, `canRecruit`, `endOfGameAchieved` and `defeated`
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

## 6e. Agent-vs-agent actions (`command_agent`)

Move one of your agents onto a tile that holds a hostile hero (settlements are the easy place to find one).

```bash
mcp get_unit '{"unitId":"U1"}'                  # 'orders' now lists attack/rob (+ dangerEstimates) with exact calls
mcp command_agent '{"unitId":"U1","order":"attack","targetUnitId":"U9"}'
```
- [ ] In-game: the agent-duel window opens, exactly as clicking the hero's "Attack" box would
- [ ] The tool result carries the battle **inline** as `pendingDecision.popupType == "PopupBattleAgent"`,
      and names the target's cancelled task in `cancelledTargetTask`
- [ ] `mcp get_unit '{"unitId":"U9"}'` → the target's `task` is now `null`. Then `resolve_decision` to
      **flee/retreat** (from round 2) and confirm the task is **still** null — attacking breaks a ritual
      permanently, win or lose. This is the counter to the Chosen One's ritual
- [ ] If your agent had >4 turns of challenge progress, the first call refuses with a `force=true` hint
      (attacking cancels *your* challenge too); `'{"force":true}'` goes through
- [ ] While that battle is open, `mcp end_turn '{"force":true}'` does not advance (`blockedBy:"combat"`)
- [ ] Errors are clean: an off-tile target names the `move_unit` call to reach it; your own agent as the
      target points at `order:"trade"`; a guarded hero (`Task_Bodyguard`) names the guard to beat first
- [ ] With two of your agents on one tile: `command_agent '{"order":"trade",...}'` opens the item window
      in-game (`popupType:"PopupItemTrading"`), and items really move between them
- [ ] `order:"rob"` (a lower-level merchant/adventurer on the tile) opens the steal window and reports
      `profileGained`/`menaceGained`; `order:"follow"` on a Harvester + merchant sets a follow task
- [ ] **None of the four orders returns a `tool failed:`/`… failed: <ExceptionType>` message** — every one
      of them dereferences the resolved target, and a target the resolver forgot to hand back crashed all
      four identically (v0.4.0 regression). Any such message names its own frame: paste it into the report
- [ ] `get_threats` → the agent's `agentSafety` entry lists `hostilesOnTile` with an `attackHint`

## 6c. Recruitment (enthralling new agents)

```bash
mcp get_player_state                             # note availableEnthrallments, agentCap, canRecruit
mcp list_recruitable_agents
```
- [ ] `list_recruitable_agents.capacity` shows `availableEnthrallments`, `nEnthralled`, `agentCap`,
      `canRecruit`; `archetypes` lists agents (e.g. a Hierophant — "Can be placed anywhere") with codes,
      stats, restrictions, and a `placement` object (`eligible` + up to 4 `exampleTargets`)
- [ ] Recruit an archetype onto a valid location (Hierophant works almost anywhere):
      `mcp recruit_agent '{"agentCode":-1,"locationId":"L12"}'` → a new agent appears in-game and in
      `list_units` (scope mine); `availableEnthrallments` dropped by 1; result may flag `levelUpPending`
- [ ] The new agent's level-up (if `map.automatic` is off) surfaces as a pending decision — resolve it
      via `resolve_decision` or `end_turn '{"resolveOptionIndex":0}'`
- [ ] Error paths return clean messages, not crashes: with 0 points ("no recruitment points"), at the
      agent cap ("agent cap reached (n/cap)"), a bad target (returns the archetype's restriction text
      plus suggested valid targets, or a "no location satisfies" note),
      both/neither of agentCode+heroUnitId, an archetype code with no locationId
- [ ] **Hero corruption:** with a hero at ≥98% shadow or insane listed under
      `list_recruitable_agents.corruptibleHeroes`, `mcp recruit_agent '{"heroUnitId":"U9"}'` corrupts it
      in place — it becomes commandable, `availableEnthrallments` dropped

## 6d. Game over (end_turn stops)

- [ ] While the game is ongoing, `game_overview.endOfGameAchieved` is `false` and `end_turn` advances
- [ ] Reach an end state (win, or lose via heroes reforging the seals / the prophecy; `Cheat` can force
      it): `game_overview.endOfGameAchieved` is `true`, `defeated`/`victoryAchieved` reflect the outcome
- [ ] `mcp end_turn` now returns `{gameOver:true, outcome:"victory"|"defeat", victoryMode, turn}` and
      does **not** advance `map.turn`
- [ ] Losing your **last agent** does NOT set `endOfGameAchieved` — `end_turn` keeps advancing and you
      can `recruit_agent` again once points regenerate

## 6b. Decision windows (events, level-ups)

- [ ] With nothing open, `mcp get_pending_decision` returns `{"pending": false}` and
      `game_overview.pendingDecision` is `null`
- [ ] Play until an agent has a skill point (a level-up popup opens on `end_turn`, or is pending):
      `game_overview.pendingDecision.kind == "levelUp"`, and **every** tool result now starts with a
      `⚠ A decision is pending …` banner
- [ ] `mcp get_pending_decision` lists the available traits with indices
- [ ] `mcp resolve_decision '{"optionIndex":0}'` → popup closes in-game; the trait shows in
      `get_person` for that agent and its `skillPoints` dropped
- [ ] Trigger a narrative event (e.g. explore ruins). `get_pending_decision` lists the choices with
      `enabled` flags; `resolve_decision` on an enabled option closes it and returns the outcome; a
      disabled option returns a clear "condition isn't met" error
- [ ] After resolving, `mcp end_turn` (no force) advances instead of "a dialog is open"

**Resolving decisions without the decision tools (via end_turn / game_overview):**

- [ ] With an event blocking, `mcp game_overview` shows the full `pendingDecision` inline — its
      `options` (index + label + enabled) and a `resolveHint` — not just a "call get_pending_decision" hint
- [ ] `mcp end_turn` (no args, event blocking) does **not** advance; it returns
      `{advanced:false, blockedBy:"decision", pendingDecision:{options…, resolveHint}}`
- [ ] `mcp end_turn '{"resolveOptionIndex":0}'` answers the event with option 0 and then advances (or
      returns the next `pendingDecision` if the outcome raised a follow-up popup); the result carries
      `resolved:{ok:true,…}`
- [ ] `mcp end_turn '{"resolveOptionIndex":0}'` also works for a level-up (picks that trait) and for the
      idle-agent alert (index 0 passes all idle agents), each then advancing the turn
- [ ] `mcp resolve_decision '{"force":true}'` on an event takes the first available choice and returns
      `forcedDefault:true` (last-resort escape)

**Any other popup (generic button coverage):**

- [ ] An informational popup (e.g. `PopupMsg`, an intro/tutorial box) shows up as
      `pendingDecision.kind == "popup"`; `get_pending_decision` lists its button(s) (e.g. "Continue"/
      "OK") and the body text; `resolve_decision '{"optionIndex":0}'` dismisses it
- [ ] With an info popup queued **ahead** of a trait pick, dismissing it surfaces the trait popup
      next (banner flags it) — the early-game trait pick is no longer blocked
- [ ] A `PopupConfirmOrder` lists confirm/abort as options; picking one resolves it in-game
- [ ] Any popup can be closed with `resolve_decision '{"force":true}'` (equivalent to pressing OK)
- [ ] Clicking a power/agent option that opens a targeting selector returns `openedSelector: true`
      with a hint to use the relevant action tool

**Agent-death notice (informational popup raised during turn processing):**

- [ ] Get an agent killed by something OTHER than a battle (e.g. a high-danger challenge like
      "Infiltrate Holy Site"). On the `end_turn` that kills it, the turn **still advances**; afterwards
      `game_overview.pendingDecision.kind == "death"` (`PopupMsgAgentsDeath`) and every tool result carries
      a `⚠ A decision is pending (death: … has died) …` banner. (A death in **battle** instead raises a
      `kind:"event"` "Defeat" `PopupEvent` — see the narrative-event row below, not this one.)
- [ ] `mcp get_pending_decision` shows `kind:"death"`, the message text, and two options
      ("Dismiss" / "Focus the fallen agent's location, then dismiss")
- [ ] `mcp resolve_decision '{"optionIndex":0}'` closes it and returns `resolved:true`; the banner
      clears and `mcp end_turn` (no force) advances. With several agents dying the same turn, each
      `resolve_decision` clears one and the banner re-flags the next until all are gone
- [ ] **Headless auto-dismiss:** trigger a death, then `mcp end_turn '{"force":true}'` → the result
      shows the turn advanced **and** `autoDismissed:{count:…,dismissed:["death", …]}`; the banner is
      already clear afterward. Repeating `end_turn '{"force":true}'` across many turns never stalls on
      a death/message popup
- [ ] **Nothing dismissed is lost:** that same result also carries `digest.dismissed` with a
      `{turn, kind:"death", title:…}` entry NAMING the dead agent — not just a count. No entry has
      `popupType:"PopupMsgUnified"` (those are reported once, under `digest.events`)
- [ ] **The digest spans a whole batch:** `mcp end_turn '{"count":5,"force":true,"passIdleAgents":true}'`
      → `digest.dismissed`/`digest.events` entries carry `turn` values from more than just the final
      turn, and `autoDismissed.count` is the batch total. (Regression: the batch used to report only the
      last turn's dismissals and drop the rest.)
- [ ] **The turn's news is in the response:** `digest.events` lists notable happenings (razing,
      battles, deaths, wars, seal/prophecy progress), entries about your own units tagged `mine:true`,
      and each one also appears in `mcp get_recent_events`
- [ ] **Losing a unit stops the batch:** send an outmatched army/agent to its death during
      `end_turn '{"count":10,"force":true,"passIdleAgents":true}'` → the batch stops with
      `stopReason:"unitLost"`, `advancedBy < 10`, and `digest.lost` naming the unit and its
      `lastLocation`. Works for `UM` army units too, not only agents
- [ ] **Real choices are preserved:** when a narrative event (`kind:"event"`) is the pending popup,
      `end_turn '{"force":true}'` does **not** advance (`advanced:false`, `blockedBy:"decision"`) and does
      **not** auto-dismiss it — the result/`pendingDecision` still flags it for `get_pending_decision` /
      `resolve_decision`. (Regression: force used to tick the turn with the event still open.) The same
      holds for ANY open choice popup — force may only pass purely-informational "Dismiss" notices.
- [ ] **Open level-up blocks force; skill points still auto-spend when no popup is open:** open a
      level-up (a prior non-force `end_turn` pops it), then `end_turn '{"force":true}'` — it does **not**
      advance (`blockedBy:"decision"`, `kind:"levelUp"`); pick a trait (or `resolve_decision
      '{"force":true}'` to skip and keep the point), then the turn ends. With an unspent point and NO
      popup open, `end_turn '{"force":true}'` still auto-spends it (a trait is AI-picked) and advances.

**Idle-agent alert (non-modal — no popup, but blocks end turn):**

- [ ] With an agent that has no order and hasn't moved: `game_overview.pendingDecision.kind ==
      "idleAgents"` and every tool result carries a `⚠ … agents are idle …` banner (even though no
      popup is visible in-game)
- [ ] `mcp get_pending_decision` lists the idle agents (ids + names) and the "pass all" option
- [ ] Order one idle agent (`move_unit`) → it drops off the idle list and the banner count falls
- [ ] `mcp resolve_decision '{"optionIndex":0}'` → remaining idle agents show "Passing Turn" in
      `get_unit`; the banner clears and `mcp end_turn` (no force) advances
- [ ] **force no longer skips idle (mirrors combat):** with an idle agent, `mcp end_turn '{"force":true}'`
      does **not** advance the turn — it returns `blockedBy:"decision"` with `kind:"idleAgents"` (an idle
      agent's turn is never silently wasted). A `count>1` `force` batch stops on the first idle turn
      (`advancedBy:0`, `stopReason:"decision"`)
- [ ] `mcp resolve_decision '{"force":true}'` on the idle alert no longer passes it — it returns a guidance
      error asking for `optionIndex 0` (force still dismisses ordinary popups; idle is not one)
- [ ] **Explicit fast-forward:** `mcp end_turn '{"count":3,"passIdleAgents":true}'` advances several turns
      with idle agents present (they show "Passing Turn"), never stopping on the re-raised idle alert;
      combat/events still stop it
- [ ] Turn the in-game idle-agent alert **off** → no idle pending decision is reported, and
      `mcp end_turn '{"force":true}'` advances normally

## 7. Save-game safety

- [ ] Save the game, load the save → no errors in Player.log, game state intact
- [ ] After the load, old entity ids are rejected with "stale id" style errors; re-query works
- [ ] **Autosave fires under `end_turn(force)`:** note the newest `Autosave_*.sv` in the game's save
      folder (`%APPDATA%\ShadowsForbiddenGodsSaves\`, i.e. `…\AppData\Roaming\…`), then advance with
      `mcp end_turn '{"count":10,"force":true,"passIdleAgents":true}'` (repeat; `count` maxes at 10) until the
      turn passes a multiple of 15 → a fresh `Autosave_*.sv` is written (mtime advances; the `Autosave_1.._5`
      rotation moves on). Regression: before the fix the popup was destroyed before its save ran, so forced
      batches wrote no autosave
- [ ] Non-force `end_turn` onto a multiple of 15 surfaces the popup as
      `pendingDecision.popupType == "PopupAutosaveDialog"` ("Saving game…"/"Game Saved…", kind `"popup"`);
      the file is still written and `resolve_decision '{"force":true}'` dismisses it

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
