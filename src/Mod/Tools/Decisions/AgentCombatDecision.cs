using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// One (or more) of your agents is under attack this turn. When an AI hunter reaches a commandable agent
    /// the game does <b>not</b> auto-resolve — it sets <c>engagedBy</c> / <c>turnLastEngaged</c> (the fight
    /// icon on the portrait, <c>UIE_AgentRoster.bFight</c>) and waits. No modal <c>ui.blocker</c> opens until
    /// that icon is clicked, so without this the pending battle is invisible to any agent not reading
    /// <c>get_unit</c> on the exact engaged unit — which is why agents never reached combat.
    ///
    /// This non-modal decision surfaces the engagement everywhere a decision shows (banner / game_overview /
    /// end_turn) and, on resolve, opens the battle popup exactly as clicking the icon does
    /// (<c>new BattleAgents(att, def)</c> + <c>popBattle</c>); <see cref="PopupBattleAgentHandler"/> then
    /// drives the fight. Mirrors the engagement guard in <c>World.bEndTurn</c> (checked before the level-up
    /// and idle guards), so it is registered before <see cref="IdleAgentsDecision"/>, and <c>end_turn</c>
    /// treats it as a hard block even under <c>force=true</c>.
    /// </summary>
    public sealed class AgentCombatDecision : INonModalDecision
    {
        public string Kind() { return "combat"; }

        public bool IsPending(GameContext ctx) { return Engaged(ctx).Count > 0; }

        public string Headline(GameContext ctx)
        {
            List<UA> engaged = Engaged(ctx);
            System.Text.StringBuilder names = new System.Text.StringBuilder();
            for (int i = 0; i < engaged.Count && i < 4; i++)
            {
                if (names.Length > 0) names.Append(", ");
                names.Append(Name(engaged[i]));
            }
            if (engaged.Count > 4) names.Append(", …");
            string verb = engaged.Count == 1 ? "agent is" : "agents are";
            return engaged.Count + " of your " + verb + " under attack (" + names + ") — resolve the battle";
        }

        public JsonValue Describe(GameContext ctx)
        {
            List<UA> engaged = Engaged(ctx);
            JsonValue battles = JsonValue.NewArray();
            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < engaged.Count; i++)
            {
                UA def = engaged[i];
                UA att = def.engagedBy as UA;
                int myDanger = SafeInt(() => def.getDangerEstimate());
                int foeDanger = att != null ? SafeInt(() => att.getDangerEstimate()) : 0;
                string verdict = Verdict(myDanger, foeDanger);
                battles.Add(JsonValue.NewObject()
                    .Set("index", i)
                    .Set("agent", Summaries.UnitRef(ctx, def))
                    .Set("attacker", Summaries.UnitRef(ctx, att))
                    .Set("yourStrength", myDanger)
                    .Set("attackerStrength", foeDanger)
                    .Set("verdict", verdict));
                options.Add(JsonValue.NewObject()
                    .Set("index", i)
                    .Set("label", "Open the battle: " + Name(def) + " vs " + Name(att) + " (" + verdict +
                        ") — then fight, flee, or retreat"));
            }

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "combat")
                .Set("title", engaged.Count + (engaged.Count == 1 ? " of your agents is" : " of your agents are") +
                    " under attack")
                .Set("battles", battles)
                .Set("options", options)
                .Set("note", "Each of these agents was attacked this turn and a battle is waiting. Resolve one " +
                    "with resolve_decision optionIndex to open its combat menu, then fight to the end / flee / " +
                    "retreat via a follow-up resolve_decision. Combat cannot be skipped: end_turn is blocked " +
                    "(even force=true) until every battle is resolved. 'verdict' compares strengths — consider " +
                    "fleeing an 'outmatched' fight from round 2 (you keep the agent but lose its minions).")
                .Set("resolveWith", "resolve_decision with optionIndex (opens that battle); force=true opens the first");
        }

        public ToolResult Resolve(GameContext ctx, JsonValue args)
        {
            List<UA> engaged = Engaged(ctx);
            if (engaged.Count == 0)
                return ToolResult.Error("no agent is under attack right now.");

            int idx;
            if (!args["optionIndex"].IsNull)
            {
                idx = args["optionIndex"].AsInt(-1);
                if (idx < 0 || idx >= engaged.Count)
                    return ToolResult.Error("optionIndex " + idx + " is out of range (there are " +
                        engaged.Count + (engaged.Count == 1 ? " battle)." : " battles)."));
            }
            else if (args["force"].AsBool())
            {
                idx = 0; // force opens the FIRST pending battle — it never auto-resolves (combat is not skippable)
            }
            else
            {
                return ToolResult.Error(engaged.Count + " agent(s) under attack. Pick which battle to open with " +
                    "resolve_decision optionIndex (see get_pending_decision), or force=true to open the first.");
            }

            UA def = engaged[idx];
            UA att = def.engagedBy as UA;
            if (att == null)
                return ToolResult.Error(Name(def) + " is engaged, but not by a hero — nothing to open.");

            try
            {
                // Exactly what UIE_AgentRoster.doAgentBattle / World.bEndTurn do: the BattleAgents ctor clears
                // engagedBy/engaging on both sides and sets up the fight; popBattle makes it the ui.blocker,
                // which PopupBattleAgentHandler then drives.
                BattleAgents battle = new BattleAgents(att, def);
                ctx.Map.world.prefabStore.popBattle(battle);
            }
            catch (Exception e)
            {
                return ToolResult.Error("could not open the battle: " + e.Message);
            }

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "combat")
                .Set("opened", true)
                .Set("agent", Summaries.UnitRef(ctx, def))
                .Set("attacker", Summaries.UnitRef(ctx, att))
                .Set("hint", "the combat menu is now open — call get_pending_decision to see it, then " +
                    "resolve_decision (fight to the end / flee / retreat)."));
        }

        // ---------- engagement detection (mirrors UIE_AgentRoster.bFight / World.bEndTurn:658) ----------

        private static List<UA> Engaged(GameContext ctx)
        {
            var result = new List<UA>();
            Map map = ctx != null ? ctx.Map : null;
            if (map == null || map.units == null) return result;
            if (map.automatic) return result; // headless AI game: no player battles to drive
            foreach (Unit u in map.units)
            {
                if (u == null || u.isDead || !u.isCommandable()) continue;
                UA def = u as UA;
                if (def == null) continue;
                if (def.engagedBy is UA att && !att.isDead && def.turnLastEngaged == map.turn)
                    result.Add(def);
            }
            return result;
        }

        private static string Verdict(int you, int them)
        {
            if (them <= 0) return "favoured";
            if (you >= them * 1.2) return "favoured";
            if (them >= you * 1.2) return "outmatched";
            return "even";
        }

        private static string Name(UA u) { try { return u.getName(); } catch { return "?"; } }
        private static int SafeInt(Func<int> get) { try { return get(); } catch { return 0; } }
    }
}
