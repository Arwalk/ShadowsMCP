using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// A pending player decision that blocks turn progression <b>without</b> opening a modal
    /// <c>ui.blocker</c> — so it can't be an <see cref="IDecisionHandler"/> (there is no popup
    /// GameObject to read). Keyed purely on game state. The idle-agent alert is the first such
    /// source; future ones (pending skill points, pending combat) slot in the same way.
    ///
    /// <see cref="DecisionRegistry"/> checks these only when no modal blocker is open, matching
    /// World.bEndTurn (its ui.blocker guard precedes the idle loop). All methods run on the main thread.
    /// </summary>
    public interface INonModalDecision
    {
        /// <summary>True when this decision is currently outstanding.</summary>
        bool IsPending(GameContext ctx);

        /// <summary>Short machine label for the compact/banner forms, e.g. "idleAgents".</summary>
        string Kind();

        /// <summary>One-line human phrase for the banner, e.g. "2 of your agents are idle (Vesh, Karn)".</summary>
        string Headline(GameContext ctx);

        /// <summary>Full detail for get_pending_decision: { pending, kind, title, options:[…], … }.</summary>
        JsonValue Describe(GameContext ctx);

        /// <summary>Answer the decision. args carries optionIndex (int) and force (bool).</summary>
        ToolResult Resolve(GameContext ctx, JsonValue args);
    }
}
