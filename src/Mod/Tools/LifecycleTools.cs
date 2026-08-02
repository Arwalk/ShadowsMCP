using System;
using System.Collections.Generic;
using Assets.Code;
using Assets.Code.Modding;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;
using ShadowsMcp.Extensions;

namespace ShadowsMcp.Tools
{
    /// <summary>
    /// Game-lifecycle tools: starting a fresh game headlessly, with no human at the game
    /// window. Replicates the UIMainMenu.bStart/startProper lifecycle (mod config, the
    /// onStartGamePresssed hook, God.setup on every god) and then calls World.startup
    /// directly, skipping the god-selection carousel and the options popup. The game's own
    /// WHILE_TRUE_RESTART debug loop proves this in-place map replacement is supported.
    /// </summary>
    public static class LifecycleTools
    {
        public static void RegisterAll(GameToolHost host, GameContext ctx)
        {
            // Server-thread registration (the end_turn pattern): World.startup runs map
            // generation plus a 150-turn burn-in synchronously, far beyond the ordinary
            // per-tool budget, so the handler dispatches its own job with its own timeout.
            host.RegisterServerThreadMutating(new ToolDefinition(
                "new_game",
                "Start a NEW game from the main menu, headlessly. Generates a fresh world and begins play " +
                "as the chosen god. SLOW (~30-120s): make ONE call and wait - never retry while it runs " +
                "(even if this call times out the game finishes starting; check game_overview). Refuses if " +
                "a game is in progress unless confirm:true (abandons it WITHOUT saving). Returns the god, " +
                "the seed used and a game_overview-style summary; every other tool works immediately after.",
                Schema.Object(
                    // A free string, NOT an enum: content mods can add playable gods (via their MCP
                    // manifest and/or onStartGamePresssed), and their keys are unknowable at
                    // registration time. Base keys stay authoritative in the description.
                    Schema.Prop("god", Schema.String(
                        "Which god to play (default random). Base keys: snake=She Who Will Feast " +
                        "(army/combat), laughing_king=Iastur (madness, courtly intrigue), vinerva=Vinerva " +
                        "(nature twisted into threat), ophanim=Ophanim (judgement via its own holy order), " +
                        "mammon=Mammon (greed, trade and industry); random=any playable god. A god added " +
                        "by a content mod (see game_overview.mcpExtensions) is selectable by its " +
                        "advertised key, class name, or display name.")),
                    Schema.Prop("seed", Schema.Integer(
                        "Map seed (default random; echoed in the result; same seed = same world).")),
                    Schema.Prop("mapSize", Schema.StringEnum(
                        "World size (default standard). small=32x32 (fastest, good for testing), " +
                        "standard=42x42, large=52x52 (slow).",
                        "small", "standard", "large")),
                    Schema.Prop("difficulty", Schema.Integer(
                        "Default 0=normal. Presets: -3 easy, 0 normal, 3 hard, 6 brutal; any value works.")),
                    Schema.Prop("turnLimit", Schema.Boolean(
                        "true (default): you lose if you have not won by the god's max turns (500). " +
                        "false: endless.")),
                    Schema.Prop("confirm", Schema.Boolean(
                        "Required (true) when a game is in progress: abandons it WITHOUT saving."))),
                a => ctx.Dispatcher.Run(() => NewGame(ctx, a), ctx.Config.NewGameTimeoutMs)));
        }

        // ---------- new_game ----------

        private const int SizeSmall = 32;
        private const int SizeStandard = 42;
        private const int SizeLarge = 52;

