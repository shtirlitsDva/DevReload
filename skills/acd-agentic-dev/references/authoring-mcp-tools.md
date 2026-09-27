<authoring-mcp-tools>
A plugin can publish its own MCP tools. `RpcCore` scans the assembly on load:

```csharp
[AcadRpcSurface(Group = "myplugin")]
public static class MyPluginTools
{
    public sealed record PipeReport(string Handle, double Length, PipeKind Kind, string? Note);
    public sealed record ErasedPipe(string Handle);

    [AcadRpcTool(Effect = ToolEffect.ReadOnly), RunOnAcadMainThread,
     Description("One pipe by handle.")]
    public static PipeReport GetPipe([Description("Entity handle.")] string handle) => ...;

    [AcadRpcTool(Effect = ToolEffect.Destructive), RunOnAcadMainThread,
     Description("Erase a pipe by handle.")]
    public static ErasedPipe ErasePipe([Description("Entity handle.")] string handle) => ...;
}
```

- Tool name = `<group>_<method in snake_case>` (`myplugin_get_pipe`). Class and method are `public static`.
- `Effect` is required: `ReadOnly` (no change), `Additive` (creates, does not overwrite), `Destructive` (changes or deletes). Add `Idempotent = true` when a repeat call with the same arguments has no further effect. A tool without `Effect` makes the scan refuse the WHOLE assembly: none of its tools register. The plugin and its commands still load; the reload result is `success:false`, `loaded:true`, and `message` names the tool.
- Return a record, a `Task<record>`, or a list of records. The engine generates `outputSchema` from the type: camelCase names, non-nullable members required, enums as names. A list becomes `{"items":[...]}`.
- `string` and primitive returns get no `outputSchema`. Use them only for free text.
- Enum parameters: declare the enum type, with a C# default for an optional one. The schema lists the names and the binder refuses other values.
- A call that does nothing (bad argument, failed precondition): throw `ArgumentException` or `InvalidOperationException`. The message becomes the error result text.
- An operation that ran and has an outcome the caller acts on (a build failed, a restart is required): return a record with `success` and the state after the call.
- `ToolResult` is for images or a shape known only at run time. Put all data in `Structured`; the model reads `structuredContent` only, so data only in `Text` does not reach it.
- `[RunOnAcadMainThread]` for any AutoCAD API access. `Description` on the tool and each parameter.
- After `devreload_reload`, confirm with `devreload_list_tools`. A tool that does not show: class or method not `public`, or a parameter type the binder does not support.
</authoring-mcp-tools>
