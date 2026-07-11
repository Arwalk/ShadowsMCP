using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;

namespace ShadowsMcp.Tools
{
    /// <summary>The generic "query ANY element" tool, backed by PathEvaluator.</summary>
    public static class InspectTool
    {
        private const int MaxDepth = 5;
        private const int MaxMaxItems = 200;

        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            var evaluator = new PathEvaluator
            {
                MapProvider = () => ctx.Map,
                EntityResolver = id => ctx.ResolveEntity(id),
                EntityStub = obj => ctx.EntityStub(obj)
            };

            host.Register(new ToolDefinition(
                "inspect",
                "Inspect ANY element of the game's object graph by path, via reflection (read-only). " +
                "Roots: 'map', an entity id (L3, U17, P42, SG5, C8), or any field of map (e.g. 'overmind'). " +
                "Segments: .field, [index] for lists, [\"key\"] for dictionaries. " +
                "Examples: map.locations[4].settlement | U17.person.traits | overmind.god | map.turn. " +
                "Expansion dumps fields only; entities beyond 'depth' collapse to {$id,$type,name} stubs you can follow up on.",
                Schema.Object(
                    Schema.Prop("path", Schema.String("Path expression, e.g. map.locations[4].settlement"), required: true),
                    Schema.Prop("depth", Schema.Integer("Expansion depth, 1-" + MaxDepth + " (default 1)")),
                    Schema.Prop("maxItems", Schema.Integer("Max collection items per level, 1-" + MaxMaxItems + " (default 20)"))),
                args =>
                {
                    string path = args["path"].AsString();
                    if (string.IsNullOrEmpty(path)) return ToolResult.Error("missing 'path'");
                    int depth = Clamp(args["depth"].AsInt(1), 1, MaxDepth);
                    int maxItems = Clamp(args["maxItems"].AsInt(20), 1, MaxMaxItems);

                    string error;
                    object value = evaluator.Evaluate(path, out error);
                    if (error != null) return ToolResult.Error(error);

                    return ToolResult.Ok(JsonValue.NewObject()
                        .Set("path", path)
                        .Set("value", evaluator.Serialize(value, depth, maxItems)));
                }));
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
