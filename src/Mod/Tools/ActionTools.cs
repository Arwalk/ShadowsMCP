using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tips;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// Tools that command the player's units. Each replicates the exact guard + commit
    /// sequence the game UI uses (see docs/ground-truth-notes.md), returning API errors
    /// where the UI would show popups. Only commandable (player) units can be ordered.
    /// </summary>
    public static class ActionTools
    {
        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.Register(new ToolDefinition(
                "move_unit",
                "Order one of your agents to travel to a location (pathfinds automatically; moves immediately " +
                "with any moves left this turn, then continues each turn). Ordering a unit to its current " +
                "location cancels its movement order.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your unit's id, e.g. U17"), required: true),
                    Schema.Prop("locationId", Schema.String("Destination location id, e.g. L3"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon an in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map => MoveUnit(ctx, map, a))));

            host.Register(new ToolDefinition(
                "cancel_task",
                "Clear a unit's current order (movement or challenge).",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your unit's id, e.g. U17"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon an in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map =>
                {
                    Unit u;
                    ToolResult err = ResolveCommandable(ctx, a["unitId"].AsString(), out u);
                    if (err != null) return err;
                    if (u.task == null) return ToolResult.Ok("unit " + u.getName() + " has no task to cancel");
                    string warning = AbandonWarning(u);
                    if (warning != null && !a["force"].AsBool()) return ToolResult.Error(warning);
                    string had = u.task.getShort();
                    u.task = null;
                    CheckUiData(map);
                    return ToolResult.Ok("cancelled task '" + had + "' for " + u.getName());
                })));

            host.Register(new ToolDefinition(
                "perform_challenge",
                "Order one of your units to perform a challenge or ritual (from list_challenges). If the unit " +
                "is elsewhere, it travels there first and then begins.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your unit's id, e.g. U17"), required: true),
                    Schema.Prop("challengeId", Schema.String("Challenge id from list_challenges (stable across turns and save/load; no need to re-list before performing)"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon an in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map => PerformChallenge(ctx, map, a))));

            host.Register(new ToolDefinition(
                "use_power",
                "Cast one of your god's powers (see list_powers) on a target unit or location. The cost is " +
                "deducted from your power resource.",
                Schema.Object(
                    Schema.Prop("power", Schema.String("Power id (PW2) or name (case-insensitive)"), required: true),
                    Schema.Prop("targetUnitId", Schema.String("Target unit id, e.g. U17")),
                    Schema.Prop("targetLocationId", Schema.String("Target location id, e.g. L3"))),
                a => QueryTools.WithMap(ctx, map => UsePower(ctx, map, a))));

            host.Register(new ToolDefinition(
                "recruit_agent",
                "Recruit a new agent by spending one recruitment point (enthrallment). Either enthrall an " +
                "archetype (pass agentCode from list_recruitable_agents plus a target locationId) or corrupt " +
                "an eligible existing hero in place (pass heroUnitId). Note: losing all your agents is not a " +
                "loss - recruit more here. Recruiting grants the new agent a skill point, so a level-up trait " +
                "pick may then be pending (resolve it via resolve_decision or end_turn).",
                Schema.Object(
                    Schema.Prop("agentCode", Schema.Integer("Archetype code from list_recruitable_agents (e.g. -3 for a Warlock). Requires locationId.")),
                    Schema.Prop("heroUnitId", Schema.String("Instead of an archetype, corrupt this eligible hero in place, e.g. U17 (from list_recruitable_agents.corruptibleHeroes)")),
                    Schema.Prop("locationId", Schema.String("Target location for the archetype, e.g. L3 (ignored when heroUnitId is given)"))),
                a => QueryTools.WithMap(ctx, map => RecruitAgent(ctx, map, a))));

            host.Register(new ToolDefinition(
                "command_army",
                "Issue a commandable military unit's special order - the third action category beside challenges " +
                "and powers, used by army units such as an awakened god-army (e.g. She Who Will Feast) or an orc " +
                "raiding party. order=raze devours the human settlement the unit is standing on (move it onto the " +
                "city first; the city's defences fall each turn until it is destroyed); order=drive_back forces an " +
                "enemy hero sharing the unit's tile to retreat and drop its task; order=attack starts a battle with " +
                "an enemy army sharing the tile. A unit's currently-available orders (with the exact call) appear " +
                "under 'orders' in get_unit / list_units.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your military unit's id, e.g. U17"), required: true),
                    Schema.Prop("order", Schema.StringEnum("Which order to issue", "raze", "drive_back", "attack"), required: true),
                    Schema.Prop("targetUnitId", Schema.String("For drive_back/attack: the enemy unit sharing your unit's tile, e.g. U9 (ignored for raze)"))),
                a => QueryTools.WithMap(ctx, map => CommandArmy(ctx, map, a))));

            // Registered as a server-thread tool: end-turn processing can exceed the normal
            // per-tool timeout, so it dispatches its own job with the longer budget.
            host.RegisterServerThread(new ToolDefinition(
                "end_turn",
                "End your turn (runs the full turn processing; may take a few seconds). If a decision popup " +
                "is blocking the turn, this returns it with its options (also in game_overview.pendingDecision) " +
                "and does not advance; pass resolveOptionIndex to pick an option, and it resolves that decision " +
                "then continues ending the turn. With force=true, auto-resolves what else blocks a single " +
                "turn's end (skill points auto-spent, the idle-agent alert pushed past for that turn, and " +
                "purely-informational popups dismissed: the agent-death NOTICE - kind:\"death\", " +
                "PopupMsgAgentsDeath - and autosave/message boxes). Two things force never auto-answers: a " +
                "pending agent battle (it always blocks - blockedBy:\"combat\" - and must be resolved: fight " +
                "to the end, flee, or retreat via resolveOptionIndex / resolve_decision), and a narrative " +
                "event (kind:\"event\"), INCLUDING the \"Defeat\" event a lost battle raises, because an " +
                "event's choice can matter - answer it with resolveOptionIndex. Idle agents are a recurring " +
                "state, not a one-off notice: a count>1 batch stops each turn on the re-raised idle alert " +
                "(stopReason:\"decision\", kind:\"idleAgents\") unless the agents hold standing orders. Pass " +
                "count to advance several turns at once (force=true recommended so it doesn't stall on the " +
                "repetitive 'Life Continues'-type popups); it stops early and reports stopReason on any "
                + "decision, game over, or a meaningful threat escalation (an agent becomes huntable / a hero "
                + "it is not favoured against starts hunting it / its odds worsen), with a threatAlert listing "
                + "the affected agents (each tagged with what triggered it). Set stopOnThreatMotivation to also "
                + "halt when a hunter's motivation toward an agent is at or above that percent - it fires "
                + "whether motivation rose to it mid-batch OR was already there at the start, and can exceed "
                + "100 for a strongly-inclined hunter. A 'tips' array may also appear, "
                + "explaining a mechanic that just became relevant.",
                Schema.Object(
                    Schema.Prop("count", Schema.Integer("Advance up to this many turns (default 1, max 10). Stops early on any decision, game over, or a meaningful threat escalation (an agent becomes huntable, a hero it is not favoured against starts hunting it, or its odds worsen).")),
                    Schema.Prop("force", Schema.Boolean("Auto-resolve level-up/skill-point and idle-agent interruptions and dismiss purely-informational popups (the death NOTICE, message boxes). Does NOT skip a pending battle (always blocks) or a narrative event (kind:event, including a lost battle's Defeat popup) - resolve those explicitly. In a count>1 batch, idle agents re-raise each turn and stop the batch unless they hold orders.")),
                    Schema.Prop("resolveOptionIndex", Schema.Integer("If a decision popup is blocking the turn, choose this option (index from the pendingDecision options) to resolve it, then continue ending the turn")),
                    Schema.Prop("stopOnThreatMotivation", Schema.Integer("Opt-in caution: stop the batch on the first turn a hunter's motivation toward one of your agents is AT OR ABOVE this percent, even while the agent is still favoured - catches threat building up before an agent becomes huntable. Level-triggered: fires whether the hunter rose to it mid-batch OR was already there at batch start. Motivation can exceed 100 for a strongly-inclined hunter, so a threshold above 100 is valid (e.g. 150 = only when strongly inclined). Omit or 0 to disable (default)."))),
                a =>
                {
                    bool force = a["force"].AsBool();
                    return ctx.Dispatcher.Run(
                        () => QueryTools.WithMap(ctx, map => EndTurn(ctx, map, force, a)),
                        ctx.Config.EndTurnTimeoutMs);
                }));
        }

        // ---------- move ----------

        private static ToolResult MoveUnit(GameContext ctx, Map map, JsonValue a)
        {
            Unit u;
            ToolResult err = ResolveCommandable(ctx, a["unitId"].AsString(), out u);
            if (err != null) return err;

            Location dest = Summaries.ResolveId(ctx, a["locationId"].AsString()) as Location;
            if (dest == null) return ToolResult.Error("unknown location id: " + a["locationId"].AsString());

            // Same guards as UIInputs.rightClickOnHex:
            if (u.engagedBy != null && u.turnLastEngaged == map.turn)
                return ToolResult.Error(u.getName() + " is under attack by " + u.engagedBy.getName() +
                    " and must resolve this combat first (get_pending_decision, then resolve_decision to fight, " +
                    "flee, or retreat).");
            if (u.task is Task_Disrupted)
                return ToolResult.Error(u.getName() + " is disrupted and cannot move this turn.");

            if (u.location == dest)
            {
                if (u.task is Task_GoToLocation)
                {
                    u.task = null;
                    CheckUiData(map);
                    return ToolResult.Ok(u.getName() + " is already at " + dest.getName() + "; cancelled its movement order.");
                }
                return ToolResult.Error(u.getName() + " is already at " + dest.getName() + ".");
            }

            string warning = AbandonWarning(u);
            if (warning != null && !a["force"].AsBool()) return ToolResult.Error(warning);

            // Pre-check reachability the same way Task_GoToLocation will.
            Location[] path = map.getPathTo(u.location, dest, u, !u.society.isAtWar());
            if (path == null) path = map.getPathTo(u.location, dest, u);
            if (path == null || path.Length < 2)
                return ToolResult.Error("no path from " + u.location.getName() + " to " + dest.getName() +
                    " for " + u.getName() + ".");
            int pathSteps = path.Length - 1;

            // Exact UI commit sequence:
            u.task = new Task_GoToLocation(dest);
            if (u.movesTaken < u.getMaxMoves())
                u.task.turnTick(u);
            CheckUiData(map);

            bool arrived = u.location == dest;
            JsonValue result = JsonValue.NewObject()
                .Set("unit", Summaries.UnitRef(ctx, u))
                .Set("destination", Summaries.LocationRef(dest))
                .Set("nowAt", Summaries.LocationRef(u.location))
                .Set("arrived", arrived)
                .Set("pathSteps", pathSteps);
            if (!arrived)
            {
                int maxMoves = Math.Max(1, u.getMaxMoves());
                Location[] remaining = map.getPathTo(u.location, dest, u, !u.society.isAtWar());
                int stepsLeft = remaining != null ? remaining.Length - 1 : pathSteps;
                result.Set("estimatedTurnsToArrive", (int)Math.Ceiling(stepsLeft / (double)maxMoves));
            }
            return ToolResult.Ok(result);
        }

        // ---------- challenges ----------

        private static ToolResult PerformChallenge(GameContext ctx, Map map, JsonValue a)
        {
            Unit u;
            ToolResult err = ResolveCommandable(ctx, a["unitId"].AsString(), out u);
            if (err != null) return err;

            Challenge c = Summaries.ResolveChallengeForUnit(ctx, u, a["challengeId"].AsString());
            if (c == null)
                return ToolResult.Error(StaleChallengeError(ctx, u, a["challengeId"].AsString()));

            UA ua = u as UA;
            UM um = u as UM;
            if (ua == null && um == null)
                return ToolResult.Error(u.getName() + " cannot perform challenges.");

            // Guards, mirroring UA/UM.playerTriesToStartChallenge:
            if (u.engagedBy != null && u.turnLastEngaged == map.turn)
                return ToolResult.Error(u.getName() + " is under attack by " + u.engagedBy.getName() +
                    " and must resolve this combat first (get_pending_decision, then resolve_decision).");
            // Surface the game's own reason text (getRestriction) so a rejected attempt says WHY, not just
            // "requirements not met" - e.g. "Requires 100% Infiltration. Cannot perform if Ward > 50%".
            string restr;
            try { restr = c.getRestriction(); } catch { restr = null; }
            string why = string.IsNullOrEmpty(restr) ? "" : ": " + restr;
            if (!c.valid())
                return ToolResult.Error("the requirements to enable challenge '" + c.getName() + "' are not met" + why);
            if (ua != null && !c.validFor(ua))
                return ToolResult.Error(u.getName() + " does not meet the requirements for '" + c.getName() + "'" + why);
            if (um != null && !c.validFor(um))
                return ToolResult.Error(u.getName() + " does not meet the requirements for '" + c.getName() + "'" + why);
            if (u.task is Task_Disrupted)
                return ToolResult.Error(u.getName() + " is currently disrupted.");
            if (!c.allowMultipleUsers() && c.claimedBy != null && c.claimedBy.location == c.location &&
                c.claimedBy.task is Task_PerformChallenge activeClaim && activeClaim.challenge == c)
            {
                if (c.claimedBy == u)
                    return ToolResult.Error(u.getName() + " is already performing '" + c.getName() + "'.");
                return ToolResult.Error("'" + c.getName() + "' is already being performed by " + c.claimedBy.getName() + ".");
            }

            string warning = AbandonWarning(u);
            if (warning != null && !a["force"].AsBool()) return ToolResult.Error(warning);

            // Remote challenge: travel there first (the game uses Task_GoToPerformChallenge for this).
            if (u.location != c.location)
            {
                Location[] path = map.getPathTo(u.location, c.location, u, !u.society.isAtWar());
                if (path == null) path = map.getPathTo(u.location, c.location, u);
                if (path == null || path.Length < 2)
                    return ToolResult.Error("no path to " + c.location.getName() + " for " + u.getName() + ".");
                u.task = new Task_GoToPerformChallenge(c);
                CheckUiData(map);
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("unit", Summaries.UnitRef(ctx, u))
                    .Set("challenge", c.getName())
                    .Set("status", "travelling to " + c.location.getName() + " to begin (" + (path.Length - 1) + " steps away)"));
            }

            // Same-location commit, exactly as the game does it:
            foreach (Challenge other in u.location.GetChallenges())
            {
                if (other.claimedBy == u) other.claimedBy = null;
            }
            if (u.rituals != null)
            {
                foreach (Challenge r in u.rituals)
                {
                    if (r.claimedBy == u) r.claimedBy = null;
                }
            }
            u.task = new Task_PerformChallenge(c);
            c.claimedBy = u;
            if (ua != null)
            {
                foreach (Assets.Code.Modding.ModKernel mod in map.mods)
                {
                    mod.onPlayerStartsChallenge(ua, c);
                }
                c.onImmediateBegin(ua);
            }
            CheckUiData(map);

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("unit", Summaries.UnitRef(ctx, u))
                .Set("challenge", c.getName())
                .Set("status", "started")
                .Set("menaceGain", Summaries.Round2(c.getMenace()))
                .Set("profileGain", Summaries.Round2(c.getProfile())));
        }

        /// <summary>A stale/unknown-challenge error that also lists the unit's currently-available challenge
        /// ids+names, so the agent can retry immediately instead of guessing. With deterministic ids this is
        /// rare (it means the challenge is genuinely gone), so we spend the words on actionable alternatives.</summary>
        private static string StaleChallengeError(GameContext ctx, Unit u, string id)
        {
            var lines = new List<string>();
            try
            {
                Location loc = u.location;
                if (loc != null)
                {
                    loc.populateStandardChallenges();
                    foreach (Challenge c in loc.GetChallenges())
                    {
                        if (c == null) continue;
                        lines.Add(Summaries.ChallengeId(ctx, c) + " (" + Summaries.ChallengeName(c) + ")");
                        if (lines.Count >= 12) break;
                    }
                }
                if (u.rituals != null)
                    foreach (Challenge r in u.rituals)
                    {
                        if (r == null) continue;
                        lines.Add(Summaries.ChallengeId(ctx, r) + " (" + Summaries.ChallengeName(r) + ")");
                        if (lines.Count >= 16) break;
                    }
            }
            catch { }
            string head = "unknown or stale challenge id: " + id + ". ";
            if (lines.Count > 0)
                return head + "Challenges available to " + u.getName() + " now: " +
                    string.Join(", ", lines.ToArray()) + ".";
            return head + "Re-run list_challenges for " + u.getName() + ".";
        }

        // ---------- powers ----------

        private static ToolResult UsePower(GameContext ctx, Map map, JsonValue a)
        {
            if (map.overmind.god == null) return ToolResult.Error("no god selected yet");
            List<Power> powers = map.overmind.god.getPowers();

            string wanted = a["power"].AsString();
            if (string.IsNullOrEmpty(wanted)) return ToolResult.Error("missing 'power'");
            Power power = null;
            int index = -1;
            if (wanted.StartsWith("PW", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(wanted.Substring(2), out index) && index >= 0 && index < powers.Count)
            {
                power = powers[index];
            }
            else
            {
                for (int i = 0; i < powers.Count; i++)
                {
                    if (string.Equals(powers[i].getName(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        power = powers[i];
                        index = i;
                        break;
                    }
                }
            }
            if (power == null)
                return ToolResult.Error("unknown power '" + wanted + "' - see list_powers for ids and names");

            if (power.isPassiveOnly())
                return ToolResult.Error("'" + power.getName() + "' is passive and cannot be cast.");
            int cost = power.getCost();
            if (map.overmind.power < (double)cost)
                return ToolResult.Error("not enough power: '" + power.getName() + "' costs " + cost +
                    ", you have " + Summaries.Round2(map.overmind.power) + ".");

            bool hasUnit = !a["targetUnitId"].IsNull;
            bool hasLoc = !a["targetLocationId"].IsNull;
            if (hasUnit == hasLoc)
                return ToolResult.Error("provide exactly one of targetUnitId or targetLocationId" +
                    (power.getRestrictionText() != null ? " (target: " + power.getRestrictionText() + ")" : ""));

            // Mirrors Sel_CastPower.onClick: validTarget then cast; castCommon deducts the cost.
            if (hasUnit)
            {
                Unit target = Summaries.ResolveId(ctx, a["targetUnitId"].AsString()) as Unit;
                if (target == null) return ToolResult.Error("unknown or stale unit id: " + a["targetUnitId"].AsString());
                if (!power.validTarget(target))
                    return ToolResult.Error(target.getName() + " is not a valid target for '" + power.getName() +
                        "'. " + (power.getRestrictionText() ?? ""));
                power.cast(target);
            }
            else
            {
                Location target = Summaries.ResolveId(ctx, a["targetLocationId"].AsString()) as Location;
                if (target == null) return ToolResult.Error("unknown location id: " + a["targetLocationId"].AsString());
                if (!power.validTarget(target))
                    return ToolResult.Error(target.getName() + " is not a valid target for '" + power.getName() +
                        "'. " + (power.getRestrictionText() ?? ""));
                power.cast(target);
            }
            CheckUiData(map);

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("cast", power.getName())
                .Set("cost", cost)
                .Set("remainingPower", Summaries.Round2(map.overmind.power)));
        }

        // ---------- recruit ----------

        private static ToolResult RecruitAgent(GameContext ctx, Map map, JsonValue a)
        {
            Overmind om = map.overmind;
            if (om.god == null) return ToolResult.Error("no god selected yet");
            om.calculateAgentsUsed();

            if (om.availableEnthrallments <= 0)
                return ToolResult.Error("no recruitment points available; they regenerate every few turns " +
                    "(see get_player_state.availableEnthrallments).");
            int cap = om.getAgentCap();
            if (om.nEnthralled >= cap)
                return ToolResult.Error("agent cap reached (" + om.nEnthralled + "/" + cap +
                    "); break more seals to raise it.");

            bool hasCode = !a["agentCode"].IsNull;
            bool hasHero = !string.IsNullOrEmpty(a["heroUnitId"].AsString());
            if (hasCode == hasHero)
                return ToolResult.Error("specify exactly one of agentCode (with locationId) or heroUnitId.");

            UAE_Abstraction abstr;
            Location target;
            Unit heroUnit = null;

            if (hasHero)
            {
                Unit u = Summaries.ResolveId(ctx, a["heroUnitId"].AsString()) as Unit;
                if (u == null)
                    return ToolResult.Error("unknown or stale unit id: " + a["heroUnitId"].AsString() +
                        " - re-run list_recruitable_agents.");
                if (!Summaries.IsCorruptibleHero(u))
                    return ToolResult.Error(u.getName() + " cannot be corrupted (must be a non-commandable " +
                        "hero/acolyte at 100% shadow or insane, and not the Chosen One).");
                heroUnit = u;
                abstr = new UAE_Abstraction(map, (UA)u);
                target = u.location;
            }
            else
            {
                int code = a["agentCode"].AsInt();
                abstr = FindAbstraction(om, code);
                if (abstr == null)
                    return ToolResult.Error("unknown agent code " + code + " - see list_recruitable_agents.");
                target = Summaries.ResolveId(ctx, a["locationId"].AsString()) as Location;
                if (target == null)
                {
                    string got = a["locationId"].AsString();
                    return ToolResult.Error(string.IsNullOrEmpty(got)
                        ? "archetype recruitment needs a locationId (none was provided) - see list_locations."
                        : "archetype recruitment could not resolve locationId '" + got + "' - see list_locations.");
                }
            }

            // validTarget also enforces the agent cap; getRestrictions explains a placement failure.
            if (!abstr.validTarget(target))
            {
                string msg = "cannot place " + abstr.getName() + " at " + target.getName() +
                    ": " + abstr.getRestrictions();
                // Point the agent at where this archetype CAN go, so a bad placement teaches
                // "here is a valid target" rather than "non-Hierophant recruits just fail".
                List<Location> suggestions = Summaries.ValidTargets(map, abstr, 4);
                if (suggestions.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (Location l in suggestions)
                    {
                        string nm; try { nm = l.getName(); } catch { nm = "?"; }
                        parts.Add(Summaries.LocationId(l) + " (" + nm + ")");
                    }
                    msg += " - valid targets right now include: " + string.Join(", ", parts) +
                        " (see list_recruitable_agents -> placement.exampleTargets).";
                }
                else
                {
                    msg += " - no location currently satisfies this archetype; pick a different agentCode " +
                        "(see list_recruitable_agents).";
                }
                return ToolResult.Error(msg);
            }

            // Commit, mirroring Sel_CreateAgent.onClick: createAgent then fire the onAgentCreated mod hook.
            int beforeUnits = map.units.Count;
            abstr.createAgent(target);
            Unit created = map.units.Count > beforeUnits ? map.units[map.units.Count - 1] : heroUnit;
            if (!map.tutorial && created != null)
            {
                foreach (Assets.Code.Modding.ModKernel mod in map.mods)
                {
                    try { mod.onAgentCreated(created); } catch { }
                }
            }
            om.calculateAgentsUsed();
            CheckUiData(map);

            JsonValue result = JsonValue.NewObject()
                .Set("recruited", created != null ? Summaries.UnitSummary(ctx, created) : JsonValue.Null)
                .Set("availableEnthrallments", om.availableEnthrallments)
                .Set("nEnthralled", om.nEnthralled)
                .Set("agentCap", cap);
            if (created != null && created.person != null && created.person.skillPoints > 0)
                result.Set("levelUpPending", "the new agent has a skill point to spend; a level-up trait " +
                    "pick may be waiting - resolve it via resolve_decision or end_turn.");
            return ToolResult.Ok(result);
        }

        private static UAE_Abstraction FindAbstraction(Overmind om, int code)
        {
            foreach (UAE_Abstraction ab in om.agentsGeneric) if (ab.code == code) return ab;
            foreach (UAE_Abstraction ab in om.agentsUnique) if (ab.code == code) return ab;
            return null;
        }

        // ---------- commandable-military special orders (raze / drive back / attack) ----------

        /// <summary>Issue one of the three UM commands the game exposes on a selected commandable military unit
        /// (UIScroll_Unit's Raze / Drive Back / Attack buttons). These are neither god powers nor challenges, so
        /// they have no other tool. Each branch mirrors the exact guard + commit the matching UM.playerCommands*
        /// / playerOrdersAttack method uses, returning a clean error where the UI would pop a message.</summary>
        private static ToolResult CommandArmy(GameContext ctx, Map map, JsonValue a)
        {
            Unit u;
            ToolResult err = ResolveCommandable(ctx, a["unitId"].AsString(), out u);
            if (err != null) return err;

            UM um = u as UM;
            if (um == null)
                return ToolResult.Error(u.getName() + " is not a military unit; only commandable armies (a UM, " +
                    "such as an awakened god-army or an orc raiding party) can be given these orders.");

            string order = a["order"].AsString();
            if (string.IsNullOrEmpty(order))
                return ToolResult.Error("missing 'order' - one of raze, drive_back, attack.");
            order = order.ToLowerInvariant();

            // Shared guard: none of the three orders can be issued while in battle (each method pops this).
            if (um.task is Task_InBattle)
                return ToolResult.Error(um.getName() + " cannot be given orders while in battle.");

            switch (order)
            {
                case "raze":
                {
                    SettlementHuman sh = um.location != null ? um.location.settlement as SettlementHuman : null;
                    if (sh == null)
                        return ToolResult.Error(um.getName() + " must be standing on a human settlement to raze it - " +
                            "move it onto the city first (its current tile has no human settlement).");
                    um.playerCommandsRazeSettlement();
                    CheckUiData(map);
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, um))
                        .Set("order", "raze")
                        .Set("target", Summaries.LocationRef(um.location))
                        .Set("task", TaskShort(um.task))
                        .Set("status", "razing " + sh.getName() + " - its defences will fall each turn until it is destroyed"));
                }
                case "drive_back":
                {
                    Unit target;
                    ToolResult terr = ResolveTileTarget(ctx, um, a["targetUnitId"].AsString(), out target);
                    if (terr != null) return terr;
                    UA ua = target as UA;
                    if (ua == null)
                        return ToolResult.Error(target.getName() + " is not a hero/agent; drive_back targets an enemy " +
                            "hero (use order=attack for an enemy army).");
                    if (ua.isCommandable())
                        return ToolResult.Error(target.getName() + " is under your command; you can only drive back enemy heroes.");
                    um.playerCommandsDriveBack(ua);
                    CheckUiData(map);
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, um))
                        .Set("order", "drive_back")
                        .Set("target", Summaries.UnitRef(ctx, ua))
                        .Set("status", "drove back " + ua.getName() + " - forced to retreat and drop its current task"));
                }
                case "attack":
                {
                    Unit target;
                    ToolResult terr = ResolveTileTarget(ctx, um, a["targetUnitId"].AsString(), out target);
                    if (terr != null) return terr;
                    UM enemyArmy = target as UM;
                    if (enemyArmy == null)
                        return ToolResult.Error(target.getName() + " is not an army; order=attack targets an enemy army " +
                            "(use order=drive_back for an enemy hero).");
                    if (enemyArmy.isCommandable())
                        return ToolResult.Error(target.getName() + " is under your command; you cannot attack your own army.");
                    if (enemyArmy.society == um.society)
                        return ToolResult.Error(target.getName() + " is in your own society; you can only attack a hostile army.");
                    if (um.movesTaken >= um.getMaxMoves())
                        return ToolResult.Error(um.getName() + " has no remaining movement points this turn to attack.");
                    um.playerOrdersAttack(enemyArmy);
                    CheckUiData(map);
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, um))
                        .Set("order", "attack")
                        .Set("target", Summaries.UnitRef(ctx, enemyArmy))
                        .Set("status", "attacking " + enemyArmy.getName()));
                }
                default:
                    return ToolResult.Error("unknown order '" + order + "' - one of raze, drive_back, attack.");
            }
        }

        /// <summary>Resolve a drive_back/attack target and enforce that it shares the commander's tile
        /// (the game only offers these orders against units in um.location.units).</summary>
        private static ToolResult ResolveTileTarget(GameContext ctx, UM um, string id, out Unit target)
        {
            target = Summaries.ResolveId(ctx, id) as Unit;
            if (target == null)
                return ToolResult.Error(string.IsNullOrEmpty(id)
                    ? "this order needs a targetUnitId (the enemy unit sharing your unit's tile) - see get_unit.orders."
                    : "unknown or stale unit id: " + id + " - re-run list_units.");
            if (target.isDead)
                return ToolResult.Error(target.getName() + " is dead.");
            if (target.location != um.location)
                return ToolResult.Error(target.getName() + " is not on " + um.getName() + "'s tile; these orders only " +
                    "apply to a unit sharing your unit's location (move onto its tile first).");
            return null;
        }

        private static string TaskShort(Task t)
        {
            if (t == null) return null;
            try { return t.getShort(); } catch { return t.GetType().Name; }
        }

        // ---------- end turn ----------

        private const int MaxTurnBatch = 10;

        private enum StepStatus { Advanced, GameOver, Blocked, NotAdvanced, Error }

        private static ToolResult EndTurn(GameContext ctx, Map map, bool force, JsonValue args)
        {
            World world = map.world;
            if (world == null) return ToolResult.Error("game world not ready");

            int count = args["count"].AsInt(1);
            if (count < 1) count = 1;
            if (count > MaxTurnBatch) count = MaxTurnBatch;
            int motivationStopPct = args["stopOnThreatMotivation"].AsInt(0);

            // Single turn: preserve the original result shapes exactly, plus a threatAlert if a hero began
            // hunting one of your agents this turn.
            if (count == 1)
            {
                var before1 = Summaries.ComputeAgentSafety(ctx, map);
                StepStatus st1;
                JsonValue payload1 = AdvanceOneTurn(ctx, map, world, force, applyResolve: true, args, out st1);
                if (st1 == StepStatus.Error) return ToolResult.Error(payload1["error"].AsString());
                if (st1 == StepStatus.Advanced)
                {
                    JsonValue alert1; string reason1;
                    Summaries.EvaluateThreatStop(ctx, map, before1, args["stopOnThreatMotivation"].AsInt(0), out alert1, out reason1);
                    if (!alert1.IsNull) payload1.Set("threatAlert", alert1);
                }
                JsonValue tips1 = TipEngine.CollectContextual(ctx);
                if (!tips1.IsNull) payload1.Set("tips", tips1);
                return ToolResult.Ok(payload1);
            }

            // Multi-turn batch: advance up to count turns, stopping early on a decision, game over, or a
            // threat escalation so a batched advance never blows past an agent walking into danger.
            var before = Summaries.ComputeAgentSafety(ctx, map);
            int advancedBy = 0;
            string stopReason = null;
            JsonValue firstResolved = JsonValue.Null;
            JsonValue lastAutoDismiss = JsonValue.Null;
            JsonValue pending = JsonValue.Null;
            JsonValue threatAlert = JsonValue.Null;
            JsonValue gameOverPayload = JsonValue.Null;

            for (int i = 0; i < count; i++)
            {
                StepStatus st;
                JsonValue payload = AdvanceOneTurn(ctx, map, world, force, applyResolve: i == 0, args, out st);
                if (st == StepStatus.Error && payload["transient"].AsBool())
                {
                    // Benign mid-tick collision (an event mutated a world collection): retry this turn once
                    // before giving up. Any resolveOptionIndex was already applied on the first attempt.
                    payload = AdvanceOneTurn(ctx, map, world, force, applyResolve: false, args, out st);
                }

                if (st == StepStatus.Error)
                {
                    if (advancedBy == 0) return ToolResult.Error(payload["error"].AsString());
                    stopReason = "error"; break;
                }
                if (st == StepStatus.GameOver) { gameOverPayload = payload; stopReason = "gameOver"; break; }
                if (st == StepStatus.Blocked) { pending = payload["pendingDecision"]; stopReason = "decision"; break; }
                if (st == StepStatus.NotAdvanced) { stopReason = payload["reason"].AsString("notAdvanced"); break; }

                // Advanced.
                advancedBy++;
                if (i == 0 && !payload["resolved"].IsNull) firstResolved = payload["resolved"];
                if (!payload["autoDismissed"].IsNull) lastAutoDismiss = payload["autoDismissed"];
                // A decision may have popped mid-processing even though the turn advanced; stop and surface it.
                if (!payload["pendingDecision"].IsNull) { pending = payload["pendingDecision"]; stopReason = "decision"; break; }

                // Threat early-stop: meaningful danger (agent becomes huntable / an in-range hunter it is
                // not favoured against / worse odds), plus the opt-in rising-motivation tripwire.
                JsonValue alert; string reason;
                Summaries.EvaluateThreatStop(ctx, map, before, motivationStopPct, out alert, out reason);
                if (reason != null) { threatAlert = alert; stopReason = reason; break; }
            }

            JsonValue result = JsonValue.NewObject()
                .Set("turn", map.turn)
                .Set("advancedBy", advancedBy)
                .Set("requestedCount", count)
                .Set("stoppedEarly", advancedBy < count);
            if (advancedBy < count && stopReason != null) result.Set("stopReason", stopReason);
            if (!firstResolved.IsNull) result.Set("resolved", firstResolved);
            if (!lastAutoDismiss.IsNull && lastAutoDismiss["count"].AsInt(0) > 0) result.Set("autoDismissed", lastAutoDismiss);
            if (!pending.IsNull) result.Set("pendingDecision", pending); // already decorated by AdvanceOneTurn
            if (!threatAlert.IsNull) result.Set("threatAlert", threatAlert);
            if (!gameOverPayload.IsNull)
            {
                result.Set("gameOver", true)
                      .Set("outcome", gameOverPayload["outcome"])
                      .Set("victoryMode", gameOverPayload["victoryMode"]);
            }
            JsonValue tips = TipEngine.CollectContextual(ctx);
            if (!tips.IsNull) result.Set("tips", tips);
            return ToolResult.Ok(result);
        }

        /// <summary>Advance exactly one turn. Returns the per-turn payload and sets <paramref name="status"/>
        /// so the caller (single call or batch loop) knows whether it advanced, hit game over, is blocked by
        /// a decision, made partial progress, or errored. Mirrors the game's own end-turn guard sequence.</summary>
        private static JsonValue AdvanceOneTurn(GameContext ctx, Map map, World world, bool force, bool applyResolve, JsonValue args, out StepStatus status)
        {
            // The game is over: stop advancing and say so unmistakably. Losing your agents is NOT this -
            // only Overmind.endOfGameAchieved (heroes reforged the seals / fulfilled the prophecy, or you won).
            Overmind om = map.overmind;
            if (om != null && om.endOfGameAchieved)
            {
                status = StepStatus.GameOver;
                string vm = om.victoryAchieved ? Summaries.VictoryModeLabel(om.victoryMode) : null;
                return JsonValue.NewObject()
                    .Set("gameOver", true)
                    .Set("advanced", false)
                    .Set("outcome", om.victoryAchieved ? "victory" : "defeat")
                    .Set("victoryMode", vm)
                    .Set("turn", map.turn)
                    .Set("message", om.victoryAchieved
                        ? "You have won - the game is over. Further turns do nothing."
                        : "You have been defeated - the game is over. Further turns do nothing.");
            }

            // If the caller passed a choice for a blocking decision, answer it before advancing (first
            // iteration of a batch only). This lets an agent resolve popups through end_turn alone, without
            // loading the get_pending_decision / resolve_decision tools (which some MCP clients never load).
            JsonValue resolved = JsonValue.Null;
            if (applyResolve && !args["resolveOptionIndex"].IsNull &&
                !Decisions.DecisionRegistry.FullOrNull(ctx).IsNull)
            {
                JsonValue rargs = JsonValue.NewObject().Set("optionIndex", args["resolveOptionIndex"]);
                ToolResult rr = Decisions.DecisionRegistry.Resolve(ctx, rargs);
                resolved = JsonValue.NewObject()
                    .Set("ok", rr != null && !rr.IsError)
                    .Set("detail", rr != null ? rr.Text : null);
            }

            // Pending agent combat must NEVER be auto-resolved — even under force=true. Unlike idle agents /
            // unspent skill points / informational popups (which the game's force path legitimately pushes
            // through), a battle is a real tactical choice, and World.bEndTurn(force) would silently fight it via
            // BattleAgents.automatic(). So while combat is pending we DENY force to bEndTurn: bEndTurn(false) pops
            // the battle (or just stops on whatever popup is on top) instead of automatic()-ing it, and the turn
            // cannot advance. Detect it directly — an agent still engaged this turn, or an already-open battle
            // popup — so a message/death popup sitting on top can't mask the engagement and let force slip through.
            bool combatEngaged = AnyAgentEngaged(map);
            if (!combatEngaged)
            {
                JsonValue pdCombat = Decisions.DecisionRegistry.FullOrNull(ctx);
                combatEngaged = !pdCombat.IsNull && pdCombat["kind"].AsString() == "combat";
            }

            int before = map.turn;
            int after;
            JsonValue autoDismiss;
            try
            {
                // Deny force while combat is pending so bEndTurn pops the battle instead of auto-resolving it.
                world.bEndTurn(force && !combatEngaged);
                after = map.turn;

                // Capture this turn's status messages (idle agents, wars, seals, hero actions) into the
                // mod's own recent-events feed before the next turnTick wipes map.turnUnifiedMessages.
                ctx.Events.SnapshotTurn(after, map.turnUnifiedMessages);

                // With force=true, clear purely-informational popups (agent deaths, message boxes) that turn
                // processing may have raised, so an unattended end_turn(force) loop never stalls on a notice.
                autoDismiss = force
                    ? Decisions.DecisionRegistry.AutoDismissInformational(ctx)
                    : JsonValue.Null;
            }
            catch (System.InvalidOperationException)
            {
                // Turn processing can race with itself when an event mutates a world collection mid-tick
                // (e.g. a civil-war resolution creates a new society while social groups are being
                // enumerated). It is transient and self-healing; report cleanly and let the caller retry.
                status = StepStatus.Error;
                return JsonValue.NewObject()
                    .Set("transient", true)
                    .Set("error", "turn processing hit a transient state change (an " +
                    "event altered the world mid-turn). No stable result this call - re-check game_overview " +
                    "and call end_turn again.");
            }

            if (after > before)
            {
                status = StepStatus.Advanced;
                JsonValue result = JsonValue.NewObject()
                    .Set("turn", after)
                    .Set("advancedBy", after - before);
                if (!resolved.IsNull) result.Set("resolved", resolved);
                if (!autoDismiss.IsNull && autoDismiss["count"].AsInt(0) > 0)
                    result.Set("autoDismissed", autoDismiss);
                // A fresh decision may have popped during processing (e.g. an event's follow-up).
                JsonValue nowPending = Decisions.DecisionRegistry.FullOrNull(ctx);
                if (!nowPending.IsNull) result.Set("pendingDecision", DecorateResolveHint(nowPending));
                return result;
            }

            // Turn did not advance. If a decision is blocking, surface it with its options so the agent can
            // answer it via resolveOptionIndex. Resolving a decision can chain into a follow-up popup still
            // in the delayed queue; promote it before concluding the turn is stuck.
            JsonValue pending = Decisions.DecisionRegistry.FullOrNull(ctx);
            if (pending.IsNull)
            {
                Decisions.DecisionRegistry.PumpQueue(ctx);
                pending = Decisions.DecisionRegistry.FullOrNull(ctx);
            }
            if (!pending.IsNull)
            {
                status = StepStatus.Blocked;
                JsonValue result = JsonValue.NewObject()
                    .Set("advanced", false)
                    // Call combat out by name so the agent (and the batch stopReason) sees why force didn't skip it.
                    .Set("blockedBy", pending["kind"].AsString() == "combat" ? "combat" : "decision")
                    .Set("pendingDecision", DecorateResolveHint(pending));
                if (!resolved.IsNull) result.Set("resolved", resolved);
                return result;
            }

            // We answered a decision but the turn still hasn't advanced and nothing is pending: that is
            // progress, not a failure. Report it cleanly and actionably (call end_turn again).
            if (!resolved.IsNull)
            {
                status = StepStatus.NotAdvanced;
                return JsonValue.NewObject()
                    .Set("advanced", false)
                    .Set("turn", after)
                    .Set("reason", "decisionAnswered")
                    .Set("resolved", resolved)
                    .Set("hint", "decision answered, but the turn has not advanced yet - call end_turn again to continue.");
            }

            // Some other guard fired: report which one.
            status = StepStatus.Error;
            string diag = DiagnoseEndTurnBlock(map, world);
            return JsonValue.NewObject().Set("error", "the turn did not advance: " + diag);
        }

        /// <summary>Tag a pending-decision object with how to answer it through end_turn.</summary>
        private static JsonValue DecorateResolveHint(JsonValue pending)
        {
            if (!pending.IsNull)
                pending.Set("resolveHint", "call end_turn again with resolveOptionIndex set to the index " +
                    "of your chosen option (or force=true to skip/dismiss where allowed).");
            return pending;
        }

        /// <summary>True while any of your agents is still locked in an unresolved duel this turn (the fight-icon
        /// condition: a commandable UA engaged by a live UA, turnLastEngaged == this turn). Used to deny force to
        /// World.bEndTurn so a pending battle is never auto-resolved via BattleAgents.automatic() — the agent must
        /// fight, flee, or retreat it. Detected directly (not via the current blocker) so a message/death popup on
        /// top cannot mask the engagement.</summary>
        private static bool AnyAgentEngaged(Map map)
        {
            if (map == null || map.automatic || map.units == null) return false;
            foreach (Unit u in map.units)
            {
                if (u == null || u.isDead || !u.isCommandable()) continue;
                if (u is UA && u.engagedBy is UA att && !att.isDead && u.turnLastEngaged == map.turn) return true;
            }
            return false;
        }

        private static string DiagnoseEndTurnBlock(Map map, World world)
        {
            if (world.turnLock) return "turn processing is already underway";
            if (world.ui != null && world.ui.blocker != null)
                return "a dialog is open in the game (resolve it in-game, or retry with force=true)";
            if (world.selector != null)
                return "a targeting selector is active in the game UI";
            foreach (Unit u in map.units)
            {
                if (!u.isCommandable() || u.isDead) continue;
                if (u.turnLastEngaged == map.turn && u.engagedBy != null && !u.engagedBy.isDead)
                    return u.getName() + " is under attack by " + u.engagedBy.getName() +
                        " - resolve the battle (get_pending_decision, then resolve_decision to fight, flee, or retreat)";
                if (u is UA && u.person != null && u.person.skillPoints > 0 && !u.person.cachedOutOfTraits)
                    return u.getName() + " has unspent skill points (force=true auto-spends them)";
                if (world.option_idleAlert && u.task == null && u.movesTaken == 0)
                    return u.getName() + " is idle and the idle-agent alert is on (give it an order, " +
                        "pass it via resolve_decision, or force=true)";
            }
            return "unknown guard - check the game window for popups";
        }

        // ---------- shared helpers ----------

        private static ToolResult ResolveCommandable(GameContext ctx, string id, out Unit unit)
        {
            unit = Summaries.ResolveId(ctx, id) as Unit;
            if (unit == null)
                return ToolResult.Error("unknown or stale unit id: " + id + " - re-run list_units");
            if (unit.isDead)
                return ToolResult.Error(unit.getName() + " is dead.");
            if (!unit.isCommandable())
                return ToolResult.Error(unit.getName() + " is not under your command.");
            return null;
        }

        /// <summary>The UI warns before abandoning a challenge with more than ~4 turns of progress.</summary>
        private static string AbandonWarning(Unit u)
        {
            Task_PerformChallenge pc = u.task as Task_PerformChallenge;
            UA ua = u as UA;
            if (pc == null || ua == null) return null;
            double turnsOfProgress = pc.progress / Math.Max(1.0, pc.challenge.getProgressPerTurn(ua, null));
            if (turnsOfProgress > 4.0 && !pc.challenge.ignoreInterruptionWarning())
            {
                return u.getName() + " is performing '" + pc.challenge.getName() + "' with " +
                    (int)Math.Ceiling(turnsOfProgress) + " turns of progress that would be lost. " +
                    "Retry with force=true to abandon it.";
            }
            return null;
        }

        private static void CheckUiData(Map map)
        {
            try
            {
                if (map.world != null && map.world.ui != null) map.world.ui.checkData();
            }
            catch
            {
                // UI refresh is cosmetic; never fail a tool because of it.
            }
        }
    }
}
