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

        /// <summary>
        /// One-line, agent-readable summary of an exception: its TYPE, its message and the top few
        /// stack frames (file + line when the pdb is deployed). Tool errors carry this instead of a
        /// bare <c>ex.Message</c> — a lone "Object reference not set to an instance of an object" says
        /// nothing an agent (or a bug report) can act on, and cost a full play session before the
        /// stack trace was dug out of ShadowsMCP.log. The full trace still goes to the log via
        /// <see cref="Error"/>; this is the part that travels back over MCP. Never throws.
        /// </summary>
        public static string Describe(Exception ex, int frames = 3)
        {
            if (ex == null) return "unknown error";
            string head;
            try { head = ex.GetType().Name + ": " + ex.Message; }
            catch { return "unknown error"; }
            try
            {
                string trace = ex.StackTrace;
                if (string.IsNullOrEmpty(trace)) return head;
                string[] lines = trace.Split('\n');
                var sb = new System.Text.StringBuilder(head);
                for (int i = 0, taken = 0; i < lines.Length && taken < frames; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    sb.Append("\n  ").Append(line);
                    taken++;
                }
                return sb.ToString();
            }
            catch { return head; }
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
