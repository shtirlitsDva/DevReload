using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Acad.Rpc.Core;

/// <summary>
/// Constants and shapes for the MCP wire protocol. We implement the
/// minimum surface needed for tool discovery and invocation:
///   - initialize  (request)
///   - notifications/initialized  (one-way)
///   - tools/list  (request)
///   - tools/call  (request)
///   - notifications/tools/list_changed  (server-to-client one-way)
///   - ping  (request, returns {})
/// Wire format: line-delimited JSON-RPC 2.0. Each message is exactly
/// one JSON object on one line (no Content-Length framing).
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2025-03-26";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonObject InitializeResult(string serverName, string serverVersion)
    {
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = serverName,
                ["version"] = serverVersion,
            },
        };
    }

    public static JsonObject ToolsListResult(IEnumerable<JsonObject> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools) arr.Add(t.DeepClone());
        return new JsonObject { ["tools"] = arr };
    }

    public static JsonObject CallToolResultText(string text, bool isError = false)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
            ["isError"] = isError,
        };
    }

    public static JsonObject MakeRequest(string method, JsonObject? @params, int id)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null) msg["params"] = @params;
        return msg;
    }

    public static JsonObject MakeResponse(JsonNode id, JsonNode? result, JsonObject? error)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
        };
        if (error != null) msg["error"] = error;
        else msg["result"] = result ?? new JsonObject();
        return msg;
    }

    public static JsonObject MakeNotification(string method, JsonObject? @params)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params != null) msg["params"] = @params;
        return msg;
    }

    public static JsonObject MakeError(int code, string message)
    {
        return new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
    }

    public static class ErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }
}
