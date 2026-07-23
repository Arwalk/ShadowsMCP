using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The "Minion Management" popup (<see cref="PopupMinionDismissal"/>): raised when recruiting a
    /// minion would put an agent over the 3-minion or command-limit cap, so some must be dismissed.
    ///
    /// The generic button sweep rendered this popup unusably (reported from a playthrough): its rows
    /// are caption-less icon buttons wired via <c>AddListener</c>, so labels degraded to raw
    /// GameObject names ("Button (Previous) (1)", "[Invalid]") with no minion identity — a blind
    /// pick on a permanent choice (<c>Minion.disband("Dismissed")</c>).
    ///
    /// This handler reads the popup's own state (<c>minions</c>/<c>keep</c>/<c>canLeave</c>) and
    /// exposes it as a toggle-then-commit flow: one option per minion (keep or dismiss it, named,
    /// with stats and command cost) plus a final "accept" option that commits via
    /// <c>bTryDismiss()</c>. Toggles call <c>bKeepTrue/bKeepFalse</c> directly — the same methods
    /// the buttons are wired to — and return the refreshed state, because each toggle changes which
    /// actions the next option list offers.
    /// </summary>
    public sealed class PopupMinionDismissalHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupMinionDismissal>() != null;
        }

        public string Kind(GameObject blocker) { return "minionDismissal"; }

        // A real, permanent choice (dismissed minions disband): never auto-dismiss under end_turn(force).
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            PopupMinionDismissal popup = blocker.GetComponent<PopupMinionDismissal>();
            string who = Safe(() => popup.ua != null ? popup.ua.getName() : null) ?? "an agent";
            return "minion management for " + who + " - over capacity, pick which minions to keep";
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupMinionDismissal popup = blocker.GetComponent<PopupMinionDismissal>();
            List<Action> actions = BuildActions(popup);

            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < actions.Count; i++)
                options.Add(actions[i].ToOption(i));

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "minionDismissal")
                .Set("popupType", "PopupMinionDismissal")
                .Set("title", Safe(() => popup.title != null ? popup.title.text : null))
                .Set("unit", Summaries.UnitRef(ctx, popup.ua))
                .Set("state", StateOf(popup))
                .Set("options", options)
                .Set("note", "Toggle-then-commit: each keep/dismiss option flips that minion and returns " +
                    "the refreshed state with a NEW option list (indices shift - re-read them). Finish " +
                    "with the accept option, enabled only while the kept set fits both limits. Dismissal " +
                    "is permanent (the minion disbands), so force=true is refused - this choice must be made.")
                .Set("resolveWith", "resolve_decision with optionIndex (toggles, then accept)");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupMinionDismissal popup = blocker.GetComponent<PopupMinionDismissal>();
            UIMaster ui = SafeUi(ctx);
            if (popup == null) return ToolResult.Error("this popup has no state; retry get_pending_decision.");

            if (args["optionIndex"].IsNull)
                return ToolResult.Error("this is a real choice (dismissed minions are gone permanently), so " +
                    "force cannot pass it. Toggle keep/dismiss options until the kept set fits the limits, " +
                    "then pick the accept option (see get_pending_decision).");

            List<Action> actions = BuildActions(popup);
            int wanted = args["optionIndex"].AsInt(-1);
            if (wanted < 0 || wanted >= actions.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                    actions.Count + " options).");

            Action act = actions[wanted];
            if (act.Kind == ActionKind.Accept)
            {
                if (!Safe(() => popup.canLeave, false))
                    return ToolResult.Error("cannot accept yet: " + LimitViolation(popup) +
                        ". Dismiss more minions first.");
                try { popup.bTryDismiss(); } catch (Exception ex) { return ToolResult.Error("accept failed: " + ex.Message); }
                // Unity's == treats a destroyed object as null, so blocker==null covers the last-blocker case.
                bool closed = blocker == null || ui == null || ui.blocker != blocker;
                if (!closed) return ToolResult.Error("accept did not close the popup - re-check get_pending_decision.");
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true)
                    .Set("kind", "minionDismissal")
                    .Set("accepted", true)
                    .Set("kept", act.KeptNames)
                    .Set("dismissed", act.DismissedNames));
            }

            try
            {
                if (act.Kind == ActionKind.Dismiss) popup.bKeepFalse(act.Slot);
                else popup.bKeepTrue(act.Slot);
            }
            catch (Exception ex) { return ToolResult.Error("toggle failed: " + ex.Message); }

            // Rebuild for the response: the toggle changed both the state and the next option list.
            List<Action> next = BuildActions(popup);
            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < next.Count; i++) options.Add(next[i].ToOption(i));
            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "minionDismissal")
                .Set("toggled", act.MinionName)
                .Set("nowKept", act.Kind == ActionKind.Keep)
                .Set("stillOpen", true)
                .Set("state", StateOf(popup))
                .Set("options", options)
                .Set("hint", "popup still open - pick from the refreshed options above (accept to commit)."));
        }

        // ---------- the deterministic action list ----------

        private enum ActionKind { Keep, Dismiss, Accept }

        private sealed class Action
        {
            public ActionKind Kind;
            public int Slot;
            public string MinionName;
            public string Label;
            public bool Enabled = true;
            public bool NewlyAcquired;
            public JsonValue KeptNames;      // accept only
            public JsonValue DismissedNames; // accept only

            public JsonValue ToOption(int index)
            {
                JsonValue o = JsonValue.NewObject()
                    .Set("index", index)
                    .Set("label", Label)
                    .Set("enabled", Enabled);
                if (NewlyAcquired) o.Set("newlyAcquired", true);
                return o;
            }
        }

        /// <summary>Slots ascending (each present minion gets its keep-or-dismiss toggle), then accept.
        /// Rebuilt from live popup fields on every call, so Describe and Resolve always agree.</summary>
        private static List<Action> BuildActions(PopupMinionDismissal popup)
        {
            var actions = new List<Action>();
            Minion[] minions = Safe(() => popup.minions, null);
            bool[] keep = Safe(() => popup.keep, null);
            JsonValue keptNames = JsonValue.NewArray();
            JsonValue dismissedNames = JsonValue.NewArray();
            if (minions != null && keep != null)
            {
                for (int i = 0; i < minions.Length && i < keep.Length; i++)
                {
                    Minion m = minions[i];
                    if (m == null) continue;
                    string name = NameOf(m);
                    string stats = " (HP " + Safe(() => m.hp, 0) + "/" + Safe(() => m.getMaxHP(), 0) +
                        ", command cost " + Safe(() => m.getCommandCost(), 0) + ")";
                    bool newly = i == 3; // populate() puts the just-recruited minion in slot 3
                    if (keep[i]) keptNames.Add(JsonValue.Of(name)); else dismissedNames.Add(JsonValue.Of(name));
                    actions.Add(new Action
                    {
                        Kind = keep[i] ? ActionKind.Dismiss : ActionKind.Keep,
                        Slot = i,
                        MinionName = name,
                        NewlyAcquired = newly,
                        Label = (keep[i] ? "Dismiss " : "Keep ") + name + stats +
                            (newly ? " [newly recruited]" : ""),
                    });
                }
            }
            actions.Add(new Action
            {
                Kind = ActionKind.Accept,
                MinionName = null,
                Label = "Accept current selection",
                Enabled = Safe(() => popup.canLeave, false),
                KeptNames = keptNames,
                DismissedNames = dismissedNames,
            });
            return actions;
        }

        private static JsonValue StateOf(PopupMinionDismissal popup)
        {
            int used = 0, kept = 0, limit = Safe(() => popup.ua != null ? popup.ua.getStatCommandLimit() : 0, 0);
            JsonValue list = JsonValue.NewArray();
            Minion[] minions = Safe(() => popup.minions, null);
            bool[] keep = Safe(() => popup.keep, null);
            if (minions != null && keep != null)
            {
                for (int i = 0; i < minions.Length && i < keep.Length; i++)
                {
                    Minion m = minions[i];
                    if (m == null) continue;
                    int cost = Safe(() => m.getCommandCost(), 0);
                    if (keep[i]) { used += cost; kept++; }
                    JsonValue o = JsonValue.NewObject()
                        .Set("name", NameOf(m))
                        .Set("keep", keep[i])
                        .Set("hp", Safe(() => m.hp, 0))
                        .Set("maxHp", Safe(() => m.getMaxHP(), 0))
                        .Set("attack", Safe(() => m.getAttack(), 0))
                        .Set("defence", Safe(() => m.getMaxDefence(), 0))
                        .Set("commandCost", cost);
                    if (i == 3) o.Set("newlyAcquired", true);
                    list.Add(o);
                }
            }
            return JsonValue.NewObject()
                .Set("commandUsed", used)
                .Set("commandLimit", limit)
                .Set("keptCount", kept)
                .Set("maxMinions", 3)
                .Set("acceptEnabled", Safe(() => popup.canLeave, false))
                .Set("minions", list);
        }

        /// <summary>Mirror of the popup's own recompute() limit checks, for a concrete error message.</summary>
        private static string LimitViolation(PopupMinionDismissal popup)
        {
            int used = 0, kept = 0, limit = Safe(() => popup.ua != null ? popup.ua.getStatCommandLimit() : 0, 0);
            Minion[] minions = Safe(() => popup.minions, null);
            bool[] keep = Safe(() => popup.keep, null);
            if (minions != null && keep != null)
                for (int i = 0; i < minions.Length && i < keep.Length; i++)
                    if (minions[i] != null && keep[i]) { used += Safe(() => minions[i].getCommandCost(), 0); kept++; }
            if (used > limit) return "kept minions cost " + used + " command, over the limit of " + limit;
            if (kept > 3) return "keeping " + kept + " minions, over the maximum of 3";
            return "the kept set exceeds a limit";
        }

        private static string NameOf(Minion m)
        {
            return Safe(() => m.getName(), null) ?? "<unnamed minion>";
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); } catch { return fallback; }
        }

        private static string Safe(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
