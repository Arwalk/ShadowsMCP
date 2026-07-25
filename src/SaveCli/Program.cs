using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ShadowsMcp.Core.Json;
using ShadowsMcp.SaveAnalysis;

namespace ShadowsMcp.SaveCli
{
    /// <summary>
    /// Standalone analyzer for Shadows of Forbidden Gods save files (*.sv) — no game required.
    ///
    ///   savecli list    [--dir DIR]
    ///   savecli summary FILE [--dir DIR]
    ///   savecli inspect FILE PATH [--depth N] [--max-items N] [--dir DIR]
    ///   savecli raw     FILE PATH [--dir DIR]
    ///
    /// Exit codes: 0 ok, 1 file/path error, 2 usage.
    /// </summary>
    public static class Program
    {
        private const string Usage =
            "usage: savecli <command> [args]\n" +
            "\n" +
            "  list    [--dir DIR]                                list save files, newest first\n" +
            "  summary <file> [--dir DIR]                         high-level report (turn, god, counts, agents...)\n" +
            "  inspect <file> <path> [--depth N] [--max-items N]  bounded view of any element, e.g. locations[4].settlement\n" +
            "  raw     <file> <path> [--dir DIR]                  verbatim subtree ($refs left unresolved)\n" +
            "\n" +
            "<file> is a name from `list` (the .sv suffix is optional) or a path to a .sv file.\n" +
            "<path> uses the same syntax as the mod's inspect tool: map | locations[4].settlement | overmind.god\n" +
            "Save folder: --dir, else $SHADOWS_SAVE_DIR, else the game's folder\n" +
            "(ApplicationData/ShadowsForbiddenGodsSaves, incl. the Proton prefix on Steam Deck/Linux).";

        public static int Main(string[] args)
        {
            // All work runs on a large-stack thread: parsing and re-serializing a save walks a
            // graph thousands of levels deep, past what the default 1 MB stack can recurse.
            int exitCode = 2;
            Thread worker = new Thread(() => { exitCode = Run(args); }, 64 * 1024 * 1024);
            worker.Start();
            worker.Join();
            return exitCode;
        }

