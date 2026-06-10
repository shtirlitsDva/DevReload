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
}
