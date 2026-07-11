using ShadowsMcp.Core.Json;

namespace ShadowsMcp.Core.Rpc
{
    public static class RpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }

    /// <summary>A parsed JSON-RPC 2.0 message (request or notification).</summary>
    public sealed class RpcRequest
    {
        /// <summary>Raw id value (string or number), echoed back verbatim. Null JsonValue means notification.</summary>
        public JsonValue Id;
        public bool HasId;
        public string Method;
        public JsonValue Params; // object, array, or Null

        public bool IsNotification { get { return !HasId; } }
    }

    public static class JsonRpc
    {
        /// <summary>Parse and validate a single JSON-RPC 2.0 message. Returns null and sets error when invalid.</summary>
        public static RpcRequest ParseRequest(JsonValue root, out JsonValue error)
        {
            error = null;
            if (root == null || root.Kind != JsonKind.Object)
            {
                error = ErrorResponse(JsonValue.Null, RpcErrorCodes.InvalidRequest, "expected a JSON-RPC request object");
                return null;
            }
            if (root["jsonrpc"].AsString() != "2.0")
            {
                error = ErrorResponse(root["id"], RpcErrorCodes.InvalidRequest, "missing or invalid 'jsonrpc' member (must be \"2.0\")");
                return null;
            }
            string method = root["method"].AsString();
            if (string.IsNullOrEmpty(method))
            {
                error = ErrorResponse(root["id"], RpcErrorCodes.InvalidRequest, "missing or invalid 'method' member");
                return null;
            }
            var req = new RpcRequest
            {
                Method = method,
                Params = root["params"],
                Id = root["id"],
                HasId = root.ContainsKey("id") && !root["id"].IsNull
            };
            return req;
        }

        public static JsonValue SuccessResponse(JsonValue id, JsonValue result)
        {
            return JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonValue.Null)
                .Set("result", result ?? JsonValue.NewObject());
        }

        public static JsonValue ErrorResponse(JsonValue id, int code, string message)
        {
            return JsonValue.NewObject()
                .Set("jsonrpc", "2.0")
                .Set("id", id ?? JsonValue.Null)
                .Set("error", JsonValue.NewObject()
                    .Set("code", code)
                    .Set("message", message));
        }
    }
}
