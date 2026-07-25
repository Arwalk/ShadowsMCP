# ShadowsMCP 0.8.0 — what changed for you (the playing agent)

Driven by the 530-turn Iastur playtest (game 7). Theme: fewer tokens per call, fewer calls per
intent — repeated boilerplate is compressed, the two most common multi-call sequences (fleeing a
duel, finishing a trade) became single verbs, and a batch `end_turn` can no longer swallow your
resolve acknowledgement.

## Fixed — batch `end_turn` always acknowledges your resolve

- Passing `resolveOptionIndex` with `count > 1` used to silently drop the `resolved` /
  `resolveWarning` ack whenever the FIRST turn of the batch did not advance (blocked, game over,
  error). Now the ack from turn 1 is captured before anything else and attached to every outcome:
  a stopped batch (`advancedBy: 0`) still carries `resolved` or `resolveWarning`, and an Error
  result appends the resolveWarning text to its message. The "a provided resolve is never
  silently ignored" contract now holds on every path.

## New — combat option `fleeAsap` ("flee as soon as possible")

- The battle menu (`PopupBattleAgent`) has a new option appended AFTER the existing ones (their
  indices are unchanged): **flee as soon as possible**. It steps the battle for you and retreats
  at the first legal moment, in ONE call — previously fleeing took 4–7 manual step/re-read calls,
  and agents died in hopeless duels because of it.
- The result's `action` tells you what happened: `retreated` (fled at round 3+, safe),
  `fledLostMinions` (fled at round 2 — escape costs ALL your minions; the label warns you
  beforehand), or `fleeAsapEndedFirst` (the battle was decided before fleeing unlocked; the final
  state is reported). Available from round 1 while your side is alive and the battle undecided.

## New — one-call trade composites (`itemTrading`)

- The trade window's `options` now end with synthetic composite verbs (flagged
  `composite: true`, at the indices right after the real buttons): **"Take all and close"** and
  **"Swap top items and close"**. Each performs the exchange and then Done in one call — the
  sequence nearly every trade ended in anyway — and reports `steps`, the usual movement diff
  (`itemsMovedToA`/`goldDeltaA`/…), and `closed`.
- Safety: if your side's inventory cannot fit everything, "Take all and close" does NOT close —
  you get `closed: false`, the leftover `warning`, and a `note` telling you how to take the rest.
  A follow-up popup chained after the close is surfaced via `nextDecision`, never auto-clicked.

## Leaner — boilerplate is shown once, then compressed

- The fixed instruction strings that used to repeat on every call — decision `note` /
  `resolveWith` texts (trades, idle agents, combat, events), the `resolveHint` on
  banner-carrying tools, `list_units`' `ordersLegend` — are now emitted in FULL the first time,
  then replaced by a one-line brief that still states the exact call shape. Nothing you need in
  order to act is dropped; the full text re-appears every ~10th repeat as a safety net, and an
  MCP reconnect (`initialize`) resets everything to full — so a fresh-context client always gets
  complete instructions.
- Recurring narrative events are compacted: when a `kind:"event"` decision repeats a TITLE you
  have already seen this session, its static prose is truncated to the dynamic tail plus
  "(recurring event; full text shown earlier)". Options, labels and `enabled` flags are always
  complete; a new title always gets the full description.

## Fixed — one-shot tips no longer re-fire mid-game

- Contextual tips (and the shown-once boilerplate above) are now keyed to the GAME (the map
  seed), not the Map object: a save/load of the same game no longer resets them, so tips you have
  already read stop re-appearing after every reload. A `new_game` (different seed) starts fresh.

## New — four contextual tips (they fire once, when the trap becomes real)

- `insane_heroes_hunt` (tactics): madness is not pacification — insane heroes keep hunting your
  agents; treat them like sane hunters.
- `shadow_treadmill` (infiltration): fires when heroes drive back your shadow 3+ times in 20
  turns — break the cleansing loop (kill/divert the cleansers, enshadow rulers, spread
  elsewhere) instead of re-painting the same tiles.
- `alliance_razing` (faction): the Alliance razes enshadowed settlements and executes insane
  rulers — score qualifiers you banked die with them; check `get_victory_breakdown.details`.
- `iastur_soul` (god, Iastur only): once your awakening lays Iastur's Soul bare at the Elder
  Tomb, the game is decided by that one meter — 300% wins outright, 0% loses regardless of
  points; each Waves of Madness performance also adds +40 menace/+40 profile to the performer,
  so escort them.
