using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp
{
    /// <summary>
    /// The mod's own persistent, bounded "what happened recently" feed, surfaced by the
    /// <c>get_recent_events</c> tool. The game keeps no cross-turn event log an agent can read:
    /// <c>Map.turnUnifiedMessages</c> is wiped at the top of every <c>turnTick()</c>, and the
    /// notable popups (agent deaths, level-ups, narrative events) are transient <c>ui.blocker</c>
    /// GameObjects that append to no list. So the mod accumulates its own log from the two points it
    /// already controls during headless play:
    /// <list type="bullet">
    /// <item>each <c>end_turn</c> snapshots that turn's <c>Map.turnUnifiedMessages</c> (idle agents,
    /// wars, seals, hero actions) before the next tick clears it — see <c>ActionTools.EndTurn</c>;</item>
    /// <item>the decision layer records the death / level-up / narrative-event popups it dismisses or
    /// resolves — see <c>DecisionRegistry</c>. Those three kinds never call <c>addUnifiedMessage</c>, so
    /// they can never duplicate a snapshot entry (the no-dedup guarantee).</item>
    /// </list>
    /// Lives only on <see cref="GameContext"/> (never referenced by any game object), so nothing here
    /// can leak into save files. Cleared on new game / load in <c>ModCore.OnMapSeen</c>. All access is on
    /// the Unity main thread via the dispatcher; the lock is defensive, matching <see cref="EntityRegistry"/>.
    /// </summary>
    public sealed class RecentEventLog
    {
        /// <summary>Max entries retained; older ones are trimmed on insert. get_recent_events reads the
        /// newest <c>limit</c> of these.</summary>
        private const int Capacity = 500;

        private sealed class Entry
        {
            public int Turn;
            public string Type;
            public string Title;
            public string Message;
            public string Resolution;
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private int _lastSnapshotTurn = -1;

        /// <summary>
        /// Copy the turn's unified status messages (idle agents, wars, seals, hero actions) into the
        /// log, oldest-first, tagged with the turn. Idempotent per turn: a repeat call for a turn already
        /// snapshotted is ignored, so a non-advancing or re-driven end_turn cannot double-log.
        /// </summary>
        public void SnapshotTurn(int turn, IList<UnifiedMessage> msgs)
        {
            if (msgs == null || msgs.Count == 0) return;
            lock (_lock)
            {
                if (turn <= _lastSnapshotTurn) return;
                _lastSnapshotTurn = turn;
                foreach (UnifiedMessage m in msgs)
                {
                    if (m == null) continue;
                    _entries.Add(new Entry
                    {
                        Turn = turn,
                        Type = !string.IsNullOrEmpty(m.customMsgType) ? m.customMsgType : m.msgType.ToString(),
                        Title = m.title,
                        Message = m.message
                    });
                }
                Trim();
            }
        }

        /// <summary>
        /// Record a popup the decision layer dismissed or resolved (agent death, level-up, narrative
        /// event) — events the game persists nowhere and that never appear in a turn snapshot.
        /// </summary>
        public void RecordPopup(int turn, string type, string title, string resolution)
        {
            lock (_lock)
            {
                _entries.Add(new Entry { Turn = turn, Type = type, Title = title, Resolution = resolution });
                Trim();
            }
        }

        /// <summary>The newest <paramref name="limit"/> events, newest-first:
        /// <c>{ total, returned, items:[{turn,type,title,message?,resolution?}] }</c>.</summary>
        public JsonValue Read(int limit)
        {
            lock (_lock)
            {
                int total = _entries.Count;
                JsonValue arr = JsonValue.NewArray();
                for (int i = total - 1; i >= 0 && arr.Count < limit; i--)
                    arr.Add(ToJson(_entries[i]));
                return JsonValue.NewObject()
                    .Set("total", total)
                    .Set("returned", arr.Count)
                    .Set("items", arr);
            }
        }

        /// <summary>Drop everything — called when a new game or save is loaded.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _lastSnapshotTurn = -1;
            }
        }

        private void Trim()
        {
            int excess = _entries.Count - Capacity;
            if (excess > 0) _entries.RemoveRange(0, excess);
        }

        private static JsonValue ToJson(Entry e)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("turn", e.Turn)
                .Set("type", e.Type)
                .Set("title", e.Title);
            if (!string.IsNullOrEmpty(e.Message)) o.Set("message", e.Message);
            if (!string.IsNullOrEmpty(e.Resolution)) o.Set("resolution", e.Resolution);
            return o;
        }
    }
}
