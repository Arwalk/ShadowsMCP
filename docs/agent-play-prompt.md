# ShadowsMCP — agent-play prompt

Paste the **Prompt** block below into an agent (Claude Code / Desktop) connected to the ShadowsMCP
server. Unlike `docs/agent-test-prompt.md` (a pass/fail QA checklist), this prompt tells the agent to
**actually play the game to win**, experiment with mechanics, learn across multiple playthroughs, and
report MCP problems it hits along the way. It doubles as a long-soak test: an agent trying to win
exercises the tool surface in ways the scripted checklist never will.

## Before you run it (human setup — the agent cannot do these)

1. Install & enable the mod and start the game — leaving it at the main menu is fine: the agent
   starts its own games with `new_game`. All actions are irreversible from the agent's side — there
   is no save/undo tool, and `new_game {"confirm":true}` abandons the current game unsaved.
2. Connect the agent, e.g. `claude mcp add --transport http shadows http://<game-pc-ip>:8017/mcp`.
3. Paste the prompt. The connected server is referred to as **`shadows`**.
4. When a game ends, the agent writes a retrospective and starts the next playthrough itself,
   picking up its accumulated notes. No human action is needed between games.

---

## Prompt

````
You are playing *Shadows of Forbidden Gods* as a dark god corrupting the world, entirely through the
MCP tools of the connected server named `shadows`. You have NO view of the game window — the JSON the
tools return is your only reality. You are authorized to freely mutate this game; every action is
irreversible (no save/undo tool exists).

You have three missions, in this order of importance:

1. **Learn to play well.** Win if you can, but a well-understood defeat is worth more than a lucky
   win. You will play MULTIPLE playthroughs over time; your job is to get measurably better at the
   game from one to the next, using notes you keep on disk.
2. **Experiment with mechanics.** Deliberately exercise parts of the game you haven't mastered yet,
   even when a safer play exists, and record what you learn.
3. **Report problems with the MCP itself.** You are also this mod's most demanding user. Anything
   that confuses you, misleads you, crashes, or forces a workaround is a finding worth writing down.

### Your persistent notebook (files you maintain on disk)

Keep these files in the working directory and update them as you play — they are how you learn
across playthroughs. At the START of every session, read all three before touching the game.

- `sofg-learnings.md` — durable strategy knowledge: what works, what doesn't, per-god notes, threat
  rules of thumb (menace/profile thresholds, when heroes hunt, seal timing), opening lines you'd
  repeat. Write it as advice to your future self, with the evidence that earned each lesson.
- `sofg-experiments.md` — a running log of mechanic experiments: hypothesis → exact tool calls →
  observed result → conclusion. Include failed experiments; "this doesn't work and here's why" is a
  result.
- `sofg-mcp-issues.md` — every MCP problem, one entry each: the exact tool call (name + arguments),
  the verbatim response, what you expected, and severity (crash / wrong data / misleading or missing
  info / awkward-but-workable). Never paraphrase error text — quote it.

### How to play a turn

- Start every turn from `game_overview` — it carries the turn, time budget (`turnsRemaining` unless
  `endless`), victory progress, panic breakdown, seal countdown, threat breadcrumbs, any
  `pendingDecision`, and occasionally one-shot `tips`. Treat any `tips` entry as required reading and
  follow up with `get_tips {"id":...}` when you meet a mechanic you don't understand.
- Check `get_threats` before committing agents to anything: `agentSafety` verdicts
  (safe/favoured/even/outmatched), `topHunter.motivationPct`, and `hostilesOnTile` tell you who is
  about to die. An agent with high profile (heroes see it within profile/10; huntRadius = profile/5
  is the wider early-warning belt) and menace is huntable — manage
  those stats on purpose, not by accident.
- Give EVERY agent a job every turn (a challenge, a move, a power target). Then `end_turn`. For
  quiet stretches, batch with `end_turn {"count":N,"passIdleAgents":true,"passRoutineEvents":true}`
  and use `stopOnThreatMotivation` so a hunter closing in wakes you up; read the returned `digest`
  (events / dismissed popups / auto-resolved routine events / `lost` units) instead of assuming
  nothing happened.
- Rituals (`Cr-` ids) are performed IN PLACE, wherever the carrying unit stands — to place an item
  (e.g. the Laughing Tome) somewhere specific, move the carrier there first, then perform the
  ritual. Entries marked `channelled` pay their whole menace/profile cost on the FIRST turn of
  casting; interrupting doesn't refund it.
- Unique archetypes (positive agent codes) can be recruited only ONCE per game — the code stays
  valid as an identifier, but the archetype leaves the recruitable list after you take it.
- Verify what you did actually happened: query state before and after any consequential action
  (power cast, recruitment, tenet influence). If the state didn't change the way the result claimed,
  that's an MCP finding.
- Discover ids dynamically (`U*` from `list_units`, `L*` from `list_locations`, `C…` from
  `list_challenges`, `SG*` from `list_social_groups`, archetype codes from
  `list_recruitable_agents`). Never invent or hardcode ids. If `game_overview.idEpoch` changed since
  you last looked (it increments on every load / new game), every cached `U*` id is stale — re-run
  `list_units` before commanding anyone.
- Compare challenges with `etaTurns`, not raw `complexity`: `progressPerTurn` is unit-relative
  (stat-scaled — the same challenge can run 7x faster for another agent; `progressBreakdown` names
  why), so the right agent on the right challenge is a strategic choice.

### Decisions and popups — read before choosing

- When a decision blocks (`pendingDecision` / the ⚠ banner on tool results), `get_pending_decision`,
  read EVERY option, and choose the one that serves your strategy — do NOT default to option 0
  because it is first. Say in your narration why you chose it.
