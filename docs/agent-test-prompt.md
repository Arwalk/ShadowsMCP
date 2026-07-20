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

### Tools available (31)
game_overview, get_threats, world_summary, list_locations, get_location, list_units, get_unit,
list_persons, get_person, list_social_groups, get_social_group, list_wars, list_investigations,
list_holy_orders, get_recent_events, get_player_state, get_victory_breakdown, list_recruitable_agents,
list_powers, list_challenges, get_tips, inspect, move_unit, cancel_task, perform_challenge, use_power,
recruit_agent, command_army, get_pending_decision, resolve_decision, end_turn.

### Preflight (if this fails, stop and report BLOCKED)
1. Call `game_overview`. If it errors ("no game in progress" / not ready) or the core tools are missing,
   stop and report a single BLOCKED row explaining what was unavailable. Otherwise record the starting
   `turn` and note it in the report header.

### Rules of engagement — READ CAREFULLY
- **Discover ids dynamically; never hardcode.** Unit ids (`U*`) come from `list_units`, locations (`L*`)
  from `list_locations`/`get_location`, challenges (`C*`, now deterministic — `C{loc}-{Type}-{hash}`, or
  `Cr-…` for rituals) from `list_challenges`, archetype codes from `list_recruitable_agents`, social groups
  (`SG*`) from `list_social_groups`. Unit ids are session-scoped — if a tool says "stale id", re-query;
  challenge ids are now stable across turns and save/load (no need to re-list before `perform_challenge`).
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
- **Responses are compact JSON, and any key whose value would be `null` is omitted** (absent ≡ null / none /
  not-applicable — e.g. a cleared `task`, no `pendingDecision`, an undecided `victoryMode`). Assert on
  presence-or-absence, never on a literal `null`. The one exception is `inspect`, which keeps nulls.
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
  object with `stats` and a `traits` array whose entries are `{name, desc}` objects (not bare strings).
  Assert the id round-trips (the detail's id matches the one you asked for).
- A8 (societies): `list_social_groups` returns factions; `get_social_group` on your own faction (the one
  owning your commandable agents, e.g. from `list_units`.society) returns a detail object. Assert it
  round-trips.

**B. New state fields (recruitment + end-of-game)**
- B1: `game_overview` includes `agentCap`, `canRecruit`, `endOfGameAchieved`, `defeated`,
  `availableEnthrallments`.
- B2: `get_player_state` includes `agentCap`, `canRecruit`, `endOfGameAchieved`, `enthralledCount` (and
  `victoryMode` once the game is decided — omitted while still playing).
- B3: consistency — `canRecruit` == (`availableEnthrallments` > 0 AND `enthralledCount` < `agentCap`), and
  `defeated` == (`endOfGameAchieved` AND NOT `victoryAchieved`). Compute from the fields and compare.

**C. Movement**
- C1: pick a commandable agent from `list_units {"scope":"mine"}`; `get_location` its location to get a
  neighbour id; `move_unit` there. Assert the result's `nowAt`/`arrived`/`task`, then `get_unit` shows a
  go-to task (or it already arrived). Evidence = task before (likely absent) vs after.
- C2: `cancel_task` on that unit (or `move_unit` to its own location) clears the order — `get_unit.task`
  becomes null/absent (the key is omitted once the task is cleared). (If the unit fully arrived and is idle,
  note that and still assert task is null/absent.)
- C3 (error): `move_unit` with a bad `locationId` errors cleanly.
- C4 (error): find a non-commandable unit (`list_units {"scope":"all"}`, `commandable:false`) and try to
  `move_unit` it — errors "not under your command".

**D. Challenges**
- D1: `list_challenges {"unitId":"U..."}` for one of your agents returns a list (may be empty — if empty,
  move the agent to a settlement and retry once, else SKIP D2).
- D2: `perform_challenge` on a listed challenge; assert `get_unit.task` became a perform/travel task.
- D3 (opportunistic): if a challenge has >4 turns of progress, `move_unit`/`cancel_task` without `force`
  returns an abandon-warning error, and with `force:true` succeeds.
- D4 (restriction reason): in `list_challenges`, assert that any challenge with `valid:false` (or
  `validForUnit:false`) carries a `restriction` string explaining what it needs. If every listed challenge
  is valid, SKIP with a note (restriction is only present when the game supplies hint text).
- D5 (opt-in params): `list_challenges {"unitId":"U...","terse":true}` omits `description` from every entry
  (assert absent) while name/type/`valid` remain; `list_challenges {"unitId":"U...","performableOnly":true}`
  returns only entries with `valid` AND `validForUnit` true (assert none has either false). Compare counts
  against the unfiltered call.
- D6 (error names the reason): find a listed challenge with `valid:false` and `perform_challenge` it; assert
  the error message includes the `restriction` text, not just "requirements … are not met". If none is
  invalid, SKIP.
- D7 (stable ids): `list_challenges` for one of your agents and cache a valid challenge's `id`. `end_turn` a
  few turns (no `force` needed), then `perform_challenge {"unitId":"U...","challengeId":"<cached>"}` WITHOUT
  re-listing — assert it is accepted (a perform/travel task appears via `get_unit`), proving the id survived
  the turns. Assert the id has the deterministic shape (`C{loc}-{Type}-{hash}` or `Cr-…`), not `C8`.
- D8 (stale-id error lists alternatives): `perform_challenge {"unitId":"U...","challengeId":"C-nope"}`
  returns a clean error that BOTH says the id is unknown/stale AND lists that unit's currently-available
  challenge ids+names to retry with.

**E. Powers**
- E1: `list_powers` returns powers with `cost` and a castable flag.
- E2: pick a castable power with a valid target (a unit or location per its restriction); snapshot
  `get_player_state.power`; `use_power`; assert `remainingPower == before - cost`. If no power is castable
  right now, SKIP with reason.
- E3 (error): calling `use_power` on a passive power, or with insufficient power, or with both/neither
  target, errors cleanly.

**F. Recruitment**
- F1: `list_recruitable_agents` returns `capacity` (availableEnthrallments, nEnthralled, agentCap,
  canRecruit), `archetypes` (each with code, stats, restrictions, and a `placement` object with
  `eligible` + `exampleTargets`), and `corruptibleHeroes`.
- F1b (placement data): every archetype carries `placement.eligible` (bool) and `placement.exampleTargets`
  (up to 4 location refs where it can be enthralled right now). For a **gated** archetype (restriction is
  not "can be placed anywhere") with `eligible:true`, each example plausibly matches the restriction (e.g.
  a Warlock's examples are library-cities); if nothing qualifies, `eligible:false` and `exampleTargets` is
  empty. The Hierophant (`code -1`) is always `eligible:true` with several examples. (Only meaningful while
  `capacity.canRecruit` is true — at the agent cap every archetype reports `eligible:false`.)
- F2: if `capacity.canRecruit` is true, pick an archetype with a permissive restriction (e.g. one whose
  restriction says "can be placed anywhere", typically the Hierophant, code -1) and a valid `locationId`
  (any id from that archetype's `placement.exampleTargets` is guaranteed to work);
  snapshot `availableEnthrallments` and `list_units {"scope":"mine"}` count; `recruit_agent
  {"agentCode":<code>,"locationId":"L..."}`; assert a new agent appears (mine count +1),
  `availableEnthrallments` −1, and the result may include `levelUpPending`. If `canRecruit` is false, SKIP
  F2 (and note why).
- F3 (error): `recruit_agent {}` (neither agentCode nor heroUnitId) errors "specify exactly one…".
- F4 (error): `recruit_agent {"agentCode":<code>,"heroUnitId":"U1"}` (both) errors.
- F5 (error): `recruit_agent {"agentCode":<code>}` (no locationId) errors asking for a location.
- F6 (error): recruit an archetype with a **restrictive** placement onto an invalid location (e.g. a
  location that clearly doesn't meet its restriction) — errors with the archetype's restriction text, and
  the message also lists suggested valid targets ("valid targets right now include: L…") or, when none
  exist, says no location currently satisfies the archetype.
- F7 (opportunistic): if `corruptibleHeroes` is non-empty, `recruit_agent {"heroUnitId":"U..."}` corrupts it
  in place — that unit becomes commandable (`get_unit.commandable` true) and `availableEnthrallments` drops.
  Else SKIP.
- F8 (opportunistic): if `availableEnthrallments` reaches 0, a further `recruit_agent` errors "no
  recruitment points"; if `nEnthralled` reaches `agentCap`, it errors "agent cap reached". Test whichever
  you can reach; SKIP the rest.

**G. Decisions & blocking**
- G1: when nothing is pending, `get_pending_decision` returns `{pending:false}` and
  `game_overview.pendingDecision` is null or absent (the key is omitted when nothing is pending).
- G2: call `end_turn` repeatedly (up to ~10 times) until either a decision appears
  (`game_overview.pendingDecision` non-null, and every tool result is prefixed with a `⚠` banner) or the
  budget is exhausted. If one appears, `get_pending_decision` lists its options with indices.
- G3: resolve a decision two ways across the run — once via `resolve_decision {"optionIndex":0}` (assert the
  banner clears / pending becomes false), and once via `end_turn {"resolveOptionIndex":0}` (assert it
  answers and then advances or surfaces the next decision). If only one decision type ever appears, do it
  the once and SKIP the other with a note.
- G4 (idle-agent alert): if `end_turn` reports `blockedBy:"decision"` with an idle-agents kind, resolve it
  with `resolve_decision {"optionIndex":0}` (pass all) OR by ordering an agent, then `end_turn` advances.
  `force` does NOT skip idle: like combat, the idle alert blocks even under `force` — assert
  `end_turn {"force":true}` with an idle agent does NOT advance and returns `blockedBy:"decision"` /
  `kind:"idleAgents"` (and `resolve_decision {"force":true}` no longer passes idle: it asks for
  `optionIndex 0`). Idle is a recurring state (`Task_PassTurn` lasts one turn), so a `count>1` `force` batch
  stops on the first idle turn (`advancedBy` may be 0, `stopReason:"decision"`, `kind:"idleAgents"`). To
  advance many turns unattended, give every agent a standing order (they leave the idle set) OR pass
  `passIdleAgents:true` — the explicit opt-in that bulk-passes idle each turn (a visible "Passing Turn");
  assert `end_turn {"count":3,"passIdleAgents":true}` advances multiple turns without stopping on idle.
- G5 (opportunistic, agent death): if an agent dies, the popup kind decides the expectation — the turn
  always still advances. A non-combat death surfaces as a `kind:"death"` NOTICE (`PopupMsgAgentsDeath`):
  assert `end_turn {"force":true}` auto-dismisses it (`autoDismissed.count > 0`, `dismissed` includes
  `"death"`). A lost **battle** instead raises a `kind:"event"` "Defeat" popup (`PopupEvent`): assert
  `force` does NOT auto-dismiss it (narrative events are never auto-answered, so no `autoDismissed` entry
  for it) yet `turn` still advances, and it clears via `resolve_decision {"optionIndex":0}` /
  `end_turn {"resolveOptionIndex":0}`. Test whichever path occurs; SKIP the rest.
- G6 (opportunistic, item trading): if an item-trade popup ever blocks (`game_overview.pendingDecision` /
  `get_pending_decision` shows `kind:"itemTrading"`, `popupType:"PopupItemTrading"`), assert it exposes a
  `sides` array of two `{side, name, gold, items:[{name, top?}]}` objects and `options` whose labels are
  readable (e.g. "Take ALL…", "Done…", "Rotate side A…", "Move … gold to side B") — NOT raw
  "Button (Previous)". Resolve a non-closing option (a rotate) via `resolve_decision {"optionIndex":N}` and
  assert the returned `sides` reflect the change. Item trades aren't forcible → SKIP if none appears.
- G7 (permanent-silence warning): if any popup ever offers a "No longer show message of type…" option (e.g.
  a `PopupMsgUnified`), assert that option's label carries the explicit WARNING that it PERMANENTLY hides the
  type for the whole game (persists across reload) — so an agent won't blind itself. SKIP if none appears.

**H. End turn & game-over**
- H1: snapshot `game_overview.turn`; `end_turn` (no force); assert the returned/`game_overview` turn
  increased by ≥1.
- H2: while `game_overview.endOfGameAchieved` is false, `end_turn` continues to advance (covered by H1/G2).
- H3 (invariant): if at any point your commandable-unit count hits 0, assert `endOfGameAchieved` is STILL
  false and `end_turn` STILL advances — losing all agents is NOT a loss. (If you never hit 0 agents, SKIP.)
- H4 (opportunistic): if the game ends (`endOfGameAchieved` true), assert `end_turn` returns
  `{gameOver:true, outcome:"victory"|"defeat", ...}` and does NOT advance `turn`. Else SKIP.
- H5 (multi-turn): snapshot `game_overview.turn`; `end_turn {"count":3,"force":true,"passIdleAgents":true}`
  (`passIdleAgents` so an idle agent doesn't legitimately stop the batch at `advancedBy:0`); assert the result
  has `advancedBy` (1–3), `requestedCount:3` and a `stoppedEarly` bool, and that `turn` rose by exactly
  `advancedBy`. If `stoppedEarly` is true, assert a `stopReason` is present
  (decision / gameOver / threatEscalation / threatMotivation / notAdvanced). (Without `passIdleAgents`, an
  idle agent is expected to stop the batch early with `advancedBy:0`, `stopReason:"decision"` — see G4.)
- H6 (opportunistic, threatAlert): if any `end_turn` (single or batched) returns a `threatAlert`, assert it
  is an array whose entries each name an `agent`, a `trigger`
  (becameHuntable / gainedHunter / worsened / motivation), and — when a hunter is present — a hunter with a
  `motivationPct`, and that the same agent appears in `get_threats.agentSafety`. Note the retuned stop no
  longer fires for a merely-in-range, favoured, non-huntable hunter. Else SKIP.
- H7 (motivation stop): read a top hunter's `motivationPct` = M (>0) from `get_threats`; call
  `end_turn {"count":3,"stopOnThreatMotivation":<a value ≤ M>}`. The stop is **level-triggered**: assert it
  stops on turn 1 with `stopReason:"threatMotivation"` and a `threatAlert` entry whose `trigger` is
  `motivation` — it fires whether the hunter was ALREADY ≥ the threshold at batch start (the common case that
  used to be missed) or rises to it mid-batch. Also confirm `motivationPct` can now read >100 when the game's
  own threat text does (the flat-100 cap was removed), so a threshold >100 is accepted. SKIP only if no
  hunter with motivation >0 exists.

**I. Robustness / soak**
- I1: run `end_turn {"force":true,"passIdleAgents":true}` for ~5–10 turns in a row; assert it never stalls
  (each call returns, advancing or clearly reporting a preserved real decision) and `turn` keeps climbing.
  (`passIdleAgents` so the recurring idle-agent alert — which now blocks even under `force`, like combat —
  doesn't legitimately halt the climb; without it, an idle agent is expected to stop the advance.)
- I2 (error): a malformed call (e.g. `perform_challenge {"unitId":"U1"}` with no challengeId, or a bogus
  challengeId like `C-nope`) returns a clean error, not a hang. (Challenge ids no longer go stale over
  turns, so a genuinely-invalid id is the way to exercise this.)

**J. Threats & enemy intent**
- J1: `get_threats` returns a `count` and a `threats` array. Each entry has `message` (string),
  `severity` (number), `beneficial` (bool), and `location` (a `{id,name}` ref, omitted when the event
  points at no location). PASS if the
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
- J4 (combat odds): `get_threats` includes an `agentSafety` array. For each of your field agents it has
  `dangerEstimate` (number), `isHuntable` (bool), `inHiding` (bool) and a `verdict`
  (safe/favoured/even/outmatched); when a hunter is near it also has `topHunter` with `motivationPct` and
  `dangerEstimate`. Assert the shape for every entry. Cross-check: an agent with a `topHunter` here should,
  via `get_unit`, expose a `combat` block whose `dangerEstimate` matches. (Empty `agentSafety` is PASS when
  you have no agents in the field — note it.)

**K. Analysis surfaces (enriched detail views + new tools)**
- K1 (time budget & panic): `game_overview` includes `maxTurns`, `turnsRemaining` (omitted in an endless
  game), a `victoryMode` (a label once the game is decided, else omitted), and a `panic` object with numeric
  `total`/`fromPowerUse`/`fromCluesDiscovered`/`heroesFallen`/`temporaryChange`. Assert `maxTurns`, `panic`
  are present and that `panic.total` equals `worldPanic`.
- K2 (win-condition sheet): `get_player_state.progression` has `maxTurns`, `sealLevels` (array),
  `agentCaps` (array) and `powerLevelReqs` (array). Assert present.
- K3 (settlement economy): find a human settlement (`list_locations`, then a `get_location` whose
  `settlement.isHuman` is true) and assert `settlement.population` (number) and `settlement.food` (object)
  are present. If no human settlement turns up, SKIP.
- K4 (person sheet): `get_person` on any `P*` returns `traits` and `items` as arrays of `{name, desc}`
  objects, plus `xp`, `relationships` and `alerts`. Assert a trait entry has a `name` field.
- K5 (agent internals): `get_unit` on one of your agents (`list_units {"scope":"mine"}`) returns an `agent`
  object with a `minions` array and numeric `attack`, and an `investigation` array (possibly empty). Assert
  `agent` is present.
- K6 (wars): `list_wars` returns `items`; assert its `total` equals `game_overview.wars`. If non-empty, each
  entry has `attacker`, `defender`, `objective`. (Empty is PASS on a peaceful game — note it.)
- K7 (investigations): `list_investigations` returns `items`; assert its `total` equals
  `game_overview.activeInvestigations`. If non-empty, an entry's `target` (`U*`) round-trips via `get_unit`.
  (Empty is PASS on a quiet game.)
- K8 (religion): `list_holy_orders` returns `items`; if non-empty, each has a `holyOrder` block with numeric
  `enshadowment`, and calling `get_social_group` on that group's id shows the same `holyOrder` block. If
  empty, SKIP.
- K9 (recent events): after the `end_turn`s run above, `get_recent_events {"limit":10}` returns `total`,
  `returned` and an `items` array (≤10), newest-first, each entry `{turn, type, title, message?,
  resolution?}`. Assert it is **non-empty** and is a **cross-turn** log: an event from an earlier turn is
  still present now (it accumulates; it is not the single-turn on-screen feed). Furthermore, if any
  `end_turn` this run reported `autoDismissed` or resolved a `pendingDecision` of kind death/level-up/event,
  at least one matching `type:"death"`/`"levelUp"`/`"event"` entry must appear here. (Only PASS-empty if no
  turn has advanced yet.)
- K10 (world map): `world_summary` returns paginated `items`; each row has `id`, `neighbours` (ids) and,
  for settled rows, a `settlement` with numeric `shadow`/`defences` (and `population` for human ones) plus a
  `coords {x,y,z}`. Assert at least one settlement row and that its `id` round-trips via `get_location`.
  Then `world_summary {"settlementsOnly":false}` returns a `total` ≥ the settlements-only `total` (empty
  hexes included).
- K11 (victory breakdown): `get_victory_breakdown` returns a `breakdown` string (a multi-line scoring sheet
  mentioning "Points to win" and a "Score total") plus numeric `victoryProgress`, `avrgEnshadowment` and
  `pointsToWin`. Assert `victoryProgress` equals `game_overview.victoryProgress`, and `avrgEnshadowment`
  equals `game_overview.avrgEnshadowment`.
- K12 (seal countdown): `game_overview.seals` and `get_player_state.seals` each have `sealsBroken`,
  `sealProgress` and (unless all seals are already broken) `nextSealAt` and `turnsToNextSeal`. Assert
  `turnsToNextSeal == nextSealAt - sealProgress`. Advance a few turns and assert `sealProgress` increased
  and `turnsToNextSeal` shrank by the same amount.
- K13 (danger breadcrumb + inventory): `game_overview.threats` has numeric `agentsInField`,
  `agentsInDanger` and `agentsHuntable` (agents with profile>=50 & menace>25, exposed to assassination),
  plus a `mostUrgent` string whenever `agentsInDanger > 0` OR `agentsHuntable > 0` — assert all present.
  `get_unit` on
  one of your agents now includes a `combat` block ({dangerEstimate, hp, defence, attack, menace, profile,
  menaceFloor, profileFloor, huntRadius, isHuntable, inHiding}) and an `items` array (possibly empty) — assert
  both keys present, that `combat.huntRadius == floor(combat.profile / 5)`, and that `menaceFloor`/`profileFloor`
  are numeric.
- K14 (infiltration detail): `get_location` on a settled human location includes `settlement.infiltration`
  (0..1) and a `subsettlements` array whose entries are `{name, infiltrated}` objects (not bare strings).
  Assert the object shape. If no settled location is handy, SKIP.
- K15 (mechanics tips): `get_tips` with no arguments returns a `tips` array whose entries are `{id, title,
  category, summary, core}` plus a `hint` string — assert non-empty, that an entry carries `id` and
  `summary`, and that the index includes the ids `menace`, `profile` and `enshadow_home`. Then `get_tips
  {"id":"infiltration"}` returns one tip with a `body` string, and `get_tips {"id":"menace"}` and `get_tips
  {"id":"profile"}` each return a `body` that names the huntable thresholds (50 / 25); `get_tips
  {"category":"god"}` returns a `tips` array (all in that topic); `get_tips {"id":"nope"}` returns a clean
  "unknown tip id" error (isError). Assert all.
- K16 (contextual tips, opportunistic): a `tips` array may appear on `game_overview` and/or `end_turn` when a
  mechanic first becomes relevant (world panic crossing a threshold, a war starting, an agent entering the
  menace/profile danger band → the `agent_exposed` tip, a god- or faction-specific rule). If one appears,
  assert each entry is `{id, title, body}` and that its `id` resolves
  via `get_tips {"id":...}`, and that the same tip does NOT reappear on the next same-tool call (one-shot per
  game). If none appears within the run, SKIP with a note. (The core-mechanics primer also ships in the
  server's `initialize` instructions, which this checklist does not read directly.)

**L. Commandable-army orders (`command_army`)**
- L1 (error, wrong unit type): pick one of your agents (a `UA`, `kind:"agent"` from `list_units
  {"scope":"mine"}`) and call `command_army {"unitId":"U...","order":"raze"}`; assert a clean error saying it
  is not a military unit (agents can't raze). (Always testable.)
- L2 (orders are military-only): `get_unit` on that same agent does NOT include an `orders` array (the key is
  omitted for non-military units). Assert absence.
- L3 (error, bad order value): if you have any commandable **military** unit (`kind:"military"` AND
  `commandable:true` — from `list_units {"scope":"military"}`), call `command_army
  {"unitId":"U...","order":"nope"}` and assert a clean "unknown order" error. If you have no commandable
  military unit, SKIP (they come from an awakened god such as She Who Will Feast, or mid-game orc raiders —
  not forcible on a short run).
- L4 (opportunistic, orders shape): if a commandable military unit has an `orders` array in `get_unit`, assert
  each entry is `{order, target, hint}` with `order` ∈ {raze, drive_back, attack} and `target` a `{id,name}`
  ref (a location for raze, a unit for drive_back/attack). Else SKIP.
- L5 (opportunistic, live raze): if a commandable military unit shows a `raze` order (it is standing on a human
  settlement), snapshot that settlement's `defences` (`get_location`), call `command_army
  {"unitId":"U...","order":"raze"}`, assert `get_unit.task` becomes a raze task, then `end_turn` a turn or two
  and assert the settlement's `defences` dropped (heading toward destruction). Else SKIP.
- L6 (error, on-tile target): if you have a commandable military unit, `command_army
  {"unitId":"U...","order":"attack"}` with no `targetUnitId` (or a `targetUnitId` for a unit not on its tile)
  returns a clean error asking for an on-tile target. Else SKIP.

**M. Agent combat (`get_pending_decision` / `resolve_decision`)** — combat requires a hostile hero to reach
one of your agents, which is **not forcible** on a short run. Watch `game_overview.threats.agentsUnderAttack`
and `get_unit.engagedThisTurn` across your `end_turn`s; if no agent is ever attacked, mark M1–M4 **SKIP** with
a note.
- M1 (opportunistic, signal agreement): the first turn an agent is under attack, assert the signal is
  consistent across surfaces — `game_overview.threats.agentsUnderAttack ≥ 1` with an `underAttack` list; the
  same unit shows `engagedThisTurn:true` (+ `underAttackBy`) in both `list_units {"scope":"mine"}` and
  `get_unit`; and a `pendingDecision` of `kind:"combat"` appears (also as the ⚠ banner prefixing tool
  results). Else SKIP.
- M2 (opportunistic, force is blocked — the core guarantee): while an agent is under attack, call `end_turn
  {"force":true}` and assert it does NOT advance `turn` and returns `blockedBy:"combat"` with a
  `pendingDecision` of `kind:"combat"`. Battles are never auto-resolved. Else SKIP.
- M3 (opportunistic, open the battle): with a `kind:"combat"` decision pending (the engaged-agent list; read
  `battles` + per-battle `verdict` via `get_pending_decision`), `resolve_decision {"optionIndex":0}`. Assert
  the result reports the battle opened, and a follow-up `get_pending_decision` now returns
  `popupType:"PopupBattleAgent"` with `attacker`/`defender` blocks (name, hp, attack, minions), `round`/
  `state`, and an `options` list containing "fight to the end" and "step" (plus flee/retreat only from round
  2). Else SKIP.
- M4 (opportunistic, resolve the battle): from the open battle menu, `resolve_decision` with the fight-to-the-
  end option (or `{"force":true}`); assert it closes with an `outcome` and, on a win, a `victor`/`defeated`
  (and possibly a chained "Loot the Fallen Foe" `PopupItemTrading` as the next `pendingDecision`). Confirm the
  battle no longer blocks `end_turn`. (Choosing flee/retreat instead is equally a PASS, as long as the battle
  resolves.) Else SKIP.
- M5 (opportunistic, army field battle): if any of your military units shows `inBattle:true` in `list_units`,
  assert `get_unit` on it has a `battle` block with `attackers`/`defenders`, `commandAdvantagePct`, and
  `advantageFavours`. Army battles auto-resolve and do NOT block `end_turn`. Else SKIP.

### Reporting (required output)
1. Print a table with columns: `id | area | result (PASS/FAIL/SKIP/BLOCKED) | expected | observed (tool +
   before→after) | notes`. One row per check above.
2. Print a summary line: `PASS x / FAIL y / SKIP z / BLOCKED w` and the start→end turn numbers.
3. List every FAIL and BLOCKED with the exact tool call + response so a human can reproduce it.
4. **Also write the same report to a file** named `mcp-test-results-turn<startTurn>.md` in the working
   directory (use the starting turn number from preflight so the name is stable). Confirm the file path in
   your final message.

Work through A→M in order. Be concise in intermediate narration; the value is in the evidence and the final
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
