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
            // On a world-discard window (challenge rewards arrive this way: Person.gainItem pops the
            // earned item on the DISCARD side) the safe claim verb goes FIRST, so the reflexive
            // "pick option 0 / just close it" path collects the reward instead of destroying it
            // (G17-#4). Hoisting is keyed on the trader type + button existence, never item count,
            // so the layout cannot change between Describe and Resolve.
            bool hoist = HoistTakeAll(p, buttons);
            string takeAllLabel = "Take all and close (take ALL of side B's items + gold, then Done; stays " +
                "open with a warning instead if side A's inventory can't fit everything)";
            if (hoist)
                options.Add(JsonValue.NewObject()
                    .Set("index", 0)
                    .Set("label", takeAllLabel + " - CLAIMS the reward/items sitting on the discard side")
                    .Set("enabled", true)
                    .Set("composite", true));
            for (int i = 0; i < buttons.Count; i++)
                options.Add(JsonValue.NewObject()
                    .Set("index", hoist ? i + 1 : i)
                    .Set("label", PopupButtons.LabelFor(buttons[i]))
                    .Set("enabled", true));

            // Composite one-click verbs at FIXED synthetic indices after the real buttons (count and
            // count+1; when hoisted, Take-all sits at 0 instead and the real buttons occupy 1..count),
            // each listed only while its underlying button exists. A playtest showed nearly
            // every trade ends in exactly these sequences, at 2-4 round trips apiece.
            if (!hoist && FindByMethod(buttons, "bTakeAll") != null)
                options.Add(JsonValue.NewObject()
                    .Set("index", buttons.Count)
                    .Set("label", takeAllLabel)
                    .Set("enabled", true)
                    .Set("composite", true));
            if (FindByMethod(buttons, "swapTop") != null)
                options.Add(JsonValue.NewObject()
                    .Set("index", buttons.Count + 1)
                    .Set("label", "Swap top items and close (side A's top item goes to B, B's top to A, then Done)")
                    .Set("enabled", true)
                    .Set("composite", true));

            JsonValue described = JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "itemTrading")
                .Set("popupType", "PopupItemTrading")
                .Set("title", TitleText(p))
                .Set("sides", SidesJson(p))
                .Set("options", options)
                .Set("note", Boilerplate.NoteItemTrading)
                .Set("resolveWith", Boilerplate.RwItemTrading);
            // Limited vault access etc.: gold moves are capped per window, and "Move ALL gold" then
            // moves at most the remaining cap - state the cap up front instead of letting the label
            // over-promise (G14-#1). Items are never capped.
            JsonValue limit = GoldLimitJson(p);
            if (!limit.IsNull) described.Set("goldTransferLimit", limit);
            // Items on a "Discard Items" side are RELEASED TO THE WORLD when the window closes - for
            // the Laughing Tome that means falling asleep at a random location (G14-#9). Closing with
            // side-B items is guarded in Resolve; warn at read time too.
            string discard = DiscardWarning(p);
            if (discard != null) described.Set("warning", discard);
            return described;
        }

        /// <summary>{maxGoldToA, goldAlreadyMovedToA, goldRemainingToA, …} while this window caps gold
        /// transfers (PopupItemTrading.maxTradeA/B, e.g. Subtle Thievery's limited vault access), or
        /// Null when unlimited. All gold-move buttons AND Take All obey the cap.</summary>
        private static JsonValue GoldLimitJson(PopupItemTrading p)
        {
            try
            {
                if (p == null || (p.maxTradeA == -1 && p.maxTradeB == -1)) return JsonValue.Null;
                JsonValue lim = JsonValue.NewObject();
                if (p.maxTradeA != -1)
                {
                    int moved = (int)Math.Round(p.traderA.getGold() - p.initialGoldA);
                    lim.Set("maxGoldToA", p.maxTradeA)
                       .Set("goldAlreadyMovedToA", moved)
                       .Set("goldRemainingToA", Math.Max(0, p.maxTradeA - moved));
                }
                if (p.maxTradeB != -1)
                {
                    int moved = (int)Math.Round(p.traderB.getGold() - p.initialGoldB);
                    lim.Set("maxGoldToB", p.maxTradeB)
                       .Set("goldAlreadyMovedToB", moved)
                       .Set("goldRemainingToB", Math.Max(0, p.maxTradeB - moved));
                }
                lim.Set("note", "this window caps gold transfers (limited access): 'Move ALL gold' " +
                    "and 'Take all' move at most the remaining cap, NOT all the gold shown; a " +
                    "gold-move click with the cap used up moves nothing. Items are not capped.");
                return lim;
            }
            catch { return JsonValue.Null; }
        }

        /// <summary>True when this window should list "Take all and close" FIRST (option 0): the
        /// counterparty is the world-discard side, i.e. the window is how the game delivers earned
        /// items (challenge rewards, loot) - the safe verb gets the primacy slot (G17-#4). Keyed on
        /// trader type + button existence only, NEVER item count, so Describe and Resolve can't
        /// disagree about the layout mid-window.</summary>
        private static bool HoistTakeAll(PopupItemTrading p, List<Button> buttons)
        {
            try { return p != null && p.traderB is ItemToWorldExchange && FindByMethod(buttons, "bTakeAll") != null; }
            catch { return false; }
        }

        /// <summary>A loud warning while a world-discard side (ItemToWorldExchange, titled "Discard
        /// Items") still holds items: closing the window releases them to the world for good. Null
        /// when it does not apply.</summary>
        private static string DiscardWarning(PopupItemTrading p)
        {
            try
            {
                if (p == null || !(p.traderB is ItemToWorldExchange)) return null;
                List<string> left = ItemNames(p.traderB);
                if (left.Count == 0) return null;
                bool tome = false;
                foreach (Item it in p.traderB.getItems())
                    if (it is I_LaughingTome) tome = true;
                return "the item(s) on side B are NOT yet in your inventory - side B is a DISCARD side, " +
                    "and any item still on it when this window closes is silently LOST to you (" +
                    string.Join(", ", left.ToArray()) + "). This is how the game delivers earned items: " +
                    "a challenge reward or purchase arrives HERE, not in your inventory. Claim it with " +
                    "option 0, 'Take all and close', unless you deliberately want to discard it." +
                    (tome ? " That includes the LAUGHING TOME - dismissing this window drops it to " +
                        "fall asleep at a random location, undoing the summon. Take it (the 'Take all " +
                        "and close' option) unless you deliberately want to deploy it elsewhere." : "");
            }
            catch { return null; }
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
                // Closing a window whose discard side still holds items silently releases them to the
                // world (the Laughing Tome falls asleep at a random location - it cost a game-14 run
                // its whole engine, G14-#9). Real choice ⇒ force alone must not blow through it.
                ToolResult discardGuard = GuardDiscardClose(pd, args);
                if (discardGuard != null) return discardGuard;
                if (pd != null) { try { pd.dismiss(); } catch { } }
                else { try { if (ui != null) ui.removeBlocker(blocker); } catch { } }
                bool done = blocker == null || ui == null || ui.blocker != blocker;
                if (!done) return ToolResult.Error("could not close the trade window.");
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true).Set("kind", "itemTrading").Set("closed", true));
            }

            List<Button> buttons = PopupButtons.Enumerate(blocker);
            int wanted = args["optionIndex"].AsInt(-1);

            // Same layout rule as Describe: on a discard-side window the Take-all composite is
            // hoisted to index 0 and the real buttons occupy 1..count (G17-#4); otherwise the
            // composites live at the fixed synthetic indices right after the real buttons.
            bool hoist = HoistTakeAll(blocker.GetComponent<PopupItemTrading>(), buttons);
            if (hoist && wanted == 0)
                return ResolveComposite(ui, blocker, buttons, "bTakeAll", args);
            if ((!hoist && wanted == buttons.Count) || wanted == buttons.Count + 1)
                return ResolveComposite(ui, blocker, buttons,
                    !hoist && wanted == buttons.Count ? "bTakeAll" : "swapTop", args);
            int buttonIdx = hoist ? wanted - 1 : wanted;
            if (buttonIdx < 0 || buttonIdx >= buttons.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (" +
                    (hoist
                        ? "0 = Take all and close, the " + buttons.Count + " button option(s) are at 1.." +
                          buttons.Count + ", and Swap-top-and-close is at " + (buttons.Count + 1)
                        : "there are " + buttons.Count + " button option(s), plus the composite options " +
                          "at indices " + buttons.Count + " and " + (buttons.Count + 1)) + ").");

            string label = PopupButtons.LabelFor(buttons[buttonIdx]);
            string method = PopupButtons.FirstPersistentMethod(buttons[buttonIdx]);

            // Snapshot both sides so the result can state what actually moved. The base game's bTakeAll()
            // silently skips every item when side A has no free slot (gold still transfers), so "clicked
            // Take All" alone is NOT evidence anything happened.
            PopupItemTrading pre = blocker.GetComponent<PopupItemTrading>();
            // The Done button closes the window: same discard guard as the force path.
            if (string.Equals(method, "dismiss", StringComparison.Ordinal))
            {
                ToolResult discardGuard = GuardDiscardClose(pre, args);
                if (discardGuard != null) return discardGuard;
            }
            List<string> itemsA0 = ItemNames(pre != null ? pre.traderA : null);
            List<string> itemsB0 = ItemNames(pre != null ? pre.traderB : null);
            int goldA0 = GoldOf(pre != null ? pre.traderA : null);
            int goldB0 = GoldOf(pre != null ? pre.traderB : null);

            buttons[buttonIdx].onClick.Invoke(); // the button's own bXxx() also refreshes the popup's item slots

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
                    DiffAndWarn(o, p, itemsA0, itemsB0, goldA0, goldB0, method);
                }
            }
            return ToolResult.Ok(o);
        }

        /// <summary>One-click composite: perform the exchange (Take All / swap-top) and then Done, the
        /// sequence a playtest showed nearly every trade ends in anyway. The action button is re-found by
        /// its wired method name — never by index — so Unity button-list drift between Describe and
        /// Resolve yields a clean re-read error instead of a mis-click. A Take All that could not fit
        /// everything does NOT close: the agent gets the warning and can free a slot and take again,
        /// which silent closing would have made impossible.</summary>
        private ToolResult ResolveComposite(UIMaster ui, GameObject blocker, List<Button> buttons, string method,
            JsonValue args)
        {
            Button action = FindByMethod(buttons, method);
            if (action == null)
                return ToolResult.Error("the '" + (method == "bTakeAll" ? "Take all and close" : "Swap top items and close") +
                    "' option is not available on this trade window any more - re-read get_pending_decision.");

            PopupItemTrading pre = blocker.GetComponent<PopupItemTrading>();
            if (pre == null) return ToolResult.Error("the trade window is no longer open.");
            List<string> itemsA0 = ItemNames(pre.traderA);
            List<string> itemsB0 = ItemNames(pre.traderB);
            int goldA0 = GoldOf(pre.traderA);
            int goldB0 = GoldOf(pre.traderB);

            string label = method == "bTakeAll" ? "Take all and close" : "Swap top items and close";
            action.onClick.Invoke();

            JsonValue o = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "itemTrading")
                .Set("clicked", label)
                .Set("steps", JsonValue.NewArray().Add(method).Add("dismiss"));

            // Diff while the popup is still open (the exchange itself never closes it).
            bool exchangeClosed = blocker == null || ui == null || ui.blocker != blocker;
            if (!exchangeClosed)
            {
                PopupItemTrading p = blocker.GetComponent<PopupItemTrading>();
                if (p != null)
                {
                    DiffAndWarn(o, p, itemsA0, itemsB0, goldA0, goldB0, method);
                    if (method == "bTakeAll" && ItemNames(p.traderB).Count > 0)
                    {
                        // Partial take: keep the window open so the rest is still reachable.
                        o.Set("closed", false)
                         .Set("sides", SidesJson(p))
                         .Set("note", "not closed, so you can free a slot on side A (swap an item away) " +
                            "and Take All again to get the rest - or resolve the 'Done' option to close anyway.");
                        return ToolResult.Ok(o);
                    }
                    // Swap-top can leave items on a discard side; closing would release them (G14-#9).
                    if (DiscardWarning(p) != null && !args["confirmDiscard"].AsBool())
                    {
                        o.Set("closed", false)
                         .Set("sides", SidesJson(p))
                         .Set("warning", DiscardWarning(p) + " Left open: take them, or close with " +
                            "confirmDiscard:true to discard deliberately.");
                        return ToolResult.Ok(o);
                    }
                    try { p.dismiss(); } catch { }
                }
            }

            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            o.Set("closed", closed);
            if (closed && ui != null && ui.blocker != null)
                o.Set("nextDecision", "another decision is now pending - call get_pending_decision to see it");
            else if (!closed)
                o.Set("warning", "could not close the trade window - it is still open.");
            return ToolResult.Ok(o);
        }

        /// <summary>Attach the movement diff (items/gold per side) and the Take-All warnings to a resolve
        /// result. Shared by the single-button path and the composite verbs so the "receiver full" logic
        /// exists exactly once.</summary>
        private static void DiffAndWarn(JsonValue o, PopupItemTrading p, List<string> itemsA0,
            List<string> itemsB0, int goldA0, int goldB0, string method)
        {
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

            // A gold-move click that moved nothing used to report bare success (G14-#1): when this
            // window caps transfers, say the cap is used up rather than let silence read as progress.
            bool goldClick = method != null && method.StartsWith("bSwapGold", StringComparison.Ordinal);
            if (goldClick && goldDeltaA == 0 && goldDeltaB == 0)
            {
                JsonValue lim = GoldLimitJson(p);
                o.Set("warning", !lim.IsNull
                    ? "no gold moved - this window's gold-transfer cap (limited access) is already " +
                      "used up; see goldTransferLimit. Clicking again will not move more."
                    : "no gold moved - the source side has no gold.");
            }
            JsonValue limEcho = GoldLimitJson(p);
            if (!limEcho.IsNull) o.Set("goldTransferLimit", limEcho);
        }

        /// <summary>Refusal for closing a window whose discard side still holds items, unless the caller
        /// passed confirmDiscard:true. Null = no guard applies, proceed with the close.</summary>
        private static ToolResult GuardDiscardClose(PopupItemTrading p, JsonValue args)
        {
            if (args["confirmDiscard"].AsBool()) return null;
            string warning = DiscardWarning(p);
            if (warning == null) return null;
            return ToolResult.Error("not closed: " + warning + " To keep them, resolve option 0 ('Take all " +
                "and close'); to discard DELIBERATELY, retry this close with confirmDiscard:true.");
        }

        /// <summary>The enumerated button whose persistent onClick target is <paramref name="method"/>.</summary>
        private static Button FindByMethod(List<Button> buttons, string method)
        {
            foreach (Button b in buttons)
                if (string.Equals(PopupButtons.FirstPersistentMethod(b), method, StringComparison.Ordinal))
                    return b;
            return null;
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
