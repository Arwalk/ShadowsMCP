using System.Diagnostics;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tools.Decisions;

namespace ShadowsMcp.Tools
{
    /// <summary>Central refusal for game-mutating tools while observer mode is on (see
    /// GameToolHost.RegisterMutating — declared at registration, enforced in Execute).</summary>
    internal static class ObserverGuard
    {
        internal static ToolResult Refuse(string toolName)
        {
            return ToolResult.Error(
                "observer mode is on - a human is playing this game and '" + toolName + "' would act on " +
                "it. Nothing was changed. You are a companion: narrate and advise using read-only tools " +
                "(game_overview, queries, get_pending_decision, inspect) and follow events with " +
                "wait_for_events. Do not retry; the human can turn 'Observer mode' off in the mod config " +
                "to hand control back.");
        }
    }

    /// <summary>
    /// Observer mode's push channel: the wait_for_events long-poll. The transport stays plain
    /// Streamable HTTP (POST /mcp, no SSE); push-like latency comes from the handler blocking on
    /// <see cref="ObserverEventBuffer"/>'s wait handle on the HTTP worker thread — never on the
    /// Unity main thread, and never inside the single-flight lock (see RegisterConcurrent).
    /// </summary>
    public static class ObserverTools
    {
        private const int DefaultTimeoutS = 25;
        private const int MinTimeoutS = 1;
        // Must stay below typical MCP client tool-call kill timers (Claude Code kills long calls);
        // the tool description tells clients to allow ~70s (55s wait + one 10s state hop) worst case.
        private const int MaxTimeoutS = 55;
        private const int DefaultMaxEvents = 50;
        private const int MaxMaxEvents = 200;

        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.RegisterConcurrent(new ToolDefinition(
                "wait_for_events",
                "Observer mode's event feed: long-poll for game events while a HUMAN plays (turn " +
                "news, popups opening and being resolved, turn boundaries). Returns immediately when " +
                "events newer than your cursor exist; otherwise blocks until one arrives or " +
                "timeout_seconds elapses, then returns an empty batch (NOT an error - just call again " +
                "with the same cursor). Cursor contract: pass 0 to start, then always the last " +
                "next_cursor you received; replaying a cursor re-returns the same events. gap:true " +
                "means events were trimmed (or a new game / save load cleared the buffer - flagged by " +
                "a game_changed event) before you read them: resume from next_cursor. Each response " +
                "also carries the current turn and any decision the player is looking at " +
                "(pendingDecision - the PLAYER resolves it on screen, never you). If observer mode is " +
                "off this returns {observer_mode:false} immediately without blocking - do not poll " +
                "then. Only useful in observer mode; during headless play use end_turn's digest. " +
                "Allow ~70s client-side timeout at the maximum timeout_seconds.",
                Schema.Object(
                    Schema.Prop("cursor", Schema.Integer(
                        "0 = from the beginning of the buffer; otherwise the last next_cursor received."),
                        required: true),
                    Schema.Prop("timeout_seconds", Schema.Integer(
                        "How long to block when no events are pending (default 25, clamped 1-55).")),
                    Schema.Prop("max_events", Schema.Integer(
                        "Max events per batch (default 50, clamped 1-200); the rest wait for your next call."))),
                a => WaitForEvents(ctx, a)));
        }

        /// <summary>Runs on the HTTP worker thread (RegisterConcurrent). The wait blocks HERE; the
        /// only main-thread hop is a short post-wait state read, and its failure degrades to
        /// state_unavailable rather than failing the poll.</summary>
        private static ToolResult WaitForEvents(GameContext ctx, JsonValue a)
        {
            long cursor = a["cursor"].AsLong(-1);
            if (cursor < 0)
                return ToolResult.Error("cursor must be >= 0 (0 = from the beginning; otherwise pass " +
                    "the last next_cursor you received).");
            int timeoutS = Clamp((int)a["timeout_seconds"].AsLong(DefaultTimeoutS), MinTimeoutS, MaxTimeoutS);
            int maxEvents = Clamp((int)a["max_events"].AsLong(DefaultMaxEvents), 1, MaxMaxEvents);

            if (!ctx.Config.ObserverMode) return OffModeResult();

            Stopwatch sw = Stopwatch.StartNew();
            bool gap;
            long next;
            JsonValue events = ctx.ObserverEvents.ReadSince(cursor, maxEvents, out gap, out next);
            if (events.Count == 0 && !gap)
            {
                ctx.ObserverEvents.WaitForNew(cursor, timeoutS * 1000);
                // Toggling observer mode off pulses the waiters — re-check so a mid-poll disable
                // returns the off-mode answer promptly instead of an empty batch.
                if (!ctx.Config.ObserverMode) return OffModeResult();
                events = ctx.ObserverEvents.ReadSince(cursor, maxEvents, out gap, out next);
            }
            sw.Stop();

            JsonValue o = JsonValue.NewObject()
                .Set("observer_mode", true)
                .Set("events", events)
                .Set("next_cursor", next)
                .Set("waited_ms", sw.ElapsedMilliseconds);
            if (gap)
                o.Set("gap", true);

            // Ride-along state (turn, what the player is looking at, game over): needs the main
            // thread, so hop briefly — AFTER the wait. If the game is mid-turn-processing the hop
            // can time out; deliver the events anyway rather than failing the poll.
            ToolResult stamped = ctx.Dispatcher.Run(() =>
            {
                AttachState(ctx, o);
                return ToolResult.Ok(o);
            }, ctx.Config.ToolTimeoutMs);
            if (stamped != null && !stamped.IsError) return stamped;
            o.Set("state_unavailable", true);
            return ToolResult.Ok(o);
        }

        /// <summary>Main thread only. Failures leave the payload without state rather than throwing.</summary>
        private static void AttachState(GameContext ctx, JsonValue o)
        {
            try
            {
                Map map = ctx.Map;
                if (map == null)
                {
                    o.Set("note", "no game is loaded (main menu)");
                    return;
                }
                o.Set("turn", map.turn);
                JsonValue pd = DecisionRegistry.Compact(ctx);
                if (!pd.IsNull)
                {
                    // The registry's hint says "call resolve_decision" — wrong for a companion.
                    pd.Set("hint", "the player resolves this on screen - do not call resolve_decision; " +
                        "you may discuss the options");
                    o.Set("pendingDecision", pd);
                }
                Overmind om = map.overmind;
                if (om != null && om.endOfGameAchieved)
                    o.Set("gameOver", true).Set("victoryAchieved", om.victoryAchieved);
            }
            catch { }
        }

        private static ToolResult OffModeResult()
        {
            return ToolResult.Ok(JsonValue.NewObject()
                .Set("observer_mode", false)
                .Set("message", "observer mode is off - no events will ever arrive here, so this " +
                    "returned immediately. A human enables it in-game: Mods -> Mod Options -> " +
                    "Shadows MCP Server -> 'Observer mode'. Do not poll this tool in a loop while " +
                    "it is off."));
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
