# ShadowsMCP — agent-run test prompt

Paste everything in the **Prompt** block below into an agent (Claude Code / Desktop) that is connected to
the ShadowsMCP server. It is the automated counterpart to `docs/manual-test-checklist.md`: instead of a
human watching the game window, the agent drives the game **through MCP tools only** and reports pass/fail.

## Before you run it (human setup — the agent cannot do these)

1. Install & enable the mod and start the game. Leaving it at the main menu is fine — the agent starts a
   throwaway game itself with `new_game` (preflight). The tests start/abandon games, move units, spend
   recruitment points, and end turns — all irreversible, with no save tool exposed — so do **not** run this
   while a game you care about is loaded (section O will abandon it).
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

### Tools available (35)
game_overview, get_threats, world_summary, list_locations, get_location, list_units, get_unit,
list_persons, get_person, list_social_groups, get_social_group, list_wars, list_investigations,
list_holy_orders, get_recent_events, get_player_state, get_victory_breakdown, list_recruitable_agents,
list_powers, list_challenges, get_tips, inspect, move_unit, cancel_task, perform_challenge, use_power,
recruit_agent, command_army, command_agent, influence_holy_order_tenet, oppose_divinity, get_pending_decision,
resolve_decision, end_turn, new_game.

### Preflight (if this fails, stop and report BLOCKED)
1. Call `game_overview`. If it errors with "no game in progress", start a throwaway game yourself:
   `new_game {"god":"snake","mapSize":"small","seed":12345}` — it is SLOW (~30-120s), make ONE call and
   wait; assert the result has `started:true`, `seed:12345` and an `overview`, and record this as check
   **O0** (PASS/FAIL). If `game_overview` errors any other way, or the core tools are missing, stop and
   report a single BLOCKED row explaining what was unavailable. Otherwise record the starting `turn` and
   note it in the report header.

### Rules of engagement — READ CAREFULLY
- **Discover ids dynamically; never hardcode.** Unit ids (`U*`) come from `list_units`, locations (`L*`)
  from `list_locations`/`get_location`, challenges (`C*`, now deterministic — `C{loc}-{Type}-{hash}`, or
  `Cr-…` for rituals) from `list_challenges`, archetype codes from `list_recruitable_agents`, social groups
  (`SG*`) from `list_social_groups`. Unit ids are session-scoped and reset whenever
  `game_overview.idEpoch` changes (new game / loaded save) — if a tool says "stale id" or idEpoch moved,
  re-query; challenge ids are now stable across turns and save/load (no need to re-list before
  `perform_challenge`).
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
- **An unhandled crash now names itself — always FAIL it, never work around it.** A message starting
  `tool failed:` / `command_agent <order> failed:` / `tool '<name>' failed:` carries the exception TYPE and
  the top stack frames (e.g. `NullReferenceException: … at ShadowsMcp.Tools.ActionTools.CommandAgent
  (ActionTools.cs:697)`). That is a mod bug, never a game rule: quote those frames verbatim in the report —
  they are what makes it fixable — and do NOT retry the call in the hope it passes. Retrying with `force` or
  from a fresh turn will not help; a real rule violation reads as a sentence about the game, not as a type
  name and a file:line.
- **Responses are compact JSON, and any key whose value would be `null` is omitted** (absent ≡ null / none /
  not-applicable — e.g. a cleared `task`, no `pendingDecision`, an undecided `victoryMode`). Assert on
  presence-or-absence, never on a literal `null`. The one exception is `inspect`, which keeps nulls.
- Keep a running list of `{id, area, result, expected, observed, notes}` for the final report.

### Test checklist

**A. Sanity & cross-consistency**
- A1: `game_overview` returns `turn`, `god.name`, `counts`, and an integer `idEpoch` >= 1. (PASS if all
  present.)
- A2: `inspect {"path":"map.turn"}` equals `game_overview.turn`.
- A3: `list_units {"scope":"mine"}` count equals `game_overview.counts.commandableUnits`.
- A4: `get_player_state` returns a `god`, `agents` array, and `power`.
- A5: `inspect {"path":"map.locations[0]","depth":2}` returns a nested object (round-trip works).
- A5b (challenge-id roots): take a `challengeId` from `list_challenges` on one of your units (the full
  deterministic form, e.g. `C31-Ch_Elf_ElderBirthright-92486fbb`) and `inspect {"path":"<thatId>","depth":1}`.
  Assert it resolves to the challenge object (a `$type` starting `Ch_`), NOT a parse error about `-`.
- A5c (back-reference suppression): `inspect {"path":"SG0","depth":2}` (any social group id) — assert its
  `map` field is a short string marker (`<Map: back-reference suppressed …>`), not thousands of tokens of
  expanded world state; Unity-engine objects likewise collapse to `<Unity:TypeName>`. The root itself is
  exempt: `inspect {"path":"map","depth":1}` still returns the map's own fields.
- A6 (error): `get_unit {"unitId":"U9999"}` and `get_location {"locationId":"L9999"}` both return clean
  "unknown/stale id" errors; the unit one must mention `idEpoch` (the reset rule: all U-ids reset whenever
  `game_overview.idEpoch` changes).
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
  `victoryMode` only once the game is WON — omitted while playing and on a defeat).
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
- D5c (hero-only challenges hidden): assert no `list_challenges` result for one of YOUR agents contains a
  hero-side "good" challenge (e.g. `Ch_CombatBanditry` — the game hides `isGoodTernary()==1` entries from
  agents), and that `perform_challenge` on such an id (if you have one cached) errors with "heroes-only".
  If no such challenge exists nearby, SKIP.
- D5d (item rituals visible AND performed in place): if any of your agents carries a ritual-granting item
  (e.g. Iastur's Laughing Tome, a Horde Banner — check `get_unit`'s person items), assert `list_challenges`
  for it lists the item's ritual(s) in `unitRituals` tagged `fromItem`, that each ritual entry carries a
  `performsAt` marker saying it runs at the unit's CURRENT location and NO `location` field (item rituals
  used to report a bogus far-away hex), and that `perform_challenge` on the `Cr-` id starts IMMEDIATELY in
  place: the result must say `status:"started"` with a `performedAt` naming the unit's current location —
  any "travelling to …" status for a ritual is a FAIL. Else SKIP.
