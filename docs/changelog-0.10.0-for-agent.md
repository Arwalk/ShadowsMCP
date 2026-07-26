# ShadowsMCP 0.10.0 — what changed for you (the playing agent)

Driven by your game-13 feedback. Every claim was verified against the decompiled game before
acting; the last section records what turned out NOT to be a mod defect so future reports can be
cross-checked.

## New — the Laughing Tome's state is now observable (`tomeStatus`) [#2, #8, #13]

- Every tome-related challenge entry (Summon Tome, Forcibly Summon, Collect Tome, Bind Tome) now
  carries a `tomeStatus` object: `state` is one of `beingBound` (with the binding `unit` and its
  `location`), `heldBound` / `held` (with the `holder`), `activeAtLocation` / `inertAtLocation`
  (with the `location`), or `inEther` — each with a `note` saying what that state means for
  retrieval.
- When the state is `beingBound`, the summon's `restriction` itself is extended with the checked
  fact: "right now: being bound by <unit> at <location> - interrupt the binder or wait". A failing
  `perform_challenge` on a tome challenge also appends a one-line "Tome status: …" to the error.
- Retrieval routes, now stated instead of discovered by accident: an **inert** tome (asleep at a
  location) exposes the `Ch_CollectTome` challenge AT that location; an **active** tome
  (spreading madness) is NOT collectable — interrupt/wait out any binder, or summon. A tome held
  BOUND by a character makes the plain summon complete but do nothing (rob or kill the holder).

## Fixed — resolve_decision reads the whole outcome popup chain [#4]

- An outcome's effects can queue several popups; the old code read at most the first and then
  claimed "applied without disclosure" for text that arrived one blocker later. Now every
  consecutive outcome message is drained into a single `outcomeText` (separated by blank lines).
- If a non-message popup chains after the outcome (a follow-up event, a level-up…), the result
  carries `followUp` telling you it is now the pending decision — it is never auto-dismissed.
- Only when the game genuinely queued no text does the result say so, reworded to "applied
  without an outcome message". The misleading "applied without disclosure" string is retired.

## New — events name their actor and location [#6, #12]

- `kind:"event"` decisions now carry `actor` (`person` ref, current `gold`, and a note) — the
  bracketed option gates like "[Requires: 20 Gold]" are checked against THIS person's resources,
  which is why an option can be disabled while your treasury looks fine.
- The same decisions carry `location` (the acting person's location) — so recurring events
  compacted to "(recurring event; full text shown earlier)" still tell you WHERE they fire, and
  options like "The ruler of this location is executed" are actionable. Both fields are absent
  only on god-level events with no acting person.

## Fixed — ritual requirement errors explain the no-auto-travel rule [#9]

- `perform_challenge` on a `Cr-` ritual whose requirements fail now appends: rituals are performed
  IN PLACE and are never auto-travelled (unlike `C*` location challenges) — `move_unit` to a
  qualifying location first, then retry the same id.

## Fixed — `hiddenNotPerformable` flags truncation and keeps your own kit [#10]

- When the 20-item window drops entries, the object now carries `truncated: true` and a
  `truncatedNote` ("items lists the first N of M…"). `count` was already the true total.
- The unit's own rituals (including item-granted ones) are recorded into the window BEFORE the
  location's challenges, so a unique agent's signature kit can no longer be pushed out by generic
  location entries.

## Triage notes — reported, investigated, not a mod defect

- **The Summon Tome restriction was TRUE [#2/#8].** "Cannot be used if a hero is currently binding
  the tome" is the game's exact `validFor` predicate: it fails precisely while some unit on the map
  is performing Bind Tome. In game 13 a hero almost certainly WAS binding when you saw
  `validForUnit:false` — there was just no way to check. `tomeStatus` now proves it either way.
- **`Ch_CollectTome` appearing "at that location" [#13]** is vanilla behavior of the INERT tome
  property only; the active (madness-spreading) tome genuinely has no collect challenge. The
  `tomeStatus` notes encode this distinction.
- Your positives #11 (`stopOnThreatMotivation`) and #14 (the stale-id error's enumeration) need no
  change; #14's pattern — errors that enumerate what IS available — is noted as the direction for
  future error-message work beyond the ritual hint above.
