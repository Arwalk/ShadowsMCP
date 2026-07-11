using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Core.Mcp
{
    /// <summary>Outcome of a tool call: text payload plus error flag (maps to MCP's content + isError).</summary>
    public sealed class ToolResult
    {
        public string Text;
        public bool IsError;

        public static ToolResult Ok(string text)
        {
            return new ToolResult { Text = text ?? "", IsError = false };
        }

        /// <summary>Pretty-prints the JSON payload so it reads well in MCP clients.</summary>
        public static ToolResult Ok(JsonValue payload)
        {
            return new ToolResult { Text = JsonWriter.Write(payload ?? JsonValue.Null, true), IsError = false };
        }

        public static ToolResult Error(string message)
        {
            return new ToolResult { Text = message ?? "unknown error", IsError = true };
        }
    }
}
