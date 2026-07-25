using System.Collections.Generic;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.SaveAnalysis
{
    /// <summary>
    /// High-level report over a save graph, loosely mirroring the live game_overview tool's
    /// naming so playthrough logs and save post-mortems read alike. Field names are the game's
    /// serialized public fields (see decompiled/Assets/Code/Map.cs and Overmind.cs); anything
    /// absent from a save renders as null rather than failing.
    ///
    /// Serialized location/person/socialGroup indices are exactly the live L#/P#/SG# ids, so
    /// numbers here can be cross-referenced with a playthrough transcript. U#/C# ids are
    /// per-session and cannot be recovered from a save.
    /// </summary>
    public static class SaveSummary
    {
        private static readonly string[] VictoryModeNames =
        {
            "shadow", "insanity", "dark empire", "ruin", "frozen", "deep ones",
        };

        public static JsonValue Build(SaveGraph g)
        {
            JsonValue map = g.Root;

            JsonValue result = JsonValue.NewObject()
                .Set("turn", map["turn"])
                .Set("seed", map["seed"])
                .Set("endless", map["opt_endless"])
                .Set("tutorial", map["tutorial"])
                .Set("worldPanic", map["worldPanic"])
                .Set("awarenessOfUnderground", map["awarenessOfUnderground"])
                .Set("victoryProgress", map["data_victoryProgess"]) // sic - the game's field name
                .Set("avrgEnshadowment", map["data_avrgEnshadowment"])
                .Set("wars", CountOf(g, map["wars"]));

            result.Set("god", GodBlock(g, map));
            result.Set("counts", JsonValue.NewObject()
                .Set("locations", CountOf(g, map["locations"]))
                .Set("units", CountOf(g, map["units"]))
                .Set("persons", CountOf(g, map["persons"]))
                .Set("socialGroups", CountOf(g, map["socialGroups"]))
                .Set("houses", CountOf(g, map["houses"]))
                .Set("cultures", CountOf(g, map["cultures"])));

            Dictionary<long, string> personNames = PersonNames(g, map);
            result.Set("agents", Agents(g, map, personNames));
            result.Set("socialGroups", SocialGroups(g, map));
            result.Set("mods", Mods(g, map));

            result.Set("note",
                "raw saved state; live/computed values (threats, agent caps, display names from getName()) " +
                "are not stored in saves. locations/persons/socialGroups indices equal the live L#/P#/SG# ids; " +
                "U#/C# ids are per-session and not recoverable.");
            return result;
        }

        private static JsonValue GodBlock(SaveGraph g, JsonValue map)
        {
            JsonValue overmind = g.Deref(map["overmind"]);
            if (overmind.Kind != JsonKind.Object) return JsonValue.Null;
            JsonValue god = g.Deref(overmind["god"]);

            long mode = overmind["victoryMode"].AsLong(-1);
            string modeName = mode >= 0 && mode < VictoryModeNames.Length ? VictoryModeNames[mode] : "unknown";

            return JsonValue.NewObject()
                .Set("god", SaveGraph.TypeOf(god) ?? "unknown")
                .Set("power", overmind["power"])
                .Set("sealsBroken", overmind["sealsBroken"])
                .Set("sealProgress", overmind["sealProgress"])
                .Set("availableEnthrallments", overmind["availableEnthrallments"])
                .Set("nEnthralled", overmind["nEnthralled"])
                .Set("victoryMode", overmind["victoryMode"])
                .Set("victoryModeName", modeName)
                .Set("endOfGameAchieved", overmind["endOfGameAchieved"])
                .Set("victoryAchieved", overmind["victoryAchieved"]);
        }

        private static Dictionary<long, string> PersonNames(SaveGraph g, JsonValue map)
        {
            Dictionary<long, string> names = new Dictionary<long, string>();
            foreach (JsonValue entry in g.Payload(g.Deref(map["persons"])).Items)
            {
                JsonValue person = g.Deref(entry);
                if (person.Kind != JsonKind.Object) continue;
                long index = person["index"].AsLong(-1);
                string firstName = person["firstName"].AsString();
                if (index >= 0 && firstName != null && !names.ContainsKey(index)) names[index] = firstName;
            }
            return names;
        }

        /// <summary>The player's agents: units whose type starts with "UA" (agent hierarchy).</summary>
        private static JsonValue Agents(SaveGraph g, JsonValue map, Dictionary<long, string> personNames)
        {
            JsonValue agents = JsonValue.NewArray();
            foreach (JsonValue entry in g.Payload(g.Deref(map["units"])).Items)
            {
                JsonValue unit = g.Deref(entry);
                string type = SaveGraph.TypeOf(unit);
                if (type == null || !type.StartsWith("UA")) continue;

                long personId = unit["personID"].AsLong(-1);
                string name;
                if (!personNames.TryGetValue(personId, out name)) name = null;

                agents.Add(JsonValue.NewObject()
                    .Set("type", type)
                    .Set("name", name)
                    .Set("personIndex", personId >= 0 ? JsonValue.Of(personId) : JsonValue.Null)
                    .Set("locationIndex", unit["locIndex"])
                    .Set("hp", unit["hp"])
                    .Set("isDead", unit["isDead"]));
            }
            return agents;
        }

        private static JsonValue SocialGroups(SaveGraph g, JsonValue map)
        {
            // Location counts per group come from the locations' back-pointers, since a
            // group's own location list is a live computed property, not saved state.
            Dictionary<long, int> locationCounts = new Dictionary<long, int>();
            foreach (JsonValue entry in g.Payload(g.Deref(map["locations"])).Items)
            {
                JsonValue soc = g.Deref(g.Deref(entry)["soc"]);
                long socIndex = soc["index"].AsLong(-1);
                if (socIndex < 0) continue;
                int count;
                locationCounts.TryGetValue(socIndex, out count);
                locationCounts[socIndex] = count + 1;
            }

            JsonValue groups = JsonValue.NewArray();
            foreach (JsonValue entry in g.Payload(g.Deref(map["socialGroups"])).Items)
            {
                JsonValue sg = g.Deref(entry);
                if (sg.Kind != JsonKind.Object) continue;
                long index = sg["index"].AsLong(-1);
                int locations;
                locationCounts.TryGetValue(index, out locations);
                groups.Add(JsonValue.NewObject()
                    .Set("index", sg["index"])
                    .Set("type", SaveGraph.TypeOf(sg))
                    .Set("name", sg["name"])
                    .Set("locationCount", locations));
            }
            return groups;
        }

        /// <summary>Which mod kernels the save was made with — full type names, since the
        /// namespace is what identifies the mod.</summary>
        private static JsonValue Mods(SaveGraph g, JsonValue map)
        {
            JsonValue mods = JsonValue.NewArray();
            foreach (JsonValue entry in g.Payload(g.Deref(map["mods"])).Items)
            {
                JsonValue kernel = g.Deref(entry);
                mods.Add(SaveGraph.FullTypeOf(kernel) ?? "unknown");
            }
            return mods;
        }

        private static JsonValue CountOf(SaveGraph g, JsonValue listNode)
        {
            JsonValue payload = g.Payload(g.Deref(listNode));
            return payload.Kind == JsonKind.Array ? JsonValue.Of(payload.Count) : JsonValue.Null;
        }
    }
}
