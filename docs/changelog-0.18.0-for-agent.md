# ShadowsMCP 0.18.0 — what changed for you (the playing agent)

The game itself shipped an update (Aug 2025) and this release is the compatibility pass for it,
plus one new signal. The game update was additive — no tool, field or id you rely on was removed
or renamed — so nothing you learned breaks. Read the two "game changed" items: they change what
the world does, not what the mod does.

## New — `riskOfAttack` on social groups

The game's new side panel shows a "Risk of Attack" percentage; you now get the same signal:

- `list_social_groups` and `get_social_group` items can carry `riskOfAttack` (0..1 — multiply by
  100 for the in-game %). It is the highest motivation any nation currently has to declare war
  on that group. At ~1.0 an attack is very likely coming soon.
- The field appears ONLY on groups the game tracks a threat for: orc camps you have infiltrated
  (any location) or watched, Deep Ones, and a Dark Empire. It is omitted when zero — absence
  means "no tracked threat", not "safe".
- Use it to protect investments: a high `riskOfAttack` on an orc horde or Dark Empire you built
  up means nations are about to move on it. `list_threats` already told you WHO is most likely
  to attack ("Society most likely to attack X is Y. Motivation is N%"); `riskOfAttack` is that
  same number, queryable per group.
- `military.current`/`military.max` on the same items are now recomputed at read time (they used
  to be up to a turn stale mid-turn).

## Game changed — alliance bookkeeping fixed

Two vanilla bugs are fixed in the game update: joining the Dark Empire no longer mislabels the
absorbed nation as "the Alliance", and defunct (absorbed) societies no longer count in
alliance/dark-empire checks. If a previous playtest showed a phantom Alliance or a war aimed at
a dead nation, that was this — don't compensate for it anymore. The mod's `alliance_razing` and
`dark_empire` tips now use the same fixed logic.

## Game changed — the magical arms race is capped

"Learn Arcane Secret" motivation for enemy mages is now capped, so mages no longer get
stunlocked studying secrets during an arms race. Expect mages to keep patrolling and fighting
even after you go loud with magic. The `magic` tip mentions this.

## Also in the game update (no mod change, just so your model of the world is current)

- Civil-war messages now arrive in cause→effect order in `get_recent_events` (the "descends into
  civil war" message precedes the war declarations it triggers).
- The battle screen names armies' societies; threat tracking now covers more groups (Deep Ones,
  Dark Empire, partially infiltrated orc camps) — so `list_threats` can show more "most likely to
  attack" lines than before.

Everything else — every tool, id scheme, decision flow and tip from 0.17.0 — is unchanged.
