using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The agent-duel combat menu (<see cref="PopupBattleAgent"/>) — the popup the game opens when one of
    /// your agents is attacked (the fight icon on the portrait, <c>UIE_AgentRoster.doAgentBattle</c>) or when
    /// you attack a hero (<c>UA.playerTriesToAttack</c>). Unlike a one-shot event/level-up choice it is a
    /// multi-round duel: you <b>Step</b> through exchanges, and from round 2 you may <b>Flee/Retreat</b>; in
    /// round 1 you may reorder your minions. This bespoke handler (registered before the generic fallback)
    /// reads the live <see cref="BattleAgents"/> so the agent sees both combatants' stats, and drives the
    /// popup's own <c>bStep</c>/<c>bRetreatLeft|Right</c>/<c>bAttMove*</c>/<c>bDefMove*</c> methods so it can
    /// make every choice a human can.
    ///
    /// A won battle opens a "Loot the Fallen Foe" <see cref="PopupItemTrading"/> as the next blocker
    /// (<c>BattleAgents.victory</c>) — already handled by <see cref="PopupItemTradingHandler"/> and promoted
    /// by <c>DecisionRegistry.PumpQueue</c>.
    /// </summary>
    public sealed class PopupBattleAgentHandler : IDecisionHandler
    {
        // Fight-to-the-end safety cap. BattleAgents.automatic() caps its own loop at 128 steps; 300 bStep
        // presses (each advances at most one exchange, then one more closes) is a generous ceiling that still
        // guarantees termination if some state never flips.
        private const int StepCap = 300;

        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupBattleAgent>() != null;
        }

        public string Kind(GameObject blocker) { return "combat"; }

        // A battle is a real interaction (fight / flee / retreat): never auto-dismiss it. It also never reaches
        // end_turn(force)'s auto-dismiss loop — pending combat hard-blocks end_turn (see ActionTools.AdvanceOneTurn).
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            BattleAgents b = Battle(blocker);
            if (b == null) return "an agent battle";
            return "battle: " + Name(() => b.att.getName()) + " attacking " + Name(() => b.def.getName()) +
                " (round " + b.round + ")";
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupBattleAgent p = blocker.GetComponent<PopupBattleAgent>();
            BattleAgents b = p != null ? p.battle : null;

            JsonValue o = JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "combat")
                .Set("popupType", "PopupBattleAgent");
            if (b == null)
                return o.Set("title", "agent battle")
                        .Set("options", JsonValue.NewArray())
                        .Set("note", "battle state is unavailable; resolve_decision force=true to fight/close it.")
                        .Set("resolveWith", "resolve_decision with force=true");

            bool youAtt = SafeBool(() => b.att.isCommandable());
            bool youDef = SafeBool(() => b.def.isCommandable());

            o.Set("title", Name(() => b.att.getName()) + " attacking " + Name(() => b.def.getName()))
             .Set("round", b.round)
             .Set("state", b.state)
             .Set("complete", p.complete)
             .Set("outcome", OutcomeText(b))
             .Set("yourSide", youAtt ? "attacker" : (youDef ? "defender" : "neither"))
             .Set("attacker", SideJson(b, b.att, youAtt))
             .Set("defender", SideJson(b, b.def, youDef));

            o.Set("options", OptionsJson(BuildActions(p)));
            o.Set("note", "A multi-round duel. 'Fight to the end' resolves the whole battle in one call " +
                "(pick it when your dangerEstimate beats theirs); 'Step one exchange' advances a single round " +
                "so you can watch the odds and then flee. Flee/Retreat unlock from round 2 — round 2 is Flee " +
                "(you lose ALL your minions), round 3+ is a safe Retreat. Winning opens a 'Loot the Fallen " +
                "Foe' trade next. force=true fights to the end.");
            return o.Set("resolveWith", "resolve_decision with optionIndex, or force=true to fight to the end");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            UIMaster ui = SafeUi(ctx);
            PopupBattleAgent p = blocker != null ? blocker.GetComponent<PopupBattleAgent>() : null;
            if (p == null) return ToolResult.Error("the battle popup is no longer open.");
            BattleAgents b = p.battle;
            if (b == null)
            {
                // Unreachable in practice (popBattle always populate()s a battle), but never let a stateless
                // battle popup soft-lock end_turn: force=true closes it.
                if (!args["force"].AsBool())
                    return ToolResult.Error("this battle popup has no state; resolve_decision force=true to close it.");
                try { if (ui != null) ui.removeBlocker(blocker); } catch { }
                bool gone = blocker == null || ui == null || ui.blocker != blocker;
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true).Set("kind", "combat").Set("closed", gone)
                    .Set("note", "battle had no state; closed the popup."));
            }

            string action;
            if (!args["optionIndex"].IsNull)
            {
                List<Act> acts = BuildActions(p);
                int wanted = args["optionIndex"].AsInt(-1);
                if (wanted < 0 || wanted >= acts.Count)
                    return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                        acts.Count + (acts.Count == 1 ? " option)." : " options)."));
                action = acts[wanted].Action;
            }
            else if (args["force"].AsBool())
            {
                action = "fight"; // fight-to-end also handles the already-complete "close" case
            }
            else
            {
                return ToolResult.Error("pick an option with optionIndex (see get_pending_decision), or pass " +
                    "force=true to fight the battle to the end.");
            }

            bool youAtt = SafeBool(() => b.att.isCommandable());

            switch (action)
            {
                case "fight":
                case "close":
                    return FightToEnd(ui, blocker, p, b);
                case "step":
                    try { p.bStep(); } catch (Exception e) { return ToolResult.Error("step failed: " + e.Message); }
                    return StateResult(ui, blocker, p, b, "stepped");
                case "flee":
                    try { if (youAtt) p.bRetreatLeft(); else p.bRetreatRight(); }
                    catch (Exception e) { return ToolResult.Error("retreat failed: " + e.Message); }
                    return ClosedResult(ui, blocker, b, "fled");
                case "minionUp":
                    try { if (youAtt) p.bAttMoveUp(); else p.bDefMoveUp(); } catch { }
                    return StateResult(ui, blocker, p, b, "reordered");
                case "minionDown":
                    try { if (youAtt) p.bAttMoveDown(); else p.bDefMoveDown(); } catch { }
                    return StateResult(ui, blocker, p, b, "reordered");
            }
            return ToolResult.Error("unknown combat action.");
        }

        // ---------- driving the battle ----------

        /// <summary>Press Step until this popup closes (bStep advances an exchange, then once the battle is
        /// decided the next press applies the outcome and removes the blocker). Capped so a stuck state can't
        /// spin forever.</summary>
        private ToolResult FightToEnd(UIMaster ui, GameObject blocker, PopupBattleAgent p, BattleAgents b)
        {
            for (int i = 0; i < StepCap; i++)
            {
                if (blocker == null || ui == null || ui.blocker != blocker) break;
                try { p.bStep(); }
                catch (Exception e) { return ToolResult.Error("battle step failed: " + e.Message); }
            }
            return ClosedResult(ui, blocker, b, "foughtToEnd");
        }

        /// <summary>Result for an action that closed the battle (flee/fight-to-end/close): reports the outcome
        /// and flags the chained loot trade, if any.</summary>
        private ToolResult ClosedResult(UIMaster ui, GameObject blocker, BattleAgents b, string how)
        {
            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            JsonValue o = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "combat")
                .Set("action", how)
                .Set("outcome", OutcomeText(b))
                .Set("closed", closed);
            if (b.victor != null) o.Set("victor", Name(() => b.victor.getName()));
            if (b.defeat != null) o.Set("defeated", Name(() => b.defeat.getName()));
            if (closed && ui != null && ui.blocker != null)
                o.Set("nextDecision", "another decision is now pending (a won battle opens 'Loot the Fallen " +
                    "Foe') — call get_pending_decision to see it");
            else if (!closed)
                o.Set("note", "the battle popup is still open — call get_pending_decision to continue.");
            return ToolResult.Ok(o);
        }

        /// <summary>Result for an action that leaves the popup open (step/reorder): re-emits the live combat
        /// state and options so the agent can keep driving without a separate get_pending_decision. A step can
        /// itself end the battle, so fall through to <see cref="ClosedResult"/> when the blocker cleared.</summary>
        private ToolResult StateResult(UIMaster ui, GameObject blocker, PopupBattleAgent p, BattleAgents b, string how)
        {
            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            if (closed) return ClosedResult(ui, blocker, b, how);
            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "combat")
                .Set("action", how)
                .Set("complete", p.complete)
                .Set("round", b.round)
                .Set("state", b.state)
                .Set("outcome", OutcomeText(b))
                .Set("attacker", SideJson(b, b.att, SafeBool(() => b.att.isCommandable())))
                .Set("defender", SideJson(b, b.def, SafeBool(() => b.def.isCommandable())))
                .Set("options", OptionsJson(BuildActions(p))));
        }

        // ---------- options ----------

        private sealed class Act { public string Action; public string Label; }

        /// <summary>The ordered actions available right now, mirroring which buttons <c>PopupBattleAgent.populate</c>
        /// makes active. Built once and used by both Describe and Resolve so the option indices never drift.</summary>
        private static List<Act> BuildActions(PopupBattleAgent p)
        {
            var acts = new List<Act>();
            BattleAgents b = p != null ? p.battle : null;
            if (b == null) return acts;

            if (p.complete)
            {
                acts.Add(new Act { Action = "close", Label = "Close and apply the outcome (" + OutcomeText(b) + ")" });
                return acts;
            }

            acts.Add(new Act { Action = "fight", Label = "Fight to the end (resolve the whole battle now)" });
            acts.Add(new Act { Action = "step", Label = "Step one exchange (advance a single round of blows)" });

            bool youAtt = SafeBool(() => b.att.isCommandable());
            bool youDef = SafeBool(() => b.def.isCommandable());
            UA you = youAtt ? b.att : (youDef ? b.def : null);
            bool alive = !b.att.isDead && !b.def.isDead;

            // Flee/Retreat: only your side, only round 2+ at the top of a round (state 0), matching populate().
            if (you != null && alive && b.round > 1 && b.state == 0 && b.outcome == BattleAgents.OUTCOME_UNRESOLVED)
                acts.Add(new Act
                {
                    Action = "flee",
                    Label = b.round == 2
                        ? "Flee — escape now, but you LOSE ALL your minions"
                        : "Retreat — safe withdrawal, you keep your minions",
                });

            // Minion reorder: only your side, only round 1 state 0, only if you have more than one minion.
            if (you != null && b.round == 1 && b.state == 0 && b.outcome == BattleAgents.OUTCOME_UNRESOLVED &&
                MinionCount(you) > 1)
            {
                acts.Add(new Act { Action = "minionUp", Label = "Reorder: swap your front minion with the 2nd (changes who absorbs blows first)" });
                acts.Add(new Act { Action = "minionDown", Label = "Reorder: swap your front minion with the 3rd" });
            }

            return acts;
        }

        private static JsonValue OptionsJson(List<Act> acts)
        {
            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < acts.Count; i++)
                options.Add(JsonValue.NewObject().Set("index", i).Set("label", acts[i].Label).Set("enabled", true));
            return options;
        }

        // ---------- reading the battle model ----------

        private static JsonValue SideJson(BattleAgents b, UA ua, bool isYou)
        {
            JsonValue o = JsonValue.NewObject().Set("name", Name(() => ua.getName()));
            o.Set("isYou", isYou)
             .Set("commandable", SafeBool(() => ua.isCommandable()))
             .Set("hp", ua.hp)
             .Set("maxHp", ua.maxHp)
             .Set("defence", ua.defence)                        // live: drops as it soaks hits this battle
             .Set("attack", SafeInt(() => ua.getStatAttack()))
             .Set("dangerEstimate", SafeInt(() => b.getDangerEstimate(ua))); // hp+defence+attack+minions
            JsonValue minions = JsonValue.NewArray();
            if (ua.minions != null)
                for (int i = 0; i < ua.minions.Length; i++)
                {
                    Minion m = ua.minions[i];
                    if (m == null || m.isDead) continue;
                    minions.Add(JsonValue.NewObject()
                        .Set("slot", i)
                        .Set("name", Name(() => m.getName()))
                        .Set("hp", m.hp)
                        .Set("maxHp", SafeInt(() => m.getMaxHP()))
                        .Set("defence", m.defence)
                        .Set("attack", SafeInt(() => m.getAttack())));
                }
            return o.Set("minions", minions);
        }

        private static string OutcomeText(BattleAgents b)
        {
            int o = b.outcome;
            if (o == BattleAgents.OUTCOME_UNRESOLVED) return "unresolved";
            if (o == BattleAgents.OUTCOME_RETREAT_ATT) return "attacker retreated";
            if (o == BattleAgents.OUTCOME_RETREAT_DEF) return "defender retreated";
            if (o == BattleAgents.OUTCOME_DEATH_ATT) return "attacker killed";
            if (o == BattleAgents.OUTCOME_DEATH_DEF) return "defender killed";
            return "unknown";
        }

        private static int MinionCount(UA ua)
        {
            int n = 0;
            if (ua != null && ua.minions != null)
                foreach (Minion m in ua.minions)
                    if (m != null && !m.isDead) n++;
            return n;
        }

        // ---------- small helpers ----------

        private static BattleAgents Battle(GameObject blocker)
        {
            PopupBattleAgent p = blocker != null ? blocker.GetComponent<PopupBattleAgent>() : null;
            return p != null ? p.battle : null;
        }

        private static string Name(Func<string> get) { try { return get(); } catch { return "?"; } }
        private static bool SafeBool(Func<bool> get) { try { return get(); } catch { return false; } }
        private static int SafeInt(Func<int> get) { try { return get(); } catch { return 0; } }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
