using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// Read-only path navigation and reflection serialization over the live object graph —
    /// the engine behind the `inspect` tool ("query ANY element").
    ///
    /// Path grammar:  root ( "." ident | "[" int "]" | "[" quoted-string "]" )*
    ///   root = "map" | an entity id (L3, U17, P42, SG5, or a challenge id like
    ///          C31-Ch_Elf_ElderBirthright-92486fbb) | any member of map (e.g. "overmind")
    ///   The root token additionally accepts '-' so deterministic challenge ids parse;
    ///   member names after '.' stay strict ([A-Za-z0-9_]).
    ///
    /// Safety rules:
    ///   - Navigation reads fields first, then property getters, only when NAMED in the path.
    ///   - Bulk expansion serializes FIELDS ONLY (never property getters, never methods),
    ///     so lazily-computed game properties cannot run as a side effect of a dump.
    /// </summary>
    public sealed class PathEvaluator
    {
        /// <summary>Root object provider ("map").</summary>
        public Func<object> MapProvider;
        /// <summary>Resolves entity ids (L3/U17/P42/SG5/C8) to game objects; null if unknown.</summary>
        public Func<string, object> EntityResolver;
        /// <summary>Returns a {$id,$type,name} stub for registered entity types, or null for plain objects.</summary>
        public Func<object, JsonValue> EntityStub;
        /// <summary>Returns a short label for objects that must never be expanded when embedded in
        /// another object's fields (world-sized back-references like Map, or Unity engine objects);
        /// null to serialize normally. Only consulted below the serialization root, so navigating
        /// TO such an object (e.g. path "map") still dumps it.</summary>
        public Func<object, string> BackRefLabel;

        private const int MaxFieldsPerObject = 200;

        // ---------- path parsing ----------

        private abstract class Segment { }
        private sealed class MemberSegment : Segment { public string Name; }
        private sealed class IndexSegment : Segment { public int Index; }
        private sealed class KeySegment : Segment { public string Key; }

        public object Evaluate(string path, out string error)
        {
            error = null;
            string rootName;
            List<Segment> segments;
            if (!TryParse(path, out rootName, out segments, out error)) return null;

            object current = ResolveRoot(rootName, out error);
            if (error != null) return null;

            foreach (Segment seg in segments)
            {
                if (current == null)
                {
                    error = "path hit null before '" + Describe(seg) + "'";
                    return null;
                }
                current = Step(current, seg, out error);
                if (error != null) return null;
            }
            return current;
        }

        private object ResolveRoot(string rootName, out string error)
        {
            error = null;
            object map = MapProvider != null ? MapProvider() : null;

            if (rootName == "map")
            {
                if (map == null) error = "no game in progress - start or load a game first";
                return map;
            }

            object entity = EntityResolver != null ? EntityResolver(rootName) : null;
            if (entity != null) return entity;

            // Fall back to treating the root as a member of map ("overmind", "turn", ...)
            if (map == null)
            {
                error = "no game in progress - start or load a game first";
                return null;
            }
            object value;
            if (TryGetMember(map, rootName, out value)) return value;

            error = "unknown root '" + rootName + "' - use 'map', an entity id (L3, U17, P42, SG5, C8), or a field of map";
            return null;
        }

        private object Step(object current, Segment seg, out string error)
        {
            error = null;
            MemberSegment m = seg as MemberSegment;
            if (m != null)
            {
                object value;
                if (TryGetMember(current, m.Name, out value)) return value;
                error = "no field or property '" + m.Name + "' on " + current.GetType().Name;
                return null;
            }

            IndexSegment ix = seg as IndexSegment;
            if (ix != null)
            {
                IList list = current as IList;
                if (list != null)
                {
                    if (ix.Index < 0 || ix.Index >= list.Count)
                    {
                        error = "index " + ix.Index + " out of range (count " + list.Count + ")";
                        return null;
                    }
                    return list[ix.Index];
                }
                IDictionary dictI = current as IDictionary;
                if (dictI != null)
                {
                    if (dictI.Contains(ix.Index)) return dictI[ix.Index];
                    error = "key " + ix.Index + " not found in dictionary";
                    return null;
                }
                IEnumerable en = current as IEnumerable;
                if (en != null)
                {
                    int i = 0;
                    foreach (object item in en)
                    {
                        if (i++ == ix.Index) return item;
                    }
                    error = "index " + ix.Index + " out of range (enumerated " + i + " items)";
                    return null;
                }
                error = current.GetType().Name + " is not indexable";
                return null;
            }

            KeySegment k = (KeySegment)seg;
            IDictionary dict = current as IDictionary;
            if (dict != null)
            {
                if (dict.Contains(k.Key)) return dict[k.Key];
                // dictionaries keyed by game objects: match on the key's ToString/name
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key != null && entry.Key.ToString() == k.Key) return entry.Value;
                }
                error = "key \"" + k.Key + "\" not found in dictionary";
                return null;
            }
            error = current.GetType().Name + " is not a dictionary (use [int] or .field)";
            return null;
        }

        private static string Describe(Segment seg)
        {
            MemberSegment m = seg as MemberSegment;
            if (m != null) return "." + m.Name;
            IndexSegment ix = seg as IndexSegment;
            if (ix != null) return "[" + ix.Index + "]";
            return "[\"" + ((KeySegment)seg).Key + "\"]";
        }

        private static bool TryParse(string path, out string rootName, out List<Segment> segments, out string error)
        {
            rootName = null;
            segments = new List<Segment>();
            error = null;
            if (string.IsNullOrEmpty(path)) { error = "empty path"; return false; }

            int pos = 0;
            rootName = ReadRootIdent(path, ref pos);
            if (rootName == null) { error = "path must start with an identifier"; return false; }

            while (pos < path.Length)
            {
                char c = path[pos];
                if (c == '.')
                {
                    pos++;
                    string name = ReadIdent(path, ref pos);
                    if (name == null) { error = "expected field name after '.' at offset " + pos; return false; }
                    segments.Add(new MemberSegment { Name = name });
                }
                else if (c == '[')
                {
                    pos++;
                    if (pos < path.Length && (path[pos] == '"' || path[pos] == '\''))
                    {
                        char quote = path[pos++];
                        int start = pos;
                        while (pos < path.Length && path[pos] != quote) pos++;
                        if (pos >= path.Length) { error = "unterminated string key"; return false; }
                        segments.Add(new KeySegment { Key = path.Substring(start, pos - start) });
                        pos++; // closing quote
                    }
                    else
                    {
                        int start = pos;
                        if (pos < path.Length && path[pos] == '-') pos++;
                        while (pos < path.Length && char.IsDigit(path[pos])) pos++;
                        int index;
                        if (pos == start || !int.TryParse(path.Substring(start, pos - start), out index))
                        {
                            error = "expected integer or quoted string inside [] at offset " + start;
                            return false;
                        }
                        segments.Add(new IndexSegment { Index = index });
                    }
                    if (pos >= path.Length || path[pos] != ']') { error = "expected ']' at offset " + pos; return false; }
                    pos++;
                }
                else
                {
                    error = "unexpected character '" + c + "' at offset " + pos;
                    return false;
                }
            }
            return true;
        }

        private static string ReadIdent(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            return pos > start ? s.Substring(start, pos - start) : null;
        }

        /// <summary>Root tokens also accept '-' so deterministic challenge ids
        /// ("C31-Ch_Elf_ElderBirthright-92486fbb") can be inspected directly.</summary>
        private static string ReadRootIdent(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '-')) pos++;
            return pos > start ? s.Substring(start, pos - start) : null;
        }

        // ---------- member access ----------

        private const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Field first (game state lives in public fields), then parameterless property getter.</summary>
        public static bool TryGetMember(object target, string name, out object value)
        {
            value = null;
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, MemberFlags | BindingFlags.DeclaredOnly);
                if (f != null) { value = f.GetValue(target); return true; }
            }
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, MemberFlags | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    value = p.GetValue(target, null);
                    return true;
                }
            }
            return false;
        }

        // ---------- reflection serialization ----------

        public JsonValue Serialize(object obj, int depth, int maxItems)
        {
            return SerializeInner(obj, depth, maxItems, new HashSet<object>(ReferenceComparer.Instance), isRoot: true);
        }

        private JsonValue SerializeInner(object obj, int depth, int maxItems, HashSet<object> visited, bool isRoot = false)
        {
            if (obj == null) return JsonValue.Null;

            // World-sized back-references (Map, Unity engine objects) embedded in another object's
            // fields would dump the entire game state; collapse them regardless of remaining depth.
            // The evaluated path result itself (isRoot) is exempt so navigating to them still works.
            if (!isRoot && BackRefLabel != null)
            {
                string backRef = BackRefLabel(obj);
                if (backRef != null) return JsonValue.Of(backRef);
            }

            Type type = obj.GetType();
            if (obj is string) return JsonValue.Of((string)obj);
            if (obj is bool) return JsonValue.Of((bool)obj);
            if (obj is int || obj is long || obj is short || obj is byte || obj is sbyte ||
                obj is uint || obj is ushort)
                return JsonValue.Of(Convert.ToInt64(obj));
            if (obj is ulong) return JsonValue.Of((double)(ulong)obj);
            if (obj is float || obj is double || obj is decimal)
                return JsonValue.Of(Convert.ToDouble(obj));
            if (obj is char) return JsonValue.Of(obj.ToString());
            if (type.IsEnum) return JsonValue.Of(obj.ToString());

            // Registered entities beyond the requested depth collapse to an id stub the
            // client can follow up on with get_* tools or another inspect call.
            JsonValue stub = EntityStub != null ? EntityStub(obj) : null;
            if (stub != null && depth <= 0) return stub;

            if (depth <= 0)
                return JsonValue.Of("<" + TypeName(type) + ">");

            if (!type.IsValueType)
            {
                if (visited.Contains(obj))
                    return JsonValue.Of("<cycle: " + TypeName(type) + ">");
                visited.Add(obj);
            }

            try
            {
                IDictionary dict = obj as IDictionary;
                if (dict != null) return SerializeDictionary(dict, depth, maxItems, visited);

                IEnumerable enumerable = obj as IEnumerable;
                if (enumerable != null) return SerializeSequence(enumerable, depth, maxItems, visited);

                return SerializeObject(obj, type, stub, depth, maxItems, visited);
            }
            finally
            {
                if (!type.IsValueType) visited.Remove(obj);
            }
        }

        private JsonValue SerializeDictionary(IDictionary dict, int depth, int maxItems, HashSet<object> visited)
        {
            JsonValue result = JsonValue.NewObject().Set("$type", TypeName(dict.GetType())).Set("count", dict.Count);
            JsonValue entries = JsonValue.NewObject();
            int n = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (n++ >= maxItems) { result.Set("truncated", true); break; }
                string key = entry.Key == null ? "null" : entry.Key.ToString();
                entries.Set(key, SerializeInner(entry.Value, depth - 1, maxItems, visited));
            }
            result.Set("entries", entries);
            return result;
        }

        private JsonValue SerializeSequence(IEnumerable seq, int depth, int maxItems, HashSet<object> visited)
        {
            JsonValue items = JsonValue.NewArray();
            int n = 0;
            bool truncated = false;
            foreach (object item in seq)
            {
                if (n++ >= maxItems) { truncated = true; break; }
                items.Add(SerializeInner(item, depth - 1, maxItems, visited));
            }
            if (!truncated) return items;
            ICollection coll = seq as ICollection;
            return JsonValue.NewObject()
                .Set("count", coll != null ? coll.Count : -1)
                .Set("items", items)
                .Set("truncated", true);
        }

        private JsonValue SerializeObject(object obj, Type type, JsonValue stub, int depth, int maxItems, HashSet<object> visited)
        {
            JsonValue result = JsonValue.NewObject().Set("$type", TypeName(type));
            if (stub != null && stub.ContainsKey("$id")) result.Set("$id", stub["$id"]);

            int n = 0;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(MemberFlags | BindingFlags.DeclaredOnly))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    if (f.Name.IndexOf("k__BackingField", StringComparison.Ordinal) >= 0) continue;
                    if (n++ >= MaxFieldsPerObject) { result.Set("$truncatedFields", true); return result; }
                    object value;
                    try { value = f.GetValue(obj); }
                    catch (Exception ex) { result.Set(f.Name, "<error: " + ex.GetType().Name + ">"); continue; }
                    result.Set(f.Name, SerializeInner(value, depth - 1, maxItems, visited));
                }
            }
            return result;
        }

        private static string TypeName(Type t)
        {
            return t.Name;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
