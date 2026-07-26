using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code;
using Assets.Code.Modding;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Util;
using ShadowsMcp.Tips;

namespace ShadowsMcp.Extensions
{
    /// <summary>One agent-facing tip advertised by a content mod's manifest. Unlike <see cref="TipDef"/>
    /// there is no code trigger — the manifest is declarative — so firing is limited to "always" (once,
    /// as soon as a game is running and the optional godClass gate matches) or on demand via get_tips.</summary>
    public sealed class ExtensionTip
    {
        public readonly string Id;
        public readonly string Title;
        /// <summary>One of <see cref="TipCatalog.Categories"/>; unknown values are coerced to "basics".</summary>
        public readonly string Category;
        public readonly string Summary;
        public readonly string Body;
        /// <summary>True => surfaced once through the contextual channel (game_overview / end_turn),
        /// like a built-in tip whose trigger fired; false => reachable only via get_tips.</summary>
        public readonly bool Always;
        /// <summary>Optional God subclass name; when set the tip only fires while playing that god.</summary>
        public readonly string GodClass;
        /// <summary>Display name of the mod whose manifest declared the tip.</summary>
        public readonly string SourceMod;

        public ExtensionTip(string id, string title, string category, string summary, string body,
                            bool always, string godClass, string sourceMod)
        {
            Id = id;
            Title = title;
            Category = category;
            Summary = summary;
            Body = body;
            Always = always;
            GodClass = godClass;
            SourceMod = sourceMod;
        }
    }

    /// <summary>A playable god advertised by a content mod's manifest, so new_game can select it by a
    /// friendly key. The mod itself must still add the god instance to the setup list in its
    /// onStartGamePresssed hook — the manifest only names it.</summary>
    public sealed class ExtensionGod
    {
        public readonly string Key;
        public readonly string ClassName;
        public readonly string Description;
        public readonly string SourceMod;

        public ExtensionGod(string key, string className, string description, string sourceMod)
        {
            Key = key;
            ClassName = className;
            Description = description;
            SourceMod = sourceMod;
        }
    }

    /// <summary>
    /// Discovery and registry of content-mod MCP manifests. A content mod opts in by declaring, on its
    /// ModKernel subclass, a public instance method <c>string getShadowsMcpManifest()</c> returning a
    /// JSON manifest (see docs/mcp-extension-guide.md for the schema). Duck-typed on purpose: neither
    /// side references the other's assembly, so a content mod works without ShadowsMCP installed and
    /// vice versa, with no version lockstep.
    ///
    /// Trust model: an enabled mod already runs arbitrary C# in-process, so its manifest is strictly
    /// LESS powerful than its code — parsing applies sanity limits (size/count caps, id dedup) for
    /// robustness, not security. Note that a manifest can widen end_turn's auto-dismiss set via
    /// informationalPopups; classifications are logged at load so a mislabel is traceable.
    ///
    /// Threading: <see cref="Refresh"/> runs on the main thread (mod hooks); readers may be on server
    /// threads, so the whole registry is swapped atomically as one immutable snapshot.
    /// </summary>
    public static class McpExtensions
    {
        /// <summary>The duck-typed method content mods implement (game-style camelCase on purpose).</summary>
        public const string ManifestMethod = "getShadowsMcpManifest";

        private const int MaxManifestChars = 256 * 1024;
        private const int MaxTipsPerMod = 64;

        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot();
            public int KernelCount;
            public readonly List<string> ModNames = new List<string>();
            public readonly List<ExtensionTip> Tips = new List<ExtensionTip>();
            public readonly HashSet<string> InformationalPopups = new HashSet<string>();
            public readonly HashSet<string> HardPopups = new HashSet<string>();
            public readonly List<ExtensionGod> Gods = new List<ExtensionGod>();
            public readonly Dictionary<string, ArchetypeAbilities> AbilityPreviews =
                new Dictionary<string, ArchetypeAbilities>(StringComparer.OrdinalIgnoreCase);
        }

        private static volatile Snapshot _current = Snapshot.Empty;

        public static IList<string> ModNames { get { return _current.ModNames; } }
        public static IList<ExtensionTip> Tips { get { return _current.Tips; } }
        public static IList<ExtensionGod> Gods { get { return _current.Gods; } }

        public static bool IsInformationalPopup(string popupType)
        {
            return popupType != null && _current.InformationalPopups.Contains(popupType);
        }

        public static bool IsHardPopup(string popupType)
        {
            return popupType != null && _current.HardPopups.Contains(popupType);
        }

        /// <summary>Ability preview for a modded archetype, keyed by its UAE_* class name;
        /// null when no manifest covers it (same contract as <see cref="AbilityCatalog.Get"/>).</summary>
        public static ArchetypeAbilities AbilityPreview(string archetypeTypeName)
        {
            ArchetypeAbilities a;
            return archetypeTypeName != null && _current.AbilityPreviews.TryGetValue(archetypeTypeName, out a)
                ? a : null;
        }

