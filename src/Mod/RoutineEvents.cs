using System;
using System.Collections.Generic;

namespace ShadowsMcp
{
    /// <summary>
    /// The curated whitelist behind <c>end_turn</c>'s <c>passRoutineEvents</c> opt-in. A 530-turn
    /// playtest spent hundreds of round-trips on the same three low-stakes mid-challenge popups, each
    /// halting a count-batch (narrative events carry a real choice, so <c>force</c> rightly never skips
    /// them). This table names the KNOWN-trivial recurring events and, for each, the option the mod may
    /// answer with on the caller's behalf.
    ///
    /// Deliberately conservative:
    /// <list type="bullet">
    /// <item>keyed by exact TITLE — an unlisted event (or a modded lookalike with a new title) always
    /// blocks normally;</item>
    /// <item>the answer is matched by option LABEL, never index, and only while that option is enabled —
    /// if the game data changes, the sweep steps aside instead of guessing;</item>
    /// <item>every auto-resolve is reported (digest.autoResolvedEvents + get_recent_events), so the
    /// opt-in trades attention, not information.</item>
    /// </list>
    /// Curated choices (from the game's JSON event definitions under data/coreData):
    /// <list type="bullet">
    /// <item><b>Watched</b> (fog.midch_watched / anw.mid_oldwomen): "Let them gossip"/+15 profile (or
    /// +10), "Silence them"/+5 menace (or +10), "Abandon challenge". Chosen: <i>Silence them</i> —
    /// profile is the harder huntability gate (>= 50) and sets detection radius, so the menace bump is
    /// the smaller exposure cost; abandoning the challenge is never an acceptable default.</item>
    /// <item><b>Life Continues</b> (fog.midch_unrestFestival, fires at unrest > 40): "Subtly disrupt
    /// the party"/-15 progress, "This does not concern us"/-25 unrest, "Start a fight"/+3 menace -5
    /// progress +25 unrest. Chosen: <i>Subtly disrupt the party</i> — unrest above 40 is usually the
    /// player's own asset (it drags security down); paying 15 progress preserves it, while the
    /// "ignore" option quietly undoes the sabotage the agent is there to do.</item>
    /// <item><b>Merchant of Antiquities</b> (fog.midch_merchant_of_antiquities / anw.mid_trader):
    /// refuse/+4 profile +2 menace (or +4/+4), or gold-gated gambles for random items. Chosen:
    /// <i>Let him call them, we will not be extorted</i> — the only always-enabled option; the buys
    /// are judgement calls (gold for random loot) an auto-policy has no business making.</item>
    /// </list>
    /// </summary>
    public static class RoutineEvents
    {
        private static readonly Dictionary<string, string> Curated =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Watched", "Silence them" },
                { "Life Continues", "Subtly disrupt the party" },
                { "Merchant of Antiquities", "Let him call them, we will not be extorted" },
            };

        /// <summary>The curated option label for a whitelisted event title, or null when the event is
        /// not routine (i.e. it must block normally).</summary>
        public static string PreferredOption(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            string opt;
            return Curated.TryGetValue(title.Trim(), out opt) ? opt : null;
        }
    }
}
