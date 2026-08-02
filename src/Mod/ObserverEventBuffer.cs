using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp
{
    /// <summary>
    /// Bounded, cursor-addressable event feed for observer mode, consumed by the
    /// <c>wait_for_events</c> long-poll. Modeled on <see cref="RecentEventLog"/>, but where that
    /// log's lock is defensive (all access dispatched to the main thread), the cross-thread access
    /// here is real: ObserverCapture appends on Unity's main thread while HTTP workers read — and
    /// <b>block</b> — on their own threads. <c>_lock</c> therefore doubles as the Monitor wait
    /// handle: every append pulses it so a blocked long-poll wakes immediately.
    ///
    /// Cursor contract: event ids are monotonically increasing and NEVER reset — <see cref="Clear"/>
    /// (new game / save load) keeps the counter and instead advances <c>_trimmedThrough</c> past
    /// every retained id, so any pre-clear cursor reads as <c>gap:true</c> rather than silently
    /// aliasing into the new game's events.
    ///
    /// Payloads are fully built at append time (on the main thread), so readers never touch game
    /// objects. Lives only on <see cref="GameContext"/>, so nothing here can leak into save files.
    /// </summary>
    public sealed class ObserverEventBuffer
    {
        /// <summary>Max entries retained; older ones are trimmed on insert (same bound as
        /// <see cref="RecentEventLog"/>). A trimmed-past cursor is reported via gap, not lost silently.</summary>
        private const int Capacity = 500;

        private sealed class Entry
        {
            public long Id;
            public JsonValue Payload; // {id,turn,type,title,message?,actor?,location?} — immutable after append
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _entries = new List<Entry>(); // oldest-first
        private long _nextId = 1;
        private long _trimmedThrough; // highest id evicted by Trim or Clear; 0 = nothing evicted yet

        /// <summary>
        /// Append one event (main thread) and wake every blocked <see cref="WaitForNew"/>.
        /// <paramref name="actor"/>/<paramref name="location"/> follow the entity-stub convention
        /// ({$id,$type,name}); null/empty optionals are omitted from the payload.
        /// </summary>
        public void Append(int turn, string type, string title,
            string message = null, JsonValue actor = null, JsonValue location = null)
        {
            lock (_lock)
            {
                long id = _nextId++;
                JsonValue payload = JsonValue.NewObject()
                    .Set("id", id)
                    .Set("turn", turn)
                    .Set("type", type)
                    .Set("title", title);
                if (!string.IsNullOrEmpty(message)) payload.Set("message", message);
                if (actor != null && !actor.IsNull) payload.Set("actor", actor);
                if (location != null && !location.IsNull) payload.Set("location", location);
                _entries.Add(new Entry { Id = id, Payload = payload });

                int excess = _entries.Count - Capacity;
                if (excess > 0)
                {
                    _trimmedThrough = _entries[excess - 1].Id;
                    _entries.RemoveRange(0, excess);
                }
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>
        /// Events with id &gt; <paramref name="cursor"/>, oldest-first, capped at <paramref name="max"/>.
        /// <paramref name="gap"/> is true when the cursor points before <c>_trimmedThrough</c> — the
        /// caller missed events (trimmed past, or the buffer was cleared by a new game / load).
        /// <paramref name="nextCursor"/> is the highest id returned; when nothing is returned it is
        /// max(cursor, _trimmedThrough) so a post-gap caller self-heals instead of re-reporting the
        /// gap forever, and a bogus future cursor snaps back to the latest real id.
        /// </summary>
        public JsonValue ReadSince(long cursor, int max, out bool gap, out long nextCursor)
        {
            lock (_lock)
            {
                gap = cursor < _trimmedThrough;
                JsonValue arr = JsonValue.NewArray();
                long last = -1;
                foreach (Entry e in _entries)
                {
                    if (e.Id <= cursor) continue;
                    if (arr.Count >= max) break;
                    arr.Add(e.Payload);
                    last = e.Id;
                }
                if (last >= 0)
                {
                    nextCursor = last;
                }
                else
                {
                    long latest = _entries.Count > 0 ? _entries[_entries.Count - 1].Id : _trimmedThrough;
                    nextCursor = cursor > latest ? latest : (cursor < _trimmedThrough ? _trimmedThrough : cursor);
                }
                return arr;
            }
        }

        /// <summary>
        /// Block (HTTP worker thread) until something past <paramref name="cursor"/> is readable —
        /// a new event, or the cursor having fallen behind the trim watermark (a gap is readable
        /// too: the caller must learn about it) — or the timeout elapses. Loops around
        /// <c>Monitor.Wait</c> with a Stopwatch deadline, so spurious wakes and pulses for other
        /// cursors re-check instead of returning early. True when something is readable.
        /// </summary>
        public bool WaitForNew(long cursor, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            lock (_lock)
            {
                while (true)
                {
                    if (HasReadable(cursor)) return true;
                    long remaining = timeoutMs - sw.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    Monitor.Wait(_lock, (int)remaining);
                }
            }
        }

        private bool HasReadable(long cursor)
        {
            if (cursor < _trimmedThrough) return true;
            return _entries.Count > 0 && _entries[_entries.Count - 1].Id > cursor;
        }

        /// <summary>
        /// New game / save load: drop every entry but KEEP the id counter, and advance the trim
        /// watermark so every pre-clear cursor reads as gap:true. Wakes blocked polls so a client
        /// waiting through the transition learns about it promptly.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _trimmedThrough = _nextId - 1;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Wake blocked waiters without appending — used when observer mode is toggled
        /// off, so an in-flight long-poll returns its off-mode answer instead of timing out.</summary>
        public void PulseWaiters()
        {
            lock (_lock)
            {
                Monitor.PulseAll(_lock);
            }
        }
    }
}
