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

        /// <summary>Cursor-addressable feed for observer mode's wait_for_events long-poll, fed by
        /// <see cref="ObserverCapture"/> during human play. Cleared on new game / load alongside
        /// <see cref="Events"/>; its id counter deliberately survives the clear (see its cursor
        /// contract). The only mod state both the main thread and blocked HTTP workers touch.</summary>
        public readonly ObserverEventBuffer ObserverEvents = new ObserverEventBuffer();

        /// <summary>How many of the current <c>Map.turnUnifiedMessages</c> ObserverCapture already
        /// copied into <see cref="ObserverEvents"/> (the list is append-only between turnTick wipes,
        /// so one counter both captures incrementally and dedupes). -1 = needs a baseline: capture
        /// nothing, just record the current count — set on game change and observer-mode enable so a
        /// loaded save's backlog or pre-toggle history is not replayed. Main-thread only.</summary>
        public int ObserverCapturedCount = -1;

        /// <summary>The popup blocker ObserverCapture last recorded as open, kept ONLY for reference
        /// identity: by the time the close notification fires the GameObject is already destroyed, so
        /// its kind/title are cached alongside and the object itself must never be dereferenced.
        /// Main-thread only.</summary>
        public UnityEngine.GameObject ObserverLastBlocker;
        public string ObserverLastBlockerKind;
        public string ObserverLastBlockerTitle;

        /// <summary>Ids of contextual tips already surfaced this game (the mod-side analogue of the base
        /// game's HintSystem.hasShown[]). A tip fires at most once per game. Cleared only when a
        /// genuinely different game is loaded (Map.seed changed - see ModCore.OnMapSeen), NOT on every
        /// Map-instance swap: reloading or mid-session Map replacement of the same world must not
        /// re-spam tips the agent already read. Touched only on the main thread (single-flighted by
        /// the dispatcher), so a plain HashSet needs no lock.</summary>
        public readonly HashSet<string> ShownTips = new HashSet<string>();

        /// <summary>Per-boilerplate-key emission counters for the shown-once machinery (see
        /// <see cref="Boilerplate"/>): fixed how-to texts (trading carousel note, resolve hints,
        /// orders legend...) are sent in full the first time, then shrunk/omitted, with periodic
        /// full re-emissions. Same lifecycle as <see cref="ShownTips"/> (cleared on a new world,
        /// kept across same-game Map swaps). Main-thread only.</summary>
        public readonly Dictionary<string, int> BoilerplateCounts = new Dictionary<string, int>();

        /// <summary>Titles of narrative event popups (kind "event") already rendered in full this
        /// game. A recurring event of the same title re-renders with only its dynamic tail (the
        /// "X is performing challenge Y, progress N/M" line) - the static prose was already paid
        /// for. Same lifecycle as <see cref="ShownTips"/>. Main-thread only.</summary>
        public readonly HashSet<string> SeenEventTitles = new HashSet<string>();

        /// <summary>Map.seed of the world the one-shot sets above belong to (0 = none tracked yet).
        /// The seed is generated once per world and serialized with it, so it is identical across
        /// every Map instance of the same logical game - the stable identity that distinguishes
        /// "same game, new Map object" (keep the sets) from "different game" (clear them).</summary>
        public long KnownMapSeed;

        // The MCP initialize request arrives on the HTTP worker thread while all the shown-once
        // state above is main-thread-only, so the reconnect signal crosses over as a lone volatile
        // flag: initialize raises it, and the next main-thread Boilerplate emission consumes it
        // (clearing the counters so a fresh-context client gets the full texts again).
        private volatile bool _boilerplateReset;

        /// <summary>Signal (from any thread) that the MCP client re-initialized and its context is
        /// presumed gone: all shown-once boilerplate should be re-emitted in full.</summary>
        public void RequestBoilerplateReset() { _boilerplateReset = true; }

        /// <summary>Consume the reconnect signal (main thread). True at most once per initialize.</summary>
        public bool ConsumeBoilerplateReset()
        {
            if (!_boilerplateReset) return false;
            _boilerplateReset = false;
            return true;
        }

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
