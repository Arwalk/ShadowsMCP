using System.Collections.Generic;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.SaveAnalysis
{
    /// <summary>
    /// Resolved view over a parsed save-file JSON document (FullSerializer output).
    ///
    /// FullSerializer's reference model: an object's first serialized occurrence may carry
    /// "$id":"n" (added retroactively, so it can appear anywhere in the document); later
    /// occurrences are {"$ref":"n"}. "$type" tags polymorphic instances with the full type
    /// name, "$version" tags migratable ones, and a value that needs metadata but is not
    /// itself a JSON object (a list, a primitive) is wrapped as {"$id"/"$type"...,"$content":value}.
    /// Cycles are legal.
    /// </summary>
    public sealed class SaveGraph
    {
        /// <summary>The raw parsed document — the serialized Map object.</summary>
        public JsonValue Root { get; private set; }

        private readonly Dictionary<string, JsonValue> _byId = new Dictionary<string, JsonValue>();

        private SaveGraph(JsonValue root) { Root = root; }

        /// <summary>One full (iterative — the graph can nest thousands deep) walk indexing $id → node.</summary>
        public static SaveGraph Build(JsonValue root)
        {
            SaveGraph g = new SaveGraph(root);
            Stack<JsonValue> work = new Stack<JsonValue>();
            work.Push(root);
            while (work.Count > 0)
            {
                JsonValue node = work.Pop();
                if (node.Kind == JsonKind.Object)
                {
                    string id = node["$id"].AsString();
                    if (id != null && !g._byId.ContainsKey(id)) g._byId[id] = node;
                    foreach (KeyValuePair<string, JsonValue> member in node.Members) work.Push(member.Value);
                }
                else if (node.Kind == JsonKind.Array)
                {
                    foreach (JsonValue item in node.Items) work.Push(item);
                }
            }
            return g;
        }

        /// <summary>Follows a {"$ref":n} to its defining node. Identity for everything else;
        /// a dangling reference returns the {"$ref"} node itself (never throws).</summary>
        public JsonValue Deref(JsonValue node)
        {
            // A $ref target can itself be... a plain node; refs never chain in FullSerializer,
            // but loop defensively with a small bound rather than trusting that.
            for (int hops = 0; hops < 4; hops++)
            {
                if (node.Kind != JsonKind.Object) return node;
                string refId = node["$ref"].AsString();
                if (refId == null) return node;
                JsonValue target;
                if (!_byId.TryGetValue(refId, out target)) return node; // dangling
                node = target;
            }
            return node;
        }

        /// <summary>True if the node is an unresolvable {"$ref"}.</summary>
        public bool IsDanglingRef(JsonValue node)
        {
            string refId = node.Kind == JsonKind.Object ? node["$ref"].AsString() : null;
            return refId != null && !_byId.ContainsKey(refId);
        }

        /// <summary>The node's data payload: unwraps {"$content":...} metadata wrappers.</summary>
        public JsonValue Payload(JsonValue node)
        {
            if (node.Kind == JsonKind.Object && node.ContainsKey("$content")) return node["$content"];
            return node;
        }

        /// <summary>Short type name from a node's "$type" tag ("Assets.Code.Settlement" → "Settlement"), or null.</summary>
        public static string TypeOf(JsonValue node)
        {
            string full = node.Kind == JsonKind.Object ? node["$type"].AsString() : null;
            if (full == null) return null;
            int dot = full.LastIndexOf('.');
            return dot >= 0 ? full.Substring(dot + 1) : full;
        }

        /// <summary>Full type name from a node's "$type" tag, or null.</summary>
        public static string FullTypeOf(JsonValue node)
        {
            return node.Kind == JsonKind.Object ? node["$type"].AsString() : null;
        }

        // ---------- navigation ----------

        /// <summary>
        /// Navigate a path (PathParser grammar) from the save's root Map object. The root token
        /// is "map" or any top-level field of it ("locations", "overmind", "turn", ...). $refs
        /// are resolved and $content wrappers unwrapped before every step and at the end.
        /// Returns null and sets <paramref name="error"/> on failure.
        /// </summary>
        public JsonValue Navigate(string path, out string error)
        {
            string rootName;
            List<PathSegment> segments;
            if (!PathParser.TryParse(path, out rootName, out segments, out error)) return null;

            JsonValue current;
            if (rootName == "map")
            {
                current = Root;
            }
            else
            {
                current = StepMember(Root, rootName, out error);
                if (error != null)
                {
                    error = "unknown root '" + rootName + "' - use 'map' or a field of map (e.g. locations, units, overmind)";
                    return null;
                }
            }

            foreach (PathSegment seg in segments)
            {
                if (current.IsNull)
                {
                    error = "path hit null before '" + PathParser.Describe(seg) + "'";
                    return null;
                }
                MemberSegment m = seg as MemberSegment;
                if (m != null)
                {
                    current = StepMember(current, m.Name, out error);
                }
                else
                {
                    IndexSegment ix = seg as IndexSegment;
                    if (ix != null) current = StepIndex(current, ix.Index, out error);
                    else current = StepKey(current, ((KeySegment)seg).Key, out error);
                }
                if (error != null) return null;
            }
            return Deref(current);
        }

        private JsonValue StepMember(JsonValue node, string name, out string error)
        {
            error = null;
            JsonValue payload = Payload(Deref(node));
            if (payload.Kind != JsonKind.Object)
            {
                error = "cannot read field '" + name + "' of a " + KindWord(payload);
                return JsonValue.Null;
            }
            if (!payload.ContainsKey(name))
            {
                string type = TypeOf(payload);
                error = "no field '" + name + "' on " + (type ?? "this object");
                return JsonValue.Null;
            }
            return payload[name];
        }

        private JsonValue StepIndex(JsonValue node, int index, out string error)
        {
            error = null;
            JsonValue payload = Payload(Deref(node));
            if (payload.Kind != JsonKind.Array)
            {
                error = KindWord(payload) + " is not indexable";
                return JsonValue.Null;
            }
            if (index < 0 || index >= payload.Count)
            {
                error = "index " + index + " out of range (count " + payload.Count + ")";
                return JsonValue.Null;
            }
            return payload[index];
        }

        private JsonValue StepKey(JsonValue node, string key, out string error)
        {
            error = null;
            JsonValue payload = Payload(Deref(node));
            // FullSerializer writes string-keyed dictionaries as JSON objects.
            if (payload.Kind == JsonKind.Object)
            {
                if (payload.ContainsKey(key)) return payload[key];
                error = "key \"" + key + "\" not found in dictionary";
                return JsonValue.Null;
            }
            error = KindWord(payload) + " is not a dictionary (use [int] or .field)";
            return JsonValue.Null;
        }

        private static string KindWord(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "bool";
                case JsonKind.Number: return "number";
                case JsonKind.String: return "string";
                case JsonKind.Array: return "array";
                default: return "object";
            }
        }

        // ---------- bounded rendering ----------

        /// <summary>
        /// Bounded, cycle-safe view of a node, shaped like the live `inspect` tool's output:
        /// rendered objects are plain field maps plus a short "$type" tag; $version/$content
        /// wrappers are flattened; $refs are followed; beyond <paramref name="depth"/> levels
        /// non-primitives collapse to stubs; arrays list at most <paramref name="maxItems"/>
        /// entries; revisiting a node already on the render stack yields a cycle marker.
        /// </summary>
        public JsonValue Render(JsonValue node, int depth, int maxItems)
        {
            return RenderInner(node, depth, maxItems, new HashSet<string>());
        }

        private JsonValue RenderInner(JsonValue node, int depth, int maxItems, HashSet<string> onStack)
        {
            if (IsDanglingRef(node)) return JsonValue.Of("<unresolved $ref:" + node["$ref"].AsString() + ">");
            node = Deref(node);

            switch (node.Kind)
            {
                case JsonKind.Null:
                case JsonKind.Bool:
                case JsonKind.Number:
                case JsonKind.String:
                    return node;
            }

            string id = node.Kind == JsonKind.Object ? node["$id"].AsString() : null;
            if (id != null && onStack.Contains(id))
                return JsonValue.Of("<cycle: $id=" + id + (TypeOf(node) != null ? " " + TypeOf(node) : "") + ">");

            if (depth <= 0) return Stub(node);

            if (id != null) onStack.Add(id);
            try
            {
                JsonValue payload = Payload(node);
                if (payload.Kind == JsonKind.Array)
                {
                    JsonValue items = JsonValue.NewArray();
                    int n = 0;
                    foreach (JsonValue item in payload.Items)
                    {
                        if (n++ >= maxItems)
                        {
                            return JsonValue.NewObject()
                                .Set("count", payload.Count)
                                .Set("items", items)
                                .Set("truncated", true);
                        }
                        items.Add(RenderInner(item, depth - 1, maxItems, onStack));
                    }
                    return items;
                }
                if (!ReferenceEquals(payload, node))
                {
                    // Non-object payload under a metadata wrapper (primitive with $type/$id).
                    JsonValue rendered = RenderInner(payload, depth, maxItems, onStack);
                    string wrapType = TypeOf(node);
                    if (wrapType == null) return rendered;
                    return JsonValue.NewObject().Set("$type", wrapType).Set("value", rendered);
                }

                JsonValue result = JsonValue.NewObject();
                string type = TypeOf(node);
                if (type != null) result.Set("$type", type);
                foreach (KeyValuePair<string, JsonValue> member in node.Members)
                {
                    if (member.Key.Length > 0 && member.Key[0] == '$') continue;
                    result.Set(member.Key, RenderInner(member.Value, depth - 1, maxItems, onStack));
                }
                return result;
            }
            finally
            {
                if (id != null) onStack.Remove(id);
            }
        }

        private JsonValue Stub(JsonValue node)
        {
            JsonValue payload = Payload(node);
            if (payload.Kind == JsonKind.Array) return JsonValue.Of("<array[" + payload.Count + "]>");
            string type = TypeOf(node);
            string id = node["$id"].AsString();
            if (type == null && id == null) return JsonValue.Of("<object>");
            JsonValue stub = JsonValue.NewObject();
            if (type != null) stub.Set("$type", type);
            if (id != null) stub.Set("$id", id);
            return stub;
        }
    }
}
