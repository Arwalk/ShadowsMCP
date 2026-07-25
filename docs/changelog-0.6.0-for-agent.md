# ShadowsMCP 0.6.0 — what changed for you (the playing agent)

One feature: recruitment is no longer blind. `list_recruitable_agents` now tells you what each
archetype will actually be able to DO once recruited, before you spend the recruitment point.

## New — archetype ability previews in `list_recruitable_agents`

- Every archetype entry now carries an **`abilities`** array of `{name, desc, prereq}` objects:
  the rituals that archetype unlocks the moment it is enthralled, each with a compact
  prerequisite gist. Example: the Aristocrat's "Crisis Vote: Plague" needs plague >50% here plus
  at least 3 other qualifying major settlements within 4 steps, rulers alive, once per 32 turns —
  so recruiting her only pays off if you can engineer a multi-city crisis.
- An empty `abilities` array is a positive statement: that archetype has no recruit-unlocked
  rituals (e.g. the Bandit King is a pure fighter-commander). In that case — and for archetypes
  whose power is innate masteries or level-up choices (Warlock, Survivor, Cursed) — an
  **`abilityNote`** string says where its value actually lives.
- Scope caveat: the preview covers what is unlocked **at recruitment** (plus innate/level-up
  notes). Abilities gained later from traits, carried items, or events are not listed — those
  still surface on the recruited unit via `get_unit` / `list_challenges` as before.
- Use it for planning: match prereqs against your current position (infiltration levels, plague/
  famine spread, shadow %, horde state) instead of recruiting on stats and flavor text alone.

## New — "Discovery mode" (human-set mod config option)

- The player can enable **Discovery mode** in the in-game mod config popup to hide the
  `abilities`/`abilityNote` fields entirely, so an AI can discover the game blind. Default is
  off (previews shown). It is not a tool parameter — you cannot toggle it; if the fields are
  absent, respect the blind playthrough and learn abilities by recruiting and experimenting.
