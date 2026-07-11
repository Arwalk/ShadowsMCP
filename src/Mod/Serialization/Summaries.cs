using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp
{
    /// <summary>
    /// Game object → JSON. Every direct game-field access in the mod is confined to this file
    /// and Tools/ActionTools.cs; each member used here is recorded in docs/ground-truth-notes.md.
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

        // ---------- units ----------

        public static JsonValue UnitSummary(GameContext ctx, Unit u)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", UnitId(ctx, u))
                .Set("name", SafeName(() => u.getName()))
                .Set("type", u.GetType().Name)
                .Set("kind", u is UA ? "agent" : (u is UM ? "military" : "other"))
                .Set("commandable", u.isCommandable())
                .Set("location", LocationRef(u.location))
                .Set("society", SocialGroupRef(u.society))
                .Set("hp", u.hp)
                .Set("maxHp", u.maxHp)
                .Set("movesTaken", u.movesTaken)
                .Set("maxMoves", u.getMaxMoves())
                .Set("task", TaskBrief(u.task));
            if (u.isDead) o.Set("isDead", true);
            return o;
        }

        public static JsonValue UnitDetail(GameContext ctx, Unit u)
        {
            JsonValue o = UnitSummary(ctx, u)
                .Set("menace", Round2(u.menace))
                .Set("profile", Round2(u.profile))
                .Set("person", u.person != null ? PersonSummary(ctx, u.person) : JsonValue.Null)
                .Set("taskDetail", TaskDetail(u.task))
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

        private static JsonValue TaskDetail(Task t)
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
                        .Set("name", l.settlement.name)
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
                JsonValue s = JsonValue.NewObject()
                    .Set("type", l.settlement.GetType().Name)
                    .Set("name", l.settlement.name)
                    .Set("shadow", Round2(l.settlement.shadow))
                    .Set("defences", Round2(l.settlement.defences))
                    .Set("isHuman", l.settlement.isHuman)
                    .Set("isInfiltrated", l.settlement.isInfiltrated);
                SettlementHuman sh = l.settlement as SettlementHuman;
                if (sh != null && sh.ruler != null) s.Set("ruler", PersonRef(sh.ruler));
                JsonValue subs = JsonValue.NewArray();
                if (l.settlement.subs != null)
                {
                    foreach (Subsettlement sub in l.settlement.subs)
                    {
                        subs.Add(SafeName(() => sub.getName()));
                    }
                }
                s.Set("subsettlements", subs);
                o.Set("settlement", s);
            }

            JsonValue props = JsonValue.NewArray();
            foreach (Property p in l.properties)
            {
                props.Add(JsonValue.NewObject()
                    .Set("type", p.GetType().Name)
                    .Set("name", SafeName(() => p.getName()))
                    .Set("charge", Round2(p.charge)));
            }
            o.Set("properties", props);
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
                .Set("awareness", Round2(p.awareness))
                .Set("sanity", Round2(p.sanity))
                .Set("maxSanity", p.maxSanity)
                .Set("level", p.level)
                .Set("skillPoints", p.skillPoints)
                .Set("stats", JsonValue.NewObject()
                    .Set("might", p.stat_might)
                    .Set("lore", p.stat_lore)
                    .Set("intrigue", p.stat_intrigue)
                    .Set("command", p.stat_command));

            JsonValue traits = JsonValue.NewArray();
            if (p.traits != null)
            {
                foreach (Trait t in p.traits)
                {
                    traits.Add(SafeName(() => t.getName()));
                }
            }
            o.Set("traits", traits);

            JsonValue items = JsonValue.NewArray();
            if (p.items != null)
            {
                foreach (Item it in p.items)
                {
                    if (it != null) items.Add(SafeName(() => it.getName()));
                }
            }
            o.Set("items", items);
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
                 .Set("isAlliance", soc.isAlliance);
                Location capital = Safe(() => soc.getCapital(), null);
                if (capital != null)
                {
                    o.Set("capital", LocationRef(capital));
                    SettlementHuman seat = capital.settlement as SettlementHuman;
                    if (seat != null && seat.ruler != null) o.Set("sovereign", PersonRef(seat.ruler));
                }
            }

            JsonValue relations = JsonValue.NewArray();
            foreach (KeyValuePair<SocialGroup, DipRel> kv in sg.relations)
            {
                if (kv.Key == sg || kv.Value == null) continue;
                relations.Add(JsonValue.NewObject()
                    .Set("with", SocialGroupRef(kv.Key))
                    .Set("state", kv.Value.state.ToString())
                    .Set("status", Round2(kv.Value.status)));
            }
            o.Set("relations", relations);
            return o;
        }

        // ---------- challenges ----------

        public static JsonValue ChallengeSummary(GameContext ctx, Challenge c, Unit forUnit)
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
            try { o.Set("description", c.getDesc()); } catch { }

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

        // ---------- helpers ----------

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
