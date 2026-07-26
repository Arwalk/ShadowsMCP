# ShadowsMCP 0.12.0 — what changed for you (the playing agent)

Driven by your game-14 feedback (both sessions, turns 1-686). Every claim was verified against the
decompiled game before acting; the last section records the ground-truth findings so future reports
can be cross-checked.

## CORRECTED — the Iastur endgame guidance was wrong; the Soul meter is loss-only [#12, #17]

The single most consequential fix. Verified against game code: **nothing in the game ever RAISES
`Pr_Iastur`'s charge.** The "reach 300% and you WIN" text in the awakening message is dead vanilla
text; the meter only FALLS (a hero using the bound Tome at the Tomb drains it, your banked power
absorbing the hit first), and 0% is a real defeat. `Ch_WavesOfMadness.complete()` never touches the
charge — it drives the nearest portion of rulers/heroes insane, and each insane ruler/hero scores
standard **victory points** (about double when also enshadowed). That is the actual win route.

- The seal-9 `iastur_soul` tip is rewritten around the truth: defend the Soul (it is a loss meter),
  win through the points meter, and do NOT abandon points-scoring — waves are a points accelerator.
- `get_player_state.progression` now carries a `mechanicsNote` on the Laughing King correcting the
  vanilla mechanics blurb (which is still shown verbatim — the mod annotates, never rewrites).
- Every listed `Ch_WavesOfMadness` entry carries an `outcomeNote` stating where its payoff appears
  (victory points) and where it does not (the Soul modifier).

## Fixed — batching actually batches [#7, #20, #21, #16]

- **`stopOnThreatMotivation` now GOVERNS the threat stop.** Setting it (>0) replaces the default
  new-hunter/worse-odds triggers entirely: the batch halts for threats only when a hunter's
  motivation is at or above your percentage. The game-14 run where an explicit `:300` was overridden
  by stops at 38-180% can no longer happen. Omit the parameter to keep the old default (stop on any
  meaningful danger change).
- **`passIdleAgents:true` now covers agents that go idle MID-batch** (e.g. a challenge completes on
  turn 3 of 10): if the idle alert still manages to block, the batch passes the stragglers through
  the alert itself and retries the turn instead of halting.
- **`count` clamping is explicit.** `requestedCount` echoes the value YOU passed (20 stays 20), and
  a `countNote` states the clamp to the max batch of 10 when it applies.

## Fixed — force level-ups are named in the digest [#5]

`end_turn(force)` still auto-spends one banked skill point per agent per turn (the game's own force
path), but each spend is now reported in `digest.autoResolvedLevelUps` as
`{turn, unit, chose, level, skillPointsRemaining, note}` — the AI-picked trait is named. Remember:
the FIRST level-up is your only shot at a magic mastery (Geomancy/Death/Blood); if that matters,
end_turn without force and answer the level-up popup yourself.

## New — agent progression is visible in the curated tools [#6]

- `get_unit.agent` now carries `level`, `xp`, `xpForNextLevel`, `skillPoints` (plus a
  `skillPointNote` warning about the force auto-spend whenever a point is banked).
- Your own agents' `list_units` / `get_player_state.agents` rows carry `level`, and `skillPoints`
  whenever any are banked. No more `inspect {"path":"U2.person"}` round-trips.

## Fixed — minion stats were wrong data [#2]

`get_unit.agent.minions[]` reported `defence:0` (an uninitialised battle-eroded scratch field) and
omitted attack entirely. Entries are now `{name, hp, maxHp, attack, defence, commandCost, isDead}`
with attack/defence from the same getters every popup uses (a Sellsword reads 2/2).

## Fixed — challenge enumeration is one consistent set [#3, #14, #15, #24, #10]

- The stale-id error now enumerates the SAME set as `list_challenges` for that unit at that
  location: hero-only entries are excluded (with an explicit "(N heroes-only challenge(s) are not
  listed…)" note instead of silently eating the cap), item-granted rituals are included, duplicates
  collapse, the cap is doubled, and anything still dropped is counted ("… plus N more - run
  list_challenges").
- `list_challenges` gains a `heroOnly` block (`count`, `names`, note) naming enemy-side challenges
  present at the location that your agents can never perform — content is flagged, not invisible
  (this is where `Ch_ReforgeTheSeals` / `Ch_FulfillTheProphecy` at the Elder Tomb live).
- When a stale id's challenge TYPE has vanished from the location entirely (Learn Secret after the
  Arcane Secret was destroyed; Enshadow at 100% shadow), the error now says the offer itself has
  lapsed and names the typical causes, instead of a bare "unknown or stale".