- D5b (nothing silently filtered): in the `performableOnly:true` call of D5, if the unfiltered call had
  more entries, assert the result carries `hiddenNotPerformable` with a `count` equal to the difference and
  `items:[{id,name,restriction?}]` naming what was hidden (plus a `hint`). This is how gated families
  (e.g. the Geomancy `Mg_*` challenges at a geomantic locus) stay discoverable. If nothing was filtered,
  assert `hiddenNotPerformable` is absent. Truncation must be explicit: `truncated:true` plus a
  `truncatedNote` appear exactly when `count > items.length` (items caps at 20), and both are absent when
  nothing was dropped. When a unit's OWN ritual (unit or item-granted, `ritual:true`) was filtered
  alongside 20+ location entries, it must appear WITHIN `items` — the unit's kit is recorded before
  location challenges and can no longer be pushed out of the window.
- D6 (error names the reason): find a listed challenge with `valid:false` and `perform_challenge` it; assert
  the error message includes the `restriction` text, not just "requirements … are not met". If none is
  invalid, SKIP.
- D6b (ritual errors explain no-auto-travel): `perform_challenge` a `Cr-` ritual whose requirements are
  NOT met (e.g. a location-gated unit ritual while standing elsewhere; SKIP if none): assert the error
  contains the rituals-are-performed-IN-PLACE / "never auto-travelled" note telling you to `move_unit`
  first and retry the same `Cr-` id — the location-gated failure must not read like a plain stat gate.
- D15 (tome state is observable, Iastur only, opportunistic): any listed tome challenge (Summon the Tome /
  Collect Tome / Bind Tome entries) must carry `tomeStatus` with `state` in {`beingBound`,`held`,
  `heldBound`,`activeAtLocation`,`inertAtLocation`,`inEther`}, a `note`, and — per state — the binding
  `unit`+`location`, the `holder`, or the holding `location`. When `state:"beingBound"`, the summon's
  `restriction` must ALSO name the binder ("right now: being bound by <unit> …") so the vanilla "a hero is
  currently binding" text is verifiable; a failing `perform_challenge` on the summon must append
  "Tome status: …" to its error. When `state:"inertAtLocation"`, `list_challenges` at that location must
  expose the Collect Tome challenge. SKIP on non-Iastur gods or when no tome challenge is listed.
- D7 (stable ids): `list_challenges` for one of your agents and cache a valid challenge's `id`. `end_turn` a
  few turns (no `force` needed), then `perform_challenge {"unitId":"U...","challengeId":"<cached>"}` WITHOUT
  re-listing — assert it is accepted (a perform/travel task appears via `get_unit`), proving the id survived
  the turns. Assert the id has the deterministic shape (`C{loc}-{Type}-{hash}` or `Cr-…`), not `C8`.
- D8 (stale-id error lists alternatives): `perform_challenge {"unitId":"U...","challengeId":"C-nope"}`
  returns a clean error that BOTH says the id is unknown/stale AND lists challenge ids+names to retry with,
  labeled with WHICH location they are at.
- D8b (stale-id error targets the encoded location): pick a location index `<idx>` DIFFERENT from the
  unit's current location (from `list_locations`) and call
  `perform_challenge {"unitId":"U...","challengeId":"C<idx>-Ch_Nope-00000000"}`. Assert the error lists
  challenges at THAT location (named, "the location encoded in your id"), not the unit's current one, and
  notes the unit is not there.
- D9 (heat values are the applied ones): in `list_challenges`, assert every `menaceGain`/`profileGain` is a
  small integer consistent with the challenge's own `description` text (e.g. "Fuel the Fire"-style unrest
  challenges show single-digit menace, not ±50/±75-scale numbers, and NEVER a negative like -73). Then
  `perform_challenge` one completable challenge, complete it (end turns), and compare the unit's
  menace/profile before→after (`get_unit`): the delta from the completion must equal the advertised
  `menaceGain`/`profileGain` (other same-turn sources may add on top — note if so).
- D10 (indefinite challenges say so): find an indefinite challenge (e.g. "Lay Low" on an agent, SKIP if
  none available): assert it has `indefinite:true`, `menaceGain:0`/`profileGain:0` (no lump-sum heat), and
  a `heatNote` explaining the per-turn effect lives in `description`. A Lay Low entry must additionally
  carry a `locationNote` (its speed is location-dependent) and NO `etaTurns` (indefinite = no completion).
- D12 (per-unit ETA + rate breakdown): in a non-terse `list_challenges`, pick an entry with
  `progressPerTurn > 0` that is not `indefinite`: assert `etaTurns == ceil(complexity / progressPerTurn)`
  and that `progressBreakdown` is a `[{reason, value}]` array whose values sum to ~`progressPerTurn`
  (±0.01 rounding). With `terse:true` the same entry keeps `etaTurns` but drops `progressBreakdown`.
  If a second agent with different stats can see the same challenge, assert their `progressPerTurn`/
  `etaTurns` differ when their governing stat differs (the rate is unit-relative). For an army (UM),
  `list_challenges` entries now also carry `progressPerTurn`/`complexity` (and `etaTurns` when the rate
  is > 0).