- Pass the decision's `decisionId` back as `expectedDecisionId` when you resolve (on
  `resolve_decision` or `end_turn`): chained popups reuse option indices, and the guard refuses the
  click if the pending decision changed under you instead of answering the wrong popup.
- `end_turn {"force":true}` only auto-dismisses purely informational notices. Real choices — combat,
  narrative events, level-up trait picks, item trading, list pickers — always block, by design. When
  blocked, resolve the decision; don't hammer `force`.
- Never pick a "No longer show message of type…" option — it permanently blinds you to that message
  type for the whole game.
- Battles: an attacked agent raises a `kind:"combat"` decision. Open it, read the odds
  (`attacker`/`defender` blocks), and decide to fight, step, or flee like it matters — fleeing a bad
  fight is often correct, and the "flee as soon as possible" option does the whole step-and-retreat
  sequence in one call (round 2 escape costs ALL minions; round 3+ is safe). Starting a duel
  yourself (`command_agent` attack) cancels the target's ritual even if you then retreat; that is a
  weapon, use it.
- Trades (`kind:"itemTrading"`): the composite options ("Take all and close", "Swap top items and
  close") finish the whole exchange in one call and report exactly what moved — prefer them over
  clicking individual buttons unless you need a partial trade.

### Experimentation program

Each playthrough, pick 2–3 mechanics you have NOT yet mastered (check `sofg-experiments.md` for what
is already covered) and build your strategy around exercising them. The menu:

- Powers (`list_powers` / `use_power`) — costs, targets, what actually changes.
- Recruitment (`list_recruitable_agents` / `recruit_agent`) — different archetypes, placement
  restrictions, corrupting a hero in place. Each archetype's `abilities` array previews the
  rituals it unlocks once recruited and their prerequisites — pick archetypes whose prereqs
  your plan can actually meet.
- Challenges (`list_challenges` / `perform_challenge`) — enshadowment, infiltration, the long
  rituals and what protects them.
- Agent-vs-agent (`command_agent`) — attack, rob, trade, follow; the ritual-cancel-on-attack rule.
- Armies (`command_army`) — raze, drive back, attack (needs a commandable military unit).
- Religion (`list_holy_orders` / `influence_holy_order_tenet` / `oppose_divinity`) — tenet shifts,
  the Alignment gate, divine entities.
- Investigations & evidence (`list_investigations`, clues on `get_location`) — how detection builds
  and how to starve it.
- Deep reads (`inspect`) — when the curated tools don't explain an outcome, go look at the raw
  state and note what the curated view should have shown you (that's an MCP finding too).

Log every experiment in `sofg-experiments.md` as you go, not at the end.

### MCP problem reporting — the rules

- A response starting `tool failed:` / `command_agent <order> failed:` / `tool '<name>' failed:`
  with an exception type and stack frames is a MOD BUG, never a game rule. Quote it verbatim in
  `sofg-mcp-issues.md`, do NOT retry hoping it passes, and route around it for the rest of the game.
- A clean sentence about the game ("not under your command", "requirements not met: …") is a rule,
  not a bug — but if the message didn't tell you enough to fix your call, log that as a
  missing-info finding.
- Also log: fields that contradict each other across tools, ids that go stale unexpectedly, results
  that don't match the state change you then observe, hangs, and anything you had to guess.

### Regular reporting (required, every ~5 turns and at every major event)

Post a **Turn Report** to the user. Keep it short but complete:

1. **Situation** — turn / turnsRemaining, victory progress vs `pointsToWin`, seals broken and
   turns to next seal, world panic, agent roster (one line each: location, task, safety verdict).
2. **What happened** — events and decisions since the last report, each with the option you chose
   and WHY.
3. **What I did** — orders issued this cycle and their intent.
4. **Plan** — your strategy for the next ~5 turns, the biggest threat to it, and your contingency.
5. **Experiments & findings** — experiment progress, plus any new MCP issues (one line each; full
   detail goes in the file).

Also report immediately (outside the cycle) when: an agent dies or becomes outmatched, a seal
breaks, a war starts that touches your plans, a decision materially changes your strategy, or an
MCP crash occurs.

### End of a playthrough

When `endOfGameAchieved` is true (win or lose — losing all agents is NOT a loss; play on):

1. Post a full **Retrospective**: outcome, final score vs `get_victory_breakdown`, the 3 decisions
   that mattered most, what you would open with next time.
2. Update `sofg-learnings.md` (fold in the retrospective) and close out `sofg-experiments.md`
   entries for this run.
3. Summarize `sofg-mcp-issues.md` — new issues this run, by severity.
4. Start the next playthrough yourself with `new_game {"confirm":true}` (pick the god and options
   that serve your experiment plan; it takes ~30-120s — make ONE call and wait). Reread your three
   files first and open with a plan that uses what you learned.

Begin now: read your notebook files (create them with a header if absent), then call
`game_overview`. If it says no game is in progress, start one with `new_game` (your choice of god —
vary it across playthroughs). Post an opening **Turn Report** with your strategic assessment and
chosen experiments, then play.
````

---

## Notes

- The reporting cadence (~5 turns) is deliberate: it forces the agent to re-read `game_overview` /
  `get_threats` instead of batching blindly, which is where most real-play MCP findings surface.
- The three notebook files are the cross-playthrough memory. If you move the agent to a new working
  directory, carry them over.
- `sofg-mcp-issues.md` entries are written to be copy-pastable into bug reports against this repo —
  exact call, verbatim response, expectation.
