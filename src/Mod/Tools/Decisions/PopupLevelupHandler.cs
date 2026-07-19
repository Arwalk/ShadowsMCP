using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// Agent level-up trait pick (<see cref="PopupAgentLevelup"/>). Options are
    /// <c>Trait.getAvailableTraits(unit)</c>; the popup's public <c>choose(Trait)</c> spends the
    /// skill point and applies the trait. force=true instead dismisses (skip; the skill point
    /// stays unspent and the popup can reopen).
    /// </summary>
    public sealed class PopupLevelupHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupAgentLevelup>() != null;
        }

        public string Kind(GameObject blocker) { return "levelUp"; }

        // The trait pick matters (and the skill point would be lost on dismiss): never auto-dismiss.
        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            PopupAgentLevelup popup = blocker.GetComponent<PopupAgentLevelup>();
            string who = UnitName(popup);
            return who == null ? "a level-up trait choice" : "level-up: choose a trait for " + who;
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupAgentLevelup popup = blocker.GetComponent<PopupAgentLevelup>();
            JsonValue o = JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "levelUp")
                .Set("popupType", "PopupAgentLevelup")
                .Set("title", "Selecting a new trait for " + (UnitName(popup) ?? "an agent"))
                .Set("unit", popup != null ? Summaries.UnitRef(ctx, popup.unit) : JsonValue.Null)
                .Set("resolveWith", "resolve_decision with optionIndex (a trait), or force=true to skip");

            JsonValue options = JsonValue.NewArray();
            foreach (Trait t in AvailableTraits(popup))
            {
                options.Add(JsonValue.NewObject()
                    .Set("index", options.Count)
                    .Set("label", Name(t))
                    .Set("description", Desc(t))
                    .Set("enabled", true));
            }
            o.Set("options", options);
            return o;
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupAgentLevelup popup = blocker.GetComponent<PopupAgentLevelup>();
            UIMaster ui = SafeUi(ctx);

            if (args["optionIndex"].IsNull)
            {
                if (args["force"].AsBool())
                {
                    popup.dismiss();
                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("resolved", true)
                        .Set("kind", "levelUp")
                        .Set("skipped", true));
                }
                return ToolResult.Error("choose a trait with optionIndex (see get_pending_decision), " +
                    "or pass force=true to skip and keep the skill point.");
            }

            List<Trait> traits = AvailableTraits(popup);
            int wanted = args["optionIndex"].AsInt(-1);
            if (wanted < 0 || wanted >= traits.Count)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (there are " +
                    traits.Count + (traits.Count == 1 ? " trait)." : " traits)."));

            Trait chosen = traits[wanted];
            string label = Name(chosen);
            popup.choose(chosen); // guards on skillPoints > 0, then receiveTrait + removeBlocker

            // `blocker == null` (Unity treats a destroyed object as == null) covers the last-blocker case:
            // removeBlocker nulls ui.blocker and destroys it, where `ui.blocker != blocker` alone would
            // wrongly read false (null != destroyed → false under Unity's operator==).
            bool resolved = blocker == null || ui == null || ui.blocker != blocker;
            if (!resolved)
                return ToolResult.Error("could not apply the trait (no skill point available?).");

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "levelUp")
                .Set("unit", popup != null ? Summaries.UnitRef(ctx, popup.unit) : JsonValue.Null)
                .Set("chose", label));
        }

        // ---------- reading popup fields ----------

        private static List<Trait> AvailableTraits(PopupAgentLevelup popup)
        {
            try
            {
                if (popup != null && popup.unit != null)
                {
                    List<Trait> traits = Trait.getAvailableTraits(popup.unit);
                    if (traits != null) return traits;
                }
            }
            catch { }
            return new List<Trait>();
        }

        private static string UnitName(PopupAgentLevelup popup)
        {
            try { return popup != null && popup.unit != null ? popup.unit.getName() : null; }
            catch { return null; }
        }

        private static string Name(Trait t)
        {
            try { return t != null ? t.getName() : null; }
            catch { return null; }
        }

        private static string Desc(Trait t)
        {
            try { return t != null ? t.getDesc() : null; }
            catch { return null; }
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
