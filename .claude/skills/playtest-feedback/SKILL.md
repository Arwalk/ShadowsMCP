---
name: playtest-feedback
description: Ingest a playtest feedback report from the AI agent playing Shadows of Forbidden Gods through the MCP mod. Verifies every reported problem against the actual code (mod src/ AND decompiled game) before treating it as genuine, then classifies, reports verdicts, and — only after the user approves — implements fixes with the project's release conventions. Use when the user pastes a "GAME N" feedback report or asks to analyze/triage playtest feedback.
---

# Ingest AI playtest feedback

The feedback comes from an AI agent that played the game via the ShadowsMCP tools and wrote up
numbered issues (`G<game>-#<n>`). The agent is a careful observer but an unreliable diagnostician:
it sees symptoms through the tool surface and infers causes. **Never accept a reported cause,
mechanism, or expected behavior without verifying it in code.** Past games have included issues
where the symptom was real but the proposed mechanism was wrong (G18-#3: a "wrong species clause"
theory that was actually a missing per-clause refusal message), where the "bug" was correct
base-game behavior faithfully mirrored (G18-#5 modifier stacking), and where the mod's own
documentation was the defect (G18-#1: a locationNote sent an agent to its death).

## Phase 1 — Triage (always, before any code reading)

1. **Version check first.** Compare the mod version named in the report against `<Version>` in
   `src/Mod/ShadowsMCP.csproj` and the `docs/changelog-*-for-agent.md` history. If the report is
   from an older DLL, check each issue against the changelog before investigating — it may already
   be fixed, and reported numbers/messages may not exist in current code. Trace value provenance
   rather than assuming the report describes today's build.
2. **Split the report into independent issues.** Positives and "unchanged but noted" items need no
   verification, only acknowledgment (and possibly a follow-up idea). Repeat reports (the agent
   cites earlier game numbers) deserve a git-log check of what the earlier fix actually changed and
   why it did not cover this case.

## Phase 2 — Verify every claim in code (the core of this skill)

For each issue, establish ground truth from primary sources before judging it:

- **Mod-side text or payloads** (tool descriptions, tips, `locationNote`/`restrictionNote`/
  `mechanicsNote`, refusal messages): find the emitting code in `src/Mod/` (usually
  `Serialization/Summaries.cs`, `Tips/TipCatalog.cs`, `Tools/*.cs`) and quote it.
- **Game mechanics claims** (what a challenge/power/property actually does): read the decompiled
  game code in `decompiled/Assets/Code/`. Read the *writers* of any field involved, the full
  `validTarget`/`validFor`/`turnTick` logic, and the enumeration path that decides what a tool can
  even see (e.g. `Location.populateStandardChallenges`). A claim about where something is available
  requires an exhaustive grep for its constructors — not just its class definition.
- **Message provenance**: determine whether a quoted message is authored by the mod or is vanilla
  game text passed through. Vanilla text is annotated (tips, `*Note` fields), never rewritten —
  that is standing mod policy.
- **Watch for side effects**: some game "read" methods mutate state (e.g. `Ch_PlagueShips.valid()`
  spreads plague); verify via the mod's `SafeValid`-style replicated checks, and mirror `validTarget`
  logic in evaluators rather than calling it.

Independent issues should be verified in parallel (one Explore agent per issue or per cluster),
each returning file:line citations and verbatim quotes. Do not fix anything in this phase.

For each issue, land on exactly one verdict:

- **GENUINE — mod defect**: the mod's text, message, or payload is wrong or missing. Fixable.
- **GENUINE — game behavior, mod reports it poorly**: the game is the source, the fix is mod-side
  annotation, decomposed refusal clauses, or added context. Never "fix" by misreporting game state
  (e.g. never merge real duplicate modifiers).
- **NOT GENUINE — agent misunderstanding**: the mod and game are correct; consider whether the
  surface *invited* the misreading (that inviting surface may itself be a genuine issue).
- **STALE**: already fixed in a newer version than the one played.
- **UNRESOLVABLE without the save**: say so explicitly, state the candidate causes, and prefer a
  fix that would disambiguate next game (e.g. clause-itemized refusals).

Where the agent's proposed mechanism is wrong but the symptom is real, say both: refute the
mechanism, confirm the symptom, name the actual cause.

## Phase 3 — Report verdicts, then stop

Deliver a per-issue verdict report with file:line evidence and a recommended fix order. **Do not
implement fixes until the user approves** — the deliverable of a feedback report is the analysis.

## Phase 4 — Implementation (only after approval)

Follow the project's established fix conventions:

- **Annotate, don't rewrite**: vanilla game text stays verbatim; corrections go in `*Note` fields
  and tips, verified against code paths (a tip built on unverified game text has misdirected
  playtests before).
- **Surface on always-read tools**: agents under-use side tools; key new signals belong in
  `game_overview` / `end_turn` payloads, not only in a drill-down tool.
- **Tunable safety valves**: when a guard or early-stop annoys, retune the default AND add an
  opt-in/opt-out flag rather than removing the protection (`forceSpends*` pattern).
- **force only passes dismissable popups**: anything with a real choice must block `end_turn(force)`.
- **Floor displayed balances** (`Round2Down`) wherever a raw compare gates an action.
- **Counter primacy bias**: choice lists should expose per-option eligibility and example valid
  inputs, not rely on the agent picking wisely.
- Per-clause requirement evaluators mirror the decompiled `validTarget` exactly and report the
  failed clause first with its actual value.

Release checklist (all of it, every time):

1. Bump `<Version>` in `src/Mod/ShadowsMCP.csproj` (minor for a feedback batch, patch for a lone
   bug fix).
2. Write `docs/changelog-<version>-for-agent.md` addressed to the playing agent (see prior
   changelogs for voice: what changed *for you*, most behavior-changing item first).
3. Update `docs/agent-test-prompt.md`: every tool/field change gets a test item; invert any
   existing test that asserted the old behavior (grep for assertions the change breaks).
4. `./build.sh` — runs smoke tests and builds **both Debug and Release** and packages `dist/`
   (the user pulls DLLs from this sandbox; both configs must exist). All smoke tests must pass.
5. Commit: the user uses jujutsu, so detached HEAD is normal — commit in place, then
   `git branch -f master HEAD`; never checkout. Message style:
   `Address game-<N> playtest feedback: <short comma list> (<version>)`.

## Report format

Lead with the overall verdict count. Then one section per issue: verdict, the code evidence
(file:line), what the agent got right/wrong, and the fix (or why none). Close with a suggested fix
order and, if not yet approved, the question of whether to proceed.
