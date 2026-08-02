# ShadowsMCP 0.13.0 — what changed for you (the playing agent)

One feature release: **observer mode**. A human can now play the game normally while a connected
agent watches and narrates as a companion. If you are the *playing* agent and observer mode is off
(the default), nothing about your workflow changes — every 0.12.0 behavior is intact.

## New — observer mode (human plays, you watch)

A human-only mod-config option (`Observer mode`, off by default; never settable via any tool).
While it is on:

- **Every game-mutating tool refuses** with an error naming observer mode: `move_unit`,
  `cancel_task`, `perform_challenge`, `use_power`, `recruit_agent`, `command_army`,
  `command_agent`, `influence_holy_order_tenet`, `oppose_divinity`, `resolve_decision`,
  `end_turn`, `new_game` — 12 tools. The refusal is expected, not retryable: the human is
  driving. Nothing is ever mutated by a refused call.
- **Read-only tools all keep working** (`game_overview`, the `list_*`/`get_*` queries,
  `get_pending_decision`, `inspect`, `get_tips`) — use them to enrich narration.
- `game_overview` carries `observerMode:true` plus an `observerNote`, and any
  `pendingDecision.resolveHint` switches to "the player resolves this on screen" (the usual
  resolve instructions would send you at a refusing tool).

## New — `wait_for_events` (tool 36): the observer's long-poll event feed

`{cursor, timeout_seconds?, max_events?}` → `{observer_mode, events:[...], next_cursor, gap?,
waited_ms, turn?, pendingDecision?, gameOver?, state_unavailable?}`.

- **Cursor contract**: pass `0` to start, then always the last `next_cursor` you received.
  Events have monotonically increasing ids; replaying a cursor re-returns the same events.
- **Blocking**: returns immediately when events newer than your cursor exist; otherwise blocks
  up to `timeout_seconds` (default 25, clamped 1–55) and then returns an **empty batch with your
  cursor — never an error**. Your loop is simply "call again". Allow ~70s of client-side tool
  timeout at the maximum setting. The game stays fully responsive while you wait (the wait
  happens off the game's main thread).
- **`gap:true`**: events were trimmed past your cursor (bounded buffer, 500 events), or a new
  game / save load cleared the buffer — that case also appends a `game_changed` event saying so.
  Either way: resume from `next_cursor`; the missed events are gone.
- **Observer mode off**: returns `{observer_mode:false}` immediately with instructions for the
  human — it never blocks when nothing could ever arrive. Do not poll it in that state.
- **Ride-along state**: each response carries the current `turn`, any `pendingDecision` the
  player is looking at, and `gameOver`/`victoryAchieved` when the game ends, so a narration loop
  needs no side calls for orientation. If the game is mid-turn-processing the state read can be
  skipped (`state_unavailable:true`) — the events still arrive.

### What arrives on the feed

- `turn_start` — a synthetic marker the instant the human ends a turn (even a quiet one).
- The turn's unified messages (same stream `get_recent_events` archives; titles stripped of
  rich-text markup), tagged with `actor`/`location` entity stubs where the message names them.
  Mid-turn events (a battle resolving, a message raised by the human's own click) arrive within
  a frame, not at the next turn boundary.
- `popup` — a modal popup opened (title, kind, and whether it awaits a real player choice);
  when it closes, an event of the popup's kind records "resolved by the player". ALL modal
  popups are captured (seal breaks, duels, trading, level-ups, deaths, narrative events), except
  `PopupMsgUnified` — its content already rides the message stream (the standing no-dedup rule).
- `game_changed` — a new game or save was loaded; your cursor context is gone (see `gap`).
- `get_recent_events` also stays populated during human play now (turn snapshots are mirrored
  into it), so the companion's history queries work the same as in headless play.

## Scoping notes (deliberate v1 decisions)

- Capture granularity is **turn snapshots + popups + mid-turn messages** — no per-click input
  capture (the human's individual orders are not events; the game's own message stream is the
  right narration granularity).
- Capture uses the game's ModKernel hooks (`onTurnEnd` before the message wipe,
  `onUIFullscreenBlockerUpdate` for popups, plus a per-frame check) — no Harmony, no new
  dependencies, despite the original spec sketching Harmony patches.
- A popup already open at the moment observer mode is enabled gets no close event (its opening
  was never recorded). Toggling observer mode on mid-game starts the feed from "now" — the
  current turn's backlog is not replayed as news.
- Popup resolution capture records THAT the player resolved it, not which option they chose.

## Ground truth notes (verified against game source; not mod defects)

- `Map.turnUnifiedMessages` is wiped at the top of every `turnTick()`; the mod's `onTurnEnd`
  hook fires four lines earlier, which is why observer capture can snapshot it without patching.
- `turnTick()` also runs during map-generation burn-in; observer capture is gated on
  `map.burnInComplete`, so a new game does not flood the feed with 150 burn-in turns.
