# ShadowsMCP 0.5.1 — what changed for you (the playing agent)

Bug-fix release driven by the last playtest run. The theme: displayed numbers and results you were
right not to trust are now trustworthy — update any workaround habits from your notes.

## Fixed — `menaceGain`/`profileGain` now show REAL heat

- These fields used to show the game AI's internal utility scores (wrong sign, wrong magnitude —
  e.g. "-73" on a challenge that ADDS menace, "+50/+50" on one that applies +8/+2). They now show
  the actual one-time menace/profile applied to your unit on completion, matching the in-game UI.
  **Throw away any learned distrust or correction factors for these fields.**
- Indefinite challenges (e.g. Lay Low) additionally carry `indefinite:true` and a `heatNote`: their
  completion values are 0 and their real (often heat-REDUCING) effect is per-turn, per the
  `description`.

## Fixed — market stalls are now distinct and purchasable

- The three "Buy Item From Market" challenges at a market now have three DISTINCT ids and each
  carries `itemForSale` {name, desc} (kept even with `terse:true`). Previously all three shared one
  id and only the first stall was ever reachable. A stall's id stays stable while its item is on
  sale and correctly goes stale when the market restocks.

## Fixed — task cancellations are no longer invisible

- If a unit's in-progress challenge/ritual is invalidated mid-cast (e.g. a ritual's location no
  longer qualifies), the `end_turn` digest now carries the game's `TASK_CANCELLED` event naming the
  unit and challenge. A unit going idle always has a discoverable cause now — check
  `digest.events` (and `get_recent_events`) before re-tasking blind.
- Travel-to-challenge tasks that die silently (the challenge vanished, or the path was blocked) get
  a synthesized `TASK_CANCELLED` digest event (`synthesized:true`) — the game itself never announces
  these.

## Fixed — nothing is silently filtered or silently a no-op

- `list_challenges` with `performableOnly:true` now reports what it hid:
  `hiddenNotPerformable` {count, items:[{id,name,restriction}], hint}. Locked challenge families
  (e.g. Geomancy at a geomantic locus, mastery-gated) are discoverable this way — no more finding
  them via mistyped-id error messages.
- Item-trade resolutions now state what actually moved: `itemsMovedToA`/`itemsMovedToB` and
  `goldDeltaA`/`goldDeltaB`. "Take All" with a full inventory now returns an explicit `warning`
  that items were left behind (the game silently skips them; gold still transfers) — check for it
  and free a slot before retrying.
- Narrative-event resolutions always disclose the rolled outcome: `outcomeText` when the game
  produced text (read and cleared for you), otherwise an explicit `outcome` note that a weighted
  outcome applied without disclosure. `chose` alone no longer happens.

## Fixed — challenge-completion popup is scriptable

- `kind:"challengeComplete"` now always has exactly 3 fixed options: 0 = Dismiss, 1 = Dismiss and
  pan camera (cosmetic), 2 = Repeat immediately with an `enabled` flag (+`why` when disabled). It
  also carries the completed `challenge` {id, name} so you can re-perform later by id. Picking a
  disabled Repeat errors cleanly instead of silently dismissing. It blocks `end_turn(force)` —
  Repeat is a real choice.

## Fixed — stale-id errors point at the right place

- A stale/unknown `perform_challenge` id now lists the challenges at the location ENCODED IN YOUR
  ID (named), not wherever the unit happens to stand, and notes when the unit would need to travel.

## Known limitation (unchanged, now honest)

- When an event outcome has no description text, WHICH weighted branch fired is unknowable through
  the MCP layer — the result says so explicitly; verify effects on the relevant unit/location if it
  matters.
