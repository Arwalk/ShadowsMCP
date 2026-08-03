using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tools.Decisions;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// The holy-order screen (PopupHolyOrder), which is otherwise reachable only by clicking in the game
    /// window: spending banked Elder influence to shift a faith's tenets, and the two actions against its
    /// divine entity. Each tool replicates the exact guard + commit sequence of the corresponding button
    /// (see docs/ground-truth-notes.md), returning API errors where the UI simply hides the button.
    /// </summary>
    public static class HolyOrderTools
    {
        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.RegisterMutating(new ToolDefinition(
                "influence_holy_order_tenet",
                "Spend a holy order's banked Elder influence to shift one of its tenets by one step - your "
                + "highest-leverage lever over a religion. direction=toward_elder darkens it; toward_human "
                + "reverses. Gate: an ordinary tenet cannot be darkened while the order's 'Alignment Status' "
                + "tenet is at or above it (only structural:true tenets - Dogmatic, Preachers, Temple "
                + "Builders - are exempt), so a faith's first buys are normally H_Alignment toward_elder. "
                + "Spending resets Elder influence to 0; it refills and is CAPPED at the requirement (banked "
                + "overflow is wasted - see list_holy_orders.canChangeTenet and spend promptly). Raising "
                + "Dogmatic raises every later price. get_tips id=holy_tenets explains what darkened tenets do.",
                Schema.Object(
                    Schema.Prop("orderId", Schema.String("The holy order's social group id, e.g. SG5 (from list_holy_orders)"), required: true),
                    // Not schema-required: tenetType is an accepted alias (list_holy_orders labels the
                    // class type field "type", which repeatedly led to tenetType guesses - G14-#18);
                    // the handler requires one of the two.
                    Schema.Prop("tenet", Schema.String("Which tenet, by class type (e.g. H_CandleCircles, H_Alignment - the 'type' field in list_holy_orders) or display name (e.g. \"Candle Circles\"), from that order's holyOrder.tenets")),
                    Schema.Prop("tenetType", Schema.String("Alias of 'tenet' - either parameter works")),
                    Schema.Prop("direction", Schema.StringEnum(
                        "toward_elder = one step darker (status-1; for a structural tenet simply 'negative'). "
                        + "toward_human = one step toward humanity (status+1).",
                        "toward_elder", "toward_human"), required: true)),
                a => QueryTools.WithMap(ctx, map => InfluenceTenet(ctx, map, a))));

            host.RegisterMutating(new ToolDefinition(
                "oppose_divinity",
                "Act against the divine entity behind a holy order. action=undermine spends 1 power to cut "
                + "10 off the entity's strength - the first use anywhere starts the War in Heaven (angers "
                + "the entity, briefly raises panic). action=exile permanently banishes an entity at 0 "
                + "strength once ALL its worldly presences are corrupted; exile drives its acolytes to full "
                + "shadow and zero sanity. Read strength/anger/corrupted presences and canUndermine/canExile "
                + "from list_holy_orders {orderId}. Both raise a narrative event - answer it via "
                + "resolve_decision (or end_turn resolveOptionIndex). NOTE: when the map option "
                + "opt_divineEntities is off (the default), no order has an entity and this tool is "
                + "permanently unusable for the whole game.",
                Schema.Object(
                    Schema.Prop("orderId", Schema.String("The holy order's social group id, e.g. SG5"), required: true),
                    Schema.Prop("action", Schema.StringEnum(
                        "undermine = spend 1 power for -10 entity strength. exile = banish a fully-corrupted, "
                        + "0-strength entity for good.",
                        "undermine", "exile"), required: true)),
                a => QueryTools.WithMap(ctx, map => OpposeDivinity(ctx, map, a))));
        }

        // ---------- tenets ----------

        /// <summary>Mirrors UIE_HolyTenet.bInfluencePositively / bInfluenceNegatively, gated by the same
        /// conditions its setTo() uses to show or hide those two buttons.</summary>
        private static ToolResult InfluenceTenet(GameContext ctx, Map map, JsonValue a)
        {
            HolyOrder ho;
            ToolResult err = ResolveOrder(ctx, a["orderId"].AsString(), out ho);
            if (err != null) return err;

            string wanted = a["tenet"].AsString();
            if (string.IsNullOrEmpty(wanted)) wanted = a["tenetType"].AsString();
            if (string.IsNullOrEmpty(wanted))
                return ToolResult.Error("tenet is required (tenetType works too) - pass the tenet's class "
                    + "type or display name from list_holy_orders");
            HolyTenet t = FindTenet(ho, wanted);
            if (t == null)
                return ToolResult.Error("no tenet '" + wanted + "' in " + Summaries.SafeDisplayName(ho)
                    + " - its tenets are: " + string.Join(", ", TenetKeys(ho).ToArray())
                    + ". (Every order holds its own subset, and some are added mid-game.)");

            string direction = a["direction"].AsString();
            bool towardElder = direction == "toward_elder";
            if (!towardElder && direction != "toward_human")
                return ToolResult.Error("direction must be \"toward_elder\" or \"toward_human\"");

            // The screen-level gate: enough Elder influence banked to make any change at all.
            int req = ho.influenceElderReq;
            if (ho.influenceElder < req)
            {
                int perTurn = ho.computeInfluenceDark(null);
                string eta = perTurn > 0
                    ? " (about " + ((req - ho.influenceElder + perTurn - 1) / perTurn) + " turn(s) at +" + perTurn + "/turn)"
                    : " (it is not gaining any - enshadow its settlements, or have an agent fund the order)";
                return ToolResult.Error(Summaries.SafeDisplayName(ho) + " has " + ho.influenceElder
                    + "/" + req + " Elder influence - not enough to change a tenet" + eta);
            }

            bool canElder, canHuman;
            string blocked;
            Summaries.TenetEligibility(ho, t, out canElder, out canHuman, out blocked);
            if (towardElder && !canElder)
                return ToolResult.Error("cannot push " + Summaries.TenetName(t) + " toward_elder: "
                    + (blocked ?? "it is already at its minimum (" + t.getMaxNegativeInfluence() + ")"));
            if (!towardElder && !canHuman)
                return ToolResult.Error("cannot push " + Summaries.TenetName(t) + " toward_human: "
                    + "it is already at its maximum (" + t.getMaxPositiveInfluence() + ")");

            // Which tenets the Alignment gate is currently holding shut - re-checked after the commit so
            // the result can name exactly what this change unlocked.
            List<HolyTenet> wereBlocked = new List<HolyTenet>();
            if (t is H_Alignment)
                foreach (HolyTenet other in ho.tenets)
                {
                    if (other == t) continue;
                    bool e, h;
                    string b;
                    Summaries.TenetEligibility(ho, other, out e, out h, out b);
                    if (!e) wereBlocked.Add(other);
                }

            // Commit, exactly as the two UI buttons do.
            int before = t.status;
            if (towardElder) t.status--; else t.status++;
            ho.influenceElder = 0;
            try { ho.updateData(); } catch { /* cosmetic recompute; never fail the write */ }
            ActionTools.CheckUiData(map);

            int perTurnAfter = ho.computeInfluenceDark(null);
            JsonValue o = JsonValue.NewObject()
                .Set("order", Summaries.SocialGroupRef(ho))
                .Set("tenet", Summaries.TenetSummary(ho, t, detail: true))
                .Set("statusBefore", before)
                .Set("statusAfter", t.status)
                .Set("changed", Summaries.TenetName(t) + ": " + before + " -> " + t.status
                    + " (" + Summaries.TenetStatusLabel(t) + ")")
                .Set("influenceElder", ho.influenceElder)
                .Set("influenceElderReq", ho.influenceElderReq)
                .Set("canChangeTenet", false);
            if (perTurnAfter > 0)
                o.Set("turnsUntilCanChangeTenet",
                    (ho.influenceElderReq - ho.influenceElder + perTurnAfter - 1) / perTurnAfter);

            // Darkening Alignment is what unlocks the rest of the doctrine, so say what it just opened up.
            if (wereBlocked.Count > 0)
            {
                List<string> freed = new List<string>();
                foreach (HolyTenet other in wereBlocked)
                {
                    bool e, h;
                    string b;
                    Summaries.TenetEligibility(ho, other, out e, out h, out b);
                    if (e) freed.Add(other.GetType().Name + " (\"" + Summaries.TenetName(other) + "\")");
                }
                if (freed.Count > 0)
                    o.Set("nowDarkenable", string.Join(", ", freed.ToArray()))
                     .Set("nowDarkenableNote", "these became eligible for direction:toward_elder with this "
                        + "change - each still costs a full Elder influence bar");
            }
            return ToolResult.Ok(o);
        }

        /// <summary>Match a tenet by class type or display name, case-insensitively, against the order's
        /// LIVE list (which varies per order and grows mid-game, e.g. Ch_HungersPromise adds The Feast).</summary>
        private static HolyTenet FindTenet(HolyOrder ho, string key)
        {
            if (ho == null || ho.tenets == null) return null;
            foreach (HolyTenet t in ho.tenets)
            {
                if (t == null) continue;
                if (string.Equals(t.GetType().Name, key, StringComparison.OrdinalIgnoreCase)) return t;
                string name = Summaries.TenetName(t);
                if (name != null && string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        private static List<string> TenetKeys(HolyOrder ho)
        {
            List<string> keys = new List<string>();
            if (ho != null && ho.tenets != null)
                foreach (HolyTenet t in ho.tenets)
                    if (t != null) keys.Add(t.GetType().Name + " (\"" + Summaries.TenetName(t) + "\")");
            return keys;
        }

        // ---------- divinity ----------

        /// <summary>Mirrors PopupHolyOrder.bUndermine / bExile, including the first-use War in Heaven
        /// bookkeeping and the acolyte fallout of an exile.</summary>
        private static ToolResult OpposeDivinity(GameContext ctx, Map map, JsonValue a)
        {
            HolyOrder ho;
            ToolResult err = ResolveOrder(ctx, a["orderId"].AsString(), out ho);
            if (err != null) return err;

            // Say it once, game-wide: with the map option off NO order has an entity, so probing
            // order after order (or game after game - 17 straight by one playtester) is wasted.
            if (!map.opt_divineEntities)
                return ToolResult.Error("divine entities are DISABLED in this game (map option "
                    + "opt_divineEntities is off - the default): no holy order has one and "
                    + "oppose_divinity can never do anything this game. Drop this line of play.");

            DivineEntity d = ho.divinity;
            if (d == null)
                return ToolResult.Error(Summaries.SafeDisplayName(ho) + " has no divine entity"
                    + " (this order never had one)");
            if (d.exiled)
                return ToolResult.Error(Summaries.DivinityName(d) + " is already exiled");

            string action = a["action"].AsString();
            if (action == "undermine") return Undermine(ctx, map, ho, d);
            if (action == "exile") return Exile(ctx, map, ho, d);
            return ToolResult.Error("action must be \"undermine\" or \"exile\"");
        }

        private static ToolResult Undermine(GameContext ctx, Map map, HolyOrder ho, DivineEntity d)
        {
            if (!(map.overmind.power >= 1.0))
                return ToolResult.Error("undermining a divine entity costs 1 power; you have "
                    + Summaries.Round2Down(map.overmind.power));

            map.overmind.power -= 1.0;
            d.strength -= 10;
            if (d.strength < 0) d.strength = 0;
            d.anger += map.param.holy_entityAngerGain;

            bool startedWar = false;
            if (!map.overmind.hasStartedWarInHeaven)
            {
                startedWar = true;
                map.overmind.hasStartedWarInHeaven = true;
                map.overmind.panicTemporaryChange += 0.1;
                PopEvent(map, "anw.warInHeaven");
            }
            ActionTools.CheckUiData(map);

            JsonValue o = JsonValue.NewObject()
                .Set("order", Summaries.SocialGroupRef(ho))
                .Set("action", "undermine")
                .Set("powerRemaining", Summaries.Round2Down(map.overmind.power))
                .Set("divinity", Summaries.DivinityBlock(ctx, ho));
            if (startedWar)
                o.Set("warInHeavenBegun", true)
                 .Set("note", "your first strike against a divine entity - world panic rises temporarily");
            return WithPending(ctx, o);
        }

        private static ToolResult Exile(GameContext ctx, Map map, HolyOrder ho, DivineEntity d)
        {
            // The UI only offers Exile at 0 strength with every presence corrupted; bExile itself
            // re-checks only the strength, so enforce the stricter, visible condition.
            int corrupted = 0, total = 0;
            if (d.presences != null)
                foreach (Pr_EntityPresence p in d.presences)
                {
                    total++;
                    if (p != null && p.corrupted) corrupted++;
                }
            if (d.strength != 0)
                return ToolResult.Error(Summaries.DivinityName(d) + " still has " + d.strength
                    + " strength - undermine it to 0 first (oppose_divinity action=undermine)");
            if (total == 0 || corrupted < total)
                return ToolResult.Error(Summaries.DivinityName(d) + " has " + corrupted + "/" + total
                    + " presences corrupted - all of them must be corrupted before it can be exiled");

            d.exiled = true;
            int acolytes = 0;
            foreach (Unit unit in map.units)
            {
                UAA uaa = unit as UAA;
                if (uaa == null || uaa.order != ho || uaa.person == null) continue;
                uaa.person.sanity = 0.0;
                uaa.person.shadow = 1.0;
                acolytes++;
            }
            PopEvent(map, "anw.exiledDivinity");
            ActionTools.CheckUiData(map);

            return WithPending(ctx, JsonValue.NewObject()
                .Set("order", Summaries.SocialGroupRef(ho))
                .Set("action", "exile")
                .Set("acolytesBroken", acolytes)
                .Set("note", acolytes + " acolyte(s) of this order driven to full shadow and zero sanity")
                .Set("divinity", Summaries.DivinityBlock(ctx, ho)));
        }

        /// <summary>Raise one of the game's named events, as the popup buttons do. Missing key = no-op
        /// (the event pack may not define it), and a failure here must not undo the state change.</summary>
        private static void PopEvent(Map map, string key)
        {
            try
            {
                if (!EventManager.events.ContainsKey(key)) return;
                EventContext c = EventContext.withNothing(map);
                c.map.world.prefabStore.popEvent(EventManager.events[key].data, c);
            }
            catch
            {
                // The action already committed; a missing prefab store (headless) must not fail the tool.
            }
        }

        /// <summary>Attach the event popup these actions raise, so the agent can answer it straight away
        /// (a banner is stamped on the result too, but the options are only here).</summary>
        private static ToolResult WithPending(GameContext ctx, JsonValue o)
        {
            JsonValue pd = DecisionRegistry.FullOrNull(ctx);
            if (!pd.IsNull)
            {
                Boilerplate.CompactDecision(ctx, pd);
                o.Set("pendingDecision", pd.Set("resolveHint", Boilerplate.ResolveHint(ctx)));
            }
            return ToolResult.Ok(o);
        }

        // ---------- shared ----------

        private static ToolResult ResolveOrder(GameContext ctx, string id, out HolyOrder ho)
        {
            ho = null;
            if (string.IsNullOrEmpty(id)) return ToolResult.Error("orderId is required");
            SocialGroup sg = Summaries.ResolveId(ctx, id) as SocialGroup;
            if (sg == null)
                return ToolResult.Error("unknown social group id: " + id + " - re-run list_holy_orders");
            ho = sg as HolyOrder;
            if (ho == null)
                return ToolResult.Error(Summaries.SafeDisplayName(sg) + " (" + id
                    + ") is not a holy order - list_holy_orders shows the religions");
            return null;
        }
    }
}
