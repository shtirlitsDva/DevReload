<summary>
Make the DevReload MCP tools follow Anthropic's mcp-builder guide: structured results, outputSchema, tool annotations, correct input schemas. Decided 2026-09-27. Live test plan and results: GitHub issue #4.
</summary>

<facts-that-drove-the-design>
- The guide says: return both a text block and `structuredContent`, declare `outputSchema`, and set the annotations `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`.
- The MCP spec says `structuredContent` must be a JSON object, and the text block SHOULD carry the same JSON.
- Claude Code (anthropics/claude-code#45575, #79944) gives the model only `structuredContent` when both are present. Live test on 2.1.283: the model sees the data once, and image blocks are kept.
- Consequence: the text block must never carry information that `structuredContent` does not.
</facts-that-drove-the-design>

<decisions>
<lists>
The engine wraps every list result as `{ "items": [...] }` (`McpProtocol.ListItemsProperty`, `McpProtocol.ToStructuredContent`). Tool code does not change. The outputSchema of a list tool describes the same wrapper.
</lists>

<output-schema>
`JsonSchemaBuilder` generates it from the return type by reflection. It follows `McpProtocol.JsonOptions`: camelCase names, null members omitted (so only non-nullable members are required), enums as `{ "type": "string", "enum": [names] }`. System.Text.Json's `JsonSchemaExporter` is .NET 9+ and cannot load in AutoCAD 2025's .NET 8 default ALC. The same type mapping builds `inputSchema`, which fixes `string[]` parameters that were declared as `object`. There is no outputSchema for `string`, primitives, or `ToolResult`, because their shape is not known before the tool runs.
</output-schema>

<annotations>
`[AcadRpcTool(Effect = ToolEffect.ReadOnly | Additive | Destructive, Idempotent = bool)]`. Effect is required: an assembly with a tool that has no Effect is refused as a whole (`InvalidOperationException` naming the tools; auto-discovery reports it through `DevReloadDiagnostics`). `openWorldHint` is always false because every tool acts on the local host. `destructiveHint` and `idempotentHint` are not emitted for read-only tools, because the spec gives them no meaning there.
</annotations>

<bare-word-results>
The tools that returned a bare word now return records: open, new and activate return `AcadDocumentEntry`; close and send_command return `AcadLiveState` after the action; post_command returns `PostCommandResult(DocumentName)`. It has no success flag: a refusal is an error result, so a result already means "queued". An empty send_command string now throws instead of returning "no command".
</bare-word-results>

<enums>
Enums travel as their member name, as declared (`"Civil3D"`, `"Killed"`): `JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)` on `McpProtocol.JsonOptions`. This is the standard .NET approach, and the JSON Schema `enum` keyword lists the names. The name is the contract, because it survives reordering the enum and the agent can read it. A name+value pair was rejected because the number adds no information and invites callers to depend on it. Input matches names case-insensitively, and integers are refused, because `99` would otherwise bind as a value that no member has. `[Flags]` enums are typed as `string` without a list.
</enums>

<is-error>
A `ToolResult` with `Structured` and `IsError = true` used to go out as `isError: false`. All results now go through `McpProtocol.CallToolResult`. `CallToolResultStructured` was deleted.
</is-error>
</decisions>

<not-in-scope>
- The `title` annotation.
- Moving to the official ModelContextProtocol C# SDK.
</not-in-scope>
