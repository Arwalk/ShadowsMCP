using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Knows how to read and answer one family of modal popups (the game's <c>ui.blocker</c>).
    /// Register concrete handlers in <see cref="DecisionRegistry"/>; the first whose
    /// <see cref="CanHandle"/> matches an open blocker owns it. This is the extension seam:
    /// covering a new popup (battles, holy order, item trading…) is a new handler, no core edits.
    ///
    /// All game-object access lives in these handlers (the same confinement Summaries.cs and
    /// ActionTools.cs declare); every method here runs on Unity's main thread.
    /// </summary>
    public interface IDecisionHandler
    {
        /// <summary>True if this handler recognizes the open blocker (usually a GetComponent check).</summary>
        bool CanHandle(GameObject blocker);

        /// <summary>Short machine label for the compact/banner forms, e.g. "event", "levelUp".</summary>
        string Kind(GameObject blocker);

        /// <summary>
        /// True when this blocker is a pure notification with no meaningful choice, so force-dismissing
        /// it (as end_turn's force path does) loses nothing. False for popups where an option actually
        /// matters (narrative events, level-up trait picks) — those must be answered, never auto-dismissed.
        /// </summary>
        bool IsInformational(GameObject blocker);

        /// <summary>One-line human phrase for the banner, e.g. event: "A Stranger at the Gate".</summary>
        string Headline(GameContext ctx, GameObject blocker);

        /// <summary>
        /// Full detail for get_pending_decision:
        /// { kind, popupType, title, description, options:[ {index, label, description, enabled} ] }.
        /// </summary>
        JsonValue Describe(GameContext ctx, GameObject blocker);

        /// <summary>
        /// Answer the decision. <paramref name="args"/> carries optionIndex (int) and force (bool).
        /// Runs on the main thread; return the outcome or an error.
        /// </summary>
        ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args);
    }
}
