using ShadowsMcp.Core.Json;

namespace ShadowsMcp
{
    /// <summary>
    /// Shown-once machinery for the fixed how-to texts that ride tool responses (decision notes,
    /// resolve hints, the orders legend). A 530-turn playtest showed these identical strings made up
    /// a sizeable share of the total payload: the trading-carousel note alone re-shipped ~20 times.
    ///
    /// Policy (per key, per game — counters live in <see cref="GameContext.BoilerplateCounts"/>):
    ///   1. first emission → full text;
    ///   2. afterwards → a compressed-but-complete brief form (or omission where the surrounding
    ///      response already carries the action path);
    ///   3. every <see cref="ReemitEvery"/>th occurrence → full text again, as a safety net for a
    ///      client whose context was compacted mid-session (the transcript summary may have dropped
    ///      the original text without the server ever seeing a signal);
    ///   4. an MCP <c>initialize</c> (the one protocol-level "my context is fresh" signal — see
    ///      <see cref="GameContext.RequestBoilerplateReset"/>) resets everything to full.
    ///
    /// The dedup swaps text only on an EXACT match against the constants below, so handler error
    /// notes (e.g. <c>Undescribable</c>'s "could not be read" note) are never stripped. Handlers
    /// reference these constants directly, keeping the match guaranteed by construction.
    /// All methods run on the main thread (tool execution is single-flighted by the dispatcher).
    /// </summary>
    public static class Boilerplate
    {
        /// <summary>Every Nth suppressed emission re-ships the full text (see class remarks).</summary>
        private const int ReemitEvery = 10;

        // ---------- canonical full texts (single source of truth; handlers reference these) ----------

        public const string NoteItemTrading =
            "Items move by carousel, not free drag: to give one of side A's items to side B, " +
            "rotate side A until that item is on top (item[0], marked \"top\"), then use the 'swap the " +
            "top item of each side' option. 'Take all' pulls every item + gold from side B to side A. " +
            "The composite options do a whole exchange in one click and then close the window. " +
            "Resolve with resolve_decision optionIndex; force=true just finishes/closes (Done).";

        public const string RwItemTrading =
            "resolve_decision with optionIndex, or force=true to finish/close";

        public const string NoteIdleAgents =
            "These agents have no order and will waste this turn. Give them orders with " +
            "move_unit / perform_challenge / use_power (then they leave this list), or resolve_decision " +
            "with optionIndex 0 to pass them all. force will NOT pass them: like combat, the idle alert " +
            "blocks even under force (end_turn passIdleAgents:true is the explicit multi-turn escape).";

        public const string RwIdleAgents =
            "resolve_decision with optionIndex 0 (order them instead if you can — force does not pass idle)";

        public const string NoteCombat =
            "A multi-round duel. 'Fight to the end' resolves the whole battle in one call " +
            "(pick it when your dangerEstimate beats theirs); 'Step one exchange' advances a single round " +
            "so you can watch the odds and then flee. Flee/Retreat unlock from round 2 — round 2 is Flee " +
            "(you lose ALL your minions), round 3+ is a safe Retreat. 'Flee as soon as possible' " +
            "auto-steps until fleeing is legal, then flees (at round 2 that costs ALL your minions) — " +
            "the one-call escape for an outmatched agent. Winning opens a 'Loot the Fallen Foe' trade " +
            "next. force=true fights to the end.";

        public const string RwCombat =
            "resolve_decision with optionIndex, or force=true to fight to the end";

        public const string RwEvent =
            "pick an option by index: end_turn with resolveOptionIndex " +
            "(or resolve_decision with optionIndex); force=true takes the first available choice";

        public const string RwChallengeComplete =
            "resolve_decision with optionIndex (2 = repeat, when enabled); force=true just dismisses";

        public const string NoteCarousel =
            "These options are the REAL list entries, not carousel arrows: resolve_decision " +
            "optionIndex picks one directly (no need to scroll). \"selected\" marks the entry the " +
            "game currently highlights - it is just the starting position, NOT a recommendation. " +
            "force=true cancels and FORFEITS the choice (e.g. a completed Cause Scandal ritual then " +
            "picks nobody), so prefer an optionIndex.";

        public const string RwCarousel =
            "resolve_decision with optionIndex, or force=true to cancel (forfeits the choice)";

        private const string ResolveHintFull =
            "answer via end_turn resolveOptionIndex (or resolve_decision optionIndex); " +
            "force=true dismisses only pure notices.";

        private const string ResolveHintBrief =
            "answer: end_turn resolveOptionIndex (or resolve_decision optionIndex)";

        // ---------- what CompactDecision may swap (exact-match only) ----------

        private sealed class Known
        {
            public string Kind; public string Field; public string Full; public string Brief;
        }

