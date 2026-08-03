# ShadowsMCP 0.16.0 — what changed for you (the playing agent)

Seven fixes from the game-18 (Ophanim) playtest. The first one changes the default behaviour of
`end_turn(force)` — read it first.

## Changed — force no longer auto-spends REGULAR level-ups (G18-#4, fourth report)

`end_turn {"force":true}` used to let the game AI-spend one banked regular skill point per agent
per turn ("Bonus: Intrigue (1)" chosen for you), and because force is effectively mandatory for
batching, there was no practical way to keep force AND pick your own traits.

- **New default**: ANY pending level-up (starting-trait or regular) blocks force. The level-up
  popup is returned as `pendingDecision` with `forceDenied:"traitPick"` (regular) or
  `forceDenied:"startingTraitPick"` (the one-shot magic-mastery menu) — answer it with
  `resolveOptionIndex`, then force works again.
- **Opt back in** for unattended batches with `forceSpendsRegularTraits:true`: force then
  auto-spends one regular point per agent per turn on an AI-chosen trait, each named in
  `digest.autoResolvedLevelUps`. (To auto-spend a starting-trait pick you need BOTH
  `forceSpendsStartingTraits:true` and `forceSpendsRegularTraits:true`.)
- **See it coming**: `game_overview.levelUpsPending` names agents holding an unspent regular
  point (sibling of the existing `masteryPicksPending`), so you can level them before a batch.
- Cost to know: the game pops ONE agent's menu per end_turn call, so N simultaneous level-ups
  cost N round-trips. That is the price of choosing; pass the flag when you'd rather not pay it.

## Fixed — the Lay Low note no longer sends you to empty hexes (G18-#1, cost an agent)

The old `locationNote` claimed the wilderness variant "works from any wilderness hex". False on
both counts, verified against game code:

- 'Lay Low (Wilderness)' exists ONLY at non-city sites that still have a settlement object:
  **orc camps, ancient ruins/minor sites, witch covens and temples, deep-one cities/sanctums**.
  A truly empty hex offers NO challenges at all — never send an agent there to hide.
- A city never "stops offering" Lay Low. What actually happens: an army AT REST deals **1 HP per
  turn** to an agent at menace ≥ 40 and profile ≥ 30 performing ANY challenge there — waived at
  100% infiltration or if the army's home city is >50% shadow.
- The `Ch_LayLow`/`Ch_LayLowWilderness` locationNotes and the `menace` tip now state all of the
  above, including where to run when an at-rest army makes a city lethal (G18-#7).

## New — Start Faith tells you when the seed cannot live (G18-#2, Ophanim)

`use_power` on Start Faith reported bare success while the 1% seed was silently deleted two turns
later: ruler awareness drains Faith at **5%/turn per point of awareness**, there is no base
growth, and the game emits no message when a modifier dies.

- Every successful Start Faith cast now returns **`faithOutlook`**: the seed's projected per-turn
  balance from named terms (Ruler Awareness, Fear of Our Shadow, Neighbouring Faith, Doubters, …)
  and, whenever the balance is ≤ 0, an explicit WARNING that the Faith will be silently deleted —
  lower the ruler's awareness or raise shadow before reseeding.
- `list_powers` carries a `restrictionNote` on Start Faith stating the drain up front.

## New — Start Faith refusals name the failed clause (G18-#3, Ophanim)

"Dwarven Tyiu is not a valid target: Must be cast on a human settlement…" — when dwarven
settlements DO qualify (they are the human settlement type in code). The real rules are now
decomposed like the Vinerva powers:

- `[X]`/`[OK]` clauses, failed first: (1) human-TYPE settlement — dwarven and elven cities
  qualify, the clause says so; (2) infiltration > 0% with the actual `n/m infiltrable districts`
  fraction (a single-district site is binary 0% or 100%); (3) **no existing Ophanim Faith here**
  — a clause the game's restriction text never mentioned; the refusal names the present Faith's
  charge (a 1%-seed you forgot about counts).

## New — Ophanim's missing awareness rule is documented (G18-#6)

The single most important Ophanim term — ruler awareness suppresses Faith — appeared nowhere in
the god's mechanics text or tips.

- `get_player_state.progression` now carries a `mechanicsNote` (vanilla text annotated, not
  rewritten): awareness drains Faith 5%/turn per point, a fully-aware ruler outpaces every growth
  source combined, Faith at 0% is silently deleted — for Ophanim, awareness is the primary brake
  on your scoring engine, not a late-game threat.
- The `ophanim_faith` tip is rewritten with the exact growth model verified against code: the
  fear terms are a single tier (own shadow +4 ELSE nearby-settlement shadow +2 ELSE world shadow
  +1 — they do NOT add up), +1 for a neighbouring Faith, and the awareness drain. It also notes
  that Faith above 100% charge can spread by itself to neighbours whose ruler is <50% aware.

## New — duplicated location modifiers are annotated, not merged (G18-#5)

Two `Pr_LingeringResentment` entries at one location (or four at charge 0) are REAL game state,
not a serialization bug: the engine stacks them, each instance keeps its own charge and applies
its own +2 Unrest per turn (the doubled influence lines are the true arithmetic — two stacks =
+4/turn), and the vanilla UI shows the same duplicates.

- When a location holds >1 modifier of one type, each entry now carries `stackCount` and the
  first carries a `stackNote`: instances are independent, their per-turn effects ADD UP, and an
  instance at charge 0 was just zeroed (e.g. by an Unrest crisis) and is culled next turn.
- The per-instance list is deliberately NOT merged — the separate charges tell you when each
  stack expires.

## Unchanged but noted

- The ARMY_BLOCKS message the game shows when an army intercepts an agent is vanilla text passed
  through verbatim; the mod's pointers to the wilderness escape live in the Lay Low notes and the
  `menace` tip instead (G18-#7).
- The signed, named `influences` breakdowns praised in G18-#8 are unchanged; extending the same
  attribution to shadow changes and plague immunity is noted as future work (the game does not
  track those as reason lists, so it needs mod-side bookkeeping).
