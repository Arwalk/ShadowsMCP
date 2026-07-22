using System;
using System.Collections.Generic;
using System.Text;
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

        private readonly HashSet<string> _propSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _requiredNames = new List<string>();
        /// <summary>e.g. "unitId (required), locationId (required), force"</summary>
        private readonly string _paramsDescription;

        public ToolDefinition(string name, string description, JsonValue inputSchema, Func<JsonValue, ToolResult> handler)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema ?? Schema.Object();
            Handler = handler;

            foreach (JsonValue req in InputSchema["required"].Items) _requiredNames.Add(req.AsString());
            StringBuilder desc = new StringBuilder();
            foreach (KeyValuePair<string, JsonValue> prop in InputSchema["properties"].Members)
            {
                _propSet.Add(prop.Key);
                if (desc.Length > 0) desc.Append(", ");
                desc.Append(prop.Key);
                if (_requiredNames.Contains(prop.Key)) desc.Append(" (required)");
            }
            _paramsDescription = desc.ToString();
        }

        /// <summary>Null when args are acceptable; a ToolResult.Error naming the problem otherwise.</summary>
        public ToolResult ValidateArguments(JsonValue args)
        {
            // Non-object args are treated as empty, so only the missing-required branch can fire.
            bool isObject = args != null && args.Kind == JsonKind.Object;

            List<string> missing = null;
            foreach (string req in _requiredNames)
            {
                if (isObject && args.ContainsKey(req) && !args[req].IsNull) continue;
                if (missing == null) missing = new List<string>();
                missing.Add(req);
            }

            List<string> unknown = null;
            if (isObject)
            {
                foreach (KeyValuePair<string, JsonValue> kv in args.Members)
                {
                    // Keys starting with '_' are reserved for client metadata (e.g. _meta).
                    if (_propSet.Contains(kv.Key) || kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (unknown == null) unknown = new List<string>();
                    unknown.Add(kv.Key);
                }
            }

            if (missing == null && unknown == null) return null;

            if (_propSet.Count == 0)
                return ToolResult.Error("'" + Name + "' takes no parameters (got '" + string.Join("', '", unknown) + "')");

            StringBuilder msg = new StringBuilder();
            if (missing != null)
                msg.Append("missing required parameter(s): ").Append(string.Join(", ", missing));
            if (unknown != null)
            {
                if (msg.Length > 0) msg.Append("; ");
                msg.Append(unknown.Count == 1 ? "unknown parameter " : "unknown parameter(s): ");
                msg.Append("'").Append(string.Join("', '", unknown)).Append("'");
            }
            msg.Append(". Valid parameters: ").Append(_paramsDescription);
            return ToolResult.Error(msg.ToString());
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
