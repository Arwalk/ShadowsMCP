# ShadowsMCP 0.5.0 — what changed for you (the playing agent)

Read this before your session. One new capability: you can now start games yourself. Update your
notes and your end-of-playthrough routine accordingly.

## New — `new_game`: start your own games

- **You no longer need a human to start or restart a game.** From the main menu — or over a running
  game — call `new_game` and a fresh world is generated and handed to you, headlessly. Pick your
  god (`snake`, `laughing_king`, `vinerva`, `ophanim`, `mammon`, or `random`), and optionally a
  `seed` (echoed back; same seed = same world), `mapSize` (`small`/`standard`/`large`),
  `difficulty` (0 = normal; -3 easy, 3 hard, 6 brutal) and `turnLimit` (default true = you must win
  within your god's 500 turns; false = endless).
- **It is SLOW — one call, then wait.** Map generation plus the burn-in history simulation takes
  ~30–120 s. Do NOT retry while it runs; even if the call times out, the game finishes starting —
  check `game_overview` before doing anything else.
- **Restarting is gated.** While a game is in progress, `new_game` refuses unless you pass
  `confirm:true`, which abandons the current game WITHOUT saving (there is still no save tool).
  Never pass `confirm:true` casually — write your retrospective first.
- **On success you get your bearings for free.** The result carries the god, the seed used, and a
  full `game_overview`-style `overview`; every other tool works immediately afterwards. Old `U*`/
  `L*`/`SG*` ids from the previous game are gone (session ids reset) — re-discover everything.

## Changed routine — end of a playthrough

- The old rule "ask the user to load a new game and STOP" is obsolete. Now: write your
  retrospective, update your notebook files, then start the next playthrough yourself with
  `new_game {"confirm":true, ...}` — vary the god across playthroughs to broaden your notes.

## Still true (no change)

- Everything about playing turns is unchanged: `game_overview` first, `get_threats` before
  committing agents, real choices always block `end_turn` even under `force`.
- All actions remain irreversible; abandoning a game via `new_game {"confirm":true}` is as final as
  losing it.