        /// <summary>Runs on the main thread as one dispatcher job; the game freezes for its
        /// duration exactly as it does when a human starts a game.</summary>
        private static ToolResult NewGame(GameContext ctx, JsonValue a)
        {
            World world = World.self;
            if (world == null || world.ui == null)
                return ToolResult.Error("the game world/UI is not ready yet - retry in a few seconds");
            UIMainMenu menu = world.ui.uiMainMenu;
            if (menu != null && menu.starting != 0)
                return ToolResult.Error("mods are still loading - retry in a few seconds");
            if (world.turnLock)
                return ToolResult.Error("turn processing is underway - wait for end_turn to finish, then retry");
            if (world.ui.blocker != null)
                return ToolResult.Error(ctx.Map != null
                    ? "a popup is open in the game - resolve it first (get_pending_decision / resolve_decision), then retry new_game"
                    : "a popup is open over the main menu - a human must close it before new_game can run");
            if (world.param == null)
                return ToolResult.Error("the main menu has not finished initialising (no Params yet) - retry shortly");
            if (world.map != null && !a["confirm"].AsBool())
                return ToolResult.Error("a game is already in progress (turn " + world.map.turn + "). Pass " +
                    "confirm:true to abandon it and start fresh - there is no save tool, so unsaved progress " +
                    "is lost permanently.");

            var rng = new System.Random();
            string godKey = a["god"].AsString("random");
            if (godKey == "random")
            {
                // Manifest-advertised gods join the random pool. If the advertising mod then fails
                // to add its god in onStartGamePresssed, FindGod reports it by name below.
                var pool = new List<string>(GodKeys);
                foreach (ExtensionGod eg in McpExtensions.Gods) pool.Add(eg.Key);
                godKey = pool[rng.Next(pool.Count)];
            }
            int seed = a["seed"].IsNull ? rng.Next() : a["seed"].AsInt();
            string sizeLabel = a["mapSize"].AsString("standard");
            int size = sizeLabel == "small" ? SizeSmall : sizeLabel == "large" ? SizeLarge : SizeStandard;
            int difficulty = a["difficulty"].AsInt(0);
            bool limitEnforced = a["turnLimit"].IsNull || a["turnLimit"].AsBool();

            God chosen;
            try
            {
                // A restart can leave the UI's selection pointing at units of the discarded
                // map; ui.checkData() inside startup would then touch dead objects.
                GraphicalMap.selectedUnit = null;
                GraphicalMap.selectedHex = null;

                // Mirror UIMainMenu.bStart: fresh Map before anything touches staticMap
                // (GameOptions reads its default sizes from it, gods set up against it).
                world.chosenGod = null;
                world.map = new Map(world.param);
                World.staticMap = world.map;

                // Mirror startProper: mod config first (informMod:true re-fires
                // receiveModConfigOpts_* - if the saved port differs from the active one this
                // restarts our transport and the in-flight HTTP response dies, same as on a
                // human game start), then the onStartGamePresssed hook, then god setup.
                List<Mod> mods = menu != null && menu.modsLoaded != null ? menu.modsLoaded : EnabledMods(world);
                try { PopupModConfig.loadModConfigFromFile(mods, informMod: true); }
                catch (Exception e) { Log.Error("new_game: mod config load failed (continuing)", e); }

                List<God> gods = BaseGods();
                foreach (ModKernel kernel in World.self.loadedModKernels)
                {
                    // Per-kernel isolation the game itself doesn't have: a third-party mod
                    // throwing here must not abort our start halfway through the lifecycle.
                    try { kernel.onStartGamePresssed(world.map, gods); }
                    catch (Exception e) { Log.Error("new_game: a mod's onStartGamePresssed threw (continuing)", e); }
                }
                // startProper adds these after the mod hook; they are not offered by this
                // tool but are set up like the game does, in case a mod expects them.
                gods.Add(new God_Eternity());
                gods.Add(new God_Cards());
                gods.Add(new God_Underground());
                foreach (God g in gods) g.setup(world.map);

                chosen = FindGod(gods, godKey);
                world.chosenGod = chosen;

                // Mirror PopupGameOptions.startGame with defaults + our knobs. Constructed
                // AFTER staticMap is set, like the game does (the ctor reads sizes from it).
                var opts = new GameOptions();
                opts.seed = seed;
                opts.sizeX = size;
                opts.sizeY = size;
                opts.difficulty = difficulty;
                // INVERTED in the engine: GameOptions.turnLimit=true means map.opt_endless=true
                // (World.startup: "map.opt_endless = opts.turnLimit"; the options popup shows it
                // as tEndless.isOn = !options.turnLimit).
                opts.turnLimit = !limitEnforced;

                // The long synchronous part: map gen + burn-in + first turn. Fires
                // afterMapGenAfterHistorical on every kernel, so ModCore.OnMapSeen picks up
                // the new map and resets the entity registry - no bookkeeping needed here.
                world.startup(opts);
            }
            catch (Exception ex)
            {
                Log.Error("new_game failed during startup", ex);
                // Best-effort reset so the tool surface reports "no game" instead of touching
                // a half-generated map.
                try
                {
                    world.map = null;
                    World.staticMap = null;
                    world.chosenGod = null;
                    ctx.Map = null;
                    world.ui.setToMainMenu();
                }
                catch { }
                return ToolResult.Error("new_game failed during map generation/startup: " + Log.Describe(ex) +
                    ". The game was returned to the main menu; try again (a different seed or mapSize may help).");
            }

            Map map = world.map;
            return ToolResult.Ok(JsonValue.NewObject()
                .Set("started", true)
                .Set("god", JsonValue.NewObject()
                    .Set("key", godKey)
                    .Set("name", chosen.getName())
                    .Set("type", chosen.GetType().Name))
                .Set("seed", seed)
                .Set("mapSize", JsonValue.NewObject()
                    .Set("label", sizeLabel).Set("sizeX", size).Set("sizeY", size))
                .Set("difficulty", difficulty)
                .Set("endless", map.opt_endless)
                .Set("maxTurns", map.opt_endless ? JsonValue.Null : JsonValue.Of(chosen.getMaxTurns()))
                .Set("turn", map.turn)
                .Set("overview", QueryTools.OverviewJson(ctx, map))
                .Set("hint", "you are now playing - call list_units to meet your starting agents, " +
                    "list_powers for your god's powers, and get_tips for the mechanics primer"));
        }

