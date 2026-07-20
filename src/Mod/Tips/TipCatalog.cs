using System;
using System.Collections.Generic;
using Assets.Code;

namespace ShadowsMcp.Tips
{
    /// <summary>
    /// One curated, agent-facing tip. This is the mod's OWN explanation of a mechanic - deliberately
    /// NOT the base game's HintSystem strings, which are written for a human clicking a UI (they say
    /// "right-click to move", "hover the right-hand side", "green links", etc. - meaningless to an agent
    /// that plays through JSON tools). See docs/ground-truth-notes.md for the game accessors the triggers read.
    /// </summary>
    public sealed class TipDef
    {
        public readonly string Id;
        public readonly string Title;
        /// <summary>Topical bucket, one of <see cref="TipCatalog.Categories"/> (for get_tips filtering).</summary>
        public readonly string Category;
        /// <summary>One line, shown in the get_tips index.</summary>
        public readonly string Summary;
        /// <summary>True => woven into the initialize.instructions primer (the always-on core).</summary>
        public readonly bool Core;
        /// <summary>Contextual fire condition against live state; null for core or reference-only tips.</summary>
        public readonly Func<GameContext, bool> Trigger;
        /// <summary>Full text. A delegate so the few param-driven tips (tags, ophanim) can interpolate live
        /// numbers; most just return a constant. Must tolerate a null current map (fall back to World.staticMap
        /// or a number-free wording) so get_tips works from the main menu.</summary>
        public readonly Func<GameContext, string> Body;

        public TipDef(string id, string title, string category, string summary, bool core,
                      Func<GameContext, bool> trigger, Func<GameContext, string> body)
        {
            Id = id;
            Title = title;
            Category = category;
            Summary = summary;
            Core = core;
            Trigger = trigger;
            Body = body;
        }
    }

    /// <summary>The single source of curated tip content and its contextual triggers.</summary>
    public static class TipCatalog
    {
        /// <summary>Valid <see cref="TipDef.Category"/> values, also the get_tips `category` enum.</summary>
        public static readonly string[] Categories =
            { "basics", "world", "infiltration", "politics", "tactics", "god", "faction", "magic", "economy" };

        // ---- construction helpers (keep the catalog below declarative) ----
        private static TipDef Core(string id, string title, string cat, string summary, string body) =>
            new TipDef(id, title, cat, summary, true, null, _ => body);
        // For the contextual helpers the trigger comes right after the category, so a catalog entry reads
        // "id, title, category, WHEN-it-fires, summary, body" (CtxDyn puts its body delegate before the summary).
        private static TipDef Ctx(string id, string title, string cat, Func<GameContext, bool> trig, string summary, string body) =>
            new TipDef(id, title, cat, summary, false, trig, _ => body);
        private static TipDef CtxDyn(string id, string title, string cat, Func<GameContext, bool> trig, Func<GameContext, string> body, string summary) =>
            new TipDef(id, title, cat, summary, false, trig, body);
        private static TipDef Ref(string id, string title, string cat, string summary, string body) =>
            new TipDef(id, title, cat, summary, false, null, _ => body);
        private static TipDef RefDyn(string id, string title, string cat, string summary, Func<GameContext, string> body) =>
            new TipDef(id, title, cat, summary, false, null, body);

