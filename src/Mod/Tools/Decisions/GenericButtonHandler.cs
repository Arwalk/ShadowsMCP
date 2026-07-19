using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Fallback that makes ANY popup readable and answerable by treating its Unity <c>Button</c>s as
    /// options. Almost every popup wires its clicks through Inspector <c>Button.onClick</c> → a
    /// <c>bXxx()</c>/<c>dismiss()</c> method, so listing the interactable buttons (with their labels)
    /// and invoking the chosen one's <c>onClick</c> drives the popup exactly like a click.
    ///
    /// Always matches (last in the registry). Popups whose core interaction isn't a button
    /// (item-trading drag, mod-config inputs, carousels, text-entry, multi-step battle) still expose
    /// their cancel/dismiss/next buttons and are flagged in the note; force=true always dismisses.
    /// </summary>
    public sealed class GenericButtonHandler : IDecisionHandler
    {
        // popupType names whose main interaction a button-sweep can't fully drive.
        private static readonly HashSet<string> HardTypes = new HashSet<string>
        {
            "PopupItemTrading", "PopupModConfig", "PopupScrollSet", "PopupXScroll",
            "PopupXBoxGodSelectMsg", "PopupBoxText", "PopupBoxMod", "PopupBoxPerson", "PopupBoxAgent",
            "PopupSaveDialog", "PopupSaveMap", "PopupMsgRenameAgent", "PopupGameOptions",
            "PopupIOOptions", "PopupBattleAgent", "PopupHolyOrder",
        };

        // Pure-notification popups: every button just closes the popup (possibly panning the camera),
        // with no gameplay branch, so force-dismissing them is lossless. Deliberately conservative —
        // popups with a real alternative action (PopupChallengeComplete's "repeat", PopupConfirmOrder's
        // confirm/abort) are left OFF so end_turn's auto-dismiss never makes a silent choice.
        private static readonly HashSet<string> InformationalTypes = new HashSet<string>
        {
            "PopupMsg", "PopupMsgHint", "PopupMsgSeal", "PopupMsgAchievement", "PopupImgMsg",
            "PopupMsgUnified", "PopupTutorialMsg", "PopupMsgAgents", "PopupMsgAgentsDeath",
            "PopupAutosaveDialog",
        };

        public bool CanHandle(GameObject blocker) { return blocker != null; }

        public string Kind(GameObject blocker) { return "popup"; }

        public bool IsInformational(GameObject blocker)
        {
            return blocker != null && InformationalTypes.Contains(PopupType(blocker));
        }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            int n = Buttons(blocker).Count;
            string title = Title(blocker);
            string what = string.IsNullOrEmpty(title) ? PopupType(blocker) : "\"" + title + "\"";
            return "popup " + what + " with " + n + (n == 1 ? " option" : " options");
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            List<Button> buttons = Buttons(blocker);
            JsonValue options = JsonValue.NewArray();
            for (int i = 0; i < buttons.Count; i++)
            {
                options.Add(JsonValue.NewObject()
                    .Set("index", i)
                    .Set("label", LabelFor(buttons[i]))
                    .Set("enabled", true));
            }

            string type = PopupType(blocker);
            string note = "Pick an option with resolve_decision optionIndex, or force=true to dismiss.";
            if (HardTypes.Contains(type))
                note = "This popup needs in-game interaction for its main action (text entry, " +
                    "drag, sliders, or a carousel); the buttons below (e.g. cancel/next/dismiss) still " +
                    "work. Use resolve_decision optionIndex, or force=true to dismiss.";

            return JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "popup")
                .Set("popupType", type)
                .Set("title", Title(blocker))
                .Set("text", BodyText(blocker))
                .Set("options", options)
                .Set("note", note)
                .Set("resolveWith", "resolve_decision with optionIndex, or force=true to dismiss");
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            UIMaster ui = SafeUi(ctx);

            if (args["optionIndex"].IsNull)
            {
                if (!args["force"].AsBool())
                    return ToolResult.Error("pick an option with optionIndex (see get_pending_decision), " +
                        "or pass force=true to dismiss this popup.");
                Dismiss(ui, blocker);
                return ToolResult.Ok(JsonValue.NewObject()
                    .Set("resolved", true)
                    .Set("kind", "popup")
                    .Set("dismissed", PopupType(blocker)));
            }

            List<Button> buttons = Buttons(blocker);
            int wanted = args["optionIndex"].AsInt(-1);
            if (wanted < 0 || wanted >= buttons.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                    buttons.Count + (buttons.Count == 1 ? " option)." : " options)."));

            string label = LabelFor(buttons[wanted]);
            buttons[wanted].onClick.Invoke();

            // `blocker == null` (Unity treats a destroyed object as == null) reports a genuine close when
            // this was the last blocker — removeBlocker nulls ui.blocker and destroys it, where `ui.blocker
            // != blocker` alone would wrongly read false (null != destroyed → false under Unity's operator==).
            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            bool selector = ctx.Map != null && ctx.Map.world != null && ctx.Map.world.selector != null;
            JsonValue o = JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "popup")
                .Set("clicked", label)
                .Set("closed", closed)
                .Set("stillOpen", !closed);
            if (selector)
                o.Set("openedSelector", true)
                 .Set("hint", "this opened a targeting selector - cast/target via use_power or the relevant action tool");
            return ToolResult.Ok(o);
        }

        // ---------- button enumeration & labels ----------

        private static List<Button> Buttons(GameObject blocker)
        {
            var result = new List<Button>();
            try
            {
                foreach (Button b in blocker.GetComponentsInChildren<Button>(false))
                {
                    if (b != null && b.IsActive() && b.IsInteractable()) result.Add(b);
                }
            }
            catch { }
            return result;
        }

        private static string LabelFor(Button b)
        {
            try
            {
                Text t = b.GetComponentInChildren<Text>();
                if (t != null && !string.IsNullOrEmpty(t.text)) return t.text.Trim();
                TMP_Text tmp = b.GetComponentInChildren<TMP_Text>();
                if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text.Trim();

                // Data-object widgets carry the meaningful name.
                UIE_Trait tr = b.GetComponentInParent<UIE_Trait>();
                if (tr != null && tr.trait != null) return tr.trait.getName();
                UIE_GodPower gp = b.GetComponentInParent<UIE_GodPower>();
                if (gp != null && gp.power != null) return gp.power.getName();
                UIE_AgentSelect ag = b.GetComponentInParent<UIE_AgentSelect>();
                if (ag != null && ag.abstraction != null) return ag.abstraction.getName();

                return b.gameObject.name;
            }
            catch { return "?"; }
        }

        // ---------- popup text ----------

        private static string PopupType(GameObject blocker)
        {
            try
            {
                foreach (MonoBehaviour mb in blocker.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string n = mb.GetType().Name;
                    if (n.StartsWith("Popup")) return n;
                }
                return blocker.name;
            }
            catch { return "unknown"; }
        }

        /// <summary>Short heading = the first non-empty text that isn't inside a button.</summary>
        private static string Title(GameObject blocker) { return FirstText(blocker, shortest: true); }

        /// <summary>Body = the longest non-button text (the message/flavour).</summary>
        private static string BodyText(GameObject blocker) { return FirstText(blocker, shortest: false); }

        private static string FirstText(GameObject blocker, bool shortest)
        {
            string best = null;
            try
            {
                foreach (Text t in blocker.GetComponentsInChildren<Text>(false))
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (t.GetComponentInParent<Button>() != null) continue; // skip button captions
                    string s = t.text.Trim();
                    if (s.Length == 0) continue;
                    if (best == null || (shortest ? s.Length < best.Length : s.Length > best.Length)) best = s;
                }
                foreach (TMP_Text t in blocker.GetComponentsInChildren<TMP_Text>(false))
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (t.GetComponentInParent<Button>() != null) continue;
                    string s = t.text.Trim();
                    if (s.Length == 0) continue;
                    if (best == null || (shortest ? s.Length < best.Length : s.Length > best.Length)) best = s;
                }
            }
            catch { }
            return best;
        }

        // ---------- dismissal ----------

        private static void Dismiss(UIMaster ui, GameObject blocker)
        {
            try
            {
                UI_Dismissable d = blocker.GetComponent<UI_Dismissable>();
                if (d != null) { d.dismissKeyHit(); return; }
            }
            catch { }
            try { if (ui != null) ui.removeBlocker(blocker); }
            catch { }
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
