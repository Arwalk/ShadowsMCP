using Assets.Code;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Util;

namespace ShadowsMcp
{
    /// <summary>
    /// All runtime state for the mod, shared via ModCore's statics. Never referenced by any
    /// game object, so nothing here can leak into save files (the game serializes the whole
    /// Map graph, including the ModKernel instances themselves — see docs/ground-truth-notes.md).
    /// </summary>
    public sealed class GameContext
    {
        /// <summary>The current game's Map; null in the main menu. Written on the main thread
        /// by ModKernel hooks, read from server threads (volatile for visibility).</summary>
        private volatile Map _map;

        public Map Map
        {
            get { return _map; }
            set { _map = value; }
        }

        public readonly EntityRegistry Registry = new EntityRegistry();
        public readonly ModConfig Config = new ModConfig();
        public MainThreadDispatcher Dispatcher;

        public object ResolveEntity(string id)
        {
            return Summaries.ResolveId(this, id);
        }

        public JsonValue EntityStub(object obj)
        {
            return Summaries.EntityStub(this, obj);
        }
    }
}
