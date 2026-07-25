using System.Collections.Generic;
using Assets.Code;

namespace ShadowsMcp
{
    /// <summary>One recruit-unlocked ability (ritual) an archetype gains the moment it is enthralled.</summary>
    public sealed class AbilityPreview
    {
        public readonly string Name;
        public readonly string Desc;
        /// <summary>Compact prerequisite gist, condensed from the game's getRestriction()/validFor().</summary>
        public readonly string Prereq;

        public AbilityPreview(string name, string desc, string prereq)
        {
            Name = name;
            Desc = desc;
            Prereq = prereq;
        }
    }

    /// <summary>What one archetype unlocks at recruitment: its constructor-granted rituals, plus an
    /// optional note for innate masteries or signature level-up options when rituals alone undersell it.</summary>
    public sealed class ArchetypeAbilities
    {
        public readonly AbilityPreview[] Rituals;
        public readonly string Note;

        public ArchetypeAbilities(AbilityPreview[] rituals, string note)
        {
            Rituals = rituals;
            Note = note;
        }
    }

    /// <summary>
    /// Hand-curated preview of what each recruitable archetype can do once enthralled, keyed by
    /// UAE_Abstraction.CODE_*. Scope: rituals added in the UAE_* constructor (= unlocked at
    /// recruitment), plus a Note covering innate masteries and signature level-up trait options
    /// (getStartingTraits() entries are first level-up CHOICES, not auto-grants). Rituals gained
    /// later via traits, items or events (e.g. Ch_DarkCoronation granting the Monarch rituals to a
    /// coronated agent) are intentionally out of scope. Sourced from the decompiled UAE_*/Rt_*
    /// classes; a missing entry (unknown/modded archetype) just omits the preview.
    /// </summary>
    public static class AbilityCatalog
    {
        // CODE_* are static ints on the game assembly, so the map is built at runtime, not in
        // a const-keyed initializer.
        private static Dictionary<int, ArchetypeAbilities> _byCode;

        public static ArchetypeAbilities Get(int code)
        {
            if (_byCode == null) _byCode = Build();
            return _byCode.TryGetValue(code, out ArchetypeAbilities a) ? a : null;
        }

        // ---- construction helpers (keep the catalog below declarative) ----
        private static AbilityPreview A(string name, string desc, string prereq) =>
            new AbilityPreview(name, desc, prereq);
        private static ArchetypeAbilities Arch(params AbilityPreview[] rituals) =>
            new ArchetypeAbilities(rituals, null);
        private static ArchetypeAbilities ArchN(string note, params AbilityPreview[] rituals) =>
            new ArchetypeAbilities(rituals, note);

