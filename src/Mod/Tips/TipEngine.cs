using System;
using System.Text;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Tips
{
    /// <summary>
    /// Pure rendering/selection over <see cref="TipCatalog"/>. Three delivery channels use it:
    /// the initialize.instructions primer (<see cref="BuildInstructions"/>), the contextual one-shot tips
    /// on game_overview / end_turn (<see cref="CollectContextual"/>), and the get_tips query tool
    /// (<see cref="Index"/> / <see cref="ById"/> / <see cref="ByCategory"/>).
    /// </summary>
    public static class TipEngine
    {
        /// <summary>Cap on new contextual tips per tool call, so a turn where several triggers become true at
        /// once doesn't flood the response; the rest surface on later turns (mirrors the game's one-at-a-time
        /// hint cadence). One-shot tracking lives in <see cref="GameContext.ShownTips"/>.</summary>
        private const int MaxTipsPerCall = 3;

        /// <summary>The always-on core primer for MCP initialize.instructions: the premise plus every Core tip.
        /// Static text only (Core tips never interpolate live params), so it is built once at boot.</summary>
        public static string BuildInstructions()
        {
            var sb = new StringBuilder();
            sb.Append("MCP server embedded in the game Shadows of Forbidden Gods. ")
              .Append("Query tools read the live game state; action tools command the player's agents. ")
              .Append("Entity ids (L*, U*, P*, SG*, C*) are stable only within the current game session - ")
              .Append("re-query after loading a save. ")
              .Append("Situational tips surface automatically in game_overview / end_turn under 'tips' when they ")
              .Append("become relevant; call get_tips for the full mechanics reference (get_tips with an id or category for detail).")
              .Append("\n\nHOW THE GAME WORKS\n");
            foreach (TipDef t in TipCatalog.All)
            {
                if (!t.Core) continue;
                sb.Append("- ").Append(t.Title).Append(": ").Append(SafeBody(t, null)).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Newly-triggered contextual tips (fires each not-yet-shown tip whose Trigger is true now,
        /// up to the per-call cap), marking them shown. Returns JsonValue.Null when nothing new fired, so
        /// callers can `if (!t.IsNull) o.Set("tips", t)` exactly like the threatAlert field.</summary>
        public static JsonValue CollectContextual(GameContext ctx)
        {
            if (ctx == null) return JsonValue.Null;
            JsonValue arr = null;
            int added = 0;
            foreach (TipDef t in TipCatalog.All)
            {
                if (added >= MaxTipsPerCall) break;
                if (t.Trigger == null || ctx.ShownTips.Contains(t.Id)) continue;

                bool fired;
                try { fired = t.Trigger(ctx); }
                catch { fired = false; } // a bad predicate must never break game_overview / end_turn

                if (!fired) continue;
                ctx.ShownTips.Add(t.Id);
                if (arr == null) arr = JsonValue.NewArray();
                arr.Add(JsonValue.NewObject()
                    .Set("id", t.Id)
                    .Set("title", t.Title)
                    .Set("body", SafeBody(t, ctx)));
                added++;
            }
            return arr ?? JsonValue.Null;
        }

        /// <summary>get_tips with no args: the browsable index (id + title + category + one-line summary).</summary>
        public static JsonValue Index()
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (TipDef t in TipCatalog.All)
                arr.Add(JsonValue.NewObject()
                    .Set("id", t.Id)
                    .Set("title", t.Title)
                    .Set("category", t.Category)
                    .Set("summary", t.Summary)
                    .Set("core", t.Core));
            return JsonValue.NewObject()
                .Set("tips", arr)
                .Set("hint", "call get_tips with id=<id> for one tip's full text, or category=<category> for a whole topic");
        }

        /// <summary>get_tips id=&lt;id&gt;: one tip's full text; JsonValue.Null if the id is unknown.</summary>
        public static JsonValue ById(GameContext ctx, string id)
        {
            TipDef t = TipCatalog.Find(id);
            if (t == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("id", t.Id)
                .Set("title", t.Title)
                .Set("category", t.Category)
                .Set("body", SafeBody(t, ctx));
        }

        /// <summary>get_tips category=&lt;category&gt;: every tip's full text in that topic.</summary>
        public static JsonValue ByCategory(GameContext ctx, string category)
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (TipDef t in TipCatalog.All)
                if (string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
                    arr.Add(JsonValue.NewObject()
                        .Set("id", t.Id)
                        .Set("title", t.Title)
                        .Set("body", SafeBody(t, ctx)));
            return JsonValue.NewObject().Set("category", category).Set("tips", arr);
        }

        /// <summary>Render a body, falling back to its one-line summary if the delegate throws (e.g. a
        /// param-driven body hitting an unexpected null) - never let tip text break a tool call.</summary>
        private static string SafeBody(TipDef t, GameContext ctx)
        {
            try { return t.Body(ctx); }
            catch { return t.Summary; }
        }
    }
}
