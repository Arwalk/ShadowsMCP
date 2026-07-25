using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The idle-agent alert: when a commandable agent has no order and hasn't moved,
    /// <c>World.bEndTurn</c> refuses to advance (and just selects the unit) — but opens no popup,
    /// so nothing else here would notice. Exactly the condition at World.cs:699
    /// (<c>option_idleAlert &amp;&amp; unit.task == null &amp;&amp; unit.movesTaken == 0</c>), which
    /// ActionTools.DiagnoseEndTurnBlock also checks. Resolve by giving the agents orders elsewhere,
    /// or here by passing them (the game's own <c>Task_PassTurn</c>).
    /// </summary>
    public sealed class IdleAgentsDecision : INonModalDecision
    {
        public string Kind() { return "idleAgents"; }

        public bool IsPending(GameContext ctx)
        {
            return IdleAgents(ctx).Count > 0;
        }

        public string Headline(GameContext ctx)
        {
            List<Unit> idle = IdleAgents(ctx);
            System.Text.StringBuilder names = new System.Text.StringBuilder();
            for (int i = 0; i < idle.Count && i < 4; i++)
            {
                if (names.Length > 0) names.Append(", ");
                names.Append(Name(idle[i]));
            }
            if (idle.Count > 4) names.Append(", …");
            string plural = idle.Count == 1 ? "agent is" : "agents are";
            return idle.Count + " of your " + plural + " idle (" + names + ") — order them or pass them";
        }

        public JsonValue Describe(GameContext ctx)
        {
            List<Unit> idle = IdleAgents(ctx);
            JsonValue agents = JsonValue.NewArray();
            foreach (Unit u in idle) agents.Add(Summaries.UnitRef(ctx, u));

            JsonValue options = JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("index", 0)
                    .Set("label", "Pass this turn for all idle agents (they wait; you can re-order them later)"));

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "idleAgents")
                .Set("title", idle.Count + (idle.Count == 1 ? " of your agents is idle" : " of your agents are idle"))
                .Set("idleAgents", agents)
                .Set("options", options)
                .Set("note", Boilerplate.NoteIdleAgents)
                .Set("resolveWith", Boilerplate.RwIdleAgents);
        }

        public ToolResult Resolve(GameContext ctx, JsonValue args)
        {
            List<Unit> idle = IdleAgents(ctx);
            if (idle.Count == 0)
                return ToolResult.Error("no agents are idle right now.");

            // force does NOT pass idle agents — mirror AgentCombatDecision, where force is not a blanket
            // resolve. Passing must be the conscious optionIndex 0 so an idle agent's turn is never silently
            // wasted; end_turn passIdleAgents:true is the explicit multi-turn fast-forward.
            bool pass = args["optionIndex"].AsInt(-1) == 0;
            if (!pass)
                return ToolResult.Error(idle.Count + " agent(s) are idle (" + IdList(ctx, idle) + "). " +
                    "Give them orders (move_unit / perform_challenge / use_power), or pass them with " +
                    "resolve_decision optionIndex 0. force will not pass them — like combat, idle blocks even under force.");

            JsonValue passed = JsonValue.NewArray();
            foreach (Unit u in idle)
            {
                u.task = new Task_PassTurn();
                passed.Add(Summaries.UnitRef(ctx, u));
            }
            CheckUiData(ctx);

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "idleAgents")
                .Set("passed", passed));
        }

        // ---------- idle detection (mirrors World.bEndTurn:699 / DiagnoseEndTurnBlock) ----------

        private static List<Unit> IdleAgents(GameContext ctx)
        {
            var result = new List<Unit>();
            Map map = ctx != null ? ctx.Map : null;
            if (map == null || map.world == null) return result;
            if (!map.world.option_idleAlert) return result; // alert off ⇒ end turn won't block on idle

            foreach (Unit u in map.units)
            {
                if (u == null || u.isDead || !u.isCommandable() || !(u is UA)) continue;
                if (u.task == null && u.movesTaken == 0) result.Add(u);
            }
            return result;
        }

        private static string Name(Unit u)
        {
            try { return u.getName(); }
            catch { return "?"; }
        }

        private static string IdList(GameContext ctx, List<Unit> units)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Unit u in units)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Summaries.UnitId(ctx, u));
            }
            return sb.ToString();
        }

        private static void CheckUiData(GameContext ctx)
        {
            try
            {
                Map map = ctx.Map;
                if (map != null && map.world != null && map.world.ui != null) map.world.ui.checkData();
            }
            catch { }
        }
    }
}
