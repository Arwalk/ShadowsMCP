using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tools.Decisions;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// Tools for the game's modal decision windows (level-up trait picks, narrative choice
    /// events, and any other popup that blocks play). Detection and resolution are delegated to
    /// <see cref="DecisionRegistry"/> and its handlers, so new popup types are added there.
    /// A pending decision is also flagged in game_overview and banner-stamped on every tool result.
    /// </summary>
    public static class DecisionTools
    {
        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.Register(new ToolDefinition(
                "get_pending_decision",
                "Show the decision the game is currently waiting on (level-up trait pick, narrative " +
                "event choice, an idle-agent alert, or any other modal popup) with its options. Every " +
                "popup is listed as a set of numbered options you can pick with resolve_decision; " +
                "returns {pending:false} when nothing is open.",
                Schema.Object(),
                a => QueryTools.WithMap(ctx, map => ToolResult.Ok(DecisionRegistry.Current(ctx)))));

            host.Register(new ToolDefinition(
                "resolve_decision",
                "Answer the current pending decision (from get_pending_decision): optionIndex clicks that " +
                "option. force=true dismisses a popup like pressing OK (for a level-up it skips while " +
                "keeping the skill point). Idle-agent alert: optionIndex 0 passes all idle agents - or give " +
                "them orders; force does NOT pass idle (it blocks, like combat). A decision queued behind " +
                "this one is flagged in the result banner.",
                Schema.Object(
                    Schema.Prop("optionIndex", Schema.Integer("Zero-based index of the option to choose (from get_pending_decision)")),
                    Schema.Prop("force", Schema.Boolean("Skip a level-up / dismiss an unmodelled popup without choosing (does NOT pass the idle-agent alert — use optionIndex 0 for that)"))),
                a => QueryTools.WithMap(ctx, map => DecisionRegistry.Resolve(ctx, a))));
        }
    }
}
