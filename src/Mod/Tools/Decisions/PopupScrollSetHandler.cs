using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The vertical selection carousel (<see cref="PopupScrollSet"/>): pick one entry from a scrolling
    /// list. Backs Cause Scandal's victim pick (<c>Rt_CauseScandal.complete</c>), Guard Ruins' minion
    /// assignment (<c>Ch_GuardRuins</c>), the like/dislike tag pickers (<c>P_ForIdleHands</c>,
    /// <c>P_DevilMakesWork</c>) and the Overmind's automatic choice — all built by
    /// <c>PrefabStore.getScrollSetText</c>.
    ///
    /// The generic button sweep only saw next / prev / select / cancel, so an agent had to commit to
    /// whatever entry happened to be highlighted without ever seeing the list — a genuinely blind pick
    /// (reported from a playthrough: "I chose my scandal victim blind"). This handler instead lists the
    /// entries themselves as the options, marks the one the game currently highlights, and picks by index
    /// directly: <c>bSelect()</c> commits <c>scrollables[index]</c>, and <c>index</c> is a plain public
    /// field, so assigning it and calling <c>bSelect()</c> IS the human click on that entry — no rotating.
    ///
    /// force=true maps to <c>bCancel()</c> (the popup's own cancel, which notifies the cancelReceiver).
    /// </summary>
    public sealed class PopupScrollSetHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupScrollSet>() != null;
        }

        public string Kind(GameObject blocker) { return "carousel"; }

        // Always a real choice (a victim, a minion, a tag): never auto-dismiss it under end_turn(force),
        // or the game silently forfeits whatever the list was for.
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            PopupScrollSet set = blocker.GetComponent<PopupScrollSet>();
            string title = TitleText(set);
            int n = Entries(set).Count;
            string what = string.IsNullOrEmpty(title) ? "a selection list" : "selection list \"" + title + "\"";
            return what + " with " + n + (n == 1 ? " entry" : " entries");
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupScrollSet set = blocker.GetComponent<PopupScrollSet>();
            List<PopupScrollable> entries = Entries(set);
            int selected = SelectedIndex(set);

            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < entries.Count; i++)
            {
                string label = LabelOf(entries[i]);
                string body = Safe(() => entries[i].getBody());
                JsonValue o = JsonValue.NewObject()
                    .Set("index", i)
                    .Set("label", label)
                    .Set("enabled", Selectable(entries[i]));
                // getBody() is the same string as the label for the plain text lists; only carry it when
                // it actually adds something (e.g. an agent box's blurb).
                if (!string.IsNullOrEmpty(body) && body != label) o.Set("description", body);
                if (i == selected) o.Set("selected", true);
                options.Add(o);
            }

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "carousel")
                .Set("popupType", "PopupScrollSet")
                .Set("title", TitleText(set))
                .Set("text", BodyText(set))
                .Set("selectedIndex", selected)
                .Set("options", options)
                .Set("note", "These options are the REAL list entries, not carousel arrows: resolve_decision " +
                    "optionIndex picks one directly (no need to scroll). \"selected\" marks the entry the " +
                    "game currently highlights - it is just the starting position, NOT a recommendation. " +
                    "force=true cancels and FORFEITS the choice (e.g. a completed Cause Scandal ritual then " +
                    "picks nobody), so prefer an optionIndex.")
                .Set("resolveWith", "resolve_decision with optionIndex, or force=true to cancel (forfeits the choice)");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupScrollSet set = blocker.GetComponent<PopupScrollSet>();
            UIMaster ui = SafeUi(ctx);
            if (set == null) return ToolResult.Error("this selection list has no state; retry get_pending_decision.");

            if (args["optionIndex"].IsNull)
            {
                if (!args["force"].AsBool())
                    return ToolResult.Error("pick an entry with optionIndex (see get_pending_decision), or " +
                        "pass force=true to cancel the selection (which forfeits it).");
                try { set.bCancel(); } catch { }
                // Unity's == treats a destroyed object as null, so blocker==null covers the last-blocker case
                // (removeBlocker nulls ui.blocker AND destroys the popup).
                bool cleared = blocker == null || ui == null || ui.blocker != blocker;
                if (!cleared) return ToolResult.Error("could not cancel the selection list.");
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true)
                    .Set("kind", "carousel")
                    .Set("cancelled", true));
            }

            List<PopupScrollable> entries = Entries(set);
            int wanted = args["optionIndex"].AsInt(-1);
            if (wanted < 0 || wanted >= entries.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                    entries.Count + (entries.Count == 1 ? " entry)." : " entries)."));
            if (!Selectable(entries[wanted]))
                return ToolResult.Error("entry " + wanted + " (" + LabelOf(entries[wanted]) +
                    ") cannot be selected - pick one whose \"enabled\" is true.");

            string label = LabelOf(entries[wanted]);
            // The popup commits scrollables[index], so pointing index at the wanted entry and clicking
            // select is exactly the human interaction (each entry also carries its own receiver index,
            // so this stays correct whatever order the list was built in).
            set.index = wanted;
            set.bSelect();

            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            bool selector = ctx.Map != null && ctx.Map.world != null && ctx.Map.world.selector != null;
            JsonValue r = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "carousel")
                .Set("chose", label)
                .Set("closed", closed);
            if (selector)
                r.Set("openedSelector", true)
                 .Set("hint", "this opened a targeting selector - cast/target via use_power or the relevant action tool");
            return ToolResult.Ok(r);
        }

        // ---------- reading popup fields ----------

        /// <summary>The live entry list, never null and never containing a null entry.</summary>
        private static List<PopupScrollable> Entries(PopupScrollSet set)
        {
            var result = new List<PopupScrollable>();
            try
            {
                if (set != null && set.scrollables != null)
                    foreach (PopupScrollable s in set.scrollables)
                        if (s != null) result.Add(s);
            }
            catch { }
            return result;
        }

        /// <summary>
        /// The entry's display name. Must come from the <c>PopupScrollable</c> interface, NOT from
        /// <c>getTextElement()</c>: the text boxes <c>getScrollSetText</c> builds
        /// (<c>UIE_SelectableText</c>) put their words in <c>body</c> and leave <c>title</c> — which is
        /// what <c>getTextElement()</c> returns — empty, so reading that yields blank labels.
        /// <c>PopupBoxText</c> is the mirror case (empty getTitle, label in getBody).
        /// </summary>
        private static string LabelOf(PopupScrollable s)
        {
            string title = Safe(() => s.getTitle());
            if (!string.IsNullOrEmpty(title)) return title.Trim();
            string body = Safe(() => s.getBody());
            return string.IsNullOrEmpty(body) ? "?" : body.Trim();
        }

        private static bool Selectable(PopupScrollable s)
        {
            try { return s.selectable(); } catch { return true; }
        }

        private static int SelectedIndex(PopupScrollSet set)
        {
            try { return set != null ? set.index : 0; } catch { return 0; }
        }

        private static string TitleText(PopupScrollSet set)
        {
            try { return set != null && set.title != null ? set.title.text : null; }
            catch { return null; }
        }

        private static string BodyText(PopupScrollSet set)
        {
            try { return set != null && set.body != null ? set.body.text : null; }
            catch { return null; }
        }

        private static string Safe(Func<string> get) { try { return get(); } catch { return null; } }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
