using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Util;

namespace ShadowsMcp
{
    /// <summary>
    /// All runtime state for the mod, shared via ModCore's statics. Never referenced by any
    /// game object, so nothing here can leak into save files (the game serializes the whole
    /// Map graph, including the ModKernel instances themselves — see docs/ground-truth-notes.md).
    /// </summary>
    public sealed class GameContext
    {
        /// <summary>The current game's Map; null in the main menu. Written on the main thread
        /// by ModKernel hooks, read from server threads (volatile for visibility).</summary>
        private volatile Map _map;

        public Map Map
        {
            get { return _map; }
            set { _map = value; }
        }

        public readonly EntityRegistry Registry = new EntityRegistry();
        public readonly ModConfig Config = new ModConfig();

        /// <summary>Persistent, bounded "what happened recently" feed for get_recent_events, accumulated
        /// across turns from end_turn snapshots and dismissed/resolved popups. Reset on new game / load
        /// alongside <see cref="Registry"/> (see ModCore.OnMapSeen).</summary>
        public readonly RecentEventLog Events = new RecentEventLog();

        /// <summary>Ids of contextual tips already surfaced this game (the mod-side analogue of the base
        /// game's HintSystem.hasShown[]). A tip fires at most once per game. Reset on new game / load
        /// alongside <see cref="Registry"/> / <see cref="Events"/> (see ModCore.OnMapSeen), so one-shot
        /// tips never leak across games loaded in the same process. Touched only on the main thread
        /// (single-flighted by the dispatcher), so a plain HashSet needs no lock.</summary>
        public readonly HashSet<string> ShownTips = new HashSet<string>();

        /// <summary>The last decision banner stamped onto a tool result (see GameToolHost.Stamp).
        /// While the same decision stays pending, repeat stamps shrink to a one-liner — the full
        /// headline was already shown and remains in game_overview.pendingDecision. Cleared whenever
        /// no decision is pending, so a new decision always gets the full banner. Main-thread only.</summary>
        public string LastBanner;

        public MainThreadDispatcher Dispatcher;

        public object ResolveEntity(string id)
        {
            return Summaries.ResolveId(this, id);
        }

        public JsonValue EntityStub(object obj)
        {
            return Summaries.EntityStub(this, obj);
        }
    }
}
