using System;
using System.Collections.Generic;
using Assets.Code;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Shared helpers for reading a popup's Unity <c>Button</c>s and giving each a human/agent-readable
    /// label. Used by <see cref="GenericButtonHandler"/> (the always-matching fallback) and by bespoke
    /// handlers that list the same buttons (item-trading), so labels stay consistent and, crucially,
    /// caption-less <b>icon</b> buttons get a real name from their wired <c>onClick</c> method instead of the
    /// raw Unity GameObject name (e.g. the trade carousel arrows that otherwise read "Button (Previous)").
    /// </summary>
    internal static class PopupButtons
    {
        /// <summary>Every interactable button under the popup, in Unity hierarchy order (the option index).</summary>
        internal static List<Button> Enumerate(GameObject blocker)
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

        /// <summary>Best label for a button: its caption text, else the data-widget it belongs to, else the
        /// friendly name of its Inspector-wired <c>onClick</c> method (fixes caption-less icon buttons), else
        /// the raw GameObject name. The base game's permanent "silence this message type" button is tagged
        /// with an explicit warning so an agent doesn't blind itself while trimming popups.</summary>
        internal static string LabelFor(Button b)
        {
            try
            {
                string label = BaseLabel(b);
                if (IsPermanentSilence(b))
                    label += " [WARNING: PERMANENTLY hides ALL messages of this type for the rest of the game " +
                        "(saved across reload) - prefer a plain dismiss unless you never want them]";
                return label;
            }
            catch { return "?"; }
        }

        private static string BaseLabel(Button b)
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

            // Caption-less icon button: name it from the Inspector-wired onClick target method (item-trading
            // arrows, "take all", gold-swap, dismiss). UnityEvent exposes the persistent listener's method.
            string friendly = FriendlyMethodLabel(FirstPersistentMethod(b));
            if (friendly != null) return friendly;

            return b.gameObject.name;
        }

        /// <summary>True when this button permanently silences a whole message type for the rest of the game
        /// (the base game's "No longer show message of type" option), persisted across save/load. Detected by
        /// the wired method (<c>dismissNoRepeats</c>) OR the caption text, so it is caught regardless of how
        /// the button is wired.</summary>
        internal static bool IsPermanentSilence(Button b)
        {
            if (string.Equals(FirstPersistentMethod(b), "dismissNoRepeats", StringComparison.Ordinal)) return true;
            try
            {
                string s = null;
                Text t = b.GetComponentInChildren<Text>();
                if (t != null) s = t.text;
                if (s == null)
                {
                    TMP_Text tmp = b.GetComponentInChildren<TMP_Text>();
                    if (tmp != null) s = tmp.text;
                }
                return s != null && s.IndexOf("No longer show message of type", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>The first Inspector-set (persistent) onClick target method name, or null. Code-wired
        /// listeners (AddListener) are invisible here by design - those popups already have bespoke handlers.
        /// Internal so bespoke handlers can identify a clicked button by its wired method (e.g. the
        /// item-trading handler detecting bTakeAll to check for a silent receiver-full no-op).</summary>
        internal static string FirstPersistentMethod(Button b)
        {
            try
            {
                if (b == null || b.onClick == null) return null;
                int n = b.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    string m = b.onClick.GetPersistentMethodName(i);
                    if (!string.IsNullOrEmpty(m)) return m;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Friendly labels for known wired methods. Item-trading (PopupItemTrading): "side A" is the
        /// left trader, "side B" the right - both are named in the itemTrading decision's <c>sides</c> block.
        /// Gold buttons move all/25/1 gold; A# means "to side A", B# means "to side B".</summary>
        private static string FriendlyMethodLabel(string method)
        {
            if (string.IsNullOrEmpty(method)) return null;
            switch (method)
            {
                case "dismiss": return "Done - finish and close";
                // PopupChallengeComplete (normally handled by its bespoke handler; these labels are
                // defense-in-depth for any other popup wiring the same methods):
                case "dismissGoto": return "Dismiss and pan the camera to the unit (no gameplay effect)";
                case "dismissRepeat": return "Repeat this challenge immediately";
                case "bTakeAll": return "Take ALL of side B's items (into side A's free slots) and all their gold";
                case "swapTop": return "Swap the top item of each side (moves side A's top item to side B, and B's to A)";
                case "bRotateLeft": return "Rotate side A's item carousel forward";
                case "bRotateLeftReverse": return "Rotate side A's item carousel back";
                case "bRotateRight": return "Rotate side B's item carousel forward";
                case "bRotateRightReverse": return "Rotate side B's item carousel back";
                case "bSwapGoldA1": return "Move ALL gold to side A";
                case "bSwapGoldA2": return "Move 25 gold to side A";
                case "bSwapGoldA3": return "Move 1 gold to side A";
                case "bSwapGoldB1": return "Move ALL gold to side B";
                case "bSwapGoldB2": return "Move 25 gold to side B";
                case "bSwapGoldB3": return "Move 1 gold to side B";
                default: return null;
            }
        }
    }
}
