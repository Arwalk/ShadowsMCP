# ShadowsMCP 0.14.0 — what changed for you (the playing agent)

Five fixes from the game-16 playtest. Two change `end_turn` batching behaviour — read those first.

## Changed — `end_turn(force)` no longer auto-spends the FIRST level-up (G16-#1)

An agent's first level-up is its one-shot **starting-trait (magic mastery) menu**; force used to
let the game AI-pick it silently, forfeiting the mastery forever (three games running).

- Now: when any of your agents' next pick is a starting-trait menu, `end_turn {force:true}`
  **blocks** instead of spending it — the level-up popup is returned as `pendingDecision`, with
  `forceDenied:"startingTraitPick"` and a note naming the agent. Answer the popup
  (`resolveOptionIndex`) and force works again.
- Regular (non-first) level-ups keep auto-spending under force, each named in
  `digest.autoResolvedLevelUps` as before.
- `game_overview.masteryPicksPending` now warns you *before* the block: it lists agents whose
  next level-up is the one-shot menu (absent when none).
- Opt-out: pass `forceSpendsStartingTraits:true` to restore the old auto-spend, deliberately.
- Collateral (engine limitation — bEndTurn's force is all-or-nothing): while a starting pick is
  pending, other agents' regular points also pause auto-spending for that call; answer the
  mastery popup and the next forced call resumes them.

## New — a hero starting a hunt stops a batch: `stopReason:"heroAttacking"` (G16-#4 / G15-#1)

`stopOnThreatMotivation` never reacted to `HERO_ATTACKING` — a batch could run straight through
the only window to respond to a hero committing to attack you (repositioning, Lay Low, bodyguard,
or a power that targets attacking heroes, e.g. Iastur's).

- A batch now stops the turn a hero **starts** an attack-pursuit
  (`Task_AttackUnit`) against any of your agents or servants: `stopReason:"heroAttacking"`, and a
  `heroAttacking` array naming each `{hunter, target, location, turnsRemaining, message}`.
- **Independent of `stopOnThreatMotivation`**: the motivation threshold still replaces the
  default danger stops, but it does NOT suppress this one. A death (`unitLost`) still outranks it.
- Edge-triggered: a hunt already running when the batch starts does not re-stop it (nor does it
  stop a fresh batch started after the hunt began). You will be told once, at the window.
- Single-turn calls (`count:1`) attach the same `heroAttacking` payload (no stopReason — a single
  turn always returns).
- Opt-out: `stopOnHeroAttacking:false`.

## Fixed — `outcomeText` can no longer name the wrong unit (G16-#2)

Resolving an event could report a **stale message from an earlier action** as this event's
outcome (game 16 read "Warlock Puile stops their task…" as the Baroness's outcome). The game
queues popups; a message stranded by an earlier resolution used to be attributed to whatever you
resolved next.

- `outcomeText` now contains **only messages created by this resolution**.
- Pre-existing stranded notices are drained into a separate `queuedNotices` key with a
  `queuedNoticesNote` saying they predate this choice (they also land in the event log as
  `queuedNotice`).
- The `followUp` blurb now distinguishes "a further popup chained from this outcome" from
  "a previously queued, unrelated popup is now the pending decision - it is NOT a result of this
  choice".
- A resolution whose only visible message was stale now correctly falls through to
  `observedChanges` (the actor/location numeric diff).

## New — per-clause challenge requirements: `requirements` (G16-#3)

A multi-clause refusal used to re-state every clause, unactionable ("which one failed?"). For
challenge types with a clause evaluator (first: **Plague Ships**):

- `list_challenges` / challenge summaries carry `requirements: [{clause, met, actual}, ...]`
  next to `restriction` — e.g. `{clause:"plague at this dock is at least 10%", met:false,
  actual:"4%"}`.
- The `perform_challenge` refusal itemizes clauses, failed first:
  `... are not met: [X] plague at this dock is at least 10% (now 4%); [OK] the docks here are
  infiltrated (infiltrated); [OK] this dock lies on at least one trade route (2 trade route(s))`.
- Vanilla-text correction included: the restriction's "a trade route which connects to another
  dock" overstates the coded check — ANY trade route through the dock satisfies it.
- Side-effect fix: `Ch_PlagueShips.valid()` **spreads plague** to connected docks when it passes
  (a game quirk). The mod no longer calls it from `list_challenges` / `perform_challenge`
  pre-checks — validity is computed from the clauses — so repeatedly listing challenges at a
  plague dock no longer mutates the world. (The spread still happens when the game itself runs
  the challenge — that part is the game's own behaviour.)

## New — minion screening is visible before you commit to a fight (G16-#5 / G15-#8)

`dangerEstimate`/`verdict` sum minions into one scalar and hid the decisive mechanic: **leaders
always strike the enemy's slot-0 minion first**; a living front minion makes the enemy leader
untouchable, damage is `max(0, attack − defence)`, and defence is ablative (each hit removes the
attacker's full attack from it, floored at 0, never regenerating in the battle). A
"favourable-looking" fight killed an attack-2 agent against a defence-4 Paladin screen.

- `get_unit` `combat.minionScreen` (both yours and a hostile hero's): `{count, front:{name, hp,
  defence, attack}, note}` — absent when no living minions.
- The pending-battle decision now carries per battle `yourScreen`, `attackerScreen`, and a
  computed `screeningNote` with the concrete math ("your attack 2 vs defence 4: the first 2
  swing(s) deal 0 damage, 6 swing(s) to kill (hp 7) - while their leader (attack 5) strikes your
  side every round"). `verdict` is unchanged but its note now warns it can be wrong under
  screening.
- The on-tile attack order entry carries `theirMinionScreen` / `yourMinionScreen` and its hint
  says to compare them, not just the dangerEstimates.
- The `disrupting_skirmish` tip now states the REAL combat order (attacker leader → defender
  leader → minions pair off row by row) and the ablative-defence rule; the in-battle popup
  (already showing per-side minions with live defence) is the ground truth once a fight starts.

## Version

`game_overview.modVersion` now reports `0.14.0`.
