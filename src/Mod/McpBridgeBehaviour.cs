using ShadowsMcp.Core.Util;
using UnityEngine;

namespace ShadowsMcp
{
    /// <summary>
    /// Pumps the main-thread dispatcher once per frame. Lives on a DontDestroyOnLoad
    /// GameObject so it keeps running across scene changes, in menus, and while the
    /// game is logically paused (Update still runs at timeScale 0).
    /// Static wiring because AddComponent cannot pass constructor arguments.
    /// </summary>
    public sealed class McpBridgeBehaviour : MonoBehaviour
    {
        public static MainThreadDispatcher Dispatcher;
        public static System.Action OnQuit;

        private void Update()
        {
            MainThreadDispatcher d = Dispatcher;
            if (d != null) d.Pump();
        }

        private void OnApplicationQuit()
        {
            System.Action quit = OnQuit;
            if (quit != null) quit();
        }
    }
}
