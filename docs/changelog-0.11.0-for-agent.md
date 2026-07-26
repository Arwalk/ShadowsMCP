# ShadowsMCP 0.11.0 — what changed for you (the playing agent)

This release is about third-party **content mods** (new gods, events, archetypes). In a vanilla
install nothing changes for you. When a content mod that advertises an MCP manifest is installed:

## New — content mods can teach you their mechanics

- `game_overview` carries a `mcpExtensions` array naming the content mods that registered a
  manifest. If the field is absent, everything below is inactive.
- Their tips are merged into `get_tips` (index, `id=`, `category=`) with a `source` field naming
  the mod. The mod's most important tips arrive automatically, once, under `tips` on
  `game_overview`/`end_turn` — same channel as built-in contextual tips, some gated to the god
  being played. Treat them like built-in tips: they explain mechanics the base tips know nothing
  about, so read them before improvising against modded content.
- `list_recruitable_agents`: modded archetypes can now carry the same `abilities` recruit-preview
  block as vanilla ones.

## Changed — new_game god selection

- The `god` parameter is now a free string instead of a fixed enum. The five base keys
  (`snake`, `laughing_king`, `vinerva`, `ophanim`, `mammon`) and `random` work exactly as before.
  A content mod's god is selectable by its advertised key, its class name (e.g. `God_MyGod`), or
  its display name; `random` also draws from advertised modded gods. An unknown key returns an
  error listing everything available, including modded gods present in the setup list.

## Changed — modded popups

- A popup type a content mod declares as informational is now auto-dismissed by
  `end_turn(force)` like a vanilla notification (still named in the digest). Undeclared modded
  popups behave as before: conservatively surfaced as real decisions, never silently dismissed.
- A popup the mod declares "hard" warns you (in `note`) that its main action needs in-game
  interaction beyond the listed buttons.
