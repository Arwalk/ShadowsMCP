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
        private readonly HashSet<string> _concurrentTools = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _mutatingTools = new HashSet<string>(StringComparer.Ordinal);

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

        /// <summary>
        /// Register a long-poll tool: the handler runs on the HTTP worker thread OUTSIDE the
        /// single-flight lock (a wait_for_events blocking 25s inside it would stall every other
        /// tool call for the whole poll) and skips the Stamp hop — the banner machinery mutates
        /// main-thread-only state (ctx.LastBanner) and would race the locked path, and a long-poll
        /// tool carries the pending decision in its own payload anyway. The handler owns any
        /// main-thread access it needs (short dispatcher hops only, never the wait itself).
        /// </summary>
        public void RegisterConcurrent(ToolDefinition tool)
        {
            Register(tool);
            _concurrentTools.Add(tool.Name);
        }

        /// <summary>Register a game-mutating tool: refused centrally while observer mode is on (a
        /// human is playing; the connected agent must not fight them for control).</summary>
        public void RegisterMutating(ToolDefinition tool)
        {
            Register(tool);
            _mutatingTools.Add(tool.Name);
        }

        /// <summary>Server-thread variant of <see cref="RegisterMutating"/> (end_turn, new_game).</summary>
        public void RegisterServerThreadMutating(ToolDefinition tool)
        {
            RegisterServerThread(tool);
            _mutatingTools.Add(tool.Name);
        }

        public override ToolResult Execute(string name, JsonValue args)
        {
            if (!HasTool(name)) return null;
            if (_concurrentTools.Contains(name))
                return base.Execute(name, args); // no lock, no Stamp — see RegisterConcurrent
            // Checked before the lock: the refusal is instant, needs no main-thread hop, and reading
            // the config bool from this thread is safe (written only on the main thread; a one-toggle
            // staleness window is harmless).
            if (_ctx.Config.ObserverMode && _mutatingTools.Contains(name))
                return Tools.ObserverGuard.Refuse(name);
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
