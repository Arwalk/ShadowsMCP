# ShadowsMCP 0.9.0 — what changed for you (the playing agent)

Driven by your game-7 feedback (the 530-turn Iastur run). Every claim was verified against the
decompiled game before acting; the fixes below are the ones that survived triage, and the last
section records what turned out NOT to be a bug so future reports can be cross-checked.

## Fixed — rituals now perform IN PLACE (the "Place Tome" blocker)

- Your report was right and it was this mod's bug: an item ritual's stored `location` is a dead
  placeholder the game never reads (item rituals are constructed against the map's first location —
  hence "Silong hills"), and the game always performs rituals wherever the carrier stands. The mod
  used to pathfind to that placeholder; now `perform_challenge` on any ritual (`Cr-` id) starts it
  immediately at the unit's current location and the result carries `performedAt`.
- `list_challenges` no longer prints the bogus `location` on rituals — they carry a `performsAt`
  marker instead: "the unit's current location". To place the Tome somewhere specific: move the
  carrier there first, then perform the ritual. No target parameter is needed.

## New — `end_turn {"passRoutineEvents":true}` (throughput)

- Opt-in: a curated whitelist of recurring, low-stakes mid-challenge events is auto-answered with
  a fixed sensible option so a `count` batch no longer stops every 1-3 turns on them:
  - "Watched" → "Silence them" (the smaller exposure cost — profile is the harder huntability gate);
  - "Life Continues" → "Subtly disrupt the party" (preserves the unrest you built; the "ignore"
    option quietly reduces unrest by 25);
  - "Merchant of Antiquities" → the refusal (the buys are judgement calls left to you).
- Every auto-answer is reported in `digest.autoResolvedEvents` (`{turn, title, chose, outcome?}`)
  and in `get_recent_events` — the opt-in trades attention, not information. Any event NOT on the
  whitelist (and any whitelisted event whose curated option is missing or disabled) still blocks
  exactly as before. `force` semantics are unchanged.

## Clarified — data that read as wrong but wasn't

- **Archetype codes are stable** (15 is always The Seeker). What changes is availability: unique
  (positive-code) archetypes can be recruited ONCE per game — the game removes them from the
  recruitable list afterwards. The old "unknown agent code 15" error now says exactly that, names
  the archetype, and lists what is still recruitable as `code (name)` pairs.
- **`isInfiltrated` is gone.** That raw game flag meant an orc-style whole-settlement takeover and
  is never set by human-city infiltration — which is why it read `false` next to `infiltration: 1`.
  Settlements now report `fullyInfiltrated` (derived: infiltration fraction >= 1.0) alongside the
  unchanged `infiltration` fraction, in both `get_location` and `world_summary`.
- **Channelled casts pay heat up front.** Entries with `channelled: true` (e.g. Waves of Madness)
  now carry a `heatNote`: the listed `menaceGain`/`profileGain` are applied in full on the FIRST
  turn of casting, nothing further on completion, and interrupting does not spare them. Plan
  exposure around the start of the cast, not its end.
- **Exclusive challenges name their performer.** A single-user challenge actively performed by
  another unit at that location (your Lay Low case) now says so in `restriction` ("currently being
  performed by X - only one unit may perform this at a time"), in addition to the `claimedBy` field
  that was already emitted.

## Triage notes — reported, investigated, not a mod defect

- The contradictory "now likes Shadow and will preferentially do quests to which feature this tags
  positively, or avoid actions which increase it" confirmation is the game's own verbatim string
  (grammar bug included). The actual mechanic is the tags system — `get_tips {"id":"tags"}`.
- Mid-challenge events interrupting challenges every few turns is core game behavior (a period
  parameter plus a coin flip); the game offers no auto-resolve, only a master toggle at game
  setup. `passRoutineEvents` above is the mod-side answer.
- A city gaining a district mid-game with no notification (your Temple case) is genuinely silent
  in the game code; a mod-side notification was considered and deferred for now — re-list
  challenges after a holy order grows rich near your infiltration targets, and note the City
  Palace requires every other infiltratable district first (so a new district re-locks it).
