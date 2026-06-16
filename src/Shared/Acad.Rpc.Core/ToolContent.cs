using System.Collections.Generic;

namespace Acad.Rpc.Core;

/// <summary>
/// One image content block for an MCP tool result. <paramref name="Base64Data"/>
/// is the raw image bytes, base64-encoded (no data-URI prefix); the MCP
/// <c>image</c> content block carries it in its <c>data</c> field. This is
/// opt-in — a tool returns one (or an array) of these only when it actually
/// produces an image, e.g. a screenshot. Plain string / object returns are
/// unaffected and keep travelling as text / structuredContent.
/// </summary>
public sealed record ToolImage(string Base64Data, string MimeType = "image/png");

/// <summary>
/// Rich tool return value combining any of: a text block, a structured
/// object (spec ≥ 2025-06-18 <c>structuredContent</c>), and image content
/// blocks. Tools that only need text or a structured object keep returning
/// <c>string</c> / a record as before; this type exists for tools that must
/// attach images (e.g. a screenshot tool returning the PNG plus its bounds).
/// </summary>
public sealed class ToolResult
{
    /// <summary>Human-/agent-readable text block. When null and
    /// <see cref="Structured"/> is set, the structured object's JSON is used
    /// as the text block (mirrors how a bare object return behaves).</summary>
    public string? Text { get; init; }

    /// <summary>Optional structured payload. Serialized to
    /// <c>structuredContent</c>; must be a JSON object to appear there
    /// (arrays/primitives are dropped from structuredContent per spec, same
    /// rule as a bare object return).</summary>
    public object? Structured { get; init; }

    /// <summary>Image content blocks appended after the text block.</summary>
    public IReadOnlyList<ToolImage>? Images { get; init; }

    /// <summary>Marks the result as an error (sets <c>isError</c>).</summary>
    public bool IsError { get; init; }
}
