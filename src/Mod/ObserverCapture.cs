using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Tools.Decisions;
using UnityEngine;

namespace ShadowsMcp
{
    /// <summary>
    /// Feeds <see cref="GameContext.ObserverEvents"/> while a HUMAN plays the game (observer mode).
    /// The headless feeds — end_turn's snapshot and the decision layer — never fire during human
    /// play, so observer capture rides the ModKernel hooks the game already calls (no Harmony):
    /// <list type="bullet">
    /// <item><c>onTurnStart</c> — end of <c>turnTick()</c>, this turn's messages fully generated;</item>
    /// <item><c>onTurnEnd</c> — top of the NEXT <c>turnTick()</c>, four lines before the game wipes
    /// <c>turnUnifiedMessages</c>: the last chance to flush messages added mid-turn;</item>
    /// <item><c>onUIFullscreenBlockerUpdate</c> — every popup open/close/promote;</item>
    /// <item>plus a per-frame check (McpBridgeBehaviour) so mid-turn messages reach a blocked
    /// long-poll within a frame instead of sitting invisible until the next turn.</item>
    /// </list>
    /// <c>Map.turnUnifiedMessages</c> is append-only between wipes, so a single captured-count
    /// cursor (<see cref="GameContext.ObserverCapturedCount"/>) both captures incrementally and
    /// makes snapshot-vs-mid-turn dedup structural: every message is copied exactly once.
    ///
    /// Static by necessity: ModCore (the ModKernel) is serialized into save files and must hold no
    /// instance state; all mutable capture state lives on <see cref="GameContext"/>. Every method
    /// checks observer mode at execution time (patching is unconditional, capture is not) and never
    /// throws out of a game hook.
    ///
    /// DiscoveryMode audit (v1): everything captured here — unified-message text, popup titles,
    /// entity stubs — is exactly what the human already sees on screen, and the only
    /// DiscoveryMode-gated data in the codebase is the archetype ability previews
    /// (Summaries.AbstractionSummary), which never ride events. If richer enrichment is added
    /// later, gate it with the positive-guard-omission pattern used there.
    /// </summary>
    public static class ObserverCapture
    {
        /// <summary>Per-frame incremental capture (wired to McpBridgeBehaviour.Update). Idle cost
        /// when nothing changed: a couple of null checks and an int compare.</summary>
        public static void OnFrame(GameContext ctx)
        {
            try
            {
                if (ctx == null || !ctx.Config.ObserverMode) return;
                Map map = ctx.Map;
                if (map == null || !map.burnInComplete) return;
                CaptureNewMessages(ctx, map, map.turn);
            }
            catch { }
        }

        /// <summary>
        /// Called from ModCore.onTurnEnd — inside <c>turnTick()</c> after <c>turn++</c> but before
        /// <c>turnUnifiedMessages.Clear()</c>, so the list still holds the turn that just ended
        /// (= <c>map.turn - 1</c>). Flushes anything the frame loop has not captured yet, mirrors
        /// the turn into the get_recent_events log (idempotent per turn; the agent-driven end_turn
        /// path refuses in observer mode, so the two feeders can never double-log), and resets the
        /// capture cursor for the wipe that follows synchronously in this same call stack.
        /// </summary>
        public static void OnTurnEnd(GameContext ctx, Map map)
        {
            try
            {
                if (ctx == null || map == null || !ctx.Config.ObserverMode || !map.burnInComplete) return;
                CaptureNewMessages(ctx, map, map.turn - 1);
                ctx.Events.SnapshotTurn(map.turn - 1, map.turnUnifiedMessages);
                ctx.ObserverCapturedCount = 0; // the wipe follows before any frame can run
            }
            catch { }
        }

        /// <summary>
        /// Called from ModCore.onTurnStart — end of <c>turnTick()</c>, the new turn's messages fully
        /// generated. Appends a synthetic turn boundary (so a blocked poll wakes the instant the
        /// human ends a turn, even a quiet one) and captures the new turn's messages in the same
        /// batch — zero-frame latency on the turn's news.
        /// </summary>
        public static void OnTurnStart(GameContext ctx, Map map)
        {
            try
            {
                if (ctx == null || map == null || !ctx.Config.ObserverMode || !map.burnInComplete) return;
                ctx.ObserverEvents.Append(map.turn, "turn_start", "Turn " + map.turn + " begins");
                CaptureNewMessages(ctx, map, map.turn);
            }
            catch { }
        }

