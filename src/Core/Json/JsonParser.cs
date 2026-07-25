using System;
using System.Globalization;
using System.Text;

namespace ShadowsMcp.Core.Json
{
    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message, int position)
            : base(message + " (at offset " + position + ")") { }
    }

    /// <summary>Recursive-descent JSON parser (RFC 8259). Depth-capped to avoid stack overflow on hostile input.</summary>
    public static class JsonParser
    {
        private const int MaxDepth = 128;

        public static JsonValue Parse(string text)
        {
            return Parse(text, MaxDepth);
        }

        /// <summary>Parse with a caller-chosen depth cap. Callers raising the cap far above the
        /// default must supply the stack to match (run the parse on a thread with a larger stack).</summary>
        public static JsonValue Parse(string text, int maxDepth)
        {
            if (text == null) throw new JsonParseException("null input", 0);
            int pos = 0;
            JsonValue value = ParseValue(text, ref pos, maxDepth);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new JsonParseException("trailing characters after JSON value", pos);
            return value;
        }

        private static JsonValue ParseValue(string s, ref int pos, int depthBudget)
        {
            if (depthBudget < 0) throw new JsonParseException("nesting too deep", pos);
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new JsonParseException("unexpected end of input", pos);

            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depthBudget);
                case '[': return ParseArray(s, ref pos, depthBudget);
                case '"': return JsonValue.Of(ParseString(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return JsonValue.True;
                case 'f': Expect(s, ref pos, "false"); return JsonValue.False;
                case 'n': Expect(s, ref pos, "null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref pos);
                    throw new JsonParseException("unexpected character '" + c + "'", pos);
            }
        }

        private static JsonValue ParseObject(string s, ref int pos, int depthBudget)
        {
            pos++; // '{'
            JsonValue obj = JsonValue.NewObject();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return obj; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new JsonParseException("expected object key", pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new JsonParseException("expected ':'", pos);
                pos++;
                obj.Set(key, ParseValue(s, ref pos, depthBudget - 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonParseException("unterminated object", pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return obj; }
                throw new JsonParseException("expected ',' or '}'", pos);
            }
        }

        private static JsonValue ParseArray(string s, ref int pos, int depthBudget)
        {
            pos++; // '['
            JsonValue arr = JsonValue.NewArray();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return arr; }
            while (true)
            {
                arr.Add(ParseValue(s, ref pos, depthBudget - 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonParseException("unterminated array", pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return arr; }
                throw new JsonParseException("expected ',' or ']'", pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new JsonParseException("unterminated string", pos);
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (pos >= s.Length) throw new JsonParseException("unterminated escape", pos);
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.Length) throw new JsonParseException("truncated \\u escape", pos);
                            int cp;
                            if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                throw new JsonParseException("invalid \\u escape", pos);
                            pos += 4;
                            sb.Append((char)cp); // surrogate pairs arrive as two \u escapes and pair up naturally
                            break;
                        default:
                            throw new JsonParseException("invalid escape '\\" + e + "'", pos);
                    }
                }
                else if (c < 0x20)
                {
                    throw new JsonParseException("raw control character in string", pos);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && s[pos] == '-') pos++;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            bool integral = true;
            if (pos < s.Length && s[pos] == '.')
            {
                integral = false;
                pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
            {
                integral = false;
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            string token = s.Substring(start, pos - start);
            if (integral)
            {
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return JsonValue.Of(l);
                // fall through to double for out-of-range integers
            }
            double d;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new JsonParseException("invalid number '" + token + "'", start);
            return JsonValue.Of(d);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new JsonParseException("invalid literal", pos);
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }
    }
}
