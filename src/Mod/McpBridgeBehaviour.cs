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

        // Fires on the Unity main thread during AddComponent (inside ModCore.Boot). This is the
        // canonical place we assert runInBackground: without it Unity pauses the game loop on
        // focus loss and queued MCP requests starve until the 10s timeout. We log the read-back
        // so Player.log shows both that the line ran and that the value actually stuck.
        private void Awake()
        {
            Application.runInBackground = true;
            EnsureNonExclusiveFullscreen();
            Debug.Log($"[ShadowsMCP] Awake runInBackground -> {Application.runInBackground}");
        }

        // Re-assert exactly at the focus transition. This callback fires even as focus is lost
        // (before frames stop), so nothing can leave the flag off in a window Update never sees.
        private void OnApplicationFocus(bool focus)
        {
            Application.runInBackground = true;
            EnsureNonExclusiveFullscreen();
            Debug.Log($"[ShadowsMCP] focus={focus} runInBackground -> {Application.runInBackground}");
        }

        private void Update()
        {
            // Cheap ongoing safety net: catch any mid-frame flip. Set canonically in Awake and
            // re-asserted in OnApplicationFocus; no log here (would spam once per frame).
            if (!Application.runInBackground) Application.runInBackground = true;

            MainThreadDispatcher d = Dispatcher;
            if (d != null) d.Pump();
        }

        // Exclusive fullscreen minimizes on focus loss, and a minimized Unity app pauses even
        // with runInBackground on. Downgrade to borderless; leave Windowed alone so we don't
        // hijack windowed players or fight the game's own World.setResolution.
        private static void EnsureNonExclusiveFullscreen()
        {
            if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
                Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
        }

        private void OnApplicationQuit()
        {
            System.Action quit = OnQuit;
            if (quit != null) quit();
        }
    }
}
