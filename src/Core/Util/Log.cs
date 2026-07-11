using System;

namespace ShadowsMcp.Core.Util
{
    /// <summary>
    /// Pluggable logging. The mod wires Sink to UnityEngine.Debug.Log plus a file;
    /// the test host wires it to Console.WriteLine. Never throws.
    /// </summary>
    public static class Log
    {
        public static Action<string> Sink;

        public static void Info(string message)
        {
            Emit("[ShadowsMCP] " + message);
        }

        public static void Error(string message, Exception ex = null)
        {
            Emit("[ShadowsMCP] ERROR: " + message + (ex != null ? "\n" + ex : ""));
        }

        private static void Emit(string line)
        {
            try
            {
                Action<string> sink = Sink;
                if (sink != null) sink(line);
            }
            catch
            {
                // logging must never take the server down
            }
        }
    }
}
