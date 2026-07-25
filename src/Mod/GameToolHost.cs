using System;
using System.Collections.Generic;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tools.Decisions;

namespace ShadowsMcp
{
    /// <summary>
    /// Tool host for the live game. Every ordinary tool call is marshalled onto Unity's
    /// main thread as one dispatcher job; a single-flight lock keeps tool calls from
    /// interleaving. "Server-thread" tools (end_turn) orchestrate their own dispatcher
    /// hops so they can poll game state without ever blocking the main thread.
    /// </summary>
    public sealed class GameToolHost : ToolHostBase
    {
        private readonly GameContext _ctx;
        private readonly object _singleFlight = new object();
        private readonly HashSet<string> _serverThreadTools = new HashSet<string>(StringComparer.Ordinal);

        public GameToolHost(GameContext ctx)
        {
            _ctx = ctx;
        }

        /// <summary>Register a tool whose handler runs on the HTTP worker thread and does its own dispatching.</summary>
        public void RegisterServerThread(ToolDefinition tool)
        {
            Register(tool);
            _serverThreadTools.Add(tool.Name);
        }

        public override ToolResult Execute(string name, JsonValue args)
        {
            if (!HasTool(name)) return null;
            lock (_singleFlight)
            {
                if (_serverThreadTools.Contains(name))
                {
                    // Server-thread tools do their own dispatching; stamp on a short main-thread hop.
                    ToolResult r = base.Execute(name, args);
                    return _ctx.Dispatcher.Run(() => Stamp(r), _ctx.Config.ToolTimeoutMs);
                }
                return _ctx.Dispatcher.Run(() => Stamp(base.Execute(name, args)), _ctx.Config.ToolTimeoutMs);
            }
        }

        /// <summary>
        /// While a modal decision popup is open, prepend a one-line banner to every tool result so
        /// the agent can't miss it (it also shows in game_overview). Runs on the main thread.
        /// </summary>
        private ToolResult Stamp(ToolResult result)
        {
            if (result == null) return null;
            try
            {
                string banner = DecisionRegistry.Banner(_ctx);
                if (string.IsNullOrEmpty(banner))
                {
                    _ctx.LastBanner = null; // decision cleared — the next one gets the full banner again
                }
                else
                {
                    // Same decision as last stamp: the agent already saw the full line (and it stays in
                    // game_overview.pendingDecision) — don't re-pay its tokens on every exploratory call.
                    string stamp = banner == _ctx.LastBanner
                        ? "⚠ decision still pending - resolve_decision."
                        : banner;
                    _ctx.LastBanner = banner;
                    result.Text = stamp + "\n\n" + (result.Text ?? "");
                }
            }
            catch
            {
                // Stamping is advisory; never fail a tool because of it.
            }
            return result;
        }
    }
}
