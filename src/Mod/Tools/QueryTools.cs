using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tips;
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
                "High-level state of the current game: turn, god, resources, world counts, seal countdown and " +
                "a threats breadcrumb. idEpoch: unit ids (U*) reset whenever this number changes (new game or " +
                "loaded save) - discard cached U-ids and re-run list_units; L*/P*/SG* ids survive loads. " +
                "The world meters (victoryProgress toward 200 points, worldPanic, " +
                "awarenessOfUnderground, avrgEnshadowment) are 0-1 fractions. threats.agentsUnderAttack = a " +
                "battle is pending (blocks end_turn; resolve via pendingDecision); agentsInDanger = a hero is " +
                "closing; agentsHuntable = exposed to assassination (profile>=50 & menace>25) - open " +
                "get_threats when a danger signal is non-zero. A 'tips' array may explain a mechanic the " +
                "moment it becomes relevant (get_tips is the full reference).",
                Schema.Object(),
                a => WithMap(ctx, map => ToolResult.Ok(OverviewJson(ctx, map)))));

            host.Register(new ToolDefinition(
                "get_threats",
                "THE per-turn risk check - call it before ending a turn whenever an agent is in the field. "
                + "Mirrors the game's Threats panel (heroes moving to attack, most-inclined attacker per "
                + "agent with motivation %, prophecy progress, seal/Iastur rituals, incoming wars, "
                + "holy-order mood; sorted by severity) PLUS agentSafety: per agent its dangerEstimate, "
                + "isHuntable, top hunter and verdict (favoured/even/outmatched) - the will-it-survive "
                + "picture that lets you hide or retreat before it dies.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    var threats = new List<MsgEvent>(map.overmind.getThreats());
                    // Highest priority first; MsgEvent.priority carries the severity.
                    threats.Sort((x, y) => y.priority.CompareTo(x.priority));
                    JsonValue arr = JsonValue.NewArray();
                    foreach (MsgEvent e in threats) arr.Add(Summaries.ThreatEvent(ctx, e));
                    // Structured per-agent combat odds (who is hunting each agent, motivation %, and a
                    // strength verdict from danger estimates) - the "will my agent die" picture.
                    var safety = Summaries.ComputeAgentSafety(ctx, map);
                    JsonValue safetyArr = JsonValue.NewArray();
                    foreach (var s in safety) safetyArr.Add(Summaries.AgentSafetyJson(ctx, s));
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("count", threats.Count)
                        .Set("threats", arr)
                        .Set("agentSafety", safetyArr));
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
                "world_summary",
                "The whole map in one call - the strategic picture list_locations/get_location only give one "
                + "hex at a time. Every location returns its coords, owner and (if settled) the settlement "
                + "essentials: type, shadow, defences, population, infiltration fraction + per-district "
                + "infiltrated state, capital flag, and neighbour ids. Defaults to settled locations; pass "
                + "settlementsOnly=false for all hexes. Filter by owner (socialGroupId) or minShadow, page "
                + "with limit/offset.",
                Schema.Object(
                    Schema.Prop("settlementsOnly", Schema.Boolean("Only locations that have a settlement (default true)")),
                    Schema.Prop("socialGroupId", Schema.String("Only locations owned by this social group, e.g. SG3")),
                    Schema.Prop("minShadow", Schema.Number("Only settlements with shadow >= this (0..1)")),
                    Schema.Prop("limit", Schema.Integer("Max results (default " + DefaultLimit + ")")),
                    Schema.Prop("offset", Schema.Integer("Skip this many results"))),
                a => WithMap(ctx, map =>
                {
                    bool settlementsOnly = a["settlementsOnly"].IsNull || a["settlementsOnly"].AsBool();
                    SocialGroup socFilter = null;
                    if (!a["socialGroupId"].IsNull)
                    {
                        socFilter = Summaries.ResolveId(ctx, a["socialGroupId"].AsString()) as SocialGroup;
                        if (socFilter == null) return ToolResult.Error("unknown social group id: " + a["socialGroupId"].AsString());
                    }
                    double minShadow = a["minShadow"].AsDouble(-1.0);
                    var matches = new List<Location>();
                    foreach (Location l in map.locations)
                    {
                        if (l == null) continue;
                        if (settlementsOnly && l.settlement == null) continue;
                        if (socFilter != null && l.soc != socFilter) continue;
                        if (minShadow >= 0.0)
                        {
                            if (l.settlement == null) continue;
                            if (l.settlement.shadow < minShadow) continue;
                        }
                        matches.Add(l);
                    }
                    return Paginate(a, matches, l => Summaries.WorldSummaryRow(ctx, l));
                })));

            host.Register(new ToolDefinition(
                "list_units",
                "List units. Default scope 'mine' = your commandable agents. Paginated. A commandable unit " +
                "carries an 'orders' array when a special order (command_army / command_agent) is available on " +
                "its tile; the response-level ordersLegend explains the calls once (get_unit has per-entry hints).",
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
                    // Rows carry hint-less orders; ONE legend per response gives the call templates
                    // (get_unit keeps the full per-entry hints as the drill-in).
                    bool anyOrders = false;
                    JsonValue payload = PaginatePayload(a, matches, u =>
                    {
                        JsonValue row = Summaries.UnitSummary(ctx, u, orderHints: false);
                        if (!row["orders"].IsNull) anyOrders = true;
                        return row;
                    });
                    if (anyOrders)
                    {
                        string legend = Boilerplate.Emit(ctx, "ordersLegend", Summaries.OrdersLegend,
                            "orders legend shown earlier; get_unit carries full per-order call hints");
                        if (legend != null) payload.Set("ordersLegend", legend);
                    }
                    return ToolResult.Ok(payload);
                })));

            host.Register(new ToolDefinition(
                "get_unit",
                "Full detail for one unit: person, task, menace/profile, rituals it can perform, and (for a " +
                "commandable military unit) an 'orders' array of army commands - raze/drive_back/attack - issued " +
                "via command_army.",
                Schema.Object(Schema.Prop("unitId", Schema.String("Unit id, e.g. U17"), required: true)),
                a => WithMap(ctx, map =>
                {
                    Unit u = Summaries.ResolveId(ctx, a["unitId"].AsString()) as Unit;
                    if (u == null) return ToolResult.Error(StaleUnitIdError(ctx, a["unitId"].AsString()));
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
                        if (u != null && !u.isDead) agents.Add(Summaries.UnitSummary(ctx, u, orderHints: false));
                    }
                    JsonValue powers = JsonValue.NewArray();
                    if (om.god != null)
                    {
                        List<Power> list = om.god.getPowers();
                        for (int i = 0; i < list.Count; i++)
                        {
                            powers.Add(Summaries.PowerSummary(map, list[i]));
                        }
                    }
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("god", om.god != null ? om.god.getName() : null)
                        .Set("power", Summaries.Round2(om.power))
                        .Set("sealsBroken", om.sealsBroken)
                        .Set("sealProgress", om.sealProgress)
                        // Countdown to the next seal (nextSealAt / turnsToNextSeal) on the fixed schedule.
                        .Set("seals", Summaries.SealTiming(map))
                        .Set("availableEnthrallments", om.availableEnthrallments)
                        .Set("enthralledCount", om.nEnthralled)
                        .Set("agentCap", agentCap)
                        .Set("canRecruit", om.availableEnthrallments > 0 && om.nEnthralled < agentCap)
                        .Set("endOfGameAchieved", om.endOfGameAchieved)
                        // victoryMode defaults to 0 (SHADOW) and defeat() never sets it — gate on
                        // victoryAchieved or a defeat reads as a shadow victory.
                        .Set("victoryMode", om.victoryAchieved ? Summaries.VictoryModeLabel(om.victoryMode) : null)
                        .Set("victoryProgress", Summaries.Round2(map.data_victoryProgess))
                        // The god's win-condition sheet: time budget, seal thresholds, agent-cap curve,
                        // and the victory / seal descriptive text.
                        .Set("progression", Summaries.GodProgression(map))
                        .Set("panic", JsonValue.NewObject()
                            .Set("total", Summaries.Round2(map.worldPanic))
                            .Set("fromPowerUse", Summaries.Round2(om.panicFromPowerUse))
                            .Set("fromCluesDiscovered", Summaries.Round2(om.panicFromCluesDiscovered))
                            .Set("heroesFallen", Summaries.Round2(om.panicHeroesFallen))
                            .Set("temporaryChange", Summaries.Round2(om.panicTemporaryChange)))
                        .Set("agents", agents)
                        .Set("powers", powers));
                })));

            host.Register(new ToolDefinition(
                "get_victory_breakdown",
                "The full breakdown behind victoryProgress: the game's own scoring sheet with points-to-win "
                + "and every weighted category (% population in the Dark Empire, enshadowed population outside "
                + "it, enshadowed+insane rulers, insane rulers, population destroyed, Deep One / Vinerva / "
                + "Ophanim contributions) and the score total. Use it to see which of your activities is "
                + "actually scoring. victoryProgress = score total / pointsToWin. 'details' attributes the "
                + "columns whose aggregates are opaque: exactly which persons qualify for the insane / "
                + "insane+enshadowed columns (so a regression names its cause) and which Deep One cities "
                + "score (sanctums never score directly - they feed abyssal cities).",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    Overmind om = map.overmind;
                    if (om == null) return ToolResult.Error("no game state");
                    string breakdown;
                    // Same call the game's own HUD uses (UITopRight); it recomputes the live figures.
                    try { breakdown = om.computeVictoryProgress(); }
                    catch (Exception ex) { return ToolResult.Error("could not compute victory breakdown: " + ex.Message); }
                    JsonValue result = JsonValue.NewObject()
                        .Set("victoryProgress", Summaries.Round2(map.data_victoryProgess))
                        .Set("avrgEnshadowment", Summaries.Round2(map.data_avrgEnshadowment))
                        .Set("pointsToWin", 200)
                        .Set("breakdown", breakdown);
                    // Per-column attribution for the opaque aggregates (who qualifies, which cities score).
                    JsonValue details = Summaries.VictoryAttribution(ctx, map);
                    if (!details.IsNull) result.Set("details", details);
                    return ToolResult.Ok(result);
                })));

            host.Register(new ToolDefinition(
                "list_recruitable_agents",
                "What you can recruit right now: your capacity, the archetypes you can enthrall onto a " +
                "location (pass code + locationId to recruit_agent), and heroes corrupted enough to turn in " +
                "place (pass heroUnitId). Costs one recruitment point. Archetypes specialise (intrigue = " +
                "infiltration/rulers, might+command = combat, lore = rituals); each carries placement.eligible " +
                "+ exampleTargets showing where it can go right now - match the pick to your need, not the " +
                "first listed. Each archetype's abilities array previews the rituals it unlocks once " +
                "recruited, with prerequisites - weigh these when choosing.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    Overmind om = map.overmind;
                    if (om.god == null) return ToolResult.Error("no god selected yet");
                    om.calculateAgentsUsed();
                    int cap = om.getAgentCap();

                    JsonValue archetypes = JsonValue.NewArray();
                    foreach (UAE_Abstraction ab in om.agentsGeneric) archetypes.Add(Summaries.AbstractionSummary(ctx, ab, "generic", Summaries.PlacementSummary(map, ab, 4)));
                    foreach (UAE_Abstraction ab in om.agentsUnique) archetypes.Add(Summaries.AbstractionSummary(ctx, ab, "unique", Summaries.PlacementSummary(map, ab, 4)));

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
                "Your god's powers with cost and whether each is castable right now. Ids are stable across " +
                "turns and seal breaks, and may be non-sequential.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    if (map.overmind.god == null) return ToolResult.Error("no god selected yet");
                    JsonValue powers = JsonValue.NewArray();
                    List<Power> list = map.overmind.god.getPowers();
                    for (int i = 0; i < list.Count; i++)
                    {
                        powers.Add(Summaries.PowerSummary(map, list[i]));
                    }
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("power", Summaries.Round2(map.overmind.power))
                        .Set("items", powers));
                })));

            host.Register(new ToolDefinition(
                "list_challenges",
                "Challenges and rituals available to one of your units. menaceGain/profileGain are the "
                + "one-time menace/profile actually applied to the unit on completion (after difficulty "
                + "scaling); indefinite challenges instead act per turn (see their heatNote/description). "
                + "progressPerTurn is UNIT-relative (stat-scaled: the same challenge can run 7x faster "
                + "for another agent); etaTurns = ceil(complexity/progressPerTurn), turns of active work "
                + "excluding travel (absent when the rate is 0 or the challenge is indefinite); "
                + "progressBreakdown itemizes the rate (dropped with terse=true). valid = the challenge's "
                + "world precondition (usually true even when you cannot act); validForUnit = can THIS "
                + "unit do it from where it stands - location preconditions (infiltration %, shadow %, "
                + "ward, settlement type) are checked here, so validForUnit:false usually means 'not "
                + "here / not yet', NOT 'wrong unit type'; 'restriction' states the actual requirement. "
                + "Optionally list another location's challenges "
                + "to plan a move. Pass terse=true to drop the long prose descriptions, performableOnly=true "
                + "to list only what this unit can act on right now (anything filtered out is summarized in "
                + "hiddenNotPerformable so nothing is silently dropped).",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Unit id, e.g. U17"), required: true),
                    Schema.Prop("locationId", Schema.String("Look at this location instead of the unit's current one")),
                    Schema.Prop("terse", Schema.Boolean("Omit each challenge's long 'description' prose (keeps name/type/heat/valid/restriction). Cheaper output.")),
                    Schema.Prop("performableOnly", Schema.Boolean("Only challenges this unit can act on now (valid AND validForUnit)"))),
                a => WithMap(ctx, map =>
                {
                    Unit u = Summaries.ResolveId(ctx, a["unitId"].AsString()) as Unit;
                    if (u == null) return ToolResult.Error(StaleUnitIdError(ctx, a["unitId"].AsString()));
                    Location loc = u.location;
                    if (!a["locationId"].IsNull)
                    {
                        loc = Summaries.ResolveId(ctx, a["locationId"].AsString()) as Location;
                        if (loc == null) return ToolResult.Error("unknown location id: " + a["locationId"].AsString());
                    }
                    bool terse = a["terse"].AsBool();
                    bool performableOnly = a["performableOnly"].AsBool();
                    UA uaF = u as UA;
                    UM umF = u as UM;
                    Func<Challenge, bool> performable = c =>
                    {
                        try
                        {
                            if (!c.valid()) return false;
                            if (uaF != null) return c.validFor(uaF);
                            if (umF != null) return c.validFor(umF);
                            return true;
                        }
                        catch { return false; }
                    };

                    // Refresh the location's challenge list the same way the game UI does.
                    loc.populateStandardChallenges();

                    // With performableOnly, never drop entries silently: an agent that only ever sees the
                    // performable subset can't learn that e.g. the whole Geomancy family exists behind a
                    // mastery gate. Collect what was filtered into a compact hidden list instead.
                    int hiddenCount = 0;
                    JsonValue hiddenItems = JsonValue.NewArray();
                    Action<Challenge, bool> recordHidden = (c, isRitual) =>
                    {
                        hiddenCount++;
                        if (hiddenItems.Count >= 20) return;
                        JsonValue h = JsonValue.NewObject()
                            .Set("id", Summaries.ChallengeId(ctx, c))
                            .Set("name", Summaries.ChallengeName(c));
                        if (isRitual) h.Set("ritual", true);
                        try
                        {
                            string restr = c.getRestriction();
                            if (!string.IsNullOrEmpty(restr)) h.Set("restriction", restr);
                        }
                        catch { }
                        hiddenItems.Add(h);
                    };

                    JsonValue arr = JsonValue.NewArray();
                    foreach (Challenge c in loc.GetChallenges())
                    {
                        // The game shows an agent only non-"good" challenges (UIScroll_Unit filters
                        // isGoodTernary()==1 from BOTH its valid and invalid sections): a hero-side
                        // challenge like Combat Banditry would undo the player's own work. Mirror that.
                        if (uaF != null && Summaries.IsHeroOnly(c)) continue;
                        // For an army the game UI offers no location challenges at all — only the
                        // subclasses that explicitly implement validFor(UM) are genuinely army-usable.
                        if (umF != null && !Summaries.OverridesValidForUM(c)) continue;
                        if (performableOnly && !performable(c)) { recordHidden(c, false); continue; }
                        arr.Add(Summaries.ChallengeSummary(ctx, c, u, !terse));
                    }
                    JsonValue rituals = JsonValue.NewArray();
                    if (u.rituals != null)
                    {
                        foreach (Challenge r in u.rituals)
                        {
                            if (performableOnly && !performable(r)) { recordHidden(r, true); continue; }
                            rituals.Add(Summaries.ChallengeSummary(ctx, r, u, !terse));
                        }
                    }
                    // Rituals granted by carried items (Laughing Tome, Horde Banner…): the game merges
                    // these into the list from person.items, never into unit.rituals.
                    if (uaF != null && uaF.person != null && uaF.person.items != null)
                    {
                        foreach (Item it in uaF.person.items)
                        {
                            if (it == null) continue;
                            List<Ritual> granted;
                            try { granted = it.getRituals(uaF); } catch { continue; }
                            if (granted == null) continue;
                            foreach (Ritual r in granted)
                            {
                                if (r == null) continue;
                                if (performableOnly && !performable(r)) { recordHidden(r, true); continue; }
                                rituals.Add(Summaries.ChallengeSummary(ctx, r, u, !terse)
                                    .Set("fromItem", Summaries.SafeItemName(it)));
                            }
                        }
                    }
                    JsonValue result = JsonValue.NewObject()
                        .Set("location", Summaries.LocationRef(loc))
                        .Set("challenges", arr)
                        .Set("unitRituals", rituals);
                    if (hiddenCount > 0)
                        result.Set("hiddenNotPerformable", JsonValue.NewObject()
                            .Set("count", hiddenCount)
                            .Set("items", hiddenItems)
                            .Set("hint", "these challenges/rituals exist here but this unit cannot act on "
                                + "them right now (valid/validFor failed — see each 'restriction'). Re-run "
                                + "without performableOnly for full details."));
                    return ToolResult.Ok(result);
                })));

            host.Register(new ToolDefinition(
                "list_wars",
                "Every active war, with attacker, defender and the attacker's objective (e.g. INVASION), "
                + "start turn and projected end. The global picture behind each social group's atWarWith list.",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    JsonValue arr = JsonValue.NewArray();
                    if (map.wars != null)
                        foreach (War w in map.wars) arr.Add(Summaries.WarSummary(w));
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("total", map.wars != null ? map.wars.Count : 0)
                        .Set("items", arr));
                })));

            host.Register(new ToolDefinition(
                "list_investigations",
                "The detection dashboard: every clue the heroes hold that points at your interests "
                + "(your commandable agents or allied evil units such as orc upstarts), with the assigned "
                + "investigator, the lead's weight, how far the rumour has spread and where it was found. "
                + "Mirrors the get_threats scope; each clue can raise world panic / awareness of the "
                + "underground if pursued. Drill into one unit with get_unit (its investigation list).",
                Schema.Object(),
                a => WithMap(ctx, map =>
                {
                    JsonValue arr = JsonValue.NewArray();
                    foreach (Location l in map.locations)
                    {
                        if (l == null || l.evidence == null) continue;
                        foreach (Evidence e in l.evidence)
                            if (Summaries.IsEvidenceAgainstInterest(e)) arr.Add(Summaries.EvidenceSummary(ctx, e, true));
                    }
                    return ToolResult.Ok(JsonValue.NewObject().Set("total", arr.Count).Set("items", arr));
                })));

            host.Register(new ToolDefinition(
                "list_holy_orders",
                "Every religion (holy order): enshadowment, prophet, temples, worshipper reach, divine "
                + "entity, whether it worships you. Also the doctrine control panel: holyOrder.tenets lists "
                + "each tenet's status, range and shiftable directions (canInfluence.*), and "
                + "holyOrder.canChangeTenet says whether one can be shifted NOW with "
                + "influence_holy_order_tenet. Pass orderId (or verbose) for full detail: each tenet's "
                + "description and a ready-to-paste call.",
                Schema.Object(
                    Schema.Prop("orderId", Schema.String("Only this holy order, e.g. SG5 (implies verbose)")),
                    Schema.Prop("verbose", Schema.Boolean("Include every tenet's description and example call (large across all orders)"))),
                a => WithMap(ctx, map =>
                {
                    SocialGroup only = null;
                    if (!a["orderId"].IsNull)
                    {
                        only = Summaries.ResolveId(ctx, a["orderId"].AsString()) as SocialGroup;
                        if (only == null) return ToolResult.Error("unknown social group id: " + a["orderId"].AsString());
                        if (!(only is HolyOrder))
                            return ToolResult.Error(Summaries.SafeDisplayName(only) + " is not a holy order");
                    }
                    bool detail = only != null || a["verbose"].AsBool();
                    JsonValue arr = JsonValue.NewArray();
                    foreach (SocialGroup sg in map.socialGroups)
                    {
                        HolyOrder ho = sg as HolyOrder;
                        if (ho == null || (only != null && sg != only)) continue;
                        arr.Add(Summaries.SocialGroupSummary(ctx, sg)
                            .Set("holyOrder", Summaries.HolyOrderBlock(ctx, ho, detail)));
                    }
                    JsonValue result = JsonValue.NewObject().Set("total", arr.Count).Set("items", arr);
                    if (!detail)
                        result.Set("hint", "pass orderId (one order) or verbose:true for each tenet's description and a ready-to-paste influence_holy_order_tenet call");
                    return ToolResult.Ok(result);
                })));

            host.Register(new ToolDefinition(
                "get_recent_events",
                "Recent game events, newest first — a persistent cross-turn feed: status messages (idle "
                + "agents, wars, seals weakening, hero actions) plus the deaths, level-ups and narrative "
                + "events end_turn dismissed or resolved. Each item is {turn, type, title, message?, "
                + "resolution?}. (A player action's own messages are returned by that action, not re-logged.)",
                Schema.Object(Schema.Prop("limit", Schema.Integer("Max events (default 30)"))),
                a => WithMap(ctx, map =>
                {
                    int limit = a["limit"].AsInt(30);
                    if (limit < 1) limit = 1;
                    if (limit > MaxLimit) limit = MaxLimit;
                    return ToolResult.Ok(ctx.Events.Read(limit));
                })));
        }

        // ---------- helpers ----------

        /// <summary>
        /// Shared stale/unknown unit-id error. Stamps the current idEpoch so a client that cached
        /// U-ids across a save/load can see WHY they went stale (unit ids reset with the epoch).
        /// </summary>
        internal static string StaleUnitIdError(GameContext ctx, string id, string relist = "list_units")
        {
            return "unknown or stale unit id: " + id + " - re-run " + relist + " (idEpoch is now "
                + ctx.Registry.Epoch + "; all U-ids reset whenever game_overview.idEpoch changes, e.g. on load)";
        }

        /// <summary>
        /// The game_overview payload for a live map. Shared with new_game so a freshly
        /// started game returns the same immediate context game_overview would.
        /// </summary>
        internal static JsonValue OverviewJson(GameContext ctx, Map map)
        {
            int commandable = 0;
            int agentsUnderAttack = 0, armiesInBattle = 0;
            JsonValue underAttack = JsonValue.NewArray();
            foreach (Unit u in map.units)
            {
                if (u.isCommandable()) commandable++;
                if (u.isDead || !u.isCommandable()) continue;
                // Active combat (distinct from the predictive danger below): an agent attacked THIS turn
                // has a battle pending; an army may be mid field-battle. Surfaced so an agent reading only
                // game_overview cannot miss a fight it must resolve before the turn can end.
                // Match the actual end_turn block condition (UA engaged by a live UA this turn) so this
                // "battle pending / end_turn blocked" signal can never contradict end_turn's behaviour.
                if (u is UA && u.engagedBy is UA atk && !atk.isDead && u.turnLastEngaged == map.turn)
                {
                    agentsUnderAttack++;
                    underAttack.Add(Summaries.UnitRef(ctx, u));
                }
                if (u.task is Task_InBattle) armiesInBattle++;
            }
            Overmind om = map.overmind;
            om.calculateAgentsUsed();
            int agentCap = om.god != null ? om.getAgentCap() : 0;
            bool canRecruit = om.availableEnthrallments > 0 && om.nEnthralled < agentCap;
            int maxTurns = om.god != null ? om.god.getMaxTurns() : 0;

            // Combat-danger breadcrumb: an agent that only ever reads game_overview should still
            // notice heroes closing on its agents. agentsInDanger > 0 => open get_threats.
            var safety = Summaries.ComputeAgentSafety(ctx, map);
            int agentsInDanger = 0, agentsHuntable = 0;
            Summaries.AgentSafetyInfo worstThreat = null;
            foreach (var s in safety)
            {
                if (s.IsHuntable) agentsHuntable++;
                if (!s.InDanger()) continue;
                agentsInDanger++;
                if (worstThreat == null || s.TopMotivation > worstThreat.TopMotivation) worstThreat = s;
            }
            JsonValue threatsBlock = JsonValue.NewObject()
                .Set("agentsInField", safety.Count)
                .Set("agentsInDanger", agentsInDanger)
                // Huntable = profile>=50 AND menace>25: exposed to a ruler's assassination even before
                // a hunter is in range. The signal that most predicts losing an agent (surfaced here so
                // an agent reading only game_overview sees it, not just get_threats.agentSafety).
                .Set("agentsHuntable", agentsHuntable);
            if (worstThreat != null)
                threatsBlock.Set("mostUrgent", Summaries.AgentSafetyLine(ctx, worstThreat));
            else if (agentsHuntable > 0)
                threatsBlock.Set("mostUrgent", agentsHuntable + " agent(s) huntable (profile>=50 & menace>25) - " +
                    "exposed to assassination; get_threats shows which and how to hide");
            if (agentsInDanger > 0 || agentsHuntable > 0)
                threatsBlock.Set("hint", "get_threats has per-agent odds");
            // Active combat takes priority over predictive danger: a pending battle blocks end_turn.
            if (agentsUnderAttack > 0)
                threatsBlock.Set("agentsUnderAttack", agentsUnderAttack)
                            .Set("underAttack", underAttack)
                            .Set("combatHint", "battle pending - end_turn is blocked until resolved via " +
                                "resolve_decision (fight, flee, or retreat)");
            if (armiesInBattle > 0)
                threatsBlock.Set("armiesInBattle", armiesInBattle);

            JsonValue o = JsonValue.NewObject()
                .Set("modVersion", ModCore.ModVersion)
                .Set("turn", map.turn)
                // Unit ids (U*) are minted per session and reset on new game / load; this counter
                // increments on every such reset so a client can detect that cached U-ids are stale.
                .Set("idEpoch", ctx.Registry.Epoch)
                // In an endless game there is no turn limit: getMaxTurns() still returns a number
                // (the game ignores it), so surface null there or an agent reads it as a deadline.
                // Mirrors the game UI's "Turn: X (Endless)".
                .Set("endless", map.opt_endless)
                .Set("maxTurns", map.opt_endless ? JsonValue.Null : JsonValue.Of(maxTurns))
                .Set("turnsRemaining", map.opt_endless ? JsonValue.Null : JsonValue.Of(Math.Max(0, maxTurns - map.turn)))
                .Set("god", JsonValue.NewObject()
                    .Set("name", map.overmind.god != null ? map.overmind.god.getName() : null)
                    .Set("type", map.overmind.god != null ? map.overmind.god.GetType().Name : null))
                .Set("power", Summaries.Round2(map.overmind.power))
                // victoryMode is only recorded on a WIN (defeat leaves it at the default 0); null otherwise.
                .Set("victoryMode", om.victoryAchieved ? Summaries.VictoryModeLabel(om.victoryMode) : null)
                .Set("victoryProgress", Summaries.Round2(map.data_victoryProgess))
                // Distinct from victoryProgress: the average enshadowment of rulers/heroes. Surfaced
                // so the two are not conflated. Full victory split via get_victory_breakdown.
                .Set("avrgEnshadowment", Summaries.Round2(map.data_avrgEnshadowment))
                .Set("worldPanic", Summaries.Round2(map.worldPanic))
                // Where the world's alarm is coming from (a player reads this in tooltips).
                // No `total` here: worldPanic above IS the total.
                .Set("panic", JsonValue.NewObject()
                    .Set("fromPowerUse", Summaries.Round2(om.panicFromPowerUse))
                    .Set("fromCluesDiscovered", Summaries.Round2(om.panicFromCluesDiscovered))
                    .Set("heroesFallen", Summaries.Round2(om.panicHeroesFallen))
                    .Set("temporaryChange", Summaries.Round2(om.panicTemporaryChange)))
                .Set("awarenessOfUnderground", Summaries.Round2(map.awarenessOfUnderground))
                .Set("wars", map.wars != null ? map.wars.Count : 0)
                // Clues currently pointing at your agents/interests; drill in via list_investigations.
                .Set("activeInvestigations", Summaries.CountInvestigationsAgainstMe(map))
                .Set("threats", threatsBlock)
                .Set("sealsBroken", map.overmind.sealsBroken)
                // Seals break on a fixed turn schedule; seals.turnsToNextSeal is the countdown, and
                // each break raises your power cap / unlocks abilities. Do not miss it.
                .Set("seals", Summaries.SealTiming(map))
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
            // Perishable opportunity: a religion with its Elder influence bar full can have one tenet
            // rewritten right now, and further influence it earns is discarded until you spend it. The
            // game only says so once, in a message; without this an agent banks influence forever.
            JsonValue readyOrders = HolyOrdersReadyToInfluence(ctx, map);
            if (!readyOrders.IsNull) o.Set("holyOrders", readyOrders);
            // Contextual one-shot tips: explain a mechanic the first turn it becomes relevant, on the
            // tool the agent reads every turn (mirrors the game's own hint popups). See TipEngine.
            JsonValue tips = TipEngine.CollectContextual(ctx);
            if (!tips.IsNull) o.Set("tips", tips);
            return o;
        }

        /// <summary>
        /// The full pending-decision object for game_overview (null when nothing is pending), tagged with
        /// a hint on how to answer it without loading a separate tool: pass the chosen option index to
        /// end_turn's resolveOptionIndex.
        /// </summary>
        private static JsonValue PendingDecisionForOverview(GameContext ctx)
        {
            JsonValue pd = DecisionRegistry.FullOrNull(ctx);
            if (!pd.IsNull)
            {
                Boilerplate.CompactDecision(ctx, pd);
                pd.Set("resolveHint", Boilerplate.ResolveHint(ctx));
            }
            return pd;
        }

        /// <summary>
        /// The game_overview "you can rewrite a faith's doctrine now" block, or null when no religion has
        /// its Elder influence bar full. Kept silent in the common case; when it fires it names the orders
        /// and their Alignment status, because darkening Alignment is the prerequisite for most changes.
        /// </summary>
        internal static JsonValue HolyOrdersReadyToInfluence(GameContext ctx, Map map)
        {
            JsonValue ready = JsonValue.NewArray();
            foreach (SocialGroup sg in map.socialGroups)
            {
                HolyOrder ho = sg as HolyOrder;
                if (ho == null) continue;
                int req;
                try { req = ho.influenceElderReq; } catch { continue; }
                if (ho.influenceElder < req) continue;
                JsonValue entry = JsonValue.NewObject()
                    .Set("id", Summaries.SocialGroupId(ho))
                    .Set("name", Summaries.SafeDisplayName(ho))
                    .Set("influenceElder", ho.influenceElder)
                    .Set("influenceElderReq", req);
                if (ho.tenet_alignment != null)
                    entry.Set("alignmentStatus", ho.tenet_alignment.status);
                ready.Add(entry);
            }
            if (ready.Count == 0) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("readyToInfluence", ready)
                .Set("hint", "one tenet can be shifted NOW per order listed (influence_holy_order_tenet); "
                    + "unspent Elder influence is discarded. list_holy_orders {\"orderId\":\"SG...\"} shows "
                    + "eligible tenets; usual first move: {\"tenet\":\"H_Alignment\",\"direction\":\"toward_elder\"}.");
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
            return ToolResult.Ok(PaginatePayload(args, matches, render));
        }

        /// <summary>Paginate variant returning the still-mutable payload, for callers that append
        /// response-level fields (e.g. list_units' ordersLegend) before serializing.</summary>
        private static JsonValue PaginatePayload<T>(JsonValue args, List<T> matches, Func<T, JsonValue> render)
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
            return result;
        }
    }
}
