# ShadowsMCP 0.17.0 — what changed for you (the playing agent)

Five fixes from the game-19 (Ophanim) playtest. The first two change default behaviour — read
them first. Your three positives (G19-#1 faithOutlook, G19-#6 discard-side trade warning,
G19-#7 stale-id diagnostics) are acknowledged at the bottom.

## Changed — bare resolves are no longer wasted (G19-#8, repeat of G15-#3)

`end_turn {"resolveOptionIndex":N}` without `expectedDecisionId` used to lose the answer whenever
a sweep consumed the popup first or the decision only appeared during processing.

- **Auto-pin (new default)**: a bare resolve sent while a decision IS pending at call start is
  automatically pinned to that decision — the one you just read — so force's informational sweep
  and the routine-event sweep can never consume it out from under your answer. The result reports
  `resolved.autoPinnedTo` with the pinned id. Passing `expectedDecisionId` yourself still works
  and is still right when you hold an id from an earlier read.
- **New opt-in `resolveAppliesToNextDecision:true`**: with NO decision pending at call start, the
  answer is applied to the FIRST decision that surfaces during this call (during turn processing,
  or on a later turn of the batch). The response names exactly what received the answer
  (`resolved.appliedTo: {decisionId, kind, title}`); if nothing surfaces, `resolveWarning` says
  the answer went unused. Off by default because it answers a popup you have not read — option
  order differs per popup, so prefer `resolveOptionLabel` with this flag.
- **Tightened**: a bare resolve can no longer blind-click a popup that surfaced from the game's
  delayed queue during the call (one you have never seen); it now warns and returns the decision
  as `pendingDecision` instead — use the flag if you want unseen decisions answered.

## New — Sap Life Force is what ate your cities (G19-#2 verdict)

Verified against game code: Ophanim's Faith does NOT consume population — `Pr_Opha_Faith` never
touches it, which is why no Faith modifier ever showed a drain line. The cities died to the
`H_SapLifeforce` ("Sap Life Force") holy tenet YOU darkened: while your power is below max,
exactly ONE of your temple-cities per turn (whichever temple ticks first — you cannot choose it)
loses 2 population per darkened level, and a city dropping below 2 population is DESTROYED
outright ("Devoured by Ophanim") — temple, Faith, and ruler included. The game writes NO message
you can see for either the drain or the destruction, and the payoff is a FLAT +0.02 power/turn
per level (the tenet text's "2%" is added as an absolute amount, not a percentage). Now surfaced
everywhere the decision passes:

- The `H_SapLifeforce` tenet carries a `warning` field in EVERY serialization (bulk
  `list_holy_orders` included), stating the full mechanics before you buy.
- Your own order's tenets (the one with `worshipsThePlayer:true`) now always include `desc` and
  the ready-to-paste call, even in the bulk listing.
- Darkening it returns `tenetWarning` plus a `sapDrain` block naming every exposed temple-city
  with `{population, hitsToRuin}` in the same response.
- While it is darkened, `game_overview.ophanimSapDrain` always lists the exposed cities;
  `end_turn` attaches the same block whenever any city is within 3 hits of destruction, and the
  digest carries a synthesized `DEVOURED_BY_OPHANIM` event when one falls.
- `get_player_state.powerPerTurn` (new, all gods) shows your live regen including the bonus — the
  benefit side of the trade is no longer invisible.
- New tip `ophanim_tenets` covers all three of your exclusive tenets, including that Inquisitors'
  "cost of decreasing population" actually lives in the Inquisition challenge, and Paranoid
  Society's 15% temple prosperity hit.

## New — challenge complexity is live, and now says so (G19-#3)

Confirmed exactly as you reported: Infiltrate-family complexity is recomputed every read as
base + per-point × the settlement's LIVE security, your banked progress counts against the live
value, and Bribed Guards (-2 security) is culled silently at charge 0. The game locks nothing;
the mod now shows everything:

- `get_location` settlements carry `security` and `securityInfluences` — the number that drives
  Infiltrate (50 + 25/pt), Access Vault (20 + 8/pt), their simplified/limited variants, and both
  Assassinates (+5/pt).
- Security-scaled challenge entries carry a `complexityNote` quoting the formula, the current
  security, and — when Bribed Guards is active there — how many turns until it lapses and how
  much the complexity will rise.
- `perform_challenge` success snapshots `complexity`, `progressPerTurn` and `etaTurns` at commit
  time, so the quoted target is on record.
- `get_unit.taskDetail` on a running challenge now shows `complexity` and `etaTurnsRemaining`
  next to `progress` — no more cross-referencing two tools to learn you are at 16/100.
- When a running challenge's complexity moves ≥1 between turns, the `end_turn` digest carries a
  `CHALLENGE_COMPLEXITY_CHANGED` event quoting old → new (informational; it never stops a batch).
- A `Pr_BribedGuards` property entry carries `turnsRemaining` and a `lapseNote`.

## Fixed — the Lay Low site list, second pass (G19-#4, repeat of G18-#1)

Your hypothesis was nearly right. The 0.16.0 note's "temples" and "minor sites" were wrong,
introduced by the previous fix. Verified constructor-by-constructor: 'Lay Low (Wilderness)'
exists ONLY at orc camps, ancient-ruins sites, witch covens, temples of the WITCH faith
(HolyOrder_Witches adds it at temple creation — no other faith's temples have it, and Holy
Sites never do), and deep-one cities/sanctums. Every ruin-type minor site has one (they always
contain Ancient Ruins — the gate you inferred); human minor VILLAGES never do (their only
districts are farms/forts/Holy Sites — your Abbey of Devetes case). Also newly stated: elven
and dwarven cities are not `Set_City` in code and offer NEITHER Lay Low variant. Both
locationNotes and the `menace` tip now carry the same corrected list.

## Fixed — your own acolyte is no longer a "hostile" (G19-#5)

`get_threats.agentSafety[].hostilesOnTile` filtered purely on "not commandable", which caught
acolytes of your own holy order (Call to Serve creates autonomous units). Units whose order
worships you are now excluded from `hostilesOnTile` and from the strike-first tip. The
`orders[].attack` / `drive_back` entries the game genuinely offers against them are kept but
tagged `ownOrder:true` with an `ownOrderNote` — attacking your own acolyte only hurts your
religion. The `topHunter` scan is unchanged (it mirrors the game's own threat model, where
attack utility already rules friendly acolytes out).

## Acknowledged (no action needed)

- G19-#1: `faithOutlook` stating the growth model, not just the result, is the pattern we will
  extend to more powers in future versions.
- G19-#6: the discard-side trade warning naming the at-risk item (G17-#4 fix) confirmed working.
- G19-#7: stale-challenge-id errors enumerating the live challenge list confirmed working.
