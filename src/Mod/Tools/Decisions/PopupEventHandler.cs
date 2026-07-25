using System;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;
using UnityEngine.UI;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Narrative choice events (<see cref="PopupEvent"/>). The options are the popup's own
    /// <c>options</c> buttons, each wired (in PopupEvent.populate) to dismiss(choice, ctx) with
    /// the choice/context captured in its onClick listener. We answer by invoking that same
    /// onClick — the exact effect of a click — rather than reconstructing the EventContext.
    /// </summary>
    public sealed class PopupEventHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupEvent>() != null;
        }

        public string Kind(GameObject blocker) { return "event"; }

        // A narrative choice: the option picked changes the outcome, so never auto-dismiss.
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            string title = Title(blocker.GetComponent<PopupEvent>());
            return string.IsNullOrEmpty(title) ? "a narrative event" : "event: \"" + title + "\"";
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupEvent popup = blocker.GetComponent<PopupEvent>();
            JsonValue o = JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "event")
                .Set("popupType", "PopupEvent")
                .Set("title", Title(popup))
                .Set("description", Description(popup))
                .Set("resolveWith", Boilerplate.RwEvent);

            JsonValue options = JsonValue.NewArray();
            Button[] buttons = popup.options;
            if (buttons != null)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button b = buttons[i];
                    if (b == null || !b.gameObject.activeSelf) continue;
                    int index = options.Count;
                    options.Add(JsonValue.NewObject()
                        .Set("index", index)
                        .Set("label", ButtonLabel(b))
                        .Set("description", OptionDesc(popup, i))
                        .Set("enabled", IsEnabled(b)));
                }
            }
            o.Set("options", options);
            return o;
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupEvent popup = blocker.GetComponent<PopupEvent>();
            Button[] buttons = popup.options;

            Button target;
            bool forcedDefault = false;
            if (args["optionIndex"].IsNull)
            {
                // No explicit choice: force=true is a last-resort escape - take the first available choice
                // so the agent is never stranded on an event it can't otherwise answer.
                if (!args["force"].AsBool())
                    return ToolResult.Error("this event needs an optionIndex (see the options in " +
                        "get_pending_decision / game_overview.pendingDecision), or force=true to take the " +
                        "first available choice.");
                target = FirstEnabled(buttons);
                forcedDefault = true;
                if (target == null)
                    return ToolResult.Error("this event has no available choice to auto-pick.");
            }
            else
            {
                int wanted = args["optionIndex"].AsInt(-1);
                // Map the requested visible index back to the underlying (active) button.
                target = null;
                int visible = 0;
                if (buttons != null)
                {
                    foreach (Button b in buttons)
                    {
                        if (b == null || !b.gameObject.activeSelf) continue;
                        if (visible == wanted) { target = b; break; }
                        visible++;
                    }
                }
                if (target == null)
                    return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                        visible + (visible == 1 ? " option)." : " options)."));
                if (!IsEnabled(target))
                    return ToolResult.Error("that choice is unavailable right now (its condition isn't met).");
            }

            string label = ButtonLabel(target);
            UIMaster ui = SafeUi(ctx);

            // Clicking a valid option runs dismiss(choice, ctx) → removeBlocker: the blocker changes.
            target.onClick.Invoke();

            // `blocker == null` (Unity treats a destroyed object as == null) covers the case where this
            // was the last blocker: removeBlocker nulls ui.blocker and destroys it, and `ui.blocker !=
            // blocker` alone would wrongly read false (null != destroyed → false under Unity's operator==).
            bool resolved = blocker == null || ui == null || ui.blocker != blocker;
            if (!resolved)
                return ToolResult.Error("that choice did not resolve the event (it may be disabled).");

            JsonValue ok = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "event")
                .Set("chose", label);
            if (forcedDefault) ok.Set("forcedDefault", true);

            // A choice rolls one of its weighted outcomes (EventManager.chooseOutcome) and applies its
            // effects SILENTLY; the only disclosure channel is PopupEvent.dismiss popping the outcome's
            // description as a PopupMsg - when the outcome has one. Read that message into the result here
            // (and clear it) so the agent learns what fired in the same tool call; when there is none, say
            // so explicitly rather than reporting only the option that was clicked.
            string outcomeText = ReadAndDismissOutcomeMsg(ctx, ui);
            if (outcomeText != null)
            {
                ok.Set("outcomeText", outcomeText);
                try { ctx.Events.RecordPopup(TurnOf(ctx), "eventOutcome", FirstLine(outcomeText), "auto-read into event result"); }
                catch { }
            }
            else
            {
                ok.Set("outcome", "applied without disclosure - this choice rolled one of its weighted " +
                    "outcomes and the game showed no text for it; its effects (if any) are already applied. " +
                    "Check the relevant unit/location if you need to confirm.");
            }
            return ToolResult.Ok(ok);
        }

        /// <summary>Auto-resolve the CURRENT blocker iff it is a routine (whitelisted) narrative event —
        /// the machinery behind end_turn's <c>passRoutineEvents</c> opt-in. The curated option is found
        /// by LABEL (never index) and only clicked while enabled; any mismatch — unlisted title, missing
        /// or disabled option, not a PopupEvent — returns Null and the popup blocks normally, so a game
        /// data change degrades to the old behavior instead of a wrong click. Returns
        /// <c>{turn, title, chose, outcome?}</c> on success and records the resolution in the event log.</summary>
        internal static JsonValue TryAutoResolveRoutine(GameContext ctx)
        {
            try
            {
                DecisionRegistry.PumpQueue(ctx);
                UIMaster ui = SafeUi(ctx);
                if (ui == null || ui.blocker == null) return JsonValue.Null;
                GameObject blocker = ui.blocker;
                PopupEvent popup = blocker.GetComponent<PopupEvent>();
                if (popup == null) return JsonValue.Null;

                string title = Title(popup);
                string want = RoutineEvents.PreferredOption(title);
                if (want == null) return JsonValue.Null;

                Button target = null;
                if (popup.options != null)
                {
                    foreach (Button b in popup.options)
                    {
                        if (b == null || !b.gameObject.activeSelf) continue;
                        string lbl = ButtonLabel(b);
                        if (lbl != null && string.Equals(lbl.Trim(), want, StringComparison.OrdinalIgnoreCase))
                        {
                            target = b;
                            break;
                        }
                    }
                }
                if (target == null || !IsEnabled(target)) return JsonValue.Null;

                target.onClick.Invoke();
                if (!(blocker == null || ui.blocker != blocker)) return JsonValue.Null; // click didn't take

                // Keep the 0.8.0 recurring-event compaction consistent: this title has now been "seen"
                // even though it was never described to the client this time.
                try { ctx.SeenEventTitles.Add(title); } catch { }

                JsonValue rec = JsonValue.NewObject()
                    .Set("turn", TurnOf(ctx))
                    .Set("title", title)
                    .Set("chose", want);
                string outcomeText = ReadAndDismissOutcomeMsg(ctx, ui);
                if (outcomeText != null) rec.Set("outcome", FirstLine(outcomeText));
                try
                {
                    ctx.Events.RecordPopup(TurnOf(ctx), "event", title,
                        "auto-resolved (passRoutineEvents): " + want);
                }
                catch { }
                return rec;
            }
            catch { return JsonValue.Null; }
        }

        /// <summary>If the new live blocker is exactly a <see cref="PopupMsg"/> (the outcome-description
        /// popup), return its text and dismiss it. Any other popup type (a chained level-up, a follow-up
        /// event, …) is left pending untouched and null is returned.</summary>
        private static string ReadAndDismissOutcomeMsg(GameContext ctx, UIMaster ui)
        {
            try
            {
                DecisionRegistry.PumpQueue(ctx);
                if (ui == null || ui.blocker == null) return null;
                PopupMsg msg = ui.blocker.GetComponent<PopupMsg>();
                if (msg == null) return null;
                string text = msg.text != null ? msg.text.text : null;
                if (string.IsNullOrEmpty(text)) return null;
                msg.dismiss();
                return text;
            }
            catch { return null; }
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            string line = nl > 0 ? s.Substring(0, nl) : s;
            return line.Length > 120 ? line.Substring(0, 120) : line;
        }

        private static int TurnOf(GameContext ctx)
        {
            try { return ctx.Map != null ? ctx.Map.turn : 0; } catch { return 0; }
        }

        /// <summary>First active, condition-met choice button, or null if the event has none.</summary>
        private static Button FirstEnabled(Button[] buttons)
        {
            if (buttons == null) return null;
            foreach (Button b in buttons)
            {
                if (b == null || !b.gameObject.activeSelf) continue;
                if (IsEnabled(b)) return b;
            }
            return null;
        }

        // ---------- reading popup fields ----------

        private static string Title(PopupEvent popup)
        {
            try { return popup.title != null ? popup.title.text : null; }
            catch { return null; }
        }

        /// <summary>populate() writes the same text into descriptionH/P/N; read the first non-empty.</summary>
        private static string Description(PopupEvent popup)
        {
            string[] candidates = { Text(popup.descriptionN), Text(popup.descriptionH), Text(popup.descriptionP) };
            foreach (string s in candidates)
            {
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        private static string Text(Text t)
        {
            try { return t != null ? t.text : null; }
            catch { return null; }
        }

        private static string ButtonLabel(Button b)
        {
            try
            {
                Text label = b.GetComponentInChildren<Text>();
                return label != null ? label.text : null;
            }
            catch { return null; }
        }

        private static string OptionDesc(PopupEvent popup, int i)
        {
            try
            {
                string[] descs = popup.optDescs;
                if (descs != null && i >= 0 && i < descs.Length)
                {
                    string d = descs[i];
                    return string.IsNullOrEmpty(d) ? null : d;
                }
            }
            catch { }
            return null;
        }

        /// <summary>populate() greys out condition-failed choices to colour (0,0,0,0.5) and wires no
        /// listener; enabled choices keep their default (opaque) colour.</summary>
        private static bool IsEnabled(Button b)
        {
            try
            {
                Image img = b.GetComponent<Image>();
                if (img == null) return true;
                return img.color.a >= 0.9f;
            }
            catch { return true; }
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