        private static int Run(string[] args)
        {
            List<string> positional = new List<string>();
            string dir = null;
            int depth = 1;
            int maxItems = 20;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dir": if (++i >= args.Length) return UsageError("--dir needs a value"); dir = args[i]; break;
                    case "--depth": if (++i >= args.Length || !int.TryParse(args[i], out depth)) return UsageError("--depth needs an integer"); break;
                    case "--max-items": if (++i >= args.Length || !int.TryParse(args[i], out maxItems)) return UsageError("--max-items needs an integer"); break;
                    case "-h": case "--help": Console.WriteLine(Usage); return 0;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal)) return UsageError("unknown option " + args[i]);
                        positional.Add(args[i]);
                        break;
                }
            }
            depth = Math.Max(1, Math.Min(20, depth));
            maxItems = Math.Max(1, Math.Min(1000, maxItems));

            if (positional.Count == 0) return UsageError(null);
            string command = positional[0];
            try
            {
                switch (command)
                {
                    case "list": return positional.Count == 1 ? ListCommand(dir) : UsageError("list takes no positional args");
                    case "summary": return positional.Count == 2 ? SummaryCommand(dir, positional[1]) : UsageError("summary <file>");
                    case "inspect": return positional.Count == 3 ? InspectCommand(dir, positional[1], positional[2], depth, maxItems) : UsageError("inspect <file> <path>");
                    case "raw": return positional.Count == 3 ? RawCommand(dir, positional[1], positional[2]) : UsageError("raw <file> <path>");
                    default: return UsageError("unknown command '" + command + "'");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static int UsageError(string message)
        {
            if (message != null) Console.Error.WriteLine("error: " + message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // ---------- commands ----------

        private static int ListCommand(string dirOption)
        {
            string folder = ResolveFolder(dirOption, out string probeReport);
            if (folder == null)
            {
                Console.Error.WriteLine("error: no save folder found. Probed:\n" + probeReport +
                                        "\nPoint at one with --dir or $SHADOWS_SAVE_DIR.");
                return 1;
            }
            List<SaveFileInfo> saves = SaveFileReader.ListSaves(folder);
            Console.WriteLine(folder + "  (" + saves.Count + " save" + (saves.Count == 1 ? "" : "s") + ")");
            foreach (SaveFileInfo save in saves)
            {
                Console.WriteLine("  {0,-40} {1,-10} {2,10:N0} B  {3:yyyy-MM-dd HH:mm}Z  {4}",
                    save.FileName, save.Kind, save.SizeBytes, save.ModifiedUtc, save.VersionLine ?? "?");
            }
            return 0;
        }

        private static int SummaryCommand(string dirOption, string file)
        {
            SaveGraph graph = Load(dirOption, file, out int error);
            if (graph == null) return error;
            Console.WriteLine(JsonWriter.Write(SaveSummary.Build(graph), pretty: true));
            return 0;
        }

        private static int InspectCommand(string dirOption, string file, string path, int depth, int maxItems)
        {
            SaveGraph graph = Load(dirOption, file, out int loadError);
            if (graph == null) return loadError;
            JsonValue node = graph.Navigate(path, out string pathError);
            if (pathError != null)
            {
                Console.Error.WriteLine("error: " + pathError);
                return 1;
            }
            Console.WriteLine(JsonWriter.Write(graph.Render(node, depth, maxItems), pretty: true));
            return 0;
        }

        private static int RawCommand(string dirOption, string file, string path)
        {
            SaveGraph graph = Load(dirOption, file, out int loadError);
            if (graph == null) return loadError;
            JsonValue node = graph.Navigate(path, out string pathError);
            if (pathError != null)
            {
                Console.Error.WriteLine("error: " + pathError);
                return 1;
            }
            Console.WriteLine(JsonWriter.Write(node, pretty: true));
            return 0;
        }

        // ---------- file/folder resolution ----------

        private static SaveGraph Load(string dirOption, string file, out int errorCode)
        {
            errorCode = 0;
            string fullPath = ResolveFile(dirOption, file, out string error);
            if (fullPath == null)
            {
                Console.Error.WriteLine("error: " + error);
                errorCode = 1;
                return null;
            }
            SaveGraph graph = SaveFileReader.Read(fullPath, out _, out string warning);
            if (warning != null) Console.Error.WriteLine("warning: " + warning);
            return graph;
        }

        private static string ResolveFile(string dirOption, string file, out string error)
        {
            error = null;
            if (File.Exists(file)) return Path.GetFullPath(file);

            string folder = ResolveFolder(dirOption, out string probeReport);
            if (folder == null)
            {
                error = "no save folder found. Probed:\n" + probeReport + "\nPoint at one with --dir or $SHADOWS_SAVE_DIR.";
                return null;
            }
            string candidate = Path.Combine(folder, file);
            if (File.Exists(candidate)) return candidate;
            if (!file.EndsWith(".sv", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".sv"))
                return candidate + ".sv";

            error = "no save named \"" + file + "\" in " + folder + Suggestions(folder, file);
            return null;
        }

        private static string Suggestions(string folder, string requested)
        {
            List<SaveFileInfo> saves = SaveFileReader.ListSaves(folder);
            if (saves.Count == 0) return " (folder has no .sv files)";
            saves.Sort((a, b) => Distance(a.FileName, requested).CompareTo(Distance(b.FileName, requested)));
            List<string> names = new List<string>();
            for (int i = 0; i < saves.Count && i < 3; i++) names.Add(saves[i].FileName);
            return ". Closest names: " + string.Join(", ", names);
        }

        /// <summary>Levenshtein distance, case-insensitive — good enough to rank "did you mean" suggestions.</summary>
        private static int Distance(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            int[] prev = new int[b.Length + 1];
            int[] curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                int[] swap = prev; prev = curr; curr = swap;
            }
            return prev[b.Length];
        }

        private static string ResolveFolder(string dirOption, out string probeReport)
        {
            List<string> probed = new List<string>();
            foreach (string candidate in FolderCandidates(dirOption))
            {
                if (Directory.Exists(candidate)) { probeReport = null; return candidate; }
                probed.Add("  " + candidate);
            }
            probeReport = string.Join("\n", probed);
            return null;
        }

        private static IEnumerable<string> FolderCandidates(string dirOption)
        {
            if (dirOption != null) { yield return dirOption; yield break; }

            string env = Environment.GetEnvironmentVariable("SHADOWS_SAVE_DIR");
            if (!string.IsNullOrEmpty(env)) yield return env;

            // The game's own location (%APPDATA% on Windows, ~/.config on Linux Mono).
            yield return SaveFileReader.DefaultSaveFolder();

            // Steam Proton prefixes: the Windows path inside each game's compat prefix.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string steamRoot in new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") })
            {
                string compat = Path.Combine(steamRoot, "steamapps", "compatdata");
                if (!Directory.Exists(compat)) continue;
                foreach (string prefix in Directory.GetDirectories(compat))
                {
                    yield return Path.Combine(prefix, "pfx", "drive_c", "users", "steamuser",
                        "AppData", "Roaming", "ShadowsForbiddenGodsSaves");
                }
            }
        }
    }
}
