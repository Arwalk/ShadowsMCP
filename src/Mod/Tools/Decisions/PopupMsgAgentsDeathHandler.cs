using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;
using UnityEngine.UI;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The agent-death notification (<see cref="PopupMsgAgentsDeath"/>) the game raises during turn
    /// processing when one of your agents dies. It is purely informational — both of its buttons just
    /// close it (<c>dismiss()</c>, or <c>dismissAgentA()</c> which pans the camera to the fallen agent
    /// first), each calling <c>ui.removeBlocker</c>. We answer by invoking those public methods
    /// directly, giving clean labels instead of the generic button sweep. Because dismissing loses no
    /// choice, this is flagged <see cref="IsInformational"/> so end_turn's force path auto-clears it.
    /// </summary>
    public sealed class PopupMsgAgentsDeathHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupMsgAgentsDeath>() != null;
        }

        public string Kind(GameObject blocker) { return "death"; }

        // A death notice carries no real choice (both buttons just close it): safe to auto-dismiss.
        public bool IsInformational(GameObject blocker) { return true; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            string who = AgentName(blocker.GetComponent<PopupMsgAgentsDeath>());
            return who == null ? "an agent has died" : "death: " + who + " has died";
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupMsgAgentsDeath popup = blocker.GetComponent<PopupMsgAgentsDeath>();
            JsonValue options = JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("index", 0)
                    .Set("label", "Dismiss")
                    .Set("enabled", true))
                .Add(JsonValue.NewObject()
                    .Set("index", 1)
                    .Set("label", "Focus the fallen agent's location, then dismiss")
                    .Set("enabled", true));

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "death")
                .Set("popupType", "PopupMsgAgentsDeath")
                .Set("title", AgentName(popup) != null ? AgentName(popup) + " has died" : "An agent has died")
                .Set("agent", popup != null ? Summaries.UnitRef(ctx, popup.agentA) : JsonValue.Null)
                .Set("text", MessageText(popup))
                .Set("options", options)
                .Set("note", "Informational only — dismissing loses nothing. Either option (or force=true) " +
                    "closes it; end_turn with force=true auto-dismisses these.")
                .Set("resolveWith", "resolve_decision with optionIndex 0/1, or force=true");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupMsgAgentsDeath popup = blocker.GetComponent<PopupMsgAgentsDeath>();
            UIMaster ui = SafeUi(ctx);

            // optionIndex 1 pans the camera to the agent first; anything else (or force) just closes it.
            int index = args["optionIndex"].AsInt(0);
            string chose;
            if (index == 1 && !args["force"].AsBool() && CanPanToAgent(popup))
            {
                popup.dismissAgentA();
                chose = "Focus the fallen agent's location, then dismiss";
            }
            else
            {
                popup.dismiss();
                chose = "Dismiss";
            }

            // Success = the popup we clicked is gone. When it was the last blocker, removeBlocker sets
            // ui.blocker to null and destroys this object; Unity's overloaded == makes `blocker == null`
            // true for a destroyed object, so this covers both "queue promoted a new popup" and "nothing
            // left" (where `ui.blocker != blocker` alone would wrongly read false — null != destroyed).
            bool resolved = blocker == null || ui == null || ui.blocker != blocker;
            if (!resolved)
                return ToolResult.Error("could not dismiss the death notice.");

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "death")
                .Set("chose", chose));
        }

        // ---------- reading popup fields ----------

        private static string AgentName(PopupMsgAgentsDeath popup)
        {
            try { return popup != null && popup.agentA != null ? popup.agentA.getName() : null; }
            catch { return null; }
        }

        private static string MessageText(PopupMsgAgentsDeath popup)
        {
            try { return popup != null && popup.text != null ? popup.text.text : null; }
            catch { return null; }
        }

        /// <summary>dismissAgentA() pans to agentA.location.hex; guard against a missing location.</summary>
        private static bool CanPanToAgent(PopupMsgAgentsDeath popup)
        {
            try { return popup != null && popup.agentA != null && popup.agentA.location != null; }
            catch { return false; }
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
