using System;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Core.Mcp
{
    public sealed class ToolDefinition
    {
        public readonly string Name;
        public readonly string Description;
        /// <summary>JSON Schema for the tool's arguments.</summary>
        public readonly JsonValue InputSchema;
        public readonly Func<JsonValue, ToolResult> Handler;

        public ToolDefinition(string name, string description, JsonValue inputSchema, Func<JsonValue, ToolResult> handler)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema ?? Schema.Object();
            Handler = handler;
        }
    }

    /// <summary>Tiny helpers for building JSON Schema literals.</summary>
    public static class Schema
    {
        public static JsonValue Object(params JsonValue[] namedProps)
        {
            // namedProps come from Prop(...) as ["name", schema, required] triples packed into objects
            JsonValue properties = JsonValue.NewObject();
            JsonValue required = JsonValue.NewArray();
            foreach (JsonValue p in namedProps)
            {
                string name = p["name"].AsString();
                properties.Set(name, p["schema"]);
                if (p["required"].AsBool()) required.Add(name);
            }
            JsonValue schema = JsonValue.NewObject().Set("type", "object").Set("properties", properties);
            if (required.Count > 0) schema.Set("required", required);
            return schema;
        }

        public static JsonValue Prop(string name, JsonValue schema, bool required = false)
        {
            return JsonValue.NewObject().Set("name", name).Set("schema", schema).Set("required", required);
        }

        public static JsonValue String(string description)
        {
            return JsonValue.NewObject().Set("type", "string").Set("description", description);
        }

        public static JsonValue StringEnum(string description, params string[] values)
        {
            JsonValue vals = JsonValue.NewArray();
            foreach (string v in values) vals.Add(v);
            return JsonValue.NewObject().Set("type", "string").Set("description", description).Set("enum", vals);
        }

        public static JsonValue Integer(string description)
        {
            return JsonValue.NewObject().Set("type", "integer").Set("description", description);
        }

        public static JsonValue Number(string description)
        {
            return JsonValue.NewObject().Set("type", "number").Set("description", description);
        }

        public static JsonValue Boolean(string description)
        {
            return JsonValue.NewObject().Set("type", "boolean").Set("description", description);
        }
    }
}
