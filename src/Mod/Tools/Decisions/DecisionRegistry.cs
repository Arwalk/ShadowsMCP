using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Central access to whatever decision the game is currently waiting on. Reads game state
    /// <b>live</b> on the main thread every call — no cached state, so it stays correct across
    /// save/load (which re-creates the mod kernel; see docs/ground-truth-notes.md).
    ///
    /// Two kinds of pending decision, checked in this priority (matching World.bEndTurn, whose
    /// ui.blocker guard precedes the idle loop):
    ///   1. a modal popup (<c>ui.blocker</c>) → an <see cref="IDecisionHandler"/>;
    ///   2. else a non-modal state that blocks turn end without a popup → an
    ///      <see cref="INonModalDecision"/> (e.g. the idle-agent alert).
    /// Add handlers to either array to cover more.
    /// </summary>
    public static class DecisionRegistry
    {
        private static readonly IDecisionHandler[] Handlers =
        {
            new PopupEventHandler(),
            new PopupLevelupHandler(),
            new PopupMsgAgentsDeathHandler(),
            new PopupItemTradingHandler(),
            new PopupBattleAgentHandler(), // the agent-duel combat menu (multi-round: step / flee / retreat / reorder)
            // ... add further bespoke handlers (PopupHolyOrder …) here, before the fallback.
            new GenericButtonHandler(), // must stay last: CanHandle is always true; lists any popup's buttons
        };

        // Checked only when no modal blocker is open. Order = priority.
        private static readonly INonModalDecision[] NonModal =
        {
            new AgentCombatDecision(), // an agent under attack this turn — most urgent (bEndTurn checks it first)
            new IdleAgentsDecision(),
            // ... future: pending skill points …
        };

        private static INonModalDecision FirstNonModal(GameContext ctx)
        {
            foreach (INonModalDecision d in NonModal)
            {
                try { if (d.IsPending(ctx)) return d; }
                catch { }
            }
            return null;
        }

        /// <summary>The open modal's GameObject, or null when nothing is blocking.</summary>
        public static GameObject CurrentBlocker(GameContext ctx)
        {
            try
            {
                Map map = ctx != null ? ctx.Map : null;
                if (map == null || map.world == null || map.world.ui == null) return null;
                return map.world.ui.blocker;
            }
            catch { return null; }
        }

        public static IDecisionHandler Find(GameObject blocker)
        {
            if (blocker == null) return null;
            foreach (IDecisionHandler h in Handlers)
            {
                if (h.CanHandle(blocker)) return h;
            }
            return null; // unreachable: GenericPopupHandler matches everything
        }

        /// <summary>Full detail for get_pending_decision.</summary>
        public static JsonValue Current(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                if (h != null) return h.Describe(ctx, blocker);
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null) return nm.Describe(ctx);
            return JsonValue.NewObject().Set("pending", false);
        }

        /// <summary>
        /// Full decision detail for game_overview / end_turn: the same object get_pending_decision
        /// returns (kind, title, options with indices &amp; labels), or JsonValue.Null when nothing is
        /// pending. Surfacing the whole thing inline lets an agent that only has game_overview / end_turn
        /// loaded see exactly what to pick, without needing the (deferrable) get_pending_decision tool.
        /// </summary>
        public static JsonValue FullOrNull(GameContext ctx)
        {
            JsonValue full = Current(ctx);
            return full["pending"].AsBool() ? full : JsonValue.Null;
        }

        /// <summary>Compact summary for game_overview: null when nothing is pending.</summary>
        public static JsonValue Compact(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                if (h != null) return CompactOf(h.Describe(ctx, blocker), h.Kind(blocker), true);
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null) return CompactOf(nm.Describe(ctx), nm.Kind(), false);
            return JsonValue.Null;
        }

        private static JsonValue CompactOf(JsonValue full, string kind, bool isModal)
        {
            JsonValue compact = JsonValue.NewObject()
                .Set("kind", kind)
                .Set("optionCount", full["options"].Count)
                .Set("hint", "call get_pending_decision, then resolve_decision");
            if (isModal) compact.Set("popupType", full["popupType"]);
            if (!full["title"].IsNull) compact.Set("title", full["title"]);
            return compact;
        }

        /// <summary>One-line banner prepended to tool results while a decision is pending; null otherwise.</summary>
        public static string Banner(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                if (h != null)
                    return "⚠ A decision is pending (" + h.Headline(ctx, blocker) +
                        "). Call get_pending_decision, then resolve_decision.";
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null)
                return "⚠ " + nm.Headline(ctx) + ". Call get_pending_decision, then resolve_decision.";
            return null;
        }

        /// <summary>Answer the current decision (used by resolve_decision).</summary>
        public static ToolResult Resolve(GameContext ctx, JsonValue args)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                if (h != null) return ResolveAndLog(ctx, h, blocker, args);
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null) return nm.Resolve(ctx, args);
            return ToolResult.Error("no decision is pending (nothing needs your attention right now).");
        }

        private static readonly JsonValue ForceArgs = JsonValue.NewObject().Set("force", true);

        // ---------- recent-events capture ----------

        /// <summary>Popup kinds worth logging to the recent-events feed: the ones that never call
        /// <c>addUnifiedMessage</c>, so they appear in no end_turn snapshot and would otherwise vanish on
        /// dismiss. Kept disjoint from the snapshot stream so the two feeds never double-count.</summary>
        private static readonly HashSet<string> LoggableKinds = new HashSet<string> { "death", "event", "levelUp" };

        private static bool IsLoggableKind(string kind) { return kind != null && LoggableKinds.Contains(kind); }

        private static int TurnOf(GameContext ctx)
        {
            try { return ctx != null && ctx.Map != null ? ctx.Map.turn : 0; }
            catch { return 0; }
        }

        private static string SafeKind(IDecisionHandler h, GameObject blocker)
        {
            try { return h.Kind(blocker); } catch { return null; }
        }

        /// <summary>The popup's title via its <c>Describe</c>, falling back to the banner headline;
        /// null on error. Must be read before Resolve, which destroys the blocker.</summary>
        private static string LogTitle(GameContext ctx, IDecisionHandler h, GameObject blocker)
        {
            try
            {
                string t = h.Describe(ctx, blocker)["title"].AsString();
                if (!string.IsNullOrEmpty(t)) return t;
                return h.Headline(ctx, blocker);
            }
            catch { return null; }
        }

        /// <summary>Answer a modal decision and, for the popup kinds the game persists nowhere (narrative
        /// events, level-ups), record it in the recent-events feed. Title and chosen option come from the
        /// handler's own <c>Describe</c> BEFORE resolving, because Resolve destroys the blocker.
        /// Non-loggable kinds pass straight through unchanged.</summary>
        private static ToolResult ResolveAndLog(GameContext ctx, IDecisionHandler h, GameObject blocker, JsonValue args)
        {
            string kind = SafeKind(h, blocker);
            if (!IsLoggableKind(kind)) return h.Resolve(ctx, blocker, args);

            string title = null, resolution = "resolved";
            try
            {
                JsonValue d = h.Describe(ctx, blocker);
                title = d["title"].AsString();
                if (!args["optionIndex"].IsNull)
                {
                    string label = d["options"][args["optionIndex"].AsInt(-1)]["label"].AsString();
                    if (!string.IsNullOrEmpty(label)) resolution = label;
                }
            }
            catch { }

            ToolResult rr = h.Resolve(ctx, blocker, args);
            if (rr != null && !rr.IsError)
                ctx.Events.RecordPopup(TurnOf(ctx), kind, title, resolution);
            return rr;
        }

        /// <summary>
        /// Force-dismiss every purely-informational popup currently blocking (agent deaths, message
        /// boxes, autosave notices), so a headless <c>end_turn(force:true)</c> loop never stalls on a
        /// notification. Stops at the first popup that carries a real choice (narrative event, level-up)
        /// or is otherwise unknown — that one is left open and flagged, never silently answered.
        ///
        /// Runs on the main thread (called from within end_turn's dispatcher job). Death popups queue on
        /// the immediate <c>blockerQueue</c>, which <c>removeBlocker</c> promotes synchronously; we also
        /// pump <c>checkBlockerQueue</c> when the blocker clears, to drain the delayed queue in-job.
        /// </summary>
        public static JsonValue AutoDismissInformational(GameContext ctx, int cap = 25)
        {
            var dismissed = new List<string>();
            string remaining = null;
            bool cappedOut = false;

            int i = 0;
            while (true)
            {
                if (i++ >= cap) { cappedOut = true; break; }

                GameObject blocker = CurrentBlocker(ctx);
                if (blocker == null)
                {
                    // Promote anything sitting in the (delayed) queue, then re-check once.
                    PumpQueue(ctx);
                    blocker = CurrentBlocker(ctx);
                    if (blocker == null) break;
                }

                IDecisionHandler h = Find(blocker);
                if (h == null || !h.IsInformational(blocker))
                {
                    // A real choice (or an unknown popup) — leave it for get_pending_decision / resolve_decision.
                    if (h != null) remaining = h.Kind(blocker);
                    break;
                }

                string type = h.Kind(blocker);
                // Capture the title before Resolve destroys the blocker; among informational popups only
                // death is worth logging (persisted nowhere else, and it appears in no turn snapshot).
                string logTitle = IsLoggableKind(type) ? LogTitle(ctx, h, blocker) : null;
                ToolResult r = h.Resolve(ctx, blocker, ForceArgs);
                if (r == null || r.IsError)
                {
                    // Dismiss did not take (e.g. a button no-op) — stop rather than spin.
                    // The handler already verifies the blocker actually cleared before returning Ok.
                    remaining = type;
                    break;
                }
                dismissed.Add(type);
                if (IsLoggableKind(type)) ctx.Events.RecordPopup(TurnOf(ctx), type, logTitle, "dismissed");
            }

            if (cappedOut)
                Log.Info("auto-dismiss hit the " + cap + "-popup cap; " +
                    (CurrentBlocker(ctx) != null ? "a popup is still open (banner will flag it)" : "queue drained"));

            JsonValue types = JsonValue.NewArray();
            foreach (string t in dismissed) types.Add(JsonValue.Of(t));
            JsonValue o = JsonValue.NewObject()
                .Set("count", dismissed.Count)
                .Set("dismissed", types);
            if (remaining != null) o.Set("remaining", remaining);
            if (cappedOut) o.Set("cappedOut", true);
            return o;
        }

        /// <summary>Promote any popup sitting in the delayed blocker queue into <c>ui.blocker</c>.
        /// A decision that opens a follow-up popup (e.g. a level-up chaining into the next) leaves it
        /// queued, not yet the live blocker; end_turn calls this before deciding the turn is stuck so a
        /// freshly-queued decision is surfaced instead of being mis-reported as an unknown guard.</summary>
        internal static void PumpQueue(GameContext ctx)
        {
            try
            {
                Map map = ctx != null ? ctx.Map : null;
                if (map != null && map.world != null && map.world.ui != null)
                    map.world.ui.checkBlockerQueue();
            }
            catch { }
        }
    }
}
