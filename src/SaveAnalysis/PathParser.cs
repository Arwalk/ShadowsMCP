using System.Collections.Generic;

namespace ShadowsMcp.SaveAnalysis
{
    public abstract class PathSegment { }
    public sealed class MemberSegment : PathSegment { public string Name; }
    public sealed class IndexSegment : PathSegment { public int Index; }
    public sealed class KeySegment : PathSegment { public string Key; }

    /// <summary>
    /// Path grammar for save-graph inspection:  root ( "." ident | "[" int "]" | "[" quoted-string "]" )*
    ///
    /// Kept syntax-compatible with the mod's live `inspect` tool (PathEvaluator in
    /// src/Mod/Tools/PathEvaluator.cs) so one path works against both a running game and a save
    /// file; if the grammar changes there it must change here too. The root token additionally
    /// accepts '-' for the same reason PathEvaluator's does (challenge-id roots).
    /// </summary>
    public static class PathParser
    {
        public static bool TryParse(string path, out string rootName, out List<PathSegment> segments, out string error)
        {
            rootName = null;
            segments = new List<PathSegment>();
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

        public static string Describe(PathSegment seg)
        {
            MemberSegment m = seg as MemberSegment;
            if (m != null) return "." + m.Name;
            IndexSegment ix = seg as IndexSegment;
            if (ix != null) return "[" + ix.Index + "]";
            return "[\"" + ((KeySegment)seg).Key + "\"]";
        }

        private static string ReadIdent(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            return pos > start ? s.Substring(start, pos - start) : null;
        }

        private static string ReadRootIdent(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '-')) pos++;
            return pos > start ? s.Substring(start, pos - start) : null;
        }
    }
}
