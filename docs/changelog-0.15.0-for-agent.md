# ShadowsMCP 0.15.0 — what changed for you (the playing agent)

Nine fixes from the game-17 (Vinerva) playtest. The first two change how `end_turn` answers
decisions and how trade windows are laid out — read those first.

## Changed — `expectedDecisionId` now makes your answer LAND, not just guard it (G17-#7)

`end_turn` with `resolveOptionIndex` used to burn a round-trip whenever the decision you were
answering vanished between your read and the call (a force sweep consumed it, or it only appears
during turn processing): "no decision was pending - it was ignored", followed by a different
pending decision in the same response.

- Pass `expectedDecisionId` (echo `pendingDecision.decisionId`) with every
  `resolveOptionIndex`/`resolveOptionLabel`. It now does three things:
  1. **Answers before the sweeps**: when the pinned decision is the live blocker, your answer is
     applied *before* force's informational auto-dismiss and the routine-event sweep can consume it.
  2. **Retries after the tick**: if the pinned decision only (re)appears during turn processing,
     the same call lands your answer on it the moment the id matches — no extra round-trip.
  3. **Works mid-batch**: on `count > 1`, a pinned answer can now be consumed on any iteration,
     not just the first.
- The id match is what makes this safe: your answer can only ever land on the exact popup you
  read. Without `expectedDecisionId` the old behaviour (and the warning) remain — the warning now
  tells you to pass the id.

## Changed — reward trades: "Take all and close" is option 0 (G17-#4)

Challenge rewards and purchases (`Ch_HarvestSeed`, `Ch_BuyItem`, tome binding, loot) are delivered
by the game as a trade window with the earned item on **side B, the "Discard Items" world side** —
it is NOT in your inventory yet, and closing the window destroys it.

- On such windows, the **"Take all and close" composite is now option 0** (the real buttons shift
  to 1..count; "Swap top items and close" stays at count+1). Ordinary trades keep the old layout
  (composites after the real buttons).
- The `warning` now says outright that the items are not yet yours and points at option 0.
- The close guard is unchanged: Done/force with items still on the discard side is refused unless
  you pass `confirmDiscard:true`.

## Changed — combat: "Flee as soon as possible" is pinned at index 2 (G17-#3)

The flee-asap option was appended last, so its index shifted (2, 3 or 4) as flee/reorder options
came and went — a remembered "2 = flee" clicked a minion reorder mid-duel.

- The menu is now: 0 fight, 1 step, **2 flee-asap (stable)**; Flee/Retreat and minion-reorder
  options occupy 3+ and legitimately appear/disappear with the round — pick those by
  `optionLabel`, never a remembered index. The combat `note` says so.

## Fixed — power display can no longer read "costs 1, you have 1" (G17-#2)

The player's power balance was *rounded* for display (0.996 → "1") while castability compares the
raw value — so the numbers said a cast was legal when it was not.

- Every reported power balance (`game_overview.power`, `get_player_state.power`,
  `list_powers.power`, `use_power.remainingPower`, `oppose_divinity.powerRemaining`) is now
  **floored** at 2 decimals: whenever the displayed power ≥ a power's cost, the cast is legal.
- The insufficient-power refusal shows the floored balance plus the exact shortfall.

## New — `use_power` refusals name the failed clause (G17-#1, G17-#10)

"Fiez Citadel is not a valid target for 'Heart of the Forest'. Must be cast on land" — when the
actual failure was distance. The game's restriction strings are incomplete; the refusal now
decomposes the real `validTarget` rules, failed clauses first, like challenge `requirements`:

- Covered powers (Vinerva): Heart of the Forest, Wilderness Spirits, Manifestation, and the six
  Tempt groves. Example: `[X] within 4 steps of an existing Heart of the Forest (now nearest
  Heart is 7 step(s) away); [OK] target is land (...)`.
- `list_powers` additionally carries a **`restrictionNote`** where the game text is known-wrong:
  - Heart of the Forest: the distance/seed rule the game text omits ("Must be cast on land" is
    not the whole rule; the FIRST Heart has no distance limit).
  - Wilderness Spirits: "empty location" actually means *not owned by a human Society* — orc
    camps, ruins and Deep One sites ARE valid targets.
  - Manifestation: see below.
- Powers without an evaluator fall back to the game's restriction text, unchanged.

## New — Manifestation tells you who it kills (G17-#11)

Casting Vinerva's Manifestation destroys the settlement, killing its **entire population and its
ruler** — including an insane/enshadowed ruler who was scoring in your victory breakdown. Nothing
warned about this.

- `list_powers` marks it DESTRUCTIVE in `restrictionNote` (population + ruler die, no
  confirmation).
- The `use_power` result now carries **`notableDeaths`**: `{population, ruler:{name, shadow,
  insane, countedInVictoryColumn}, note}` — snapshotted before the cast, so you see exactly what
  the cast cost you (and whether a 5-point victory qualifier just died).

## New — per-Heart raze danger: `get_threats.hearts` (G17-#5)

Each Heart of the Forest has a menace the game only shows in hover text; societies quietly build
motivation to send an army and raze it. Three Hearts died unseen in game 17.

- `get_threats` (Vinerva, ≥1 Heart) now has `hearts`: per Heart its `location`, `menace`,
  `topSociety` (the society most inclined to raze it, with the game's own `motivationPct` and raw
  `utility`) and `willRazeNow` (utility > 0 = an army is being dispatched). The `note` explains
  the formula: +menace, −35 base reluctance, −distance, −150 at war, −menace × rulerShadow — an
  enshadowed sovereign is blind to Heart menace.
- `game_overview.threats` carries `hearts` / `heartsAtRisk` counts and a `heartAlert` headline
  for the worst Heart.
- `get_location` shows a Heart subsettlement's `menace` even at 0 (other subsettlements: when
  non-zero); `world_summary` sub entries now carry `menace` too.

## Fixed — `isHuntable` no longer reads as "safe from heroes" (G17-#6)

The profile≥50 & menace>25 gate is real, but it governs **only ruler-ordered escorted hunts**.
Independent heroes need no gate: any hero that SEES your agent (within profile/10 hexes) attacks
on menace alone — worthwhile for it from roughly menace > 30 + distance, at any profile. An
`isHuntable:false` agent with menace 43 died to an adjacent Mediator in game 17.

- `get_unit.combat` now carries a `huntNote` stating the split; tips (`menace`, `profile`,
  `agent_exposed`, `challenges`), `game_overview`'s breadcrumb and the docs are reworded.
- `get_threats.agentSafety.topHunter.motivationPct` was already the correct signal — trust it
  over `isHuntable`.

## Fixed — `oppose_divinity` says when it can never work (G17 running note)

With the map option `opt_divineEntities` off (the default — 17 games running), no holy order has
a divine entity. The tool now refuses with a game-wide "DISABLED in this game … drop this line of
play" error instead of a per-order "has no divine entity", and its description says so up front.
