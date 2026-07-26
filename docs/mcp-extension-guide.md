# Extending ShadowsMCP from a content mod

ShadowsMCP already handles third-party content generically: modded units, challenges, popups and
gods flow through the game's own lists, `GetType().Name` serialization, the generic popup handler
and the `inspect` reflection tool with no cooperation needed. What the MCP *cannot* know is what
your content **means**: how your mechanics work (so a playing agent isn't guessing), which of your
popups are safe to auto-dismiss, what key selects your god in `new_game`, and what your archetypes
unlock at recruitment.

A content mod advertises exactly that through a **manifest**: one method on its `ModKernel`
subclass returning a JSON string. The coupling is duck-typed — neither mod references the other's
DLL, so your mod works without ShadowsMCP installed, ShadowsMCP works without yours, and there is
no version lockstep.

```csharp
public class MyModCore : ModKernel
{
    // ShadowsMCP discovers this by name via reflection. No reference to ShadowsMCP needed.
    public string getShadowsMcpManifest()
    {
        return MANIFEST;
    }
    ...
}
```

Discovery runs when mods finish loading (and again on save load); a manifest that fails to parse
is skipped with a line in `ShadowsMCP.log`, never an error in your mod. When at least one manifest
is loaded, `game_overview` gains a `mcpExtensions` array naming the contributing mods.

## Manifest schema

Everything except `manifestVersion` and `name` is optional — ship only the sections you need.

```json
{
  "manifestVersion": 1,
  "name": "My Content Mod",

  "tips": [
    {
      "id": "mymod_harvest",
      "title": "The Harvest mechanic",
      "category": "god",
      "summary": "One line for the get_tips index.",
      "body": "Full agent-facing explanation of the mechanic (see the writing guidance below).",
      "when": "always",
      "godClass": "God_MyGod"
    }
  ],

  "informationalPopups": [ "PopupMyNotice" ],
  "hardPopups": [ "PopupMySlider" ],

  "gods": [
    {
      "key": "my_god",
      "className": "God_MyGod",
      "description": "One line on the god's playstyle."
    }
  ],

  "abilityPreviews": [
    {
      "archetype": "UAE_MyCultist",
      "note": "Optional free-text note (innate masteries, signature level-ups).",
      "rituals": [
        { "name": "Ritual name", "desc": "What it does", "prereq": "Compact prerequisite gist" }
      ]
    }
  ]
}
```

Field semantics:

- **tips** — agent-facing explanations of your mechanics, merged into `get_tips` (index, by id, by
  category). `id` must be globally unique: **prefix it with your mod's name**; colliding ids are
  skipped. `category` must be one of `basics, world, infiltration, politics, tactics, god,
  faction, magic, economy` (anything else is filed under `basics`). `when` is `"manual"` (default;
  reachable only via `get_tips`) or `"always"` (surfaced once, automatically, in
  `game_overview`/`end_turn` — use it for the 1–3 tips an agent cannot play your content without).
  `godClass` optionally gates a tip to games where that `God` subclass is being played.
  Cap: 64 tips per mod.
- **informationalPopups** — type names (the `Popup*` MonoBehaviour class name) of YOUR popups
  where **every button merely closes the popup** — no gameplay branch. These become safe for
  `end_turn(force)`'s auto-dismiss. Mislabeling a popup with a real choice means the agent's batch
  end-turn silently clicks through it — when in doubt, leave it out; unknown popups are
  conservatively surfaced as decisions.
- **hardPopups** — type names of your popups whose main interaction is not a button (drag,
  slider, text entry). The MCP still lists their buttons but warns the agent that the main action
  needs in-game interaction.
- **gods** — makes your playable god selectable in the `new_game` tool by `key` (and joins the
  `random` pool). Your mod must still add the god instance to the setup list in its
  `onStartGamePresssed(map, gods)` hook — the manifest only names it. (Even without a manifest
  entry, `new_game` accepts a modded god by class name or display name.)
- **abilityPreviews** — what an archetype unlocks the moment it is enthralled, keyed by the
  `UAE_*` class name; shown in `list_recruitable_agents` so an agent can plan recruitment.
  Convention: `rituals` = recruit-unlocked rituals only; use `note` for innate masteries and
  signature level-up choices.

Practical constraints: the manifest must be under 256 KB; the method must never throw (return a
constant, or wrap any assembly in try/catch); it may be called more than once. Manifest text is
shown to a playing agent as trusted guidance — write facts about mechanics, not instructions to
the agent, and never marketing copy.

## Writing tips an agent can use

The playing agent sees **only JSON from the MCP tools** — no screen, no tooltips, no art. Tips
written like UI hints ("click the glowing icon") are useless to it. What works:

- **State the numbers.** Thresholds, costs, ranges, durations: "menace above 25 makes X happen",
  not "being too threatening is risky".
- **Name the tool surface.** Say which tool/field shows the mechanic's state (`get_unit`'s
  `combat` block, a challenge's `restriction` string, a `Popup*` type name) so the agent knows
  where to look.
- **Explain the loop, not the lore.** What should the agent DO with the mechanic, in what order,
  and what commonly goes wrong. One paragraph per tip; split big mechanics into several tips.
- **Cover eligibility.** If an action is often greyed out, say exactly what unlocks it — agents
  otherwise pick the first option that doesn't error and never revisit.

## Prompt recipe: add manifest support with a coding agent

Paste the block below into a coding agent (Claude Code or similar) **opened in your content mod's
repository**. It is self-contained — the agent does not need this repo or ShadowsMCP's source.
Review the generated manifest by hand afterwards: you are the only one who knows whether a popup
truly has no gameplay branch and whether a tip's numbers are right.

````
Add ShadowsMCP manifest support to this Shadows of Forbidden Gods content mod.

ShadowsMCP is a mod that embeds an MCP server in the game so an AI agent can play it. It
discovers content-mod metadata through ONE duck-typed method — do NOT add any reference to
ShadowsMCP's assembly, do NOT add dependencies, and do NOT change any existing behavior of
this mod.

## Task

1. INVENTORY this mod's content. Find:
   - every playable God subclass it adds (added to the god list in onStartGamePresssed),
   - every Popup* MonoBehaviour it creates or opens,
   - every UAE_* agent archetype it makes recruitable,
   - every genuinely new mechanic (new resources, meters, challenge families, victory routes,
     event chains) a player must understand to play this content well. Read the code that
     implements each mechanic and note its REAL numbers: costs, thresholds, ranges, cooldowns.

2. On the mod's ModKernel subclass, add exactly this method (public instance, no parameters):

       public string getShadowsMcpManifest()

   It must return a JSON string and MUST NEVER THROW. Build it as a verbatim string constant
   (or read an embedded resource inside try/catch returning null on failure). It may be called
   more than once; keep it cheap and deterministic. Total size well under 256 KB.

3. The JSON manifest has this shape (every section except manifestVersion and name is optional
   — omit sections that don't apply to this mod):

       {
         "manifestVersion": 1,
         "name": "<the mod's display name>",
         "tips": [ { "id": "...", "title": "...", "category": "...", "summary": "...",
                     "body": "...", "when": "manual"|"always", "godClass": "God_X" } ],
         "informationalPopups": [ "PopupTypeName" ],
         "hardPopups": [ "PopupTypeName" ],
         "gods": [ { "key": "...", "className": "God_X", "description": "..." } ],
         "abilityPreviews": [ { "archetype": "UAE_X", "note": "...",
                                "rituals": [ { "name": "...", "desc": "...", "prereq": "..." } ] } ]
       }

   Rules per section:
   - tips: one tip per mechanic from the inventory. id MUST be prefixed with the mod's name
     (e.g. "mymod_harvest") — unprefixed ids collide and are dropped. category MUST be one of:
     basics, world, infiltration, politics, tactics, god, faction, magic, economy.
     summary = one line for an index. body = 3-8 sentences written for an AI agent that sees
     ONLY JSON tool output, never the screen: state exact numbers/thresholds from the code
     (never invent them — if a number can't be found, describe the behavior without one), say
     what the agent should DO and in what order, and name observable signals (unit stats,
     challenge names, popup titles) rather than visuals. NEVER write UI language ("click",
     "hover", "the icon"). Set when:"always" ONLY on the 1-3 tips without which the content is
     unplayable (they are pushed to the agent once at game start); everything else
     when:"manual". If a tip only matters when playing one of this mod's gods, set godClass to
     that God subclass name.
   - informationalPopups: ONLY popup types where EVERY button just closes the popup — pure
     notifications with no gameplay branch. These become auto-dismissable by the agent's batch
     end-turn, so when in doubt LEAVE THE POPUP OUT (the safe default: it is then surfaced to
     the agent as a decision). Verify by reading each button's onClick handler.
   - hardPopups: popup types whose main interaction is a drag/slider/text-entry rather than a
     button.
   - gods: one entry per playable god the mod adds in onStartGamePresssed. key = short
     lowercase snake_case; className = the exact God subclass name; description = one line on
     playstyle.
   - abilityPreviews: one entry per recruitable UAE_* archetype. rituals = ONLY the rituals
     granted in the archetype's constructor (unlocked at recruitment); read the actual
     validFor()/getRestriction() logic and compress it into prereq ("100% infiltration; ruler
     not already cruel"). Use note for innate masteries and signature level-up CHOICES.

4. VERIFY: the mod still compiles; the method returns valid JSON (add a unit test or a debug
   assertion parsing it if the project has test infrastructure); no other file's behavior
   changed. Then print the final manifest JSON for human review, flagging any number you could
   not confirm from code.

Ground every tip body and every prereq in this repo's actual code — no guessed values. Where
the mod's effects depend on base-game internals you cannot see, describe your mod's side only.
````

## Verifying an integration end-to-end

With both mods enabled and a game running:

1. `game_overview` → `mcpExtensions` contains your mod's name (and `ShadowsMCP.log` has a
   `mcp extensions: ...` line with the parsed counts).
2. `get_tips` → your tips appear in the index with a `source` field; `get_tips id=<your id>`
   returns the body; a `when:"always"` tip arrives under `tips` on the first
   `game_overview`/`end_turn` after the game starts (gated by `godClass` if set).
3. `new_game {"god":"<your key>"}` → starts as your god (requires your `onStartGamePresssed` to
   add it).
4. `list_recruitable_agents` → your archetype carries the `abilities` preview.
5. Open one of your informational popups → `end_turn` force-dismisses it and names it in the
   digest; an unlisted popup blocks instead.