        // Briefs are compressed complete instructions, never opaque pointers: even a client that
        // missed the full text can still act on them. A null brief means "omit the field" — used
        // only for resolveWith, whose action path the options array + resolveHint already carry.
        private static readonly Known[] KnownTexts =
        {
            new Known { Kind = "itemTrading", Field = "note", Full = NoteItemTrading,
                Brief = "carousel trade (full how-to shown earlier): rotate a side until the item is on " +
                        "top, then swap; 'Take All' pulls side B's items + gold." },
            new Known { Kind = "itemTrading", Field = "resolveWith", Full = RwItemTrading, Brief = null },
            new Known { Kind = "idleAgents", Field = "note", Full = NoteIdleAgents,
                Brief = "idle blocks even under force — pass them all with optionIndex 0, or give orders." },
            new Known { Kind = "idleAgents", Field = "resolveWith", Full = RwIdleAgents, Brief = null },
            new Known { Kind = "combat", Field = "note", Full = NoteCombat,
                Brief = "flee unlocks at round 2 (costs ALL minions), round 3+ retreats safely; " +
                        "'Flee as soon as possible' does it in one call." },
            new Known { Kind = "combat", Field = "resolveWith", Full = RwCombat, Brief = null },
            new Known { Kind = "event", Field = "resolveWith", Full = RwEvent, Brief = null },
            new Known { Kind = "challengeComplete", Field = "resolveWith", Full = RwChallengeComplete, Brief = null },
            new Known { Kind = "carousel", Field = "note", Full = NoteCarousel,
                Brief = "real list entries — pick one directly with optionIndex; force forfeits the choice." },
            new Known { Kind = "carousel", Field = "resolveWith", Full = RwCarousel, Brief = null },
        };

        /// <summary>Marker appended to a recurring event's truncated description.</summary>
        private const string RecurringEventMarker = "(recurring event; full text shown earlier)";

        // ---------- API ----------

        /// <summary>
        /// The text to emit for a fixed boilerplate <paramref name="key"/> this time around: the full
        /// text, the brief form, or null (omit — only when <paramref name="brief"/> is null).
        /// </summary>
        public static string Emit(GameContext ctx, string key, string full, string brief)
        {
            if (ctx == null) return full;
            MaybeReset(ctx);
            int count;
            ctx.BoilerplateCounts.TryGetValue(key, out count);
            ctx.BoilerplateCounts[key] = count + 1;
            return count % ReemitEvery == 0 ? full : brief;
        }

        /// <summary>The one canonical "how to answer a pending decision" hint (shown-once, key
        /// "resolveHint"). Never null: the brief form is itself a complete instruction.</summary>
        public static string ResolveHint(GameContext ctx)
        {
            return Emit(ctx, "resolveHint", ResolveHintFull, ResolveHintBrief);
        }

        /// <summary>
        /// Shrink the repeated fixed texts on a described pending decision, in place, right before it
        /// is returned to the client. Call ONLY at presentation points (get_pending_decision,
        /// game_overview, end_turn's pending echo, AttachPending, HolyOrderTools) — internal
        /// FullOrNull probes (combat/idle checks) must not touch the counters, or the "first
        /// emission = the client saw it" invariant breaks. Fields are swapped only on an exact
        /// match with the known constants, so error notes always survive intact.
        /// </summary>
        public static JsonValue CompactDecision(GameContext ctx, JsonValue described)
        {
            if (ctx == null || described == null || described.IsNull || !described["pending"].AsBool())
                return described;
            MaybeReset(ctx);

            string kind = described["kind"].AsString();
            if (kind == null) return described;

            foreach (Known k in KnownTexts)
            {
                if (k.Kind != kind) continue;
                if (described[k.Field].AsString() != k.Full) continue; // error/other text: leave alone
                string text = Emit(ctx, k.Field + ":" + kind, k.Full, k.Brief);
                if (text == null) described.Remove(k.Field);
                else described.Set(k.Field, text);
            }

            if (kind == "event") CompactRecurringEvent(ctx, described);
            return described;
        }

        /// <summary>
        /// A narrative event whose title was already rendered in full keeps only its dynamic tail —
        /// the paragraph after the last blank line, which carries the varying "X is performing
        /// challenge Y, progress N/M (+k/turn)" state — plus a marker. Options (labels, per-option
        /// descriptions, enabled flags) are never touched: they carry the mechanics of the choice.
        /// Conservative by design: a recurring event under a different title re-ships full prose.
        /// </summary>
        private static void CompactRecurringEvent(GameContext ctx, JsonValue described)
        {
            string title = described["title"].AsString();
            if (string.IsNullOrEmpty(title)) return;
            if (ctx.SeenEventTitles.Add(title)) return; // first time under this title: full prose

            string desc = described["description"].AsString();
            if (string.IsNullOrEmpty(desc) || desc.EndsWith(RecurringEventMarker)) return;

            int cut = desc.LastIndexOf("\n\n", System.StringComparison.Ordinal);
            string tail = cut >= 0 ? desc.Substring(cut + 2)
                : (desc.Length > 300 ? desc.Substring(desc.Length - 300) : desc);
            tail = tail.Trim();
            described.Set("description",
                (tail.Length > 0 ? tail + "\n" : "") + RecurringEventMarker);
        }

        /// <summary>Consume a pending client-reconnect signal: a fresh-context client re-earns every
        /// full text (boilerplate counters, event prose, and the result banner all start over).</summary>
        private static void MaybeReset(GameContext ctx)
        {
            if (!ctx.ConsumeBoilerplateReset()) return;
            ctx.BoilerplateCounts.Clear();
            ctx.SeenEventTitles.Clear();
            ctx.LastBanner = null;
        }
    }
}
