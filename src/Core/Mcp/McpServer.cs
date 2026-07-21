using System;
using ShadowsMcp.Core.Json;
using ShadowsMcp.Core.Rpc;
using ShadowsMcp.Core.Util;

namespace ShadowsMcp.Core.Mcp
{
    /// <summary>
    /// Model Context Protocol server core (transport-agnostic): routes JSON-RPC messages
    /// for the Streamable HTTP transport's plain-JSON response mode.
    /// Supported methods: initialize, notifications/*, ping, tools/list, tools/call.
    /// </summary>
    public sealed class McpServer
    {
        private static readonly string[] SupportedProtocolVersions =
        {
            "2024-11-05", "2025-03-26", "2025-06-18"
        };
        private const string DefaultProtocolVersion = "2025-03-26";

        private readonly IToolHost _host;
        private readonly string _serverName;
        private readonly string _serverVersion;
        private readonly string _instructions;

        /// <param name="instructions">Optional MCP initialize.instructions text (a standing brief the client
        /// injects into the model's context). Kept out of this game-agnostic core: the caller supplies any
        /// game-specific onboarding. When null/empty, the initialize result simply omits the field.</param>
        public McpServer(IToolHost host, string serverName, string serverVersion, string instructions = null)
        {
            _host = host;
            _serverName = serverName;
            _serverVersion = serverVersion;
            _instructions = instructions;
        }

        /// <summary>
        /// Handle one HTTP POST body. Returns the response body, or null for "202 Accepted, no body"
        /// (notifications). statusCode is the HTTP status to send.
        /// </summary>
        public string HandlePost(string body, out int statusCode)
        {
            JsonValue root;
            try
            {
                root = JsonParser.Parse(body);
            }
            catch (JsonParseException ex)
            {
                statusCode = 400;
                return JsonWriter.Write(
                    JsonRpc.ErrorResponse(JsonValue.Null, RpcErrorCodes.ParseError, "parse error: " + ex.Message), false);
            }

            if (root.Kind == JsonKind.Array)
            {
                // JSON-RPC batching was removed from the MCP spec in 2025-06-18; we never accept it.
                statusCode = 400;
                return JsonWriter.Write(
                    JsonRpc.ErrorResponse(JsonValue.Null, RpcErrorCodes.InvalidRequest, "batch requests are not supported"), false);
            }

            // Client responses (e.g. to server-initiated requests, which we never send) and
            // notifications get 202 with no body.
            if (root.Kind == JsonKind.Object && !root.ContainsKey("method"))
            {
                statusCode = 202;
                return null;
            }

            JsonValue parseError;
            RpcRequest req = JsonRpc.ParseRequest(root, out parseError);
            if (req == null)
            {
                statusCode = 400;
                return JsonWriter.Write(parseError, false);
            }

            if (req.IsNotification)
            {
                // notifications/initialized, notifications/cancelled, ... — nothing to do.
                statusCode = 202;
                return null;
            }

            JsonValue response;
            try
            {
                response = Dispatch(req);
            }
            catch (Exception ex)
            {
                Log.Error("internal error handling '" + req.Method + "'", ex);
                response = JsonRpc.ErrorResponse(req.Id, RpcErrorCodes.InternalError, "internal error: " + Log.Describe(ex));
            }
            statusCode = 200;
            return JsonWriter.Write(response, false);
        }

        private JsonValue Dispatch(RpcRequest req)
        {
            switch (req.Method)
            {
                case "initialize": return HandleInitialize(req);
                case "ping": return JsonRpc.SuccessResponse(req.Id, JsonValue.NewObject());
                case "tools/list": return HandleToolsList(req);
                case "tools/call": return HandleToolsCall(req);
                default:
                    return JsonRpc.ErrorResponse(req.Id, RpcErrorCodes.MethodNotFound, "method not found: " + req.Method);
            }
        }

        private JsonValue HandleInitialize(RpcRequest req)
        {
            string requested = req.Params["protocolVersion"].AsString();
            string negotiated = DefaultProtocolVersion;
            foreach (string v in SupportedProtocolVersions)
            {
                if (v == requested) { negotiated = v; break; }
            }

            JsonValue result = JsonValue.NewObject()
                .Set("protocolVersion", negotiated)
                .Set("capabilities", JsonValue.NewObject()
                    .Set("tools", JsonValue.NewObject()))
                .Set("serverInfo", JsonValue.NewObject()
                    .Set("name", _serverName)
                    .Set("version", _serverVersion));
            if (!string.IsNullOrEmpty(_instructions))
                result.Set("instructions", _instructions);
            return JsonRpc.SuccessResponse(req.Id, result);
        }

        private JsonValue HandleToolsList(RpcRequest req)
        {
            JsonValue tools = JsonValue.NewArray();
            foreach (ToolDefinition t in _host.ListTools())
            {
                tools.Add(JsonValue.NewObject()
                    .Set("name", t.Name)
                    .Set("description", t.Description)
                    .Set("inputSchema", t.InputSchema));
            }
            return JsonRpc.SuccessResponse(req.Id, JsonValue.NewObject().Set("tools", tools));
        }

        private JsonValue HandleToolsCall(RpcRequest req)
        {
            string name = req.Params["name"].AsString();
            if (string.IsNullOrEmpty(name))
                return JsonRpc.ErrorResponse(req.Id, RpcErrorCodes.InvalidParams, "missing tool 'name'");

            JsonValue args = req.Params["arguments"];
            if (args.IsNull) args = JsonValue.NewObject();

            ToolResult result;
            try
            {
                result = _host.Execute(name, args);
            }
            catch (Exception ex)
            {
                // Tool execution failures are tool-level errors, not protocol errors.
                Log.Error("tool '" + name + "' threw", ex);
                result = ToolResult.Error("tool '" + name + "' failed: " + Log.Describe(ex));
            }

            if (result == null)
                return JsonRpc.ErrorResponse(req.Id, RpcErrorCodes.InvalidParams, "unknown tool: " + name);

            JsonValue content = JsonValue.NewArray()
                .Add(JsonValue.NewObject()
                    .Set("type", "text")
                    .Set("text", result.Text));
            JsonValue callResult = JsonValue.NewObject()
                .Set("content", content)
                .Set("isError", result.IsError);
            return JsonRpc.SuccessResponse(req.Id, callResult);
        }
    }
}