        private static Dictionary<int, ArchetypeAbilities> Build()
        {
            var d = new Dictionary<int, ArchetypeAbilities>();

            d[UAE_Abstraction.CODE_BANDIT] = ArchN(
                "No recruit-unlocked rituals; pure fighter-commander (might 4 / command 4).");

            d[UAE_Abstraction.CODE_WARLOCK] = ArchN(
                "First level-ups offer Mastery of Geomancy / Death / Blood, each unlocking that " +
                "school's spells (Blood also grants the Taunting Lure ritual).");

            d[UAE_Abstraction.CODE_WARLORD] = Arch(
                A("Claim Territory", "Expands the orc horde into a new location",
                  "empty habitable land (ruins OK) adjacent to the horde, or coastal with an orc shipyard, or the horde holds no land"),
                A("Raiding Party", "Musters an orc raiding party under your command",
                  "100%-infiltrated orc camp; no army moving to attack it"),
                A("Commandeer Ships", "Turns a human dock into an orcish shipyard (overseas expansion)",
                  "human settlement with docks + the horde has an empty shipyard slot"),
                A("Orc Funding", "Dark Empire funds this horde's Orc Industry",
                  "stand in the Dark Empire; horde has a shipyard; not already funded"));

            d[UAE_Abstraction.CODE_HEIROPHANT] = Arch(
                A("Preach Gospel of Cowardice", "Local ruler becomes cowardly",
                  "100% infiltration; ruler not already averse to ambition/danger"),
                A("Preach Gospel of Violence", "Local ruler becomes cruel and violent",
                  "100% infiltration; ruler not already cruel/combative"),
                A("Preach Gospel of Envy", "Local ruler turns ambitious and uncooperative (invasions, civil wars)",
                  "100% infiltration; ruler not already so inclined"));

            d[UAE_Abstraction.CODE_BARONESS] = ArchN(
                "Undead vampire (might 6); level-ups offer Command of Vermin / Powered by Death / Mistress of the Night.",
                A("Eternal Servitude", "Fills every empty minion slot with Skeleton Warriors and heals existing ones",
                  "at her home (summoning) location"),
                A("Rest in Grave", "Heals her and her undead minions",
                  "at her home location"));

            d[UAE_Abstraction.CODE_TRICKSTER] = Arch(
                A("Misleading Clues", "Next challenge here pins its menace and profile on a framed hero",
                  "carry a stolen personal item of a hero or agent (not a ruler's)"),
                A("Steal Hero's Item", "Steals a personal item from a hero based here",
                  "human settlement, infiltration >0%, a hero's home"),
                A("Snake Oil", "Sells a hero a poisoned \"healing\" potion for 15 gold",
                  "hero here with at least 15 gold"));

            d[UAE_Abstraction.CODE_SURVIVOR] = ArchN(
                "No recruit-unlocked rituals; innate Mastery of Geomancy 2 (climate/famine spells).");

            d[UAE_Abstraction.CODE_DOCTOR] = Arch(
                A("New Outbreak", "Starts a plague here and drops plague immunity to 0%",
                  "human settlement, infiltration >0%, plague at 0%"),
                A("Cure Plague", "Cures all plague here, -5 menace",
                  "human settlement with plague >0%"),
                A("Medical Experimentation", "Produces an agent-only Arcane Secret",
                  "human settlement with plague >=75%"));

            d[UAE_Abstraction.CODE_COURTIER] = Arch(
                A("Steal Ruler's Item", "Steals the local ruler's personal item",
                  "human settlement, infiltration >0%"),
                A("Steal Hero's Item", "Steals a local hero's personal item",
                  "human settlement, infiltration >0%, a hero's home"),
                A("Cause Scandal", "A ruler or hero here starts disliking the carried item's owner",
                  "carry another person's personal item; a ruler or hero lives here"),
                A("Escalate to Vendetta", "Turns a killing into an inter-House blood feud",
                  "person here mourning kin killed by another House; the mourner must stay for the whole challenge"));

            d[UAE_Abstraction.CODE_MONARCH] = Arch(
                A("Dark Empire", "Converts this nation into the Dark Empire",
                  "capital city at 100% shadow; only one Dark Empire at a time; not a Holy Order"),
                A("Dark Crusade", "The Dark Empire declares war on this location's nation",
                  "human settlement of a non-Dark-Empire human nation"),
                A("Welcome Defeat", "Drops defences to 0 and halves the supported army",
                  "human settlement at 100% shadow"),
                A("Make an Example", "Executes a noble: unrest here and in neighbours to 0, clears Dark Empire agitation",
                  "Dark Empire location with a noble; unrest >50% or political agitation >0%"));

            d[UAE_Abstraction.CODE_CURSED] = ArchN(
                "No recruit-unlocked rituals; spawns with a Vow of Vengeance against a random nearby " +
                "person; level-ups offer Petrifying Gaze.");

            d[UAE_Abstraction.CODE_HARVESTER] = ArchN(
                "Innate Death-Curse Howl; level-ups offer Howl of Sin / Howl of Madness.",
                A("Harvest", "Consumes a Human Soul to recharge all Howl traits",
                  "location with a Human Soul modifier"));

            d[UAE_Abstraction.CODE_BUCCANEER] = Arch(
                A("Raid Shipping", "Disables trade routes through this ocean for a while, paying gold from endpoint prosperity",
                  "ocean location with un-raided trade routes"),
                A("Raid Port", "Halves defence; 20-turn security/prosperity/food debuff",
                  "coastal human settlement not already raided"));

            d[UAE_Abstraction.CODE_DISSIDENT] = Arch(
                A("A Better Choice", "Makes the local ruler heir to the nation (elves: instantly sovereign)",
                  "100% infiltration here AND capital at 100% shadow (or two of the nation's cities at 100%); elven society must lack a sovereign"),
                A("Separatism", "+2%/turn Political Agitation here for 50 turns",
                  "100%-infiltrated city more than 4 links from its capital"));

            d[UAE_Abstraction.CODE_SHAMAN] = ArchN(
                "Innate Geomancy and Death masteries (spellcaster).",
                A("Create Geomantic Locus", "Creates a Geomantic Locus (orc habitability, enables geomancy here)",
                  "location with a Human Soul and no existing locus; once ever"));

            d[UAE_Abstraction.CODE_ARISTOCRAT] = Arch(
                A("Crisis Vote: Plague", "Forces nobles into a vote over the plague, splitting them into resentful factions",
                  "plague >50% here + at least 3 other major settlements within 4 steps also qualifying, rulers alive; once per 32 turns"),
                A("Crisis Vote: Famine", "Forces nobles into a vote over the famine, splitting them into resentful factions",
                  "famine >10% here + at least 3 other major settlements within 4 steps also qualifying, rulers alive; once per 32 turns"));

            d[UAE_Abstraction.CODE_SPELLBINDER] = Arch(
                A("Twisted Space", "Next hero ending a turn here is disrupted", "anywhere"),
                A("Infuse Power", "Places a modifier boosting challenge progress here (charge-based, up to +4)", "anywhere"),
                A("Lash Trap", "Next hero here takes 3 HP and loses a minion", "anywhere"));

            d[UAE_Abstraction.CODE_EXILE] = Arch(
                A("Capture Victims", "Abducts victims for later sacrifice",
                  "infiltrated human or elven surface settlement"),
                A("Sacrifice to the Night", "Spends victims to remove ALL Wards on the map; leaves Evidence",
                  "at her home location, with victims"),
                A("Sacrifice to the God", "Spends victims for up to +4 Power; leaves Evidence",
                  "at her home location, with victims"));

            d[UAE_Abstraction.CODE_SEEKER] = Arch(
                A("Secret of the Arcane", "One of the five Secrets", "carry at least 1 Arcane Knowledge"),
                A("Secret of the Soul", "One of the five Secrets", "location with a Human Soul modifier"),
                A("Secret of Death", "One of the five Secrets", "Death Magic >50% here"),
                A("Secret of Madness", "One of the five Secrets", "Madness >50% or Unrest >150% here"),
                A("Secret of the Deep", "One of the five Secrets", "Deep One Cult or Malign Catch here"),
                A("Birth the Abomination", "Kills the Seeker and spawns a Shoggoth (powerful military creature under your control)",
                  "all five Secrets learned"));

            return d;
        }
    }
}
