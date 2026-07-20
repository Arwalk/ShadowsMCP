using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp
{
    /// <summary>
    /// Game object → JSON. Every direct game-field access in the mod is confined to this file,
    /// Tools/ActionTools.cs and Tools/Decisions/; each member used here is recorded in
    /// docs/ground-truth-notes.md.
    ///
    /// Id scheme: locations, persons and social groups have stable native indices → "L3",
    /// "P42", "SG5". Units and challenges get session-scoped registry ids → "U17", "C8".
    /// </summary>
    public static class Summaries
    {
        // ---------- ids ----------

        public static string LocationId(Location l) { return l == null ? null : "L" + l.index; }
        public static string PersonId(Person p) { return p == null ? null : "P" + p.index; }
        public static string SocialGroupId(SocialGroup sg) { return sg == null ? null : "SG" + sg.index; }
        public static string UnitId(GameContext ctx, Unit u) { return u == null ? null : ctx.Registry.IdFor(u, "U"); }
        public static string ChallengeId(GameContext ctx, Challenge c) { return c == null ? null : ctx.Registry.IdFor(c, "C"); }

        /// <summary>Resolve any entity id. Returns null when unknown, stale, or no game loaded.</summary>
        public static object ResolveId(GameContext ctx, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Map map = ctx.Map;

            if (id.StartsWith("SG", StringComparison.OrdinalIgnoreCase))
            {
                int idx;
                if (map == null || !int.TryParse(id.Substring(2), out idx)) return null;
                foreach (SocialGroup sg in map.socialGroups)
                {
                    if (sg != null && sg.index == idx) return sg;
                }
                return null;
            }

            char kind = char.ToUpperInvariant(id[0]);
            if (kind == 'L' || kind == 'P')
            {
                int idx;
                if (map == null || !int.TryParse(id.Substring(1), out idx)) return null;
                if (kind == 'L')
                {
                    foreach (Location l in map.locations)
                    {
                        if (l != null && l.index == idx) return l;
                    }
                    return null;
                }
                foreach (Person p in map.persons)
                {
                    if (p != null && p.index == idx) return p;
                }
                return null;
            }

            if (kind == 'U' || kind == 'C')
                return ctx.Registry.Resolve(id.ToUpperInvariant());

            return null;
        }

        /// <summary>{$id,$type,name} stub for known entity types; null for anything else.</summary>
        public static JsonValue EntityStub(GameContext ctx, object obj)
        {
            Location loc = obj as Location;
            if (loc != null) return Stub(LocationId(loc), loc, SafeName(() => loc.getName()));
            Unit unit = obj as Unit;
            if (unit != null) return Stub(UnitId(ctx, unit), unit, SafeName(() => unit.getName()));
            Person person = obj as Person;
            if (person != null) return Stub(PersonId(person), person, SafeName(() => person.getFullName()));
            SocialGroup sg = obj as SocialGroup;
            if (sg != null) return Stub(SocialGroupId(sg), sg, SafeName(() => sg.getName()));
            Challenge ch = obj as Challenge;
            if (ch != null) return Stub(ChallengeId(ctx, ch), ch, SafeName(() => ch.getName()));
            return null;
        }

        private static JsonValue Stub(string id, object obj, string name)
        {
            return JsonValue.NewObject()
                .Set("$id", id)
                .Set("$type", obj.GetType().Name)
                .Set("name", name);
        }

        private static string SafeName(Func<string> get)
        {
            try { return get(); }
            catch { return "<unnamed>"; }
        }

        // ---------- refs (id + name, for embedding) ----------

        public static JsonValue LocationRef(Location l)
        {
            if (l == null) return JsonValue.Null;
            return JsonValue.NewObject().Set("id", LocationId(l)).Set("name", SafeName(() => l.getName()));
        }

        public static JsonValue SocialGroupRef(SocialGroup sg)
        {
            if (sg == null) return JsonValue.Null;
            return JsonValue.NewObject().Set("id", SocialGroupId(sg)).Set("name", SafeName(() => sg.getName()));
        }

        public static JsonValue PersonRef(Person p)
        {
            if (p == null) return JsonValue.Null;
            return JsonValue.NewObject().Set("id", PersonId(p)).Set("name", SafeName(() => p.getFullName()));
        }

        public static JsonValue UnitRef(GameContext ctx, Unit u)
        {
            if (u == null) return JsonValue.Null;
            return JsonValue.NewObject().Set("id", UnitId(ctx, u)).Set("name", SafeName(() => u.getName()));
        }

        // ---------- threats ----------

        /// <summary>One entry from the game's built-in threats panel (Overmind.getThreats()).
        /// severity is the event priority (higher = more pressing); beneficial flags the few
        /// positive/opportunity entries. location is the hex the event points at, or null.</summary>
        public static JsonValue ThreatEvent(GameContext ctx, MsgEvent e)
        {
            if (e == null) return JsonValue.Null;
            Location loc = null;
            try { if (e.hex != null && e.hex.locationIndex != -1) loc = e.hex.location; }
            catch { }
            return JsonValue.NewObject()
                .Set("message", e.msg)
                .Set("severity", Round2(e.priority))
                .Set("beneficial", e.beneficial)
                .Set("location", LocationRef(loc));
        }

        // ---------- recruitment / end-of-game ----------

        /// <summary>An agent archetype you can enthrall (from overmind.agentsGeneric/agentsUnique).</summary>
        public static JsonValue AbstractionSummary(UAE_Abstraction abstr, string category)
        {
            return JsonValue.NewObject()
                .Set("code", abstr.code)
                .Set("name", SafeName(() => abstr.getName()))
                .Set("category", category)
                .Set("stats", JsonValue.NewObject()
                    .Set("might", abstr.getStatMight())
                    .Set("intrigue", abstr.getStatIntrigue())
                    .Set("lore", abstr.getStatLore())
                    .Set("command", abstr.getStatCommand()))
                .Set("restrictions", SafeName(() => abstr.getRestrictions()))
                .Set("desc", SafeName(() => abstr.getDesc()));
        }

        /// <summary>
        /// Whether a unit is an existing hero you could corrupt in place (code==0 enthrallment).
        /// Mirrors the corruptible-heroes scan in PopupAgentCreation.populate: a live UAG/UAA that is
        /// not commandable, not the Chosen One, and at 100% shadow or insane.
        /// </summary>
        public static bool IsCorruptibleHero(Unit unit)
        {
            if (unit == null || unit.isDead) return false;
            UA ua = unit as UA;
            if (ua == null || (!(ua is UAG) && !(ua is UAA))) return false;
            if (ua.isCommandable() || ua.person == null) return false;
            foreach (Trait t in ua.person.traits)
            {
                if (t is T_ChosenOne) return false;
            }
            return ua.person.shadow >= 0.98 || ua.person.isInsane();
        }

        /// <summary>Overmind.victoryMode int → label (see Overmind.VICTORY_MODE_* constants); null if unknown.</summary>
        public static string VictoryModeLabel(int mode)
        {
            switch (mode)
            {
                case 0: return "SHADOW";
                case 1: return "INSANITY";
                case 2: return "DARK_EMPIRE";
                case 3: return "RUIN";
                case 4: return "FROZEN";
                case 5: return "DEEP_ONES";
                default: return null;
            }
        }

        // ---------- units ----------

        public static JsonValue UnitSummary(GameContext ctx, Unit u)
        {
            // A dead unit keeps a stale task and can still report isCommandable()==true; force the
            // caller-facing view to null/false so a remembered id can't look actionable (the action
            // layer already rejects commands to it with "<unit> is dead").
            bool dead = u.isDead;
            JsonValue o = JsonValue.NewObject()
                .Set("id", UnitId(ctx, u))
                .Set("name", SafeName(() => u.getName()))
                .Set("type", u.GetType().Name)
                .Set("kind", u is UA ? "agent" : (u is UM ? "military" : "other"))
                .Set("commandable", !dead && u.isCommandable())
                .Set("location", LocationRef(u.location))
                .Set("society", SocialGroupRef(u.society))
                .Set("hp", u.hp)
                .Set("maxHp", u.maxHp)
                .Set("movesTaken", u.movesTaken)
                .Set("maxMoves", u.getMaxMoves())
                .Set("task", dead ? JsonValue.Null : TaskBrief(u.task));
            if (dead) o.Set("isDead", true);
            return o;
        }

        public static JsonValue UnitDetail(GameContext ctx, Unit u)
        {
            JsonValue o = UnitSummary(ctx, u)
                .Set("menace", Round2(u.menace))
                .Set("profile", Round2(u.profile))
                .Set("person", u.person != null ? PersonSummary(ctx, u.person) : JsonValue.Null)
                .Set("taskDetail", u.isDead ? JsonValue.Null : TaskDetail(ctx, u.task))
                .Set("engagedBy", UnitRef(ctx, u.engagedBy))
                .Set("engagedThisTurn", u.engagedBy != null && u.turnLastEngaged == u.map.turn);
            if (u.rituals != null && u.rituals.Count > 0)
            {
                JsonValue rituals = JsonValue.NewArray();
                foreach (Challenge r in u.rituals)
                {
                    rituals.Add(ChallengeSummary(ctx, r, u));
                }
                o.Set("rituals", rituals);
            }

            // Agent internals: corruption, combat power, fatigue, and the minions that fight beside it.
            UA ua = u as UA;
            if (ua != null)
            {
                JsonValue minions = JsonValue.NewArray();
                if (ua.minions != null)
                {
                    foreach (Minion m in ua.minions)
                        if (m != null && !m.isDead) minions.Add(MinionSummary(m));
                }
                o.Set("agent", JsonValue.NewObject()
                    .Set("corrupted", ua.corrupted)
                    .Set("attack", Safe(() => ua.getStatAttack(), 0))
                    .Set("challengesSinceRest", ua.challengesSinceRest)
                    .Set("turnsIdle", ua.turnsIdle)
                    .Set("disruptionExhaustion", ua.disruptionExhaustion)
                    .Set("minions", minions));

                // Combat readiness for risk management. dangerEstimate is the strength number the engine
                // compares unit-vs-unit (UA.getDangerEstimate: hp + defence + attack + minions) — read a
                // hostile hero's dangerEstimate too and compare to gauge who would win. isHuntable is the
                // human-ruler assassination trigger (profile >= 50 AND menace > 25).
                o.Set("combat", JsonValue.NewObject()
                    .Set("dangerEstimate", Safe(() => ua.getDangerEstimate(), 0))
                    .Set("hp", u.hp)
                    .Set("defence", Safe(() => ua.getMaxDefence(), 0))
                    .Set("attack", Safe(() => ua.getStatAttack(), 0))
                    .Set("menace", Round2(u.menace))
                    .Set("profile", Round2(u.profile))
                    .Set("isHuntable", u.profile >= 50.0 && u.menace > 25.0)
                    .Set("inHiding", ua.task is Task_InHiding));

                // What this agent is carrying (items live on the person; same shape as get_person.items).
                JsonValue items = JsonValue.NewArray();
                if (ua.person != null && ua.person.items != null)
                {
                    foreach (Item it in ua.person.items)
                    {
                        if (it != null)
                            items.Add(JsonValue.NewObject()
                                .Set("name", SafeName(() => it.getName()))
                                .Set("desc", Safe(() => it.getShortDesc(), null)));
                    }
                }
                o.Set("items", items);
            }

            // The detection picture for this unit: which heroes are building a case against it.
            if (!u.isDead && u.map != null)
            {
                JsonValue investigation = JsonValue.NewArray();
                int myPersonIndex = u.person != null ? u.person.index : -1;
                foreach (Location l in u.map.locations)
                {
                    if (l == null || l.evidence == null) continue;
                    foreach (Evidence e in l.evidence)
                    {
                        if (e == null) continue;
                        if (e.pointsTo == u || (myPersonIndex >= 0 && e.pointsToPerson == myPersonIndex))
                            investigation.Add(EvidenceSummary(ctx, e, false));
                    }
                }
                o.Set("investigation", investigation);
            }
            return o;
        }

        private static JsonValue TaskBrief(Task t)
        {
            if (t == null) return JsonValue.Null;
            string desc;
            try { desc = t.getShort(); }
            catch { desc = t.GetType().Name; }
            return JsonValue.Of(desc);
        }

        private static JsonValue TaskDetail(GameContext ctx, Task t)
        {
            if (t == null) return JsonValue.Null;
            JsonValue o = JsonValue.NewObject().Set("type", t.GetType().Name);
            try { o.Set("description", t.getLong()); } catch { }
            Task_PerformChallenge pc = t as Task_PerformChallenge;
            if (pc != null)
            {
                o.Set("challenge", SafeName(() => pc.challenge.getName()))
                 .Set("progress", Round2(pc.progress))
                 .Set("turnsTaken", pc.turnsTaken);
            }
            Task_GoToLocation go = t as Task_GoToLocation;
            if (go != null)
            {
                o.Set("destination", LocationRef(go.target));
            }
            // Enemy intent: who this unit is hunting / disrupting / guarding, and how long until
            // it loses the trail. Lets you answer "who is targeting my agent" from get_unit.
            Task_AttackUnit attack = t as Task_AttackUnit;
            if (attack != null)
            {
                o.Set("target", UnitRef(ctx, attack.target))
                 .Set("turnsRemaining", attack.turnsRemaining);
            }
            Task_DisruptUA disrupt = t as Task_DisruptUA;
            if (disrupt != null)
            {
                o.Set("target", UnitRef(ctx, disrupt.other))
                 .Set("turnsLeft", disrupt.turnsLeft);
            }
            Task_Bodyguard guard = t as Task_Bodyguard;
            if (guard != null)
            {
                o.Set("target", UnitRef(ctx, guard.target))
                 .Set("turnsRemaining", guard.turnsRemaining);
                if (guard.targetChallenge != null)
                    o.Set("challenge", SafeName(() => guard.targetChallenge.getName()));
            }
            return o;
        }

        // ---------- locations ----------

        public static JsonValue LocationSummary(GameContext ctx, Location l)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", LocationId(l))
                .Set("name", SafeName(() => l.getName()))
                .Set("terrainInfo", JsonValue.NewObject()
                    .Set("isOcean", l.isOcean)
                    .Set("isCoastal", l.isCoastal)
                    .Set("isMajor", l.isMajor))
                .Set("owner", SocialGroupRef(l.soc))
                .Set("settlement", l.settlement != null
                    ? JsonValue.NewObject()
                        .Set("type", l.settlement.GetType().Name)
                        .Set("name", SafeName(() => l.settlement.getName()))
                    : JsonValue.Null);

            JsonValue units = JsonValue.NewArray();
            foreach (Unit u in l.units) units.Add(UnitRef(ctx, u));
            o.Set("units", units);

            JsonValue props = JsonValue.NewArray();
            foreach (Property p in l.properties) props.Add(SafeName(() => p.getName()));
            o.Set("properties", props);

            JsonValue neighbours = JsonValue.NewArray();
            foreach (Location n in l.getNeighbours()) neighbours.Add(LocationId(n));
            o.Set("neighbours", neighbours);
            return o;
        }

        public static JsonValue LocationDetail(GameContext ctx, Location l)
        {
            JsonValue o = LocationSummary(ctx, l);

            if (l.hex != null)
                o.Set("hex", JsonValue.NewObject().Set("x", l.hex.x).Set("y", l.hex.y).Set("z", l.hex.z));

            if (l.settlement != null)
            {
                Settlement st = l.settlement;
                JsonValue s = JsonValue.NewObject()
                    .Set("type", st.GetType().Name)
                    .Set("name", SafeName(() => st.getName()))
                    .Set("shadow", Round2(st.shadow))
                    .Set("defences", Round2(st.defences))
                    .Set("isHuman", st.isHuman)
                    .Set("isInfiltrated", st.isInfiltrated)
                    // Fraction of infiltratable sub-districts infiltrated (0..1); 1.0 == fully infiltrated.
                    // Several challenges (Enshadow, Desecrate) gate on this reaching 1.0.
                    .Set("infiltration", Round2(Safe(() => st.infiltration, 0.0)));
                // What this settlement is currently enacting (applies to any settlement type).
                if (st.actionUnderway != null)
                    s.Set("action", JsonValue.NewObject()
                        .Set("name", SafeName(() => st.actionUnderway.getName()))
                        .Set("progress", st.actionProgress));
                // Human settlements carry the economy layer (population, prosperity, food, succession).
                SettlementHuman sh = st as SettlementHuman;
                if (sh != null)
                {
                    if (sh.ruler != null) s.Set("ruler", PersonRef(sh.ruler));
                    if (sh.heir != null) s.Set("heir", PersonRef(sh.heir));
                    s.Set("population", sh.population)
                     .Set("prosperity", Round2(sh.prosperity))
                     .Set("growingPop", Round2(sh.growingPop))
                     .Set("food", JsonValue.NewObject()
                        .Set("lastTurn", sh.foodLastTurn)
                        .Set("local", sh.foodLocal)
                        .Set("imported", sh.foodImported))
                     .Set("shadowPolicy", sh.shadowPolicy.ToString())
                     .Set("supportedMilitary", UnitRef(ctx, sh.supportedMilitary))
                     .Set("holyOrder", SocialGroupRef(sh.order));
                }
                JsonValue subs = JsonValue.NewArray();
                if (st.subs != null)
                {
                    foreach (Subsettlement sub in st.subs)
                    {
                        // {name, infiltrated} per district (was name-only), so an agent can see e.g.
                        // "City Palace infiltrated: false" — the gate behind Enshadow / Desecrate.
                        JsonValue sv = JsonValue.NewObject()
                            .Set("name", SafeName(() => sub.getName()))
                            .Set("infiltrated", sub.infiltrated);
                        if (Safe(() => sub.menace, 0.0) != 0.0) sv.Set("menace", Round2(sub.menace));
                        subs.Add(sv);
                    }
                }
                s.Set("subsettlements", subs);
                o.Set("settlement", s);
            }

            JsonValue props = JsonValue.NewArray();
            foreach (Property p in l.properties)
            {
                JsonValue pv = JsonValue.NewObject()
                    .Set("type", p.GetType().Name)
                    .Set("name", SafeName(() => p.getName()))
                    .Set("charge", Round2(p.charge));
                if (p.influences != null && p.influences.Count > 0)
                    pv.Set("influences", InfluenceList(p.influences));
                props.Add(pv);
            }
            o.Set("properties", props);

            // Clues the heroes hold here (each can raise panic / awareness of the underground if investigated).
            if (l.evidence != null && l.evidence.Count > 0)
            {
                JsonValue ev = JsonValue.NewArray();
                foreach (Evidence e in l.evidence) ev.Add(EvidenceSummary(ctx, e, true));
                o.Set("evidence", ev);
            }
            return o;
        }

        // ---------- persons ----------

        public static JsonValue PersonSummary(GameContext ctx, Person p)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", PersonId(p))
                .Set("name", SafeName(() => p.getFullName()))
                .Set("society", SocialGroupRef(p.society))
                .Set("unit", UnitRef(ctx, p.unit))
                .Set("isDead", p.isDead)
                .Set("shadow", Round2(p.shadow));
            if (p.rulerOf >= 0)
            {
                Location seat = FindLocation(ctx, p.rulerOf);
                o.Set("rulerOf", LocationRef(seat));
            }
            return o;
        }

        public static JsonValue PersonDetail(GameContext ctx, Person p)
        {
            JsonValue o = PersonSummary(ctx, p)
                .Set("state", p.state.ToString())
                .Set("age", p.age)
                .Set("gold", p.gold)
                .Set("prestige", Round2(p.prestige))
                .Set("targetPrestige", Round2(p.targetPrestige))
                .Set("awareness", Round2(p.awareness))
                .Set("sanity", Round2(p.sanity))
                .Set("maxSanity", p.maxSanity)
                .Set("level", p.level)
                .Set("xp", p.XP)
                .Set("xpForNextLevel", p.XPForNextLevel)
                .Set("skillPoints", p.skillPoints)
                .Set("kills", p.statistic_kills)
                .Set("watched", p.watched)
                .Set("species", p.species != null ? SafeName(() => p.species.name()) : null)
                .Set("house", p.house != null ? p.house.name : null)
                .Set("stats", JsonValue.NewObject()
                    .Set("might", p.stat_might)
                    .Set("lore", p.stat_lore)
                    .Set("intrigue", p.stat_intrigue)
                    .Set("command", p.stat_command))
                // Alert flags the game raises as a person's corruption/awareness crosses thresholds.
                .Set("alerts", JsonValue.NewObject()
                    .Set("maxShadow", p.alert_maxShadow)
                    .Set("halfShadow", p.alert_halfShadow)
                    .Set("aware", p.alert_aware))
                // Who this person likes/hates (drives their politics); resolved to person refs.
                .Set("relationships", JsonValue.NewObject()
                    .Set("likes", PersonRefsByIndex(ctx, p.likes))
                    .Set("hates", PersonRefsByIndex(ctx, p.hates))
                    .Set("extremeLikes", PersonRefsByIndex(ctx, p.extremeLikes))
                    .Set("extremeHates", PersonRefsByIndex(ctx, p.extremeHates)));

            // Traits and items as {name, desc} objects (were name strings) so their effects are legible.
            JsonValue traits = JsonValue.NewArray();
            if (p.traits != null)
            {
                foreach (Trait t in p.traits)
                {
                    traits.Add(JsonValue.NewObject()
                        .Set("name", SafeName(() => t.getName()))
                        .Set("desc", Safe(() => t.getDesc(), null)));
                }
            }
            o.Set("traits", traits);

            JsonValue items = JsonValue.NewArray();
            if (p.items != null)
            {
                foreach (Item it in p.items)
                {
                    if (it != null)
                        items.Add(JsonValue.NewObject()
                            .Set("name", SafeName(() => it.getName()))
                            .Set("desc", Safe(() => it.getShortDesc(), null)));
                }
            }
            o.Set("items", items);

            // Curses are carried on the noble house (shared by all its members).
            if (p.house != null && p.house.curses != null && p.house.curses.Count > 0)
            {
                JsonValue curses = JsonValue.NewArray();
                foreach (Curse c in p.house.curses) curses.Add(SafeName(() => c.getName()));
                o.Set("curses", curses);
            }
            return o;
        }

        // ---------- social groups ----------

        public static JsonValue SocialGroupSummary(GameContext ctx, SocialGroup sg)
        {
            Map map = ctx.Map;
            int locationCount = 0;
            if (map != null)
            {
                foreach (Location l in map.locations)
                {
                    if (l.soc == sg) locationCount++;
                }
            }
            JsonValue o = JsonValue.NewObject()
                .Set("id", SocialGroupId(sg))
                .Set("name", SafeName(() => sg.getName()))
                .Set("type", sg.GetType().Name)
                .Set("isPlayerFaction", map != null && sg == (SocialGroup)map.soc_dark)
                .Set("locationCount", locationCount)
                .Set("military", JsonValue.NewObject()
                    .Set("current", Round2(sg.currentMilitary))
                    .Set("max", Round2(sg.maxMilitary)));

            JsonValue wars = JsonValue.NewArray();
            foreach (KeyValuePair<SocialGroup, DipRel> kv in sg.relations)
            {
                if (kv.Key != sg && kv.Value != null && kv.Value.state == DipRel.dipState.war)
                    wars.Add(SocialGroupRef(kv.Key));
            }
            o.Set("atWarWith", wars);
            return o;
        }

        public static JsonValue SocialGroupDetail(GameContext ctx, SocialGroup sg)
        {
            JsonValue o = SocialGroupSummary(ctx, sg);

            Society soc = sg as Society;
            if (soc != null)
            {
                o.Set("posture", soc.posture.ToString())
                 .Set("isRebellion", soc.isRebellion)
                 .Set("isDarkEmpire", soc.isDarkEmpire)
                 .Set("isAlliance", soc.isAlliance)
                 .Set("internationalTension", Round2(soc.data_highestInternationalTension))
                 .Set("offensiveTarget", SocialGroupRef(soc.offensiveTarget))
                 .Set("defensiveTarget", SocialGroupRef(soc.defensiveTarget));
                Location capital = Safe(() => soc.getCapital(), null);
                if (capital != null)
                {
                    o.Set("capital", LocationRef(capital));
                    SettlementHuman seat = capital.settlement as SettlementHuman;
                    if (seat != null && seat.ruler != null) o.Set("sovereign", PersonRef(seat.ruler));
                }
                // The strategic-level intent: what this nation is currently enacting (declare war,
                // quarantine, crusade, join the dark empire...), the counterpart to unit taskDetail.
                if (soc.actionUnderway != null)
                    o.Set("nationalAction", JsonValue.NewObject()
                        .Set("name", SafeName(() => soc.actionUnderway.getName()))
                        .Set("desc", Safe(() => soc.actionUnderway.getShortDesc(), null))
                        .Set("turnsRequired", Safe(() => soc.actionUnderway.getTurnsRequired(), 0))
                        .Set("progress", soc.actionProgress));
            }

            // Religion-specific state when this group is a holy order.
            HolyOrder ho = sg as HolyOrder;
            if (ho != null) o.Set("holyOrder", HolyOrderBlock(ctx, ho));

            JsonValue relations = JsonValue.NewArray();
            foreach (KeyValuePair<SocialGroup, DipRel> kv in sg.relations)
            {
                if (kv.Key == sg || kv.Value == null) continue;
                JsonValue rel = JsonValue.NewObject()
                    .Set("with", SocialGroupRef(kv.Key))
                    .Set("state", kv.Value.state.ToString())
                    .Set("status", Round2(kv.Value.status));
                if (kv.Value.war != null) rel.Set("war", WarSummary(kv.Value.war));
                relations.Add(rel);
            }
            o.Set("relations", relations);
            return o;
        }

        // ---------- challenges ----------

        public static JsonValue ChallengeSummary(GameContext ctx, Challenge c, Unit forUnit, bool includeDescription = true)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", ChallengeId(ctx, c))
                .Set("name", SafeName(() => c.getName()))
                .Set("type", c.GetType().Name)
                .Set("isRitual", c is Ritual)
                .Set("location", LocationRef(c.location))
                .Set("valid", Safe(() => c.valid(), false))
                .Set("menaceGain", Safe(() => Round2(c.getMenace()), 0))
                .Set("profileGain", Safe(() => Round2(c.getProfile()), 0))
                .Set("danger", Safe(() => c.getDanger(), 0))
                .Set("claimedBy", UnitRef(ctx, c.claimedBy));
            // Why the challenge is locked / what it needs — the game's own hint text (getRestriction).
            // `valid` says whether the world preconditions are met and `validForUnit` whether THIS unit
            // qualifies; `restriction` states the actual requirement (e.g. "Requires 100% Infiltration.
            // Cannot perform if Ward is higher than 50%"). Combine it with the location's shadow /
            // infiltration / ward (get_location or world_summary) to see which condition is unmet.
            try
            {
                string restriction = c.getRestriction();
                if (!string.IsNullOrEmpty(restriction)) o.Set("restriction", restriction);
            }
            catch { }
            if (includeDescription)
            {
                try { o.Set("description", c.getDesc()); } catch { }
            }

            UA ua = forUnit as UA;
            if (ua != null)
            {
                o.Set("validForUnit", Safe(() => c.validFor(ua), false));
                o.Set("progressPerTurn", Safe(() => Round2(c.getProgressPerTurn(ua, null)), 0));
                o.Set("complexity", Safe(() => Round2(c.getComplexityAfterDifficulty()), 0));
            }
            UM um = forUnit as UM;
            if (um != null)
            {
                o.Set("validForUnit", Safe(() => c.validFor(um), false));
            }
            return o;
        }

        // ---------- player / god ----------

        public static JsonValue PowerSummary(Map map, Power p, int index)
        {
            bool passive = Safe(() => p.isPassiveOnly(), false);
            int cost = Safe(() => p.getCost(), 0);
            JsonValue o = JsonValue.NewObject()
                .Set("id", "PW" + index)
                .Set("name", SafeName(() => p.getName()))
                .Set("cost", cost)
                .Set("passiveOnly", passive)
                .Set("castableNow", !passive && map.overmind.power >= (double)cost);
            try { o.Set("description", p.getDesc()); } catch { }
            try
            {
                string restriction = p.getRestrictionText();
                if (!string.IsNullOrEmpty(restriction)) o.Set("targetRestriction", restriction);
            }
            catch { }
            return o;
        }

        /// <summary>The god's win-condition sheet: time budget, seal thresholds, the agent-cap curve
        /// as seals break, power-level requirements, and the victory / seal descriptive text a player
        /// reads on screen. Null when no god is selected.</summary>
        public static JsonValue GodProgression(Map map)
        {
            God god = map.overmind.god;
            if (god == null) return JsonValue.Null;
            int maxTurns = Safe(() => god.getMaxTurns(), 0);
            return JsonValue.NewObject()
                .Set("maxTurns", maxTurns)
                .Set("turnsRemaining", map.opt_endless ? JsonValue.Null : JsonValue.Of(Math.Max(0, maxTurns - map.turn)))
                .Set("maxPower", Safe(() => god.getMaxPower(), 0))
                .Set("sealLevels", IntArray(Safe(() => god.getSealLevels(), null)))
                .Set("agentCaps", IntArray(Safe(() => god.getAgentCaps(), null)))
                .Set("powerLevelReqs", IntList(god.powerLevelReqs))
                // How this god plays and wins (meaningful throughout the game).
                .Set("mechanics", Safe(() => god.getDetailedMechanics(), null))
                .Set("sealDesc", Safe(() => god.getSealDesc(), null))
                .Set("powerIncreaseText", Safe(() => god.powerIncreaseText(), null))
                // The specific victory blurb is mode-keyed, and victoryMode is -1 until a win is
                // recorded — so only surface it once the game is actually decided.
                .Set("victoryMessage", map.overmind.endOfGameAchieved
                    ? Safe(() => god.getVictoryMessage(map.overmind.victoryMode), null) : null);
        }

        private static JsonValue IntArray(int[] a)
        {
            JsonValue arr = JsonValue.NewArray();
            if (a != null) foreach (int v in a) arr.Add(v);
            return arr;
        }

        private static JsonValue IntList(List<int> a)
        {
            JsonValue arr = JsonValue.NewArray();
            if (a != null) foreach (int v in a) arr.Add(v);
            return arr;
        }

        // ---------- evidence / investigation (the detection economy) ----------

        /// <summary>True when a clue points at a unit you benefit from — one of your commandable
        /// agents or any UAE (evil agents on your side). Mirrors the hostile-target test in
        /// get_threats / list_units(hostileToMe): target.isCommandable() || target is UAE.</summary>
        public static bool IsEvidenceAgainstInterest(Evidence e)
        {
            if (e == null || e.pointsTo == null || e.pointsTo.isDead) return false;
            return e.pointsTo.isCommandable() || e.pointsTo is UAE;
        }

        /// <summary>One clue the heroes hold. investigator is the hero chasing it (or null); weight is
        /// how strong the lead is; rumours counts how far it has spread; reported flags that it has
        /// already been handed to a society. Pass includeTarget for the global investigations view.</summary>
        public static JsonValue EvidenceSummary(GameContext ctx, Evidence e, bool includeTarget)
        {
            if (e == null) return JsonValue.Null;
            JsonValue o = JsonValue.NewObject();
            if (includeTarget) o.Set("target", UnitRef(ctx, e.pointsTo));
            return o
                .Set("investigator", UnitRef(ctx, e.assignedInvestigator))
                .Set("weight", Round2(e.weight))
                .Set("rumours", e.rumourCounter)
                .Set("foundAt", LocationRef(FindLocation(ctx, e.locationFound)))
                .Set("turnDropped", e.turnDropped)
                .Set("reported", e.reportedToSociety);
        }

        /// <summary>Total live clues pointing at your interests, across the whole map.</summary>
        public static int CountInvestigationsAgainstMe(Map map)
        {
            if (map == null) return 0;
            int n = 0;
            foreach (Location l in map.locations)
            {
                if (l == null || l.evidence == null) continue;
                foreach (Evidence e in l.evidence)
                    if (IsEvidenceAgainstInterest(e)) n++;
            }
            return n;
        }

        // ---------- wars ----------

        public static JsonValue WarSummary(War w)
        {
            if (w == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("attacker", SocialGroupRef(w.att))
                .Set("defender", SocialGroupRef(w.def))
                .Set("objective", w.attackerObjective.ToString())
                .Set("startTurn", w.startTurn)
                .Set("canTimeOut", w.canTimeOut)
                .Set("endsTurn", Safe(() => w.turnOfEnd(), -1));
        }

        // ---------- religion ----------

        /// <summary>Religion-specific state of a HolyOrder (a SocialGroup subclass): enshadowment,
        /// prophet, tenets and reach. Null when the group is not a holy order.</summary>
        public static JsonValue HolyOrderBlock(GameContext ctx, HolyOrder ho)
        {
            if (ho == null) return JsonValue.Null;
            JsonValue tenets = JsonValue.NewArray();
            if (ho.tenets != null)
                foreach (HolyTenet t in ho.tenets) tenets.Add(SafeName(() => t.getName()));
            return JsonValue.NewObject()
                .Set("enshadowment", Round2(ho.enshadowment))
                .Set("worshipsThePlayer", ho.worshipsThePlayer)
                .Set("nAcolytes", ho.nAcolytes)
                .Set("nTemples", ho.nTemples)
                .Set("nWorshippers", ho.nWorshippers)
                .Set("nWorshippingRulers", ho.nWorshippingRulers)
                .Set("reserves", ho.reserves)
                .Set("influenceElder", ho.influenceElder)
                .Set("influenceHuman", ho.influenceHuman)
                .Set("prophet", UnitRef(ctx, ho.prophet))
                .Set("divinity", ho.divinity != null ? SafeName(() => ho.divinity.getName()) : null)
                .Set("tenets", tenets);
        }

        // ---------- agent minions ----------

        public static JsonValue MinionSummary(Minion m)
        {
            if (m == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("hp", m.hp)
                .Set("defence", m.defence)
                .Set("isDead", m.isDead);
        }

        // ---------- helpers ----------

        /// <summary>Resolve a native person index to a Person (mirrors FindLocation). -1 → null.</summary>
        public static Person FindPerson(GameContext ctx, int index)
        {
            Map map = ctx.Map;
            if (map == null || index < 0) return null;
            foreach (Person p in map.persons)
            {
                if (p != null && p.index == index) return p;
            }
            return null;
        }

        /// <summary>Render a list of native person indices (likes/hates/…) as PersonRef entries.</summary>
        private static JsonValue PersonRefsByIndex(GameContext ctx, List<int> indices)
        {
            JsonValue arr = JsonValue.NewArray();
            if (indices != null)
                foreach (int idx in indices)
                {
                    Person p = FindPerson(ctx, idx);
                    if (p != null) arr.Add(PersonRef(p));
                }
            return arr;
        }

        /// <summary>A property's influence breakdown (ReasonMsg list) as [{reason, value}].</summary>
        private static JsonValue InfluenceList(List<ReasonMsg> influences)
        {
            JsonValue arr = JsonValue.NewArray();
            if (influences != null)
                foreach (ReasonMsg r in influences)
                    if (r != null) arr.Add(JsonValue.NewObject().Set("reason", r.msg).Set("value", Round2(r.value)));
            return arr;
        }

        // ---------- seals ----------

        /// <summary>Live seal countdown: seals broken, the running turn counter (sealProgress), the turn
        /// the next seal breaks (God.getSealLevels()[sealsBroken]) and turns remaining. Surfaced flat so an
        /// agent reading game_overview each turn cannot miss the fixed break schedule.</summary>
        public static JsonValue SealTiming(Map map)
        {
            Overmind om = map != null ? map.overmind : null;
            God god = om != null ? om.god : null;
            int[] levels = god != null ? Safe(() => god.getSealLevels(), null) : null;
            int broken = om != null ? om.sealsBroken : 0;
            int progress = om != null ? om.sealProgress : 0;
            JsonValue o = JsonValue.NewObject()
                .Set("sealsBroken", broken)
                .Set("sealProgress", progress);
            if (levels != null && broken >= 0 && broken < levels.Length)
            {
                o.Set("nextSealAt", levels[broken]);
                o.Set("turnsToNextSeal", Math.Max(0, levels[broken] - progress));
            }
            return o;
        }

        // ---------- combat safety (risk management) ----------

        /// <summary>Per-agent risk snapshot: the most-inclined nearby hunter for each of your commandable
        /// agents, mirroring the hunt scan in Overmind.getThreats. dangerEstimate is the engine's
        /// unit-vs-unit strength (UA.getDangerEstimate); isHuntable is the human-ruler assassination trigger
        /// (profile >= 50 AND menace > 25). Shared by get_threats (detail), game_overview (counts) and
        /// end_turn (before/after escalation diff).</summary>
        public sealed class AgentSafetyInfo
        {
            public UA Agent;
            public int DangerEstimate;
            public double Profile;
            public double Menace;
            public bool IsHuntable;
            public bool InHiding;
            public UA TopHunter;
            public double TopMotivation;   // 0..1
            public int HunterDanger;

            public string Verdict()
            {
                if (TopHunter == null) return "safe";
                if (DangerEstimate >= (double)HunterDanger * 1.2) return "favoured";
                if (HunterDanger >= (double)DangerEstimate * 1.2) return "outmatched";
                return "even";
            }

            /// <summary>A nearby hunter is inclined AND you are not clearly stronger.</summary>
            public bool InDanger() { return TopHunter != null && Verdict() != "favoured"; }
        }

        public static List<AgentSafetyInfo> ComputeAgentSafety(GameContext ctx, Map map)
        {
            var result = new List<AgentSafetyInfo>();
            if (map == null || map.units == null) return result;
            foreach (Unit unit in map.units)
            {
                if (unit == null || unit.isDead) continue;
                UA ua = unit as UA;
                if (ua == null || !ua.isCommandable()) continue;

                double myProfile = Safe(() => ua.profile, 0.0);
                UA topHunter = null;
                double topMotivation = 0.0;
                foreach (Unit other in map.units)
                {
                    // Mirror Overmind.getThreats: only hostile heroes (skip your own agents and allied UAEN),
                    // within the hero's reach (stepDist <= profile/5), ranked by getAttackUtility.
                    if (other == null || other.isDead || other is UAEN || other.isCommandable()) continue;
                    UA hunter = other as UA;
                    if (hunter == null) continue;
                    int dist;
                    try { dist = map.getStepDist(hunter.location, ua.location); }
                    catch { continue; }
                    if ((double)dist > myProfile / 5.0) continue;

                    var reasons = new List<ReasonMsg>();
                    try { hunter.getAttackUtility(ua, reasons); }
                    catch { continue; }
                    double pos = 0.0, neg = 1.0;
                    foreach (ReasonMsg r in reasons)
                    {
                        if (r.value > 0.0) pos += r.value; else neg -= r.value;
                    }
                    double motivation = neg != 0.0 ? pos / neg : 0.0;
                    if (motivation > topMotivation) { topMotivation = motivation; topHunter = hunter; }
                }

                double myMenace = Safe(() => ua.menace, 0.0);
                result.Add(new AgentSafetyInfo
                {
                    Agent = ua,
                    DangerEstimate = Safe(() => ua.getDangerEstimate(), 0),
                    Profile = myProfile,
                    Menace = myMenace,
                    IsHuntable = myProfile >= 50.0 && myMenace > 25.0,
                    InHiding = ua.task is Task_InHiding,
                    TopHunter = topHunter,
                    TopMotivation = topHunter != null ? Math.Min(topMotivation, 1.0) : 0.0,
                    HunterDanger = topHunter != null ? Safe(() => topHunter.getDangerEstimate(), 0) : 0
                });
            }
            return result;
        }

        public static JsonValue AgentSafetyJson(GameContext ctx, AgentSafetyInfo s)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("agent", UnitRef(ctx, s.Agent))
                .Set("dangerEstimate", s.DangerEstimate)
                .Set("profile", Round2(s.Profile))
                .Set("menace", Round2(s.Menace))
                .Set("isHuntable", s.IsHuntable)
                .Set("inHiding", s.InHiding)
                .Set("verdict", s.Verdict());
            if (s.TopHunter != null)
                o.Set("topHunter", JsonValue.NewObject()
                    .Set("unit", UnitRef(ctx, s.TopHunter))
                    .Set("motivationPct", (int)Math.Round(s.TopMotivation * 100.0))
                    .Set("dangerEstimate", s.HunterDanger));
            return o;
        }

        /// <summary>Compact one-line danger summary for game_overview.threats.mostUrgent / threatAlert.</summary>
        public static string AgentSafetyLine(GameContext ctx, AgentSafetyInfo s)
        {
            if (s.TopHunter == null) return null;
            string hunter = SafeName(() => s.TopHunter.getName());
            string agent = SafeName(() => s.Agent.getName());
            return hunter + " is hunting " + agent + " (motivation " +
                (int)Math.Round(s.TopMotivation * 100.0) + "%, " + s.Verdict() + ")";
        }

        private static int VerdictRank(string v)
        {
            switch (v)
            {
                case "favoured": return 1;
                case "even": return 2;
                case "outmatched": return 3;
                default: return 0; // safe
            }
        }

        /// <summary>Compare a before-snapshot to the live state; return an array of agents that just gained a
        /// hunter or whose odds worsened (null if none). Drives end_turn.threatAlert / early-stop.</summary>
        public static JsonValue ThreatAlert(GameContext ctx, Map map, List<AgentSafetyInfo> before)
        {
            var now = ComputeAgentSafety(ctx, map);
            var byAgent = new Dictionary<UA, AgentSafetyInfo>();
            if (before != null) foreach (var b in before) byAgent[b.Agent] = b;

            JsonValue alerts = JsonValue.NewArray();
            foreach (var s in now)
            {
                AgentSafetyInfo b;
                bool had = byAgent.TryGetValue(s.Agent, out b);
                bool gainedHunter = s.TopHunter != null && (!had || b.TopHunter == null);
                bool worsened = s.TopHunter != null && had && b.TopHunter != null &&
                                VerdictRank(s.Verdict()) > VerdictRank(b.Verdict());
                if (gainedHunter || worsened)
                {
                    JsonValue a = AgentSafetyJson(ctx, s);
                    a.Set("message", AgentSafetyLine(ctx, s));
                    alerts.Add(a);
                }
            }
            return alerts.Count > 0 ? alerts : JsonValue.Null;
        }

        // ---------- world summary ----------

        /// <summary>One terse strategic row per location for world_summary: coords, owner, and settlement
        /// essentials (shadow, defences, population, infiltration + per-sub infiltrated state, capital flag)
        /// plus neighbour ids. Same field reads as LocationDetail, without the per-location tool overhead.</summary>
        public static JsonValue WorldSummaryRow(GameContext ctx, Location l)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", LocationId(l))
                .Set("name", SafeName(() => l.getName()))
                .Set("owner", SocialGroupRef(l.soc));
            if (l.hex != null)
                o.Set("coords", JsonValue.NewObject().Set("x", l.hex.x).Set("y", l.hex.y).Set("z", l.hex.z));

            if (l.settlement != null)
            {
                Settlement st = l.settlement;
                JsonValue sset = JsonValue.NewObject()
                    .Set("type", st.GetType().Name)
                    .Set("shadow", Round2(st.shadow))
                    .Set("defences", Round2(st.defences))
                    .Set("isInfiltrated", st.isInfiltrated)
                    .Set("infiltration", Round2(Safe(() => st.infiltration, 0.0)));
                SettlementHuman sh = st as SettlementHuman;
                if (sh != null) sset.Set("population", sh.population);
                Society soc = l.soc as Society;
                if (soc != null && soc.capital == l.index) sset.Set("isCapital", true);
                if (st.subs != null && st.subs.Count > 0)
                {
                    JsonValue subs = JsonValue.NewArray();
                    foreach (Subsettlement sub in st.subs)
                        subs.Add(JsonValue.NewObject()
                            .Set("name", SafeName(() => sub.getName()))
                            .Set("infiltrated", sub.infiltrated));
                    sset.Set("subs", subs);
                }
                o.Set("settlement", sset);
            }

            JsonValue neighbours = JsonValue.NewArray();
            foreach (Location n in l.getNeighbours()) neighbours.Add(LocationId(n));
            o.Set("neighbours", neighbours);
            return o;
        }

        public static Location FindLocation(GameContext ctx, int index)
        {
            Map map = ctx.Map;
            if (map == null) return null;
            foreach (Location l in map.locations)
            {
                if (l != null && l.index == index) return l;
            }
            return null;
        }

        public static double Round2(double v)
        {
            return Math.Round(v, 2);
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }
    }
}
