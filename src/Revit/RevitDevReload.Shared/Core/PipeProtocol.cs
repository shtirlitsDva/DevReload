using System.Text.Json;

namespace RevitDevReload.Core
{
    public readonly struct PipeRequest
    {
        public PipeRequest(int id, string cmd, JsonElement? args)
        {
            Id = id;
            Cmd = cmd;
            Args = args;
        }

        public int Id { get; }
        public string Cmd { get; }
        public JsonElement? Args { get; }
    }

    // Newline-delimited JSON over a named pipe. Deliberately minimal — this
    // is the in-process control surface for the CLI/agent, not the full MCP
    // host (which is net8-only and lives outside the Revit process).
    public static class PipeProtocol
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static PipeRequest ParseRequest(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            int id = root.GetProperty("id").GetInt32();
            string cmd = root.GetProperty("cmd").GetString() ?? "";
            // JSON null and an absent "args" are the same thing — normalize
            // here so dispatchers can rely on HasValue meaning "real args"
            // (TryGetProperty on a Null-kind element throws).
            JsonElement? args = root.TryGetProperty("args", out var a)
                                && a.ValueKind != JsonValueKind.Null
                ? a.Clone()
                : (JsonElement?)null;
            return new PipeRequest(id, cmd, args);
        }

        public static string SerializeOk(int id, object? result)
        {
            return JsonSerializer.Serialize(
                new { id, ok = true, result }, _options);
        }

        public static string SerializeError(int id, string error)
        {
            return JsonSerializer.Serialize(
                new { id, ok = false, error }, _options);
        }
    }
}
