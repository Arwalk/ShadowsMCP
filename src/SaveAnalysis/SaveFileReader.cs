using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.SaveAnalysis
{
    public sealed class SaveFileInfo
    {
        public string FileName;
        public string FullPath;
        /// <summary>"quicksave" | "autosave" | "named".</summary>
        public string Kind;
        /// <summary>First line of the file, e.g. "Version;2;0".</summary>
        public string VersionLine;
        public long SizeBytes;
        public DateTime ModifiedUtc;
    }

    /// <summary>
    /// Reads Shadows of Forbidden Gods save files (*.sv). On-disk format (World.save):
    /// a "Version;2;0" line, the SAVEFILEDATAHEADER marker line, then the whole Map object
    /// graph as minified FullSerializer JSON. Uncompressed. Note *.mapsv files in the same
    /// folder are scenario scripts (key;value lines for genMapFromCustom), not saves.
    /// </summary>
    public static class SaveFileReader
    {
        public const string HeaderMarker = "SAVEFILEDATAHEADER";
        public const string ExpectedVersionLine = "Version;2;0";

        /// <summary>Save graphs nest along the serializer's depth-first walk of the object
        /// graph, far past JsonParser's default cap; the parse below supplies the stack to match.</summary>
        public const int SaveMaxDepth = 4096;
        private const int ParseThreadStackBytes = 64 * 1024 * 1024;

        /// <summary>The game's save folder: ApplicationData/ShadowsForbiddenGodsSaves (World.saveFolder).</summary>
        public static string DefaultSaveFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ShadowsForbiddenGodsSaves");
        }

        /// <summary>All *.sv files in the folder, newest first.</summary>
        public static List<SaveFileInfo> ListSaves(string folder)
        {
            List<SaveFileInfo> result = new List<SaveFileInfo>();
            if (!Directory.Exists(folder)) return result;
            foreach (string path in Directory.GetFiles(folder, "*.sv"))
            {
                FileInfo fi = new FileInfo(path);
                result.Add(new SaveFileInfo
                {
                    FileName = fi.Name,
                    FullPath = fi.FullName,
                    Kind = KindOf(fi.Name),
                    VersionLine = ReadVersionLine(path),
                    SizeBytes = fi.Length,
                    ModifiedUtc = fi.LastWriteTimeUtc,
                });
            }
            result.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
            return result;
        }

        public static string KindOf(string fileName)
        {
            if (string.Equals(fileName, "quicksave.sv", StringComparison.OrdinalIgnoreCase)) return "quicksave";
            if (fileName.StartsWith("Autosave", StringComparison.OrdinalIgnoreCase)) return "autosave";
            return "named";
        }

        private static string ReadVersionLine(string path)
        {
            try
            {
                using (StreamReader reader = new StreamReader(path))
                {
                    string line = reader.ReadLine();
                    return line != null ? line.TrimEnd('\r') : null;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads and parses a save file into a SaveGraph. Throws IOException/FormatException with
        /// a prescriptive message on unreadable or non-save files; a surprising (but parseable)
        /// version line comes back as <paramref name="warning"/> instead of failing.
        /// </summary>
        public static SaveGraph Read(string fullPath, out string versionLine, out string warning)
        {
            warning = null;
            string text = File.ReadAllText(fullPath);

            int marker = text.IndexOf(HeaderMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                string hint = fullPath.EndsWith(".mapsv", StringComparison.OrdinalIgnoreCase)
                    ? " (.mapsv files are scenario scripts, not saves; only .sv files contain game state)"
                    : "";
                throw new FormatException("not a save file: no " + HeaderMarker + " marker in " + Path.GetFileName(fullPath) + hint);
            }

            versionLine = text.Substring(0, marker).Trim();
            if (versionLine != ExpectedVersionLine)
                warning = "unexpected save version line \"" + versionLine + "\" (expected \"" + ExpectedVersionLine + "\"); attempting to parse anyway";

            int jsonStart = text.IndexOf('\n', marker);
            if (jsonStart < 0) throw new FormatException("no data after " + HeaderMarker + " marker");
            string json = text.Substring(jsonStart + 1);

            JsonValue root = ParseWithBigStack(json);
            if (root.Kind != JsonKind.Object)
                throw new FormatException("save payload is not a JSON object");
            return SaveGraph.Build(root);
        }

        /// <summary>Parses on a dedicated large-stack thread: the recursive-descent parser needs
        /// one frame per nesting level, and SaveMaxDepth levels overflow a default 1 MB stack.</summary>
        private static JsonValue ParseWithBigStack(string json)
        {
            JsonValue root = null;
            Exception failure = null;
            Thread thread = new Thread(
                () =>
                {
                    try { root = JsonParser.Parse(json, SaveMaxDepth); }
                    catch (Exception ex) { failure = ex; }
                },
                ParseThreadStackBytes);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
            return root;
        }
    }
}
