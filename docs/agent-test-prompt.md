# ShadowsMCP — agent-run test prompt

Paste everything in the **Prompt** block below into an agent (Claude Code / Desktop) that is connected to
the ShadowsMCP server. It is the automated counterpart to `docs/manual-test-checklist.md`: instead of a
human watching the game window, the agent drives the game **through MCP tools only** and reports pass/fail.

## Before you run it (human setup — the agent cannot do these)

1. Install & enable the mod, start the game, and load **a fresh or throwaway game** (any god, smallest map
   is fine). The tests move units, spend recruitment points, and end turns — all irreversible, with no save
   tool exposed — so do **not** run this on a game you care about.
2. Connect the agent to the server, e.g. `claude mcp add --transport http shadows http://<game-pc-ip>:8017/mcp`.
3. Paste the prompt. The connected server is referred to as **`shadows`**.

---

## Prompt

````
You are a QA agent testing the **ShadowsMCP** server for the game *Shadows of Forbidden Gods*. You drive a
live, throwaway game entirely through the MCP tools of the connected server (named `shadows`) and produce a
structured test report. You have NO view of the game window — your only evidence is the JSON that tools
return. Do not use any tools other than the `shadows` server's.

The game: you play a dark god corrupting the world through agents (your commandable units). You are
authorized to freely mutate THIS game (it is expendable). Actions are irreversible — there is no save/undo.

### Tools available (23)
game_overview, get_threats, list_locations, get_location, list_units, get_unit, list_persons,
get_person, list_social_groups, get_social_group, get_player_state, list_recruitable_agents,
list_powers, list_challenges, inspect, move_unit, cancel_task, perform_challenge, use_power,
recruit_agent, get_pending_decision, resolve_decision, end_turn.

### Preflight (if this fails, stop and report BLOCKED)
1. Call `game_overview`. If it errors ("no game in progress" / not ready) or the core tools are missing,
   stop and report a single BLOCKED row explaining what was unavailable. Otherwise record the starting
   `turn` and note it in the report header.

### Rules of engagement — READ CAREFULLY
- **Discover ids dynamically; never hardcode.** Unit ids (`U*`) come from `list_units`, locations (`L*`)
  from `list_locations`/`get_location`, challenges (`C*`) from `list_challenges`, archetype codes from
  `list_recruitable_agents`, social groups (`SG*`) from `list_social_groups`. Ids are session-scoped — if a
  tool says "stale id", re-query.
- **Verify by state-diff.** For every action, call the relevant query tool BEFORE, perform the action, then
  query AFTER, and assert the specific field changed as expected. Record the before value, the action, and
  the after value as evidence. Example: for `use_power`, snapshot `get_player_state.power`, cast, then
  assert `remainingPower == before - cost`.
- **Never mark PASS without the tool output that proves it.** No guessing, no screen-based reasoning.
- **One assertion per check.**
- **Opportunistic checks** (marked "opportunistic" below) depend on conditions you cannot reliably force —
  an agent dying, a narrative event firing, reaching game-over, having 0 recruitment points, a corruptible
  hero existing. If the condition never arises within ~15 `end_turn`s, mark the check **SKIP** with the
  reason. Do NOT mark it FAIL.
- **Don't get stuck.** If a tool reports a blocking decision or idle-agent alert when you didn't expect one,
  resolve it (see section G) and continue. Some checks below are specifically testing that block behavior.
- **Error-path checks expect a clean `isError` result, not a crash/hang.** A well-formed error message =
  PASS; a hang, stack trace, or malformed response = FAIL.
- Keep a running list of `{id, area, result, expected, observed, notes}` for the final report.

### Test checklist

**A. Sanity & cross-consistency**
- A1: `game_overview` returns `turn`, `god.name`, `counts`. (PASS if all present.)
- A2: `inspect {"path":"map.turn"}` equals `game_overview.turn`.
- A3: `list_units {"scope":"mine"}` count equals `game_overview.counts.commandableUnits`.
- A4: `get_player_state` returns a `god`, `agents` array, and `power`.
- A5: `inspect {"path":"map.locations[0]","depth":2}` returns a nested object (round-trip works).
- A6 (error): `get_unit {"unitId":"U9999"}` and `get_location {"locationId":"L9999"}` both return clean
  "unknown/stale id" errors.
