using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;
using UnityEngine.UI;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The item-trading window (<see cref="PopupItemTrading"/>) - swapping items/gold between two parties
    /// (an agent and a merchant, planting an item in a cache, …). The game moves items by DRAG, which has no
    /// button to invoke, so rather than leave the agent with unlabeled icon buttons and magic indices this
    /// bespoke handler (registered before the generic fallback):
    ///   - shows both sides' contents - name, gold, and the ordered items with the actionable TOP item marked;
    ///   - lists the control buttons with real labels (Take All / Done / rotate / gold) via
    ///     <see cref="PopupButtons"/>.
    /// To give a specific item to the other side: rotate your side until that item is on top (item[0]), then
    /// use the "swap the top item of each side" button. "Take all" pulls every item + gold from side B to A.
    /// </summary>
    public sealed class PopupItemTradingHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupItemTrading>() != null;
        }

        public string Kind(GameObject blocker) { return "itemTrading"; }

        // A trade is a real interaction (moving items/gold): never auto-dismiss it under end_turn(force).
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            PopupItemTrading p = blocker.GetComponent<PopupItemTrading>();
            string a = TraderName(p, true), b = TraderName(p, false);
            if (a != null && b != null) return "item trade between " + a + " and " + b;
            return "an item trade";
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupItemTrading p = blocker.GetComponent<PopupItemTrading>();

            List<Button> buttons = PopupButtons.Enumerate(blocker);
            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < buttons.Count; i++)
                options.Add(JsonValue.NewObject()
                    .Set("index", i)
                    .Set("label", PopupButtons.LabelFor(buttons[i]))
                    .Set("enabled", true));

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "itemTrading")
                .Set("popupType", "PopupItemTrading")
                .Set("title", TitleText(p))
                .Set("sides", SidesJson(p))
                .Set("options", options)
                .Set("note", "Items move by carousel, not free drag: to give one of side A's items to side B, " +
                    "rotate side A until that item is on top (item[0], marked \"top\"), then use the 'swap the " +
                    "top item of each side' option. 'Take all' pulls every item + gold from side B to side A. " +
                    "Resolve with resolve_decision optionIndex; force=true just finishes/closes (Done).")
                .Set("resolveWith", "resolve_decision with optionIndex, or force=true to finish/close");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            UIMaster ui = SafeUi(ctx);

            if (args["optionIndex"].IsNull)
            {
                if (!args["force"].AsBool())
                    return ToolResult.Error("pick an option with optionIndex (see get_pending_decision), or " +
                        "pass force=true to finish and close the trade.");
                PopupItemTrading pd = blocker.GetComponent<PopupItemTrading>();
                if (pd != null) { try { pd.dismiss(); } catch { } }
                else { try { if (ui != null) ui.removeBlocker(blocker); } catch { } }
                bool done = blocker == null || ui == null || ui.blocker != blocker;
                if (!done) return ToolResult.Error("could not close the trade window.");
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true).Set("kind", "itemTrading").Set("closed", true));
            }

            List<Button> buttons = PopupButtons.Enumerate(blocker);
            int wanted = args["optionIndex"].AsInt(-1);
            if (wanted < 0 || wanted >= buttons.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                    buttons.Count + (buttons.Count == 1 ? " option)." : " options)."));

            string label = PopupButtons.LabelFor(buttons[wanted]);
            string method = PopupButtons.FirstPersistentMethod(buttons[wanted]);

            // Snapshot both sides so the result can state what actually moved. The base game's bTakeAll()
            // silently skips every item when side A has no free slot (gold still transfers), so "clicked
            // Take All" alone is NOT evidence anything happened.
            PopupItemTrading pre = blocker.GetComponent<PopupItemTrading>();
            List<string> itemsA0 = ItemNames(pre != null ? pre.traderA : null);
            List<string> itemsB0 = ItemNames(pre != null ? pre.traderB : null);
            int goldA0 = GoldOf(pre != null ? pre.traderA : null);
            int goldB0 = GoldOf(pre != null ? pre.traderB : null);

            buttons[wanted].onClick.Invoke(); // the button's own bXxx() also refreshes the popup's item slots

            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            JsonValue o = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "itemTrading")
                .Set("clicked", label)
                .Set("closed", closed);
            // After a non-closing action, echo the updated sides so the agent sees the effect immediately.
            if (!closed)
            {
                PopupItemTrading p = blocker.GetComponent<PopupItemTrading>();
                if (p != null)
                {
                    o.Set("sides", SidesJson(p));

                    List<string> itemsA1 = ItemNames(p.traderA);
                    List<string> itemsB1 = ItemNames(p.traderB);
                    JsonValue movedToA = NamesJson(MultisetDiff(itemsA1, itemsA0));
                    JsonValue movedToB = NamesJson(MultisetDiff(itemsB1, itemsB0));
                    if (movedToA.Count > 0) o.Set("itemsMovedToA", movedToA);
                    if (movedToB.Count > 0) o.Set("itemsMovedToB", movedToB);
                    int goldDeltaA = GoldOf(p.traderA) - goldA0;
                    int goldDeltaB = GoldOf(p.traderB) - goldB0;
                    if (goldDeltaA != 0) o.Set("goldDeltaA", goldDeltaA);
                    if (goldDeltaB != 0) o.Set("goldDeltaB", goldDeltaB);

                    if (method == "bTakeAll")
                    {
                        int leftBehind = ItemNames(p.traderB).Count;
                        if (leftBehind > 0)
                            o.Set("warning", "receiver's inventory was full - " + leftBehind + " item(s) could " +
                                "not be taken and remain with " + (TraderName(p, false) ?? "side B") +
                                (goldDeltaA > 0 ? " (their gold still transferred)" : "") +
                                ". Free a slot on side A (swap an item away) and Take All again to get the rest.");
                        else if (movedToA.Count == 0 && goldDeltaA == 0)
                            o.Set("warning", "nothing moved - side B had no items or gold to take.");
                    }
                }
            }
            return ToolResult.Ok(o);
        }

        // ---------- reading popup fields ----------

        private static JsonValue SidesJson(PopupItemTrading p)
        {
            JsonValue sides = JsonValue.NewArray();
            sides.Add(SideJson("A", p != null ? p.traderA : null));
            sides.Add(SideJson("B", p != null ? p.traderB : null));
            return sides;
        }

        private static JsonValue SideJson(string key, ItemTradeInterface trader)
        {
            JsonValue o = JsonValue.NewObject().Set("side", key);
            if (trader == null) return o;
            try { o.Set("name", trader.getName()); } catch { }
            try { o.Set("gold", (int)trader.getGold()); } catch { }
            JsonValue items = JsonValue.NewArray();
            try
            {
                Item[] arr = trader.getItems();
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Item it = arr[i];
                        if (it == null) continue;
                        JsonValue io = JsonValue.NewObject().Set("name", Safe(() => it.getName()));
                        string d = Safe(() => it.getShortDesc());
                        if (!string.IsNullOrEmpty(d)) io.Set("desc", d);
                        if (i == 0) io.Set("top", true); // slot the swap/take buttons act on
                        items.Add(io);
                    }
            }
            catch { }
            o.Set("items", items);
            return o;
        }

        private static string TitleText(PopupItemTrading p)
        {
            try { return p != null && p.title != null ? p.title.text : null; }
            catch { return null; }
        }

        private static string TraderName(PopupItemTrading p, bool a)
        {
            try
            {
                ItemTradeInterface t = p == null ? null : (a ? p.traderA : p.traderB);
                return t != null ? t.getName() : null;
            }
            catch { return null; }
        }

        /// <summary>The non-null item names on one side, in slot order (slot order is irrelevant to the
        /// diff - the carousels rotate - so movement detection compares these as multisets).</summary>
        private static List<string> ItemNames(ItemTradeInterface trader)
        {
            var names = new List<string>();
            try
            {
                Item[] arr = trader != null ? trader.getItems() : null;
                if (arr != null)
                    foreach (Item it in arr)
                        if (it != null) names.Add(Safe(() => it.getName()) ?? "?");
            }
            catch { }
            return names;
        }

        private static int GoldOf(ItemTradeInterface trader)
        {
            try { return trader != null ? (int)trader.getGold() : 0; } catch { return 0; }
        }

        /// <summary>Multiset difference: entries of <paramref name="after"/> not accounted for in
        /// <paramref name="before"/> (duplicates respected).</summary>
        private static List<string> MultisetDiff(List<string> after, List<string> before)
        {
            var remaining = new List<string>(before);
            var gained = new List<string>();
            foreach (string name in after)
            {
                if (!remaining.Remove(name)) gained.Add(name);
            }
            return gained;
        }

        private static JsonValue NamesJson(List<string> names)
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (string n in names) arr.Add(n);
            return arr;
        }

        private static string Safe(Func<string> get) { try { return get(); } catch { return null; } }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
