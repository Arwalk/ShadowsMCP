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

        /// <summary>
        /// Serializes the JSON payload compactly (no indentation) and drops null-valued keys — the
        /// consumer is an agent, not a human, so whitespace and "key": null pairs are wasted tokens.
        /// Use the (payload, omitNull) overload with omitNull:false where a null carries meaning
        /// (e.g. the inspect reflection tool).
        /// </summary>
        public static ToolResult Ok(JsonValue payload)
        {
            return Ok(payload, omitNull: true);
        }

        public static ToolResult Ok(JsonValue payload, bool omitNull)
        {
            return new ToolResult { Text = JsonWriter.Write(payload ?? JsonValue.Null, false, omitNull), IsError = false };
        }

        public static ToolResult Error(string message)
        {
            return new ToolResult { Text = message ?? "unknown error", IsError = true };
        }
    }
}
