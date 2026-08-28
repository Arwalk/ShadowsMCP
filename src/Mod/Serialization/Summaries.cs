using System;
using System.Collections.Generic;
using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Extensions;

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

        /// <summary>Deterministic, stable challenge id (unlike the old weak-registry "C8"): a pure function
        /// of the challenge's runtime type, its stable native <c>locationIndex</c>, and a hash of its display
        /// name (which embeds the target, disambiguating the per-hero duplicates at one settlement). Because
        /// it re-derives from persistent data it survives turns AND save/load, so a cached id keeps resolving
        /// via <see cref="ResolveChallengeForUnit"/>. Rituals key off the owning unit instead of a location
        /// ("Cr-..."). Still "C"-prefixed for compatibility. ctx is unused, kept for call symmetry.</summary>
        public static string ChallengeId(GameContext ctx, Challenge c)
        {
            if (c == null) return null;
            string type = c.GetType().Name;
            string name = SafeName(() => c.getName()) ?? "";
            // Market stalls: Sub_Market holds three Ch_BuyItem all named "Buy Item From Market", which
            // would collide into one id (and FindByCanonicalId would only ever resolve the first stall).
            // Salt with the item on sale — the offer's real identity: stable while that item is on sale,
            // correctly invalidated when the stall restocks (a cached id must never silently buy another
            // item). Two stalls selling identically-named items still collide, but those purchases are
            // interchangeable.
            if (c is Ch_BuyItem buy)
                name += "|" + (SafeName(() => buy.onSale != null ? buy.onSale.getName() : null) ?? "empty");
            string h = StableHash8(type + "|" + name);
            if (c is Ritual) return "Cr-" + type + "-" + h;
            int loc;
            try { loc = c.locationIndex; } catch { loc = -1; }
            return "C" + loc + "-" + type + "-" + h;
        }

        /// <summary>Public name reader for a challenge (used in the stale-id error listing).</summary>
        public static string ChallengeName(Challenge c) { return c == null ? null : SafeName(() => c.getName()); }

        /// <summary>FNV-1a 32-bit as 8 hex chars. Deterministic across processes/reloads, unlike
        /// <c>string.GetHashCode()</c> (which .NET randomizes per run) - required for a stable id.</summary>
        private static string StableHash8(string s)
        {
            uint hash = 2166136261u;
            if (s != null)
                foreach (char ch in s) { hash ^= ch; hash *= 16777619u; }
            return hash.ToString("x8");
        }

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
            {
                object hit = ctx.Registry.Resolve(id.ToUpperInvariant());
                if (hit != null) return hit;
                // Deterministic challenge ids ("C31-Ch_...-hash" / "Cr-...") are re-derived rather
                // than registered, so the registry never holds them - re-scan the game instead.
                return kind == 'C' ? (object)ResolveChallengeGlobal(ctx, id) : null;
            }

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

        /// <summary>A social group's display name for error messages, never throwing.</summary>
        public static string SafeDisplayName(SocialGroup sg)
        {
            return sg == null ? "<none>" : SafeName(() => sg.getName());
        }

        /// <summary>A holy tenet's display name, never throwing.</summary>
        public static string TenetName(HolyTenet t)
        {
            return t == null ? "<none>" : SafeName(() => t.getName());
        }

        /// <summary>A divine entity's display name, never throwing.</summary>
        public static string DivinityName(DivineEntity d)
        {
            return d == null ? "<none>" : SafeName(() => d.getName());
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

        /// <summary>An agent archetype you can enthrall (from overmind.agentsGeneric/agentsUnique).
        /// When <paramref name="placement"/> is supplied it is attached as a "placement" object saying
        /// where this archetype can actually be enthralled right now (see <see cref="PlacementSummary"/>).
        /// Unless the player enabled Discovery mode, an "abilities" preview of the rituals the archetype
        /// unlocks at recruitment (see <see cref="AbilityCatalog"/>) is attached so recruitment can be
        /// planned around prerequisites.</summary>
        public static JsonValue AbstractionSummary(GameContext ctx, UAE_Abstraction abstr, string category, JsonValue placement = null)
        {
            JsonValue o = JsonValue.NewObject()
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
            if (!ctx.Config.DiscoveryMode)
            {
                // Vanilla archetypes come from the hand-curated catalog (by CODE_*); a modded one can
                // supply its preview via its mod's MCP manifest, keyed by the UAE_* class name.
                ArchetypeAbilities cat = AbilityCatalog.Get(abstr.code)
                    ?? McpExtensions.AbilityPreview(abstr.GetType().Name);
                if (cat != null)
                {
                    JsonValue abilities = JsonValue.NewArray();
                    foreach (AbilityPreview a in cat.Rituals)
                        abilities.Add(JsonValue.NewObject()
                            .Set("name", a.Name)
                            .Set("desc", a.Desc)
                            .Set("prereq", a.Prereq));
                    o.Set("abilities", abilities);
                    if (cat.Note != null) o.Set("abilityNote", cat.Note);
                }
            }
            if (placement != null) o.Set("placement", placement);
            return o;
        }

        /// <summary>
        /// Up to <paramref name="max"/> locations where <paramref name="abstr"/> can be enthralled
        /// right now, per the game's own UAE_Abstraction.validTarget. Early-stops at max; a throwing
        /// validTarget is treated as "not a valid target" so one odd location can't break the scan.
        /// Note: validTarget also returns false for every archetype once the roster is at its agent
        /// cap, so results are only meaningful while capacity.canRecruit is true.
        /// </summary>
        public static List<Location> ValidTargets(Map map, UAE_Abstraction abstr, int max)
        {
            var hits = new List<Location>();
            if (map == null || abstr == null) return hits;
            foreach (Location l in map.locations)
            {
                bool ok;
                try { ok = abstr.validTarget(l); }
                catch { ok = false; }
                if (!ok) continue;
                hits.Add(l);
                if (hits.Count >= max) break;
            }
            return hits;
        }

        /// <summary>Where an archetype can actually be enthralled right now: an "eligible" flag plus a
        /// few example target locations. Turns the free-text restrictions into something an agent can
        /// act on instead of guessing. See <see cref="ValidTargets"/> for the agent-cap caveat.</summary>
        public static JsonValue PlacementSummary(Map map, UAE_Abstraction abstr, int maxExamples)
        {
            List<Location> targets = ValidTargets(map, abstr, maxExamples);
            JsonValue examples = JsonValue.NewArray();
            foreach (Location l in targets) examples.Add(LocationRef(l));
            return JsonValue.NewObject()
                .Set("eligible", targets.Count > 0)
                .Set("exampleTargets", examples);
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

        /// <summary>One static legend for the order entries UnitSummary emits without per-row hints
        /// (list views). Emitted once per response — the exact call templates the per-row hints used
        /// to repeat on every entry. get_unit keeps the full per-entry hints as the drill-in.</summary>
        public const string OrdersLegend =
            "orders: command_agent {unitId, order, targetUnitId} - attack=duel an enemy hero (compare both " +
            "dangerEstimates first; permanently cancels the target's in-progress challenge AND your own); " +
            "rob=steal items (needs higher level, once per 5 turns, raises your profile+menace); trade=move " +
            "items/gold between two of YOUR agents; follow=Harvester shadows a merchant. " +
            "command_army {unitId, order, targetUnitId?} - raze=destroy the settlement your army stands on; " +
            "drive_back=force a co-located hero to retreat and drop its task; attack=battle a co-located " +
            "enemy army.";

        public static JsonValue UnitSummary(GameContext ctx, Unit u, bool orderHints = true)
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
            // Special orders the game surfaces on a selected unit and that are neither challenges nor powers:
            // a UM's raze / drive back / attack (command_army) and an agent's on-tile attack / rob / trade /
            // follow (command_agent). Omitted (null) when no such order applies right now.
            JsonValue orders = UnitOrders(ctx, u, orderHints);
            if (!orders.IsNull) o.Set("orders", orders);
            // Your own agents' progression at list granularity (get_player_state.agents / list_units):
            // level everywhere, plus a skillPoints flag whenever a point is banked - the pick is
            // strategic (first level-up gates magic mastery, G16-#1; ANY pending pick now blocks
            // end_turn(force) unless the matching forceSpends* flag opts back in, G18-#4). Full XP
            // numbers live in get_unit.agent.
            UA uaSummary = u as UA;
            if (!dead && uaSummary != null && u.isCommandable() && uaSummary.person != null)
            {
                o.Set("level", uaSummary.person.level);
                if (uaSummary.person.skillPoints > 0)
                    o.Set("skillPoints", uaSummary.person.skillPoints);
            }
            // Active combat, surfaced in list views so an under-attack agent / in-battle army is visible without
            // a get_unit round-trip (agents were reaching combat via no other signal). engagedThisTurn is the
            // fight-icon condition (UIE_AgentRoster.bFight): a battle is pending — resolve it via
            // get_pending_decision. inBattle is an army fighting a multi-turn BattleArmy.
            if (!dead && u.engagedBy != null && u.map != null && u.turnLastEngaged == u.map.turn)
                o.Set("engagedThisTurn", true).Set("underAttackBy", UnitRef(ctx, u.engagedBy));
            if (!dead && u.task is Task_InBattle) o.Set("inBattle", true);
            if (dead) o.Set("isDead", true);
            return o;
        }

        /// <summary>The special orders available to <paramref name="u"/> on its current tile - the game's own
        /// action boxes (UIScroll_Unit), which are neither god powers nor challenges and so surface nowhere
        /// else: a military unit's Raze / Drive Back / Attack (command_army) and an agent's Attack / Rob /
        /// Trade / Follow against another agent standing on the same tile (command_agent). Each entry is
        /// {order, target, hint}; the hint spells out the exact call to make. Returns
        /// <see cref="JsonValue.Null"/> unless this is a live commandable unit with at least one order
        /// applicable right now.</summary>
        public static JsonValue UnitOrders(GameContext ctx, Unit u, bool includeHints = true)
        {
            if (u is UA) return AgentOrders(ctx, u as UA, includeHints);
            UM um = u as UM;
            if (um == null || um.isDead || !um.isCommandable()) return JsonValue.Null;
            // In battle every order pops "... while in battle" - offer none (mirrors UM.playerCommands*).
            if (um.task is Task_InBattle) return JsonValue.Null;

            JsonValue arr = JsonValue.NewArray();
            bool any = false;

            // Raze: the unit is standing on a human settlement (the only gate the UI applies).
            SettlementHuman razeTarget = um.location != null ? um.location.settlement as SettlementHuman : null;
            if (razeTarget != null)
            {
                JsonValue e = JsonValue.NewObject()
                    .Set("order", "raze")
                    .Set("target", LocationRef(um.location));
                if (includeHints)
                    e.Set("hint", "command_army {unitId:" + UnitId(ctx, um) + ", order:\"raze\"} razes " +
                        SafeName(() => razeTarget.getName()) + " - its defences fall each turn until it is destroyed");
                arr.Add(e);
                any = true;
            }

            // Drive back / attack: one entry per hostile unit sharing this tile (the UI iterates location.units).
            if (um.location != null && um.location.units != null)
            {
                foreach (Unit other in um.location.units)
                {
                    if (other == null || other.isDead) continue;
                    UA ua = other as UA;
                    if (ua != null && !ua.isCommandable())
                    {
                        JsonValue e = JsonValue.NewObject()
                            .Set("order", "drive_back")
                            .Set("target", UnitRef(ctx, ua));
                        if (IsOwnOrderUnit(ua))
                            e.Set("ownOrder", true)
                             .Set("ownOrderNote", "this is an acolyte of YOUR OWN faith (its order " +
                                 "worships you) - driving it back only hurts your own religion");
                        if (includeHints)
                            e.Set("hint", "command_army {unitId:" + UnitId(ctx, um) + ", order:\"drive_back\", targetUnitId:" +
                                UnitId(ctx, ua) + "} forces this hero to retreat and drop its task");
                        arr.Add(e);
                        any = true;
                    }
                    UM enemyArmy = other as UM;
                    if (enemyArmy != null && !enemyArmy.isCommandable() && enemyArmy.society != um.society)
                    {
                        JsonValue e = JsonValue.NewObject()
                            .Set("order", "attack")
                            .Set("target", UnitRef(ctx, enemyArmy));
                        if (includeHints)
                            e.Set("hint", "command_army {unitId:" + UnitId(ctx, um) + ", order:\"attack\", targetUnitId:" +
                                UnitId(ctx, enemyArmy) + "} starts a battle with this army");
                        arr.Add(e);
                        any = true;
                    }
                }
            }

            return any ? arr : JsonValue.Null;
        }

        /// <summary>The agent-vs-agent actions available to <paramref name="ua"/> right now - one entry per
        /// other agent on its tile, mirroring the action boxes UIScroll_Unit builds by walking
        /// <c>ua.location.units</c> (Attack an enemy hero, Rob a weaker merchant/adventurer, Trade with one of
        /// your own agents, Follow a merchant as a Harvester). Surfacing them here is what makes the offensive
        /// half of the agent layer discoverable: it rides along in list_units and get_unit, each hint carrying
        /// the literal command_agent call. Null when nothing applies.</summary>
        private static JsonValue AgentOrders(GameContext ctx, UA ua, bool includeHints = true)
        {
            if (ua == null || ua.isDead || !ua.isCommandable()) return JsonValue.Null;
            if (ua.location == null || ua.location.units == null) return JsonValue.Null;
            // Same suppressions as the command_agent guards: a pending duel or disruption blocks every order.
            if (ua.engagedBy != null && ua.map != null && ua.turnLastEngaged == ua.map.turn) return JsonValue.Null;
            if (ua.task is Task_Disrupted) return JsonValue.Null;

            string me = UnitId(ctx, ua);
            JsonValue arr = JsonValue.NewArray();
            bool any = false;

            foreach (Unit other in ua.location.units)
            {
                if (other == null || other.isDead || other == ua) continue;
                UA target = other as UA;
                if (target == null) continue;
                string tid = UnitId(ctx, target);

                if (!target.isCommandable())
                {
                    bool busy = target.engagedBy != null && ua.map != null && target.turnLastEngaged == ua.map.turn;
                    if (!busy)
                    {
                        // What attacking would break stays a DATA field in both modes: it is
                        // target-specific eligibility info a legend cannot carry.
                        string breaksTask = target.task is Task_PerformChallenge
                            ? SafeName(() => target.task.getShort()) : null;
                        JsonValue e = JsonValue.NewObject()
                            .Set("order", "attack")
                            .Set("target", UnitRef(ctx, target))
                            .Set("theirDangerEstimate", Safe(() => target.getDangerEstimate(), 0))
                            .Set("yourDangerEstimate", Safe(() => ua.getDangerEstimate(), 0));
                        // dangerEstimate hides minion screening (G16-#5) - surface both screens so a
                        // "favourable" duel against a high-defence front minion is visible as a wall.
                        JsonValue theirScreen = MinionScreen(target as UA);
                        JsonValue yourScreen = MinionScreen(ua);
                        if (!theirScreen.IsNull) e.Set("theirMinionScreen", theirScreen);
                        if (!yourScreen.IsNull) e.Set("yourMinionScreen", yourScreen);
                        if (breaksTask != null) e.Set("cancelsTheirTask", breaksTask);
                        // The game UI genuinely offers this attack, so the entry stays - but flag it.
                        if (IsOwnOrderUnit(target))
                            e.Set("ownOrder", true)
                             .Set("ownOrderNote", "this is an acolyte of YOUR OWN faith (its order " +
                                 "worships you) - attacking it only hurts your own religion");
                        if (includeHints)
                            e.Set("hint", "command_agent {unitId:" + me + ", order:\"attack\", targetUnitId:" + tid +
                                "} starts a duel with this hero" +
                                (breaksTask != null ? " and cancels their '" + breaksTask + "' for good (even if you flee)" : "") +
                                " - compare the two dangerEstimates AND the minionScreen blocks first: a " +
                                "screening front minion can blank a low-attack agent entirely (see get_tips " +
                                "id=disrupting_skirmish)");
                        arr.Add(e);
                        any = true;
                    }

                    // Rob: merchant/adventurer, and you must outrank them (UA.playerTriesToRob).
                    if ((target is UAG || target is UAA) && target.person != null && ua.person != null &&
                        target.person.level < ua.person.level &&
                        (ua.map == null || ua.turnLastDidRobbery == 0 || ua.map.turn - ua.turnLastDidRobbery >= 5))
                    {
                        JsonValue e = JsonValue.NewObject()
                            .Set("order", "rob")
                            .Set("target", UnitRef(ctx, target));
                        if (includeHints)
                            e.Set("hint", "command_agent {unitId:" + me + ", order:\"rob\", targetUnitId:" + tid +
                                "} steals their items (raises your profile and menace; once per 5 turns)");
                        arr.Add(e);
                        any = true;
                    }

                    if (ua is UAE_Harvester && target is UAG)
                    {
                        JsonValue e = JsonValue.NewObject()
                            .Set("order", "follow")
                            .Set("target", UnitRef(ctx, target));
                        if (includeHints)
                            e.Set("hint", "command_agent {unitId:" + me + ", order:\"follow\", targetUnitId:" + tid +
                                "} shadows this merchant wherever they go");
                        arr.Add(e);
                        any = true;
                    }
                }
                else
                {
                    JsonValue e = JsonValue.NewObject()
                        .Set("order", "trade")
                        .Set("target", UnitRef(ctx, target));
                    if (includeHints)
                        e.Set("hint", "command_agent {unitId:" + me + ", order:\"trade\", targetUnitId:" + tid +
                            "} moves items and gold between these two agents of yours");
                    arr.Add(e);
                    any = true;
                }
            }

            return any ? arr : JsonValue.Null;
        }

        public static JsonValue UnitDetail(GameContext ctx, Unit u)
        {
            JsonValue o = UnitSummary(ctx, u)
                .Set("menace", Round2(u.menace))
                .Set("profile", Round2(u.profile))
                .Set("person", u.person != null ? PersonSummary(ctx, u.person) : JsonValue.Null)
                .Set("taskDetail", u.isDead ? JsonValue.Null : TaskDetail(ctx, u.task, u))
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
                JsonValue agent = JsonValue.NewObject()
                    .Set("corrupted", ua.corrupted)
                    .Set("attack", Safe(() => ua.getStatAttack(), 0))
                    .Set("challengesSinceRest", ua.challengesSinceRest)
                    .Set("turnsIdle", ua.turnsIdle)
                    .Set("disruptionExhaustion", ua.disruptionExhaustion)
                    .Set("minions", minions);
                // Level/XP/skill points were only reachable via inspect (G14-#6), yet they gate the
                // magic-mastery pick (first level-up) and, with forceSpendsRegularTraits:true,
                // end_turn(force) auto-spends banked points.
                if (ua.person != null)
                {
                    agent.Set("level", ua.person.level)
                        .Set("xp", ua.person.XP)
                        .Set("xpForNextLevel", ua.person.XPForNextLevel)
                        .Set("skillPoints", ua.person.skillPoints);
                    if (ua.person.skillPoints > 0)
                        agent.Set("skillPointNote", "unspent skill point(s): the next end_turn blocks " +
                            "with the level-up popup - answer it to pick the trait yourself (this " +
                            "holds even under force, forceDenied:\"traitPick\"/\"startingTraitPick\"; " +
                            "only forceSpendsRegularTraits:true lets force auto-spend a regular pick " +
                            "on an AI-picked trait)");
                }
                o.Set("agent", agent);

                // Combat readiness for risk management. dangerEstimate is the strength number the engine
                // compares unit-vs-unit (UA.getDangerEstimate: hp + defence + attack + minions) — but it
                // SUMS minions into one fungible scalar and hides screening: a favourable-looking number
                // can still lose to a high-defence front minion (G16-#5). Read minionScreen on both sides
                // alongside it. isHuntable gates ONLY the ruler-ordered escorted hunt
                // (SettlementHuman.cs:432: profile >= 50 AND menace > 25); the independent hero AI
                // needs no gate at all (G17-#6 - see huntNote below).
                JsonValue combat = JsonValue.NewObject()
                    .Set("dangerEstimate", Safe(() => ua.getDangerEstimate(), 0))
                    .Set("hp", u.hp)
                    .Set("defence", Safe(() => ua.getMaxDefence(), 0))
                    .Set("attack", Safe(() => ua.getStatAttack(), 0))
                    .Set("menace", Round2(u.menace))
                    .Set("profile", Round2(u.profile))
                    // menace/profile ratchet up a floor they can never fall below (Unit.addMenace/addProfile):
                    // a high floor means the exposure is permanent - plan around it (Lay Low / In Hiding
                    // only bleed down TO the floor; nothing in the live game lowers the floor itself).
                    .Set("menaceFloor", Round2(u.inner_menaceMin))
                    .Set("profileFloor", Round2(u.inner_profileMin))
                    // early-warning belt mirrored from Overmind.getThreats (dist <= profile/5); hero AI's
                    // actual vision is the tighter profile/10 (UA.getVisibleUnits), ruler hunts are uncapped
                    .Set("huntRadius", (int)(u.profile / 5.0))
                    .Set("isHuntable", u.profile >= 50.0 && u.menace > 25.0)
                    .Set("huntNote", "isHuntable (profile>=50 & menace>25) gates only RULER-ordered " +
                        "escorted hunts; independent heroes attack on menace alone - worthwhile for " +
                        "them from roughly menace > 30 + step distance - whenever this unit is within " +
                        "profile/10 hexes of them. get_threats.agentSafety has the live per-hunter odds.")
                    .Set("inHiding", ua.task is Task_InHiding);
                JsonValue screen = MinionScreen(ua);
                if (!screen.IsNull) combat.Set("minionScreen", screen);
                o.Set("combat", combat);

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

            // Army in a multi-turn field battle (Task_InBattle → BattleArmy): the read-only "See Battle" view -
            // who is fighting, the command advantage, effects, and this cycle's combat log. Army battles
            // auto-resolve one cycle per turn; you influence them via command_army or by commanding as a hero.
            if (u.task is Task_InBattle inBattle && inBattle.battle != null)
                o.Set("battle", ArmyBattleJson(ctx, inBattle.battle));

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

        /// <summary>Read-only summary of an army field battle (<see cref="BattleArmy"/>) — the data
        /// <c>PopupBattleArmy</c> renders: the two sides (armies + hero commanders with hp), the command
        /// advantage (positive favours the attackers), any battle effects, and this cycle's combat log. Army
        /// battles auto-resolve one cycle per turn; the player influences them only via command_army orders or
        /// by commanding as a hero (challenges), so this is view-only.</summary>
        public static JsonValue ArmyBattleJson(GameContext ctx, BattleArmy b)
        {
            if (b == null) return JsonValue.Null;
            double adv = Safe(() => b.computeAdvantage(), 0.0); // engine range -2..2
            JsonValue o = JsonValue.NewObject()
                .Set("done", b.done)
                .Set("note", "committed: auto-resolves one cycle per end_turn; no retreat. To sway it, move a " +
                    "hero/agent onto the battle's tile and perform 'Command Battle (Attacking)' / 'Command Battle " +
                    "(Defending)' - they appear in list_challenges only for a unit co-located with the battle")
                // Signed percent the human sees on PopupBattleArmy (computeAdvantage * 100); sign = who leads.
                .Set("commandAdvantagePct", Round2(adv * 100.0))
                .Set("advantageFavours", adv > 0 ? "attackers" : (adv < 0 ? "defenders" : "neither"))
                .Set("attackers", ArmyList(ctx, b.attackers))
                .Set("defenders", ArmyList(ctx, b.defenders))
                .Set("attackerCommanders", CommanderList(ctx, b.attComs))
                .Set("defenderCommanders", CommanderList(ctx, b.defComs));
            if (b.attEffect != null) o.Set("attackerEffect", SafeName(() => b.attEffect.getName()));
            if (b.defEffect != null) o.Set("defenderEffect", SafeName(() => b.defEffect.getName()));
            if (b.messages != null && b.messages.Count > 0)
            {
                JsonValue log = JsonValue.NewArray();
                foreach (string m in b.messages) log.Add(JsonValue.Of(StripRichText(m)));
                o.Set("log", log);
            }
            return o;
        }

        private static JsonValue ArmyList(GameContext ctx, List<UM> units)
        {
            JsonValue arr = JsonValue.NewArray();
            if (units != null)
                foreach (UM um in units)
                {
                    if (um == null) continue;
                    arr.Add(JsonValue.NewObject()
                        .Set("id", UnitId(ctx, um))
                        .Set("name", SafeName(() => um.getName()))
                        .Set("hp", um.hp)
                        .Set("maxHp", um.maxHp));
                }
            return arr;
        }

        private static JsonValue CommanderList(GameContext ctx, List<UA> coms)
        {
            JsonValue arr = JsonValue.NewArray();
            if (coms != null)
                foreach (UA ua in coms)
                    if (ua != null) arr.Add(UnitRef(ctx, ua));
            return arr;
        }

        /// <summary>Strip Unity rich-text tags (e.g. &lt;color=#aaaaaaff&gt;…&lt;/color&gt;) from a battle log line.
        /// Internal so ObserverCapture can reuse it on the same message stream.</summary>
        internal static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        }

        private static JsonValue TaskBrief(Task t)
        {
            if (t == null) return JsonValue.Null;
            string desc;
            try { desc = t.getShort(); }
            catch { desc = t.GetType().Name; }
            return JsonValue.Of(desc);
        }

        private static JsonValue TaskDetail(GameContext ctx, Task t, Unit owner = null)
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
                // Progress and its target in ONE payload: the completion check is
                // progress >= ceil(live complexity) each tick, so show the live value
                // and the remaining ETA at this unit's rate (G19-#3).
                double cx = Safe(() => pc.challenge.getComplexityAfterDifficulty(), 0.0);
                bool indef = Safe(() => pc.challenge.isIndefinite(), false);
                if (!indef && cx > 0.0)
                {
                    o.Set("complexity", Round2(cx));
                    double ppt = 0.0;
                    if (owner is UA oua) ppt = Safe(() => pc.challenge.getProgressPerTurn(oua, null), 0.0);
                    else if (owner is UM oum) ppt = Safe(() => pc.challenge.getProgressPerTurn(oum, null), 0.0);
                    if (ppt > 0.0)
                        o.Set("etaTurnsRemaining", (int)Math.Ceiling(Math.Max(0.0, cx - pc.progress) / ppt));
                }
                string secNote = SecurityComplexityNote(pc.challenge);
                if (secNote != null) o.Set("complexityNote", secNote);
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
                    // NOT the game's raw isInfiltrated bool: that flag means an orc-style whole-settlement
                    // takeover and is never set by human-city infiltration, so it read as a contradiction
                    // next to infiltration==1.0. fullyInfiltrated is derived from the fraction instead.
                    .Set("fullyInfiltrated", Safe(() => st.infiltration >= 1.0, false))
                    // Fraction of infiltratable sub-districts infiltrated (0..1); 1.0 == fully infiltrated.
                    // Several challenges (Enshadow, Desecrate) gate on this reaching 1.0.
                    .Set("infiltration", Round2(Safe(() => st.infiltration, 0.0)));
                // Security is the live input to the Infiltrate/Vault/Assassinate complexity formulas
                // (e.g. Infiltrate = 50 + 25/point) - without it the agent can't see why a challenge's
                // complexity moved (G19-#3). getSecurity is a pure read (sums property/ruler terms).
                s.Set("security", Safe(() => st.getSecurity(null), 0));
                try
                {
                    List<ReasonMsg> secReasons = new List<ReasonMsg>();
                    st.getSecurity(secReasons);
                    if (secReasons.Count > 0) s.Set("securityInfluences", InfluenceList(secReasons));
                }
                catch { }
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
                        // Menace when non-zero - and ALWAYS for a Heart of the Forest, so the
                        // countdown that gets a Heart razed is visible from turn one (G17-#5).
                        double subMenace = Safe(() => sub.menace, 0.0);
                        if (subMenace != 0.0 || sub is Sub_Vinerva_HeartOfForest)
                            sv.Set("menace", Round2(subMenace));
                        subs.Add(sv);
                    }
                }
                s.Set("subsettlements", subs);
                o.Set("settlement", s);
            }

            // Duplicate instances of one property type are REAL game state, not a serialization
            // bug: the engine appends without a same-type check (e.g. every Brutal Crackdown adds
            // its own Pr_LingeringResentment) and each instance ticks, decays, and contributes its
            // influences independently - the vanilla UI shows the same duplicates (G18-#5). Mirror
            // the list 1:1 and annotate stacks instead of merging, which would misreport totals.
            Dictionary<string, int> typeCounts = new Dictionary<string, int>();
            foreach (Property p in l.properties)
            {
                string t = p.GetType().Name;
                int n;
                typeCounts.TryGetValue(t, out n);
                typeCounts[t] = n + 1;
            }
            JsonValue props = JsonValue.NewArray();
            HashSet<string> stackNoted = new HashSet<string>();
            foreach (Property p in l.properties)
            {
                string t = p.GetType().Name;
                JsonValue pv = JsonValue.NewObject()
                    .Set("type", t)
                    .Set("name", SafeName(() => p.getName()))
                    .Set("charge", Round2(p.charge));
                if (typeCounts[t] > 1)
                {
                    pv.Set("stackCount", typeCounts[t]);
                    if (stackNoted.Add(t))
                        pv.Set("stackNote", typeCounts[t] + " independent instances of this modifier " +
                            "are here - each keeps its own charge and applies its own influences every " +
                            "turn (their per-turn effects ADD UP). An instance at charge 0 was just " +
                            "zeroed (e.g. by an Unrest crisis) and is culled next turn.");
                }
                if (p.influences != null && p.influences.Count > 0)
                    pv.Set("influences", InfluenceList(p.influences));
                // Bribed Guards decays 1 charge/turn and the engine culls it below 0.1 with no
                // message - the -2 security discount (and the lower Infiltrate-family complexity it
                // bought) vanishes silently mid-challenge (G19-#3).
                if (p is Pr_BribedGuards)
                    pv.Set("turnsRemaining", (int)Math.Ceiling(Math.Max(0.0, p.charge - 0.1)))
                      .Set("lapseNote", "decays 1 charge/turn and is removed SILENTLY below 0.1 - " +
                          "its -2 security discount lapses with it, raising Infiltrate-family " +
                          "complexity mid-challenge with no event");
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
            // currentMilitary/maxMilitary are otherwise only recomputed on the game's turn tick;
            // the side panel (UILeftLocation.setTo) recomputes right before display, so match it.
            try { sg.computeMilitary(); } catch { }
            JsonValue o = JsonValue.NewObject()
                .Set("id", SocialGroupId(sg))
                .Set("name", SafeName(() => sg.getName()))
                .Set("type", sg.GetType().Name)
                .Set("isPlayerFaction", map != null && sg == (SocialGroup)map.soc_dark)
                .Set("locationCount", locationCount)
                .Set("military", JsonValue.NewObject()
                    .Set("current", Round2(sg.currentMilitary))
                    .Set("max", Round2(sg.maxMilitary)));

            // The side panel's "Risk of Attack": highest motivation (0..1) of any nation to attack
            // this group. Overmind.getThreats maintains it only for groups the dark player has a
            // stake in (infiltrated/watched orc camps, Deep Ones, the Dark Empire); omitted at 0,
            // matching the panel's visibility gate.
            if (sg.data_highestAttackThreat > 0.0)
                o.Set("riskOfAttack", Round2(sg.data_highestAttackThreat));

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
            if (ho != null) o.Set("holyOrder", HolyOrderBlock(ctx, ho, detail: true));

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

        /// <summary>Resolve a (deterministic) challenge id for a specific commandable unit. Recomputes the
        /// canonical id over the unit's reachable challenges (the id's encoded location, plus the unit's own
        /// tile) and its rituals, matching by string. Returns null only when the challenge is genuinely gone.
        /// Lives here (not in the generic ResolveId) because rituals need the performing unit as context:
        /// a ritual id has no unit component, so the same "Cr-..." id names each unit's own instance — the
        /// performing unit's copy must win (a global scan could return another unit's instance).</summary>
        public static Challenge ResolveChallengeForUnit(GameContext ctx, Unit unit, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            Map map = ctx != null ? ctx.Map : null;
            if (map == null) return null;

            if (id.StartsWith("Cr-", StringComparison.OrdinalIgnoreCase))
            {
                // Ritual id: only the performing unit owns it (incl. rituals granted by carried items).
                Challenge r = FindByCanonicalId(ctx, RitualsFor(unit), id);
                if (r != null) return r;
            }
            else
            {
                Challenge hit = ResolveLocationChallengeId(ctx, map, id);
                if (hit != null) return hit;
            }

            // Last resort: the unit's own tile + rituals (covers an id whose encoded location changed).
            if (unit != null)
            {
                Location ul = unit.location;
                if (ul != null)
                {
                    try { ul.populateStandardChallenges(); } catch { }
                    Challenge hit = FindByCanonicalId(ctx, SafeChallenges(ul), id);
                    if (hit != null) return hit;
                }
                Challenge rit = FindByCanonicalId(ctx, RitualsFor(unit), id);
                if (rit != null) return rit;
            }
            return null;
        }

        /// <summary>Resolve a deterministic challenge id WITHOUT a performing-unit context (used by the
        /// generic ResolveId, e.g. for inspect roots). Location ids ("C{idx}-...") re-scan the encoded
        /// location; ritual ids ("Cr-...") carry no location, so every commandable unit's rituals are
        /// scanned instead (rituals are per-unit and player units are few).</summary>
        public static Challenge ResolveChallengeGlobal(GameContext ctx, string id)
        {
            Map map = ctx != null ? ctx.Map : null;
            if (map == null || string.IsNullOrEmpty(id)) return null;

            if (id.StartsWith("Cr-", StringComparison.OrdinalIgnoreCase))
            {
                if (map.units == null) return null;
                foreach (Unit u in map.units)
                {
                    if (u == null || !SafeIsCommandable(u)) continue;
                    Challenge rit = FindByCanonicalId(ctx, RitualsFor(u), id);
                    if (rit != null) return rit;
                }
                return null;
            }
            return ResolveLocationChallengeId(ctx, map, id);
        }

        private static bool SafeIsCommandable(Unit u)
        {
            try { return u.isCommandable(); } catch { return false; }
        }

        /// <summary>True for a challenge meant for HEROES, not the dark player (isGoodTernary()==1):
        /// the game UI hides these from an agent entirely — performing one (e.g. Combat Banditry)
        /// would undo the player's own work.</summary>
        public static bool IsHeroOnly(Challenge c)
        {
            try { return c != null && c.isGoodTernary() == 1; } catch { return false; }
        }

        /// <summary>True for a unit that belongs to the player's own faith: a UAA acolyte whose holy
        /// order worships the player (only HolyOrder_Ophanim sets <c>worshipsThePlayer</c>; witch
        /// acolytes stay neutral). Such a unit is not commandable but is NOT an enemy - never list
        /// it among hostiles by default (G19-#5).</summary>
        public static bool IsOwnOrderUnit(Unit u)
        {
            try
            {
                return u is UAA uaa && uaa.order != null && uaa.order.worshipsThePlayer;
            }
            catch { return false; }
        }

        /// <summary>True when this challenge's type actually implements <c>validFor(UM)</c> rather than
        /// inheriting the base default (which returns true for everything). The game UI never offers
        /// location challenges to an army, so only explicit overrides are genuinely army-usable.</summary>
        public static bool OverridesValidForUM(Challenge c)
        {
            if (c == null) return false;
            try
            {
                var m = c.GetType().GetMethod("validFor", new[] { typeof(UM) });
                return m != null && m.DeclaringType != typeof(Challenge);
            }
            catch { return false; }
        }

        public static string SafeItemName(Item it)
        {
            try { return it != null ? it.getName() : null; } catch { return null; }
        }

        /// <summary>Every ritual this unit can perform: its own <c>rituals</c> list PLUS rituals granted
        /// by carried items (Laughing Tome, Horde Banner, personal items…). The game UI merges
        /// <c>person.items[i].getRituals(ua)</c> into the challenge list (UIScroll_Unit) but the engine
        /// never copies them into <c>unit.rituals</c> — reading only that field made item rituals
        /// invisible and unstartable through the MCP.</summary>
        public static IEnumerable<Challenge> RitualsFor(Unit u)
        {
            if (u == null) yield break;
            if (u.rituals != null)
                foreach (Challenge r in u.rituals)
                    if (r != null) yield return r;
            UA ua = u as UA;
            if (ua == null || ua.person == null || ua.person.items == null) yield break;
            foreach (Item it in ua.person.items)
            {
                if (it == null) continue;
                List<Ritual> granted;
                try { granted = it.getRituals(ua); } catch { continue; }
                if (granted == null) continue;
                foreach (Ritual r in granted)
                    if (r != null) yield return r;
            }
        }

        /// <summary>Scan the location encoded in a "C{idx}-..." id for a challenge matching it.</summary>
        private static Challenge ResolveLocationChallengeId(GameContext ctx, Map map, string id)
        {
            int dash = id.IndexOf('-');
            int idx;
            if (dash > 1 && (id[0] == 'C' || id[0] == 'c') &&
                int.TryParse(id.Substring(1, dash - 1), out idx))
            {
                Location loc = LocationByIndex(map, idx);
                if (loc != null)
                {
                    try { loc.populateStandardChallenges(); } catch { }
                    return FindByCanonicalId(ctx, SafeChallenges(loc), id);
                }
            }
            return null;
        }

        private static Challenge FindByCanonicalId(GameContext ctx, IEnumerable<Challenge> challenges, string id)
        {
            if (challenges == null) return null;
            foreach (Challenge c in challenges)
            {
                if (c == null) continue;
                if (string.Equals(ChallengeId(ctx, c), id, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        private static Location LocationByIndex(Map map, int idx)
        {
            if (map == null || map.locations == null) return null;
            foreach (Location l in map.locations) if (l != null && l.index == idx) return l;
            return null;
        }

        private static IEnumerable<Challenge> SafeChallenges(Location loc)
        {
            try { return loc.GetChallenges(); } catch { return null; }
        }

        public static JsonValue ChallengeSummary(GameContext ctx, Challenge c, Unit forUnit, bool includeDescription = true)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("id", ChallengeId(ctx, c))
                .Set("name", SafeName(() => c.getName()))
                .Set("type", c.GetType().Name)
                .Set("isRitual", c is Ritual);
            // A ritual acts wherever its carrier stands; its stored location is a dead placeholder the
            // game never reads (item rituals are constructed against map.locations[0]) — emitting it sent
            // agents marching to an irrelevant hex. Location challenges keep their real location.
            if (c is Ritual)
                o.Set("performsAt", "the unit's current location (rituals are performed in place, wherever the carrier stands)");
            else
                o.Set("location", LocationRef(c.location));
            // SafeValid, not c.valid(): Ch_PlagueShips.valid() spreads plague as a side effect,
            // so a plain valid() probe here made list_challenges mutate the world.
            o.Set("valid", SafeValid(c))
                // The heat the unit actually receives on completion (what Task_PerformChallenge applies
                // and the in-game UI shows) — NOT Challenge.getMenace()/getProfile(), which are the
                // engine AI's utility-scoring inputs and can be negative or wildly off from applied heat.
                .Set("menaceGain", Safe(() => c.getCompletionMenaceAfterDifficulty(), 0))
                .Set("profileGain", Safe(() => c.getCompletionProfile(), 0))
                .Set("danger", Safe(() => c.getDanger(), 0))
                .Set("claimedBy", UnitRef(ctx, c.claimedBy));
            bool indefinite = Safe(() => c.isIndefinite(), false);
            if (indefinite)
            {
                o.Set("indefinite", true);
                o.Set("heatNote", "indefinite challenge: menaceGain/profileGain are one-time completion "
                    + "values (often 0); its real effect is applied per turn while active and is stated "
                    + "in 'description' (e.g. Lay Low REDUCES menace and profile each turn).");
            }
            // Channelled casts pay their whole heat bill up front (Task_PerformChallenge applies the
            // completion menace/profile on the FIRST casting tick and skips it at completion) — without
            // this note the listed gains read like completion costs an interrupt would avoid.
            if (Safe(() => c.isChannelled(), false))
            {
                o.Set("channelled", true);
                o.Set("heatNote", "channelled: the listed menaceGain/profileGain are applied IN FULL on "
                    + "the first turn of casting (interrupting or abandoning does NOT spare them), and "
                    + "nothing further is applied on completion.");
            }
            // Lay Low's speed is location-dependent but neither its restriction nor its description says
            // so — hand-written note (AbilityCatalog precedent), always emitted even in terse mode
            // because it is the actionable line. progressPerTurn/progressBreakdown below carry the
            // actual numbers for THIS unit at THIS location.
            if (c is Ch_LayLow)
                o.Set("locationNote", "Lay Low's speed depends on WHERE you lay low: the base reduction "
                    + "is added again for each of - settlement infiltration >= 50%, location shadow >= 50%, "
                    + "and (Ophanim god only) 100% Ophanim faith here. progressPerTurn is the actual "
                    + "per-turn menace AND profile reduction at this location (progressBreakdown names the "
                    + "active boosts); an infiltrated or enshadowed settlement can be 2-4x faster than an "
                    + "untouched one. A HUMAN city never stops offering Lay Low (elven and dwarven "
                    + "cities are not 'cities' in code - they offer NEITHER Lay Low variant), but an "
                    + "army AT REST here deals "
                    + "1 HP per turn to an agent with menace >= " + c.map.param.ua_armyBlockMenace
                    + " and profile >= " + c.map.param.ua_armyBlockProfile + " performing ANY challenge "
                    + "(waived at 100% infiltration, or if the army's home city is >50% shadow). To lay "
                    + "low out of an army's reach, use the variant 'Lay Low (Wilderness)' - offered ONLY "
                    + "at: orc camps, ancient-ruins sites (every ruin-type minor site has one, but human "
                    + "minor VILLAGES never do), witch covens, temples of the WITCH faith only (ordinary "
                    + "temples and Holy Sites do NOT offer it), and deep-one cities/sanctums. Empty "
                    + "wilderness hexes with no settlement offer NO challenges at all - don't go there.");
            else if (c is Ch_LayLowWilderness)
                o.Set("locationNote", "Wilderness Lay Low runs at a base rate, doubled only if the "
                    + "settlement here is 100% infiltrated; progressPerTurn is the actual rate at this "
                    + "location. It exists ONLY at: orc camps, ancient-ruins sites (every ruin-type "
                    + "minor site has one, but human minor VILLAGES never do), witch covens, temples of "
                    + "the WITCH faith only (ordinary temples and Holy Sites do NOT offer it), and "
                    + "deep-one cities/sanctums - never on empty hexes.");
            // The description's "leading to your victory" is via VICTORY POINTS (insane rulers/heroes
            // score), NOT via the Iastur's Soul modifier - which no game code ever raises. Without this
            // note the vanilla text sends players grinding a meter that cannot move (G14-#12/#17).
            else if (c is Ch_WavesOfMadness)
                o.Set("outcomeNote", "each completed wave drives the nearest portion of rulers/heroes "
                    + "insane, which scores standard VICTORY POINTS (double if also enshadowed). It does "
                    + "NOT raise the Iastur's Soul modifier at the Tomb - despite the game's own text, "
                    + "nothing raises that meter; it is a loss meter only (see get_tips id=iastur_soul).");
            // Laughing Tome challenges: the summon's vanilla restriction ("a hero is currently binding
            // the tome") is TRUE exactly when some unit is running Ch_BindTome, but nothing lets the
            // player verify that — game 13 concluded the restriction was wrong and the deployed tome
            // unrecoverable (Collect Tome, on the INERT property only, was found by accident). Attach
            // the tome's observable state to every tome-related challenge entry.
            JsonValue tomeStatus = JsonValue.Null;
            if (c is Ch_SummonLaughingTome || c is Ch_ForciblySummonLaughingTome ||
                c is Ch_CollectTome || c is Ch_BindTome)
            {
                tomeStatus = LaughingTomeStatus(ctx, c.map);
                if (!tomeStatus.IsNull) o.Set("tomeStatus", tomeStatus);
            }
            // Why the challenge is locked / what it needs — the game's own hint text (getRestriction).
            // `valid` is the challenge's WORLD precondition (most types hard-code `return true`, e.g.
            // Ch_LayLow); the location/settlement preconditions (infiltration %, shadow %, ward,
            // settlement type) are checked inside validFor(unit) because the unit carries its location —
            // so `validForUnit: false` usually means "not here / not yet", not "wrong unit type".
            // `restriction` states the actual requirement (e.g. "Requires 100% Infiltration. Cannot
            // perform if Ward is higher than 50%"). Combine it with the location's shadow /
            // infiltration / ward (get_location or world_summary) to see which condition is unmet.
            try
            {
                string restriction = c.getRestriction();
                if (!string.IsNullOrEmpty(restriction)) o.Set("restriction", restriction);
            }
            catch { }
            // Per-clause decomposition of `valid` where an evaluator exists (Ch_PlagueShips):
            // states which clause is unmet and its actual value, since the prose restriction
            // re-states all clauses indistinguishably. Emitted even in terse mode - like
            // locationNote, it is the actionable line.
            JsonValue clauseReqs = ChallengeRequirements(c);
            if (!clauseReqs.IsNull) o.Set("requirements", clauseReqs);
            // The vanilla restriction ("shadow > 10% and Well of Shadows modifier < 100%") omits that
            // the challenge is constructed against a HUMAN SETTLEMENT and simply never exists elsewhere -
            // a playtest marched an agent to a 100%-shadow ruin on the strength of that text (G14-#19).
            if (c is Ch_WellOfShadows)
            {
                const string wellNote = "Note: only exists at populated human settlements - ruins, " +
                    "wilderness hexes and non-human sites never offer it, whatever their shadow";
                string existingR = o["restriction"].AsString();
                o.Set("restriction", string.IsNullOrEmpty(existingR) ? wellNote : existingR + ". " + wellNote);
            }
            // Exclusive challenge actively in use by ANOTHER unit (the exact predicate perform_challenge
            // rejects on): surface it in restriction so "only one at a time" is visible before the call,
            // not just as an error after it. claimedBy alone proved too easy to overlook (game 7's agent
            // hit the Lay Low rejection blind).
            try
            {
                if (!c.allowMultipleUsers() && c.claimedBy != null && c.claimedBy != forUnit &&
                    c.claimedBy.location == c.location &&
                    c.claimedBy.task is Task_PerformChallenge activeClaim && activeClaim.challenge == c)
                {
                    string inUse = "currently being performed by " + c.claimedBy.getName() +
                        " - only one unit may perform this at a time";
                    string existing = o["restriction"].AsString();
                    o.Set("restriction", string.IsNullOrEmpty(existing) ? inUse : existing + ". " + inUse);
                }
            }
            catch { }
            // Make the summon's binding restriction provable: name the binder in the restriction
            // itself, so "Cannot be used if a hero is currently binding the tome" reads as a checked
            // fact instead of a wrong guess (tomeStatus carries the structured version).
            if (c is Ch_SummonLaughingTome && tomeStatus["state"].AsString() == "beingBound")
            {
                string binder = tomeStatus["unit"]["name"].AsString() ?? "a hero";
                string binderLoc = tomeStatus["location"]["name"].AsString();
                string now = "right now: being bound by " + binder +
                    (binderLoc != null ? " at " + binderLoc : "") + " - interrupt the binder or wait";
                string existing = o["restriction"].AsString();
                o.Set("restriction", string.IsNullOrEmpty(existing) ? now : existing + ". " + now);
            }
            // Market stalls: always show the wares (even terse) — the three stalls share one display
            // name, so without this an agent cannot tell the offers apart or choose between them.
            if (c is Ch_BuyItem stall && stall.onSale != null)
            {
                JsonValue item = JsonValue.NewObject()
                    .Set("name", SafeName(() => stall.onSale.getName()));
                try { item.Set("desc", stall.onSale.getShortDesc()); } catch { }
                o.Set("itemForSale", item);
            }
            // Security-scaled challenges recompute complexity live every read; emitted even in
            // terse mode - like locationNote, it is the actionable line (G19-#3).
            string secNote = SecurityComplexityNote(c);
            if (secNote != null) o.Set("complexityNote", secNote);
            if (includeDescription)
            {
                try { o.Set("description", c.getDesc()); } catch { }
            }

            UA ua = forUnit as UA;
            if (ua != null)
            {
                o.Set("validForUnit", Safe(() => c.validFor(ua), false));
                // One getProgressPerTurn call feeds both the number and its itemized breakdown
                // (base + stat + trait/item boosts) — the rate is unit-relative (stat-scaled), so
                // the same complexity can run 7x faster for one agent than another. The breakdown
                // rides the terse knob (includeDescription) to keep list output cheap.
                List<ReasonMsg> reasons = includeDescription ? new List<ReasonMsg>() : null;
                double ppt = Safe(() => c.getProgressPerTurn(ua, reasons), 0.0);
                double cx = Safe(() => c.getComplexityAfterDifficulty(), 0.0);
                o.Set("progressPerTurn", Round2(ppt));
                o.Set("complexity", Round2(cx));
                // Turns of ACTIVE work at the current rate (travel excluded); meaningless for
                // indefinites, absent when the rate is 0.
                if (!indefinite && ppt > 0.0)
                    o.Set("etaTurns", (int)Math.Ceiling(cx / ppt));
                if (reasons != null && reasons.Count > 0)
                {
                    JsonValue br = JsonValue.NewArray();
                    try
                    {
                        foreach (ReasonMsg m in reasons)
                            br.Add(JsonValue.NewObject().Set("reason", m.msg).Set("value", Round2(m.value)));
                    }
                    catch { }
                    if (br.Count > 0) o.Set("progressBreakdown", br);
                }
            }
            UM um = forUnit as UM;
            if (um != null)
            {
                o.Set("validForUnit", Safe(() => c.validFor(um), false));
                double ppt = Safe(() => c.getProgressPerTurn(um, null), 0.0);
                double cx = Safe(() => c.getComplexityAfterDifficulty(), 0.0);
                o.Set("progressPerTurn", Round2(ppt));
                o.Set("complexity", Round2(cx));
                if (!indefinite && ppt > 0.0)
                    o.Set("etaTurns", (int)Math.Ceiling(cx / ppt));
            }
            return o;
        }

        /// <summary>Volatility warning for the security-scaled challenge family (Infiltrate,
        /// Access Vault, Assassinate): their getComplexity re-reads the settlement's LIVE
        /// security on every call and challenge progress is compared against that live ceiling
        /// each tick (Task_PerformChallenge), so a lapsing discount (Bribed Guards) silently
        /// re-inflates an in-progress challenge (G19-#3). Null for every other type.</summary>
        public static string SecurityComplexityNote(Challenge c)
        {
            int perPoint;
            if (c is Ch_InfiltrateSimplified) perPoint = 7;
            else if (c is Ch_Infiltrate) perPoint = Safe(() => c.map.param.ch_complexityPerSecurityPoint, 25);
            else if (c is Ch_AccessVaultLimited) perPoint = 4;
            else if (c is Ch_AccessVault) perPoint = 8;
            else if (c is Ch_AssassinateSilent || c is Ch_AssassinateBrutal) perPoint = 5;
            else return null;
            int sec = Safe(() => c.location != null && c.location.settlement != null
                ? c.location.settlement.getSecurity(null) : 0, 0);
            string note = "complexity is NOT fixed: it is recomputed every turn as base + " + perPoint +
                " per point of the settlement's LIVE security (now " + sec + "), and accumulated " +
                "progress is measured against the live value - if security rises mid-challenge, the " +
                "total climbs and your % completion drops with no message.";
            try
            {
                if (c.location != null && c.location.properties != null)
                    foreach (Property pr in c.location.properties)
                        if (pr is Pr_BribedGuards)
                        {
                            note += " Bribed Guards is active here (-2 security): it lapses " +
                                "SILENTLY in ~" + (int)Math.Ceiling(Math.Max(0.0, pr.charge - 0.1)) +
                                " turn(s) (charge " + Round2(pr.charge) + "), which will raise this " +
                                "complexity by " + (2 * perPoint) + ".";
                            break;
                        }
            }
            catch { }
            return note;
        }

        // ---------- laughing tome ----------

        /// <summary>
        /// Where the Laughing Tome currently is. The states mirror I_LaughingTome.purgeExisting +
        /// Ch_SummonLaughingTome.validFor/complete: only "beingBound" blocks the summon's validFor,
        /// and only "heldBound" makes a completed summon silently do nothing. Game 13's player read
        /// the (true) "a hero is binding the tome" restriction as wrong because nothing let them
        /// verify it; this makes the blocking hero, holder or holding location observable.
        /// Returns JsonValue.Null when no game or on any game-read failure.
        /// </summary>
        public static JsonValue LaughingTomeStatus(GameContext ctx, Map map)
        {
            try
            {
                if (map == null) return JsonValue.Null;
                foreach (Unit u in map.units)
                {
                    if (u != null && u.task is Task_PerformChallenge tpc && tpc.challenge is Ch_BindTome)
                        return JsonValue.NewObject()
                            .Set("state", "beingBound")
                            .Set("unit", UnitRef(ctx, u))
                            .Set("location", LocationRef(u.location))
                            .Set("note", "a hero is binding the tome - Summon Tome is blocked until the "
                                + "binding completes or is interrupted (kill, rob or disrupt the binder)");
                }
                foreach (Person p in map.persons)
                {
                    if (p == null || p.items == null) continue;
                    foreach (Item it in p.items)
                    {
                        if (!(it is I_LaughingTome tome)) continue;
                        if (tome.bound)
                            return JsonValue.NewObject()
                                .Set("state", "heldBound")
                                .Set("holder", PersonRef(p))
                                .Set("location", LocationRef(p.getLocation()))
                                .Set("note", "bound to its holder - Summon Tome will complete but do "
                                    + "NOTHING; rob or kill the holder to retrieve it");
                        return JsonValue.NewObject()
                            .Set("state", "held")
                            .Set("holder", PersonRef(p))
                            .Set("location", LocationRef(p.getLocation()))
                            .Set("note", "held unbound - Summon Tome will retrieve it");
                    }
                }
                foreach (Location l in map.locations)
                {
                    if (l == null || l.properties == null) continue;
                    foreach (Property pr in l.properties)
                    {
                        if (pr is Pr_LaughingTomeInert)
                            return JsonValue.NewObject()
                                .Set("state", "inertAtLocation")
                                .Set("location", LocationRef(l))
                                .Set("note", "asleep - the 'Collect Tome' challenge is available AT this "
                                    + "location (list_challenges there), or Summon Tome retrieves it from "
                                    + "anywhere");
                        if (pr is Pr_LaughingTome)
                            return JsonValue.NewObject()
                                .Set("state", "activeAtLocation")
                                .Set("location", LocationRef(l))
                                .Set("note", "active in the wild - NOT collectable while active (heroes "
                                    + "may come bind it here); Summon Tome retrieves it from anywhere");
                    }
                }
                return JsonValue.NewObject()
                    .Set("state", "inEther")
                    .Set("note", "not in the world - Summon Tome will conjure it directly");
            }
            catch { return JsonValue.Null; }
        }

        /// <summary>One-sentence rendering of <see cref="LaughingTomeStatus"/> for error messages
        /// ("Tome status: being bound by X at Y."); null when the status is unreadable.</summary>
        public static string LaughingTomeStatusText(GameContext ctx, Map map)
        {
            JsonValue ts = LaughingTomeStatus(ctx, map);
            if (ts == null || ts.IsNull) return null;
            string state = ts["state"].AsString();
            if (state == null) return null;
            string who = ts["unit"]["name"].AsString() ?? ts["holder"]["name"].AsString();
            string where = ts["location"]["name"].AsString();
            string s;
            switch (state)
            {
                case "beingBound": s = "being bound by " + (who ?? "a hero"); break;
                case "heldBound": s = "held BOUND by " + (who ?? "someone") + " (summon completes but does nothing)"; break;
                case "held": s = "held unbound by " + (who ?? "someone"); break;
                case "inertAtLocation": s = "lying inert"; break;
                case "activeAtLocation": s = "deployed and active"; break;
                default: s = "in the ether (not in the world)"; break;
            }
            if (where != null) s += " at " + where;
            if (state == "inertAtLocation") s += " - the 'Collect Tome' challenge there retrieves it";
            return "Tome status: " + s + ".";
        }

        // ---------- challenge requirement clauses ----------

        /// <summary>
        /// Per-challenge-type decomposition of valid() into observable clauses, computed from
        /// public game state WITHOUT calling valid(). A multi-clause refusal that re-states all
        /// clauses is unactionable (game 16 abandoned a viable Plague Ships line because only one
        /// of three restated clauses had actually failed); this names which clause is unmet and
        /// what the actual value is. Returns Null for types without an evaluator — extend by
        /// adding cases here; SafeValid picks the table up automatically.
        /// Ch_PlagueShips MUST stay off the valid() path entirely: its trade-route loop calls
        /// Property.addToPropertySingleShot, i.e. CHECKING validity spreads plague to connected
        /// docks (Ch_PlagueShips.cs:136-159).
        /// </summary>
        public static JsonValue ChallengeRequirements(Challenge c)
        {
            try
            {
                if (c is Ch_PlagueShips ps)
                {
                    Map map = ps.map;
                    Location loc = ps.location;
                    if (map == null || loc == null) return JsonValue.Null;
                    JsonValue reqs = JsonValue.NewArray();

                    bool infiltrated = Safe(() => ps.sub != null && ps.sub.infiltrated, false);
                    reqs.Add(JsonValue.NewObject()
                        .Set("clause", "the docks here are infiltrated")
                        .Set("met", infiltrated)
                        .Set("actual", infiltrated ? "infiltrated" : "not infiltrated"));

                    int need = Safe(() => map.param.ch_plagueShipsPlagueReq, 10);
                    // Location.getStandardPropertyLevel is internal - replicate it (first PLAGUE
                    // property's charge, 0 when absent; Location.cs:161-171).
                    double plague = Safe(() =>
                    {
                        if (loc.properties != null)
                            foreach (Property pr in loc.properties)
                                if (pr != null && pr.getPropType() == Property.standardProperties.PLAGUE)
                                    return (double)pr.charge;
                        return 0.0;
                    }, 0.0);
                    reqs.Add(JsonValue.NewObject()
                        .Set("clause", "plague at this dock is at least " + need + "%")
                        .Set("met", plague >= need)
                        .Set("actual", Round2(plague) + "%"));

                    int routes = Safe(() =>
                    {
                        var density = map.tradeManager != null ? map.tradeManager.tradeDensity : null;
                        if (density == null || loc.index < 0 || loc.index >= density.Length) return 0;
                        return density[loc.index] != null ? density[loc.index].Count : 0;
                    }, 0);
                    reqs.Add(JsonValue.NewObject()
                        .Set("clause", "this dock lies on at least one trade route")
                        .Set("met", routes > 0)
                        .Set("actual", routes + " trade route(s)")
                        // valid() only requires ANY route through this dock; the "connects to
                        // another dock" wording in getRestriction has no matching check.
                        .Set("note", "the vanilla restriction says 'a trade route which connects "
                            + "to another dock', but the actual check is only that ANY trade route "
                            + "passes through this dock"));
                    return reqs;
                }
            }
            catch { }
            return JsonValue.Null;
        }

        /// <summary>
        /// Challenge.valid() without side effects. For types with a ChallengeRequirements
        /// evaluator, validity is the conjunction of the clauses (verified equivalent to valid()
        /// minus its side effects — for Ch_PlagueShips the plague-spread loop after the three
        /// gates never returns false); every other type falls back to valid() itself.
        /// </summary>
        public static bool SafeValid(Challenge c)
        {
            JsonValue reqs = ChallengeRequirements(c);
            if (!reqs.IsNull)
            {
                foreach (JsonValue r in reqs.Items)
                    if (!r["met"].AsBool()) return false;
                return true;
            }
            return Safe(() => c.valid(), false);
        }

        /// <summary>
        /// One-line rendering of <see cref="ChallengeRequirements"/> for refusal messages, failed
        /// clauses first: "[X] plague at this dock is at least 10% (now 4%); [OK] ...". Null when
        /// the type has no evaluator.
        /// </summary>
        public static string ChallengeRequirementsText(Challenge c)
        {
            return RenderRequirementClauses(ChallengeRequirements(c));
        }

        /// <summary>Shared renderer for requirement-clause tables ({clause, met, actual} entries),
        /// failed clauses first: "[X] plague at this dock is at least 10% (now 4%); [OK] ...".
        /// Null when the table is Null/empty.</summary>
        private static string RenderRequirementClauses(JsonValue reqs)
        {
            if (reqs.IsNull || reqs.Count == 0) return null;
            List<string> failed = new List<string>();
            List<string> met = new List<string>();
            foreach (JsonValue r in reqs.Items)
            {
                bool ok = r["met"].AsBool();
                string line = (ok ? "[OK] " : "[X] ") + r["clause"].AsString() +
                    " (" + (ok ? "" : "now ") + r["actual"].AsString() + ")";
                (ok ? met : failed).Add(line);
            }
            failed.AddRange(met);
            return string.Join("; ", failed);
        }

        // ---------- power requirements ----------

        /// <summary>
        /// Per-power-type decomposition of validTarget(Location) into observable clauses, same
        /// pattern as <see cref="ChallengeRequirements"/>. Motivated by G17-#1: Heart of the
        /// Forest's in-game restriction text is just "Must be cast on land" while validTarget
        /// also enforces the heart-distance/seed rule, so a distance refusal read as a terrain
        /// refusal and sent the playtester hunting for non-existent empty land. Returns Null for
        /// power types without an evaluator - callers fall back to getRestrictionText().
        /// Evaluators mirror the decompiled validTarget() exactly; they never call it.
        /// </summary>
        public static JsonValue PowerRequirements(Map map, Power p, Location loc)
        {
            try
            {
                if (map == null || p == null || loc == null) return JsonValue.Null;
                if (p is P_Vinerva_HeartOfForest) return HeartOfForestRequirements(map, loc);
                if (p is P_Vinerva_WildernessSpirits) return WildernessSpiritsRequirements(map, loc);
                if (p is P_Vinerva_Manifestation) return ManifestationRequirements(map, loc);
                if (p is P_Vinerva_Tempt_Gold) return TemptRequirements(map, loc, typeof(Pr_Vinverva_Gold), "Grove of Golden Roses");
                if (p is P_Vinerva_Tempt_Nectar) return TemptRequirements(map, loc, typeof(Pr_Vinverva_Food), "Nectar Grove");
                if (p is P_Vinerva_Tempt_PeaceLily) return TemptRequirements(map, loc, typeof(Pr_Vinverva_Peace), "Peace Lily grove");
                if (p is P_Vinerva_Tempt_Health) return TemptRequirements(map, loc, typeof(Pr_Vinverva_Health), "healing grove");
                if (p is P_Vinerva_Tempt_Might) return TemptRequirements(map, loc, null, null, landNotSettlement: true);
                if (p is P_Vinerva_Tempt_Salvation) return TemptRequirements(map, loc, null, null, landNotSettlement: true);
                if (p is P_Opha_StartFaith) return StartFaithRequirements(map, loc);
            }
            catch { }
            return JsonValue.Null;
        }

        /// <summary>Rendering of <see cref="PowerRequirements"/> for use_power refusals, failed
        /// clauses first. Null when the power type has no evaluator.</summary>
        public static string PowerRequirementsText(Map map, Power p, Location loc)
        {
            return RenderRequirementClauses(PowerRequirements(map, p, loc));
        }

        private static JsonValue HeartClause(Map map, Location loc, bool zeroHeartsPass, string zeroHeartsActual)
        {
            int maxDist = Safe(() => map.param.power_vinerva_growthMaxDist, 0);
            God_Vinerva gv = map.overmind.god as God_Vinerva;
            if (gv == null)
                // Non-Vinerva casters (Eternity's borrowed powers) skip the heart clause entirely.
                return JsonValue.NewObject()
                    .Set("clause", "no Heart-distance limit for this god")
                    .Set("met", true)
                    .Set("actual", "not playing Vinerva");
            if (gv.hearts.Count == 0)
                return JsonValue.NewObject()
                    .Set("clause", "within " + maxDist + " steps of an existing Heart of the Forest")
                    .Set("met", zeroHeartsPass)
                    .Set("actual", zeroHeartsActual);
            int nearest = int.MaxValue;
            foreach (int heart in gv.hearts)
            {
                int d = Safe(() => map.getStepDist(loc, map.locations[heart]), int.MaxValue);
                if (d < nearest) nearest = d;
            }
            return JsonValue.NewObject()
                .Set("clause", "within " + maxDist + " steps of an existing Heart of the Forest")
                .Set("met", nearest <= maxDist)
                .Set("actual", nearest == int.MaxValue
                    ? "no reachable Heart"
                    : "nearest Heart is " + nearest + " step(s) away");
        }

        private static JsonValue HeartOfForestRequirements(Map map, Location loc)
        {
            JsonValue reqs = JsonValue.NewArray();
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "target is land")
                .Set("met", !loc.isOcean)
                .Set("actual", loc.isOcean ? "ocean" : "land (settlements count as land)"));
            bool hasHeart = Safe(() =>
            {
                if (loc.settlement != null)
                    foreach (Subsettlement sub in loc.settlement.subs)
                        if (sub is Sub_Vinerva_HeartOfForest) return true;
                return false;
            }, false);
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "no Heart of the Forest stands here already")
                .Set("met", !hasHeart)
                .Set("actual", hasHeart ? "a Heart is already here" : "no Heart here"));
            // In-reach: first Heart is free of the distance rule; later ones can also piggyback on
            // a Vinerva Seed carried by any unit standing at the target.
            bool seedHere = Safe(() =>
            {
                foreach (Unit unit in loc.units)
                {
                    if (unit.person == null) continue;
                    foreach (Item item in unit.person.items)
                        if (item is I_VinervaSeed) return true;
                }
                return false;
            }, false);
            JsonValue heartClause = HeartClause(map, loc, zeroHeartsPass: true,
                zeroHeartsActual: "no Hearts exist yet - the FIRST Heart has no distance limit");
            if (!heartClause["met"].AsBool() && seedHere)
                heartClause.Set("met", true)
                    .Set("actual", heartClause["actual"].AsString() + ", but a unit here carries a Vinerva Seed");
            else if (!heartClause["met"].AsBool())
                heartClause.Set("note", "alternatively, a unit standing at the target carrying a " +
                    "Vinerva Seed (Harvest Seed challenge at any Heart) satisfies this clause");
            reqs.Add(heartClause);
            return reqs;
        }

        private static JsonValue WildernessSpiritsRequirements(Map map, Location loc)
        {
            JsonValue reqs = JsonValue.NewArray();
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "target is land")
                .Set("met", !loc.isOcean)
                .Set("actual", loc.isOcean ? "ocean" : "land"));
            bool societyLand = Safe(() => loc.soc is Society, false);
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "target is not part of a human Society")
                .Set("met", !societyLand)
                .Set("actual", societyLand
                    ? "claimed by " + SafeName(() => loc.soc.getName())
                    : "not Society land")
                .Set("note", "the game text says 'empty location' but the actual check is Society " +
                    "ownership: orc camps, ruins and Deep One sites ARE valid targets; any " +
                    "human-settled hex is not"));
            reqs.Add(HeartClause(map, loc, zeroHeartsPass: false,
                zeroHeartsActual: "no Hearts exist yet - grow one with Heart of the Forest first"));
            return reqs;
        }

        private static JsonValue ManifestationRequirements(Map map, Location loc)
        {
            JsonValue reqs = JsonValue.NewArray();
            SettlementHuman sh = loc.settlement as SettlementHuman;
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "target is a human settlement with a living ruler")
                .Set("met", sh != null && Safe(() => sh.ruler != null, false))
                .Set("actual", sh == null ? "not a human settlement"
                    : Safe(() => sh.ruler != null, false) ? "ruled human settlement" : "the seat is empty"));
            int need = Safe(() => map.param.power_vinervaManifestationReqGift, 0);
            double gift = Safe(() =>
            {
                foreach (Property property in loc.properties)
                    if (property is PR_Vinerva_GiftAccepted) return property.charge;
                return 0.0;
            }, 0.0);
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "Vinerva's Gift accepted here at charge >= " + need)
                .Set("met", gift >= (double)need)
                .Set("actual", gift > 0.0 ? "Gift charge " + Round2(gift) : "no Gift accepted here"));
            reqs.Add(HeartClause(map, loc, zeroHeartsPass: true,
                zeroHeartsActual: "no Hearts exist yet - the distance rule is waived"));
            return reqs;
        }

        private static JsonValue TemptRequirements(Map map, Location loc, Type existingProperty,
            string groveName, bool landNotSettlement = false)
        {
            JsonValue reqs = JsonValue.NewArray();
            if (landNotSettlement)
            {
                // Tempt_Might / Tempt_Salvation: any land within Heart reach.
                reqs.Add(JsonValue.NewObject()
                    .Set("clause", "target is land")
                    .Set("met", !loc.isOcean)
                    .Set("actual", loc.isOcean ? "ocean" : "land"));
            }
            else
            {
                bool already = Safe(() =>
                {
                    foreach (Property property in loc.properties)
                        if (property != null && existingProperty.IsInstanceOfType(property)) return true;
                    return false;
                }, false);
                reqs.Add(JsonValue.NewObject()
                    .Set("clause", "no " + groveName + " here already")
                    .Set("met", !already)
                    .Set("actual", already ? "one is already growing here" : "none here"));
                reqs.Add(JsonValue.NewObject()
                    .Set("clause", "target is a human settlement")
                    .Set("met", loc.settlement is SettlementHuman)
                    .Set("actual", loc.settlement is SettlementHuman ? "human settlement"
                        : loc.settlement != null ? SafeName(() => loc.settlement.getName()) : "empty land"));
            }
            reqs.Add(HeartClause(map, loc, zeroHeartsPass: false,
                zeroHeartsActual: "no Hearts exist yet - the Tempt line is unusable until you grow " +
                    "a first Heart with Heart of the Forest"));
            return reqs;
        }

        /// <summary>
        /// P_Opha_StartFaith.validTarget has three clauses but its restriction text names only
        /// two, and the unnamed one (no existing Faith here) produced a flatly wrong refusal in
        /// G18-#3. "Human settlement" is the C# type SettlementHuman - dwarven and elven cities
        /// subclass it, so species is never the reason a cast is refused.
        /// </summary>
        private static JsonValue StartFaithRequirements(Map map, Location loc)
        {
            JsonValue reqs = JsonValue.NewArray();
            bool humanType = loc.settlement is SettlementHuman;
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "target is a human-type settlement (human, dwarven and elven cities all qualify)")
                .Set("met", humanType)
                .Set("actual", humanType ? "qualifying settlement"
                    : loc.settlement != null ? SafeName(() => loc.settlement.getName()) + " does not qualify"
                    : "no settlement here"));
            double infiltration = Safe(() => loc.settlement is SettlementHuman sh ? sh.infiltration : 0.0, 0.0);
            string districts = Safe(() =>
            {
                if (!(loc.settlement is SettlementHuman sh) || sh.subs == null) return null;
                int infiltrable = 0, done = 0;
                foreach (Subsettlement sub in sh.subs)
                {
                    if (sub.infiltrated) { done++; infiltrable++; }
                    else if (sub.canBeInfiltrated()) infiltrable++;
                }
                return done + "/" + infiltrable + " infiltrable districts infiltrated";
            }, null);
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "settlement infiltration > 0% (run Infiltrate on a district here first)")
                .Set("met", humanType && infiltration > 0.0)
                .Set("actual", !humanType ? "no qualifying settlement"
                    : Round2Down(infiltration * 100) + "%" + (districts != null ? " (" + districts + ")" : "")));
            double existingCharge = -1.0;
            Safe(() =>
            {
                foreach (Property property in loc.properties)
                    if (property is Pr_Opha_Faith faith) { existingCharge = faith.charge; return true; }
                return false;
            }, false);
            reqs.Add(JsonValue.NewObject()
                .Set("clause", "no Ophanim Faith already present here")
                .Set("met", existingCharge < 0.0)
                .Set("actual", existingCharge < 0.0 ? "none here"
                    : "Faith already here at " + Round2(existingCharge) + "% charge (it may be about to " +
                      "fade: see get_location properties[].influences - ruler awareness drains it)"));
            return reqs;
        }

        // ---------- victory attribution ----------

        /// <summary>
        /// Names WHO/WHAT is scoring in the victory columns whose aggregates are opaque, replicating
        /// Overmind.computeVictoryProgress()'s qualifier logic (Overmind.cs:387-470):
        ///  - insane(+enshadowed) rulers/heroes: rulers of human settlements whose society is neither
        ///    the Dark Empire nor Ophanim-controlled (those populations score in their own columns
        ///    instead — the else-if chain), plus living non-commandable heroes; split on shadow > 0.5.
        ///  - Deep One cities: only Set_DeepOneAbyssalCity.population scores; a Set_DeepOneSanctum
        ///    never scores itself, it feeds population into a nearby abyssal city.
        /// Returns JsonValue.Null when there is nothing to attribute (sections are also omitted
        /// individually when empty, so a mid-game payload stays small).
        /// </summary>
        public static JsonValue VictoryAttribution(GameContext ctx, Map map)
        {
            const int Cap = 25;
            JsonValue insaneShadow = JsonValue.NewArray();
            JsonValue insaneOnly = JsonValue.NewArray();
            JsonValue cities = JsonValue.NewArray();
            int sanctums = 0;
            bool insaneShadowTrunc = false, insaneOnlyTrunc = false;

            foreach (Location location in map.locations)
            {
                try
                {
                    if (location.settlement is SettlementHuman && location.soc is Society society
                        && !society.isDarkEmpire && !society.isOphanimControlled)
                    {
                        Person person = location.person();
                        if (person != null && person.isInsane())
                        {
                            JsonValue q = PersonRef(person)
                                .Set("role", "ruler")
                                .Set("location", LocationRef(location))
                                .Set("shadow", Round2(person.shadow));
                            if (person.shadow > 0.5)
                            {
                                if (insaneShadow.Count < Cap) insaneShadow.Add(q); else insaneShadowTrunc = true;
                            }
                            else
                            {
                                if (insaneOnly.Count < Cap) insaneOnly.Add(q); else insaneOnlyTrunc = true;
                            }
                        }
                    }
                    if (location.settlement is Set_DeepOneAbyssalCity abyssal)
                        cities.Add(JsonValue.NewObject()
                            .Set("id", LocationId(location))
                            .Set("name", SafeName(() => location.getName()))
                            .Set("population", Round2(abyssal.population)));
                    if (location.settlement is Set_DeepOneSanctum) sanctums++;
                }
                catch { }
            }
            foreach (Person p in map.persons)
            {
                try
                {
                    if (p == null || p.isDead || !(p.unit is UAG) || p.unit.isCommandable() || !p.isInsane())
                        continue;
                    JsonValue q = PersonRef(p)
                        .Set("role", "hero")
                        .Set("location", LocationRef(p.unit.location))
                        .Set("shadow", Round2(p.shadow));
                    if (p.shadow > 0.5)
                    {
                        if (insaneShadow.Count < Cap) insaneShadow.Add(q); else insaneShadowTrunc = true;
                    }
                    else
                    {
                        if (insaneOnly.Count < Cap) insaneOnly.Add(q); else insaneOnlyTrunc = true;
                    }
                }
                catch { }
            }

            JsonValue details = JsonValue.NewObject();
            bool any = false;
            if (insaneShadow.Count > 0)
            {
                any = true;
                JsonValue s = JsonValue.NewObject()
                    .Set("pointsEach", Safe(() => map.param.victory_insaneAndShadow, 0.0))
                    .Set("qualifiers", insaneShadow)
                    .Set("note", "rulers of human settlements OUTSIDE the Dark Empire / Ophanim societies "
                        + "who are insane with shadow > 0.5, plus living non-commandable heroes insane with "
                        + "shadow > 0.5. The count drops when one dies, is cured, falls to shadow <= 0.5, "
                        + "loses their seat, is corrupted into your own agent, or their city joins the Dark "
                        + "Empire (its population then scores in the Dark Empire column instead).");
                if (insaneShadowTrunc) s.Set("truncated", true);
                details.Set("insaneAndShadowRulersAndHeroes", s);
            }
            if (insaneOnly.Count > 0)
            {
                any = true;
                JsonValue s = JsonValue.NewObject()
                    .Set("pointsEach", Safe(() => map.param.victory_insane, 0.0))
                    .Set("qualifiers", insaneOnly)
                    .Set("note", "same rules but shadow <= 0.5; enshadowing one past 0.5 moves it to the "
                        + "higher-weighted insaneAndShadow column.");
                if (insaneOnlyTrunc) s.Set("truncated", true);
                details.Set("insaneOnlyRulersAndHeroes", s);
            }
            if (cities.Count > 0 || sanctums > 0)
            {
                any = true;
                details.Set("deepOneCities", JsonValue.NewObject()
                    .Set("cities", cities)
                    .Set("sanctumCount", sanctums)
                    .Set("note", "only Abyssal City population scores; a Sanctum never scores itself - it "
                        + "periodically pushes population into a nearby abyssal city (or founds one once "
                        + "large enough). The breakdown line only appears once its points exceed 0, and its "
                        + "printed points are truncated to an integer (0.9 displays as 0), so early Deep One "
                        + "investment can look like nothing is happening while it is in fact accruing."));
            }
            return any ? details : JsonValue.Null;
        }

        /// <summary>
        /// Pre-cast snapshot of who Vinerva's Manifestation is about to kill (G17-#11).
        /// P_Vinerva_Manifestation.cast -> SettlementHuman.fallIntoRuin kills the whole
        /// population and the ruler (SettlementHuman.cs:1188-1223), with no in-game preview.
        /// countedInVictoryColumn mirrors VictoryAttribution's qualifier rule so the caller
        /// sees when the cast will drop points from the insane/enshadowed-rulers columns.
        /// Null when the target has no ruled human settlement.
        /// </summary>
        public static JsonValue NotableDeathsForManifestation(GameContext ctx, Map map, Location target)
        {
            try
            {
                SettlementHuman sh = target != null ? target.settlement as SettlementHuman : null;
                if (sh == null) return JsonValue.Null;
                JsonValue o = JsonValue.NewObject()
                    .Set("population", sh.population);
                Person ruler = Safe(() => sh.ruler, null);
                if (ruler != null)
                {
                    bool scores = Safe(() =>
                        target.soc is Society society && !society.isDarkEmpire &&
                        !society.isOphanimControlled && ruler.isInsane(), false);
                    o.Set("ruler", PersonRef(ruler)
                        .Set("shadow", Round2(Safe(() => ruler.shadow, 0.0)))
                        .Set("insane", Safe(() => ruler.isInsane(), false))
                        .Set("countedInVictoryColumn", scores));
                    if (scores)
                        o.Set("note", "this ruler was scoring in the insane" +
                            (Safe(() => ruler.shadow, 0.0) > 0.5 ? "-and-enshadowed" : "-only") +
                            " rulers victory column - those points are gone now");
                }
                return o;
            }
            catch { return JsonValue.Null; }
        }

        // ---------- player / god ----------

        public static JsonValue PowerSummary(Map map, Power p)
        {
            // Id from the god's master power list, not the seal-filtered getPowers() list —
            // that list gains members as seals break, which would shift positional ids.
            int index = Safe(() => map.overmind.god.powers.IndexOf(p), -1);
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
            string note = PowerRestrictionNote(map, p);
            if (note != null) o.Set("restrictionNote", note);
            return o;
        }

        /// <summary>
        /// Mod-side correction for powers whose in-game restriction text is known to be
        /// incomplete or misleading (verified against the decompiled validTarget/cast). We
        /// annotate rather than rewrite the game's text (targetRestriction stays verbatim).
        /// </summary>
        private static string PowerRestrictionNote(Map map, Power p)
        {
            try
            {
                int maxDist = Safe(() => map.param.power_vinerva_growthMaxDist, 0);
                if (p is P_Vinerva_HeartOfForest)
                    return "the in-game restriction text is incomplete: casting ALSO requires no " +
                        "Heart already at the target, and - once any Heart exists - the target " +
                        "within " + maxDist + " steps of an existing Heart OR a unit at the target " +
                        "carrying a Vinerva Seed. The FIRST Heart has no distance limit. A refusal " +
                        "names the exact failed clause.";
                if (p is P_Vinerva_WildernessSpirits)
                    return "'empty location' means not owned by a human Society: orc camps, ruins " +
                        "and Deep One sites ARE valid targets; any human-settled hex is not.";
                if (p is P_Vinerva_Manifestation)
                    return "DESTRUCTIVE: casting this kills the settlement's ENTIRE population AND " +
                        "its ruler outright - including an insane or enshadowed ruler currently " +
                        "scoring in the insane/enshadowed-rulers victory column. The settlement is " +
                        "replaced by a Manifestation plus a new Heart. No confirmation is asked; " +
                        "the cast result lists the deaths under notableDeaths.";
                if (p is P_Opha_StartFaith)
                    return "the in-game restriction text is incomplete: (1) 'human settlement' means " +
                        "the human-TYPE settlement class - dwarven and elven cities qualify too; " +
                        "(2) casting also requires no Ophanim Faith already at the target; (3) the " +
                        "seed starts at just 1% charge while the LOCAL RULER'S AWARENESS drains it " +
                        "at 5%/turn per point of awareness, with no base growth - cast on a settlement " +
                        "with an aware ruler and the Faith silently vanishes within a couple of turns. " +
                        "Seed where the ruler is unaware (or dead), or where shadow-fear growth " +
                        "(see get_tips id=ophanim_faith) outruns the drain. A refusal names the " +
                        "exact failed clause.";
            }
            catch { }
            return null;
        }

        /// <summary>The god's win-condition sheet: time budget, seal thresholds, the agent-cap curve
        /// as seals break, power-level requirements, and the victory / seal descriptive text a player
        /// reads on screen. Null when no god is selected.</summary>
        public static JsonValue GodProgression(Map map)
        {
            God god = map.overmind.god;
            if (god == null) return JsonValue.Null;
            int maxTurns = Safe(() => god.getMaxTurns(), 0);
            JsonValue o = JsonValue.NewObject()
                // getMaxTurns() returns a number even in an endless game (the game ignores it),
                // so null both time-budget fields there or an agent reads a deadline that isn't real.
                .Set("endless", map.opt_endless)
                .Set("maxTurns", map.opt_endless ? JsonValue.Null : JsonValue.Of(maxTurns))
                .Set("turnsRemaining", map.opt_endless ? JsonValue.Null : JsonValue.Of(Math.Max(0, maxTurns - map.turn)))
                .Set("maxPower", Safe(() => god.getMaxPower(), 0))
                .Set("sealLevels", IntArray(Safe(() => god.getSealLevels(), null)))
                .Set("agentCaps", IntArray(Safe(() => god.getAgentCaps(), null)))
                .Set("powerLevelReqs", IntList(god.powerLevelReqs))
                // How this god plays and wins (meaningful throughout the game).
                .Set("mechanics", Safe(() => god.getDetailedMechanics(), null))
                .Set("sealDesc", Safe(() => god.getSealDesc(), null))
                .Set("powerIncreaseText", Safe(() => god.powerIncreaseText(), null))
                // The specific victory blurb is mode-keyed, and victoryMode stays at its default (0 =
                // SHADOW) until victory() records a real mode — defeat() never touches it. Gate on
                // victoryAchieved, not endOfGameAchieved, or a defeat would show a mode-0 VICTORY blurb.
                .Set("victoryMessage", map.overmind.victoryAchieved
                    ? Safe(() => god.getVictoryMessage(map.overmind.victoryMode), null) : null);
            // The Laughing King's vanilla mechanics blurb (and the seal-9 awakening message) promise a
            // waves-of-madness win route through the Iastur's Soul modifier - but no code path in the
            // game ever raises that charge (only Ch_BindIastur lowers it; 0% is a real defeat). Two
            // playtests were steered into building whole strategies on the dead meter (G14-#12/#17);
            // annotate the vanilla text rather than rewriting it (mod policy).
            if (god is God_LaughingKing)
                o.Set("mechanicsNote", "CORRECTION to the mechanics text above (verified against game " +
                    "code): the 'win by collapsing humanity's sanity' route works through the STANDARD " +
                    "victory-points meter - each ruler/hero Waves of Madness drives insane scores " +
                    "victory points (about double if also enshadowed). The 'Iastur's Soul reaches 300% " +
                    "= win' claim in the awakening message is dead text: nothing in the game ever " +
                    "raises the Soul charge. It only FALLS (heroes using the bound Tome), and 0% is a " +
                    "real defeat - defend it, but do not try to raise it. See get_tips id=iastur_soul.");
            // The Ophanim mechanics blurb describes Faith growth purely positively and names ruler
            // awareness only as a Doubt driver - but awareness is Faith's PRIMARY brake (-5/turn at
            // full awareness vs +4 max realistic growth), which G18 had to reverse-engineer from raw
            // influence lists after two silently-evaporated casts. Annotate, don't rewrite (mod policy).
            if (god is God_Ophanim)
                o.Set("mechanicsNote", "ADDITION to the mechanics text above (verified against game " +
                    "code): a LOCAL RULER'S AWARENESS directly drains Faith at 5%/turn per point of " +
                    "awareness (a fully-aware ruler outpaces every growth source combined; the drain " +
                    "stops only if the ruler dies or the nation becomes Ophanim-controlled). Faith " +
                    "that reaches 0% is silently deleted. For Ophanim, awareness is not a late-game " +
                    "threat but the primary brake on your scoring engine - keep rulers unaware or " +
                    "remove them before seeding. Read any Faith's exact growth terms in " +
                    "get_location properties[].influences. See get_tips id=ophanim_faith.");
            return o;
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
        /// prophet, tenets (with their live influence eligibility) and reach. Null when the group is not
        /// a holy order. <paramref name="detail"/> adds each tenet's description and a ready-to-paste
        /// influence_holy_order_tenet call - omitted from the bulk listing, where ~20 tenets per order
        /// across every religion would dominate the payload.</summary>
        public static JsonValue HolyOrderBlock(GameContext ctx, HolyOrder ho, bool detail)
        {
            if (ho == null) return JsonValue.Null;

            int req = Safe(() => ho.influenceElderReq, 0);
            int perTurn = Safe(() => ho.computeInfluenceDark(null), 0);
            bool canChange = ho.influenceElder >= req;

            JsonValue tenets = JsonValue.NewArray();
            if (ho.tenets != null)
                // YOUR OWN order's tenets always carry desc + the ready-to-paste call, even in the
                // bulk listing: it is one order (~5-8 tenets) and its trap tenet (Sap Life Force)
                // hid its cost behind the detail knob for a whole game (G19-#2).
                foreach (HolyTenet t in ho.tenets) tenets.Add(TenetSummary(ho, t, detail || ho.worshipsThePlayer));

            JsonValue o = JsonValue.NewObject()
                .Set("enshadowment", Round2(ho.enshadowment))
                .Set("worshipsThePlayer", ho.worshipsThePlayer)
                .Set("nAcolytes", ho.nAcolytes)
                .Set("nTemples", ho.nTemples)
                .Set("nWorshippers", ho.nWorshippers)
                .Set("nWorshippingRulers", ho.nWorshippingRulers)
                .Set("reserves", ho.reserves)
                .Set("influenceElder", ho.influenceElder)
                .Set("influenceElderReq", req)
                .Set("influenceElderPerTurn", perTurn)
                .Set("canChangeTenet", canChange)
                // influenceHuman is spent by the game's own AI (HolyOrder.humanAIExpenditure), not by you.
                .Set("influenceHuman", ho.influenceHuman)
                .Set("influenceHumanReq", Safe(() => ho.influenceHumanReq, 0));

            if (canChange)
                o.Set("influenceCapped", true)
                 .Set("hint", "you can change one tenet now - influence_holy_order_tenet {\"orderId\":\""
                     + SocialGroupId(ho) + "\",...}. Elder influence is capped at the requirement, so "
                     + "further gain is wasted until you spend it.");
            else if (perTurn > 0)
                o.Set("turnsUntilCanChangeTenet", (req - ho.influenceElder + perTurn - 1) / perTurn);

            return o
                .Set("prophet", UnitRef(ctx, ho.prophet))
                .Set("divinity", DivinityBlock(ctx, ho))
                .Set("tenets", tenets);
        }

        /// <summary>One tenet of a holy order, with the two influence directions the game would offer.
        /// "toward_elder" is the UI's negative/left button (status--), "toward_human" its positive one.</summary>
        public static JsonValue TenetSummary(HolyOrder ho, HolyTenet t, bool detail)
        {
            if (t == null) return JsonValue.Null;
            bool towardElder, towardHuman;
            string blocked;
            TenetEligibility(ho, t, out towardElder, out towardHuman, out blocked);

            JsonValue o = JsonValue.NewObject()
                .Set("name", SafeName(() => t.getName()))
                .Set("type", t.GetType().Name)
                .Set("status", t.status)
                .Set("min", Safe(() => t.getMaxNegativeInfluence(), 0))
                .Set("max", Safe(() => t.getMaxPositiveInfluence(), 0))
                .Set("structural", Safe(() => t.structuralTenet(), false))
                .Set("reads", TenetStatusLabel(t))
                .Set("canInfluence", JsonValue.NewObject()
                    .Set("toward_elder", towardElder)
                    .Set("toward_human", towardHuman));
            if (blocked != null) o.Set("blockedReason", blocked);
            // Sap Life Force's cost is easy to misread and its effects are invisible in play
            // (G19-#2) - the warning rides EVERY serialization of this tenet, both detail modes.
            string sapWarn = SapLifeforceWarning(t);
            if (sapWarn != null) o.Set("warning", sapWarn);
            if (detail)
            {
                o.Set("desc", SafeName(() => t.getDesc()));
                if (towardElder || towardHuman)
                    o.Set("call", "influence_holy_order_tenet {\"orderId\":\"" + SocialGroupId(ho)
                        + "\",\"tenet\":\"" + t.GetType().Name + "\",\"direction\":\""
                        + (towardElder ? "toward_elder" : "toward_human") + "\"}");
            }
            return o;
        }

        /// <summary>The game's own status wording for a tenet (UIE_HolyTenet.setTo): ordinary tenets read
        /// as Human/Elder Powers, structural ones as Positive/Negative.</summary>
        public static string TenetStatusLabel(HolyTenet t)
        {
            if (t == null) return null;
            bool structural = Safe(() => t.structuralTenet(), false);
            if (t.status == 0) return "Neutral (Inert)";
            if (t.status > 0) return (structural ? "Positive: +" : "Human: +") + t.status;
            return (structural ? "Negative: +" : "Elder Powers: +") + (-t.status);
        }

        /// <summary>The verified-against-code cost/benefit warning for H_SapLifeforce, null for any
        /// other tenet. Mechanics (H_SapLifeforce.turnTickTemple): while overmind power is below max,
        /// exactly ONE Ophanim temple-city per turn map-wide (shared lastTurnEffected guard - whichever
        /// temple ticks first) takes population += status*2; below 2 population the settlement is
        /// destroyed via fallIntoRuin("Devoured by Ophanim"). Neither the drain nor the destruction
        /// produces a message the tools can pass through (fallIntoRuin uses map.addMessage, which
        /// never reaches the digest). Payoff (God_Ophanim.getPowerPerTurn): a FLAT
        /// holy_sapLifePowerGain (0.02) per darkened level - the desc's "2%" is not a percentage.</summary>
        public static string SapLifeforceWarning(HolyTenet t)
        {
            if (!(t is H_SapLifeforce)) return null;
            double gain = Safe(() => World.staticMap.param.holy_sapLifePowerGain, 0.02);
            if (t.status < 0)
            {
                int lvl = -t.status;
                return "DARKENED - drain is LIVE: while your power is below max, ONE Ophanim " +
                    "temple-city per turn (whichever temple ticks first - you cannot choose it) " +
                    "loses " + (2 * lvl) + " population, and a city dropping below 2 population is " +
                    "DESTROYED outright ('Devoured by Ophanim'), temple, Faith and ruler included. " +
                    "The game shows NO message for the drain or the destruction. The payoff is a " +
                    "FLAT +" + Round2(gain * lvl) + " power/turn (the description's '" +
                    (int)(100.0 * gain) + "%' is added as an absolute amount, not a percentage). " +
                    "The drain pauses only while power is at max. Watch game_overview.ophanimSapDrain " +
                    "for the cities at risk. See get_tips id=ophanim_tenets.";
            }
            return "WARNING before darkening: each level makes ONE Ophanim temple-city per turn " +
                "(not choosable) lose 2 population while your power is below max, and a city " +
                "below 2 population is DESTROYED outright ('Devoured by Ophanim') - silently, " +
                "with no message anywhere. The payoff is a FLAT +" + Round2(gain) +
                " power/turn per level (the description's '" + (int)(100.0 * gain) +
                "%' is not a percentage). See get_tips id=ophanim_tenets.";
        }

        /// <summary>The god's power regen per turn, modifiers included (base regen scales with seals
        /// broken and difficulty; Ophanim's Sap Life Force adds a flat bonus per darkened level).
        /// Pure read - the game's own top-bar hover calls it every frame.</summary>
        public static double SafePowerPerTurn(Overmind om)
        {
            try { return om != null && om.god != null ? om.god.getPowerPerTurn() : 0.0; }
            catch { return 0.0; }
        }

        /// <summary>Live status of the Sap Life Force drain: JsonValue.Null unless the player god is
        /// Ophanim AND its faith's tenet_sap is darkened. Lists every temple-city of the player's
        /// faith with how many hits it can survive - ONE of them is drained each turn while power
        /// is below max (G19-#2). Also fills <paramref name="atRisk"/> (may be null) with the
        /// snapshot used by end_turn's DEVOURED_BY_OPHANIM detection.</summary>
        public static JsonValue ComputeSapDrain(GameContext ctx, Map map, List<Location> atRisk = null)
        {
            try
            {
                if (map == null || map.overmind == null) return JsonValue.Null;
                if (!(map.overmind.god is God_Ophanim g) || g.faith == null) return JsonValue.Null;
                HolyTenet sap = g.faith.tenet_sap;
                if (sap == null || sap.status >= 0) return JsonValue.Null;
                int lvl = -sap.status;
                int popLoss = 2 * lvl;
                bool active = Safe(() => map.overmind.power < (double)map.overmind.getMaxPower(), true);

                JsonValue cities = JsonValue.NewArray();
                int nCities = 0;
                if (map.locations != null)
                    foreach (Location l in map.locations)
                    {
                        if (l == null || !(l.settlement is SettlementHuman sh)) continue;
                        bool hasTemple = false;
                        if (sh.subs != null)
                            foreach (Subsettlement sub in sh.subs)
                                if (sub is Sub_Temple temple && temple.order == g.faith) { hasTemple = true; break; }
                        if (!hasTemple) continue;
                        nCities++;
                        // Hits until population < 2 destroys it (fallIntoRuin at pop < 2).
                        int hitsToRuin = sh.population < 2 ? 0 : ((sh.population - 2) / popLoss) + 1;
                        cities.Add(JsonValue.NewObject()
                            .Set("location", LocationRef(l))
                            .Set("population", sh.population)
                            .Set("hitsToRuin", hitsToRuin));
                        atRisk?.Add(l);
                    }
                if (nCities == 0) return JsonValue.Null;

                return JsonValue.NewObject()
                    .Set("tenetStatus", sap.status)
                    .Set("popLossPerHit", popLoss)
                    .Set("drainActive", active)
                    .Set("powerPerTurnBonus", Round2(Safe(() => World.staticMap.param.holy_sapLifePowerGain, 0.02) * lvl))
                    .Set("templeCities", cities)
                    .Set("note", "Sap Life Force is darkened: ONE of these temple-cities takes -" +
                        popLoss + " population each turn while your power is below max (whichever " +
                        "temple ticks first - you cannot choose it), and a city below 2 population " +
                        "is DESTROYED outright ('Devoured by Ophanim') with no game message. A city " +
                        "with hitsToRuin 1 can die NEXT turn. To stop the drain: influence the " +
                        "tenet back toward_human, or accept the losses for the flat power/turn bonus.");
            }
            catch { return JsonValue.Null; }
        }

        /// <summary>True when a ComputeSapDrain payload lists a temple-city within 3 hits of ruin -
        /// the gate for attaching the drain block to end_turn (always on game_overview).</summary>
        public static bool SapDrainImminent(JsonValue drain)
        {
            try
            {
                if (drain.IsNull) return false;
                foreach (JsonValue c in drain["templeCities"].Items)
                    if (c["hitsToRuin"].AsInt(int.MaxValue) <= 3) return true;
            }
            catch { }
            return false;
        }

        /// <summary>A temple-city as it stood while the Sap Life Force drain was armed - enough to
        /// name it after fallIntoRuin replaced its settlement object.</summary>
        public sealed class SapCitySnap
        {
            public Location Location;
            public string Name;
            public int Population;
        }

        /// <summary>Snapshot the temple-cities exposed to the Sap Life Force drain (empty list when
        /// the drain is not armed). Companion snapshot for <see cref="EvaluateSapDevoured"/>.</summary>
        public static List<SapCitySnap> ComputeSapCitySnaps(GameContext ctx, Map map)
        {
            var result = new List<SapCitySnap>();
            var atRisk = new List<Location>();
            if (ComputeSapDrain(ctx, map, atRisk).IsNull) return result;
            foreach (Location l in atRisk)
            {
                try
                {
                    if (!(l.settlement is SettlementHuman sh)) continue;
                    result.Add(new SapCitySnap
                    {
                        Location = l,
                        Name = SafeName(() => sh.getName()),
                        Population = sh.population
                    });
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// Compare a Sap-drain city snapshot to the live map: any snapshotted temple-city whose
        /// settlement is now ruins gets a DEVOURED_BY_OPHANIM digest event. The game announces the
        /// destruction only via map.addMessage (the threats stream), which never reaches the digest,
        /// so the mod synthesizes the record (G19-#2). Returns JsonValue.Null when none fell.
        /// </summary>
        public static JsonValue EvaluateSapDevoured(GameContext ctx, Map map, List<SapCitySnap> before)
        {
            if (before == null || before.Count == 0 || map == null) return JsonValue.Null;
            JsonValue arr = JsonValue.NewArray();
            foreach (SapCitySnap b in before)
            {
                try
                {
                    if (b == null || b.Location == null) continue;
                    if (b.Location.settlement is SettlementHuman) continue; // still standing
                    string message = b.Name + " (population " + b.Population + " last turn) fell " +
                        "into ruin - with Sap Life Force darkened the usual cause is 'Devoured by " +
                        "Ophanim' (the drain took its population below 2). The settlement, its " +
                        "temple, its Faith and its ruler are gone.";
                    arr.Add(JsonValue.NewObject()
                        .Set("turn", map.turn)
                        .Set("type", "DEVOURED_BY_OPHANIM")
                        .Set("title", "Temple-city devoured by Ophanim")
                        .Set("message", message)
                        .Set("mine", true)
                        .Set("synthesized", true));
                    try { ctx.Events.RecordPopup(map.turn, "DEVOURED_BY_OPHANIM", message, "synthesized"); } catch { }
                }
                catch { }
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
        }

        /// <summary>Which way this tenet may be influenced right now, mirroring the button-visibility rules
        /// in UIE_HolyTenet.setTo. Deliberately IGNORES whether the order has banked enough Elder influence
        /// (that is an order-level gate, reported separately as canChangeTenet) so an agent can plan ahead.
        /// <paramref name="blockedReason"/> is set only when neither direction is legal.</summary>
        public static void TenetEligibility(HolyOrder ho, HolyTenet t,
            out bool towardElder, out bool towardHuman, out string blockedReason)
        {
            towardElder = false;
            towardHuman = false;
            blockedReason = null;
            if (ho == null || t == null) return;

            int min = Safe(() => t.getMaxNegativeInfluence(), 0);
            int max = Safe(() => t.getMaxPositiveInfluence(), 0);
            bool structural = Safe(() => t.structuralTenet(), false);
            HolyTenet alignment = ho.tenet_alignment;

            // The gate the game hides behind an invisible button: an ordinary tenet cannot be pushed further
            // into the dark while the order's Alignment Status is at or above it. Drive Alignment down first.
            bool alignmentBlocks = !(t is H_Alignment) && !structural && alignment != null
                                   && t.status <= 0 && t.status <= alignment.status;

            towardElder = t.status > min && !alignmentBlocks;
            towardHuman = t.status < max;

            if (towardElder || towardHuman) return;
            if (alignmentBlocks)
                blockedReason = "Alignment Status is " + (alignment.status >= 0 ? "+" : "") + alignment.status
                    + ": drive it toward_elder (below " + t.status + ") before this tenet can be darkened"
                    + (t.status >= max ? ", and it is already at its most human (" + max + ")" : "");
            else if (t.status <= min && t.status >= max)
                blockedReason = "fixed at " + t.status + " (range " + min + " to " + max + ")";
            else if (t.status <= min)
                blockedReason = "already at its most negative (" + min + ")";
            else
                blockedReason = "already at its most positive (" + max + ")";
        }

        /// <summary>The divine entity behind a holy order, with the two actions the holy-order screen offers
        /// against it (oppose_divinity). Null when the order has none (opt_divineEntities off, or Ophanim).</summary>
        public static JsonValue DivinityBlock(GameContext ctx, HolyOrder ho)
        {
            if (ho == null || ho.divinity == null) return JsonValue.Null;
            DivineEntity d = ho.divinity;

            int corrupted = 0, total = 0;
            if (d.presences != null)
                foreach (Pr_EntityPresence p in d.presences)
                {
                    total++;
                    if (p != null && p.corrupted) corrupted++;
                }

            double power = ctx != null && ctx.Map != null && ctx.Map.overmind != null ? ctx.Map.overmind.power : 0.0;
            bool canUndermine = !d.exiled && power >= 1.0;
            bool canExile = !d.exiled && d.strength == 0 && total > 0 && corrupted == total;

            JsonValue o = JsonValue.NewObject()
                .Set("name", SafeName(() => d.getName()))
                .Set("mood", d.exiled ? "EXILED" : SafeName(() => d.getMoodDesc()))
                .Set("strength", d.strength)
                .Set("anger", Round2(d.anger))
                .Set("exiled", d.exiled)
                .Set("presencesCorrupted", corrupted)
                .Set("presencesTotal", total)
                .Set("canUndermine", canUndermine)
                .Set("canExile", canExile);

            if (d.exiled)
                o.Set("blockedReason", "already exiled - its opinions no longer matter");
            else if (!canUndermine)
                o.Set("blockedReason", "undermining costs 1 power (you have " + Round2(power) + ")");
            else if (!canExile)
                o.Set("exileNeeds", "strength 0 (now " + d.strength + ") and every presence corrupted ("
                    + corrupted + "/" + total + ") - undermine it and corrupt its presences first");
            return o;
        }

        // ---------- agent minions ----------

        public static JsonValue MinionSummary(Minion m)
        {
            if (m == null) return JsonValue.Null;
            // attack/defence come from the getters, matching every popup (minion dismissal, combat).
            // Minion.defence (innerDefence) is battle-eroded scratch state the constructor never
            // initialises - it read 0 for a fresh minion and made fights look unwinnable (G14-#2).
            return JsonValue.NewObject()
                .Set("name", SafeName(() => m.getName()))
                .Set("hp", m.hp)
                .Set("maxHp", Safe(() => m.getMaxHP(), 0))
                .Set("attack", Safe(() => m.getAttack(), 0))
                .Set("defence", Safe(() => m.getMaxDefence(), 0))
                .Set("commandCost", Safe(() => m.getCommandCost(), 1))
                .Set("isDead", m.isDead);
        }

        /// <summary>
        /// First-contact damage math for one attacker swinging repeatedly at one defender
        /// (BattleAgents.attackDownRow): damage per swing = max(0, attack − defence), and defence
        /// is ABLATIVE — every swing subtracts the attacker's FULL attack from it (floored at 0);
        /// it is set once at battle start and never regenerates. swingsBlanked = leading swings
        /// that deal 0 HP; swingsToKill = total swings to bring hp to 0 ("never" when attack is
        /// 0); line = the preformatted sentence combat surfaces embed.
        /// </summary>
        public static JsonValue ScreeningMath(int attack, int defence, int hp)
        {
            if (attack <= 0)
                return JsonValue.NewObject()
                    .Set("swingsBlanked", "all")
                    .Set("swingsToKill", "never")
                    .Set("line", "attack 0 can never damage this defender");
            int blanked = defence > 0 ? defence / attack : 0;
            int rem = Math.Max(0, defence - blanked * attack);
            int kill = blanked + (int)Math.Ceiling((hp + rem) / (double)attack);
            return JsonValue.NewObject()
                .Set("swingsBlanked", blanked)
                .Set("swingsToKill", kill)
                .Set("line", "attack " + attack + " vs defence " + defence + ": " +
                    (blanked > 0 ? "the first " + blanked + " swing(s) deal 0 damage, " : "") +
                    kill + " swing(s) to kill (hp " + hp + ")");
        }

        /// <summary>
        /// The unit's living-minion screen as it matters BEFORE a battle: in agent combat both
        /// leaders always strike the enemy's SLOT 0 (BattleAgents.step hard-codes row 0), so one
        /// living front minion makes this unit's leader untouchable by the enemy leader until it
        /// dies. Uses getMaxDefence/getAttack — live Minion.defence is battle-eroded scratch that
        /// reads 0 outside a battle (G14-#2). Null when the unit has no living minions.
        /// </summary>
        public static JsonValue MinionScreen(UA ua)
        {
            try
            {
                if (ua == null || ua.minions == null) return JsonValue.Null;
                Minion front = null;
                int count = 0;
                foreach (Minion m in ua.minions)
                {
                    if (m == null || m.isDead) continue;
                    count++;
                    if (front == null) front = m;
                }
                if (front == null) return JsonValue.Null;
                return JsonValue.NewObject()
                    .Set("count", count)
                    .Set("front", JsonValue.NewObject()
                        .Set("name", SafeName(() => front.getName()))
                        .Set("hp", front.hp)
                        .Set("defence", Safe(() => front.getMaxDefence(), 0))
                        .Set("attack", Safe(() => front.getAttack(), 0)))
                    .Set("note", "a living front minion SCREENS its leader: the enemy leader always "
                        + "strikes slot 0 and cannot touch this unit's leader until the front minion "
                        + "dies. Damage per swing is max(0, attack - defence) and defence is ablative "
                        + "(each hit removes the attacker's full attack from it, floored at 0, never "
                        + "regenerating) - a low-attack agent can deal 0 damage for several rounds "
                        + "while being hit back every round.");
            }
            catch { return JsonValue.Null; }
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
            public double TopMotivation;   // hunter's inclination to attack; uncapped (1.0 = evenly weighed, >1.0 = strongly inclined, mirroring the game's own >100% threat text)
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

        /// <summary>
        /// Per-Heart raze danger for Vinerva (G17-#5: the whole Heart economy was a hidden
        /// countdown - menace lived only in a get_location subsettlement field, and the game
        /// shows the "society most likely to attack" number only in hover text). Mirrors
        /// Sub_Vinerva_HeartOfForest.getHoverOverText (decompiled :32-80): scan every Society's
        /// razeActions for actions targeting this Heart, recompute getUtility with reasons, fold
        /// ReasonMsg pos/neg into motivation = pos/neg (same folding as ComputeAgentSafety).
        /// AN_RazeSubsettlement.getUtility is a pure computation (no state writes) - safe to call.
        /// Empty list when not playing Vinerva or no Hearts exist.
        /// </summary>
        public sealed class HeartRiskInfo
        {
            public Location Loc;
            public double Menace;
            public Society TopSociety;
            public double TopMotivation;
            public double TopUtility;
        }

        public static List<HeartRiskInfo> ComputeHeartRisks(GameContext ctx, Map map)
        {
            var result = new List<HeartRiskInfo>();
            God_Vinerva gv = map != null ? map.overmind.god as God_Vinerva : null;
            if (gv == null || gv.hearts == null) return result;
            foreach (int heartIdx in gv.hearts)
            {
                try
                {
                    Location loc = map.locations[heartIdx];
                    Sub_Vinerva_HeartOfForest heart = null;
                    if (loc != null && loc.settlement != null)
                        foreach (Subsettlement sub in loc.settlement.subs)
                            if (sub is Sub_Vinerva_HeartOfForest h) { heart = h; break; }
                    if (heart == null) continue;

                    var info = new HeartRiskInfo { Loc = loc, Menace = Safe(() => heart.menace, 0.0) };
                    foreach (SocialGroup sg in map.socialGroups)
                    {
                        if (!(sg is Society society) || society.getSovreign() == null) continue;
                        foreach (AN_RazeSubsettlement raze in society.razeActions.Values)
                        {
                            if (raze.target != heart) continue;
                            var reasons = new List<ReasonMsg>();
                            double utility = Safe(() => raze.getUtility(society, society.getSovreign(), reasons), double.MinValue);
                            if (utility == double.MinValue) continue;
                            double pos = 0.0, neg = 0.0;
                            foreach (ReasonMsg r in reasons)
                            {
                                if (r.value > 0.0) pos += r.value; else neg -= r.value;
                            }
                            double motivation = neg > 0.0 ? pos / neg : 1.0;
                            if (info.TopSociety == null || utility >= info.TopUtility)
                            {
                                info.TopSociety = society;
                                info.TopUtility = utility;
                                info.TopMotivation = motivation;
                            }
                        }
                    }
                    result.Add(info);
                }
                catch { }
            }
            return result;
        }

        public static JsonValue HeartRiskJson(GameContext ctx, HeartRiskInfo h)
        {
            JsonValue o = JsonValue.NewObject()
                .Set("location", LocationRef(h.Loc))
                .Set("menace", Round2(h.Menace));
            if (h.TopSociety != null)
                o.Set("topSociety", JsonValue.NewObject()
                    .Set("society", SocialGroupRef(h.TopSociety))
                    // The game's own hover number ("attack motivation at N%"), uncapped.
                    .Set("motivationPct", (int)Math.Round(h.TopMotivation * 100.0))
                    .Set("utility", Round2(h.TopUtility)))
                 .Set("willRazeNow", h.TopUtility > 0.0);
            return o;
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
                    // Uncapped: a strongly-inclined hunter reads >100%, matching the game's own threat text
                    // (get_threats.threats[].message) rather than clamping to a misleading flat 100%.
                    TopMotivation = topHunter != null ? topMotivation : 0.0,
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
                .Set("huntRadius", (int)(s.Profile / 5.0))
                .Set("isHuntable", s.IsHuntable)
                .Set("inHiding", s.InHiding)
                .Set("verdict", s.Verdict());
            if (s.TopHunter != null)
                o.Set("topHunter", JsonValue.NewObject()
                    .Set("unit", UnitRef(ctx, s.TopHunter))
                    .Set("motivationPct", (int)Math.Round(s.TopMotivation * 100.0))
                    .Set("dangerEstimate", s.HunterDanger));
            // A hero standing on your agent's tile is not only a threat - it is a target. Without this, a
            // closing hunter reads as danger only, and the pre-emptive strike (command_agent order:"attack",
            // which also destroys whatever ritual they are performing) never comes to mind.
            JsonValue onTile = HostilesOnTile(ctx, s.Agent);
            if (!onTile.IsNull)
                o.Set("hostilesOnTile", onTile)
                 .Set("attackHint", "these heroes share your agent's tile - you can strike first with " +
                     "command_agent {unitId:" + UnitId(ctx, s.Agent) + ", order:\"attack\", targetUnitId:…} " +
                     "(see get_unit.orders); it cancels their ritual even if you then flee");
            return o;
        }

        /// <summary>Hostile heroes standing on this agent's tile - i.e. the ones it could attack right now
        /// (same set AgentOrders builds its attack entries from). Null when there are none.</summary>
        private static JsonValue HostilesOnTile(GameContext ctx, UA ua)
        {
            if (ua == null || ua.isDead || ua.location == null || ua.location.units == null) return JsonValue.Null;
            JsonValue arr = JsonValue.NewArray();
            bool any = false;
            foreach (Unit other in ua.location.units)
            {
                if (other == null || other.isDead || other == ua) continue;
                UA hero = other as UA;
                if (hero == null || hero.isCommandable()) continue;
                // Your own faith's acolytes share tiles with your agents constantly; they are not
                // commandable but they are not about to kill anyone (G19-#5).
                if (IsOwnOrderUnit(hero)) continue;
                arr.Add(JsonValue.NewObject()
                    .Set("unit", UnitRef(ctx, hero))
                    .Set("dangerEstimate", Safe(() => hero.getDangerEstimate(), 0))
                    .Set("task", hero.task != null ? SafeName(() => hero.task.getShort()) : null));
                any = true;
            }
            return any ? arr : JsonValue.Null;
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

        /// <summary>Compare a batch-start snapshot to the live state and decide whether a batched end_turn
        /// should stop for threats. Retuned to fire only on *meaningful* danger by default (an agent becomes
        /// huntable, gains a hunter it is NOT favoured against, or its odds worsen) - a merely-in-range,
        /// favoured, non-huntable hunter no longer stops the batch (that was the "fires constantly at low
        /// motivation" noise). When <paramref name="motivationStopPct"/> &gt; 0, that threshold GOVERNS the
        /// whole threat stop: the batch halts only for a hunter whose motivation toward an agent is AT OR
        /// ABOVE the % (level-triggered, so it also fires when the hunter was already above at batch start).
        /// The default danger triggers below the threshold are then suppressed entirely - a game-14 run had
        /// every batch halted at 38-180% motivation against an explicit stopOnThreatMotivation:300, because
        /// the triggers used to be independent stop conditions with no opt-out (G14-#7/#20).
        /// The threshold governs only THIS motivation/danger stop — the heroAttacking stop
        /// (<see cref="EvaluateHeroAttackStop"/>) is a separate condition it never suppresses.
        /// <paramref name="alert"/> gets the per-agent detail (each entry tagged
        /// with a <c>trigger</c>), or null if nothing; <paramref name="reason"/> is the stopReason
        /// ("threatEscalation" for danger, "threatMotivation" for the threshold, else null).</summary>
        public static void EvaluateThreatStop(GameContext ctx, Map map, List<AgentSafetyInfo> before,
            int motivationStopPct, out JsonValue alert, out string reason)
        {
            var now = ComputeAgentSafety(ctx, map);
            var byAgent = new Dictionary<UA, AgentSafetyInfo>();
            if (before != null) foreach (var b in before) byAgent[b.Agent] = b;

            JsonValue alerts = JsonValue.NewArray();
            bool danger = false, motivation = false;
            foreach (var s in now)
            {
                AgentSafetyInfo b;
                bool had = byAgent.TryGetValue(s.Agent, out b);

                bool becameHuntable = s.IsHuntable && (!had || !b.IsHuntable);
                bool gainedHunter = s.TopHunter != null && (!had || b.TopHunter == null) && s.InDanger();
                bool worsened = s.TopHunter != null && had && b.TopHunter != null &&
                                VerdictRank(s.Verdict()) > VerdictRank(b.Verdict());
                bool meaningful = becameHuntable || gainedHunter || worsened;

                int nowPct = s.TopHunter != null ? (int)Math.Round(s.TopMotivation * 100.0) : 0;
                bool atOrAboveThreshold = motivationStopPct > 0 && s.TopHunter != null &&
                                          nowPct >= motivationStopPct;
                // An explicit threshold replaces the default danger triggers instead of adding to them:
                // "stopOnThreatMotivation:300" must mean "do NOT stop below 300".
                if (motivationStopPct > 0) meaningful = false;

                if (meaningful || atOrAboveThreshold)
                {
                    JsonValue a = AgentSafetyJson(ctx, s);
                    a.Set("message", AgentSafetyLine(ctx, s));
                    a.Set("trigger", atOrAboveThreshold ? "motivation"
                        : becameHuntable ? "becameHuntable"
                        : worsened ? "worsened"
                        : "gainedHunter");
                    alerts.Add(a);
                    if (meaningful) danger = true;
                    if (atOrAboveThreshold) motivation = true;
                }
            }

            alert = alerts.Count > 0 ? alerts : JsonValue.Null;
            reason = danger ? "threatEscalation" : (motivation ? "threatMotivation" : null);
        }

        // ---------- end_turn digest ----------

        /// <summary>
        /// Message types that always make end_turn's digest, whoever they are about: the things a player
        /// who looked away for ten turns MUST be told happened. Everything else only qualifies when it
        /// touches one of your own units (see <see cref="NotableTurnEvents"/>).
        /// </summary>
        private static readonly HashSet<UnifiedMessage.messageType> NotableMessageTypes =
            new HashSet<UnifiedMessage.messageType>
            {
                // deaths
                UnifiedMessage.messageType.DEATH, UnifiedMessage.messageType.AGENT_DIES,
                UnifiedMessage.messageType.HERO_DIES, UnifiedMessage.messageType.PROPHET_DIES,
                UnifiedMessage.messageType.KILLED_BY_DANGER, UnifiedMessage.messageType.DEATH_EXPLORING_RUINS,
                // battle / armies
                UnifiedMessage.messageType.BATTLE, UnifiedMessage.messageType.ARMY_ORDERED_TO_ATTACK_ARMY,
                UnifiedMessage.messageType.ARMY_BLOCKS, UnifiedMessage.messageType.ARMY_DRIVES_BACK,
                // razing / territory
                UnifiedMessage.messageType.RAZED_DURING_WAR, UnifiedMessage.messageType.RAZE_ORDER_ISSUED,
                UnifiedMessage.messageType.OPHANIM_RAZE_ORDERS, UnifiedMessage.messageType.ALLIANCE_OUTPOST,
                // war
                UnifiedMessage.messageType.WAR, UnifiedMessage.messageType.CRUSADE,
                UnifiedMessage.messageType.CIVIL_WAR,
                // the loss clock
                UnifiedMessage.messageType.SEALS_REFORGING, UnifiedMessage.messageType.PROPHECY_FULFILLING,
                UnifiedMessage.messageType.CHOSEN_ONE_FUNDED, UnifiedMessage.messageType.CHOSEN_ONE_MENACE,
                // exposure
                UnifiedMessage.messageType.CULTISTS_EXPOSED, UnifiedMessage.messageType.CULTISTS_ARRESTED,
                UnifiedMessage.messageType.SHADOW_MARKET_RAIDED, UnifiedMessage.messageType.SHADOW_DRIVEN_BACK,
                UnifiedMessage.messageType.AGENT_SABOTAGED, UnifiedMessage.messageType.EVIDENCE_REPORTED,
                // being hunted
                UnifiedMessage.messageType.HERO_ATTACKING, UnifiedMessage.messageType.HERO_WITH_ESCORT_ATTACKING,
                UnifiedMessage.messageType.NEMESIS_GAINED, UnifiedMessage.messageType.VENDETTA,
            };

        /// <summary>Types suppressed from the digest even when they concern one of your units, because the
        /// agent already learns them through a dedicated channel or they are pure chatter.</summary>
        private static readonly HashSet<UnifiedMessage.messageType> DigestNoiseTypes =
            new HashSet<UnifiedMessage.messageType>
            {
                UnifiedMessage.messageType.AGENT_IDLE,    // surfaces as the idleAgents decision
                UnifiedMessage.messageType.TUTORIAL,
                UnifiedMessage.messageType.UNIT_ARRIVES,
                // TASK_CANCELLED deliberately NOT here: a mid-cast invalidation (e.g. Dark Empire losing
                // its 100%-shadow capital at 48/50 progress) is exactly the news an unattended player
                // must hear; the `mine` gate keeps it scoped to your own units.
            };

        /// <summary>
        /// The turn's <c>Map.turnUnifiedMessages</c>, filtered to what an unattended player needs to hear:
        /// anything touching one of your own units (tagged <c>mine</c>) plus a fixed high-severity whitelist
        /// (razing, battles, deaths, wars, the seal/prophecy clock). This is the same stream
        /// <c>RecentEventLog.SnapshotTurn</c> archives — the digest is a filtered *view* of it, so anything
        /// here also appears in <c>get_recent_events</c>.
        ///
        /// Must be called in the same window as the snapshot (right after <c>bEndTurn</c>), before the next
        /// <c>turnTick</c> wipes the list. Returns <c>JsonValue.Null</c> when nothing qualifies.
        /// </summary>
        public static JsonValue NotableTurnEvents(GameContext ctx, Map map, int turn)
        {
            if (map == null || map.turnUnifiedMessages == null) return JsonValue.Null;

            // Locations one of your commandable units currently stands on — the cheap, crisp "mine" test
            // for a message whose subject is a place rather than a unit.
            var myLocations = new HashSet<Location>();
            if (map.units != null)
                foreach (Unit u in map.units)
                {
                    if (u == null || u.isDead) continue;
                    bool mine; try { mine = u.isCommandable(); } catch { continue; }
                    if (mine && u.location != null) myLocations.Add(u.location);
                }

            JsonValue arr = JsonValue.NewArray();
            foreach (UnifiedMessage m in map.turnUnifiedMessages)
            {
                if (m == null) continue;
                if (m.customMsgType == null && DigestNoiseTypes.Contains(m.msgType)) continue;

                bool mine = TouchesMine(m.objA, myLocations) || TouchesMine(m.objB, myLocations);
                bool severe = m.customMsgType == null && NotableMessageTypes.Contains(m.msgType);
                if (!mine && !severe) continue;

                JsonValue o = JsonValue.NewObject()
                    .Set("turn", turn)
                    .Set("type", !string.IsNullOrEmpty(m.customMsgType) ? m.customMsgType : m.msgType.ToString())
                    .Set("title", StripRichText(m.title));
                string body = StripRichText(m.message);
                if (!string.IsNullOrEmpty(body)) o.Set("message", body);
                if (mine) o.Set("mine", true);
                arr.Add(o);
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
        }

        /// <summary>True when a unified message's subject is one of your commandable units, or a location
        /// one of them is standing on.</summary>
        private static bool TouchesMine(object obj, HashSet<Location> myLocations)
        {
            Unit u = obj as Unit;
            if (u != null)
            {
                try { return u.isCommandable(); } catch { return false; }
            }
            Location l = obj as Location;
            return l != null && myLocations.Contains(l);
        }

        /// <summary>One of your commandable units as it stood at snapshot time — enough to name it after it
        /// dies, when <c>getName()</c> and <c>location</c> are no longer trustworthy.</summary>
        public sealed class OwnedUnitInfo
        {
            public Unit Unit;
            public string Name;
            public Location Location;
            /// <summary>Set when the unit was travelling to a challenge (Task_GoToPerformChallenge) at
            /// snapshot time — that task nulls itself out with NO game message when the challenge vanishes
            /// or a move fails, so <see cref="EvaluateTaskLoss"/> has to detect the transition itself.</summary>
            public string TravelChallengeName;
        }

        /// <summary>
        /// Snapshot every unit you command. Unlike <see cref="ComputeAgentSafety"/> (which walks only
        /// <c>UA</c> agents) this includes <c>UM</c> military units, so losing an army — the case that is
        /// invisible to the threat early-stop — can be detected.
        /// </summary>
        public static List<OwnedUnitInfo> ComputeOwnedRoster(GameContext ctx, Map map)
        {
            var result = new List<OwnedUnitInfo>();
            if (map == null || map.units == null) return result;
            foreach (Unit u in map.units)
            {
                if (u == null || u.isDead) continue;
                bool mine; try { mine = u.isCommandable(); } catch { continue; }
                if (!mine) continue;
                string travelChallenge = null;
                if (u.task is Task_GoToPerformChallenge travel && travel.challenge != null)
                    travelChallenge = SafeName(() => travel.challenge.getName()) ?? "a challenge";
                result.Add(new OwnedUnitInfo
                {
                    Unit = u,
                    Name = SafeName(() => u.getName()),
                    Location = u.location,
                    TravelChallengeName = travelChallenge
                });
            }
            return result;
        }

        /// <summary>
        /// Compare a roster snapshot to the live map and list the units that died or vanished, as
        /// <c>[{unit,name,lastLocation}]</c>. Only removals count, so recruiting mid-batch is a no-op.
        /// Returns <c>JsonValue.Null</c> when nothing was lost.
        /// </summary>
        public static JsonValue EvaluateUnitLoss(GameContext ctx, Map map, List<OwnedUnitInfo> before)
        {
            if (before == null || before.Count == 0 || map == null) return JsonValue.Null;
            var alive = new HashSet<Unit>();
            if (map.units != null)
                foreach (Unit u in map.units)
                    if (u != null && !u.isDead) alive.Add(u);

            JsonValue arr = JsonValue.NewArray();
            foreach (OwnedUnitInfo b in before)
            {
                if (b == null || b.Unit == null || alive.Contains(b.Unit)) continue;
                JsonValue o = JsonValue.NewObject()
                    .Set("unit", UnitId(ctx, b.Unit))
                    .Set("name", b.Name);
                if (b.Location != null) o.Set("lastLocation", LocationRef(b.Location));
                arr.Add(o);
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
        }

        /// <summary>
        /// Commandable live agents whose banked skill point's NEXT pick is the one-shot
        /// starting-trait (magic mastery) menu — the exact Trait.getAvailableTraits gate
        /// (hasStartingTraits() && !hasAssignedStartingTraits, Trait.cs:81-88).
        /// bEndTurn(force)'s auto-spend (UA.spendSkillPoint) would consume that menu with an
        /// AI pick and set hasAssignedStartingTraits forever, so end_turn treats these as a
        /// real choice that blocks force (G16-#1). person.level == 0 is only a correlate of
        /// "first level-up" and is deliberately not used. Per-agent failures fail OPEN (agent
        /// skipped) so a modded god whose hasStartingTraits() throws degrades to the old
        /// auto-spend instead of wedging end_turn. Empty list when none (never null).
        /// </summary>
        public static List<UA> PendingStartingTraitPicks(GameContext ctx, Map map)
        {
            var result = new List<UA>();
            if (map == null || map.units == null) return result;
            foreach (Unit u in map.units)
            {
                try
                {
                    UA ua = u as UA;
                    if (ua == null || ua.isDead || !ua.isCommandable() || ua.person == null) continue;
                    if (ua.person.skillPoints <= 0 || ua.person.cachedOutOfTraits) continue;
                    if (ua.hasStartingTraits() && !ua.hasAssignedStartingTraits) result.Add(ua);
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// (hunter → target) keys for every hostile hero currently running a Task_AttackUnit
        /// against a unit you benefit from (your commandables, or any UAE — the exact
        /// Overmind.getThreats target test, same as QueryTools.IsHostileToMe). The batch-start
        /// snapshot for <see cref="EvaluateHeroAttackStop"/>.
        /// </summary>
        public static HashSet<string> ComputeAttackPairs(GameContext ctx, Map map)
        {
            var result = new HashSet<string>();
            if (map == null || map.units == null) return result;
            foreach (Unit u in map.units)
            {
                try
                {
                    if (u == null || u.isDead || u.isCommandable()) continue;
                    Task_AttackUnit attack = u.task as Task_AttackUnit;
                    if (attack == null || attack.target == null) continue;
                    if (!(attack.target.isCommandable() || attack.target is UAE)) continue;
                    result.Add(UnitId(ctx, u) + "->" + UnitId(ctx, attack.target));
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// Stop signal for a hero STARTING an attack-pursuit mid-batch: rescans the map and fires
        /// on any (hunter → target) pair absent from the batch-start snapshot. Edge-triggered on
        /// purpose — a hunt already running when the batch began never re-stops every turn; a
        /// fresh batch call sees it in its own snapshot and also stays quiet. This is the one
        /// reaction window before the hunter arrives (reposition, Lay Low, bodyguard, or a
        /// targeting-window power such as Iastur's PW4), and it is deliberately INDEPENDENT of
        /// stopOnThreatMotivation's "threshold replaces the default triggers" rule: game 15/16
        /// batches ran silently through HERO_ATTACKING and lost the window (G16-#4).
        /// <paramref name="reason"/> is "heroAttacking" or null.
        /// </summary>
        public static void EvaluateHeroAttackStop(GameContext ctx, Map map, HashSet<string> before,
            out JsonValue alert, out string reason)
        {
            alert = JsonValue.Null;
            reason = null;
            if (map == null || map.units == null || before == null) return;
            JsonValue arr = JsonValue.NewArray();
            foreach (Unit u in map.units)
            {
                try
                {
                    if (u == null || u.isDead || u.isCommandable()) continue;
                    Task_AttackUnit attack = u.task as Task_AttackUnit;
                    if (attack == null || attack.target == null) continue;
                    if (!(attack.target.isCommandable() || attack.target is UAE)) continue;
                    if (before.Contains(UnitId(ctx, u) + "->" + UnitId(ctx, attack.target))) continue;
                    JsonValue e = JsonValue.NewObject()
                        .Set("hunter", UnitRef(ctx, u))
                        .Set("target", UnitRef(ctx, attack.target))
                        .Set("turnsRemaining", attack.turnsRemaining)
                        .Set("message", SafeName(() => u.getName()) + " has begun hunting " +
                            SafeName(() => attack.target.getName()) + " (" + attack.turnsRemaining +
                            " turns of pursuit left) - this is the reaction window: reposition, " +
                            "Lay Low, bodyguard, or a power that targets attacking heroes.");
                    if (u.location != null) e.Set("location", LocationRef(u.location));
                    arr.Add(e);
                }
                catch { }
            }
            if (arr.Count > 0)
            {
                alert = arr;
                reason = "heroAttacking";
            }
        }

        /// <summary>
        /// Detect units whose travel-to-challenge task silently died since the snapshot.
        /// <c>Task_GoToPerformChallenge.turnTick</c> nulls the task with no UnifiedMessage when the
        /// challenge disappears or a move fails — the one cancellation the game never announces
        /// (a <c>Task_PerformChallenge</c> cancellation on a commandable unit emits TASK_CANCELLED,
        /// which the digest now passes through). Returns digest-shaped events (same fields as
        /// <see cref="NotableTurnEvents"/>, plus <c>synthesized</c>) or <c>JsonValue.Null</c>;
        /// each is also archived into <c>ctx.Events</c> for get_recent_events.
        /// </summary>
        public static JsonValue EvaluateTaskLoss(GameContext ctx, Map map, List<OwnedUnitInfo> before)
        {
            if (before == null || before.Count == 0 || map == null) return JsonValue.Null;
            JsonValue arr = JsonValue.NewArray();
            foreach (OwnedUnitInfo b in before)
            {
                if (b == null || b.Unit == null || b.TravelChallengeName == null) continue;
                if (b.Unit.isDead) continue;                 // deaths are EvaluateUnitLoss's story
                bool stillTasked; try { stillTasked = b.Unit.task != null; } catch { continue; }
                if (stillTasked) continue;                   // arrived (now performing) or retasked
                string title = "Task cancelled";
                string message = b.Name + "'s travel to perform '" + b.TravelChallengeName +
                    "' ended prematurely (the challenge disappeared or the path was blocked); the unit is now idle.";
                arr.Add(JsonValue.NewObject()
                    .Set("turn", map.turn)
                    .Set("type", "TASK_CANCELLED")
                    .Set("title", title)
                    .Set("message", message)
                    .Set("mine", true)
                    .Set("synthesized", true));
                try { ctx.Events.RecordPopup(map.turn, "TASK_CANCELLED", message, "synthesized"); } catch { }
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
        }

        /// <summary>An owned unit's in-progress challenge and the complexity it read at snapshot
        /// time. Security-scaled challenges recompute complexity live, so the target can move
        /// under banked progress with no game message (G19-#3).</summary>
        public sealed class ChallengeSnap
        {
            public Unit Unit;
            public string UnitName;
            public Challenge Challenge;
            public string ChallengeName;
            public double Complexity;
        }

        /// <summary>Snapshot every commandable unit currently performing a definite challenge,
        /// with the complexity quoted at snapshot time. Companion to <see cref="ComputeOwnedRoster"/>.</summary>
        public static List<ChallengeSnap> ComputeChallengeSnaps(GameContext ctx, Map map)
        {
            var result = new List<ChallengeSnap>();
            if (map == null || map.units == null) return result;
            foreach (Unit u in map.units)
            {
                try
                {
                    if (u == null || u.isDead || !u.isCommandable()) continue;
                    if (!(u.task is Task_PerformChallenge pc) || pc.challenge == null) continue;
                    if (pc.challenge.isIndefinite()) continue;
                    result.Add(new ChallengeSnap
                    {
                        Unit = u,
                        UnitName = SafeName(() => u.getName()),
                        Challenge = pc.challenge,
                        ChallengeName = SafeName(() => pc.challenge.getName()),
                        Complexity = pc.challenge.getComplexityAfterDifficulty()
                    });
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// Compare a challenge snapshot to the live map and emit a digest-shaped event for every
        /// owned in-progress challenge whose complexity moved (>= 1.0) since the snapshot — the
        /// silent goalpost shift a lapsing security discount (Bribed Guards) causes (G19-#3).
        /// Informational digest events only, never a stopReason; each is archived into
        /// <c>ctx.Events</c> for get_recent_events. Returns <c>JsonValue.Null</c> when nothing moved.
        /// </summary>
        public static JsonValue EvaluateComplexityShift(GameContext ctx, Map map, List<ChallengeSnap> before)
        {
            if (before == null || before.Count == 0 || map == null) return JsonValue.Null;
            JsonValue arr = JsonValue.NewArray();
            foreach (ChallengeSnap b in before)
            {
                try
                {
                    if (b == null || b.Unit == null || b.Unit.isDead) continue;
                    if (!(b.Unit.task is Task_PerformChallenge pc) || pc.challenge != b.Challenge) continue;
                    double now = pc.challenge.getComplexityAfterDifficulty();
                    if (Math.Abs(now - b.Complexity) < 1.0) continue;
                    string message = b.UnitName + "'s '" + b.ChallengeName + "' complexity moved " +
                        Round2(b.Complexity) + " -> " + Round2(now) + " (progress " + Round2(pc.progress) +
                        ") - usually a security change, e.g. a Bribed Guards discount lapsing silently; " +
                        "progress counts against the LIVE value, so re-check etaTurns.";
                    arr.Add(JsonValue.NewObject()
                        .Set("turn", map.turn)
                        .Set("type", "CHALLENGE_COMPLEXITY_CHANGED")
                        .Set("title", "Challenge complexity changed")
                        .Set("message", message)
                        .Set("mine", true)
                        .Set("synthesized", true));
                    try { ctx.Events.RecordPopup(map.turn, "CHALLENGE_COMPLEXITY_CHANGED", message, "synthesized"); } catch { }
                }
                catch { }
            }
            return arr.Count > 0 ? arr : JsonValue.Null;
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
                    .Set("fullyInfiltrated", Safe(() => st.infiltration >= 1.0, false))
                    .Set("infiltration", Round2(Safe(() => st.infiltration, 0.0)));
                SettlementHuman sh = st as SettlementHuman;
                if (sh != null) sset.Set("population", sh.population);
                Society soc = l.soc as Society;
                if (soc != null && soc.capital == l.index) sset.Set("isCapital", true);
                if (st.subs != null && st.subs.Count > 0)
                {
                    JsonValue subs = JsonValue.NewArray();
                    foreach (Subsettlement sub in st.subs)
                    {
                        JsonValue sv = JsonValue.NewObject()
                            .Set("name", SafeName(() => sub.getName()))
                            .Set("infiltrated", sub.infiltrated);
                        double subMenace = Safe(() => sub.menace, 0.0);
                        if (subMenace != 0.0 || sub is Sub_Vinerva_HeartOfForest)
                            sv.Set("menace", Round2(subMenace));
                        subs.Add(sv);
                    }
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

        /// <summary>
        /// Floor at 2 decimals. Use for the player's POWER balance everywhere it is displayed:
        /// the game floors its display (UITopLeft.cs:44 shows (int)power) while castability
        /// compares the raw double, so Math.Round would show 0.996 as "1" and then refuse a
        /// 1-cost cast with "costs 1, you have 1" (G17-#2). Displayed power must never exceed
        /// castable power. Never use this for the &lt; cost comparison itself - that stays raw.
        /// </summary>
        public static double Round2Down(double v)
        {
            return Math.Floor(v * 100.0) / 100.0;
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }
    }
}
