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
                "god; your agents are your hands in the world, weakening human societies while you wake. Seals " +
                "break on a fixed schedule - each raises your power cap, speeds power gain, and raises your " +
                "agent cap. Human heroes hunt and kill agents that grow too exposed. You act through agents " +
                "(the unit tools) and god powers (list_powers / use_power). Read game_overview every turn - " +
                "turn, resources, seal countdown, threats, and any pending decision."),

            Core("infiltration", "Infiltration", "infiltration",
                "The core loop; reduce a settlement's security (unrest, neighbours, enshadowed ruler) to speed it.",
                "Infiltration is the core loop and unlocks most challenges (list_challenges). Higher-security " +
                "settlements take longer - cities more than villages, capitals most of all. Reduce security " +
                "before or during: cause unrest (e.g. a famine - raid the farms and villages feeding it), " +
                "infiltrate the neighbours first, or enshadow the ruler (an enshadowed ruler welcomes your " +
                "cult in). Plan the reduction; don't just throw an agent at a capital."),

            Core("challenges", "Challenges, profile & menace", "basics",
                "Agents act by performing challenges, which build profile (detection range) and menace (threat).",
                "Agents mainly act by performing challenges (list_challenges on a unit, then perform_challenge). " +
                "Each takes several turns (agent skill vs complexity) and grants the agent profile (detection: " +
                "hero AI sees it within profile/10 hexes) and menace (how threatening heroes consider it). At " +
                "profile >= 50 and menace > 25 an agent is huntable (get_unit's combat.isHuntable) and rulers " +
                "send hunters - watch get_threats. Both stats are sticky: a floor ratchets up as the agent acts " +
                "and neither can drop below it, so don't build exposure you don't need; bleed them down with the " +
                "Lay Low challenge or In Hiding, or enshadow the local ruler (blind to menace). get_tips " +
                "id=menace / id=profile have the exact thresholds. Agents can also act on co-located agents " +
                "directly with command_agent (attack / rob / trade - see 'orders' in the unit views); attacking " +
                "cancels the target's in-progress ritual permanently (get_tips id=agent_can_attack)."),

            Core("recruitment", "Recruiting agents", "basics",
                "Need a roster slot (cap grows with seals) + a recruitment point; losing all agents is not a loss.",
                "Recruiting an agent needs a free roster slot (cap rises with seals) and a recruitment point " +
                "(gained over time): list_recruitable_agents, then recruit_agent. Archetypes specialise " +
                "(intrigue = infiltration and steering rulers, might/command = armies and combat, lore = " +
                "rituals and knowledge) - pick what fits the plan, not the first listed; each archetype's " +
                "placement.eligible and exampleTargets show where it can be enthralled right now. Losing all " +
                "your agents is NOT a loss - points regenerate; recruit again. The game truly ends only when " +
                "endOfGameAchieved is set."),

            Core("panic_vs_awareness", "Panic vs. awareness", "world",
                "worldPanic = vague dread from your actions; awareness = people who know the truth and fight you.",
                "Two world meters (both 0-1 fractions) track how much humanity notices you. worldPanic is vague " +
                "dread - people sense the world going wrong without knowing why; it rises mainly from god powers " +
                "and clues heroes discover. awarenessOfUnderground is people who KNOW the truth about you - they " +
                "act deliberately and fund the Chosen One. Both climb as you act; game_overview's panic block " +
                "shows the sources."),

            Core("victory", "Winning on points", "basics",
                "You win on victoryProgress toward pointsToWin (~200), not on any single meter.",
                "You win on points, not on any single meter: victoryProgress in game_overview is your weighted " +
                "score toward pointsToWin (200); get_victory_breakdown shows the split. Don't confuse it with " +
                "avrgEnshadowment (average shadow on rulers/heroes) or worldPanic - enshadowing helps, but the " +
                "score decides. Gods win differently; see get_player_state.progression."),

            // ---------- CONTEXTUAL: fire once when a live condition becomes true ----------
            Ctx("high_sec", "High-security infiltration", "infiltration", HighSecInfiltration,
                "An agent is infiltrating a settlement with security above 5 - reduce security to speed it.",
                "One of your agents is infiltrating a high-security settlement (security above 5). The higher the " +
                "security, the slower the infiltration - cities and especially capitals start very high. Speed it " +
                "up by reducing security first: start a famine by raiding the surrounding farmland and villages to " +
                "cut its food - when food drops below the population the city starves, and hunger becomes famine, " +
                "then unrest, which drags security and prosperity down - or infiltrate the surrounding locations, " +
                "or enshadow its ruler. Preparing the ground first is usually faster than grinding a raw " +
                "high-security infiltration."),

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

            Ctx("agent_exposed", "An agent is becoming huntable", "tactics", AgentBecomingExposed,
                "An agent's profile & menace have entered the danger band - heroes can hunt it and rulers may send assassins.",
                "One of your agents has built up profile and menace into the danger band (profile >= 40 and " +
                "menace >= 20; at profile >= 50 and menace > 25 it is outright huntable - see get_unit's " +
                "combat.isHuntable and get_threats). Hero AI can see and attack it within profile/10 hexes " +
                "(get_threats scans a wider profile/5 belt as early warning), and human rulers may order hunts " +
                "with no range limit. Options: pull it out of hunter range; run the Lay " +
                "Low challenge or leave it In Hiding (combat.inHiding) to bleed profile and menace toward their " +
                "floors; or enshadow the local ruler, who then ignores the menace. The floors themselves ratchet " +
                "up permanently and nothing resets them, so exposure management is preventive: act BEFORE the " +
                "floor rises, and treat a badly over-exposed veteran as your candidate for risky, high-value work " +
                "rather than expecting it to ever go quiet again. Acting before it is fully huntable is far " +
                "cheaper than reviving a hunted agent."),

            Ctx("agent_under_attack", "An agent is under attack - resolve the battle", "tactics", AgentUnderAttack,
                "An agent was attacked this turn and a battle is pending. Fight, flee, or retreat via get_pending_decision - it blocks end_turn.",
                "One of your agents has been reached by a hostile hero and a battle is now pending (game_overview." +
                "threats.agentsUnderAttack, and get_unit shows engagedThisTurn). This is a real decision, not " +
                "automatic: call get_pending_decision to open the combat menu, then resolve_decision to 'fight to " +
                "the end' (best when your dangerEstimate beats theirs), or 'flee'/'retreat'. Fleeing only unlocks " +
                "from round 2 - at round 2 you escape but lose ALL your minions; from round 3 the retreat is safe. " +
                "Winning lets you loot the loser. end_turn is blocked (even with force=true) until every pending " +
                "battle is resolved, so an agent can never sleepwalk into a fight it should have fled."),

            Ctx("agent_can_attack", "You can strike first - attacking breaks rituals", "tactics", HostileHeroOnAgentTile,
                "A hostile hero shares one of your agents' tiles: command_agent order=attack duels them, and cancels their ritual even if you flee.",
                "One of your agents is standing on the same tile as a hostile hero, which means you can attack " +
                "rather than wait to be attacked - see 'orders' in get_unit/list_units, and issue it with " +
                "command_agent order=\"attack\". The key mechanic: starting the battle cancels the target's " +
                "in-progress challenge or ritual OUTRIGHT, and it stays cancelled whether you win, flee, retreat, " +
                "or lose. That makes a deliberately-lost duel a legitimate way to break a ritual you cannot stop " +
                "any other way - most notably the Chosen One's. Two costs: your own agent's in-progress challenge " +
                "is cancelled too (pass force=true to accept losing its progress), and a hero being guarded " +
                "(Task_Bodyguard) cannot be touched until the guard is beaten. Compare get_unit combat." +
                "dangerEstimate on both sides before committing, and remember flee only unlocks from round 2. " +
                "The same tool covers the other on-tile agent actions: rob a weaker merchant or adventurer, and " +
                "trade items between two of your own agents."),

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

            Ctx("army_in_battle", "An army is fighting a field battle", "tactics", ArmyInBattle,
                "One of your armies is in a multi-turn field battle (inBattle). It auto-resolves each turn; watch it via get_unit.battle.",
                "One of your military units is locked in an army-vs-army field battle (list_units shows inBattle; " +
                "get_unit shows a 'battle' block with both sides, the command-advantage %, and this cycle's combat " +
                "log). Unlike an agent duel this resolves automatically, one cycle per turn - there is no menu to " +
                "drive and it does not block end_turn. You sway it indirectly: bring more armies to the tile " +
                "(command_army attack), or move an agent onto the battle's tile and perform 'Command Battle " +
                "(Attacking)' or 'Command Battle (Defending)' - these appear in list_challenges only while the " +
                "unit shares the battle's tile, and completing one adds command advantage to that side. " +
                "commandAdvantagePct's sign shows who is currently favoured."),

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
                "while the Laughing Tome is being read by mortal eyes: held unbound by a ruler or an independent " +
                "hero (your own agents' reading does not count), or present in a location as the 'Laughing " +
                "King's Tome' modifier. While it is unread, your power regenerates at only HALF the normal " +
                "rate - keep the Tome in play. Note: the game's own hint popup wrongly claims regen stops " +
                "entirely while the Tome is unread; the game code halves it. Trust this tip, not the popup."),

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
                "lose both they fall into a succession crisis for many months. Humanity can be turned against them by " +
                "religious tenet or politics. Their Crystalsmiths make anti-shadow crystals but can be driven " +
                "insane if the Arcane Secrets they rely on are corrupted into Dangerous Knowledge."),

            Ctx("holy_tenets", "A religion's doctrine can be rewritten", "faction", CanInfluenceHolyOrder,
                "A holy order has enough Elder influence to change a tenet - spend it, it stops accruing.",
                "One of the world's religions has filled its Elder influence bar, which lets you rewrite one " +
                "of its tenets with influence_holy_order_tenet (list_holy_orders {orderId} shows each tenet's " +
                "status, range and whether it is currently shiftable). This is the deepest lever you have over " +
                "a faith: darkened tenets make its temples spread shadow (Dark Worship), let its acolytes strip " +
                "wards (Candle Circles), turn its Healers into plague-spreaders, its Prophets of Doom into " +
                "madness engines, or - via The Feast - convert the entire faith into a vampire cult whose " +
                "acolytes raise the dead. Three rules decide the order you buy them in. First, an ordinary " +
                "tenet cannot be pushed darker while the order's 'Alignment Status' tenet sits at or above it, " +
                "so the opening purchases for a faith are almost always Alignment Status toward_elder (which " +
                "also enshadows its acolytes as it falls); only the three structural tenets (Dogmatic, " +
                "Preachers, Temple Builders) escape that gate. Second, spending " +
                "resets that order's Elder influence to 0, and the influence it earns is CAPPED at the " +
                "requirement - anything gained while the bar is already full is thrown away, so a change " +
                "deferred is influence burned. Third, raising the Dogmatic tenet multiplies the cost of every " +
                "later change to that faith, so darken it only deliberately. Elder influence itself grows from " +
                "enshadowing the order's settlements, and an agent can also fund an order to add half the cash " +
                "as influence."),

            Ctx("insane_heroes_hunt", "Insane heroes still hunt you", "tactics", AnyInsaneHero,
                "A hero has gone insane - madness is NOT pacification; insane heroes keep hunting your agents.",
                "A hero in the world has gone insane. Do not treat that as neutralised: madness does not " +
                "pacify a hero. An insane hero keeps its stats, minions and quest AI, and often keeps hunting " +
                "your agents at high motivation - the practical difference is who it also endangers (its own " +
                "side) and that human rulers may eventually execute it. If an insane hero is hunting one of " +
                "your agents, treat it exactly like a sane hunter: check get_threats, pull the agent out of " +
                "range or lie low, or kill the hero. Driving heroes mad is a tool for wrecking human society, " +
                "not a defence for an exposed agent."),

            Ctx("shadow_treadmill", "Heroes are erasing your shadow", "infiltration", ShadowTreadmill,
                "Shadow keeps getting driven back where you spread it - break the treadmill, don't out-grind it.",
                "Heroes have repeatedly driven back your shadow in the last stretch of turns (see " +
                "get_recent_events, type SHADOW_DRIVEN_BACK). Re-enshadowing the same places while cleansers " +
                "work is a treadmill: they remove it about as fast as you add it, and your agents pay profile " +
                "and menace for every pass while the heroes pay nothing. Break the loop instead: kill or " +
                "divert the cleansing heroes (attack them, give them worse problems elsewhere), spread shadow " +
                "somewhere they are not watching, or enshadow the local RULERS - heroes at 100% personal " +
                "shadow stop cleansing altogether, and an enshadowed ruler stops inviting the defence in."),

            Ctx("alliance_razing", "The Alliance razes what you corrupted", "faction", AllianceActive,
                "The Alliance destroys enshadowed settlements and executes insane rulers - your score dies with them.",
                "The Alliance is active, and it does not merely resist you - it AMPUTATES: enshadowed " +
                "settlements are razed and replaced with clean outposts, and insane or fallen rulers are " +
                "deposed or executed. Every razed settlement and every executed enshadowed ruler is score you " +
                "lose (check get_victory_breakdown for which qualifiers your points rest on). Near Alliance " +
                "territory, favour assets it cannot raze or redeem: kill its crusading armies before they " +
                "arrive, keep your highest-value enshadowed rulers defended or use them before they are " +
                "purged, and put new corruption far from the shieldwall rather than adjacent to it."),

            Ctx("iastur_soul", "Iastur: the Soul at the Tomb is a LOSS meter; win through victory points", "god", IasturSoulPresent,
                "Iastur's Soul (Tomb) only ever FALLS - 0% = defeat. The '300% = win' text is dead vanilla text; win via points.",
                "Your awakening has laid Iastur's Soul bare at the Elder Tomb (modifier starts at 100%). " +
                "The game's own message says reaching 300% wins the game - that text is WRONG (dead vanilla " +
                "text): no code path in the game ever RAISES the Soul charge, so treat it purely as a LOSS " +
                "meter. It falls only when a hero uses the bound Laughing Tome against the Tomb ('Tome " +
                "Used' messages; your unspent power reserves absorb the hit first), and at 0% Iastur dies " +
                "and you lose - so keep power banked as a shield, stop heroes from binding the tome, and " +
                "kill any binder heading for a library. Your actual win route is unchanged from the whole " +
                "game so far: the standard victory-points meter (game_overview victory progress). Waves of " +
                "Madness at the Tomb feeds THAT meter - each completed wave drives the nearest chunk of " +
                "rulers and heroes insane, and every insane ruler/hero scores victory points (roughly " +
                "double when also enshadowed) - its payoff appears in your points total, never in the Soul " +
                "modifier, which will sit unchanged at its current % after a successful wave. Each wave " +
                "also adds +40 menace and +40 profile to the performer (paid up front - it is channelled), " +
                "so escort or rotate performers. Do NOT abandon points-scoring elsewhere: waves are a " +
                "points accelerator, not a separate win track."),

            // ---------- REFERENCE-ONLY: available via get_tips, no automatic trigger ----------
            RefDyn("tags", "Tags (NPC motivation)", "politics",
                "Tags are likes/dislikes that add or subtract motivation for a task - the lever hierophants pull.",
                TagsBody),

            Ref("profile", "Profile (detection)", "infiltration",
                "Profile is detection range: hero AI sees the agent within profile/10 hexes; get_threats scans a wider profile/5 belt.",
                "Profile is how visible an agent is. Hero AI can see and choose to attack it within profile/10 " +
                "hexes; get_threats (and get_unit's combat.huntRadius) scan a wider profile/5 belt as early " +
                "warning, and a ruler's hunt order has no range limit at all - so a high-profile agent is " +
                "reachable from far away. (Challenge reach is separate: an agent sees a challenge whose own " +
                "profile/10 covers the distance to it, plus everything at its home location at any distance.) " +
                "Profile is built by performing challenges; channelled spells apply their whole " +
                "profile cost up front when casting begins, so interrupting one early does not spare the agent " +
                "(see get_tips id=magical_mastery). It is sticky: every gain also raises a minimum - at least a " +
                "third of the current value - that profile can never drop below (get_unit combat.profileFloor). " +
                "Reduce profile by lying low - the Lay Low challenge, or leaving the agent In Hiding. " +
                "An agent is huntable only at profile >= 50 AND menace > 25."),

            Ref("menace", "Menace (threat)", "tactics",
                "Menace is how much heroes, armies and nations want to attack - it crosses fixed thresholds.",
                "Menace is how threatening a target is considered, and it is the main term that makes something " +
                "worth attacking. For an agent it raises how strongly heroes that can detect it (see profile) want " +
                "to attack, crossing fixed thresholds: menace >= 20 with profile >= 20 gives the Infamous trait; " +
                "menace > 25 with profile >= 50 makes it huntable, so rulers send assassins (get_unit's " +
                "combat.isHuntable); menace >= 40 with profile >= 30 lets a human army block and attack it " +
                "mid-challenge. You can only Redress Crimes (pay gold to cut menace) while menace is under 20. " +
                "Enshadowed rulers are blind to menace - their urge to attack is scaled by (1 - their shadow) - so " +
                "enshadowing the local ruler shields a menacing agent. Menace is sticky: a floor ratchets up with " +
                "every gain and it can never drop below it (get_unit combat.menaceFloor); lower it by lying low " +
                "(Lay Low or In Hiding). Nations and cults " +
                "carry their own menace too: a society's menace draws crusades and wars against it, and a " +
                "subsettlement's menace draws raids and razing armies - watch it on get_social_group and location detail."),

            Ref("enshadow_home", "Corrupting heroes via their home", "infiltration",
                "Heroes gain shadow resting in enshadowed locations - enshadow a hero's home to corrupt it slowly.",
                "Heroes recover by resting in their home location, and while they rest somewhere enshadowed they " +
                "take on shadow. So enshadowing a hero's home settlement is a slow, hands-off way to corrupt that " +
                "hero - useful against ones too strong or too distant to attack directly. A fully shadowed hero " +
                "can then be enthralled as your agent (see list_recruitable_agents' corruptible heroes)."),

            Ref("disrupting_skirmish", "Disrupting heroes by attacking", "tactics",
                "Attacking a hero interrupts it - use hit-and-run to pull heroes off agents or stall quests.",
                "Attacking a hero interrupts it: a hero that is attacked stops what it is doing and often goes to " +
                "heal before starting anything new. Exploit this - attack and retreat to pull a hero off one of " +
                "your agents or to stall a quest that threatens your plans. Agents who reduce the damage a hero " +
                "deals in combat (such as the Cursed) can do this repeatedly, surviving long enough to slip away. " +
                "Combat resolves in a fixed order - attacker, then defender, then the attacker's minions, then the " +
                "defender's minions - and each deals its stated attack as unrandomised damage, so you can predict " +
                "a skirmish before committing (compare the two sides' dangerEstimate on get_unit)."),

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

        // Fires when one of your agents has built profile/menace into the danger band - approaching the
        // profile>=50 & menace>25 huntable threshold that ComputeAgentSafety and human rulers use. 40/20 is an
        // early-warning choice (like AwarenessRising's 0.1) so it fires before the agent is fully huntable.
        private static bool AgentBecomingExposed(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
                if (u is UA && !u.isDead && u.isCommandable() && u.profile >= 40.0 && u.menace >= 20.0)
                    return true;
            return false;
        }

        // Fires when one of your agents was attacked this turn - engagedBy set and turnLastEngaged == turn (the
        // fight-icon condition, UIE_AgentRoster.bFight). A battle is pending; AgentCombatDecision surfaces it.
        private static bool AgentUnderAttack(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
                if (u is UA && !u.isDead && u.isCommandable() && u.engagedBy != null && u.turnLastEngaged == m.turn)
                    return true;
            return false;
        }

        // Fires when one of your agents shares a tile with a hostile hero - i.e. the attack action box the game
        // would draw (UIScroll_Unit walks ua.location.units for non-commandable UAs). The pre-emptive strike is
        // available right now, and with it the break-their-ritual trick.
        private static bool HostileHeroOnAgentTile(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
            {
                UA ua = u as UA;
                if (ua == null || ua.isDead || !ua.isCommandable()) continue;
                if (ua.location == null || ua.location.units == null) continue;
                if (ua.engagedBy != null && ua.turnLastEngaged == m.turn) continue; // already the combat tip's case
                foreach (Unit other in ua.location.units)
                {
                    UA hero = other as UA;
                    if (hero != null && hero != ua && !hero.isDead && !hero.isCommandable()) return true;
                }
            }
            return false;
        }

        // Fires when one of your armies is in a multi-turn field battle (Task_InBattle → BattleArmy).
        private static bool ArmyInBattle(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
                if (u is UM && !u.isDead && u.isCommandable() && u.task is Task_InBattle) return true;
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

        /// <summary>Any religion whose banked Elder influence has reached its requirement, i.e. a tenet
        /// change is available right now (HolyOrder.turnTick raises the same one-off in-game message).</summary>
        private static bool CanInfluenceHolyOrder(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.socialGroups == null) return false;
            foreach (SocialGroup sg in m.socialGroups)
            {
                HolyOrder ho = sg as HolyOrder;
                if (ho == null) continue;
                try { if (ho.influenceElder >= ho.influenceElderReq) return true; } catch { }
            }
            return false;
        }

        // Fires when any living hero (a non-commandable UA) has gone insane (Person.isInsane). Insane
        // heroes keep their quest AI and often keep hunting agents - the trap the tip warns about.
        private static bool AnyInsaneHero(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.units == null) return false;
            foreach (Unit u in m.units)
            {
                UA hero = u as UA;
                if (hero == null || hero.isDead || hero.isCommandable()) continue;
                try { if (hero.person != null && hero.person.isInsane()) return true; } catch { }
            }
            return false;
        }

        // Fires when heroes have driven shadow back 3+ times inside the last 20 turns - read from the
        // mod's own event log, since no single-turn game accessor can express "this keeps happening".
        private static bool ShadowTreadmill(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || c.Events == null) return false;
            return c.Events.CountSince("SHADOW_DRIVEN_BACK", m.turn - 20) >= 3;
        }

        // Fires when the Alliance has formed (complements the reference-only 'alliance' tip: this one is
        // about what the Alliance does to score qualifiers you already banked).
        private static bool AllianceActive(GameContext c)
        {
            Map m = c != null ? c.Map : null;
            if (m == null || m.socialGroups == null) return false;
            foreach (SocialGroup sg in m.socialGroups)
                if (sg is Society s && s.isAlliance) return true;
            return false;
        }

        // Fires once Iastur's awakening has laid the Soul bare at the Elder Tomb - the Pr_Iastur property
        // exists nowhere until God_LaughingKing's awakening places it, so its presence IS the endgame signal.
        private static bool IasturSoulPresent(GameContext c)
        {
            if (!GodIsLaughingKing(c)) return false;
            Map m = c.Map;
            if (m == null || m.locations == null) return false;
            foreach (Location l in m.locations)
            {
                if (l == null || l.properties == null) continue;
                foreach (Property p in l.properties)
                    if (p is Pr_Iastur) return true;
            }
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
