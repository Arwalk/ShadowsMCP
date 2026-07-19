using System;
using System.Collections.Generic;
using System.IO;
using Assets.Code;
using Assets.Code.Modding;
using ShadowsMcp.Core.Http;
using ShadowsMcp.Core.Mcp;
using ShadowsMcp.Core.Util;
using ShadowsMcp.Tools;
using UnityEngine;

namespace ShadowsMcp
{
    /// <summary>
    /// Mod entry point. The game finds this ModKernel subclass in the DLL, instantiates it,
    /// and calls the hook methods.
    ///
    /// CRITICAL: this class must hold NO instance state. The game serializes map.mods
    /// (including this object) into save files with FullSerializer and re-creates it on
    /// load, so instances are disposable — everything lives in statics, shared across
    /// whichever instance the game currently talks to. (See docs/ground-truth-notes.md.)
    /// </summary>
    public class ModCore : ModKernel
    {
        /// <summary>
        /// The mod's release version — read from the assembly, whose version is set by
        /// &lt;Version&gt; in ShadowsMCP.csproj (the single source of truth). Surfaced over MCP
        /// in serverInfo.version (initialize) and in the game_overview tool so a connected
        /// client can confirm which build it is talking to.
        /// </summary>
        public static readonly string ModVersion = ReadAssemblyVersion();

        private static string ReadAssemblyVersion()
        {
            System.Version v = typeof(ModCore).Assembly.GetName().Version;
            return v != null ? v.ToString(3) : "0.0.0";
        }

        private static readonly object BootLock = new object();
        private static bool _booted;
        private static GameContext _ctx;
        private static McpServer _server;
        private static GameToolHost _host;
        private static HttpTransport _transport;
        private static string _logPath;
        private static readonly object LogFileLock = new object();

        // ---------- lifecycle hooks ----------

        public override void onModsInitiallyLoaded()
        {
            Boot(); // fires more than once (per-DLL and again when all mods finish) — Boot is idempotent
        }

        public override void onStartGamePresssed(Map map, List<God> gods) { OnMapSeen(map); }
        public override void afterMapGenAfterHistorical(Map map) { OnMapSeen(map); }
        public override void afterLoading(Map map) { OnMapSeen(map); }
        public override void onTurnStart(Map map) { OnMapSeen(map); }
        public override void onTurnEnd(Map map) { OnMapSeen(map); }

        // The game calls this whenever the modal blocker changes (a decision popup opened or
        // closed). We keep no state — the decision tools read ui.blocker live — but logging the
        // transition helps trace agent runs. Never throw out of a game hook.
        public override void onUIFullscreenBlockerUpdate(GameObject blocker)
        {
            try
            {
                Log.Info(blocker != null
                    ? "decision popup opened: " + blocker.name
                    : "decision popup closed");
            }
            catch { }
        }

        // Option names must match mod_config.json exactly; values arrive when the player
        // applies the in-game mod config popup and again when a game is started.
        public override void receiveModConfigOpts_int(string optName, int value)
        {
            if (_ctx == null) return;
            if (optName == "Port" && value != _ctx.Config.Port && value >= 1024 && value <= 65535)
            {
                _ctx.Config.Port = value;
                Log.Info("config: port -> " + value);
                RestartTransport();
            }
        }

        public override void receiveModConfigOpts_bool(string optName, bool value)
        {
            if (_ctx == null) return;
            if (optName == "Listen on LAN" && value != _ctx.Config.ListenLan)
            {
                _ctx.Config.ListenLan = value;
                Log.Info("config: listen on LAN -> " + value);
                RestartTransport();
            }
        }

        // ---------- boot / shutdown (statics only) ----------

        private static void Boot()
        {
            lock (BootLock)
            {
                if (_booted) return;
                _booted = true;
            }
            try
            {
                _logPath = Path.Combine(Application.persistentDataPath, "ShadowsMCP.log");
                Log.Sink = WriteLog;
                Log.Info("booting v" + ModVersion + " (game " + World.getVersionID() + ")");

                _ctx = new GameContext();
                _ctx.Dispatcher = new MainThreadDispatcher(); // constructed here = on Unity's main thread

                McpBridgeBehaviour.Dispatcher = _ctx.Dispatcher;
                McpBridgeBehaviour.OnQuit = Shutdown;
                var go = new GameObject("ShadowsMCP");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<McpBridgeBehaviour>();

                _host = new GameToolHost(_ctx);
                QueryTools.RegisterAll(_host, _ctx);
                ActionTools.RegisterAll(_host, _ctx);
                InspectTool.RegisterAll(_host, _ctx);
                DecisionTools.RegisterAll(_host, _ctx);

                _server = new McpServer(_host, "shadows-mcp", ModVersion);
                RestartTransport();
            }
            catch (Exception ex)
            {
                // Never throw out of a mod hook: the loader would flag the whole mod as failed.
                try { Log.Error("boot failed", ex); } catch { }
            }
        }

        private static void RestartTransport()
        {
            try
            {
                if (_transport != null) _transport.Stop();
                _transport = new HttpTransport(_server, _ctx.Config.ListenLan, _ctx.Config.Port);
                _transport.Start();
            }
            catch (Exception ex)
            {
                Log.Error("could not start the MCP server (port " + _ctx.Config.Port + "-" +
                    (_ctx.Config.Port + 9) + " all busy?)", ex);
            }
        }

        private static void Shutdown()
        {
            try { if (_transport != null) _transport.Stop(); } catch { }
        }

        private static void OnMapSeen(Map map)
        {
            if (_ctx == null || map == null) return;
            if (!ReferenceEquals(_ctx.Map, map))
            {
                _ctx.Map = map;
                _ctx.Registry.Reset(); // new game or loaded save: session ids start over
                Log.Info("tracking map (turn " + map.turn + ") - entity ids reset");
            }
        }

        private static void WriteLog(string line)
        {
            try { Debug.Log(line); } catch { }
            try
            {
                lock (LogFileLock)
                {
                    File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
