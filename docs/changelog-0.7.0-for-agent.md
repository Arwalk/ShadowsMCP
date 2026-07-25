# ShadowsMCP 0.7.0 — what changed for you (the playing agent)

Driven by your game-6 feedback (the first AI victory). Theme: making state you previously had
to infer — id resets, popup identity, who is scoring, how fast a challenge really runs —
directly observable.

## New — `game_overview.idEpoch` (unit-id reset detector)

- Unit ids (`U*`) are minted per session and **reset whenever a save is loaded or a new game
  starts** — the same unit can come back as a different U-number. `idEpoch` increments on every
  such reset: if it differs from the last value you saw, discard every cached U-id and re-run
  `list_units`. `L*`/`P*`/`SG*` ids ride the game's own persisted index and survive loads.
- Every "unknown or stale unit id" error now states the current idEpoch and this rule, so a
  stale cached id fails loudly with its cause instead of silently commanding the wrong unit.
- Caveat on P-ids: the game *recycles* a dead person's index, so a dead person's P-id may later
  resolve to a different living person. Treat the P-id of anyone dead as expired.

## New — `decisionId` / `expectedDecisionId` (chained-popup safety)

- Every pending decision (get_pending_decision, game_overview.pendingDecision, end_turn's
  pendingDecision) now carries a **`decisionId`** token for that exact popup instance.
- Pass it back as **`expectedDecisionId`** on `resolve_decision` (or on `end_turn` alongside
  `resolveOptionIndex`). If the pending decision changed between your read and your click — a
  follow-up popup promoted from the queue, a battle opening — **nothing is clicked** and the
  error describes what is actually open, with its options. On end_turn the refusal surfaces via
  `resolveWarning`.
- A chained follow-up popup reuses option indices but always gets a fresh decisionId. This is
  the fix for "optionIndex 0 was Dismiss, resolved as Replay": chain with expectedDecisionId and
  blind clicks become impossible. Omitting the param keeps the old behavior.

## New — `get_victory_breakdown.details` (score attribution)

- The opaque per-head columns are now attributed. `details.insaneAndShadowRulersAndHeroes` and
  `details.insaneOnlyRulersAndHeroes` list exactly which persons qualify (`{id, name, role,
  location, shadow}` + pointsEach): when the count regresses, diff the lists to see who died,
  was cured, dropped below shadow 0.5, lost their seat, was corrupted into your own agent, or
  whose city joined the Dark Empire (it then scores in the Dark Empire column instead).
- `details.deepOneCities` lists the Abyssal Cities that actually score (with population) and a
  `sanctumCount`. **A Sanctum never scores directly** — it periodically pushes population into a
  nearby abyssal city (or founds one once large enough). The game also hides the Deep One
  breakdown line until its points exceed 0 and truncates its printed points to an integer, so
  early Deep One investment accrues invisibly in the game's own string; `details` shows it.
- Sections appear only when non-empty; qualifier lists cap at 25 with `truncated: true`.

## New — `etaTurns` + `progressBreakdown` in `list_challenges`

- **`progressPerTurn` is unit-relative** (stat-scaled): the same "complexity 40" challenge can
  run at 7/turn for one agent and 1/turn for another. New **`etaTurns`** =
  ceil(complexity / progressPerTurn), the turns of active work at this unit's rate (travel
  excluded); absent when the rate is 0 or the challenge is indefinite. Use it to compare
  challenges and units instead of raw complexity.
- **`progressBreakdown`** itemizes the rate (`{reason, value}`: base stat, traits, items,
  location boosts). Dropped with `terse:true`. Armies (UM) now get progressPerTurn /
  complexity / etaTurns too.

## Clarified — Lay Low, and valid vs validForUnit

- `Ch_LayLow` entries now carry a **`locationNote`**: Lay Low is not city-only, but its speed is
  location-dependent (base reduction added again for settlement infiltration >= 50%, location
  shadow >= 50%, and — Ophanim god only — 100% Ophanim faith here). `progressPerTurn` is the
  actual per-turn menace+profile reduction at this location; an infiltrated or enshadowed
  settlement can be 2-4x faster. A wilderness variant exists with its own note.
- `valid` / `validForUnit` were never transposed; the semantics were under-documented. `valid` is
  the challenge's *world* precondition (usually true even when you cannot act). **Location
  preconditions — infiltration %, shadow %, ward, settlement type — are checked inside
  `validForUnit`**, because the unit carries its location. So `validForUnit: false` usually
  means "not here / not yet", NOT "wrong unit type"; `restriction` states the actual
  requirement.

## Already fixed (your data was from an old DLL)

- "Listed menaceGain/profileGain are ceilings, not costs": fixed in **0.5.1**. Your advertised
  numbers (Study Death 125, Skeletal Servitor 20, Well of Shadows 39.87) match the pre-0.5.1
  source (the engine AI's utility values); since 0.5.1 `menaceGain`/`profileGain` are the values
  actually applied on completion (Well of Shadows: menace 10 / profile 3 — exactly the actuals
  you measured). If you see fractional profileGain again, the harness is loading a stale DLL.
- "get_person.shadow and inspect disagree (0.64 vs 0)": both read the same `Person.shadow`. A
  settlement has its own unrelated `shadow` field (the settlement's enshadowment) — an inspect
  path through the location/settlement returns that one. When comparing, inspect the person id
  (`P*.shadow`), not the settlement.
