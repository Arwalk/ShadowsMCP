# ShadowsMCP 0.4.5 — what changed for you (the playing agent)

Read this before your session. It fixes several defects you reported in game 8, and corrects one
piece of advice the interface itself gave you. Update your notes accordingly.

## Fixed — retire these workarounds

- **The "Minion Management" popup is now readable.** No more `[Invalid]` / `Button (Previous) (N)`.
  It surfaces as `kind:"minionDismissal"` with one option per minion — "Dismiss/Keep <name>
  (HP x/y, command cost c)", the just-recruited minion flagged `newlyAcquired` — plus a `state`
  block (`commandUsed`/`commandLimit`/`keptCount`/`acceptEnabled`) and a final "Accept current
  selection" option. It is a **toggle-then-commit** flow: each keep/dismiss toggle returns
  `stillOpen:true` with refreshed `state` and a NEW options list (indices shift — re-read them),
  then Accept commits. **`force` is refused** on this popup: dismissal is permanent, so you must
  choose. Stop using `resolve_decision {force:true}` to blind-dismiss it.
- **`inspect` now accepts full challenge ids as roots.** `inspect
  {"path":"C31-Ch_Elf_ElderBirthright-92486fbb"}` resolves (no more "unexpected character '-'").
  Ritual ids (`Cr-...`) work too.
- **`inspect` no longer dumps the world at depth ≥ 2.** Embedded back-references to the Map and
  Unity engine objects (World, UIMaster, managers) collapse to a short marker like
  `<Map: back-reference suppressed - inspect the 'map' root directly>`. Deep inspects on entities
  (`SG53`, `U17`, …) are now safe and compact. Navigating TO the map (`inspect map`) still works.
- **`end_turn {resolveOptionIndex}` is never silently ignored anymore.** If the index could not be
  applied — nothing was actually pending, or the resolve failed — the result carries a
  `resolveWarning` saying exactly that. Your game-8 rule ("advancedBy:0 + unchanged decision ⇒
  retry with resolve_decision") is obsolete: the queued-decision case that caused it is fixed
  (the pending queue is promoted before the resolve runs).
- **Multi-army battle popup spam no longer stalls a force loop.** A "Battle" notice the game raises
  late in one `end_turn(force)` call is swept at the start of your next one, so repeated
  `end_turn {"force":true}` keeps advancing through a 6-army battle. Nothing is lost: every battle
  message still lands in `digest.events` and `get_recent_events`. Real choices still always block.

## Corrected advice — update your strategy notes

- **There is NO "store the agent" mechanic. There never was.** The old `agent_exposed` tip
  recommended storing to halve menace/profile and clear the floors — that was wrong (the mechanic
  is dormant/cut content in the game itself; nothing in the UI or the MCP can do it). The tip now
  gives the real guidance: **menace/profile floors ratchet permanently and nothing resets them.**
  Manage exposure preventively (pull out of hunter range early, Lay Low / In Hiding before the
  floor rises), and treat a badly over-exposed veteran as your candidate for risky high-value work,
  not as something you can ever make quiet again.

## New discoverability — use it

- `get_unit` on an in-battle army: the `battle.note` now tells you how to sway the fight — move a
  hero/agent onto the battle's tile and perform **'Command Battle (Attacking)' / 'Command Battle
  (Defending)'**; these appear in `list_challenges` only for a unit co-located with the battle.
  The `army_in_battle` tip says the same.

## Still true (no change — from your own game-8 findings)

- `use_power`'s parameter is `powerId` (schema is the source of truth; wrong/missing params get a
  full "Valid parameters: …" error naming every field).
- Armies cannot retreat once a field battle starts; it auto-resolves one cycle per `end_turn`.
- Challenge ids can go stale legitimately — re-run `list_challenges` right before
  `perform_challenge`; the stale-id error lists current alternatives.
- Narrative events (including "Defeat") always block even under `force`; answer them with
  `resolveOptionIndex` / `resolve_decision`.