- A7 (people): `list_persons` returns a list; `get_person` on the first person's id (`P*`) returns a detail
  object with stats/traits. Assert the id round-trips (the detail's id matches the one you asked for).
- A8 (societies): `list_social_groups` returns factions; `get_social_group` on your own faction (the one
  owning your commandable agents, e.g. from `list_units`.society) returns a detail object. Assert it
  round-trips.

**B. New state fields (recruitment + end-of-game)**
- B1: `game_overview` includes `agentCap`, `canRecruit`, `endOfGameAchieved`, `defeated`,
  `availableEnthrallments`.
- B2: `get_player_state` includes `agentCap`, `canRecruit`, `endOfGameAchieved`, `victoryMode`,
  `enthralledCount`.
- B3: consistency — `canRecruit` == (`availableEnthrallments` > 0 AND `enthralledCount` < `agentCap`), and
  `defeated` == (`endOfGameAchieved` AND NOT `victoryAchieved`). Compute from the fields and compare.

**C. Movement**
- C1: pick a commandable agent from `list_units {"scope":"mine"}`; `get_location` its location to get a
  neighbour id; `move_unit` there. Assert the result's `nowAt`/`arrived`/`task`, then `get_unit` shows a
  go-to task (or it already arrived). Evidence = task before (likely null) vs after.
- C2: `cancel_task` on that unit (or `move_unit` to its own location) clears the order — `get_unit.task`
  becomes null. (If the unit fully arrived and is idle, note that and still assert task is null.)
- C3 (error): `move_unit` with a bad `locationId` errors cleanly.
- C4 (error): find a non-commandable unit (`list_units {"scope":"all"}`, `commandable:false`) and try to
  `move_unit` it — errors "not under your command".

**D. Challenges**
- D1: `list_challenges {"unitId":"U..."}` for one of your agents returns a list (may be empty — if empty,
  move the agent to a settlement and retry once, else SKIP D2).
- D2: `perform_challenge` on a listed challenge; assert `get_unit.task` became a perform/travel task.
- D3 (opportunistic): if a challenge has >4 turns of progress, `move_unit`/`cancel_task` without `force`
  returns an abandon-warning error, and with `force:true` succeeds.

**E. Powers**
- E1: `list_powers` returns powers with `cost` and a castable flag.
- E2: pick a castable power with a valid target (a unit or location per its restriction); snapshot
  `get_player_state.power`; `use_power`; assert `remainingPower == before - cost`. If no power is castable
  right now, SKIP with reason.
- E3 (error): calling `use_power` on a passive power, or with insufficient power, or with both/neither
  target, errors cleanly.

**F. Recruitment**
- F1: `list_recruitable_agents` returns `capacity` (availableEnthrallments, nEnthralled, agentCap,
  canRecruit), `archetypes` (each with code, stats, restrictions), and `corruptibleHeroes`.
- F2: if `capacity.canRecruit` is true, pick an archetype with a permissive restriction (e.g. one whose
  restriction says "can be placed anywhere", typically the Hierophant, code -1) and a valid `locationId`;
  snapshot `availableEnthrallments` and `list_units {"scope":"mine"}` count; `recruit_agent
  {"agentCode":<code>,"locationId":"L..."}`; assert a new agent appears (mine count +1),
  `availableEnthrallments` −1, and the result may include `levelUpPending`. If `canRecruit` is false, SKIP
  F2 (and note why).
- F3 (error): `recruit_agent {}` (neither agentCode nor heroUnitId) errors "specify exactly one…".
- F4 (error): `recruit_agent {"agentCode":<code>,"heroUnitId":"U1"}` (both) errors.
- F5 (error): `recruit_agent {"agentCode":<code>}` (no locationId) errors asking for a location.
- F6 (error): recruit an archetype with a **restrictive** placement onto an invalid location (e.g. a
  location that clearly doesn't meet its restriction) — errors with the archetype's restriction text.
- F7 (opportunistic): if `corruptibleHeroes` is non-empty, `recruit_agent {"heroUnitId":"U..."}` corrupts it
  in place — that unit becomes commandable (`get_unit.commandable` true) and `availableEnthrallments` drops.
  Else SKIP.
- F8 (opportunistic): if `availableEnthrallments` reaches 0, a further `recruit_agent` errors "no
  recruitment points"; if `nEnthralled` reaches `agentCap`, it errors "agent cap reached". Test whichever
  you can reach; SKIP the rest.

**G. Decisions & blocking**
- G1: when nothing is pending, `get_pending_decision` returns `{pending:false}` and
  `game_overview.pendingDecision` is null.
- G2: call `end_turn` repeatedly (up to ~10 times) until either a decision appears
  (`game_overview.pendingDecision` non-null, and every tool result is prefixed with a `⚠` banner) or the
  budget is exhausted. If one appears, `get_pending_decision` lists its options with indices.
- G3: resolve a decision two ways across the run — once via `resolve_decision {"optionIndex":0}` (assert the
  banner clears / pending becomes false), and once via `end_turn {"resolveOptionIndex":0}` (assert it
  answers and then advances or surfaces the next decision). If only one decision type ever appears, do it
  the once and SKIP the other with a note.
- G4 (idle-agent alert): if `end_turn` reports `blockedBy:"decision"` with an idle-agents kind, resolve it
  with `resolve_decision {"optionIndex":0}` (pass all) OR by ordering an agent, then `end_turn` advances.
- G5 (opportunistic, agent death): if an agent dies (an `end_turn` result or `game_overview` shows a death
  decision), assert the turn still advanced and `end_turn {"force":true}` auto-dismisses it
  (`autoDismissed.count > 0`). Else SKIP.

**H. End turn & game-over**
- H1: snapshot `game_overview.turn`; `end_turn` (no force); assert the returned/`game_overview` turn
  increased by ≥1.
- H2: while `game_overview.endOfGameAchieved` is false, `end_turn` continues to advance (covered by H1/G2).
- H3 (invariant): if at any point your commandable-unit count hits 0, assert `endOfGameAchieved` is STILL
  false and `end_turn` STILL advances — losing all agents is NOT a loss. (If you never hit 0 agents, SKIP.)
- H4 (opportunistic): if the game ends (`endOfGameAchieved` true), assert `end_turn` returns
  `{gameOver:true, outcome:"victory"|"defeat", ...}` and does NOT advance `turn`. Else SKIP.

**I. Robustness / soak**
- I1: run `end_turn {"force":true}` for ~5–10 turns in a row; assert it never stalls (each call returns,
  advancing or clearly reporting a preserved real decision) and `turn` keeps climbing.
- I2 (error): a malformed call (e.g. `perform_challenge {"unitId":"U1"}` with no challengeId, or a stale id
  after several turns) returns a clean error, not a hang.

**J. Threats & enemy intent**
- J1: `get_threats` returns a `count` and a `threats` array. Each entry has `message` (string),
  `severity` (number), `beneficial` (bool), and `location` (a `{id,name}` ref or null). PASS if the
  shape holds for every entry (the array MAY be empty on a very early/quiet turn — that's still PASS;
  note it and lean on J3 later once the world is more active).
- J2 (consistency): assert `threats` is sorted by `severity` descending, and every non-null
  `location.id` (`L*`) round-trips via `get_location`.
- J3 (opportunistic, enemy intent): scan `list_units {"scope":"all"}` for a hostile unit whose `task`
  brief mentions hunting/attacking/disrupting; `get_unit` on it and assert `taskDetail.target` is a
  `U*` ref that round-trips via `get_unit`, plus a `turnsRemaining` or `turnsLeft` number is present.
  Cross-check with `list_units {"scope":"hostileToMe"}` — any unit it returns MUST expose such a
  `taskDetail.target`. NOTE: `hostileToMe` (and `get_threats`) intentionally cover units hostile to your
  *interests*, not strictly your own commandable agents — this mirrors the game's own threat panel
  (`target.isCommandable() || target is UAE`), so a hero hunting a shadow-aligned third party such as an
  **orc upstart** is a correct match, NOT a FAIL. If no hostile-to-you unit exists within ~15 `end_turn`s,
  mark SKIP (enemy intent is not forcible) — do NOT FAIL.

### Reporting (required output)
1. Print a table with columns: `id | area | result (PASS/FAIL/SKIP/BLOCKED) | expected | observed (tool +
   before→after) | notes`. One row per check above.
2. Print a summary line: `PASS x / FAIL y / SKIP z / BLOCKED w` and the start→end turn numbers.
3. List every FAIL and BLOCKED with the exact tool call + response so a human can reproduce it.
4. **Also write the same report to a file** named `mcp-test-results-turn<startTurn>.md` in the working
   directory (use the starting turn number from preflight so the name is stable). Confirm the file path in
   your final message.

Work through A→J in order. Be concise in intermediate narration; the value is in the evidence and the final
report.
````

---

## Notes

- This exercises the same functional surface as `docs/manual-test-checklist.md` §5–§8, minus the parts an
  agent can't do (install §1–2, firewall/LAN §3–4) and minus "visual confirmation in-game" (replaced by
  state-diffs).
- Opportunistic checks (deaths, events, game-over, hitting caps) may legitimately come back `SKIP` on a
  short run — that's expected, not a failure. For fuller coverage, run it again on a more advanced save, or
  let it play more turns.
- If a client leaves some MCP tools deferred/unloaded, the agent will report those as BLOCKED by name —
  which is itself useful signal about the client's tool loading.
