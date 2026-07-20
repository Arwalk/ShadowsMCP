using System.Globalization;
using System.Text;

namespace ShadowsMcp.Core.Json
{
    public static class JsonWriter
    {
        public static string Write(JsonValue value, bool pretty, bool omitNull = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, pretty, 0, omitNull);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue v, bool pretty, int indent, bool omitNull)
        {
            if (v == null) { sb.Append("null"); return; }
            switch (v.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(v.RawBool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    if (v.IsIntegral) sb.Append(v.RawLong.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(v.RawDouble.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, v.RawString);
                    break;
                case JsonKind.Array:
                    WriteArray(sb, v, pretty, indent, omitNull);
                    break;
                case JsonKind.Object:
                    WriteObject(sb, v, pretty, indent, omitNull);
                    break;
            }
        }

        private static void WriteArray(StringBuilder sb, JsonValue v, bool pretty, int indent, bool omitNull)
        {
            if (v.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            bool first = true;
            foreach (JsonValue item in v.Items)
            {
                // Array elements (including nulls) are kept verbatim to preserve index alignment;
                // omitNull only prunes null members of nested objects.
                if (!first) sb.Append(',');
                first = false;
                if (pretty) NewlineIndent(sb, indent + 1);
                WriteValue(sb, item, pretty, indent + 1, omitNull);
            }
            if (pretty) NewlineIndent(sb, indent);
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, JsonValue v, bool pretty, int indent, bool omitNull)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in v.Members)
            {
                // When omitNull is set, drop keys whose value is null — absent ≡ null for a consumer,
                // and these dominate large list pages (settlement/unit/task refs that are usually null).
                if (omitNull && kv.Value != null && kv.Value.Kind == JsonKind.Null) continue;
                if (!first) sb.Append(',');
                first = false;
                if (pretty) NewlineIndent(sb, indent + 1);
                WriteString(sb, kv.Key);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, kv.Value, pretty, indent + 1, omitNull);
            }
            // "first" is still true iff nothing was written (empty object, or every member omitted).
            if (!first && pretty) NewlineIndent(sb, indent);
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void NewlineIndent(StringBuilder sb, int indent)
        {
            sb.Append('\n');
            for (int i = 0; i < indent; i++) sb.Append("  ");
        }
    }
}
