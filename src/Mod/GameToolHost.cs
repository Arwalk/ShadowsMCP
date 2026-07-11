using System;
using System.Collections.Generic;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

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
                    return base.Execute(name, args);
                return _ctx.Dispatcher.Run(() => base.Execute(name, args), _ctx.Config.ToolTimeoutMs);
            }
        }
    }
}