## Fixed — the Summon Tome discard trap [#9]

The summon's chained "Discard Items" trading popup can no longer silently lose the Tome:

- The decision carries a `warning` naming what is on the discard side (the LAUGHING TOME called out
  specially: dismissing releases it to fall asleep at a random location, undoing the summon).
- Closing the window (Done, or `resolve_decision force`) while the discard side still holds items is
  REFUSED with instructions. Take the items ('Take all and close'), or pass `confirmDiscard:true`
  (new parameter on `resolve_decision` and `end_turn`) to discard deliberately.

## Fixed — capped gold trades tell you about the cap [#1]

Trades opened by limited access (Subtle Thievery's `Ch_AccessVaultLimited`) now carry
`goldTransferLimit` (`maxGoldToA`, `goldAlreadyMovedToA`, `goldRemainingToA`, note): "Move ALL gold"
moves at most the remaining cap — that is the game's real rule (`ch_accessVaultMinorLimit`), now
stated instead of discovered. A gold-move click that moves nothing (cap used up) returns a
`warning` instead of a bare success.

## New — resolve decisions by label [#13]

`resolve_decision` (and `end_turn` as `resolveOptionLabel`) accepts `optionLabel`: exact
case-insensitive match, else a unique substring; ambiguity or no match refuses cleanly and lists
the real labels. Use it on the tag-pick carousel, whose indices legitimately shift between casts as
taken tags drop out — the carousel `note` now warns about exactly that.

## Fixed — "Repeat this challenge" is honest [#22]

The challengeComplete popup's repeat option used to read a Unity button whose state could be stale
when no frame had run; `enabled` is now computed from the game's own live predicate (challenge
valid, unclaimed, still offered, unit idle and alive). `enabled:true` means the repeat will take.

## New — event decisions carry the stakes and report silent outcomes [#23, #8]

- `kind:"event"` decisions with a located actor now include `locationModifiers` — the current
  charges the option deltas apply to, with Devastation's destruction cap (300) flagged. "Increases
  Devastation by 100" is now checkable against the bar it fills.
- When a resolved event applies its effects with no outcome text, the result now includes
  `observedChanges`: a before/after diff of the actor's menace/profile/gold and the location's
  modifier charges — "+50% shadow" and "-5 menace" arrive in the same call instead of costing a
  confirming query. (Effects on other people/places can still act silently; the fallback advice
  remains.)

## Fixed — smaller papercuts [#18, #19, #15]

- `influence_holy_order_tenet` accepts `tenetType` as a full alias of `tenet` (the tenet objects
  label the field `type`, so that guess now just works).
- Well of Shadows' restriction is annotated with the missing precondition: it only EXISTS at
  populated human settlements — ruins and wilderness never offer it, whatever their shadow.
- The Lay Low `locationNote` now points to the wilderness variant when a city stops offering it
  (e.g. an army at rest patrolling there).

## Ground truth notes (verified against game source; not mod defects)

- **The Soul meter really is inert upward** (#12/#17): only `Ch_BindIastur` (hero-side) modifies
  `Pr_Iastur.charge`, always downward; `charge <= 0` calls `overmind.defeat(...)`. There is no
  win-at-300 code path. The Laughing King wins via `Overmind.computeVictoryProgress`, where each
  insane ruler/hero scores `victory_insane` points (`victory_insaneAndShadow` when enshadowed).
- **The 35-gold cap is real game behaviour** (#1): `map.param.ch_accessVaultMinorLimit` caps the
  WINDOW's total transfer (`PopupItemTrading.maxTradeA`), so a second "Move ALL" click has nothing
  left to move. The mod now reports the cap; the cap itself is by design.
- **The tome loss on dismissal** (#9) is the vanilla trading flow (`ItemToWorldExchange`): items
  left on the discard side are released when the window closes. The mod now guards the close.
- **`stopOnThreatMotivation` really did gate nothing for the danger triggers** (#20): the
  meaningful-danger stop and the motivation tripwire were independent conditions; confirmed and
  redesigned as above.
