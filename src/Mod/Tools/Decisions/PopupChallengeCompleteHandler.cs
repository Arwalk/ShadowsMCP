using System;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using UnityEngine;

namespace ShadowsMcp.Tools.Decisions
{
    /// <summary>
    /// The "challenge complete" popup (<see cref="PopupChallengeComplete"/>). Without a bespoke handler
    /// the generic button lister shows 2 or 3 near-identically-labeled options whose INDICES shift:
    /// the Repeat button's active state is re-evaluated every frame (challenge still valid/unclaimed,
    /// unit idle/alive), so "always pick option 2" is unscriptable. This handler presents a FIXED
    /// three-option list (Dismiss / Dismiss-and-pan / Repeat) with an <c>enabled</c> flag on Repeat,
    /// and resolves by calling the popup's own methods - immune to button reordering.
    ///
    /// Repeat is a real gameplay choice, so the popup is NOT informational: end_turn(force) must stop
    /// here rather than silently discard the option to re-run the challenge.
    /// </summary>
    public sealed class PopupChallengeCompleteHandler : IDecisionHandler
    {
        public bool CanHandle(GameObject blocker)
        {
            return blocker != null && blocker.GetComponent<PopupChallengeComplete>() != null;
        }

        public string Kind(GameObject blocker) { return "challengeComplete"; }

        public bool IsInformational(GameObject blocker) { return false; }

        public string Headline(GameContext ctx, GameObject blocker)
        {
            PopupChallengeComplete p = blocker.GetComponent<PopupChallengeComplete>();
            string title = FirstLine(BodyText(p));
            return string.IsNullOrEmpty(title) ? "a challenge was completed" : title;
        }

        public JsonValue Describe(GameContext ctx, GameObject blocker)
        {
            PopupChallengeComplete p = blocker.GetComponent<PopupChallengeComplete>();
            bool canRepeat = RepeatAvailable(p);

            JsonValue options = JsonValue.NewArray();
            options.Add(JsonValue.NewObject()
                .Set("index", 0)
                .Set("label", "Dismiss")
                .Set("enabled", true));
            options.Add(JsonValue.NewObject()
                .Set("index", 1)
                .Set("label", "Dismiss and pan the camera to the unit (no gameplay effect)")
                .Set("enabled", true));
            JsonValue repeat = JsonValue.NewObject()
                .Set("index", 2)
                .Set("label", "Repeat this challenge immediately")
                .Set("enabled", canRepeat);
            if (!canRepeat)
                repeat.Set("why", "not repeatable right now: the challenge is gone, claimed or no longer " +
                    "valid for this unit, or the unit is busy or dead (the game re-evaluates this each frame)");
            options.Add(repeat);

            JsonValue o = JsonValue.NewObject()
                .Set("pending", true)
                .Set("kind", "challengeComplete")
                .Set("popupType", "PopupChallengeComplete")
                .Set("title", BodyText(p))
                .Set("options", options)
                .Set("resolveWith", Boilerplate.RwChallengeComplete);
            string flavour = FlavourText(p);
            if (!string.IsNullOrEmpty(flavour)) o.Set("flavour", flavour);
            try { if (p.unit != null) o.Set("unit", Summaries.UnitRef(ctx, p.unit)); } catch { }
            // Name + id of the completed challenge so the agent can re-perform it later by id even
            // after dismissing (perform_challenge handles travel/claiming itself).
            try
            {
                if (p.ch != null)
                    o.Set("challenge", JsonValue.NewObject()
                        .Set("id", Summaries.ChallengeId(ctx, p.ch))
                        .Set("name", Summaries.ChallengeName(p.ch)));
            }
            catch { }
            return o;
        }

        public ToolResult Resolve(GameContext ctx, GameObject blocker, JsonValue args)
        {
            PopupChallengeComplete p = blocker.GetComponent<PopupChallengeComplete>();
            UIMaster ui = SafeUi(ctx);

            int wanted = args["optionIndex"].AsInt(-1);
            if (args["optionIndex"].IsNull)
            {
                if (!args["force"].AsBool())
                    return ToolResult.Error("pick an option with optionIndex (0 dismiss, 1 dismiss+pan, " +
                        "2 repeat when enabled), or pass force=true to just dismiss.");
                wanted = 0;
            }
            if (wanted < 0 || wanted > 2)
                return ToolResult.Error("optionIndex " + wanted + " is out of range (0 dismiss, " +
                    "1 dismiss+pan, 2 repeat).");

            string action;
            switch (wanted)
            {
                case 1:
                    // dismissGoto() only removes the blocker when the unit is alive; a dead unit would
                    // leave the popup open. Fall back to a plain dismiss in that case.
                    bool dead; try { dead = p.unit == null || p.unit.isDead; } catch { dead = true; }
                    if (dead) { try { p.dismiss(); } catch { } action = "dismissed (unit is dead - no pan)"; }
                    else { try { p.dismissGoto(); } catch { } action = "dismissed and panned to the unit"; }
                    break;
                case 2:
                    // dismissRepeat() silently degrades to a plain dismiss when ineligible - reject
                    // instead, so a "repeat" answer is never quietly turned into a discard.
                    if (!RepeatAvailable(p))
                        return ToolResult.Error("this challenge cannot be repeated right now (gone, claimed, " +
                            "no longer valid for the unit, or the unit is busy/dead). Use optionIndex 0 to " +
                            "dismiss, then perform_challenge with the challenge id to retry later.");
                    try { p.dismissRepeat(); } catch { }
                    action = "dismissed and restarted the challenge";
                    break;
                default:
                    try { p.dismiss(); } catch { }
                    action = "dismissed";
                    break;
            }

            // Unity's destroyed-object == null covers the last-blocker case (see PopupEventHandler).
            bool closed = blocker == null || ui == null || ui.blocker != blocker;
            if (!closed) return ToolResult.Error("the completion popup did not close.");

            return ToolResult.Ok(JsonValue.NewObject()
                .Set("resolved", true)
                .Set("kind", "challengeComplete")
                .Set("action", action));
        }

        /// <summary>The game's own repeatability predicate (PopupChallengeComplete.Update /
        /// dismissRepeat), evaluated directly against live state. Reading the bRepeat button's active
        /// flag - what this used to do - served a STALE enabled:true when no Unity frame had run since
        /// the popup opened (headless resolves happen between frames), advertising a repeat the resolve
        /// then refused (G14-#22).</summary>
        private static bool RepeatAvailable(PopupChallengeComplete p)
        {
            try
            {
                UA ua = p != null ? p.unit as UA : null;
                Challenge ch = p != null ? p.ch : null;
                if (ua == null || ch == null || ua.isDead || ua.task != null) return false;
                return Summaries.SafeValid(ch) && ch.claimedBy == null && ch.validFor(ua) &&
                       ch.location != null && ch.location.GetChallenges().Contains(ch);
            }
            catch { return false; }
        }

        private static string BodyText(PopupChallengeComplete p)
        {
            try { return p != null && p.textBody != null ? p.textBody.text : null; }
            catch { return null; }
        }

        private static string FlavourText(PopupChallengeComplete p)
        {
            try { return p != null && p.textFlavour != null ? p.textFlavour.text : null; }
            catch { return null; }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int nl = s.IndexOf('\n');
            return nl > 0 ? s.Substring(0, nl) : s;
        }

        private static UIMaster SafeUi(GameContext ctx)
        {
            try { return ctx.Map != null && ctx.Map.world != null ? ctx.Map.world.ui : null; }
            catch { return null; }
        }
    }
}