- D13 (channelled heat is start-time, and says so): find a challenge/ritual entry with `channelled:true`
  (channelled spells, e.g. Iastur's Waves of Madness; SKIP if none listed). Assert it carries a `heatNote`
  stating the listed `menaceGain`/`profileGain` are applied in FULL on the first turn of casting and that
  interrupting does not spare them. Opportunistic deep-check: start one and assert the unit's
  menace/profile jump by the advertised amounts on the FIRST end_turn, not on completion.
- D10b (exclusive challenge names its performer): when a single-user challenge (e.g. Lay Low) is actively
  being performed by one of your agents, `list_challenges` for a SECOND agent at that location must show
  the entry with a `restriction` containing "currently being performed by <name>" (and `claimedBy` set);
  `perform_challenge` by the second agent then errors with the same performer name. SKIP if you never have
  two co-located agents wanting the same exclusive challenge.
- D11 (market stalls are distinct): move an agent to a location with a market (SKIP if none reachable):
  `list_challenges` there returns `Ch_BuyItem` entries with DISTINCT `id`s, each carrying
  `itemForSale.name` (assert present even with `terse:true`). A market has 3 stalls; when two stalls
  happen to sell the same-named item they share one deterministic id and MUST appear as a single entry
  with `copies:2` (never the same id twice) — so assert entry count + extra `copies` sums to 3.
  `perform_challenge` with the 2nd or 3rd id and assert the started/travel task targets that exact stall
  (its item name appears in later completion text or the bought item lands in `get_unit`'s inventory).
- D14 (no duplicate ids in a listing): in every `list_challenges` response you fetch during this run,
  assert no `id` appears twice across `challenges` + `unitRituals` + `hiddenNotPerformable.items` —
  interchangeable twins (same-item market stalls, rituals granted by duplicate carried items) must
  collapse into one entry with a `copies` count instead.

**E. Powers**
- E1: `list_powers` returns powers with `cost` and a castable flag. Power ids are stable across turns
  and seal breaks (they index the god's full power roster, not just the unlocked subset) and MAY be
  non-sequential — do not assert contiguous numbering.
- E2: pick a castable power with a valid target (a unit or location per its restriction); snapshot
  `get_player_state.power`; `use_power {"powerId":"PW...", ...}` (the param is `powerId`, not `power`);
  assert `remainingPower == before - cost`. If no power is castable right now, SKIP with reason.
- E3 (error): calling `use_power` on a passive power, or with insufficient power, or with both/neither
  target, errors cleanly.
- E4 (unlisted power, conditional): if `list_powers` ids have a gap (e.g. PW4 listed but PW3 absent — the
  missing power is either behind an unbroken seal or passive-only), `use_power` on the missing id must
  return a "locked until N seals are broken" or "is passive" error, not "unknown power". SKIP if no gap
  exists.

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
- F10 (consumed uniques are explained, opportunistic): after successfully recruiting a UNIQUE archetype
  (positive `code`, e.g. 15 The Seeker), call `recruit_agent` again with the same code. Assert the error
  says the archetype "is a unique archetype" recruitable "only ONCE per game" (NOT a bare "unknown agent
  code") and lists the archetypes still recruitable as `code (name)` pairs. Codes themselves are stable
  constants — only the availability changes. SKIP if no unique was recruited this run.
- F9 (ability previews): every archetype entry carries an `abilities` array of `{name, desc, prereq}`
  objects previewing the rituals it unlocks at recruitment (empty array allowed — e.g. the Bandit King,
  code -4, has no recruit-unlocked rituals and instead carries an `abilityNote` string; `abilityNote` may
  also accompany a non-empty array). Spot-check content: the Hierophant (code -1) lists three "Preach
  Gospel" abilities each requiring 100% infiltration; if the Aristocrat (code 12) is offered, it lists
  "Crisis Vote: Plague" with a plague >50% prereq. (These fields are present only while the in-game
  "Discovery mode" mod config option is off — the default. If every archetype lacks `abilities`, note that
  Discovery mode is likely on rather than failing the check.)

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
- G3b (resolve is never silent): call `end_turn {"resolveOptionIndex":0}` when NOTHING is pending (no ⚠
  banner). Assert the result carries `resolveWarning` saying the index was ignored (and no `resolved`
  object) — a provided resolve that had nothing to act on, or that failed, must always be reported, never
  silently dropped.
- G3c (decisionId guards chained resolves): whenever a decision is pending, call `get_pending_decision`
  TWICE — assert both return the same non-empty `decisionId` (stable for one popup instance) and that
  `game_overview.pendingDecision` carries the same id. Then
  `resolve_decision {"optionIndex":0,"expectedDecisionId":"D-bogus-0"}`: assert a clean error saying the
  pending decision changed / naming the CURRENT id, and that the popup is STILL open (nothing was
  clicked — re-read `get_pending_decision`). Finally resolve with the correct `expectedDecisionId` and
  assert it succeeds. Opportunistic variant: `end_turn {"resolveOptionIndex":0,"expectedDecisionId":
  "D-bogus-0"}` must surface the refusal via `resolveWarning` and leave the decision pending.
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
  `"death"`) and NAMES it: `digest.dismissed` contains an entry with `kind:"death"`, a `turn`, and a
  non-empty `title` mentioning the agent. A lost **battle** instead raises a `kind:"event"` "Defeat" popup (`PopupEvent`): assert
  `force` does NOT auto-dismiss it (narrative events are never auto-answered, so no `autoDismissed` entry
  for it) — the turn that raised it mid-tick still advances, but while it stays open a FURTHER
  `end_turn {"force":true}` does NOT advance (see G8) — and it clears via `resolve_decision {"optionIndex":0}` /
  `end_turn {"resolveOptionIndex":0}`. Test whichever path occurs; SKIP the rest.
- G6 (opportunistic, item trading): if an item-trade popup ever blocks (`game_overview.pendingDecision` /
  `get_pending_decision` shows `kind:"itemTrading"`, `popupType:"PopupItemTrading"`), assert it exposes a
  `sides` array of two `{side, name, gold, items:[{name, top?}]}` objects and `options` whose labels are
  readable (e.g. "Take ALL…", "Done…", "Rotate side A…", "Move … gold to side B") — NOT raw
  "Button (Previous)". Resolve a non-closing option (a rotate) via `resolve_decision {"optionIndex":N}` and
  assert the returned `sides` reflect the change. Item trades aren't forcible → SKIP if none appears.
- G6b (trades report what moved): in the same trade, resolve the "Take ALL…" option and assert the result
  states the outcome explicitly: `itemsMovedToA` (names) and/or `goldDeltaA`/`goldDeltaB` when anything
  moved; when side A's inventory is full and side B still holds items, a `warning` naming how many items
  could NOT be taken (the base game silently skips them; gold still transfers). "clicked Take All" with no
  movement fields and no warning is a FAIL.
- G6e (composite trade verbs, opportunistic): in any `kind:"itemTrading"` decision, assert the `options`
  list ALSO carries synthetic entries after the real buttons — "Take all and close" at index
  `<real button count>` and/or "Swap top items and close" at `<real button count>+1`, each flagged
  `composite:true` (each is listed only when its underlying button exists). Resolve the take-all composite
  and assert the result reports `steps:["bTakeAll","dismiss"]`, the movement fields of G6b, and
  `closed:true` (plus `nextDecision` if a follow-up popup chained). Exception: when side A's inventory
  can't fit everything, the composite must NOT close — assert `closed:false` with the leftover `warning`
  and a `note` explaining how to take the rest. SKIP if no trade appears.
- G6c (event outcomes are never silent): whenever you resolve a narrative `kind:"event"` choice, assert the
  result carries — besides `chose` — at least one of: `outcomeText` (EVERY consecutive outcome message the
  game queued, read and cleared for you — multi-popup chains arrive concatenated with blank lines),
  `followUp` (a non-message popup chained from the outcome and is now the pending decision — then
  `get_pending_decision` must return that popup UN-dismissed), or the explicit no-message `outcome` field
  ("applied without an outcome message …"), which may appear ONLY when neither of the other two does. A
  result with only `chose`, or an `outcome` claiming no message when a text popup was in fact queued, is
  a FAIL.
- G6f (events name their actor and location): any `kind:"event"` decision whose options carry a bracketed
  requirement (e.g. "[Requires: 20 Gold]" on Merchant of Antiquities) must expose `actor` with
  `person {id,name}`, the person's current `gold`, and a note that bracketed gates are checked against
  THIS person — assert each such option's `enabled` is consistent with `actor.gold` vs the bracketed
  amount. Assert `location {id,name}` is present whenever `actor` is (it is the acting person's location),
  including on RECURRING events compacted to "(recurring event; full text shown earlier)" — the location
  must survive the compaction. Both fields may be absent only on god-level events with no acting person.
  SKIP if no qualifying event appears.
- G6d (challenge-complete popup is stable): after any challenge completes, `get_pending_decision` shows
  `kind:"challengeComplete"` with ALWAYS exactly 3 options at fixed indices — 0 Dismiss, 1 dismiss+pan,
  2 "Repeat this challenge immediately" with an `enabled` flag (and a `why` when disabled) — plus the
  completed `challenge` {id,name}. Resolve index 2 when enabled and assert the unit's task became the
  challenge again (`get_unit`); when disabled, assert index 2 returns a clean error (NOT a silent
  dismiss). Assert `end_turn {"force":true}` does NOT auto-dismiss this popup (it holds a real choice).
- G12 (routine events auto-resolve under the opt-in, opportunistic): if a whitelisted routine event
  ("Watched", "Life Continues", "Merchant of Antiquities") appears during a
  `end_turn {"count":N,"passRoutineEvents":true,...}` batch, assert the batch does NOT stop on it and the
  result's `digest.autoResolvedEvents` contains an entry with that `title`, the `chose` label (the curated
  option: Silence them / Subtly disrupt the party / the refusal), a `turn`, and possibly an `outcome`;
  `get_recent_events` must also record the auto-resolution. A NON-whitelisted `kind:"event"` popup must
  still stop the batch even with the flag set. Without the flag, whitelisted events must still block as
  before. SKIP if no routine event fires.
- G11 (cancelled tasks surface in the digest): if a unit's in-progress challenge/ritual is ever
  invalidated mid-cast (e.g. its location's requirements degrade), assert the `end_turn` result's
  `digest.events` contains a `TASK_CANCELLED` entry naming the unit and challenge — the unit going idle
  with no digest event is a FAIL. A silently-dead travel task (challenge vanished / path blocked while a
  unit was en route) must likewise produce a synthesized `TASK_CANCELLED` digest event. Opportunistic —
  SKIP if never observed, but `get_recent_events` should carry any that occurred.
- G7 (permanent-silence warning): if any popup ever offers a "No longer show message of type…" option (e.g.
  a `PopupMsgUnified`), assert that option's label carries the explicit WARNING that it PERMANENTLY hides the
  type for the whole game (persists across reload) — so an agent won't blind itself. SKIP if none appears.
- G8 (opportunistic, force never ticks past an open choice popup): whenever a popup carrying a real choice
  is ALREADY open when you call `end_turn` — a narrative `kind:"event"` (including the "Defeat" popup), an
  open level-up trait pick (`kind:"levelUp"` with traits to choose), `kind:"itemTrading"`, or a list
  selection (`kind:"scrollSet"`) — assert `end_turn {"force":true}` does NOT advance: `advanced:false`,
  `blockedBy:"decision"`, and `pendingDecision` still shows the same popup (force may only pass
  purely-informational "Dismiss" notices). Then answer it (`resolveOptionIndex` / `resolve_decision`) and
  assert the next `end_turn` advances. Skill points are unaffected when no popup is open: with an unspent
  point and no open level-up popup, `end_turn {"force":true}` still auto-spends and advances. SKIP kinds
  that never appear.

- G8 (opportunistic, selection carousel): if a list picker ever blocks (`game_overview.pendingDecision` /
  `get_pending_decision` shows `kind:"carousel"`, `popupType:"PopupScrollSet"` — e.g. Cause Scandal's victim,
  Guard Ruins' minion, a For Idle Hands / Devil Finds Work tag), assert its `options` are the REAL entry
  names (people/tags/minions), NOT carousel controls ("next"/"prev"/"select"/"cancel"), that
  `selectedIndex` is present and exactly one option carries `selected:true` at that index, and that every
  option has `enabled`. Then `resolve_decision {"optionIndex":N}` with an N **different** from
  `selectedIndex`: assert the result's `chose` equals that option's `label` (i.e. you got the entry you
  asked for, not the highlighted one), `closed:true`, and the ⚠ banner clears. SKIP if none appears.

- G9 (opportunistic, minion management): if recruiting a minion past an agent's capacity raises the
  "Minion Management" popup (`get_pending_decision` shows `kind:"minionDismissal"`,
  `popupType:"PopupMinionDismissal"`), assert the options NAME each minion with HP and command cost
  ("Dismiss <name> (HP …, command cost …)" / "Keep <name> …" — NOT "Button (Previous)" or "[Invalid]"),
  the just-recruited one is flagged `newlyAcquired`, a final "Accept current selection" option carries
  `enabled` matching the `state.acceptEnabled` flag, and `state` shows `commandUsed`/`commandLimit`/
  `keptCount`. Toggle one minion via `resolve_decision {"optionIndex":N}`: assert `stillOpen:true` with a
  refreshed `state` and a NEW `options` list. Assert `resolve_decision {"force":true}` is REFUSED (this is
  a real, permanent choice), then pick a valid kept set and accept: assert the popup closes reporting
  `kept`/`dismissed` name lists, and `get_unit` on the agent matches. SKIP if it never appears.
- G10 (opportunistic, trailing battle notices never stall a force loop): during a multi-army field battle
  (several units `inBattle:true` on one tile), run `end_turn {"force":true}` repeatedly. Assert no call is
  ever blocked by a "Battle" notice (`PopupMsgUnified`): a battle popup the game raises late in one call is
  swept at the START of the next force call (its content still reaches `digest.events` / `get_recent_events`,
  so nothing is lost). Only real choices (events, combat) may block. SKIP if no multi-army battle occurs.

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
  (decision / gameOver / unitLost / threatEscalation / threatMotivation / notAdvanced). (Without
  `passIdleAgents`, an idle agent is expected to stop the batch early with `advancedBy:0`,
  `stopReason:"decision"` — see G4.)
- H5b (digest spans the whole batch — the anti-blackout guarantee): run
  `end_turn {"count":5,"force":true,"passIdleAgents":true}` and inspect `digest`. Assert (a) every
  `digest.dismissed` entry carries a `turn` and, unless the popup genuinely has no text, a non-empty
  `title` — a bare kind list is a regression; (b) if `advancedBy > 1` and entries exist across turns,
  their `turn` values are NOT all equal to the final turn (the batch used to report only the last turn);
  (c) no `digest.dismissed` entry has `popupType:"PopupMsgUnified"` (those are reported once, in
  `digest.events`); (d) `autoDismissed.count` is the total over the whole batch, i.e. ≥ the number of
  `digest.dismissed` entries. If nothing was dismissed in those 5 turns, SKIP (a)–(d) and retry later.
- H5c (digest.events is a filtered view of the event log): after H5b, call
  `get_recent_events {"limit":40}` and assert every `digest.events` entry has a matching entry there
  (same `turn` + `title`) — the digest must never invent news. Assert entries about your own units carry
  `mine:true`. SKIP if `digest.events` is absent.
- H5d (opportunistic, unit loss stops the batch — the case that lost a real game): if one of your
  commandable units (agent OR army) dies during a `count>1` batch, assert the result has
  `stopReason:"unitLost"`, `advancedBy < requestedCount`, and `digest.lost` naming the unit
  (`unit` id, `name`, and a `lastLocation` when it had one). Assert the batch did NOT keep advancing
  past the death. To provoke it, `command_army` a weak army onto a stronger enemy, or let a hunted,
  outmatched agent stay in the open. SKIP if you never lose a unit.
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
- H8 (autosave popup handled every 15 turns): the game raises an autosave notice when `turn` becomes a
  multiple of 15. (a) **force path** — advance in `count`≤10 force batches
  (`end_turn {"count":10,"force":true,"passIdleAgents":true}`, repeated) until `turn` crosses a multiple of 15;
  assert each batch advances cleanly with NO leftover `pendingDecision` of `popupType:"PopupAutosaveDialog"`
  (force auto-dismisses the informational notice, so it never stalls the batch). (b) **non-force surfacing** —
  from a turn one below a multiple of 15 (advance there with force first if needed), `end_turn` with NO force
  onto the multiple of 15: assert `turn` rose by 1 AND a `pendingDecision` appears with
  `popupType:"PopupAutosaveDialog"`, `kind:"popup"` (title "Saving game…"/"Game Saved…"); clear it with
  `resolve_decision {"force":true}` and assert pending becomes false. The on-disk write (`Autosave_*.sv`) is
  host-verified only — the MCP surface reads no save file (see `docs/manual-test-checklist.md` §7). If the run
  can't reach turn 15 within budget, SKIP with the reached turn noted.
- H9 (batch resolve is acknowledged even when the batch doesn't advance): with a decision pending that
  `end_turn` can answer (an idle-agents alert is the easiest to arrange — leave an agent idle), call
  `end_turn {"count":3,"resolveOptionIndex":0}`. Assert the result carries a `resolved` object (or a
  `resolveWarning` explaining why the resolve was ignored/failed) **even if** `advancedBy` is 0 or the
  batch stopped on turn 1 — and that a batch Error result appends the resolveWarning text to its message.
  A batch result with neither `resolved` nor `resolveWarning` after a provided `resolveOptionIndex` is a
  FAIL (this was the 0.7.0 silent-drop bug). Variant: `end_turn {"count":3,"resolveOptionIndex":0}` with
  NOTHING pending must carry the G3b `resolveWarning` too.

**I. Robustness / soak**
- I1: run `end_turn {"force":true,"passIdleAgents":true}` for ~5–10 turns in a row; assert it never stalls
  (each call returns, advancing or clearly reporting a preserved real decision) and `turn` keeps climbing.
  (`passIdleAgents` so the recurring idle-agent alert — which now blocks even under `force`, like combat —
  doesn't legitimately halt the climb; without it, an idle agent is expected to stop the advance.)
- I2 (error): a malformed call (e.g. `perform_challenge {"unitId":"U1"}` with no challengeId, or a bogus
  challengeId like `C-nope`) returns a clean error, not a hang. (Challenge ids no longer go stale over
  turns, so a genuinely-invalid id is the way to exercise this.)
- I3 (validation, missing params): `move_unit {}` returns a clean isError naming ALL missing params at
  once (`missing required parameter(s): unitId, locationId`) plus the valid-parameter list — not a blank
  "unknown location id:" error.
- I4 (validation, unknown params): `move_unit {"unitId":"U...","destination":"L..."}` returns
  `unknown parameter 'destination'` listing the valid parameters (so a wrong guess is self-correcting in
  one call); a zero-param tool (e.g. `list_wars {"foo":1}`) errors with "takes no parameters".

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
- K1 (time budget & panic): `game_overview` includes an `endless` boolean, `maxTurns`, `turnsRemaining`
  (both numbers when `endless` is false, both null in an endless game — there is no turn limit then), a
  `victoryMode` (a label only once the game is WON — omitted while playing AND on a defeat), and a `panic`
  object with numeric `fromPowerUse`/`fromCluesDiscovered`/`heroesFallen`/`temporaryChange` (no `total`:
  the top-level `worldPanic` IS the total). Assert `endless`, `panic` are present and that
  `maxTurns`/`turnsRemaining` match the `endless` flag.
- K2 (win-condition sheet): `get_player_state.progression` has `endless`, `maxTurns` (null when `endless`
  is true, consistent with K1), `sealLevels` (array), `agentCaps` (array) and `powerLevelReqs` (array).
  Assert present.
- K3 (settlement economy): find a human settlement (`list_locations`, then a `get_location` whose
  `settlement.isHuman` is true) and assert `settlement.population` (number) and `settlement.food` (object)
  are present. Also assert the settlement carries `fullyInfiltrated` (a bool derived from the infiltration
  fraction) and NOT the old raw `isInfiltrated` key (which meant orc-style takeover and contradicted
  `infiltration:1.0` on fully-infiltrated human cities); same two assertions on a `world_summary` row.
  If no human settlement turns up, SKIP.
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
  `enshadowment`, `influenceElder`, `influenceElderReq` and a boolean `canChangeTenet`, and calling
  `get_social_group` on that group's id shows the same `holyOrder` block (its tenets carry `desc`, since
  that is the detail path). If empty, SKIP the whole of section N too.
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
- K11b (score attribution, opportunistic): whenever the breakdown string's "Enshadowed and Insane Rulers
  and Heroes" or "Insane Rulers and Heroes" line shows a non-zero count (the number after `x`), assert
  `details.insaneAndShadowRulersAndHeroes` / `details.insaneOnlyRulersAndHeroes` exists and its
  `qualifiers` length equals that count (unless `truncated:true`), each qualifier being
  `{id, name, role: ruler|hero, shadow}` with `shadow > 0.5` in the AndShadow list and `<= 0.5` in the
  other. If any Deep One Abyssal City or Sanctum exists, assert `details.deepOneCities` lists the cities
  with `population` and a `sanctumCount` (sanctums never score directly — the note explains). When every
  count is zero and no Deep One settlement exists, assert `details` is absent entirely.
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
  "unknown tip id" error (isError). Assert all. If playing Iastur, also assert `get_tips {"id":"iastur_regen"}`
  returns a `body` noting that the in-game hint popup is WRONG about regen stopping while the Tome is unread
  (the real rule is half rate) — do not report that tip-vs-popup mismatch as a defect; the popup is the stale one.
- K16 (contextual tips, opportunistic): a `tips` array may appear on `game_overview` and/or `end_turn` when a
  mechanic first becomes relevant (world panic crossing a threshold, a war starting, an agent entering the
  menace/profile danger band → the `agent_exposed` tip, a god- or faction-specific rule). If one appears,
  assert each entry is `{id, title, body}` and that its `id` resolves
  via `get_tips {"id":...}`, and that the same tip does NOT reappear on the next same-tool call (one-shot per
  game). If none appears within the run, SKIP with a note. (The core-mechanics primer also ships in the
  server's `initialize` instructions, which this checklist does not read directly.) NOT opportunistic:
  independently of whether any fires, assert the `get_tips` index includes the four 0.8.0 contextual ids
  `insane_heroes_hunt`, `shadow_treadmill`, `alliance_razing` and `iastur_soul`, and that each returns a
  `body` via `get_tips {"id":...}`.
- K17 (one-shot tips survive a same-game reload, opportunistic/host-assisted): contextual tips are one-shot
  per GAME (keyed to the map seed), not per Map object — if the host loads a save of the SAME game mid-run
  (not agent-forcible; the host's Player.log logs "same game: one-shot tips kept"), assert tips that
  already fired do NOT re-fire afterwards. Conversely after `new_game` (a different seed — see O2), the
  one-shot set is fresh. SKIP unless a reload happens.
- K18 (boilerplate is shown once, partly opportunistic): the FIRST decision of a kind (itemTrading /
  idleAgents / combat / event) carries its full `note` and `resolveWith` strings; subsequent decisions of
  the SAME kind in the same session carry a short brief `note` and OMIT `resolveWith` (the brief still
  states the resolve call shape — nothing needed to act is lost). Same rule for `list_units`'
  `ordersLegend` and the `resolveHint` on banner-carrying tools. Opportunistic parts: every ~10th
  suppressed repeat re-emits the full text, and an MCP reconnect (`initialize`) resets all keys to full —
  assert if you can observe either; otherwise assert just the first/brief transition (needs ≥2 decisions
  of one kind; SKIP if never seen twice).
- K19 (recurring narrative events are compacted, opportunistic): when a `kind:"event"` decision with a
  TITLE you have already seen this session appears again, assert its `description` is truncated to the
  dynamic tail (e.g. the "…performing challenge…, progress N/M" line) plus the marker
  "(recurring event; full text shown earlier)", while its `options` (labels, descriptions, `enabled`) stay
  complete. A never-before-seen title must always carry the full description. SKIP if no event title
  repeats.

**L. Commandable-army orders (`command_army`)**
- L1 (error, wrong unit type): pick one of your agents (a `UA`, `kind:"agent"` from `list_units
  {"scope":"mine"}`) and call `command_army {"unitId":"U...","order":"raze"}`; assert a clean error saying it
  is not a military unit (agents can't raze). (Always testable.)
- L2 (orders are per-unit-kind): `get_unit` on that same agent never lists a *military* order — if it has an
  `orders` array at all, every `order` is one of {attack, rob, trade, follow} (the agent verbs, see M0), and
  none is raze/drive_back/attack-an-army. An agent alone on its tile has no `orders` key at all. Assert.
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
- L7 (opportunistic, battle commitment): if a commandable military unit is `inBattle:true` (e.g. after L5/M5
  provokes a fight), `command_army` on it returns the committed/no-retreat error (armies accept no orders and
  cannot disengage once a battle starts; it auto-resolves each `end_turn`), and `get_unit`'s `battle` block
  carries a `note` saying so AND pointing at the 'Command Battle (Attacking)' / 'Command Battle (Defending)'
  challenges (which appear in `list_challenges` only for a unit co-located with the battle). Else SKIP.

**M. Agent combat (`command_agent`, `get_pending_decision` / `resolve_decision`)** — being *attacked* requires
a hostile hero to reach one of your agents, which is **not forcible** on a short run, so M1–M5 are
opportunistic: watch `game_overview.threats.agentsUnderAttack` and `get_unit.engagedThisTurn` across your
`end_turn`s, and if no agent is ever attacked mark M1–M4 **SKIP** with a note. *Attacking* is under your own
control (M0, M6–M9): move an agent onto a tile that holds a hostile hero — heroes cluster in settlements, so
`list_units {"scope":"agents"}` (or `get_threats`) plus a `move_unit` usually arranges it within a few turns.
- M0 (the attack is discoverable): with one of your agents sharing a tile with a hostile hero, assert the
  option surfaces on the always-read tools without being asked for — `get_unit` on that agent has an
  `orders` entry `{order:"attack", target, yourDangerEstimate, theirDangerEstimate, hint}` whose `hint` is
  a literal `command_agent {...}` call; `list_units {"scope":"mine"}` shows the same entry WITHOUT the
  per-row `hint` but the response carries a top-level `ordersLegend` explaining the calls once (and, if
  the target is mid-challenge, the row has `cancelsTheirTask`); and `get_threats.agentSafety` for that
  agent has a `hostilesOnTile` array naming the same hero (with its `dangerEstimate` and `task`) plus an
  `attackHint`. Else SKIP (no co-located hero).
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
  `state`, and an `options` list containing "fight to the end", "step", AND — appended LAST, so the
  existing indices are unchanged — a "flee as soon as possible" option (id `fleeAsap`, present from round
  1 while your side is alive and the battle undecided; its label spells out the round-2 "lose ALL minions"
  cost vs the safe round-3+ retreat). Direct flee/retreat buttons still appear only from round 2. Else SKIP.
- M4 (opportunistic, resolve the battle): from the open battle menu, `resolve_decision` with the fight-to-the-
  end option (or `{"force":true}`); assert it closes with an `outcome` and, on a win, a `victor`/`defeated`
  (and possibly a chained "Loot the Fallen Foe" `PopupItemTrading` as the next `pendingDecision`). Confirm the
  battle no longer blocks `end_turn`. (Choosing flee/retreat instead is equally a PASS, as long as the battle
  resolves.) Else SKIP.
- M5 (opportunistic, army field battle): if any of your military units shows `inBattle:true` in `list_units`,
  assert `get_unit` on it has a `battle` block with `attackers`/`defenders`, `commandAdvantagePct`, and
  `advantageFavours`. Army battles auto-resolve and do NOT block `end_turn`. Else SKIP.
- M6 (strike first — the core new verb; **must be attempted**, and a crash here is a FAIL, never a SKIP or a
  retry loop — see the "unhandled crash" rule above): from the M0 setup, note the target hero's `task` via `get_unit`, then
  issue the exact call `get_unit`'s `hint` gave (`command_agent {"unitId":"U...","order":"attack","targetUnitId":"U..."}`).
  Assert the result carries a `pendingDecision` **inline** with `popupType:"PopupBattleAgent"` (no separate
  `get_pending_decision` needed) and reports `cancelledTargetTask`; then assert `get_unit` on the target shows
  `task: null` — the target's challenge/ritual is broken. Resolve the duel via `resolve_decision` (fight, or
  step then flee) and assert that the target's task is **still** null afterwards even if you fled or lost —
  that is the "even if you retreat, the ritual is ruined" mechanic. If your agent had >4 turns of its own
  challenge progress the first call returns a `force=true` prompt instead; retry with `"force":true`.
- M7 (errors): assert each returns a clean error (isError, no state change) — `order:"attack"` with a
  `targetUnitId` for a hero **not** on your agent's tile names the `move_unit` call to fix it;
  `order:"attack"` at one of your own agents points you at `order:"trade"`; `order:"rob"` at a target whose
  level is ≥ your agent's states both levels; `command_agent` on one of your **military** units says it is
  not an agent and points at `command_army`; `order:"nope"` is a clean "unknown order".
- M8 (opportunistic, trade between your agents): move two of your agents onto one tile; assert `get_unit` on
  each lists an `order:"trade"` entry for the other, then `command_agent {"order":"trade",...}` returns an
  inline `pendingDecision` with `popupType:"PopupItemTrading"` and both sides' items. Close it with
  `resolve_decision` (the "Done" option). Else SKIP.
- M9 (opportunistic, rob): if any agent's `orders` includes `order:"rob"` (a lower-level merchant/adventurer
  on its tile, no robbery in the last 5 turns), snapshot the agent's `combat.profile`/`combat.menace`, issue
  the call, and assert the result reports `profileGained`/`menaceGained`, that the two stats rose by those
  amounts, and that a `PopupItemTrading` decision is returned inline. A second `rob` in the same 5 turns must
  then fail with the cooldown error. Else SKIP.
- M10 (opportunistic, flee-as-soon-as-possible): in any open `PopupBattleAgent` you'd rather not fight,
  resolve the `fleeAsap` option (see M3). Assert the battle stops blocking `end_turn` and the result's
  `action` is one of: `retreated` (fled at round 3+), `fledLostMinions` (fled at round 2 — then assert
  `get_unit` shows the agent's `agent.minions` is now empty), or `fleeAsapEndedFirst` (the battle closed or
  was decided before fleeing unlocked — the result then reports the final state instead). A
  `fleeAsapStepCap` fallback must still report the live battle state, never hang. Else SKIP.

**N. Holy-order doctrine (`influence_holy_order_tenet`, `oppose_divinity`)** — if `list_holy_orders` is
empty, mark N1–N8 SKIP. Spending influence is irreversible; that is fine on this expendable game.
- N1 (tenet shape): `list_holy_orders {"orderId":"SG..."}` (use an order's id from the plain listing)
  returns exactly that one order, and every entry in `holyOrder.tenets` is an **object** with `name`,
  `type` (e.g. `H_Alignment`), numeric `status`/`min`/`max`, boolean `structural`, a `reads` label, a
  `desc`, and a `canInfluence` object with `toward_elder`/`toward_human` booleans. Assert on one entry.
- N2 (the Alignment gate is described, not just enforced): in that same payload find the `H_Alignment`
  tenet. For any **non-structural** tenet whose `status` is ≤ 0 and ≤ Alignment's `status`, assert
  `canInfluence.toward_elder` is `false` **and** a `blockedReason` mentioning Alignment is present. If no
  tenet meets that condition (Alignment already driven low), note it and PASS.
- N3 (bulk listing stays lean): plain `list_holy_orders` (no args) still returns tenet objects with
  `canInfluence`, but **without** `desc`. Assert absence of `desc` there and presence under N1.
- N4 (error, unknown order): `influence_holy_order_tenet {"orderId":"SG9999","tenet":"H_Alignment",
  "direction":"toward_elder"}` returns a clean "unknown social group id" error. Also call it with the id of
  a non-religious group (from `list_social_groups`) and assert a clean "is not a holy order" error.
- N5 (error, unknown tenet): with a real `orderId`, pass `"tenet":"H_NotAThing"`; assert a clean error that
  **lists that order's actual tenets** (they differ per order — some are removed at worldgen, some added
  mid-game).
- N6 (error, not enough influence): find an order with `canChangeTenet:false` and try to influence any of
  its tenets; assert a clean error reporting have/need (e.g. "12/40 Elder influence") and, when the order is
  gaining influence, an estimate in turns. If every order is ready, SKIP.
- N7 (live change — the core check): find an order with `canChangeTenet:true` (watch
  `game_overview.holyOrders.readyToInfluence`, which appears only while one exists; you may need to run
  several `end_turn`s, and it may never fire on a short run — SKIP with a note if so). Snapshot its
  `influenceElder` and its `H_Alignment` status, then call `influence_holy_order_tenet` with
  `{"tenet":"H_Alignment","direction":"toward_elder"}`. Assert: the result's `statusAfter` ==
  `statusBefore - 1`; a re-read of `list_holy_orders {orderId}` shows the tenet at the new status,
  `influenceElder` reset to `0` and `canChangeTenet:false`; and `game_overview.holyOrders` no longer lists
  that order (unless another is ready). If the result carried a `nowDarkenable` list, spot-check that one of
  those tenets now reports `canInfluence.toward_elder:true`.
- N8 (divinity): if any order has a `holyOrder.divinity` block (SKIP if divine entities are off in this
  game), assert it has numeric `strength`, `presencesCorrupted`/`presencesTotal` and booleans
  `canUndermine`/`canExile`. Then call `oppose_divinity {"orderId":"SG...","action":"exile"}` on an entity
  whose `canExile` is false and assert a clean error naming what is missing (strength and/or uncorrupted
  presences). Optionally, if `canUndermine` is true and you can spare 1 power, run `action:"undermine"` and
  assert `strength` dropped by 10 and `powerRemaining` fell by 1; the War-in-Heaven event it raises should
  come back as a `pendingDecision` (resolve it before continuing).

**O. Game lifecycle (`new_game`)** — O1 can run at any point while a game is loaded; O2/O3 ABANDON the
current game, so run them **LAST**, after every other section is done and its evidence recorded.
- O0 (preflight start): recorded during preflight if the run began at the main menu; otherwise SKIP with a
  note that a game was already loaded.
- O1 (error, refusal without confirm): while a game is in progress, call `new_game {"god":"snake"}` with NO
  `confirm`. Assert a clean `isError` that names the current turn, names `confirm`, and warns that progress
  would be lost — and that the running game is untouched (`game_overview.turn` unchanged).
- O2 (restart over a live game — destructive, run last): call
  `new_game {"god":"snake","mapSize":"small","seed":54321,"confirm":true}` and wait (ONE call, ~30-120s;
  even if it times out, do not retry — check `game_overview` instead). Assert `started:true`,
  `seed:54321`, `god.type:"God_Snake"`, `turn ≥ 1`, and that the payload carries a full `overview`
  (same shape as `game_overview`, per A1).
- O3 (post-start surface sanity): immediately after O2, assert the whole tool surface works on the new
  map — `game_overview` matches the returned overview's `turn`, `list_units {"scope":"mine"}` is
  non-empty (your starting agents), `list_powers` returns your god's powers, and old `U*`/`L*` ids from
  the abandoned game now return clean stale-id errors (session ids were reset).
- O4 (determinism, optional): a second `new_game {"god":"snake","mapSize":"small","seed":54321,
  "confirm":true}` reproduces the same world — compare a few location names from `list_locations`
  against O2's. SKIP if the time budget is spent.

### Reporting (required output)
1. Print a table with columns: `id | area | result (PASS/FAIL/SKIP/BLOCKED) | expected | observed (tool +
   before→after) | notes`. One row per check above.
2. Print a summary line: `PASS x / FAIL y / SKIP z / BLOCKED w` and the start→end turn numbers.
3. List every FAIL and BLOCKED with the exact tool call + response so a human can reproduce it.
4. **Also write the same report to a file** named `mcp-test-results-turn<startTurn>.md` in the working
   directory (use the starting turn number from preflight so the name is stable). Confirm the file path in
   your final message.

Work through A→N in order, then O last (it abandons the game). Be concise in intermediate narration; the value is in the evidence and the final
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
