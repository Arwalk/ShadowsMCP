using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;
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
            host.RegisterMutating(new ToolDefinition(
                "move_unit",
                "Order one of your agents to travel to a location (pathfinds automatically; moves immediately " +
                "with any moves left this turn, then continues each turn). Ordering a unit to its current " +
                "location cancels its movement order.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your unit's id, e.g. U17"), required: true),
                    Schema.Prop("locationId", Schema.String("Destination location id, e.g. L3"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon an in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map => MoveUnit(ctx, map, a))));

            host.RegisterMutating(new ToolDefinition(
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

            host.RegisterMutating(new ToolDefinition(
                "perform_challenge",
                "Order one of your units to perform a challenge or ritual (from list_challenges). If the unit " +
                "is elsewhere, it travels there first and then begins.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your unit's id, e.g. U17"), required: true),
                    Schema.Prop("challengeId", Schema.String("Challenge id from list_challenges (stable across turns and save/load; no need to re-list before performing)"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon an in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map => PerformChallenge(ctx, map, a))));

            host.RegisterMutating(new ToolDefinition(
                "use_power",
                "Cast one of your god's powers (see list_powers) on a target unit or location. The cost is " +
                "deducted from your power resource.",
                Schema.Object(
                    Schema.Prop("powerId", Schema.String("Power id from list_powers, e.g. PW2 (stable across turns and seal breaks; ids may be non-sequential) or name (case-insensitive)"), required: true),
                    Schema.Prop("targetUnitId", Schema.String("Target unit id, e.g. U17")),
                    Schema.Prop("targetLocationId", Schema.String("Target location id, e.g. L3"))),
                a => QueryTools.WithMap(ctx, map => UsePower(ctx, map, a))));

            host.RegisterMutating(new ToolDefinition(
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

            host.RegisterMutating(new ToolDefinition(
                "command_army",
                "Issue a military unit's special order (armies like an awakened god-army or orc raiders; " +
                "available orders appear under 'orders' in the unit views). order=raze devours the human " +
                "settlement the unit stands on (move onto it first; defences fall each turn until destroyed); " +
                "order=drive_back forces an enemy hero on the tile to retreat and drop its task; order=attack " +
                "battles an enemy army on the tile. Once a battle starts the army is COMMITTED - no retreat; " +
                "it auto-resolves one cycle per end_turn until a side is destroyed or routs, and the unit " +
                "accepts no orders meanwhile (only agent duels can flee).",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your military unit's id, e.g. U17"), required: true),
                    Schema.Prop("order", Schema.StringEnum("Which order to issue", "raze", "drive_back", "attack"), required: true),
                    Schema.Prop("targetUnitId", Schema.String("For drive_back/attack: the enemy unit sharing your unit's tile, e.g. U9 (ignored for raze)"))),
                a => QueryTools.WithMap(ctx, map => CommandArmy(ctx, map, a))));

            host.RegisterMutating(new ToolDefinition(
                "command_agent",
                "Act on ANOTHER agent on the same tile as one of your agents (move_unit there first). " +
                "order=attack duels an enemy hero and CANCELS both sides' in-progress challenges permanently - " +
                "even if you flee or lose (the standard way to break a ritual you cannot otherwise stop; " +
                "compare both units' combat.dangerEstimate AND combat.minionScreen first - see get_tips " +
                "id=agent_can_attack). " +
                "order=rob steals items from a weaker enemy (you must be HIGHER level; once per 5 turns; " +
                "raises your profile and menace). order=trade moves items/gold between two of YOUR OWN " +
                "agents. order=follow makes a Harvester shadow a merchant. attack/rob/trade open a menu " +
                "returned inline as pendingDecision - drive it with resolve_decision. Available orders appear " +
                "under 'orders' in the unit views.",
                Schema.Object(
                    Schema.Prop("unitId", Schema.String("Your agent's id, e.g. U17"), required: true),
                    Schema.Prop("order", Schema.StringEnum("Which action to take against the target agent",
                        "attack", "rob", "trade", "follow"), required: true),
                    Schema.Prop("targetUnitId", Schema.String("The other agent sharing your agent's tile, e.g. U9 (an enemy hero for attack/rob, one of your own agents for trade)"), required: true),
                    Schema.Prop("force", Schema.Boolean("Abandon your agent's own in-progress challenge without confirmation"))),
                a => QueryTools.WithMap(ctx, map => CommandAgent(ctx, map, a))));

            // Registered as a server-thread tool: end-turn processing can exceed the normal
            // per-tool timeout, so it dispatches its own job with the longer budget.
            host.RegisterServerThreadMutating(new ToolDefinition(
                "end_turn",
                "End your turn (runs the full turn processing; may take a few seconds). " +
                "A blocking decision popup is returned with its options instead of advancing (also in " +
                "game_overview.pendingDecision); answer it by passing resolveOptionIndex (a failed or " +
                "unneeded resolve is reported in resolveWarning, never silently ignored). " +
                "force=true auto-resolves ONLY what carries no choice: purely-informational popups are " +
                "dismissed (message boxes, death notices; the periodic autosave is written to disk " +
                "first). Everything with a real choice blocks even under force: a pending agent battle " +
                "(blockedBy:\"combat\" - fight, flee, or retreat), the idle-agent alert (kind:\"idleAgents\" " +
                "- give idle agents orders, or resolveOptionIndex 0 passes them all), narrative events " +
                "(kind:\"event\", including the Defeat event of a lost battle), ANY level-up " +
                "(force blocks with forceDenied:\"startingTraitPick\" on the one-shot starting-trait/" +
                "magic-mastery menu and forceDenied:\"traitPick\" on a regular level-up - answer the popup " +
                "to choose the trait yourself, or pass forceSpendsRegularTraits:true to let force " +
                "auto-spend regular picks on an AI-chosen trait, each named in " +
                "digest.autoResolvedLevelUps), " +
                "and any other choice popup (an open level-up trait pick, trading, list selections). " +
                "Idle recurs every turn, so a count>1 batch stops on the first idle turn unless every agent " +
                "holds a standing order or you pass passIdleAgents:true. " +
                "passRoutineEvents:true additionally auto-answers a curated whitelist of recurring " +
                "low-stakes mid-challenge events with a fixed sensible option (each reported in " +
                "digest.autoResolvedEvents); all other events still block. " +
                "count advances several turns (force=true recommended); the batch stops early with a " +
                "stopReason on any decision, game over, the loss of one of your units " +
                "(stopReason:\"unitLost\"), or a meaningful threat escalation (threatAlert names the agents " +
                "affected and why); set stopOnThreatMotivation to make a hunter-motivation percentage the " +
                "ONLY motivation/danger threat stop instead. Separately from that threshold, a hero " +
                "STARTING an attack-pursuit of one of your units always stops the batch " +
                "(stopReason:\"heroAttacking\", payload names hunter/target/turnsRemaining) - the one " +
                "window to react before it arrives; stopOnHeroAttacking:false opts out. " +
                "EVERY call returns a 'digest' covering every turn of the batch - " +
                "digest.dismissed (each popup force cleared), digest.events (notable news; your units " +
                "tagged mine:true), digest.lost (your units that died). Read it: it is the only place a " +
                "batch's news appears in full (get_recent_events keeps the unabridged log). A 'tips' array " +
                "may explain a mechanic that just became relevant.",
                Schema.Object(
                    Schema.Prop("count", Schema.Integer("Advance up to this many turns (default 1, max 10); stops early per the rules above, and the digest covers every turn advanced.")),
                    Schema.Prop("force", Schema.Boolean("Dismiss informational popups; never skips a real choice (see tool description) - in particular ANY pending level-up (starting-trait or regular) blocks instead of being auto-spent, unless the matching forceSpends* flag opts back in. Every dismissal is named in the digest - nothing is lost.")),
                    Schema.Prop("forceSpendsStartingTraits", Schema.Boolean("Default false: an agent's FIRST level-up - the one-shot starting-trait (magic mastery) menu - blocks force so you can choose it yourself. Pass true to restore the old behaviour and let force auto-spend even that pick on an AI-chosen trait (permanently forfeiting the mastery choice).")),
                    Schema.Prop("forceSpendsRegularTraits", Schema.Boolean("Default false: a REGULAR level-up (any unspent skill point after the starting pick) blocks force so you choose the trait yourself (forceDenied:\"traitPick\"). Pass true to let force auto-spend one point per agent per turn on an AI-chosen trait (each named in digest.autoResolvedLevelUps) - useful for long unattended batches where a random trait beats stopping every level-up.")),
                    Schema.Prop("passIdleAgents", Schema.Boolean("Bulk-pass every idle agent each turn (a visible 'Passing Turn') so a batch doesn't stop on the recurring idle alert - including agents that go idle MID-batch (e.g. a challenge completes). A conscious choice to waste those turns - prefer standing orders. Combat and events still block.")),
                    Schema.Prop("passRoutineEvents", Schema.Boolean("Auto-answer a curated whitelist of recurring low-stakes mid-challenge events ('Watched' -> Silence them, 'Life Continues' -> Subtly disrupt the party, 'Merchant of Antiquities' -> refuse) with a fixed sensible option so they don't stop the batch. Every auto-answer is reported in digest.autoResolvedEvents (title, chose, outcome). All other events still block normally.")),
                    Schema.Prop("resolveOptionIndex", Schema.Integer("Answer the blocking decision with this option index (from pendingDecision.options), then continue ending the turn.")),
                    Schema.Prop("resolveOptionLabel", Schema.String("Answer the blocking decision by option LABEL instead of index (exact match preferred, else unique substring; case-insensitive) - safer on lists whose indices shift between reads.")),
                    Schema.Prop("expectedDecisionId", Schema.String("Strongly recommended with resolveOptionIndex/-Label: pass pendingDecision.decisionId. Guarantees the answer only ever lands on that exact decision (a mismatch clicks nothing, reported in resolveWarning) AND arms the retry that makes the answer land even when the decision is briefly absent when the call starts or only (re)appears during turn processing - without it, that situation wastes the answer with a 'no decision was pending' warning.")),
                    Schema.Prop("confirmDiscard", Schema.Boolean("With resolveOptionIndex/-Label on an item-trading decision: confirm closing a trade window whose 'Discard Items' side still holds items, deliberately releasing them to the world.")),
                    Schema.Prop("stopOnThreatMotivation", Schema.Integer("Your threat-stop threshold: when set (>0) the batch stops for threats ONLY once a hunter's motivation toward one of your agents is AT OR ABOVE this percent (level-triggered; can exceed 100 for a strongly-inclined hunter) - it REPLACES the default new-hunter/worse-odds stops, so batches no longer halt on every minor escalation. Omit or 0 for the default: stop on any meaningful danger change. The heroAttacking stop is separate and unaffected by this threshold.")),
                    Schema.Prop("stopOnHeroAttacking", Schema.Boolean("Default true: the batch stops (stopReason:\"heroAttacking\") the turn a hero STARTS an attack-pursuit of one of your agents or servants - the only window to react (reposition, Lay Low, bodyguard, or a power that targets attacking heroes) before it closes. Edge-triggered: a hunt already running when the batch starts does not re-stop it. Independent of stopOnThreatMotivation. Pass false to opt out."))),
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

            // Hero-side ("good") challenges are hidden from the player's agents in the game UI —
            // performing one would undo the player's own work. Never start one via a remembered id.
            if (Summaries.IsHeroOnly(c))
                return ToolResult.Error("'" + c.getName() + "' is a heroes-only challenge - your agents cannot perform it.");

            // Guards, mirroring UA/UM.playerTriesToStartChallenge:
            if (u.engagedBy != null && u.turnLastEngaged == map.turn)
                return ToolResult.Error(u.getName() + " is under attack by " + u.engagedBy.getName() +
                    " and must resolve this combat first (get_pending_decision, then resolve_decision).");
            // Surface the game's own reason text (getRestriction) so a rejected attempt says WHY, not just
            // "requirements not met" - e.g. "Requires 100% Infiltration. Cannot perform if Ward > 50%".
            string restr;
            try { restr = c.getRestriction(); } catch { restr = null; }
            // Where a per-clause evaluator exists, itemize which clause failed ([X]/[OK], failed
            // first) instead of re-stating the whole restriction: game 16 abandoned a viable
            // Plague Ships line because the refusal restated all three clauses when only one
            // (unknowable which) had failed.
            string clauses = Summaries.ChallengeRequirementsText(c);
            if (clauses != null) restr = clauses;
            string why = string.IsNullOrEmpty(restr) ? "" : ": " + restr;
            // The summon's restriction ("a hero is currently binding the tome") is true but
            // unverifiable from the outside — attach the tome's actual observable state.
            if (c is Ch_SummonLaughingTome || c is Ch_ForciblySummonLaughingTome || c is Ch_CollectTome)
            {
                string tome = Summaries.LaughingTomeStatusText(ctx, map);
                if (tome != null) why += (why.Length == 0 ? ": " : ". ") + tome;
            }
            // Location challenges auto-travel (below); rituals never do — the same call shape fails
            // where a C*-id would have started a journey, and nothing used to say so (game 13 #9).
            if (c is Ritual)
                why += (why.Length == 0 ? ": " : ". ") + "Note: rituals are performed IN PLACE and are " +
                    "never auto-travelled (unlike location challenges); if the requirement is " +
                    "location-bound, move_unit to a qualifying location first, then retry this same Cr- id.";
            // SafeValid: never probe Ch_PlagueShips.valid() directly - checking it spreads plague.
            if (!Summaries.SafeValid(c))
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
            // NEVER for rituals: a ritual acts wherever its carrier stands and its stored location is a
            // dead placeholder (item rituals are constructed against map.locations[0] — I_LaughingTome.cs);
            // the game itself starts rituals in place with no location check (UA.playerTriesToStartChallenge,
            // and Task_GoToPerformChallenge converts a ritual to perform-in-place on its first tick).
            if (!(c is Ritual) && u.location != c.location)
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

            // Heat actually applied on completion (Task_PerformChallenge), not the AI-utility scores.
            int menaceOnCompletion, profileOnCompletion;
            try { menaceOnCompletion = c.getCompletionMenaceAfterDifficulty(); } catch { menaceOnCompletion = 0; }
            try { profileOnCompletion = c.getCompletionProfile(); } catch { profileOnCompletion = 0; }
            JsonValue started = JsonValue.NewObject()
                .Set("unit", Summaries.UnitRef(ctx, u))
                .Set("challenge", c.getName())
                .Set("status", "started")
                .Set("menaceGain", menaceOnCompletion)
                .Set("profileGain", profileOnCompletion);
            if (c is Ritual)
                started.Set("performedAt", Summaries.LocationRef(u.location));
            return ToolResult.Ok(started);
        }

        /// <summary>A stale/unknown-challenge error that also lists challenge ids+names the agent can retry
        /// with. The failed id encodes its target location ("C&lt;locIdx&gt;-..."), so list THAT location's
        /// challenges when it still resolves — not the unit's current location, which is unrelated when the
        /// unit was routed somewhere remote. Falls back to the unit's current location (labeled) otherwise.</summary>
        private static string StaleChallengeError(GameContext ctx, Unit u, string id)
        {
            // "C12-Ch_Foo-a1b2c3d4" → location index 12. Ritual ids ("Cr-...") don't encode a location.
            Location target = null;
            try
            {
                if (id != null && id.StartsWith("C") && !id.StartsWith("Cr-"))
                {
                    int dash = id.IndexOf('-');
                    int locIdx;
                    if (dash > 1 && int.TryParse(id.Substring(1, dash - 1), out locIdx))
                        target = Summaries.ResolveId(ctx, "L" + locIdx) as Location;
                }
            }
            catch { }

            Location loc = target ?? u.location;
            string targetName;
            try { targetName = target != null ? target.getName() : null; } catch { targetName = "that location"; }
            string where = target != null
                ? "at " + targetName + " (the location encoded in your id)"
                : "at " + u.getName() + "'s current location";
            // Enumerate with the SAME filters, sources and dedupe as list_challenges (hero-only and
            // non-army entries excluded, item rituals included), so this error and that tool can never
            // disagree about what exists here. The old version listed the raw location list with a cap
            // of 12: hero-side entries ate the cap and the player's real options fell off the end,
            // which read as "this challenge does not exist" (G14-#3/#14/#15).
            var lines = new List<string>();
            int total = 0, heroOnlyCount = 0;
            const int MaxLines = 24;
            var seen = new HashSet<string>();
            UA uaList = u as UA;
            UM umList = u as UM;
            Action<Challenge> addLine = c =>
            {
                string cid;
                try { cid = Summaries.ChallengeId(ctx, c); } catch { cid = null; }
                if (cid == null || !seen.Add(cid)) return; // interchangeable duplicates collapse
                total++;
                if (lines.Count < MaxLines)
                    lines.Add(cid + " (" + Summaries.ChallengeName(c) + ")");
            };
            try
            {
                if (loc != null)
                {
                    loc.populateStandardChallenges();
                    foreach (Challenge c in loc.GetChallenges())
                    {
                        if (c == null) continue;
                        if (uaList != null && Summaries.IsHeroOnly(c)) { heroOnlyCount++; continue; }
                        if (umList != null && !Summaries.OverridesValidForUM(c)) continue;
                        addLine(c);
                    }
                }
                if (u.rituals != null)
                    foreach (Challenge r in u.rituals)
                        if (r != null) addLine(r);
                // Item-granted rituals (Laughing Tome, banners…) are part of list_challenges' set too.
                if (uaList != null && uaList.person != null && uaList.person.items != null)
                    foreach (Item it in uaList.person.items)
                    {
                        if (it == null) continue;
                        List<Ritual> granted;
                        try { granted = it.getRituals(uaList); } catch { continue; }
                        if (granted == null) continue;
                        foreach (Ritual r in granted)
                            if (r != null) addLine(r);
                    }
            }
            catch { }
            string head = "unknown or stale challenge id: " + id + ". ";
            string tail = target != null && target != u.location
                ? " Note: " + u.getName() + " is not there; perform_challenge handles the travel itself."
                : "";
            // When the failed id's challenge TYPE is absent from the location's whole current list, the
            // offer itself has lapsed - say so, or the disappearance reads as a dead end (G14-#10:
            // Learn Secret vanished when the Arcane Secret was destroyed; Enshadow at max shadow).
            try
            {
                int d1 = id != null ? id.IndexOf('-') : -1;
                int d2 = id != null ? id.LastIndexOf('-') : -1;
                if (d1 > 0 && d2 > d1)
                {
                    string type = id.Substring(d1 + 1, d2 - d1 - 1);
                    bool stillOffered = false;
                    if (loc != null)
                        foreach (Challenge c in loc.GetChallenges())
                            if (c != null && c.GetType().Name == type) { stillOffered = true; break; }
                    if (!stillOffered && type.Length > 0)
                        tail += " The location no longer offers any '" + type + "' challenge at all: " +
                            "its enabling condition has lapsed since you read the id (typical causes: " +
                            "the thing it acted on was consumed or destroyed, or the location already " +
                            "reached the state the challenge creates - e.g. 100% shadow for Enshadow).";
                }
            }
            catch { }
            if (heroOnlyCount > 0)
                tail += " (" + heroOnlyCount + " heroes-only challenge(s) here are not listed - your " +
                    "agents cannot perform them; list_challenges names them under heroOnly.)";
            if (lines.Count > 0)
                return head + "Challenges " + where + " for " + u.getName() + " (same set as " +
                    "list_challenges, including its rituals): " + string.Join(", ", lines.ToArray()) +
                    (total > lines.Count
                        ? ", … plus " + (total - lines.Count) + " more - run list_challenges for the full set."
                        : ".") + tail;
            return head + "Re-run list_challenges for " + u.getName() + "." + tail;
        }

        // ---------- powers ----------

        private static ToolResult UsePower(GameContext ctx, Map map, JsonValue a)
        {
            if (map.overmind.god == null) return ToolResult.Error("no god selected yet");
            // Resolve against the master power list, whose indices match the PW ids emitted by
            // PowerSummary. The seal-filtered getPowers() list shifts as seals break.
            List<Power> powers = map.overmind.god.powers;

            string wanted = a["powerId"].AsString();
            if (string.IsNullOrEmpty(wanted)) return ToolResult.Error("missing 'powerId'");
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
            List<int> reqs = map.overmind.god.powerLevelReqs;
            if (index < reqs.Count && reqs[index] > map.overmind.sealsBroken)
                return ToolResult.Error("'" + power.getName() + "' is locked until " + reqs[index] +
                    " seals are broken (currently " + map.overmind.sealsBroken + ").");
            int cost = power.getCost();
            if (map.overmind.power < (double)cost)
                // Raw compare (matches the game, UIE_GodPower.cs:38-42); display FLOORS so it can
                // never read "costs 1, you have 1" while refusing (G17-#2).
                return ToolResult.Error("not enough power: '" + power.getName() + "' costs " + cost +
                    ", you have " + Summaries.Round2Down(map.overmind.power) + " (short " +
                    Summaries.Round2(cost - map.overmind.power) + "; power accrues each turn).");

            bool hasUnit = !a["targetUnitId"].IsNull;
            bool hasLoc = !a["targetLocationId"].IsNull;
            if (hasUnit == hasLoc)
                return ToolResult.Error("provide exactly one of targetUnitId or targetLocationId" +
                    (power.getRestrictionText() != null ? " (target: " + power.getRestrictionText() + ")" : ""));

            // Mirrors Sel_CastPower.onClick: validTarget then cast; castCommon deducts the cost.
            JsonValue notableDeaths = JsonValue.Null;
            string faithOutlook = null;
            if (hasUnit)
            {
                Unit target = Summaries.ResolveId(ctx, a["targetUnitId"].AsString()) as Unit;
                if (target == null) return ToolResult.Error(QueryTools.StaleUnitIdError(ctx, a["targetUnitId"].AsString()));
                if (!power.validTarget(target))
                    return ToolResult.Error(target.getName() + " is not a valid target for '" + power.getName() +
                        "': " + (power.getRestrictionText() ?? "see list_powers targetRestriction"));
                power.cast(target);
            }
            else
            {
                Location target = Summaries.ResolveId(ctx, a["targetLocationId"].AsString()) as Location;
                if (target == null) return ToolResult.Error("unknown location id: " + a["targetLocationId"].AsString());
                if (!power.validTarget(target))
                {
                    // Name the FAILED clause first (G17-#1: "Must be cast on land" for what was
                    // actually a distance refusal sent the playtester hunting for empty land).
                    // Falls back to the game's restriction text for powers without an evaluator.
                    string clauses = Summaries.PowerRequirementsText(map, power, target);
                    return ToolResult.Error(target.getName() + " is not a valid target for '" + power.getName() +
                        "': " + (clauses ?? power.getRestrictionText() ?? "see list_powers targetRestriction"));
                }
                // Destructive casts report who they are about to kill (G17-#11); snapshot BEFORE
                // cast - afterwards the settlement and ruler are gone.
                if (IsKnownDestructive(power))
                    notableDeaths = Summaries.NotableDeathsForManifestation(ctx, map, target);
                power.cast(target);
                // Start Faith's success payload is otherwise indistinguishable from a no-op (cost 0,
                // pool unchanged) while the 1% seed can be silently deleted within two turns by the
                // ruler-awareness drain (G18-#2) - always report the seed's projected balance.
                if (power is P_Opha_StartFaith)
                    faithOutlook = StartFaithOutlook(map, target);
            }
            CheckUiData(map);

            JsonValue ok = JsonValue.NewObject()
                .Set("cast", power.getName())
                .Set("cost", cost)
                .Set("remainingPower", Summaries.Round2Down(map.overmind.power));
            if (!notableDeaths.IsNull)
                ok.Set("notableDeaths", notableDeaths)
                  .Set("warning", "this cast destroyed the settlement: its population and ruler " +
                      "died (see notableDeaths)");
            if (faithOutlook != null)
                ok.Set("faithOutlook", faithOutlook);
            return ToolResult.Ok(ok);
        }

        /// <summary>Powers whose cast destroys a settlement and kills its residents outright -
        /// these get a pre-cast notableDeaths snapshot attached to the success payload (G17-#11).
        /// Extend by adding cases here.</summary>
        private static bool IsKnownDestructive(Power p)
        {
            return p is P_Vinerva_Manifestation;
        }

        /// <summary>
        /// Projected first-turn charge balance for a freshly cast Start Faith, mirroring
        /// Pr_Opha_Faith.turnTick's influence terms. The seed starts at 1% and has no base
        /// growth, so a net-negative balance means silent deletion within two turns with no
        /// message from the game (G18-#2).
        /// </summary>
        private static string StartFaithOutlook(Map map, Location loc)
        {
            try
            {
                if (!(loc.settlement is SettlementHuman sh)) return null;
                bool faithfulNation = loc.soc is Society soc && soc.isOphanimControlled;
                double net = 0.0;
                List<string> terms = new List<string>();
                if (faithfulNation) { net += 3.0; terms.Add("+3 Faithful Nation"); }
                else if (sh.ruler != null && sh.ruler.awareness > 0.0)
                {
                    double drain = 5.0 * sh.ruler.awareness;
                    net -= drain;
                    terms.Add("-" + Summaries.Round2(drain) + " Ruler Awareness (ruler is " +
                        (int)(sh.ruler.awareness * 100.0) + "% aware)");
                }
                foreach (Property pr in loc.properties)
                    if (pr is Pr_Opha_Doubt)
                    {
                        net -= pr.charge / 30.0;
                        terms.Add("-" + Summaries.Round2(pr.charge / 30.0) + " Doubters");
                    }
                bool nearbyShadow = false, neighbourFaith = false;
                foreach (Location n in loc.getNeighbours())
                {
                    foreach (Property pr in n.properties)
                        if (pr is Pr_Opha_Faith) neighbourFaith = true;
                    Settlement s = n.settlement;
                    if (s != null && s.shadowPolicy == Settlement.shadowResponse.FULL_FLOW && s.shadow >= 0.25)
                        nearbyShadow = true;
                }
                if (sh.shadow > map.param.prop_opha_faithWorldShadowReq) { net += 4.0; terms.Add("+4 Fear of Our Shadow"); }
                else if (nearbyShadow) { net += 2.0; terms.Add("+2 Fear of Nearby Shadow"); }
                else if (map.data_avrgEnshadowment > map.param.prop_opha_faithWorldShadowReq) { net += 1.0; terms.Add("+1 Fear of World Shadow"); }
                if (neighbourFaith) { net += 1.0; terms.Add("+1 Neighbouring Faith"); }
                string outlook = "seeded at 1% charge; projected " + (net >= 0.0 ? "+" : "") +
                    Summaries.Round2(net) + "/turn from " +
                    (terms.Count > 0 ? string.Join(", ", terms.ToArray()) : "no growth or drain terms yet");
                if (net <= 0.0)
                    outlook += ". WARNING: at this balance the Faith will be SILENTLY DELETED within a " +
                        "couple of turns. Lower the ruler's awareness (or remove the ruler), or raise " +
                        "shadow here, before reseeding.";
                return outlook;
            }
            catch { return null; }
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
                    return ToolResult.Error(QueryTools.StaleUnitIdError(ctx, a["heroUnitId"].AsString(),
                        "list_recruitable_agents"));
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
                    return ToolResult.Error(UnknownAgentCodeError(om, code));
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

        /// <summary>The "unknown agent code" error done right: codes are stable constants, but unique
        /// (positive-code) archetypes are REMOVED from the game's recruitable list after their single
        /// recruitment — so a code that worked earlier legitimately stops resolving. Name the archetype,
        /// say why it is gone, and list what is still recruitable.</summary>
        private static string UnknownAgentCodeError(Overmind om, int code)
        {
            string name = ArchetypeName(code);
            string msg;
            if (code > 0 && name != null)
                msg = "agent code " + code + " (" + name + ") is a unique archetype that is no longer " +
                    "available - uniques can be recruited only ONCE per game (it was already recruited, " +
                    "or this game's options exclude it).";
            else if (name != null)
                msg = "agent code " + code + " (" + name + ") is not available in this game.";
            else
                msg = "unknown agent code " + code + ".";
            var parts = new List<string>();
            try
            {
                foreach (UAE_Abstraction ab in om.agentsGeneric) parts.Add(ab.code + " (" + ab.getName() + ")");
                foreach (UAE_Abstraction ab in om.agentsUnique) parts.Add(ab.code + " (" + ab.getName() + ")");
            }
            catch { }
            if (parts.Count > 0)
                msg += " Archetypes recruitable right now: " + string.Join(", ", parts) +
                    " - see list_recruitable_agents.";
            else
                msg += " See list_recruitable_agents.";
            return msg;
        }

        /// <summary>Static code → display name (mirrors UAE_Abstraction.getName()), so a CONSUMED unique
        /// can still be named in errors after the game removed its abstraction from the list.</summary>
        private static string ArchetypeName(int code)
        {
            switch (code)
            {
                case -4: return "A Bandit King";
                case -3: return "A Warlock";
                case -2: return "A Warlord";
                case -1: return "A Heirophant";
                case 1: return "The Baroness";
                case 2: return "The Trickster";
                case 3: return "The Survivor";
                case 4: return "The Plague Doctor";
                case 5: return "The Courtier";
                case 6: return "The Monarch";
                case 7: return "The Cursed";
                case 8: return "The Harvester";
                case 9: return "The Buccaneer";
                case 10: return "The Dissident";
                case 11: return "The Shaman";
                case 12: return "The Aristocrat";
                case 13: return "The Spellbinder";
                case 14: return "The Exile";
                case 15: return "The Seeker";
                default: return null;
            }
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
                return ToolResult.Error(um.getName() + " cannot be given orders while in battle: armies are " +
                    "committed once a battle starts - there is no retreat order; the battle auto-resolves one " +
                    "cycle per end_turn (see get_unit's battle block). Only agent duels can flee.");

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
                    : QueryTools.StaleUnitIdError(ctx, id));
            if (target.isDead)
                return ToolResult.Error(target.getName() + " is dead.");
            if (target.location != um.location)
                return ToolResult.Error(target.getName() + " is not on " + um.getName() + "'s tile; these orders only " +
                    "apply to a unit sharing your unit's location (move onto its tile first).");
            return null;
        }

        // ---------- agent-vs-agent actions (attack / rob / trade / follow) ----------

        /// <summary>Act on another agent sharing your agent's tile - the four action boxes
        /// <c>UIScroll_Unit</c> builds by walking <c>ua.location.units</c> (Attack / Rob / Trade / Follow),
        /// each wired to a <c>UA.playerTriesTo*</c> method. Without this an agent could only ever be attacked,
        /// never attack: the whole offensive half of the agent layer had no verb. Every branch mirrors the
        /// exact guard + commit of its <c>UA.playerTriesTo*</c> method, returning a clean error where the game
        /// pops a message. <c>UA.playerTriesToDisrupt</c> is deliberately NOT exposed - it exists on UA but no
        /// UI path reaches it, and this layer only replicates actions a human player can take.</summary>
        private static ToolResult CommandAgent(GameContext ctx, Map map, JsonValue a)
        {
            Unit u;
            ToolResult err = ResolveCommandable(ctx, a["unitId"].AsString(), out u);
            if (err != null) return err;

            UA ua = u as UA;
            if (ua == null)
                return ToolResult.Error(u.getName() + " is not an agent; these actions belong to your agents " +
                    "(a UA). For a military unit's orders use command_army.");

            string order = a["order"].AsString();
            if (string.IsNullOrEmpty(order))
                return ToolResult.Error("missing 'order' - one of attack, rob, trade, follow.");
            order = order.ToLowerInvariant();

            UA target;
            ToolResult terr = ResolveAgentTarget(ctx, ua, a["targetUnitId"].AsString(), out target);
            if (terr != null) return terr;

            // Shared guard, first in every playerTriesTo* method: an agent under attack must fight first.
            if (ua.engagedBy != null && ua.turnLastEngaged == map.turn)
                return ToolResult.Error(ua.getName() + " is under attack by " + ua.engagedBy.getName() +
                    " and must resolve this combat before taking action (get_pending_decision, then " +
                    "resolve_decision to fight, flee, or retreat).");
            // Disruption blocks attack/rob/follow. playerTriesToTrade does NOT check it - neither do we.
            if (order != "trade" && ua.task is Task_Disrupted)
                return ToolResult.Error(ua.getName() + " is disrupted and cannot act this turn.");

            // Every branch below reads live game state (traits, tasks, minions, the popup layer). An
            // unexpected throw in any of it used to escape to MainThreadDispatcher and come back as a bare
            // "tool failed: Object reference not set…", naming neither the tool nor the order.
            try
            {
                return CommandAgentOrder(ctx, map, a, ua, target, order);
            }
            catch (Exception e)
            {
                Log.Error("command_agent " + order + " threw", e);
                return ToolResult.Error("command_agent " + order + " failed: " + Log.Describe(e));
            }
        }

        /// <summary>The per-order guards and commit, split out so <see cref="CommandAgent"/> can wrap the
        /// whole thing in one attributed try/catch. Every branch mirrors its <c>UA.playerTriesTo*</c>.</summary>
        private static ToolResult CommandAgentOrder(GameContext ctx, Map map, JsonValue a, UA ua, UA target,
            string order)
        {
            switch (order)
            {
                case "attack":
                {
                    if (target.isCommandable())
                        return ToolResult.Error(target.getName() + " is one of your own agents; you cannot attack " +
                            "it (use order=\"trade\" to swap items with it).");
                    if (target.engagedBy != null && target.turnLastEngaged == map.turn)
                        return ToolResult.Error(target.getName() + " is already being attacked by " +
                            target.engagedBy.getName() + "; that combat must be resolved before you can fight them.");
                    // A bodyguard on the target's tile must be beaten first (the guard's own duel).
                    foreach (Unit other in (target.location != null ? target.location.units : EmptyUnits))
                    {
                        UA guard = other as UA;
                        Task_Bodyguard bg = guard != null ? guard.task as Task_Bodyguard : null;
                        if (bg != null && bg.target == target)
                            return ToolResult.Error(target.getName() + " is guarded by " + guard.getName() +
                                ". Defeat the guard first: command_agent {unitId:" + Summaries.UnitId(ctx, ua) +
                                ", order:\"attack\", targetUnitId:" + Summaries.UnitId(ctx, guard) + "}.");
                    }
                    // The UI's popConfirmOrder branch: your own challenge progress is destroyed too
                    // (BattleAgents.setupBattle nulls a Task_PerformChallenge on BOTH sides).
                    string warning = AbandonWarning(ua);
                    if (warning != null && !a["force"].AsBool()) return ToolResult.Error(warning);

                    string targetTask = TaskShort(target.task);
                    try
                    {
                        // Exactly UA.playerTriesToAttack's commit.
                        target.task = null;
                        BattleAgents battle = new BattleAgents(ua, target);
                        map.world.prefabStore.popBattle(battle);
                    }
                    catch (Exception e)
                    {
                        return ToolResult.Error("could not start the battle: " + e.Message);
                    }
                    CheckUiData(map);

                    JsonValue res = JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, ua))
                        .Set("order", "attack")
                        .Set("target", Summaries.UnitRef(ctx, target))
                        .Set("cancelledTargetTask", targetTask)
                        .Set("status", "battle opened against " + target.getName() +
                            (targetTask != null
                                ? " - their task '" + targetTask + "' is cancelled, and stays cancelled even if you flee or lose"
                                : ""));
                    return ToolResult.Ok(AttachPending(ctx, res,
                        "the combat menu is open - see pendingDecision, then resolve_decision (fight to the " +
                        "end, step one exchange, or flee/retreat from round 2)."));
                }
                case "rob":
                {
                    if (target.isCommandable())
                        return ToolResult.Error(target.getName() + " is one of your own agents; use order=\"trade\" " +
                            "to move items between your agents.");
                    if (!(target is UAG) && !(target is UAA))
                        return ToolResult.Error(target.getName() + " cannot be robbed - only a merchant (UAG) or " +
                            "an adventurer/agent (UAA) carries items you can steal.");
                    if (target.person.level >= ua.person.level)
                        return ToolResult.Error(ua.getName() + " (level " + ua.person.level + ") must be a HIGHER " +
                            "level than " + target.getName() + " (level " + target.person.level + ") to steal from them.");
                    if (map.turn - ua.turnLastDidRobbery < 5 && ua.turnLastDidRobbery != 0)
                        return ToolResult.Error(ua.getName() + " robbed someone on turn " + ua.turnLastDidRobbery +
                            "; robberies are 5 turns apart - " + (5 - (map.turn - ua.turnLastDidRobbery)) +
                            " turn(s) to wait.");

                    try
                    {
                        // Exactly UA.playerTriesToRob's commit, in the same order.
                        ua.addProfile(map.param.ua_robProfileGain);
                        ua.addMenace(map.param.ua_robMenaceGain);
                        ua.turnLastDidRobbery = map.turn;
                        map.world.prefabStore.popItemTrade(ua.person, target.person, "Stealing Items");
                    }
                    catch (Exception e)
                    {
                        return ToolResult.Error("could not open the robbery: " + e.Message);
                    }
                    CheckUiData(map);

                    JsonValue res = JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, ua))
                        .Set("order", "rob")
                        .Set("target", Summaries.UnitRef(ctx, target))
                        .Set("profileGained", map.param.ua_robProfileGain)
                        .Set("menaceGained", map.param.ua_robMenaceGain)
                        .Set("status", "robbing " + target.getName() + " - the cost is already paid (+" +
                            map.param.ua_robProfileGain + " profile, +" + map.param.ua_robMenaceGain +
                            " menace), so take the items");
                    return ToolResult.Ok(AttachPending(ctx, res,
                        "the steal window is open - see pendingDecision, then resolve_decision (\"Take all\" " +
                        "pulls everything across, then Done)."));
                }
                case "trade":
                {
                    if (!target.isCommandable())
                        return ToolResult.Error(target.getName() + " is not one of your agents; trading moves items " +
                            "between two of YOUR OWN agents (use order=\"rob\" to steal from an enemy).");
                    try
                    {
                        map.world.prefabStore.popItemTrade(ua.person, target.person);
                    }
                    catch (Exception e)
                    {
                        return ToolResult.Error("could not open the trade: " + e.Message);
                    }
                    CheckUiData(map);

                    JsonValue res = JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, ua))
                        .Set("order", "trade")
                        .Set("target", Summaries.UnitRef(ctx, target))
                        .Set("status", "trading items between " + ua.getName() + " and " + target.getName());
                    return ToolResult.Ok(AttachPending(ctx, res,
                        "the trade window is open - see pendingDecision, then resolve_decision (rotate a side " +
                        "until the item you want is on top, swap, then Done)."));
                }
                case "follow":
                {
                    if (!(ua is UAE_Harvester))
                        return ToolResult.Error(ua.getName() + " cannot follow another agent - only a Harvester " +
                            "may shadow a merchant.");
                    if (!(target is UAG))
                        return ToolResult.Error(target.getName() + " is not a merchant (UAG); a Harvester can only " +
                            "follow a merchant.");
                    // The game also pops a confirmation message here; we skip it - a blocker the MCP would
                    // only have to dismiss again (unlike attack/rob/trade, whose popup IS the interaction).
                    ua.task = new Task_Follow(ua, target);
                    CheckUiData(map);
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("unit", Summaries.UnitRef(ctx, ua))
                        .Set("order", "follow")
                        .Set("target", Summaries.UnitRef(ctx, target))
                        .Set("task", TaskShort(ua.task))
                        .Set("status", ua.getName() + " now follows " + target.getName() +
                            ", moving to their location each time they move"));
                }
                default:
                    return ToolResult.Error("unknown order '" + order + "' - one of attack, rob, trade, follow.");
            }
        }

        /// <summary>Stand-in for a missing tile roster, so a location-less target can't NRE a guard loop.</summary>
        private static readonly List<Unit> EmptyUnits = new List<Unit>();

        /// <summary>Resolve a command_agent target and enforce that it is another live agent sharing the
        /// commander's tile (the game only builds these action boxes for units in <c>ua.location.units</c>).</summary>
        private static ToolResult ResolveAgentTarget(GameContext ctx, UA ua, string id, out UA target)
        {
            target = null;
            Unit t = Summaries.ResolveId(ctx, id) as Unit;
            if (t == null)
                return ToolResult.Error(string.IsNullOrEmpty(id)
                    ? "this order needs a targetUnitId (the other agent sharing your agent's tile) - see get_unit.orders."
                    : QueryTools.StaleUnitIdError(ctx, id));
            if (t.isDead)
                return ToolResult.Error(t.getName() + " is dead.");
            if (t == ua)
                return ToolResult.Error("an agent cannot act on itself; targetUnitId must be another agent.");
            // Write straight into the out param (as ResolveCommandable does): a local here silently left
            // `target` null on the success path, so EVERY command_agent order NRE'd on its first use of it.
            target = t as UA;
            if (target == null)
                return ToolResult.Error(t.getName() + " is not an agent; these actions only apply to another " +
                    "hero/agent (for an enemy army use command_army on a military unit of yours).");
            if (target.location != ua.location)
                return ToolResult.Error(target.getName() + " is not on " + ua.getName() + "'s tile (they are at " +
                    (target.location != null ? target.location.getName() : "?") + "). Move there first: move_unit " +
                    "{unitId:" + Summaries.UnitId(ctx, ua) + ", locationId:" +
                    (target.location != null ? Summaries.LocationId(target.location) : "L?") + "}.");
            return null;
        }

        /// <summary>Attach the popup a command_agent order just opened, so the combat/trade menu comes back in
        /// the SAME call instead of needing a get_pending_decision round-trip (which may not be loaded).</summary>
        private static JsonValue AttachPending(GameContext ctx, JsonValue result, string hint)
        {
            JsonValue pending = Decisions.DecisionRegistry.FullOrNull(ctx);
            if (pending.IsNull)
            {
                Decisions.DecisionRegistry.PumpQueue(ctx);
                pending = Decisions.DecisionRegistry.FullOrNull(ctx);
            }
            if (!pending.IsNull)
                result.Set("pendingDecision", Boilerplate.CompactDecision(ctx, pending)).Set("hint", hint);
            return result;
        }

        private static string TaskShort(Task t)
        {
            if (t == null) return null;
            try { return t.getShort(); } catch { return t.GetType().Name; }
        }

        // ---------- end turn ----------

        private const int MaxTurnBatch = 10;

        private enum StepStatus { Advanced, GameOver, Blocked, NotAdvanced, Error }

        /// <summary>
        /// Accumulates "what actually happened" across EVERY turn of one end_turn call, so a batched
        /// advance can no longer swallow the news. Three streams, each entry tagged with its turn:
        /// <c>dismissed</c> (the named popups force cleared - see
        /// <c>DecisionRegistry.AutoDismissInformational</c>'s <c>items</c>), <c>events</c> (the turn's
        /// notable unified messages - see <c>Summaries.NotableTurnEvents</c>), and <c>lost</c> (your units
        /// that died - see <c>Summaries.EvaluateUnitLoss</c>).
        ///
        /// Capped per stream to keep a 10-turn batch's response affordable; anything dropped is counted in
        /// <c>truncated</c> rather than silently discarded (the full history is always in
        /// <c>get_recent_events</c>). Losses are never capped - they are the whole point.
        /// </summary>
        private sealed class TurnDigest
        {
            private const int MaxDismissed = 20;
            private const int MaxEvents = 20;
            private const int MaxAutoResolved = 20;
            private const int MaxLevelUps = 10;

            private readonly JsonValue _dismissed = JsonValue.NewArray();
            private readonly JsonValue _events = JsonValue.NewArray();
            private readonly JsonValue _autoResolved = JsonValue.NewArray();
            private readonly JsonValue _levelUps = JsonValue.NewArray();
            private JsonValue _lost = JsonValue.Null;
            private int _truncated;

            /// <summary>Take one turn's named dismissals (<c>autoDismissed.items</c>) and notable events.</summary>
            public void Absorb(JsonValue dismissedItems, JsonValue events)
            {
                Append(_dismissed, dismissedItems, MaxDismissed);
                Append(_events, events, MaxEvents);
            }

            /// <summary>Routine events answered on the caller's behalf (passRoutineEvents) — each entry
            /// is {turn, title, chose, outcome?} so the opt-in trades attention, not information.</summary>
            public void AbsorbAutoResolved(JsonValue records)
            {
                Append(_autoResolved, records, MaxAutoResolved);
            }

            /// <summary>Skill points force auto-spent by the game's bEndTurn(force) path — each entry is
            /// {turn, unit, chose, ...}. Level-ups used to be the one force side effect no result named
            /// (G14-#5); since G16-#1 only REGULAR picks can reach this (a pending starting-trait/mastery
            /// pick denies force before bEndTurn).</summary>
            public void AbsorbAutoLevelUps(JsonValue records)
            {
                Append(_levelUps, records, MaxLevelUps);
            }

            public void SetLost(JsonValue lost) { _lost = lost; }

            private void Append(JsonValue into, JsonValue from, int cap)
            {
                if (from.IsNull) return;
                for (int i = 0; i < from.Count; i++)
                {
                    if (into.Count >= cap) { _truncated++; continue; }
                    into.Add(from[i]);
                }
            }

            /// <summary>The digest object, or null when the call had nothing to report.</summary>
            public JsonValue ToJson()
            {
                if (_dismissed.Count == 0 && _events.Count == 0 && _autoResolved.Count == 0 &&
                    _levelUps.Count == 0 && _lost.IsNull)
                    return JsonValue.Null;
                JsonValue o = JsonValue.NewObject();
                if (_dismissed.Count > 0) o.Set("dismissed", _dismissed);
                if (_events.Count > 0) o.Set("events", _events);
                if (_autoResolved.Count > 0) o.Set("autoResolvedEvents", _autoResolved);
                if (_levelUps.Count > 0) o.Set("autoResolvedLevelUps", _levelUps);
                if (!_lost.IsNull) o.Set("lost", _lost);
                if (_truncated > 0)
                    o.Set("truncated", _truncated)
                     .Set("truncatedHint", "older entries were dropped - call get_recent_events for the full log.");
                return o;
            }
        }

        private static ToolResult EndTurn(GameContext ctx, Map map, bool force, JsonValue args)
        {
            World world = map.world;
            if (world == null) return ToolResult.Error("game world not ready");

            int requestedCount = args["count"].AsInt(1);
            int count = requestedCount;
            if (count < 1) count = 1;
            if (count > MaxTurnBatch) count = MaxTurnBatch;
            int motivationStopPct = args["stopOnThreatMotivation"].AsInt(0);
            // Default ON, opt-out - and deliberately NOT governed by stopOnThreatMotivation's
            // "threshold replaces the default stops" rule: HERO_ATTACKING is the one reaction
            // window and batches ran straight through it for two games (G15-#1 / G16-#4).
            bool stopOnHeroAttacking = args["stopOnHeroAttacking"].AsBool(true);

            // Single turn: preserve the original result shapes exactly, plus a threatAlert if a hero began
            // hunting one of your agents this turn.
            if (count == 1)
            {
                var before1 = Summaries.ComputeAgentSafety(ctx, map);
                var roster1 = Summaries.ComputeOwnedRoster(ctx, map);
                var attackPairs1 = stopOnHeroAttacking ? Summaries.ComputeAttackPairs(ctx, map) : null;
                var digest1 = new TurnDigest();
                StepStatus st1;
                JsonValue payload1 = AdvanceOneTurn(ctx, map, world, force, applyResolve: true, args, digest1, out st1);
                if (st1 == StepStatus.Error)
                {
                    // The error text must carry the resolve outcome: a resolveOptionIndex that was consumed
                    // (or ignored) on the way to this error would otherwise vanish without a trace.
                    string msg1 = payload1["error"].AsString();
                    string rw1 = payload1["resolveWarning"].AsString();
                    if (rw1 != null) msg1 += " (also: " + rw1 + ")";
                    return ToolResult.Error(msg1);
                }
                if (st1 == StepStatus.Advanced)
                {
                    // Losses first: a unit you no longer have outranks any warning about one you still do.
                    digest1.SetLost(Summaries.EvaluateUnitLoss(ctx, map, roster1));
                    // Travel tasks that died silently (no game message exists for these).
                    digest1.Absorb(JsonValue.Null, Summaries.EvaluateTaskLoss(ctx, map, roster1));
                    JsonValue alert1; string reason1;
                    Summaries.EvaluateThreatStop(ctx, map, before1, args["stopOnThreatMotivation"].AsInt(0), out alert1, out reason1);
                    if (!alert1.IsNull) payload1.Set("threatAlert", alert1);
                    // A single turn always returns anyway - no stopReason - but the new-hunt alert
                    // must still land in the payload (single-turn is the common cadence).
                    if (attackPairs1 != null)
                    {
                        JsonValue ha1; string haReason1;
                        Summaries.EvaluateHeroAttackStop(ctx, map, attackPairs1, out ha1, out haReason1);
                        if (haReason1 != null) payload1.Set("heroAttacking", ha1);
                    }
                }
                JsonValue d1 = digest1.ToJson();
                if (!d1.IsNull) payload1.Set("digest", d1);
                JsonValue tips1 = TipEngine.CollectContextual(ctx);
                if (!tips1.IsNull) payload1.Set("tips", tips1);
                return ToolResult.Ok(payload1);
            }

            // Multi-turn batch: advance up to count turns, stopping early on a decision, game over, or a
            // threat escalation so a batched advance never blows past an agent walking into danger.
            var before = Summaries.ComputeAgentSafety(ctx, map);
            var roster = Summaries.ComputeOwnedRoster(ctx, map);
            var attackPairs = stopOnHeroAttacking ? Summaries.ComputeAttackPairs(ctx, map) : null;
            // Per-iteration snapshot for silent travel-task loss; `roster` itself must stay the batch-start
            // snapshot so EvaluateUnitLoss catches a death on any turn of the batch.
            var taskSnap = roster;
            var digest = new TurnDigest();
            int advancedBy = 0;
            string stopReason = null;
            JsonValue firstResolved = JsonValue.Null;
            string firstResolveWarning = null;
            // Dismissals accumulate over the WHOLE batch (this used to keep only the last turn's object,
            // so a 10-turn force batch reported turn 10 and silently dropped turns 1-9).
            int dismissTotal = 0;
            JsonValue dismissKinds = JsonValue.NewArray();
            JsonValue dismissRemaining = JsonValue.Null;
            bool dismissCappedOut = false;
            JsonValue pending = JsonValue.Null;
            JsonValue threatAlert = JsonValue.Null;
            JsonValue heroAttacking = JsonValue.Null;
            JsonValue gameOverPayload = JsonValue.Null;

            // One idle-alert retry per turn of the batch: the pre-advance Task_PassTurn sweep can miss an
            // agent that goes idle DURING the tick (its challenge completed, its travel ended), and the
            // resulting idleAgents block used to halt a passIdleAgents batch anyway (G14-#21).
            bool idleRetried = false;
            bool resolveConsumed = false;
            int attempt = 0;
            for (int i = 0; i < count; i++)
            {
                attempt++;
                StepStatus st;
                // Beyond the first iteration a resolve is only re-armed when the caller pinned the
                // decision (expectedDecisionId) and no earlier iteration consumed it - the pin makes
                // a mid-batch decision answerable without any risk of clicking an unrelated popup
                // (G17-#7; the guard inside DecisionRegistry.Resolve enforces the id match).
                bool allowResolve = attempt == 1 ||
                    (!args["expectedDecisionId"].IsNull && !resolveConsumed);
                JsonValue payload = AdvanceOneTurn(ctx, map, world, force, applyResolve: allowResolve, args, digest, out st);
                // Harvest the resolve outcome HERE, whatever branch follows: the Blocked /
                // NotAdvanced / GameOver / Error exits below used to drop it, making a consumed
                // resolveOptionIndex look like a silent no-op ("advancedBy:0, no resolved, no warning").
                // Captured before the transient retry too - the retry runs applyResolve:false and its
                // payload never carries the resolve fields.
                if (allowResolve && !payload["resolved"].IsNull)
                {
                    firstResolved = payload["resolved"];
                    firstResolveWarning = payload["resolveWarning"].AsString();
                    resolveConsumed = true;
                }
                else if (attempt == 1)
                {
                    firstResolveWarning = payload["resolveWarning"].AsString();
                }
                if (st == StepStatus.Error && payload["transient"].AsBool())
                {
                    // Benign mid-tick collision (an event mutated a world collection): retry this turn once
                    // before giving up. Any resolveOptionIndex was already applied on the first attempt.
                    payload = AdvanceOneTurn(ctx, map, world, force, applyResolve: false, args, digest, out st);
                }

                if (st == StepStatus.Error)
                {
                    if (advancedBy == 0)
                    {
                        string msg = payload["error"].AsString();
                        if (firstResolveWarning != null) msg += " (also: " + firstResolveWarning + ")";
                        return ToolResult.Error(msg);
                    }
                    stopReason = "error"; break;
                }
                if (st == StepStatus.GameOver) { gameOverPayload = payload; stopReason = "gameOver"; break; }
                if (st == StepStatus.Blocked)
                {
                    // passIdleAgents' contract is "idle never stops the batch": when the block IS the
                    // idle alert, pass the stragglers through the decision itself and retry this turn
                    // once, instead of surfacing the alert the caller explicitly opted past.
                    if (args["passIdleAgents"].AsBool() && !idleRetried &&
                        payload["pendingDecision"]["kind"].AsString() == "idleAgents")
                    {
                        ToolResult passed = Decisions.DecisionRegistry.Resolve(ctx,
                            JsonValue.NewObject().Set("optionIndex", 0));
                        if (passed != null && !passed.IsError) { idleRetried = true; i--; continue; }
                    }
                    pending = payload["pendingDecision"]; stopReason = "decision"; break;
                }
                if (st == StepStatus.NotAdvanced) { stopReason = payload["reason"].AsString("notAdvanced"); break; }

                // Advanced.
                advancedBy++;
                idleRetried = false; // the retry guard is per-turn, not per-batch
                JsonValue ad = payload["autoDismissed"];
                if (!ad.IsNull)
                {
                    dismissTotal += ad["count"].AsInt(0);
                    foreach (JsonValue k in ad["dismissed"].Items) dismissKinds.Add(k);
                    dismissRemaining = ad["remaining"];
                    if (ad["cappedOut"].AsBool()) dismissCappedOut = true;
                }

                // Travel tasks that died silently this turn (no game message exists for these); the batch
                // keeps going - a task-less agent trips the idleAgents decision next turn anyway.
                digest.Absorb(JsonValue.Null, Summaries.EvaluateTaskLoss(ctx, map, taskSnap));
                taskSnap = Summaries.ComputeOwnedRoster(ctx, map);

                // Losing a unit stops the batch, like a threat escalation - checked BEFORE the threat scan so
                // a death is never masked by a warning about an agent that is merely in danger. This is the
                // gap that let an army (a UM, which the UA-only threat scan never sees) die on turn 2 of a
                // ten-turn batch with the remaining eight played blind.
                JsonValue lost = Summaries.EvaluateUnitLoss(ctx, map, roster);
                if (!lost.IsNull) { digest.SetLost(lost); stopReason = "unitLost"; break; }

                // A hero STARTING an attack-pursuit stops the batch - checked after losses (a death
                // outranks the warning) but evaluated before the decision check, so the alert is
                // attached to the result even when a popped decision supplies the stopReason.
                string haReason = null;
                if (attackPairs != null)
                {
                    JsonValue haAlert;
                    Summaries.EvaluateHeroAttackStop(ctx, map, attackPairs, out haAlert, out haReason);
                    if (haReason != null) heroAttacking = haAlert;
                }

                // A decision may have popped mid-processing even though the turn advanced; stop and surface it.
                if (!payload["pendingDecision"].IsNull) { pending = payload["pendingDecision"]; stopReason = "decision"; break; }
                if (haReason != null) { stopReason = "heroAttacking"; break; }

                // Threat early-stop: meaningful danger (agent becomes huntable / an in-range hunter it is
                // not favoured against / worse odds), plus the opt-in rising-motivation tripwire.
                JsonValue alert; string reason;
                Summaries.EvaluateThreatStop(ctx, map, before, motivationStopPct, out alert, out reason);
                if (reason != null) { threatAlert = alert; stopReason = reason; break; }
            }

            JsonValue result = JsonValue.NewObject()
                .Set("turn", map.turn)
                .Set("advancedBy", advancedBy)
                // What the caller ASKED for, not the clamped value - requestedCount:10 against an input
                // of 20 silently rewrote the caller's own number (G14-#16).
                .Set("requestedCount", requestedCount)
                .Set("stoppedEarly", advancedBy < count);
            if (requestedCount != count)
                result.Set("countNote", "count " + requestedCount + " was clamped to the max batch size of " +
                    MaxTurnBatch + " - call end_turn again to continue");
            if (advancedBy < count && stopReason != null) result.Set("stopReason", stopReason);
            if (!firstResolved.IsNull) result.Set("resolved", firstResolved);
            if (firstResolveWarning != null) result.Set("resolveWarning", firstResolveWarning);
            if (dismissTotal > 0)
            {
                JsonValue ad = JsonValue.NewObject()
                    .Set("count", dismissTotal)          // whole batch, not just the final turn
                    .Set("dismissed", dismissKinds);
                if (!dismissRemaining.IsNull) ad.Set("remaining", dismissRemaining);
                if (dismissCappedOut) ad.Set("cappedOut", true);
                result.Set("autoDismissed", ad);
            }
            if (!pending.IsNull) result.Set("pendingDecision", pending); // already decorated by AdvanceOneTurn
            if (!threatAlert.IsNull) result.Set("threatAlert", threatAlert);
            if (!heroAttacking.IsNull) result.Set("heroAttacking", heroAttacking);
            if (!gameOverPayload.IsNull)
            {
                result.Set("gameOver", true)
                      .Set("outcome", gameOverPayload["outcome"])
                      .Set("victoryMode", gameOverPayload["victoryMode"]);
            }
            JsonValue dig = digest.ToJson();
            if (!dig.IsNull) result.Set("digest", dig);
            JsonValue tips = TipEngine.CollectContextual(ctx);
            if (!tips.IsNull) result.Set("tips", tips);
            return ToolResult.Ok(result);
        }

        /// <summary>Advance exactly one turn. Returns the per-turn payload and sets <paramref name="status"/>
        /// so the caller (single call or batch loop) knows whether it advanced, hit game over, is blocked by
        /// a decision, made partial progress, or errored. Mirrors the game's own end-turn guard sequence.
        /// Feeds this turn's named dismissals and notable events straight into <paramref name="digest"/>, so
        /// they survive a batch instead of being overwritten by the next turn.</summary>
        private static JsonValue AdvanceOneTurn(GameContext ctx, Map map, World world, bool force, bool applyResolve, JsonValue args, TurnDigest digest, out StepStatus status)
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

            // A popup the game raised late last call (e.g. a battle notice created after bEndTurn returned)
            // may still be sitting in the delayed queue rather than being the live blocker. Promote it now so
            // every guard below - the resolve check, the combat probe, HardChoiceBlockerOpen - sees it; the
            // old ordering read ui.blocker directly and silently skipped a queued decision's resolve.
            Decisions.DecisionRegistry.PumpQueue(ctx);

            // The caller's answer to a pending decision (first iteration of a batch only, unless the
            // expectedDecisionId retry below re-arms it). This lets an agent resolve popups through
            // end_turn alone, without loading the get_pending_decision / resolve_decision tools
            // (which some MCP clients never load). A resolve that had nothing to act on or failed is
            // reported (resolveWarning), never silent.
            JsonValue resolved = JsonValue.Null;
            string resolveWarning = null;
            bool wantResolve = applyResolve &&
                (!args["resolveOptionIndex"].IsNull || !args["resolveOptionLabel"].IsNull);
            bool resolveIgnoredNoDecision = false;
            string expectedId = args["expectedDecisionId"].AsString();

            // G17-#7 (repeat of the G15-#3 pattern): the force auto-dismiss and routine-event sweep
            // below can consume the very decision the caller is answering, after which the resolve
            // found "no decision pending" and the turn burned a round-trip. When the caller pinned
            // WHICH decision they are answering (expectedDecisionId) and it is the live blocker right
            // now, answer it FIRST - the pin makes this safe (it can only land on the popup they
            // actually read). Without the pin the sweeps keep running first, so a stale notice can't
            // swallow a blind resolveOptionIndex meant for the real choice underneath.
            if (wantResolve && expectedId != null && string.Equals(
                    Decisions.DecisionRegistry.CurrentDecisionId(ctx), expectedId, StringComparison.Ordinal))
            {
                ApplyResolve(ctx, args, ref resolved, ref resolveWarning);
                wantResolve = false;
            }

            // Under force, also clear leftover informational popups before the guards run, so a stale notice
            // can't block this call (last turn's sweep ran before the game raised it) or swallow a
            // resolveOptionIndex meant for the real choice underneath.
            JsonValue preDismiss = force
                ? Decisions.DecisionRegistry.AutoDismissInformational(ctx)
                : JsonValue.Null;
            if (digest != null && !preDismiss.IsNull) digest.Absorb(preDismiss["items"], JsonValue.Null);

            // Opt-in: answer whitelisted routine events (Watched, Life Continues, …) with their curated
            // option so they don't stop the batch. Runs with or without force — it is its own opt-in —
            // and anything not on the whitelist still blocks like any narrative event.
            bool passRoutine = args["passRoutineEvents"].AsBool();
            if (passRoutine && digest != null) digest.AbsorbAutoResolved(SweepRoutineEvents(ctx));

            if (wantResolve)
            {
                if (Decisions.DecisionRegistry.FullOrNull(ctx).IsNull)
                {
                    resolveIgnoredNoDecision = true;
                    resolveWarning = "resolveOptionIndex/resolveOptionLabel was provided but no decision " +
                        "was pending when it was applied - most likely the decision you answered was " +
                        "already consumed (a force sweep, or it resolved itself), or it only appears " +
                        "during turn processing. Echo pendingDecision.decisionId as expectedDecisionId " +
                        "and the answer is retried automatically the moment that exact decision " +
                        "(re)appears" + (expectedId == null ? " - none was provided this call, so it was ignored."
                            : " (no match this call).");
                }
                else
                {
                    ApplyResolve(ctx, args, ref resolved, ref resolveWarning);
                }
            }

            // Pending agent combat must NEVER be auto-resolved — even under force=true. Unlike unspent skill
            // points / informational popups (which the game's force path legitimately pushes through), a
            // battle is a real tactical choice, and World.bEndTurn(force) would silently fight it via
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

            // The idle-agent alert is a hard block too — force must NOT silently waste an idle agent's turn
            // (in this game you almost always want every unit active). Mirror combat: while an agent is idle we
            // DENY force to bEndTurn, so bEndTurn(false) stops on the idle guard (World.cs:699) and the turn
            // surfaces the idleAgents decision instead of blowing past it. Detected directly (not via the
            // pending-decision kind) so a message/death popup on top can't mask the idle state and let force slip.
            // passIdleAgents is the one deliberate escape: assign every idle agent Task_PassTurn (a visible
            // "Passing Turn"), so an intentional multi-turn fast-forward keeps advancing — a conscious choice to
            // waste those turns, not the blanket force. It leaves a to-be-fought (engaged) agent alone.
            if (args["passIdleAgents"].AsBool())
            {
                foreach (Unit u in map.units)
                {
                    if (u == null || u.isDead || !u.isCommandable() || !(u is UA)) continue;
                    if (u.engagedBy != null && u.turnLastEngaged == map.turn) continue;
                    if (u.task == null && u.movesTaken == 0) u.task = new Task_PassTurn();
                }
            }
            bool idleBlocks = AnyAgentIdle(map, world); // passIdleAgents just gave those agents a task ⇒ not idle

            // Any OPEN popup carrying a real choice (narrative event, trading, a list selection, an
            // already-open level-up trait pick…) is a hard block too: force may only pass popups marked
            // informational (a pure "Dismiss" notice). Without this, bEndTurn(force) would bypass its own
            // ui.blocker guard (World.cs:642) and tick the turn with the popup still open, unanswered.
            // bEndTurn's force path still auto-spends unspent skill points when no popup is open — this
            // only stops force once a choice popup has actually been raised.
            bool hardChoiceOpen = Decisions.DecisionRegistry.HardChoiceBlockerOpen(ctx);

            int before = map.turn;
            int after;
            JsonValue autoDismiss;
            // Every unspent skill point is a real choice: a forced bEndTurn would AI-spend it via
            // UA.spendSkillPoint - which force did silently for four games (G16-#1 starting picks,
            // G18-#4 regular picks). Denying force makes bEndTurn(false) raise the level-up popup
            // instead; the caller answers it and force works again. The one-shot starting-trait
            // (magic mastery) pick additionally closes its menu permanently when AI-spent
            // (hasAssignedStartingTraits), so it keeps its own opt-in flag and forceDenied label.
            // Collateral (bEndTurn force is all-or-nothing): while any pick blocks, other agents'
            // points also pause auto-spending for this call; bEndTurn pops the FIRST pending agent's
            // menu, so N simultaneous level-ups cost N end_turn round-trips - forceSpendsRegularTraits
            // is the escape hatch for unattended batches.
            List<UA> masteryPicks = force && !args["forceSpendsStartingTraits"].AsBool()
                ? Summaries.PendingStartingTraitPicks(ctx, map) : null;
            bool masteryBlocks = masteryPicks != null && masteryPicks.Count > 0;
            List<SkillPointSnap> traitPicks = force && !args["forceSpendsRegularTraits"].AsBool()
                ? SnapshotPendingSkillPoints(map) : null;
            bool traitBlocks = traitPicks != null && traitPicks.Count > 0;
            // Deny force while combat, the idle-agent alert, a real-choice popup, or a pending
            // level-up pick is pending, so bEndTurn stops (pops the battle / selects the idle
            // unit / leaves the popup blocking / pops the level-up) instead of auto-resolving,
            // silently wasting, or ticking past them.
            bool allowForce = force && !combatEngaged && !idleBlocks && !hardChoiceOpen && !masteryBlocks && !traitBlocks;
            // A forced bEndTurn auto-spends one banked skill point per agent (World.cs:689-697,
            // AI-picked trait) and used to do so with no trace in any result (G14-#5): snapshot the
            // agents it will touch so the digest can name the level-up and the trait it chose.
            List<SkillPointSnap> lvlSnap = allowForce && digest != null ? SnapshotPendingSkillPoints(map) : null;
            try
            {
                world.bEndTurn(allowForce);
                after = map.turn;
                if (lvlSnap != null) digest.AbsorbAutoLevelUps(EvaluateAutoLevelUps(ctx, map, lvlSnap));

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

            // A routine event raised DURING the tick would otherwise surface as pendingDecision and stop
            // the batch — sweep again post-advance so the opt-in covers both sides of the turn.
            if (passRoutine && digest != null) digest.AbsorbAutoResolved(SweepRoutineEvents(ctx));

            // Name every popup force just cleared into the digest, whether or not the turn advanced.
            if (digest != null) digest.Absorb(autoDismiss["items"], JsonValue.Null);
            // One combined report for the caller: the pre-advance sweep and the post-advance sweep are a
            // single logical "force cleared the notices" operation. (Digest items were absorbed per-sweep.)
            autoDismiss = MergeDismiss(preDismiss, autoDismiss);

            if (after > before)
            {
                status = StepStatus.Advanced;
                // The turn's notable unified messages (razing, battles, deaths, wars). Read here, while
                // map.turnUnifiedMessages still holds this turn's batch - the next turnTick wipes it. Guarded
                // by the advance so a non-advancing call cannot re-report the previous turn's messages.
                if (digest != null) digest.Absorb(JsonValue.Null, Summaries.NotableTurnEvents(ctx, map, after));

                JsonValue result = JsonValue.NewObject()
                    .Set("turn", after)
                    .Set("advancedBy", after - before);
                if (!resolved.IsNull) result.Set("resolved", resolved);
                if (resolveWarning != null) result.Set("resolveWarning", resolveWarning);
                if (!autoDismiss.IsNull && autoDismiss["count"].AsInt(0) > 0)
                    result.Set("autoDismissed", CompactDismiss(autoDismiss));
                // Deferred resolve retry (G17-#7): the caller's answer found nothing pre-tick, but the
                // decision it pinned surfaced DURING processing - same decisionId means provably the
                // same popup (modal ids are per-popup-object), so land the answer now instead of
                // burning a round-trip re-presenting it.
                if (resolveIgnoredNoDecision && expectedId != null && string.Equals(
                        Decisions.DecisionRegistry.CurrentDecisionId(ctx), expectedId, StringComparison.Ordinal))
                {
                    ApplyResolve(ctx, args, ref resolved, ref resolveWarning);
                    resolveIgnoredNoDecision = false;
                    if (!resolved.IsNull) result.Set("resolved", resolved);
                    if (resolveWarning != null) result.Set("resolveWarning", resolveWarning);
                    else result.Remove("resolveWarning");
                }

                // A fresh decision may have popped during processing (e.g. an event's follow-up). When the
                // caller opted into passIdleAgents, don't surface the idle alert they'll re-raise next turn
                // (Task_PassTurn clears each turnTick) — else a passIdleAgents batch would stop on it every turn.
                // Any OTHER decision (level-up, event, combat) still surfaces and stops the batch.
                JsonValue nowPending = Decisions.DecisionRegistry.FullOrNull(ctx);
                if (!nowPending.IsNull &&
                    !(args["passIdleAgents"].AsBool() && nowPending["kind"].AsString() == "idleAgents"))
                    result.Set("pendingDecision", DecorateResolveHint(ctx, nowPending));
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
            // Deferred resolve retry (G17-#7), non-advanced side: the pinned decision was absent
            // pre-tick but is the blocker now - land the answer instead of re-presenting it.
            if (!pending.IsNull && resolveIgnoredNoDecision && expectedId != null && string.Equals(
                    Decisions.DecisionRegistry.CurrentDecisionId(ctx), expectedId, StringComparison.Ordinal))
            {
                ApplyResolve(ctx, args, ref resolved, ref resolveWarning);
                resolveIgnoredNoDecision = false;
                // Resolving can chain into a follow-up popup still in the delayed queue.
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
                    .Set("pendingDecision", DecorateResolveHint(ctx, pending));
                // Say WHY force didn't take this level-up: the starting-trait (magic mastery) menu
                // (G16-#1) and regular level-ups (G18-#4) both block by default, with distinct
                // labels so the caller knows which opt-out flag applies.
                if (pending["kind"].AsString() == "levelUp")
                {
                    if (masteryBlocks)
                    {
                        string who = null;
                        try { who = masteryPicks[0].getName(); } catch { }
                        result.Set("forceDenied", "startingTraitPick")
                              .Set("forceDeniedNote", (who ?? "an agent") + " has an unspent skill point " +
                                  "whose next pick is its one-shot STARTING-TRAIT (magic mastery) menu - " +
                                  "force never auto-spends this (an AI pick would forfeit the mastery " +
                                  "permanently). Answer the level-up popup via resolveOptionIndex, then " +
                                  "force works again; pass forceSpendsStartingTraits:true only if you " +
                                  "deliberately want the old auto-spend.");
                    }
                    else if (traitBlocks)
                    {
                        string who = null;
                        try { who = traitPicks[0].Agent.getName(); } catch { }
                        result.Set("forceDenied", "traitPick")
                              .Set("forceDeniedNote", (who ?? "an agent") + " has an unspent skill " +
                                  "point (regular level-up). Answer the popup via resolveOptionIndex " +
                                  "to choose the trait yourself, then force works again - or pass " +
                                  "forceSpendsRegularTraits:true to let force auto-spend regular picks " +
                                  "on AI-chosen traits (reported in digest.autoResolvedLevelUps).");
                    }
                }
                if (!resolved.IsNull) result.Set("resolved", resolved);
                if (resolveWarning != null) result.Set("resolveWarning", resolveWarning);
                return result;
            }

            // We answered a decision but the turn still hasn't advanced and nothing is pending: that is
            // progress, not a failure. Report it cleanly and actionably (call end_turn again).
            if (!resolved.IsNull)
            {
                status = StepStatus.NotAdvanced;
                JsonValue result = JsonValue.NewObject()
                    .Set("advanced", false)
                    .Set("turn", after)
                    .Set("reason", "decisionAnswered")
                    .Set("resolved", resolved)
                    .Set("hint", "decision answered, but the turn has not advanced yet - call end_turn again to continue.");
                if (resolveWarning != null) result.Set("resolveWarning", resolveWarning);
                return result;
            }

            // Some other guard fired: report which one.
            status = StepStatus.Error;
            string diag = DiagnoseEndTurnBlock(map, world);
            JsonValue err = JsonValue.NewObject().Set("error", "the turn did not advance: " + diag);
            if (resolveWarning != null) err.Set("resolveWarning", resolveWarning);
            return err;
        }

        /// <summary>The one place end_turn's resolveOptionIndex/resolveOptionLabel is actually clicked
        /// (pre-tick, guarded-early, and the post-tick expectedDecisionId retries all funnel here).
        /// On success clears any prior "was ignored" warning; on failure reports via resolveWarning.</summary>
        private static void ApplyResolve(GameContext ctx, JsonValue args, ref JsonValue resolved, ref string resolveWarning)
        {
            JsonValue rargs = JsonValue.NewObject();
            if (!args["resolveOptionIndex"].IsNull) rargs.Set("optionIndex", args["resolveOptionIndex"]);
            if (!args["resolveOptionLabel"].IsNull) rargs.Set("optionLabel", args["resolveOptionLabel"]);
            if (!args["confirmDiscard"].IsNull) rargs.Set("confirmDiscard", args["confirmDiscard"]);
            // Optional stale-decision guard: with expectedDecisionId the resolve refuses (and
            // reports via resolveWarning) when the pending decision is no longer the one read.
            if (!args["expectedDecisionId"].IsNull)
                rargs.Set("expectedDecisionId", args["expectedDecisionId"]);
            ToolResult rr = Decisions.DecisionRegistry.Resolve(ctx, rargs);
            resolved = JsonValue.NewObject()
                .Set("ok", rr != null && !rr.IsError)
                .Set("detail", rr != null ? rr.Text : null);
            resolveWarning = rr == null || rr.IsError
                ? "resolving the pending decision failed: " + (rr != null ? rr.Text : "no result")
                : null;
        }

        /// <summary>Answer consecutive whitelisted routine events (passRoutineEvents): each successful
        /// auto-resolve may uncover another queued popup, so loop — bounded — until the blocker is
        /// anything but a routine event. Returns the {turn,title,chose,outcome?} records (or Null).</summary>
        private static JsonValue SweepRoutineEvents(GameContext ctx)
        {
            JsonValue records = JsonValue.Null;
            for (int guard = 0; guard < 6; guard++)
            {
                JsonValue rec = Decisions.PopupEventHandler.TryAutoResolveRoutine(ctx);
                if (rec.IsNull) break;
                if (records.IsNull) records = JsonValue.NewArray();
                records.Add(rec);
            }
            return records;
        }

        /// <summary>Combine the pre-advance and post-advance informational sweeps into one report:
        /// counts sum, dismissed-kind lists concatenate, cappedOut ORs, and `remaining` (what stopped
        /// the sweep) comes from the later pass, which saw the final state.</summary>
        private static JsonValue MergeDismiss(JsonValue a, JsonValue b)
        {
            if (a.IsNull || a["count"].AsInt(0) == 0) return b;
            if (b.IsNull || b["count"].AsInt(0) == 0) return a;
            JsonValue kinds = JsonValue.NewArray();
            foreach (JsonValue k in a["dismissed"].Items) kinds.Add(k);
            foreach (JsonValue k in b["dismissed"].Items) kinds.Add(k);
            JsonValue o = JsonValue.NewObject()
                .Set("count", a["count"].AsInt(0) + b["count"].AsInt(0))
                .Set("dismissed", kinds);
            if (!b["remaining"].IsNull) o.Set("remaining", b["remaining"]);
            if (a["cappedOut"].AsBool() || b["cappedOut"].AsBool()) o.Set("cappedOut", true);
            return o;
        }

        /// <summary>The auto-dismiss summary minus its <c>items</c> array — the counts stay on
        /// <c>autoDismissed</c>, the named entries live once, in <c>digest.dismissed</c>.</summary>
        private static JsonValue CompactDismiss(JsonValue autoDismiss)
        {
            JsonValue o = JsonValue.NewObject();
            foreach (var kv in autoDismiss.Members)
                if (kv.Key != "items") o.Set(kv.Key, kv.Value);
            return o;
        }

        /// <summary>Tag a pending-decision object with how to answer it through end_turn, and shrink
        /// its repeated boilerplate (this IS a presentation point — the object goes to the client).</summary>
        private static JsonValue DecorateResolveHint(GameContext ctx, JsonValue pending)
        {
            if (!pending.IsNull)
            {
                Boilerplate.CompactDecision(ctx, pending);
                pending.Set("resolveHint", Boilerplate.ResolveHint(ctx));
            }
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

        /// <summary>True while the idle-agent alert blocks turn end: option_idleAlert on and a commandable UA has
        /// no order and hasn't moved (the World.cs:699 guard). Used to deny force to bEndTurn so a forced end_turn
        /// never silently wastes an idle agent's turn — it must be ordered or explicitly passed (resolve optionIndex
        /// 0 / passIdleAgents), exactly as a pending battle is never auto-resolved. Detected directly (not via the
        /// pending-decision kind) so a message/death popup on top can't mask the idle state and let force slip.
        /// Gated on UA to match the engine guard (UA-only) and IdleAgentsDecision.IdleAgents.</summary>
        private static bool AnyAgentIdle(Map map, World world)
        {
            if (map == null || map.automatic || map.units == null) return false;
            if (world == null || !world.option_idleAlert) return false;
            foreach (Unit u in map.units)
            {
                if (u == null || u.isDead || !u.isCommandable() || !(u is UA)) continue;
                if (u.task == null && u.movesTaken == 0) return true;
            }
            return false;
        }

        /// <summary>One agent's pre-bEndTurn levelling state, for diffing what the game's force path
        /// auto-spent (see <see cref="SnapshotPendingSkillPoints"/>).</summary>
        private sealed class SkillPointSnap
        {
            public UA Agent;
            public int SkillPoints;
            public List<string> TraitNames;
        }

        /// <summary>The commandable agents whose banked skill point a forced bEndTurn is about to
        /// auto-spend (the exact World.cs:689 predicate), with their current trait names so the picked
        /// trait can be identified afterwards. Null when none qualify.</summary>
        private static List<SkillPointSnap> SnapshotPendingSkillPoints(Map map)
        {
            List<SkillPointSnap> snaps = null;
            try
            {
                foreach (Unit u in map.units)
                {
                    UA ua = u as UA;
                    if (ua == null || ua.isDead || !ua.isCommandable() || ua.person == null) continue;
                    if (ua.person.skillPoints <= 0 || ua.person.cachedOutOfTraits) continue;
                    var names = new List<string>();
                    try
                    {
                        if (ua.person.traits != null)
                            foreach (Trait t in ua.person.traits)
                                if (t != null) names.Add(TraitName(t));
                    }
                    catch { }
                    if (snaps == null) snaps = new List<SkillPointSnap>();
                    snaps.Add(new SkillPointSnap
                    {
                        Agent = ua,
                        SkillPoints = ua.person.skillPoints,
                        TraitNames = names,
                    });
                }
            }
            catch { }
            return snaps;
        }

        /// <summary>Diff the snapshot against the post-bEndTurn state: every agent whose point count fell
        /// gets a digest record naming the trait the AI picked for it. Null when nothing was spent.</summary>
        private static JsonValue EvaluateAutoLevelUps(GameContext ctx, Map map, List<SkillPointSnap> before)
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (SkillPointSnap s in before)
            {
                try
                {
                    if (s.Agent == null || s.Agent.isDead || s.Agent.person == null) continue;
                    if (s.Agent.person.skillPoints >= s.SkillPoints) continue; // nothing spent
                    var gained = new List<string>();
                    if (s.Agent.person.traits != null)
                        foreach (Trait t in s.Agent.person.traits)
                        {
                            if (t == null) continue;
                            string n = TraitName(t);
                            if (!s.TraitNames.Remove(n)) gained.Add(n);
                        }
                    arr.Add(JsonValue.NewObject()
                        .Set("turn", map.turn)
                        .Set("unit", Summaries.UnitRef(ctx, s.Agent))
                        .Set("chose", gained.Count > 0 ? string.Join(", ", gained.ToArray())
                            : "(trait could not be identified)")
                        .Set("level", s.Agent.person.level)
                        .Set("skillPointsRemaining", s.Agent.person.skillPoints)
                        // Only reachable with forceSpendsRegularTraits:true - by default any pending
                        // pick (starting or regular) denies force before bEndTurn (G16-#1, G18-#4).
                        .Set("note", "regular skill point auto-spent (forceSpendsRegularTraits, " +
                            "AI-picked trait); omit that flag to choose level-up traits yourself"));
                }
                catch { }
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
        }

        private static string TraitName(Trait t)
        {
            try { return t.getName(); } catch { return t.GetType().Name; }
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
                    return u.getName() + " has unspent skill points - answer the level-up popup to " +
                        "choose the trait (force auto-spends them only with forceSpendsRegularTraits:true, " +
                        "or forceSpendsStartingTraits:true for a first-level-up starting-trait pick)";
                if (world.option_idleAlert && u.task == null && u.movesTaken == 0)
                    return u.getName() + " is idle and the idle-agent alert is on (give it an order, " +
                        "pass it via resolve_decision optionIndex 0, or fast-forward with end_turn passIdleAgents:true)";
            }
            return "unknown guard - check the game window for popups";
        }

        // ---------- shared helpers ----------

        private static ToolResult ResolveCommandable(GameContext ctx, string id, out Unit unit)
        {
            unit = Summaries.ResolveId(ctx, id) as Unit;
            if (unit == null)
                return ToolResult.Error(QueryTools.StaleUnitIdError(ctx, id));
            if (unit.isDead)
                return ToolResult.Error(unit.getName() + " is dead.");
            if (!unit.isCommandable())
                return ToolResult.Error(unit.getName() + " is not under your command.");
            return null;
        }

        /// <summary>The UI warns before abandoning a challenge with more than ~4 turns of progress.</summary>
        private static string AbandonWarning(Unit u)
        {
            // Never throws: it runs getProgressPerTurn / ignoreInterruptionWarning, arbitrary challenge (and
            // modded-challenge) code, and it is evaluated even when the caller passed force=true - so a throw
            // here would block the very action the warning only advises about, with no way around it.
            try
            {
                Task_PerformChallenge pc = u.task as Task_PerformChallenge;
                UA ua = u as UA;
                if (pc == null || ua == null || pc.challenge == null) return null;
                double turnsOfProgress = pc.progress / Math.Max(1.0, pc.challenge.getProgressPerTurn(ua, null));
                if (turnsOfProgress > 4.0 && !pc.challenge.ignoreInterruptionWarning())
                {
                    return u.getName() + " is performing '" + pc.challenge.getName() + "' with " +
                        (int)Math.Ceiling(turnsOfProgress) + " turns of progress that would be lost. " +
                        "Retry with force=true to abandon it.";
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Error("could not compute the abandon-progress warning", e);
                return null;
            }
        }

        /// <summary>Refresh the game's UI after a tool wrote state (the UI would otherwise keep showing
        /// stale panels). Cosmetic - shared with HolyOrderTools, never throws.</summary>
        internal static void CheckUiData(Map map)
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