        /// <summary>
        /// Called from ModCore.onUIFullscreenBlockerUpdate on every blocker open/close/promote.
        /// The hook carries the NEW blocker (or null); on a close the previous popup GameObject is
        /// already destroyed, which is why its kind/title are cached at open time and the close
        /// path only reference-compares. Observe only: never dismiss, never resolve, never pump
        /// the blocker queue.
        /// </summary>
        public static void OnBlockerUpdate(GameContext ctx, GameObject blocker)
        {
            try
            {
                if (ctx == null) return;
                if (!ctx.Config.ObserverMode)
                {
                    ClearBlockerCache(ctx);
                    return;
                }

                // The popup we recorded as open is gone (closed, or replaced by a promoted one):
                // record the human's resolution under the popup's own kind, like RecentEventLog does.
                if (ctx.ObserverLastBlocker != null && !ReferenceEquals(blocker, ctx.ObserverLastBlocker))
                {
                    ctx.ObserverEvents.Append(TurnOf(ctx),
                        ctx.ObserverLastBlockerKind ?? "popup",
                        ctx.ObserverLastBlockerTitle,
                        "resolved by the player");
                    ClearBlockerCache(ctx);
                }

                if (blocker == null || ReferenceEquals(blocker, ctx.ObserverLastBlocker)) return;

                IDecisionHandler h = DecisionRegistry.Find(blocker);
                if (h == null) return; // unreachable: GenericButtonHandler matches everything

                string kind;
                try { kind = h.Kind(blocker) ?? "popup"; }
                catch { kind = "popup"; }

                string title, popupType, body;
                DecisionRegistry.DescribeForLog(ctx, h, blocker, out title, out popupType, out body);

                // PopupMsgUnified is skipped entirely: Map.addUnifiedMessage appends every one of
                // them to turnUnifiedMessages BEFORE popping it, so the message stream above already
                // carries its title, body and type (the same no-dedup rule the decision layer uses).
                if (popupType == "PopupMsgUnified") return;

                bool isChoice;
                try { isChoice = !h.IsInformational(blocker); }
                catch { isChoice = true; } // unreadable ⇒ treat as a real choice, matching the decision layer

                if (string.IsNullOrEmpty(title)) title = blocker.name;
                ctx.ObserverEvents.Append(TurnOf(ctx), "popup", title,
                    kind + (isChoice ? " — awaiting the player's choice" : ""));

                ctx.ObserverLastBlocker = blocker;
                ctx.ObserverLastBlockerKind = kind;
                ctx.ObserverLastBlockerTitle = title;
            }
            catch { }
        }

        /// <summary>
        /// Called from ModCore.OnMapSeen when the Map instance changed (new game or save load).
        /// Clears the buffer (stale cursors will read gap:true — ids keep counting) and, when
        /// observing, appends a synthetic marker so the companion learns WHY there is a gap.
        /// Runs regardless of observer mode: capture bookkeeping must never leak across games.
        /// </summary>
        public static void OnGameChanged(GameContext ctx)
        {
            try
            {
                if (ctx == null) return;
                ctx.ObserverEvents.Clear();
                ctx.ObserverCapturedCount = -1;
                ClearBlockerCache(ctx);
                if (ctx.Config.ObserverMode)
                    ctx.ObserverEvents.Append(TurnOf(ctx), "game_changed",
                        "A new game or save was loaded",
                        "earlier events are gone; your previous cursor reports gap:true - resume from next_cursor");
            }
            catch { }
        }

        /// <summary>Called from ModCore's config hook when the human flips the Observer mode option.</summary>
        public static void OnModeChanged(GameContext ctx, bool on)
        {
            try
            {
                if (ctx == null) return;
                if (on)
                {
                    // Fresh baseline: don't dump the current turn's backlog as "news". A popup
                    // already open right now gets no close event (its open was never recorded).
                    ctx.ObserverCapturedCount = -1;
                    ClearBlockerCache(ctx);
                }
                else
                {
                    // Wake any blocked poll so it returns its off-mode answer promptly.
                    ctx.ObserverEvents.PulseWaiters();
                }
            }
            catch { }
        }

        // ---------- internals ----------

        /// <summary>Copy messages [captured, Count) into the buffer tagged <paramref name="turnTag"/>
        /// and advance the cursor. A negative cursor is the baseline sentinel (record the count,
        /// capture nothing); a shrunk list means an unexpected wipe (re-baseline, capture nothing).</summary>
        private static void CaptureNewMessages(GameContext ctx, Map map, int turnTag)
        {
            var msgs = map.turnUnifiedMessages;
            if (msgs == null) return;
            int n = msgs.Count;
            if (ctx.ObserverCapturedCount < 0 || n < ctx.ObserverCapturedCount)
            {
                ctx.ObserverCapturedCount = n;
                return;
            }
            for (int i = ctx.ObserverCapturedCount; i < n; i++)
            {
                UnifiedMessage m = msgs[i];
                if (m == null) continue;
                string type = !string.IsNullOrEmpty(m.customMsgType) ? m.customMsgType : m.msgType.ToString();

                // objA/objB → actor/location stubs ({$id,$type,name}), the 0.10.0 enrichment
                // convention: a Location fills `location`, anything else stub-able fills `actor`.
                JsonValue actor = null, location = null;
                try { Classify(ctx, m.objA, ref actor, ref location); } catch { }
                try { Classify(ctx, m.objB, ref actor, ref location); } catch { }

                ctx.ObserverEvents.Append(turnTag, type,
                    Summaries.StripRichText(m.title), Summaries.StripRichText(m.message),
                    actor, location);
            }
            ctx.ObserverCapturedCount = n;
        }

        private static void Classify(GameContext ctx, object obj, ref JsonValue actor, ref JsonValue location)
        {
            if (obj == null) return;
            if (obj is Location)
            {
                if (location == null) location = ctx.EntityStub(obj);
            }
            else if (actor == null)
            {
                actor = ctx.EntityStub(obj);
            }
        }

        private static void ClearBlockerCache(GameContext ctx)
        {
            ctx.ObserverLastBlocker = null;
            ctx.ObserverLastBlockerKind = null;
            ctx.ObserverLastBlockerTitle = null;
        }

        private static int TurnOf(GameContext ctx)
        {
            try { return ctx.Map != null ? ctx.Map.turn : 0; }
            catch { return 0; }
        }
    }
}
