using System.Collections.Generic;
using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Core.Mcp
{
    /// <summary>
    /// The seam between the transport-facing MCP server and whatever executes tools.
    /// The game host marshals Execute onto Unity's main thread; the test host runs inline.
    /// Execute is called on a server worker thread and may block.
    /// </summary>
    public interface IToolHost
    {
        IReadOnlyList<ToolDefinition> ListTools();
        ToolResult Execute(string name, JsonValue args);
    }

    /// <summary>Registry-based host that executes tool handlers inline. Subclasses can wrap Execute.</summary>
    public class ToolHostBase : IToolHost
    {
        private readonly List<ToolDefinition> _tools = new List<ToolDefinition>();
        private readonly Dictionary<string, ToolDefinition> _byName =
            new Dictionary<string, ToolDefinition>(System.StringComparer.Ordinal);

        public void Register(ToolDefinition tool)
        {
            _tools.Add(tool);
            _byName[tool.Name] = tool;
        }

        public IReadOnlyList<ToolDefinition> ListTools() { return _tools; }

        public virtual ToolResult Execute(string name, JsonValue args)
        {
            ToolDefinition tool;
            if (!_byName.TryGetValue(name, out tool))
                return null; // McpServer turns null into "unknown tool" -32602
            return tool.Handler(args ?? JsonValue.NewObject());
        }

        public bool HasTool(string name) { return _byName.ContainsKey(name); }
    }
}
