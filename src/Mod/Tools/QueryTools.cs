using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tools.Decisions;

namespace ShadowsMcp.Tools
{
    /// <summary>Read-only tools over the live game state. All handlers run on the main thread.</summary>
    public static class QueryTools
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 500;

        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.Register(new ToolDefinition(
                "game_overview",
                "High-level state of the current game: turn, your god, resources, threat levels, world counts.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    int commandable = 0;
                    foreach (Unit u in map.units)
                    {
                        if (u.isCommandable()) commandable++;
                    }
                    Overmind om = map.overmind;
                    om.calculateAgentsUsed();
                    int agentCap = om.god != null ? om.getAgentCap() : 0;
                    bool canRecruit = om.availableEnthrallments > 0 && om.nEnthralled < agentCap;
                    JsonValue o = JsonValue.NewObject()
                        .Set("modVersion", ModCore.ModVersion)
                        .Set("turn", map.turn)
                        .Set("god", JsonValue.NewObject()
                            .Set("name", map.overmind.god != null ? map.overmind.god.getName() : null)
                            .Set("type", map.overmind.god != null ? map.overmind.god.GetType().Name : null))
                        .Set("power", Summaries.Round2(map.overmind.power))
                        .Set("victoryProgress", Summaries.Round2(map.data_victoryProgess))
                        .Set("worldPanic", Summaries.Round2(map.worldPanic))
                        .Set("awarenessOfUnderground", Summaries.Round2(map.awarenessOfUnderground))
                        .Set("sealsBroken", map.overmind.sealsBroken)
                        .Set("availableEnthrallments", map.overmind.availableEnthrallments)
                        // Losing all agents is NOT a loss (you are the god, points regenerate); recruit more
                        // with recruit_agent. The game truly ends only when endOfGameAchieved is set.
                        .Set("agentCap", agentCap)
                        .Set("canRecruit", canRecruit)
                        .Set("endOfGameAchieved", om.endOfGameAchieved)
                        .Set("defeated", om.endOfGameAchieved && !om.victoryAchieved)
                        .Set("victoryAchieved", map.overmind.victoryAchieved)
                        // null unless the game is waiting on a decision popup; otherwise the full detail
                        // (options with indices) so you can resolve it without loading get_pending_decision:
                        // pass the chosen index to end_turn's resolveOptionIndex (or resolve_decision).
                        .Set("pendingDecision", PendingDecisionForOverview(ctx))
                        .Set("counts", JsonValue.NewObject()
                            .Set("locations", map.locations.Count)
                            .Set("units", map.units.Count)
                            .Set("commandableUnits", commandable)
                            .Set("persons", map.persons.Count)
                            .Set("socialGroups", map.socialGroups.Count));
                    return ToolResult.Ok(o);
                })));

            host.Register(new ToolDefinition(
                "get_threats",
                "Current threats and opportunities, mirroring the game's built-in Threats panel: "
                + "heroes moving to attack your agents, the most-inclined attacker per agent (with "
                + "motivation %), the Chosen One's prophecy progress, seal/Iastur rituals, incoming "
                + "wars and holy-order mood. Sorted by severity (highest first).",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    var threats = new List<MsgEvent>(map.overmind.getThreats());
                    // Highest priority first; MsgEvent.priority carries the severity.
                    threats.Sort((x, y) => y.priority.CompareTo(x.priority));
                    JsonValue arr = JsonValue.NewArray();
                    foreach (MsgEvent e in threats) arr.Add(Summaries.ThreatEvent(ctx, e));
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("count", threats.Count)
                        .Set("threats", arr));
                })));

            host.Register(new ToolDefinition(
                "list_locations",
                "List locations on the world map, with owner, settlement, units present and neighbours. Paginated.",
                Schema.Object(
                    Schema.Prop("nameFilter", Schema.String("Case-insensitive substring match on the location name")),
                    Schema.Prop("socialGroupId", Schema.String("Only locations owned by this social group (e.g. SG3)")),
                    Schema.Prop("limit", Schema.Integer("Max results (default " + DefaultLimit + ")")),
                    Schema.Prop("offset", Schema.Integer("Skip this many results"))),
                a => WithMap(ctx, map =>
                {
                    string nameFilter = a["nameFilter"].AsString();
                    SocialGroup socFilter = null;
                    if (!a["socialGroupId"].IsNull)
                    {
                        socFilter = Summaries.ResolveId(ctx, a["socialGroupId"].AsString()) as SocialGroup;
                        if (socFilter == null) return ToolResult.Error("unknown social group id: " + a["socialGroupId"].AsString());
                    }
                    var matches = new List<Location>();
                    foreach (Location l in map.locations)
                    {
                        if (socFilter != null && l.soc != socFilter) continue;
                        if (nameFilter != null)
                        {
                            string n;
                            try { n = l.getName(); }
                            catch { n = ""; }
                            if (n.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        matches.Add(l);
                    }
                    return Paginate(a, matches, l => Summaries.LocationSummary(ctx, l));
                })));

            host.Register(new ToolDefinition(
                "get_location",
                "Full detail for one location: settlement, properties, units, ruler, hex coordinates, neighbours.",
                Schema.Object(Schema.Prop("locationId", Schema.String("Location id, e.g. L3"), required: true)),
                a => WithMap(ctx, map =>
                {
                    Location l = Summaries.ResolveId(ctx, a["locationId"].AsString()) as Location;
                    if (l == null) return ToolResult.Error("unknown location id: " + a["locationId"].AsString());
                    return ToolResult.Ok(Summaries.LocationDetail(ctx, l));
                })));

            host.Register(new ToolDefinition(
                "list_units",
                "List units. Default scope 'mine' = your commandable agents. Paginated.",
                Schema.Object(
                    Schema.Prop("scope", Schema.StringEnum("Filter: mine (default), agents, military, all, hostileToMe (units hunting/disrupting a shadow-aligned unit you benefit from - your own agents or allied evil units such as orc upstarts; mirrors get_threats)", "mine", "agents", "military", "all", "hostileToMe")),
                    Schema.Prop("socialGroupId", Schema.String("Only units of this social group (e.g. SG3)")),
                    Schema.Prop("limit", Schema.Integer("Max results (default " + DefaultLimit + ")")),
                    Schema.Prop("offset", Schema.Integer("Skip this many results"))),
                a => WithMap(ctx, map =>
                {
                    string scope = a["scope"].AsString("mine");
                    SocialGroup socFilter = null;
                    if (!a["socialGroupId"].IsNull)
                    {
                        socFilter = Summaries.ResolveId(ctx, a["socialGroupId"].AsString()) as SocialGroup;
                        if (socFilter == null) return ToolResult.Error("unknown social group id: " + a["socialGroupId"].AsString());
                    }
                    var matches = new List<Unit>();
                    foreach (Unit u in map.units)
                    {
                        if (u.isDead) continue;
                        if (socFilter != null && u.society != socFilter) continue;
                        switch (scope)
                        {
                            case "mine": if (!u.isCommandable()) continue; break;
                            case "agents": if (!(u is UA)) continue; break;
                            case "military": if (!(u is UM)) continue; break;
                            case "all": break;
                            case "hostileToMe": if (!IsHostileToMe(u)) continue; break;
                            default: return ToolResult.Error("invalid scope: " + scope);
                        }
                        matches.Add(u);
                    }
                    return Paginate(a, matches, u => Summaries.UnitSummary(ctx, u));
                })));

            host.Register(new ToolDefinition(
                "get_unit",
                "Full detail for one unit: person, task, menace/profile, rituals it can perform.",
                Schema.Object(Schema.Prop("unitId", Schema.String("Unit id, e.g. U17"), required: true)),
                a => WithMap(ctx, map =>
                {
                    Unit u = Summaries.ResolveId(ctx, a["unitId"].AsString()) as Unit;
                    if (u == null) return ToolResult.Error("unknown or stale unit id: " + a["unitId"].AsString() + " - re-run list_units");
                    return ToolResult.Ok(Summaries.UnitDetail(ctx, u));
                })));

            host.Register(new ToolDefinition(
                "list_persons",
                "List people (rulers, nobles, agents' hosts...). Paginated.",
                Schema.Object(
                    Schema.Prop("scope", Schema.StringEnum("Filter: alive (default), rulers, all", "alive", "rulers", "all")),
                    Schema.Prop("socialGroupId", Schema.String("Only members of this social group (e.g. SG3)")),
                    Schema.Prop("nameFilter", Schema.String("Case-insensitive substring match on the person's name")),
                    Schema.Prop("limit", Schema.Integer("Max results (default " + DefaultLimit + ")")),
                    Schema.Prop("offset", Schema.Integer("Skip this many results"))),
                a => WithMap(ctx, map =>
                {
                    string scope = a["scope"].AsString("alive");
                    string nameFilter = a["nameFilter"].AsString();
                    SocialGroup socFilter = null;
                    if (!a["socialGroupId"].IsNull)
                    {
                        socFilter = Summaries.ResolveId(ctx, a["socialGroupId"].AsString()) as SocialGroup;
                        if (socFilter == null) return ToolResult.Error("unknown social group id: " + a["socialGroupId"].AsString());
                    }
                    var matches = new List<Person>();
                    foreach (Person p in map.persons)
                    {
                        if (socFilter != null && (SocialGroup)p.society != socFilter) continue;
                        switch (scope)
                        {
                            case "alive": if (p.isDead) continue; break;
                            case "rulers": if (p.isDead || p.rulerOf < 0) continue; break;
                            case "all": break;
                            default: return ToolResult.Error("invalid scope: " + scope);
                        }
                        if (nameFilter != null)
                        {
                            string n;
                            try { n = p.getFullName(); }
                            catch { n = ""; }
                            if (n.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        matches.Add(p);
                    }
                    return Paginate(a, matches, p => Summaries.PersonSummary(ctx, p));
                })));

            host.Register(new ToolDefinition(
                "get_person",
                "Full detail for one person: stats, traits, items, sanity, shadow, rulership.",
                Schema.Object(Schema.Prop("personId", Schema.String("Person id, e.g. P42"), required: true)),
                a => WithMap(ctx, map =>
                {
                    Person p = Summaries.ResolveId(ctx, a["personId"].AsString()) as Person;
                    if (p == null) return ToolResult.Error("unknown person id: " + a["personId"].AsString());
                    return ToolResult.Ok(Summaries.PersonDetail(ctx, p));
                })));

            host.Register(new ToolDefinition(
                "list_social_groups",
                "List all societies and factions with their wars and military strength.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    JsonValue arr = JsonValue.NewArray();
                    foreach (SocialGroup sg in map.socialGroups)
                    {
                        arr.Add(Summaries.SocialGroupSummary(ctx, sg));
                    }
                    return ToolResult.Ok(JsonValue.NewObject().Set("total", map.socialGroups.Count).Set("items", arr));
                })));

            host.Register(new ToolDefinition(
                "get_social_group",
                "Full detail for one social group: relations, wars, capital, sovereign.",
                Schema.Object(Schema.Prop("socialGroupId", Schema.String("Social group id, e.g. SG3"), required: true)),
                a => WithMap(ctx, map =>
                {
                    SocialGroup sg = Summaries.ResolveId(ctx, a["socialGroupId"].AsString()) as SocialGroup;
                    if (sg == null) return ToolResult.Error("unknown social group id: " + a["socialGroupId"].AsString());
                    return ToolResult.Ok(Summaries.SocialGroupDetail(ctx, sg));
                })));

            host.Register(new ToolDefinition(
                "get_player_state",
                "Your god, power resource, agents, enthrallment capacity, victory progress and powers.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    Overmind om = map.overmind;
                    om.calculateAgentsUsed();
                    int agentCap = om.god != null ? om.getAgentCap() : 0;
                    JsonValue agents = JsonValue.NewArray();
                    foreach (Unit u in om.agents)
                    {
                        if (u != null && !u.isDead) agents.Add(Summaries.UnitSummary(ctx, u));
                    }
                    JsonValue powers = JsonValue.NewArray();
                    if (om.god != null)
                    {
                        List<Power> list = om.god.getPowers();
                        for (int i = 0; i < list.Count; i++)
                        {
                            powers.Add(Summaries.PowerSummary(map, list[i], i));
                        }
                    }
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("god", om.god != null ? om.god.getName() : null)
                        .Set("power", Summaries.Round2(om.power))
                        .Set("sealsBroken", om.sealsBroken)
                        .Set("sealProgress", om.sealProgress)
                        .Set("availableEnthrallments", om.availableEnthrallments)
                        .Set("enthralledCount", om.nEnthralled)
                        .Set("agentCap", agentCap)
                        .Set("canRecruit", om.availableEnthrallments > 0 && om.nEnthralled < agentCap)
                        .Set("endOfGameAchieved", om.endOfGameAchieved)
                        .Set("victoryMode", om.endOfGameAchieved ? Summaries.VictoryModeLabel(om.victoryMode) : null)
                        .Set("victoryProgress", Summaries.Round2(map.data_victoryProgess))
                        .Set("agents", agents)
                        .Set("powers", powers));
                })));

            host.Register(new ToolDefinition(
                "list_recruitable_agents",
                "What you can recruit right now: your recruitment capacity, the agent archetypes you can " +
                "enthrall onto a location (pass an archetype's code to recruit_agent with a target locationId), " +
                "and any existing heroes corrupted enough to turn to your side in place (pass their unit id " +
                "as recruit_agent's heroUnitId). Recruiting spends one recruitment point.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    Overmind om = map.overmind;
                    if (om.god == null) return ToolResult.Error("no god selected yet");
                    om.calculateAgentsUsed();
                    int cap = om.getAgentCap();

                    JsonValue archetypes = JsonValue.NewArray();
                    foreach (UAE_Abstraction ab in om.agentsGeneric) archetypes.Add(Summaries.AbstractionSummary(ab, "generic"));
                    foreach (UAE_Abstraction ab in om.agentsUnique) archetypes.Add(Summaries.AbstractionSummary(ab, "unique"));

                    JsonValue heroes = JsonValue.NewArray();
                    foreach (Unit u in map.units)
                    {
                        if (!Summaries.IsCorruptibleHero(u)) continue;
                        heroes.Add(JsonValue.NewObject()
                            .Set("unit", Summaries.UnitRef(ctx, u))
                            .Set("location", Summaries.LocationRef(u.location))
                            .Set("shadow", Summaries.Round2(u.person.shadow))
                            .Set("insane", u.person.isInsane()));
                    }

                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("capacity", JsonValue.NewObject()
                            .Set("availableEnthrallments", om.availableEnthrallments)
                            .Set("nEnthralled", om.nEnthralled)
                            .Set("agentCap", cap)
                            .Set("canRecruit", om.availableEnthrallments > 0 && om.nEnthralled < cap))
                        .Set("archetypes", archetypes)
                        .Set("corruptibleHeroes", heroes));
                })));

            host.Register(new ToolDefinition(
                "list_powers",
                "Your god's powers with cost and whether each is castable right now.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    if (map.overmind.god == null) return ToolResult.Error("no god selected yet");
                    JsonValue powers = JsonValue.NewArray();
                    List<Power> list = map.overmind.god.getPowers();
                    for (int i = 0; i < list.Count; i++)
                    {
                        powers.Add(Summaries.PowerSummary(map, list[i], i));
                    }
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("power", Summaries.Round2(map.overmind.power))
                        .Set("items", powers));
                })));

            host.Register(new ToolDefinition(
                "list_challenges",
                "Challenges and rituals available to one of your units. Optionally list another location's challenges to plan a move.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Unit id, e.g. U17"), required: true),
                    Schema.Prop("locationId", Schema.String("Look at this location instead of the unit's current one"))),
                a => WithMap(ctx, map =>
                {
                    Unit u = Summaries.ResolveId(ctx, a["unitId"].AsString()) as Unit;
                    if (u == null) return ToolResult.Error("unknown or stale unit id: " + a["unitId"].AsString() + " - re-run list_units");
                    Location loc = u.location;
                    if (!a["locationId"].IsNull)
                    {
                        loc = Summaries.ResolveId(ctx, a["locationId"].AsString()) as Location;
                        if (loc == null) return ToolResult.Error("unknown location id: " + a["locationId"].AsString());
                    }

                    // Refresh the location's challenge list the same way the game UI does.
                    loc.populateStandardChallenges();

                    JsonValue arr = JsonValue.NewArray();
                    foreach (Challenge c in loc.GetChallenges())
                    {
                        arr.Add(Summaries.ChallengeSummary(ctx, c, u));
                    }
                    JsonValue rituals = JsonValue.NewArray();
                    if (u.rituals != null)
                    {
                        foreach (Challenge r in u.rituals)
                        {
                            rituals.Add(Summaries.ChallengeSummary(ctx, r, u));
                        }
                    }
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("location", Summaries.LocationRef(loc))
                        .Set("challenges", arr)
                        .Set("unitRituals", rituals));
                })));
        }

        // ---------- helpers ----------

        /// <summary>
        /// The full pending-decision object for game_overview (null when nothing is pending), tagged with
        /// a hint on how to answer it without loading a separate tool: pass the chosen option index to
        /// end_turn's resolveOptionIndex.
        /// </summary>
        private static JsonValue PendingDecisionForOverview(GameContext ctx)
        {
            JsonValue pd = DecisionRegistry.FullOrNull(ctx);
            if (!pd.IsNull)
                pd.Set("resolveHint", "pick an option by its index: call end_turn with resolveOptionIndex " +
                    "(or resolve_decision with optionIndex). force=true skips/dismisses where allowed.");
            return pd;
        }

        /// <summary>True when this unit's current task targets a shadow-aligned unit you benefit from —
        /// a commandable unit (your agent) OR any UAE (evil agents on your side, including player-seeded
        /// orc upstarts) — i.e. it is hunting or disrupting your interests. Mirrors the exact target test
        /// in Overmind.getThreats() (`target.isCommandable() || target is UAE`), which is why an attack on
        /// an orc upstart also qualifies.</summary>
        private static bool IsHostileToMe(Unit u)
        {
            Task_AttackUnit attack = u.task as Task_AttackUnit;
            if (attack != null && attack.target != null)
                return attack.target.isCommandable() || attack.target is UAE;
            Task_DisruptUA disrupt = u.task as Task_DisruptUA;
            if (disrupt != null && disrupt.other != null)
                return disrupt.other.isCommandable() || disrupt.other is UAE;
            return false;
        }

        internal static ToolResult WithMap(GameContext ctx, Func<Map, ToolResult> body)
        {
            Map map = ctx.Map;
            if (map == null)
                return ToolResult.Error("No game in progress - start or load a game first.");
            return body(map);
        }

        private static ToolResult Paginate<T>(JsonValue args, List<T> matches, Func<T, JsonValue> render)
        {
            int limit = args["limit"].AsInt(DefaultLimit);
            if (limit < 1) limit = 1;
            if (limit > MaxLimit) limit = MaxLimit;
            int offset = args["offset"].AsInt(0);
            if (offset < 0) offset = 0;

            JsonValue items = JsonValue.NewArray();
            for (int i = offset; i < matches.Count && i < offset + limit; i++)
            {
                items.Add(render(matches[i]));
            }
            JsonValue result = JsonValue.NewObject()
                .Set("total", matches.Count)
                .Set("offset", offset)
                .Set("items", items);
            if (offset + limit < matches.Count) result.Set("nextOffset", offset + limit);
            return ToolResult.Ok(result);
        }
    }
}
