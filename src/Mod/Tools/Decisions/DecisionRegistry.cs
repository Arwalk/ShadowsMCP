using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
            new PopupScrollSetHandler(), // the scrolling "pick one from this list" carousel (scandal victim, tags, minions)
            new PopupBattleAgentHandler(), // the agent-duel combat menu (multi-round: step / flee / retreat / reorder)
            new PopupMinionDismissalHandler(), // over-capacity minion keep/dismiss (toggle-then-commit; a real choice)
            new PopupChallengeCompleteHandler(), // stable Dismiss/Goto/Repeat options (Repeat's button toggles per frame)
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

        // ---------- decision instance ids ----------

        /// <summary>
        /// Token for the exact popup instance currently open. Popup GameObjects are created per popup
        /// and destroyed on resolve, so the managed identity hash is stable for THIS popup's lifetime
        /// and different for every new one — a client that read a decision can pass the token back as
        /// expectedDecisionId and be guaranteed it answers the popup it read, not a follow-up that got
        /// promoted from the queue in between. Kind prefix is cosmetic (readability in logs).
        /// </summary>
        private static string ModalDecisionId(IDecisionHandler h, GameObject blocker)
        {
            return "D-" + (SafeKind(h, blocker) ?? "popup") + "-" +
                unchecked((uint)RuntimeHelpers.GetHashCode(blocker)).ToString("x");
        }

        /// <summary>Non-modal decisions (idle agents, agent combat) have no popup instance; kind + turn
        /// identifies "this decision, this turn", which is the granularity a client can act on.</summary>
        private static string NonModalDecisionId(INonModalDecision d, GameContext ctx)
        {
            return "D-" + (SafeKind(d) ?? "nonmodal") + "-t" + TurnOf(ctx);
        }

        /// <summary>The decisionId of whatever is pending right now, or null when nothing is.</summary>
        internal static string CurrentDecisionId(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                return h != null ? ModalDecisionId(h, blocker) : null;
            }
            INonModalDecision nm = FirstNonModal(ctx);
            return nm != null ? NonModalDecisionId(nm, ctx) : null;
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
            return null; // unreachable: GenericButtonHandler matches everything
        }

        /// <summary>Full detail for get_pending_decision.</summary>
        public static JsonValue Current(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker != null)
            {
                IDecisionHandler h = Find(blocker);
                if (h != null) return SafeDescribe(ctx, h, blocker);
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null) return SafeDescribeNonModal(ctx, nm);
            return JsonValue.NewObject().Set("pending", false);
        }

        /// <summary>Describe a modal, never throwing. A blocker IS open at this point, so a handler that
        /// blows up must still report <c>pending:true</c> — swallowing it would tell the caller nothing is
        /// waiting while the game sits frozen behind a popup, and this runs on the commit path
        /// (<c>ActionTools.AttachPending</c>), where it would also mis-report a battle that did open.</summary>
        private static JsonValue SafeDescribe(GameContext ctx, IDecisionHandler h, GameObject blocker)
        {
            JsonValue described;
            try { described = h.Describe(ctx, blocker); }
            catch (Exception e)
            {
                described = Undescribable(SafeKind(h, blocker), e);
                try { if (blocker != null) described.Set("popupType", blocker.name); } catch { }
            }
            // Instance token for THIS popup — pass back as resolve_decision.expectedDecisionId to
            // guarantee the click lands on the popup that was read (chained popups get fresh ids).
            try { described.Set("decisionId", ModalDecisionId(h, blocker)); } catch { }
            return described;
        }

        /// <summary>Non-modal counterpart of <see cref="SafeDescribe"/>: describe without throwing and
        /// stamp the decisionId token.</summary>
        private static JsonValue SafeDescribeNonModal(GameContext ctx, INonModalDecision nm)
        {
            JsonValue described;
            try { described = nm.Describe(ctx); }
            catch (Exception e) { described = Undescribable(SafeKind(nm), e); }
            try { described.Set("decisionId", NonModalDecisionId(nm, ctx)); } catch { }
            return described;
        }

        /// <summary>A decision we know is pending but could not read: still actionable via force.</summary>
        private static JsonValue Undescribable(string kind, Exception e)
        {
            Log.Error("could not describe the pending decision", e);
            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", kind ?? "unknown")
                .Set("options", JsonValue.NewArray())
                .Set("note", "a decision is open but its details could not be read (" + Log.Describe(e, 1) +
                    "); resolve_decision with force=true, or handle it in the game window.")
                .Set("resolveWith", "resolve_decision with force=true");
        }

        private static string SafeKind(INonModalDecision d)
        {
            try { return d.Kind(); } catch { return null; }
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
                if (h != null) return CompactOf(SafeDescribe(ctx, h, blocker), SafeKind(h, blocker), true);
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null) return CompactOf(SafeDescribeNonModal(ctx, nm), SafeKind(nm), false);
            return JsonValue.Null;
        }

        private static JsonValue CompactOf(JsonValue full, string kind, bool isModal)
        {
            JsonValue compact = JsonValue.NewObject()
                .Set("kind", kind ?? "unknown")   // SafeKind returns null if a handler's Kind() threw
                .Set("optionCount", full["options"].Count)
                .Set("hint", "call get_pending_decision, then resolve_decision");
            if (isModal) compact.Set("popupType", full["popupType"]);
            if (!full["title"].IsNull) compact.Set("title", full["title"]);
            if (!full["decisionId"].IsNull) compact.Set("decisionId", full["decisionId"]);
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
                {
                    string headline;
                    try { headline = h.Headline(ctx, blocker); }
                    catch { headline = SafeKind(h, blocker) ?? "unreadable"; }
                    return "⚠ A decision is pending (" + headline +
                        "). Call get_pending_decision, then resolve_decision.";
                }
            }
            INonModalDecision nm = FirstNonModal(ctx);
            if (nm != null)
            {
                string headline;
                try { headline = nm.Headline(ctx); }
                catch { headline = "a decision is pending"; }
                return "⚠ " + headline + ". Call get_pending_decision, then resolve_decision.";
            }
            return null;
        }

        /// <summary>Answer the current decision (used by resolve_decision).</summary>
        public static ToolResult Resolve(GameContext ctx, JsonValue args)
        {
            // Optional stale-decision guard: when the caller says WHICH decision it is answering and
            // the pending one has changed since (a chained popup promoted from the queue, a battle
            // opening, a load...), click NOTHING and describe what is actually open now. Without the
            // param behaviour is unchanged. Applies to force too — force clears notices, it does not
            // license clicking blind on a popup the caller never read.
            string expected = args["expectedDecisionId"].AsString();
            if (!string.IsNullOrEmpty(expected))
            {
                string current = CurrentDecisionId(ctx);
                if (current == null)
                    return ToolResult.Error("expectedDecisionId " + expected + " was given but no decision "
                        + "is pending any more - nothing was clicked. Re-check game_overview.pendingDecision.");
                if (!string.Equals(current, expected, StringComparison.Ordinal))
                    return StaleDecisionError(ctx, expected, current);
            }
            // optionLabel: pick by what the option SAYS, not where it sits. Carousel-style lists
            // renumber as taken entries drop out (the tag picker moved 'Danger' off index 4 between
            // casts and a habitual index bought the wrong dislike, G14-#13); a label survives that.
            if (args["optionIndex"].IsNull && !string.IsNullOrEmpty(args["optionLabel"].AsString()))
            {
                ToolResult labelErr = MapOptionLabel(ctx, args);
                if (labelErr != null) return labelErr;
            }
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

        /// <summary>Translate <c>optionLabel</c> into <c>optionIndex</c> against the live decision:
        /// exact match (case-insensitive) wins, else a UNIQUE substring match; anything else is a
        /// refusal that lists the real labels. Writes the resolved index into <paramref name="args"/>
        /// and returns null on success.</summary>
        private static ToolResult MapOptionLabel(GameContext ctx, JsonValue args)
        {
            string want = args["optionLabel"].AsString().Trim();
            JsonValue opts;
            try { opts = Current(ctx)["options"]; }
            catch { opts = JsonValue.Null; }
            int exact = -1, partial = -1, partialCount = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                string label = opts[i]["label"].AsString();
                if (string.IsNullOrEmpty(label)) continue;
                int idx = opts[i]["index"].AsInt(i);
                if (string.Equals(label.Trim(), want, StringComparison.OrdinalIgnoreCase))
                {
                    exact = idx;
                    break;
                }
                if (label.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = idx;
                    partialCount++;
                }
            }
            int chosen = exact >= 0 ? exact : (partialCount == 1 ? partial : -1);
            if (chosen < 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("optionLabel \"").Append(want).Append("\" ")
                  .Append(partialCount > 1 ? "matches more than one option" : "matches no option")
                  .Append(" - nothing was clicked. Current options: ");
                for (int i = 0; i < opts.Count && i < 20; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(opts[i]["index"].AsInt(i)).Append(": ")
                      .Append(opts[i]["label"].AsString() ?? "?");
                }
                if (opts.Count > 20) sb.Append(", … (").Append(opts.Count).Append(" total)");
                sb.Append(". Use an exact or unique label, or optionIndex.");
                return ToolResult.Error(sb.ToString());
            }
            args.Set("optionIndex", chosen);
            return null;
        }

        /// <summary>Refusal for a mismatched expectedDecisionId, modeled on ActionTools.StaleChallengeError:
        /// name what changed, show the decision that IS open (with its options) so recovery is one call.</summary>
        private static ToolResult StaleDecisionError(GameContext ctx, string expected, string current)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("the pending decision changed: you expected ").Append(expected)
              .Append(" but the current one is ").Append(current);
            try
            {
                JsonValue d = Current(ctx);
                string kind = d["kind"].AsString();
                string title = d["title"].AsString();
                if (!string.IsNullOrEmpty(kind)) sb.Append(" (").Append(kind)
                    .Append(string.IsNullOrEmpty(title) ? "" : ", \"" + title + "\"").Append(")");
                JsonValue opts = d["options"];
                if (opts.Count > 0)
                {
                    sb.Append(". Nothing was clicked. Its options: ");
                    for (int i = 0; i < opts.Count && i < 10; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(i).Append(": ").Append(opts[i]["label"].AsString() ?? "?");
                    }
                    if (opts.Count > 10) sb.Append(", … (" + opts.Count + " total)");
                }
                else sb.Append(". Nothing was clicked");
            }
            catch { sb.Append(". Nothing was clicked"); }
            sb.Append(". Re-read it (get_pending_decision or game_overview.pendingDecision) and resolve "
                + "with the new decisionId.");
            return ToolResult.Error(sb.ToString());
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

        /// <summary>The popup's title (falling back to the banner headline), its <c>Popup*</c> type
        /// name and its body text, all from ONE <c>Describe</c> call. Must be read before Resolve, which
        /// destroys the blocker. All outputs are null on error — never throws. Internal so
        /// ObserverCapture can identify popups it merely observes (it never resolves them).</summary>
        internal static void DescribeForLog(GameContext ctx, IDecisionHandler h, GameObject blocker,
            out string title, out string popupType, out string body)
        {
            title = null; popupType = null; body = null;
            try
            {
                JsonValue d = h.Describe(ctx, blocker);
                title = d["title"].AsString();
                popupType = d["popupType"].AsString();
                body = d["text"].AsString();
            }
            catch { }
            if (string.IsNullOrEmpty(title))
            {
                try { title = h.Headline(ctx, blocker); } catch { title = null; }
            }
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

        /// <summary>True when the open modal carries a real choice — anything NOT marked informational
        /// (a pure "Dismiss" notification). end_turn(force) must never tick past one of these: force's
        /// contract is to clear notices, not to blow through decisions. An unreadable popup counts as a
        /// real choice (never bypass blindly), matching <see cref="AutoDismissInformational"/>'s stop rule.</summary>
        public static bool HardChoiceBlockerOpen(GameContext ctx)
        {
            GameObject blocker = CurrentBlocker(ctx);
            if (blocker == null) return false;
            try
            {
                IDecisionHandler h = Find(blocker);
                return h == null || !h.IsInformational(blocker);
            }
            catch { return true; }
        }

        /// <summary>
        /// Force-dismiss every purely-informational popup currently blocking (agent deaths, message
        /// boxes, autosave notices), so a headless <c>end_turn(force:true)</c> loop never stalls on a
        /// notification. Stops at the first popup that carries a real choice (narrative event, level-up)
        /// or is otherwise unknown — that one is left open and flagged, never silently answered.
        /// The autosave notice is flushed to disk before it is dismissed (its <c>world.save</c> runs in
        /// <c>PopupAutosaveDialog.Update()</c>, which never ticks in this same-job create+destroy — see
        /// <c>GenericButtonHandler.FlushPendingAutosave</c>), so a forced batch still autosaves every 15 turns.
        ///
        /// Returns <c>{count, dismissed:[kind…], items:[{turn,kind,popupType?,title?}], remaining?, cappedOut?}</c>.
        /// <c>items</c> names each dismissal so end_turn's digest can report WHAT was cleared, not just how
        /// many; <c>count</c>/<c>dismissed</c> keep their original shape.
        ///
        /// Runs on the main thread (called from within end_turn's dispatcher job). Death popups queue on
        /// the immediate <c>blockerQueue</c>, which <c>removeBlocker</c> promotes synchronously; we also
        /// pump <c>checkBlockerQueue</c> when the blocker clears, to drain the delayed queue in-job.
        /// </summary>
        public static JsonValue AutoDismissInformational(GameContext ctx, int cap = 25)
        {
            var dismissed = new List<string>();
            JsonValue items = JsonValue.NewArray();
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
                // Identify the popup before Resolve destroys the blocker. Every dismissal is named in the
                // returned `items` (end_turn's digest) — a bare count is what let a razing and a lost
                // battle vanish into "3 popups dismissed". Only the loggable kinds also reach the
                // recent-events feed, keeping that feed disjoint from the turn snapshot (see LoggableKinds).
                string logTitle, popupType, logBody;
                DescribeForLog(ctx, h, blocker, out logTitle, out popupType, out logBody);
                ToolResult r = h.Resolve(ctx, blocker, ForceArgs);
                if (r == null || r.IsError)
                {
                    // Dismiss did not take (e.g. a button no-op) — stop rather than spin.
                    // The handler already verifies the blocker actually cleared before returning Ok.
                    remaining = type;
                    break;
                }
                dismissed.Add(type);
                // PopupMsgUnified is skipped: Map.addUnifiedMessage appends every one of them to
                // turnUnifiedMessages BEFORE popping it, so the digest's `events` (built from that same
                // stream) already carries its title, body and type. Listing it here too would duplicate.
                if (popupType != "PopupMsgUnified")
                {
                    JsonValue it = JsonValue.NewObject().Set("turn", TurnOf(ctx)).Set("kind", type);
                    if (!string.IsNullOrEmpty(popupType) && popupType != type) it.Set("popupType", popupType);
                    if (!string.IsNullOrEmpty(logTitle)) it.Set("title", logTitle);
                    // Seal breaks and the god's awakening (both PopupMsgSeal) are the game's biggest beats
                    // and their body (which powers unlocked, cap changes) exists nowhere else once dismissed
                    // — keep it in the digest instead of flattening it to a bare title.
                    if (popupType == "PopupMsgSeal" && !string.IsNullOrEmpty(logBody))
                        it.Set("detail", logBody.Length > 400 ? logBody.Substring(0, 400) + "…" : logBody);
                    items.Add(it);
                }
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
            if (items.Count > 0) o.Set("items", items);
            if (remaining != null) o.Set("remaining", remaining);
            if (cappedOut) o.Set("cappedOut", true);
            return o;
        }

        /// <summary>Promote any popup sitting in the delayed blocker queue into <c>ui.blocker</c>.
        /// A decision that opens a follow-up popup (e.g. a level-up chaining into the next) leaves it
        /// queued, not yet the live blocker; end_turn calls this before deciding the turn is stuck so a
        /// freshly-queued decision is surfaced instead of being mis-reported as an unknown guard.
        /// Observer-mode invariant: every call site (end_turn's paths, PopupEventHandler's resolve
        /// paths) sits behind a tool that refuses while observer mode is on, so the mod never
        /// promotes popups out from under a human player — no gating needed here.</summary>
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
