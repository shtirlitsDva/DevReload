<acd-mcp>
`Acd.Mcp.Bridge.exe` (stdio) talks over the pipe `acd-mcp-<pid>` to `Acd.Mcp.dll` inside AutoCAD. `autocad_script_execute` compiles a C# snippet with Roslyn and runs it on the main thread under `Doc.LockDocument()`. The session keeps state between calls. Globals: `Doc`, `Db`, `Ed`, `CivilDoc` (null outside Civil 3D), `Acd`.

Load `/acd-mcp:script` (one drawing) or `/acd-mcp:batch` (many .dwg) before you write snippets. Key rules:
- Block-form `using` only: `using (var tr = ...) { ... }`. Top-level `using var tr = ...;` parses as a using directive and does not compile.
- Return entities (or `List<T>`/`IEnumerable<T>` of them) and read `returnValueJson`. Registered DTOs project them in a stable shape; hand-built anonymous objects change shape between calls and make assertions brittle.
- `{"$unsupported":"T"}` = no DTO for `T`. Add one with `/acd-mcp:add-dto` if you query `T` again.

Use it to arrange fixtures (draw input entities, set layers) and to assert (count and inspect output).

The pipe accepts concurrent connections; snippets run serialized on the main thread.
</acd-mcp>

<triage>
Resource `acd-mcp://status`: per-handler health. The bridge serves it, so it answers when tool calls do not.

- `PIPE_NOT_LISTENING` — `acd-mcp-<pid>` is still coming up after the load. Retry once.
- `MULTIPLE_AUTOCAD_PLUGINS` — Acd.Mcp is loaded in more than one instance. Pass `pid` (from `acad_list_instances`).
</triage>
