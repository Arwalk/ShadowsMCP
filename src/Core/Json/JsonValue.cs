using System;
using System.Collections.Generic;

namespace ShadowsMcp.Core.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A dynamic JSON value (tagged union). Hand-rolled so the mod has zero external
    /// dependencies — the game only ships Unity's JsonUtility, which cannot represent
    /// free-form JSON, and bundling Newtonsoft risks version conflicts with other mods.
    /// Object member order is preserved for deterministic output.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }

        private bool _bool;
        private double _double;
        private long _long;
        private bool _isIntegral;
        private string _string;
        private List<JsonValue> _array;
        private List<string> _keys;
        private Dictionary<string, JsonValue> _members;

        private JsonValue() { }

        public static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };
        public static readonly JsonValue True = new JsonValue { Kind = JsonKind.Bool, _bool = true };
        public static readonly JsonValue False = new JsonValue { Kind = JsonKind.Bool, _bool = false };

        public static JsonValue Of(bool b) { return b ? True : False; }
        public static JsonValue Of(long n) { return new JsonValue { Kind = JsonKind.Number, _long = n, _double = n, _isIntegral = true }; }
        public static JsonValue Of(int n) { return Of((long)n); }
        public static JsonValue Of(double n)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) return Null;
            return new JsonValue { Kind = JsonKind.Number, _double = n, _long = (long)n, _isIntegral = false };
        }
        public static JsonValue Of(string s) { return s == null ? Null : new JsonValue { Kind = JsonKind.String, _string = s }; }

        public static JsonValue NewArray() { return new JsonValue { Kind = JsonKind.Array, _array = new List<JsonValue>() }; }
        public static JsonValue NewObject()
        {
            return new JsonValue
            {
                Kind = JsonKind.Object,
                _keys = new List<string>(),
                _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            };
        }

        public static implicit operator JsonValue(bool b) { return Of(b); }
        public static implicit operator JsonValue(int n) { return Of(n); }
        public static implicit operator JsonValue(long n) { return Of(n); }
        public static implicit operator JsonValue(double n) { return Of(n); }
        public static implicit operator JsonValue(string s) { return Of(s); }

        public bool IsNull { get { return Kind == JsonKind.Null; } }

        // ---- object API ----

        public JsonValue Set(string key, JsonValue value)
        {
            RequireKind(JsonKind.Object, "Set");
            if (value == null) value = Null;
            if (!_members.ContainsKey(key)) _keys.Add(key);
            _members[key] = value;
            return this;
        }

        public bool ContainsKey(string key)
        {
            return Kind == JsonKind.Object && _members.ContainsKey(key);
        }

        /// <summary>Remove an object member (no-op when absent). Unlike Set(key, Null), the key is
        /// gone from the serialized output entirely.</summary>
        public JsonValue Remove(string key)
        {
            RequireKind(JsonKind.Object, "Remove");
            if (_members.Remove(key)) _keys.Remove(key);
            return this;
        }

        /// <summary>Object member lookup; returns JsonValue.Null when absent (never throws, never returns null).</summary>
        public JsonValue Get(string key)
        {
            if (Kind != JsonKind.Object) return Null;
            JsonValue v;
            return _members.TryGetValue(key, out v) ? v : Null;
        }

        public JsonValue this[string key] { get { return Get(key); } }

        public IEnumerable<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                if (Kind != JsonKind.Object) yield break;
                foreach (string k in _keys) yield return new KeyValuePair<string, JsonValue>(k, _members[k]);
            }
        }

        // ---- array API ----

        public JsonValue Add(JsonValue value)
        {
            RequireKind(JsonKind.Array, "Add");
            _array.Add(value ?? Null);
            return this;
        }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) return _array.Count;
                if (Kind == JsonKind.Object) return _keys.Count;
                return 0;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array || index < 0 || index >= _array.Count) return Null;
                return _array[index];
            }
        }

        public IEnumerable<JsonValue> Items
        {
            get
            {
                if (Kind != JsonKind.Array) yield break;
                foreach (JsonValue v in _array) yield return v;
            }
        }

        // ---- typed accessors (lenient: return the fallback on kind mismatch) ----

        public string AsString(string fallback = null) { return Kind == JsonKind.String ? _string : fallback; }
        public bool AsBool(bool fallback = false) { return Kind == JsonKind.Bool ? _bool : fallback; }
        public long AsLong(long fallback = 0) { return Kind == JsonKind.Number ? (_isIntegral ? _long : (long)_double) : fallback; }
        public int AsInt(int fallback = 0) { return (int)AsLong(fallback); }
        public double AsDouble(double fallback = 0) { return Kind == JsonKind.Number ? _double : fallback; }
        public bool IsIntegral { get { return Kind == JsonKind.Number && _isIntegral; } }

        internal double RawDouble { get { return _double; } }
        internal long RawLong { get { return _long; } }
        internal string RawString { get { return _string; } }
        internal bool RawBool { get { return _bool; } }

        private void RequireKind(JsonKind kind, string op)
        {
            if (Kind != kind)
                throw new InvalidOperationException("JsonValue." + op + " requires " + kind + " but value is " + Kind);
        }

        public override string ToString() { return JsonWriter.Write(this, false); }
    }
}
