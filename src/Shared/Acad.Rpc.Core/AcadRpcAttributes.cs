using System;

namespace Acad.Rpc.Core;

/// <summary>
/// Marks a class containing tool methods. Required on the declaring
/// class so attribute scan can be fast (no need to inspect every type
/// in the assembly).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AcadRpcSurfaceAttribute : Attribute
{
    /// <summary>
    /// Optional group / namespace prefix for tools on this class.
    /// Default is the source assembly's simple name (dot→underscore).
    /// </summary>
    public string? Group { get; set; }
}

/// <summary>
/// Marks a method to be exposed as an MCP tool. The method may be
/// static or instance; v1 supports static only. Parameters bind from
/// the call's "arguments" JSON object by name. Use
/// <see cref="System.ComponentModel.DescriptionAttribute"/> on
/// parameters for parameter docs.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AcadRpcToolAttribute : Attribute
{
    /// <summary>Override the auto-derived tool name. When unset, the
    /// tool name is "<group>_<snake_case_method_name>".</summary>
    public string? Name { get; set; }

    /// <summary>What the tool does to its environment. Required: a tool left
    /// at <see cref="ToolEffect.Unspecified"/> refuses to register, because the
    /// MCP defaults for a missing hint (destructive, not read-only) would
    /// silently mislabel every read tool.</summary>
    public ToolEffect Effect { get; set; }

    /// <summary>Calling again with the same arguments has no further effect.
    /// Emitted as <c>idempotentHint</c>; ignored for
    /// <see cref="ToolEffect.ReadOnly"/>, where the spec gives it no meaning.</summary>
    public bool Idempotent { get; set; }
}

/// <summary>
/// A tool's effect on its environment, emitted as the MCP tool annotations
/// <c>readOnlyHint</c> / <c>destructiveHint</c>. Every tool here acts on the
/// local AutoCAD / Revit host only, so <c>openWorldHint</c> is always false.
/// </summary>
public enum ToolEffect
{
    /// <summary>Not declared. Registration fails.</summary>
    Unspecified = 0,

    /// <summary>Reads state; changes nothing.</summary>
    ReadOnly,

    /// <summary>Changes state, but only by adding to it (opens, creates, binds).</summary>
    Additive,

    /// <summary>May overwrite, delete, discard or run arbitrary actions.</summary>
    Destructive,
}

/// <summary>
/// Declares host state this tool needs before it can run — e.g. a current
/// drawing. The KEY is all that lives here; what it means and how it is
/// checked is the host's, via <see cref="AcadRpcHostOptions.StateChecks"/>.
/// </summary>
/// <remarks>
/// The split is the point. This engine is shared by hosts with different
/// document models, so the precondition itself cannot live here — but the
/// enforcement and the wording the agent reads must exist exactly once, or
/// they drift. Every tool that needed a current drawing used to hand-roll its
/// own null check and its own message, and two of them forgot, which is how a
/// missing document surfaced as a NullReferenceException.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RpcRequiresAttribute : Attribute
{
    public RpcRequiresAttribute(string stateKey) => StateKey = stateKey;

    /// <summary>Key into the host's state checks. Unknown at dispatch time =
    /// a wiring bug, and reported as one rather than assumed satisfied.</summary>
    public string StateKey { get; }
}

/// <summary>One host-supplied precondition.</summary>
/// <param name="Requirement">The clause appended to every requiring tool's
/// description, as the agent reads it (e.g. "current drawing"). Short and
/// literal — it is repeated on every tool that declares the key.</param>
/// <param name="Check">Returns null when satisfied, otherwise the refusal
/// message. Runs on the same thread the tool would have run on, so it can use
/// host APIs with the same thread affinity.</param>
public sealed record RpcStateCheck(string Requirement, Func<string?> Check);
