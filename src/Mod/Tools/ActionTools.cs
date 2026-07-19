using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

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
                    Schema.Prop("challengeId", Schema.String("Challenge id from list_challenges, e.g. C8"), required: true),
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

            // Registered as a server-thread tool: end-turn processing can exceed the normal
            // per-tool timeout, so it dispatches its own job with the longer budget.
            host.RegisterServerThread(new ToolDefinition(
                "end_turn",
                "End your turn (runs the full turn processing; may take a few seconds). With force=true, " +
                "auto-resolves whatever blocks the turn end (pending battles are fought automatically, " +
                "skill points auto-spent, idle-agent warnings skipped) - same as the game's own force path.",
                Schema.Object(
                    Schema.Prop("force", Schema.Boolean("Push through battle/level-up/idle-agent interruptions"))),
                a =>
                {
                    bool force = a["force"].AsBool();
                    return ctx.Dispatcher.Run(
                        () => QueryTools.WithMap(ctx, map => EndTurn(ctx, map, force)),
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
                    " and must resolve this combat first (end_turn with force=true auto-resolves it).");
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

            Challenge c = Summaries.ResolveId(ctx, a["challengeId"].AsString()) as Challenge;
            if (c == null)
                return ToolResult.Error("unknown or stale challenge id: " + a["challengeId"].AsString() +
                    " - re-run list_challenges");

            UA ua = u as UA;
            UM um = u as UM;
            if (ua == null && um == null)
                return ToolResult.Error(u.getName() + " cannot perform challenges.");

            // Guards, mirroring UA/UM.playerTriesToStartChallenge:
            if (u.engagedBy != null && u.turnLastEngaged == map.turn)
                return ToolResult.Error(u.getName() + " is under attack by " + u.engagedBy.getName() +
                    " and must resolve this combat first.");
            if (!c.valid())
                return ToolResult.Error("the requirements to enable challenge '" + c.getName() + "' are not met.");
            if (ua != null && !c.validFor(ua))
                return ToolResult.Error(u.getName() + " does not meet the requirements for '" + c.getName() + "'.");
            if (um != null && !c.validFor(um))
                return ToolResult.Error(u.getName() + " does not meet the requirements for '" + c.getName() + "'.");
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

        // ---------- end turn ----------

        private static ToolResult EndTurn(GameContext ctx, Map map, bool force)
        {
            World world = map.world;
            if (world == null) return ToolResult.Error("game world not ready");

            int before = map.turn;
            world.bEndTurn(force);
            int after = map.turn;

            // With force=true, clear purely-informational popups (agent deaths, message boxes) that turn
            // processing may have raised, so an unattended end_turn(force) loop never stalls on a notice.
            // A popup carrying a real choice is left open and surfaced via the pending-decision banner.
            JsonValue autoDismiss = force
                ? Decisions.DecisionRegistry.AutoDismissInformational(ctx)
                : JsonValue.Null;

            if (after > before)
            {
                JsonValue result = JsonValue.NewObject()
                    .Set("turn", after)
                    .Set("advancedBy", after - before);
                if (!autoDismiss.IsNull && autoDismiss["count"].AsInt(0) > 0)
                    result.Set("autoDismissed", autoDismiss);
                return ToolResult.Ok(result);
            }

            // bEndTurn returned without advancing: report which of its guards fired.
            string reason = DiagnoseEndTurnBlock(map, world);
            return ToolResult.Error("the turn did not advance: " + reason);
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
                    return u.getName() + " is engaged in combat by " + u.engagedBy.getName() +
                        " (force=true auto-resolves the battle)";
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
