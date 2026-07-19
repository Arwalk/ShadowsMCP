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
                .Set("resolveWith", "pick an option by index: end_turn with resolveOptionIndex " +
                    "(or resolve_decision with optionIndex); force=true takes the first available choice");

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
            return ToolResult.Ok(ok);
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