        public static readonly List<TipDef> All = new List<TipDef>
        {
            // ---------- CORE: the always-on primer (also woven into initialize.instructions) ----------
            Core("premise", "Premise", "basics",
                "You are a sleeping dark god; agents and powers are how you weaken humanity while you wake.",
                "Shadows of Forbidden Gods is a game of infiltration and hidden agendas. You are a sleeping dark " +
                "god; your agents are your hands in the world, weakening human societies while you wake. As you " +
                "wake you break seals on a fixed schedule - each seal raises your power cap, speeds power gain, " +
                "and increases how many agents you can hold. Human heroes oppose your agents and will hunt and " +
                "kill any that grow too exposed. You act through agents (query and command them with the unit " +
                "tools) and through god powers (list_powers / use_power). Read game_overview every turn - it " +
                "carries the turn, your resources, the seal countdown, threats, and any pending decision."),

            Core("infiltration", "Infiltration", "infiltration",
                "The core loop; reduce a settlement's security (unrest, neighbours, enshadowed ruler) to speed it.",
                "Infiltration is the core loop and unlocks most challenges (see list_challenges). Higher-security " +
                "settlements take longer - cities more than villages, capitals most of all. Reduce a settlement's " +
                "security before or during infiltration: cause unrest (for example start a famine by raiding the " +
                "farms and villages that feed it), infiltrate its neighbours first, or enshadow its ruler (an " +
                "enshadowed ruler welcomes your cult in). Plan the reduction; don't just throw an agent at a capital."),

            Core("challenges", "Challenges, profile & menace", "basics",
                "Agents act by performing challenges, which build profile (detection range) and menace (threat).",
                "Agents mainly act by performing challenges (list_challenges on a unit, then perform_challenge). " +
                "Each takes several turns depending on the agent's skill and the challenge's complexity, and " +
                "building it up grants the agent profile and menace. Profile is how far away heroes can detect " +
                "the agent; menace is how threatening heroes consider it. High profile plus high menace makes " +
                "heroes hunt that agent - watch get_threats and retreat or lie low before an agent is killed."),

            Core("recruitment", "Recruiting agents", "basics",
                "Need a roster slot (cap grows with seals) + a recruitment point; losing all agents is not a loss.",
                "To bring on a new agent you need two things: a free roster slot (your agent cap, which rises as " +
                "you break seals) and a recruitment point (gained over time). See list_recruitable_agents and " +
                "recruit_agent. Archetypes are not interchangeable - they specialise by their stats (intrigue for " +
                "infiltration and steering rulers, might/command for leading armies and combat, lore for rituals " +
                "and knowledge), so pick the one that fits your current plan rather than always the first on the " +
                "list; each archetype's placement.eligible and exampleTargets show where it can actually be " +
                "enthralled right now. Losing all your agents is NOT a loss - you are the god and your points " +
                "regenerate; just recruit again. The game only truly ends when endOfGameAchieved is set."),

            Core("panic_vs_awareness", "Panic vs. awareness", "world",
                "worldPanic = vague dread from your actions; awareness = people who know the truth and fight you.",
                "Two world meters track how much humanity notices you, and they differ. worldPanic is how visible " +
                "your actions are - people sense the world is going wrong without knowing why; it rises mainly from " +
                "using god powers and from clues heroes discover. awarenessOfUnderground is how many people know the " +
                "truth about you specifically - they act against you deliberately, taking pre-emptive action and " +
                "funding the Chosen One. Both climb as you act; game_overview's panic breakdown shows the sources."),

            Core("victory", "Winning on points", "basics",
                "You win on victoryProgress toward pointsToWin (~200), not on any single meter.",
                "You win on points, not on any single meter. victoryProgress in game_overview is your weighted " +
                "score toward pointsToWin (~200); get_victory_breakdown shows the category split behind it. Don't " +
                "confuse it with avrgEnshadowment (average shadow on rulers/heroes) or worldPanic - enshadowing " +
                "helps, but the score decides the game. Different gods win differently; see your god's own " +
                "mechanics in get_player_state.progression."),

            // ---------- CONTEXTUAL: fire once when a live condition becomes true ----------
            Ctx("high_sec", "High-security infiltration", "infiltration", HighSecInfiltration,
                "An agent is infiltrating a settlement with security above 5 - reduce security to speed it.",
                "One of your agents is infiltrating a high-security settlement (security above 5). The higher the " +
                "security, the slower the infiltration - cities and especially capitals start very high. Speed it " +
                "up by reducing security first: start a famine by raiding the surrounding farmland and villages to " +
                "cut its food, infiltrate the surrounding locations, or enshadow its ruler. Preparing the ground " +
                "first is usually faster than grinding a raw high-security infiltration."),

            Ctx("world_panic", "World panic is rising", "world", PanicRising,
                "Panic passed 0.1: your actions are becoming visible; awareness and hero funding start to grow.",
                "World panic has begun to rise. It measures how visible your actions are and how afraid ordinary " +
                "people are of the changing times - they don't yet know why the world is going wrong, but they " +
                "feel it. As panic climbs, people start gaining awareness of the real threat (you), and heroes " +
                "begin receiving funding from their cities. Expect resistance to stiffen from here on."),

            Ctx("awareness", "Awareness is rising", "world", AwarenessRising,
                "People are learning the truth about you and will act deliberately - low agent profile slows this.",
                "Awareness of the underground is rising. Unlike panic (a vague dread), awareness is people who know " +
                "the truth about your return and why the world is failing. They take deliberate steps to stop you - " +
                "acting pre-emptively against the shadow and funding the Chosen One as a champion against you. " +
                "Keeping your agents' profile low slows how fast the truth spreads."),

            Ctx("mid_challenge_events", "Mid-challenge events", "tactics", PerformingChallenge,
                "Challenges are interrupted every few turns by an event you must resolve as a decision.",
                "While an agent performs a challenge, it will hit obstacles: every few turns a random event " +
                "interrupts the challenge and the agent must deal with it to continue. These surface as decisions - " +
                "resolve them via game_overview's pendingDecision (or get_pending_decision / resolve_decision). " +
                "Expect challenges to run longer than the raw turn estimate because of these events."),

            Ctx("army_orders", "Commandable armies: raze, drive back, attack", "tactics", HasCommandableArmy,
                "A commandable military unit has special orders (command_army): raze cities, drive back heroes, attack armies.",
                "You control a military unit (a UM) - for example an awakened god-army such as She Who Will Feast, " +
                "or an orc raiding party. Besides moving, it has special orders that are NEITHER challenges nor god " +
                "powers, so they never appear in list_challenges or list_powers: they are listed under 'orders' in " +
                "get_unit/list_units and issued with the command_army tool. order=raze devours the human settlement " +
                "the unit is standing on (move it onto the city first; the city's defences fall each turn until it " +
                "is destroyed) - this is how She Who Will Feast wins; order=drive_back forces an enemy hero on its " +
                "tile to retreat; order=attack starts a battle with an enemy army on its tile. If this unit IS your " +
                "awakened god, guard it - its death ends the game."),

            Ctx("politics", "War & civil war", "politics", AnyWar,
                "Wars devastate human nations; hierophants in an infiltrated capital can start them and civil wars.",
                "There is now a war in the world, and wars are a powerful weapon for you. Once you have infiltrated " +
                "a city (especially a capital) you can use hierophant agents to influence its ruler's opinions. " +
                "Start wars by making a ruler obsessed with combat and ambition and then degrading relations " +
                "between nations; wars cause long-lasting devastation that weakens human societies. Civil wars " +
                "start when a city ruler disagrees with their sovereign - push the sovereign one way and the dukes " +
                "the other; ambitious dukes are especially prone to breaking away."),

            // god-specific
            Ctx("vinerva_seed", "Vinerva: seeds", "god", GodIsVinerva,
                "Vinerva Seeds expand to unreachable locations via the Heart of the Forest power.",
                "You are playing Vinerva. Vinerva Seeds let you expand to locations normal growth cannot reach - " +
                "across oceans or great deserts. Move an agent carrying a seed to the target location, then use " +
                "Vinerva's Heart of the Forest power there."),

            Ctx("vinerva_menace", "Vinerva: menace on your Hearts", "god", GodIsVinerva,
                "Harmful powers near your Hearts of the Forest raise menace; humanity sends armies to burn them.",
                "As Vinerva, using harmful powers around your Hearts of the Forest draws humanity's attention, and " +
                "they will eventually send armies to burn your trees. Each Heart of the Forest has its own menace " +
                "and a greatest-threat-against-it; human nations deploy armies when they judge that menace high " +
                "enough. Watch the menace around your Hearts and don't over-extend near them."),

            Ctx("iastur_regen", "Iastur: power regeneration", "god", GodIsLaughingKing,
                "Iastur regains power 50% faster, but only while the Laughing Tome is being read.",
                "You are playing Iastur, the Laughing King. Iastur regains power 50% faster than usual, but only " +
                "while the Laughing Tome is being read: the Tome must be in a person's inventory in its normal " +
                "(non-bound) state, or present in a location as the 'Laughing King's Tome' modifier. If the Tome " +
                "is not being read, your power does not regenerate - keep it in play."),

            CtxDyn("ophanim_faith", "Ophanim: faith growth", "god", GodIsOphanim, OphanimFaithBody,
                "Ophanim's Faith grows from fear of shadow - fastest from shadow in a location itself."),

            // faction / world existence
            Ctx("dark_empire", "The Dark Empire", "faction", HasDarkEmpire,
                "Your shadow's military force; its leader runs Dark Crusades. Re-crown via 'Dark Coronation'.",
                "A Dark Empire exists in the world - your shadow's main military force. Its leader (initially the " +
                "Monarch) can launch Dark Crusades to spread your shadow to new lands by conquest. If the Monarch " +
                "is killed, crown a new leader at the empire's capital with the 'Dark Coronation' challenge."),

            Ctx("deep_ones", "Deep Ones", "faction", HasDeepOnes,
                "A minor race you can ally with; nurture hidden cults to charge them toward taking locations.",
                "Deep Ones are active - a minor race you can ally with. Nurture their cults by keeping them hidden " +
                "from heroes, and you can curse human families into Deep Ones. Past 100% charge a cult spreads " +
                "shadow and madness in its location; at 300% it takes over the location and lets you transform " +
                "nearby humans into more Deep Ones, which count toward victory."),

            Ctx("elves", "Elves", "faction", HasElves,
                "Strong but brittle: shadow-resistant, yet a lost Wayfinder + sovereign causes a succession crisis.",
                "Elves are present. They resist shadow and disease and are strong in battle, but they are brittle: " +
                "their rulers must be appointed by a Wayfinder and their heroes created by a sovereign, so if they " +
                "lose both they fall into a succession crisis for years. Humanity can be turned against them by " +
                "religious tenet or politics. Their Crystalsmiths make anti-shadow crystals but can be driven " +
                "insane if the Arcane Secrets they rely on are corrupted into Dangerous Knowledge."),

            // ---------- REFERENCE-ONLY: available via get_tips, no automatic trigger ----------
            RefDyn("tags", "Tags (NPC motivation)", "politics",
                "Tags are likes/dislikes that add or subtract motivation for a task - the lever hierophants pull.",
                TagsBody),

            Ref("disrupting_skirmish", "Disrupting heroes by attacking", "tactics",
                "Attacking a hero interrupts it - use hit-and-run to pull heroes off agents or stall quests.",
                "Attacking a hero interrupts it: a hero that is attacked stops what it is doing and often goes to " +
                "heal before starting anything new. Exploit this - attack and retreat to pull a hero off one of " +
                "your agents or to stall a quest that threatens your plans. Agents who reduce the damage a hero " +
                "deals in combat (such as the Cursed) can do this repeatedly, surviving long enough to slip away."),

            Ref("low_magic", "Death magic scales with Death", "magic",
                "'Release from Death' is weak at low-Death locations; create death first, then exploit it.",
                "The 'Release from Death' challenge scales with how much Death is present at the location. At a " +
                "low-Death location the army it raises is weak. Death magic rewards set-up: deliberately create " +
                "death first (through plagues, famines, war, or disasters), then exploit it. Check a location's " +
                "Death level before relying on it."),

            Ref("magic", "Magic is optional (and provokes an arms race)", "magic",
                "Pursuing magic wakes the world's mages into an arms race for Arcane Secrets.",
                "Magic is mostly optional. If you never pursue magical research or magical agents, the world's " +
                "mages stay weak and cautious. But once they detect a new power gaining magical strength, they " +
                "start a magical arms race - racing to uncover Arcane Secrets to deny them to you and boost " +
                "themselves. Arcane Secrets are found in libraries, or produced by researching them for gold, by " +
                "the Plague Doctor's experiments, or by studying the souls of dying heroes."),

            Ref("magical_mastery", "Magical schools & channelling", "magic",
                "Schools need different sources; channelled spells apply menace/profile up front and drain the source.",
                "As you gain magical talent you master magical schools, each drawing on a different source: " +
                "geomancy needs geomantic loci, death magic needs recent death, blood magic needs personal items " +
                "or human souls. Many magical challenges are 'channelled' over several turns and can be detected at " +
                "range; channelling applies its menace and profile penalty at the START of casting, so interrupting " +
                "it early doesn't spare the agent. Hostile mages can strike your casters through geomantic loci " +
                "while they channel; your casters can spend some of a source's power to build an arcane fortress " +
                "for defence. Since a channelled spell drains its source, consider stopping once the source is depleted."),

            Ref("shadow_dead_parent", "A shadow whose parent died", "tactics",
                "It binds to the location where its parent died and loses no HP while standing there.",
                "A shadow whose parent hero has died does not have to vanish. It becomes bound to the location " +
                "where its parent died (unless it can slip into the shadows and roam free), and while it stands on " +
                "that location it doesn't lose HP. A dead parent is not the end of a shadow - just a tether."),

            Ref("income", "Gold from enshadowed rulers", "economy",
                "Once panic is high, fallen rulers fund agents whose home is their settlement.",
                "Gold matters for agents and heroes alike. Once world panic is high enough, rulers begin funding " +
                "heroes from their treasuries. Your agents can benefit too: an agent created in a human settlement " +
                "counts that place as home, and if that place's ruler falls into shadow they periodically fund your " +
                "agent. Enshadowing the right rulers turns their treasuries into your income."),

            Ref("orc_plunder", "Orc plunder", "economy",
                "Razing settlements sends plunder to the orc fortress; infiltrate it to access the gold.",
                "When an orc army or a warlord's raiding party razes a settlement, it carries a share of that " +
                "settlement's wealth back to its home fortress as plunder, based on population and prosperity. " +
                "Once you have infiltrated the fortress, that plunder is accessible to your agents. Raiders under " +
                "a warlord accumulate plunder faster than a plain army."),

            Ref("chosen_one_funding", "Chosen One funding", "economy",
                "Aware, unfallen nobles fund the Chosen One; enshadow or kill them to cut it off.",
                "Aware nobles who have not fallen into shadow will fund the Chosen One as their champion against " +
                "the coming darkness. As a result the Chosen One almost always has gold for the best minions and " +
                "items humanity can offer, making them a lethal opponent. Enshadowing or killing wealthy aware " +
                "nobles cuts off that funding."),

            Ref("alliance", "The Alliance", "faction",
                "Humanity's last stand - united, aware, and (by default) immune to shadow and infiltration.",
                "If humanity forms the Alliance, it is their last stand: politically united and finally aware, they " +
                "raise a shieldwall around their remaining cities and launch crusades to reclaim lost land and " +
                "destroy threats. Depending on game options the Alliance may be outright immune to shadow and " +
                "infiltration, or merely hardened against them with stronger armies and ruler-redeeming quests. " +
                "Allied nations also defend one another when either is attacked. Check whether your usual " +
                "subversion still works before relying on it against them."),
        };

