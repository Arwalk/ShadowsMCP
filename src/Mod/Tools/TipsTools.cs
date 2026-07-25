using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Tips;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// The get_tips query tool: a curated, agent-facing catalog of the game's mechanics (see
    /// <see cref="TipCatalog"/>). The catalog is static, so the handler needs no map; it still runs on the
    /// main thread (via GameToolHost) because the few param-driven tips read live game params.
    /// </summary>
    public static class TipsTools
    {
        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            host.Register(new ToolDefinition(
                "get_tips",
                "Curated mechanic explanations written for an agent (infiltration, politics, magic, economy, "
                + "world meters, tactics, god/faction rules). No args = the index (id + title + summary); "
                + "id = one tip's full text; category = a whole topic. Relevant tips also surface "
                + "automatically in game_overview / end_turn, and the core ones are in the initialize "
                + "instructions - this is the on-demand reference.",
                Schema.Object(
                    Schema.Prop("id", Schema.String("Return the full text of this one tip (ids are in the index, e.g. infiltration, world_panic, dark_empire).")),
                    Schema.Prop("category", Schema.StringEnum("Return every tip in this topic.", TipCatalog.Categories))),
                a =>
                {
                    string id = a["id"].AsString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        JsonValue one = TipEngine.ById(ctx, id);
                        return one.IsNull
                            ? ToolResult.Error("unknown tip id: " + id + " - call get_tips with no arguments for the list")
                            : ToolResult.Ok(one);
                    }

                    string category = a["category"].AsString();
                    if (!string.IsNullOrEmpty(category))
                        return ToolResult.Ok(TipEngine.ByCategory(ctx, category));

                    return ToolResult.Ok(TipEngine.Index());
                }));
        }
    }
}
