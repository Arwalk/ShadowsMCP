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

            // person1 is the event's acting person (EventContext.person, captured in
            // PopupEvent.populate); vanilla evaluates option conditions — the bracketed
            // "[Requires: N Gold]" gates — against THIS person's resources, which repeated
            // playtests could not tell (games 12/13 #6). Absent for god-level events with no
            // acting person. The location also fixes recurring-event summaries that reference
            // "this location" with no way to identify it (game 13 #12): both fields survive
            // Boilerplate.CompactRecurringEvent, which only rewrites the description.
            try
            {
                Person actor = popup.person1;
                if (actor != null)
                {
                    o.Set("actor", JsonValue.NewObject()
                        .Set("person", Summaries.PersonRef(actor))
                        .Set("gold", actor.gold)
                        .Set("note", "bracketed option requirements (e.g. [Requires: N Gold]) are " +
                            "checked against this person's resources"));
                    Location loc = actor.getLocation();
                    if (loc != null)
                    {
                        o.Set("location", Summaries.LocationRef(loc));
                        // The current charges the option deltas apply to. A GUI player sees the
                        // devastation/unrest bars behind the popup; a JSON-only player chose "+100
                        // Devastation" twice with no way to see the city was near its 300 destruction
                        // cap, and lost the settlement, its ruler and a well (G14-#23).
                        JsonValue mods = LocationModifiers(loc);
                        if (!mods.IsNull)
                            o.Set("locationModifiers", mods)
                             .Set("locationModifiersNote", "current modifier charges at the event's " +
                                "location - weigh option deltas against these (Devastation destroys " +
                                "the settlement outright at 300).");
                    }
                }
            }
            catch { }

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

            // Snapshot the observable numbers an outcome most often moves (actor heat/gold, location
            // modifiers) BEFORE the click: two thirds of event choices apply their effects with no
            // outcome text at all, and each used to cost a confirming query (G14-#8).
            Person snapActor = null;
            Location snapLoc = null;
            double menace0 = 0, profile0 = 0;
            int gold0 = 0;
            var charges0 = new System.Collections.Generic.Dictionary<string, double>();
            try
            {
                snapActor = popup.person1;
                if (snapActor != null)
                {
                    gold0 = snapActor.gold;
                    if (snapActor.unit != null) { menace0 = snapActor.unit.menace; profile0 = snapActor.unit.profile; }
                    snapLoc = snapActor.getLocation();
                    if (snapLoc != null && snapLoc.properties != null)
                        foreach (Property pr in snapLoc.properties)
                            if (pr != null) charges0[pr.GetType().Name] = pr.charge;
                }
            }
            catch { }

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
            // effects SILENTLY; disclosure arrives as a chain of queued PopupMsgs (the outcome's
            // description and anything its effects popped). Read the whole chain into the result here;
            // a non-PopupMsg follow-up is a real decision, left pending and pointed at instead.
            bool followUp;
            string outcomeText = ReadAndDismissOutcomeMsgs(ctx, ui, out followUp);
            if (outcomeText != null)
            {
                ok.Set("outcomeText", outcomeText);
                try { ctx.Events.RecordPopup(TurnOf(ctx), "eventOutcome", FirstLine(outcomeText), "auto-read into event result"); }
                catch { }
            }
            if (followUp)
                ok.Set("followUp", "a further popup chained from this outcome and is now the pending " +
                    "decision - call get_pending_decision to see and resolve it" +
                    (outcomeText == null ? "; the outcome details are likely in it" : ""));
            else if (outcomeText == null)
                ok.Set("outcome", "applied without an outcome message - this choice rolled one of its " +
                    "weighted outcomes and the game queued no text for it; its effects (if any) are " +
                    "already applied. observedChanges below diffs the actor/location for you; anything " +
                    "it does not cover, check the relevant unit/location.");
            // Textless outcome: diff the snapshot so the numeric effect is IN the result instead of
            // costing a confirming get_unit/get_location round-trip.
            if (outcomeText == null)
            {
                JsonValue changes = ObservedChanges(snapActor, snapLoc, menace0, profile0, gold0, charges0);
                if (!changes.IsNull) ok.Set("observedChanges", changes);
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
                string outcomeText = ReadAndDismissOutcomeMsgs(ctx, ui, out _);
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

        /// <summary>Drain every consecutive <see cref="PopupMsg"/> blocker (the outcome description
        /// plus any notices its effects queued behind it), concatenating their texts. An outcome's
        /// effects can pop messages DURING chooseOutcome, so the disclosure is often a chain, not one
        /// popup — reading only the first made resolve_decision claim "no disclosure" for text that
        /// arrived one blocker later (game 13 #4). Stops at the first non-PopupMsg blocker: that is a
        /// real decision (a chained event, a level-up…), is never dismissed here, and is reported via
        /// <paramref name="followUpPending"/>. A textless PopupMsg is dismissed too (it carries no
        /// information; leaving it pending stranded the queue). Capped defensively at 8 popups.</summary>
        private static string ReadAndDismissOutcomeMsgs(GameContext ctx, UIMaster ui, out bool followUpPending)
        {
            followUpPending = false;
            string acc = null;
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    DecisionRegistry.PumpQueue(ctx);
                    if (ui == null || ui.blocker == null) return acc;
                    PopupMsg msg = ui.blocker.GetComponent<PopupMsg>();
                    if (msg == null) { followUpPending = true; return acc; }
                    string text = msg.text != null ? msg.text.text : null;
                    msg.dismiss();
                    if (!string.IsNullOrEmpty(text))
                        acc = acc == null ? text : acc + "\n\n" + text;
                }
                // Cap hit with popups possibly still queued — whatever remains is still pending.
                DecisionRegistry.PumpQueue(ctx);
                if (ui != null && ui.blocker != null) followUpPending = true;
            }
            catch { }
            return acc;
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

        /// <summary>The location's current property charges ({type, name, charge}), capped at 10
        /// entries. Null when there are none.</summary>
        private static JsonValue LocationModifiers(Location loc)
        {
            try
            {
                if (loc == null || loc.properties == null || loc.properties.Count == 0) return JsonValue.Null;
                JsonValue arr = JsonValue.NewArray();
                foreach (Property pr in loc.properties)
                {
                    if (pr == null) continue;
                    string name; try { name = pr.getName(); } catch { name = pr.GetType().Name; }
                    JsonValue e = JsonValue.NewObject()
                        .Set("type", pr.GetType().Name)
                        .Set("name", name)
                        .Set("charge", Math.Round(pr.charge, 1));
                    if (pr is Pr_Devastation) e.Set("max", 300).Set("atMax", "settlement destroyed");
                    arr.Add(e);
                    if (arr.Count >= 10) break;
                }
                return arr.Count > 0 ? arr : JsonValue.Null;
            }
            catch { return JsonValue.Null; }
        }

        /// <summary>Diff the pre-click snapshot against the live actor/location: gold, menace, profile
        /// and each location modifier whose charge moved. Null when nothing observable changed (the
        /// outcome may still have acted elsewhere - relationships, other people).</summary>
        private static JsonValue ObservedChanges(Person actor, Location loc, double menace0,
            double profile0, int gold0, System.Collections.Generic.Dictionary<string, double> charges0)
        {
            try
            {
                if (actor == null) return JsonValue.Null;
                JsonValue o = JsonValue.NewObject();
                bool any = false;
                if (actor.gold != gold0) { o.Set("actorGoldDelta", actor.gold - gold0); any = true; }
                if (actor.unit != null)
                {
                    double dm = Math.Round(actor.unit.menace - menace0, 1);
                    double dp = Math.Round(actor.unit.profile - profile0, 1);
                    if (dm != 0) { o.Set("actorMenaceDelta", dm); any = true; }
                    if (dp != 0) { o.Set("actorProfileDelta", dp); any = true; }
                }
                if (loc != null && loc.properties != null)
                {
                    JsonValue mods = JsonValue.NewArray();
                    var seen = new HashSet<string>();
                    foreach (Property pr in loc.properties)
                    {
                        if (pr == null) continue;
                        string key = pr.GetType().Name;
                        seen.Add(key);
                        double before;
                        double delta = charges0.TryGetValue(key, out before)
                            ? Math.Round(pr.charge - before, 1)
                            : Math.Round(pr.charge, 1); // property newly added by the outcome
                        if (delta == 0) continue;
                        string name; try { name = pr.getName(); } catch { name = key; }
                        mods.Add(JsonValue.NewObject()
                            .Set("name", name)
                            .Set("chargeDelta", delta)
                            .Set("chargeNow", Math.Round(pr.charge, 1)));
                    }
                    foreach (var kv in charges0)
                        if (!seen.Contains(kv.Key) && kv.Value != 0)
                            mods.Add(JsonValue.NewObject()
                                .Set("name", kv.Key)
                                .Set("chargeDelta", Math.Round(-kv.Value, 1))
                                .Set("chargeNow", 0)
                                .Set("removed", true));
                    if (mods.Count > 0) { o.Set("locationModifierDeltas", mods); any = true; }
                }
                if (!any) return JsonValue.Null;
                o.Set("note", "diff of the acting person and their location, before vs after this " +
                    "choice - the outcome's directly observable numeric effects.");
                return o;
            }
            catch { return JsonValue.Null; }
        }
    }
}