        /// <summary>Case-insensitive lookup by id; null if unknown.</summary>
        public static TipDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (TipDef t in All)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        // ===================== triggers (grounded in real game accessors) =====================
        // Each mirrors the game's own hint condition where a clean one exists; each is null-guarded so a
        // menu/no-game context simply yields "not fired". See docs/ground-truth-notes.md.

        private static bool PanicRising(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            return m != null && m.worldPanic >= 0.1; // mirrors Map.popHint WORLD_PANIC threshold
        }

        private static bool AwarenessRising(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            // No canonical game threshold (the game's AWARENESS hint is event-driven); 0.1 is our design choice.
            return m != null && m.awarenessOfUnderground >= 0.1;
        }

        private static bool AnyWar(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            return m != null && m.wars != null && m.wars.Count > 0;
        }

        private static bool PerformingChallenge(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
                if (u != null && u.isCommandable() && u.task is Task_PerformChallenge) return true;
            return false;
        }

        // Fires when the player controls a military unit (a commandable UM) - an awakened god-army like
        // UM_SheWhoWillFeast, or a mid-game orc raiding party - whose raze/drive-back/attack orders are only
        // reachable via command_army. See Summaries.UnitOrders.
        private static bool HasCommandableArmy(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
                if (u is UM && !u.isDead && u.isCommandable()) return true;
            return false;
        }