        public static ExtensionTip FindTip(string id)
        {
            foreach (ExtensionTip t in _current.Tips)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        /// <summary>
        /// Rescan every loaded kernel for a manifest. Called from mod hooks (onModsInitiallyLoaded
        /// fires per-DLL, so mods loading after us are picked up on later firings; OnMapSeen covers
        /// save loads). Cheap when nothing changed: the kernel list only grows while mods load, so an
        /// unchanged count means an unchanged set. Never throws.
        /// </summary>
        public static void Refresh()
        {
            try
            {
                World world = World.self;
                List<ModKernel> kernels = world != null ? world.loadedModKernels : null;
                if (kernels == null || kernels.Count == _current.KernelCount) return;

                var snap = new Snapshot { KernelCount = kernels.Count };
                foreach (ModKernel kernel in kernels)
                {
                    if (kernel == null || kernel is ModCore) continue;
                    try { ScanKernel(snap, kernel); }
                    catch (Exception e)
                    {
                        Log.Error("mcp extensions: reading " + kernel.GetType().Name +
                            "'s manifest failed - that mod's manifest is skipped", e);
                    }
                }
                _current = snap;
                if (snap.ModNames.Count > 0)
                    Log.Info("mcp extensions: " + string.Join(", ", snap.ModNames.ToArray()) +
                        " (" + snap.Tips.Count + " tips, " + snap.Gods.Count + " gods, " +
                        snap.InformationalPopups.Count + " informational popups, " +
                        snap.AbilityPreviews.Count + " ability previews)");
            }
            catch (Exception e)
            {
                try { Log.Error("mcp extensions: refresh failed", e); } catch { }
            }
        }

        // ---------- parsing ----------

        private static void ScanKernel(Snapshot snap, ModKernel kernel)
        {
            MethodInfo m = kernel.GetType().GetMethod(ManifestMethod,
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null || m.ReturnType != typeof(string)) return;

            string json = (string)m.Invoke(kernel, null);
            if (string.IsNullOrEmpty(json)) return;
            if (json.Length > MaxManifestChars)
            {
                Log.Info("mcp extensions: " + kernel.GetType().Name + "'s manifest is over " +
                    MaxManifestChars + " chars - skipped");
                return;
            }

            JsonValue root = JsonParser.Parse(json);
            string sourceMod = root["name"].AsString(kernel.GetType().Name);
            snap.ModNames.Add(sourceMod);

            ParseTips(snap, root["tips"], sourceMod);
            foreach (JsonValue p in root["informationalPopups"].Items)
                AddPopupType(snap.InformationalPopups, p, sourceMod, "informational");
            foreach (JsonValue p in root["hardPopups"].Items)
                AddPopupType(snap.HardPopups, p, sourceMod, "hard");
            ParseGods(snap, root["gods"], sourceMod);
            ParseAbilityPreviews(snap, root["abilityPreviews"], sourceMod);
        }

        private static void ParseTips(Snapshot snap, JsonValue tips, string sourceMod)
        {
            int count = 0;
            foreach (JsonValue t in tips.Items)
            {
                if (count >= MaxTipsPerMod)
                {
                    Log.Info("mcp extensions: " + sourceMod + " declares over " + MaxTipsPerMod +
                        " tips - the rest are skipped");
                    break;
                }
                string id = t["id"].AsString();
                string title = t["title"].AsString();
                string body = t["body"].AsString();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
                {
                    Log.Info("mcp extensions: " + sourceMod + " has a tip missing id/title/body - skipped");
                    continue;
                }
                if (TipCatalog.Find(id) != null || FindIn(snap.Tips, id) != null)
                {
                    Log.Info("mcp extensions: " + sourceMod + " tip id '" + id +
                        "' collides with an existing tip - skipped (prefix ids with the mod's name)");
                    continue;
                }
                snap.Tips.Add(new ExtensionTip(
                    id, title,
                    NormalizeCategory(t["category"].AsString(), sourceMod, id),
                    t["summary"].AsString(title), body,
                    t["when"].AsString("manual") == "always",
                    t["godClass"].AsString(), sourceMod));
                count++;
            }
        }

        private static ExtensionTip FindIn(List<ExtensionTip> tips, string id)
        {
            foreach (ExtensionTip t in tips)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private static string NormalizeCategory(string category, string sourceMod, string tipId)
        {
            if (!string.IsNullOrEmpty(category))
                foreach (string known in TipCatalog.Categories)
                    if (string.Equals(known, category, StringComparison.OrdinalIgnoreCase)) return known;
            if (!string.IsNullOrEmpty(category))
                Log.Info("mcp extensions: " + sourceMod + " tip '" + tipId + "' has unknown category '" +
                    category + "' - filed under basics");
            return "basics";
        }

        private static void AddPopupType(HashSet<string> into, JsonValue entry, string sourceMod, string kind)
        {
            string type = entry.AsString();
            if (string.IsNullOrEmpty(type)) return;
            if (into.Add(type))
                Log.Info("mcp extensions: " + sourceMod + " classifies popup " + type + " as " + kind);
        }

        private static void ParseGods(Snapshot snap, JsonValue gods, string sourceMod)
        {
            foreach (JsonValue g in gods.Items)
            {
                string key = g["key"].AsString();
                string className = g["className"].AsString();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(className))
                {
                    Log.Info("mcp extensions: " + sourceMod + " has a god missing key/className - skipped");
                    continue;
                }
                snap.Gods.Add(new ExtensionGod(key.ToLowerInvariant(), className,
                    g["description"].AsString(), sourceMod));
            }
        }

        private static void ParseAbilityPreviews(Snapshot snap, JsonValue previews, string sourceMod)
        {
            foreach (JsonValue ap in previews.Items)
            {
                string archetype = ap["archetype"].AsString();
                if (string.IsNullOrEmpty(archetype))
                {
                    Log.Info("mcp extensions: " + sourceMod + " has an ability preview missing its " +
                        "archetype class name - skipped");
                    continue;
                }
                var rituals = new List<AbilityPreview>();
                foreach (JsonValue r in ap["rituals"].Items)
                {
                    string name = r["name"].AsString();
                    if (string.IsNullOrEmpty(name)) continue;
                    rituals.Add(new AbilityPreview(name, r["desc"].AsString(""), r["prereq"].AsString("")));
                }
                snap.AbilityPreviews[archetype] =
                    new ArchetypeAbilities(rituals.ToArray(), ap["note"].AsString());
            }
        }
    }
}