        // ---------- helpers ----------

        private static readonly string[] GodKeys = { "snake", "laughing_king", "vinerva", "ophanim", "mammon" };

        /// <summary>The base playable gods, in the order startProper builds them.</summary>
        private static List<God> BaseGods()
        {
            return new List<God>
            {
                new God_Snake(),
                new God_LaughingKing(),
                new God_Vinerva(),
                new God_Ophanim(),
                new God_Mammon(),
            };
        }

        private static God FindGod(List<God> gods, string key)
        {
            foreach (God g in gods)
            {
                if (key == "snake" && g is God_Snake) return g;
                if (key == "laughing_king" && g is God_LaughingKing) return g;
                if (key == "vinerva" && g is God_Vinerva) return g;
                if (key == "ophanim" && g is God_Ophanim) return g;
                if (key == "mammon" && g is God_Mammon) return g;
            }

            // A content mod's god: the mod added the instance to `gods` in its onStartGamePresssed;
            // match its manifest-advertised key first, then class name, then display name.
            string manifestClass = null;
            foreach (ExtensionGod eg in McpExtensions.Gods)
                if (string.Equals(eg.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    manifestClass = eg.ClassName;
                    break;
                }
            foreach (God g in gods)
            {
                string cls = g.GetType().Name;
                if (manifestClass != null && string.Equals(cls, manifestClass, StringComparison.OrdinalIgnoreCase))
                    return g;
                if (string.Equals(cls, key, StringComparison.OrdinalIgnoreCase)) return g;
                string name = null;
                try { name = g.getName(); } catch { }
                if (name != null && string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return g;
            }

            throw new InvalidOperationException("god not found in the setup list: " + key +
                (manifestClass != null
                    ? " (its mod's manifest advertises class " + manifestClass + ", but no god of that " +
                      "class was added in onStartGamePresssed - is the content mod enabled?)"
                    : "") +
                ". Available: snake, laughing_king, vinerva, ophanim, mammon" + ModdedGodList(gods));
        }

        /// <summary>Class names of the non-vanilla playable gods currently in the setup list, for the
        /// FindGod error message. Excludes the three internal gods startProper always appends.</summary>
        private static string ModdedGodList(List<God> gods)
        {
            var extras = new List<string>();
            foreach (God g in gods)
            {
                if (g is God_Snake || g is God_LaughingKing || g is God_Vinerva || g is God_Ophanim ||
                    g is God_Mammon || g is God_Eternity || g is God_Cards || g is God_Underground) continue;
                extras.Add(g.GetType().Name);
            }
            return extras.Count == 0 ? "" : "; modded: " + string.Join(", ", extras.ToArray());
        }

        /// <summary>Fallback when the main-menu component is unavailable: the enabled subset
        /// of the world's known mods (what UIMainMenu.loadMods would have produced).</summary>
        private static List<Mod> EnabledMods(World world)
        {
            var list = new List<Mod>();
            foreach (Mod m in world.availableMods)
                if (m.enabled) list.Add(m);
            return list;
        }
    }
}
