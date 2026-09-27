using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
    public const string LatestProtocolVersion = "2025-11-25";

    // Versions whose required server-side behavior (for a tools-only
    // server over a line-delimited pipe) we actually implement.
    private static readonly HashSet<string> SupportedVersions = new(StringComparer.Ordinal)
    {
        "2025-11-25", "2025-06-18", "2025-03-26",
    };

    /// <summary>
    /// Spec negotiation rule: if the client's requested version is
    /// supported, echo it back; otherwise answer with our latest and
    /// let the client decide whether to proceed.
    /// </summary>
    public static string NegotiateVersion(string? requested) =>
        requested != null && SupportedVersions.Contains(requested)
            ? requested
            : LatestProtocolVersion;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Output goes over a pipe to an MCP client, never into HTML — relaxed
        // escaping keeps '>' and non-ASCII readable instead of \uXXXX.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enums travel as their member name ("Civil3D"), the form the agent can
        // read and the one that survives reordering the enum. Names are kept as
        // declared, so an enum value reads the same as the strings tools already
        // take. Integers are refused on input: a number is not the contract, and
        // an undefined one (99) would otherwise bind as a value no member has.
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static JsonObject InitializeResult(
        string serverName, string serverVersion, string protocolVersion)
    {
        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
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

    /// <summary>The property a list result is wrapped under, because
    /// <c>structuredContent</c> must be a JSON object. The outputSchema of a
    /// list-returning tool describes the same wrapper.</summary>
    public const string ListItemsProperty = "items";

    /// <summary>
    /// The <c>structuredContent</c> for a serialized tool result: an object as
    /// is, an array wrapped as <c>{ "items": [...] }</c>, anything else none.
    /// </summary>
    /// <remarks>
    /// Every structured result must reach structuredContent, not only the text
    /// block: when both are present Claude Code shows the model only
    /// structuredContent (anthropics/claude-code#45575).
    /// </remarks>
    public static JsonObject? ToStructuredContent(JsonNode? node) => node switch
    {
        JsonObject obj => obj,
        JsonArray arr => new JsonObject { [ListItemsProperty] = arr },
        _ => null,
    };

    /// <summary>An <c>image</c> content block: base64 bytes + MIME type.</summary>
    public static JsonObject ImageContentBlock(string base64Data, string mimeType) =>
        new()
        {
            ["type"] = "image",
            ["data"] = base64Data,
            ["mimeType"] = mimeType,
        };

    /// <summary>
    /// General tool result assembler. Emits a text block (when
    /// <paramref name="text"/> is non-empty, OR when there are no images — the
    /// content array must never be empty), followed by one image block per
    /// entry in <paramref name="images"/>, plus optional
    /// <c>structuredContent</c>. With no images and no structured object it
    /// matches <see cref="CallToolResultText"/>.
    /// </summary>
    public static JsonObject CallToolResult(
        string? text,
        JsonObject? structuredContent,
        IEnumerable<ToolImage>? images,
        bool isError)
    {
        var content = new JsonArray();

        var imageBlocks = new JsonArray();
        if (images != null)
        {
            foreach (var img in images)
            {
                if (img == null) continue;
                imageBlocks.Add(ImageContentBlock(img.Base64Data, img.MimeType));
            }
        }

        // A text block is always present unless images carry the payload and
        // there is no text — keeps the content array non-empty either way.
        if (!string.IsNullOrEmpty(text) || imageBlocks.Count == 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text ?? "" });

        foreach (var b in imageBlocks) content.Add(b!.DeepClone());

        var result = new JsonObject { ["content"] = content, ["isError"] = isError };
        if (structuredContent != null) result["structuredContent"] = structuredContent;
        return result;
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