        private static bool HighSecInfiltration(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
            {
                if (u == null || !u.isCommandable()) continue;
                // Mirrors Task_PerformChallenge's HIGH_SEC trigger: infiltrating & settlement security > 5.
                if (u.task is Task_PerformChallenge pc && pc.challenge is Ch_Infiltrate
                    && u.location != null && u.location.settlement != null
                    && u.location.settlement.getSecurity(null) > 5)
                    return true;
            }
            return false;
        }

        private static bool HasDarkEmpire(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.socialGroups == null) return false;
            foreach (SocialGroup sg in m.socialGroups)
                if (sg is Society s && s.isDarkEmpire) return true;
            return false;
        }

        private static bool HasDeepOnes(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null) return false;
            if (m.overmind != null && m.overmind.deepOnesRiseUp) return true;
            if (m.socialGroups != null)
                foreach (SocialGroup sg in m.socialGroups)
                    if (sg is SG_DeepOnes) return true;
            return false;
        }

        private static bool HasElves(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.socialGroups == null) return false;
            foreach (SocialGroup sg in m.socialGroups)
                if (sg is Soc_Elven) return true;
            return false;
        }

        private static bool GodIsVinerva(GameContext c) => GodOf(c) is God_Vinerva;
        private static bool GodIsOphanim(GameContext c) => GodOf(c) is God_Ophanim;
        // Iastur IS the Laughing King - there is no God_Iastur type.
        private static bool GodIsLaughingKing(GameContext c) => GodOf(c) is God_LaughingKing;

        private static God GodOf(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            return m != null && m.overmind != null ? m.overmind.god : null;
        }

        // ===================== dynamic bodies (interpolate live game params) =====================

        private static string TagsBody(GameContext c)
        {
            Map m = (c != null ? c.Map : null) ?? World.staticMap;
            if (m == null || m.param == null)
                return "Tags are concepts an NPC's AI has opinions on (their likes/dislikes). A liking or " +
                       "disliking toward a tag raises or lowers that NPC's motivation for a task; an extreme one " +
                       "shifts it more. Quests and actions carry positive and negative tags, and a negative tag " +
                       "applies the negative of its motivation. This is the lever hierophants pull to steer rulers.";
            var like = m.param.utility_person_FromLiking;
            var extreme = m.param.utility_person_FromExtremeLiking;
            return "Tags are concepts an NPC's AI has opinions on (their likes/dislikes). A liking or disliking " +
                   "toward a tag adds or subtracts " + like + " from that NPC's motivation for a task; an extreme " +
                   "liking or dislike shifts it by " + extreme + ". Quests and actions carry positive and negative " +
                   "tags: a negative tag applies the negative of its motivation (a 'cure disease' quest tags " +
                   "'disease' negatively, so a hero who dislikes disease gains +" + like + " motivation to do it). " +
                   "This is the lever hierophants pull to steer rulers.";
        }

        private static string OphanimFaithBody(GameContext c)
        {
            Map m = (c != null ? c.Map : null) ?? World.staticMap;
            if (m == null || m.param == null)
                return "You are playing Ophanim. Ophanim's Faith grows from fear of the shadow: people join it " +
                       "when they see shadow near them or in their own city - fastest from shadow in the location " +
                       "itself, slowest from world-wide shadow.";
            int world = (int)(100.0 * m.param.prop_opha_faithWorldShadowReq);
            int own = (int)(100.0 * m.param.prop_opha_faithOwnShadowReq);
            return "You are playing Ophanim. Ophanim's Faith grows from fear of the shadow: people join it when " +
                   "they see shadow near them or in their own city. Faith grows if the world is more than " + world +
                   "% enshadowed, if a location neighbours somewhere with at least 25% shadow, or if a location " +
                   "itself has at least " + own + "% shadow. It grows fastest from shadow in the location itself " +
                   "and slowest from world-wide shadow.";
        }
    }
}
