# ShadowsMCP 0.5.2 — what changed for you (the playing agent)

Quality pass: a coherence audit of every tool against the game's own code, plus a token-diet on
tool descriptions and outputs. Several numbers you may have learned from tips were WRONG and are
now corrected — update your notes.

## Fixed — game-rule claims that were wrong

- **Hero detection radius**: heroes' AI sees an agent within **profile/10** hexes, not profile/5.
  The profile/5 belt is the wider early-warning scan `get_threats` runs (still reported as
  `combat.huntRadius`), and a ruler's hunt order has **no range cap**. Tips, tool text and docs
  all said /5 was detection — recalibrate any distance-based safety margins.
- **"Storing an agent" does not exist.** Two reference tips (`profile`, `menace`) still said
  storing halves exposure and clears the floor; that mechanic is unreachable in the live game.
  Only Lay Low / In Hiding (down to the floors) work; the floors themselves never drop.
- **Iastur (Laughing King) power regen**: with the Tome unread you regenerate at **half rate**,
  not zero — and the reader must be a ruler or an independent hero (your own agent holding the
  Tome does NOT count).
- **`victoryMode`/`victoryMessage` on defeat**: these previously reported a mode-0 SHADOW
  *victory* label/blurb after a DEFEAT (the game never sets victoryMode on defeat). They are now
  null unless `victoryAchieved` is true.
- `pointsToWin` is exactly 200 (was stated "~200"); elf succession crises last "many months",
  not years. Huntable thresholds (profile>=50 & menace>25) were audited and CONFIRMED correct.

## Fixed — challenges you could/couldn't see

- **Hero-only ("good") challenges no longer appear** in `list_challenges` and cannot be started:
  e.g. Combat Banditry (it REMOVES the banditry your agents planted). The game hides these from
  agents; the MCP now does too.
- **Item-granted rituals are now visible and startable**: rituals from carried items (Laughing
  Tome, Horde Banner, personal items…) appear in `unitRituals` tagged `fromItem`, and their
  `Cr-` ids resolve in `perform_challenge`. Previously they were entirely invisible via MCP.
- Armies (UM) no longer see location challenges the game never offers them — only challenges
  with a real army implementation are listed for a UM.
- A ritual id is now always resolved against the PERFORMING unit's own copy (two units holding
  the same ritual share an id; previously another unit's instance could be picked up).

## Fixed — decision-layer safety

- Explicitly dismissing a **confirm-order popup** (`resolve_decision force=true`) now declines
  it. Previously the dismiss path would have EXECUTED the confirmed order.
- Dismissing a **crisis-vote result popup** (plague/famine aristocrat vote) now applies the
  vote's outcome before closing. Previously dismiss discarded the entire result of a ritual you
  had spent turns casting.
- **Seal breaks and the god's awakening keep their detail under force**: `digest`'s dismissal
  items for these popups now carry a `detail` field with the popup body (which powers unlocked,
  etc.) instead of flattening to a bare title.

## Changed — outputs are leaner (same information)

- `list_units` / `get_player_state` order entries no longer repeat a full call-template hint per
  row; a single response-level **`ordersLegend`** explains the `command_agent` / `command_army`
  calls once. Per-row entries keep `{order, target, dangerEstimates, cancelsTheirTask}`.
  `get_unit` keeps the full per-entry hints.
- `game_overview`: `panic.total` removed (it duplicated the top-level `worldPanic`); recurring
  hint strings shortened; description now states the world meters are **0-1 fractions**.
- While the SAME decision stays pending, repeat tool calls get a one-line banner instead of the
  full headline (the full text remains in `game_overview.pendingDecision`).
- Tool descriptions were deduplicated/condensed (~22% smaller tools/list, ~18% smaller
  initialize instructions). No behavioral contract changed; deep mechanic education lives in the
  contextual tips (`get_tips`) that fire when relevant.
- `end_turn` force wording corrected: ONE unspent skill point per agent is auto-spent per forced
  turn (an agent holding several spends one per turn).

## Infrastructure

- `build.sh` now auto-deploys `dist/ShadowsMCP/` into the in-repo game install
  (`game/data/optionalData/ShadowsMCP/`) — the deployed copy had been stale since Jul 19.
- MCP protocol negotiation now defaults to the newest supported version (2025-06-18) when a
  client requests an unsupported one.

## Known limitations (documented, unchanged)

- After a victory, dismissing the victory popup opens the game's playback screen, which has no
  close button — the session is effectively over at that point anyway.
