using System;
using System.Collections.Generic;
using Assets.Code;
using Assets.Code.Modding;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;

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
            host.RegisterServerThread(new ToolDefinition(
                "new_game",
                "Start a NEW game from the main menu, headlessly - no human at the game window needed. " +
                "Generates a fresh world and begins play as the chosen god. SLOW: map generation plus the " +
                "burn-in history simulation takes ~30-120s; make ONE call and wait - do not retry while it " +
                "runs (even if this call times out, the game finishes starting; check game_overview). If a " +
                "game is already in progress it refuses unless confirm:true, which abandons that game " +
                "WITHOUT saving. On success returns the god, the seed used and a full game_overview-style " +
                "summary; every other tool (list_units, list_powers, end_turn, ...) works immediately after.",
                Schema.Object(
                    Schema.Prop("god", Schema.StringEnum(
                        "Which god to play (default random). snake=She Who Will Feast (devouring serpent, " +
                        "army/combat focus), laughing_king=Iastur, The Laughing King (madness and courtly " +
                        "intrigue), vinerva=Vinerva (life and nature twisted into threat), ophanim=Ophanim, " +
                        "The Divine Beyond (judgement through its own holy order), mammon=Mammon (greed, " +
                        "trade and industry).",
                        "snake", "laughing_king", "vinerva", "ophanim", "mammon", "random")),
                    Schema.Prop("seed", Schema.Integer(
                        "Map-generation seed. Omit for a random seed. The seed used is echoed in the result; " +
                        "the same seed reproduces the same world.")),
                    Schema.Prop("mapSize", Schema.StringEnum(
                        "World size (default standard). small=32x32 (fastest to generate, good for testing), " +
                        "standard=42x42 (the game's default), large=52x52 (slow to generate).",
                        "small", "standard", "large")),
                    Schema.Prop("difficulty", Schema.Integer(
                        "Difficulty (default 0=normal). Game presets: -3 easy, 0 normal, 3 hard, 6 brutal; " +
                        "any value works - negative=easier, positive=harder.")),
                    Schema.Prop("turnLimit", Schema.Boolean(
                        "true (default): you lose if you have not won by your god's max turns (500). " +
                        "false: endless game, no time-out defeat.")),
                    Schema.Prop("confirm", Schema.Boolean(
                        "Required (true) when a game is already in progress: abandons it WITHOUT saving - " +
                        "unsaved progress is lost permanently."))),
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
                godKey = GodKeys[rng.Next(GodKeys.Length)];
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
            throw new InvalidOperationException("god not found in the setup list: " + key);
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
