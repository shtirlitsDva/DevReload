using System;
using System.IO;
using System.Text.Json.Nodes;

using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Disk-persisted snapshot of the in-AutoCAD remote tool surface
/// (<c>devreload_*</c>/<c>ui_*</c> plus any plugin-contributed tools). The
/// bridge serves this when no instance is currently bound so the merged
/// <c>tools/list</c> never collapses to local <c>acad_*</c> the moment the
/// bound AutoCAD goes away (crash) or the bridge is killed + respawned by the
/// MCP client. Calls still route by the <c>pid</c> the agent supplies, so a
/// stable catalogue is all that's needed to keep working across instance/
/// session churn — no pid persistence required.
///
/// The remote tool SET is identical across instances of the same plugin
/// version, so a snapshot taken from one instance is valid for any other. The
/// snapshot is refreshed from a live instance whenever one is connected, and
/// version-stamped so a plugin upgrade invalidates a stale shape. Best-effort:
/// any IO/parse failure degrades to "no cache" (the pre-existing behaviour).
/// </summary>
public sealed class RemoteToolCatalogCache
{
    private readonly string _path;
    private readonly string _version;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    public RemoteToolCatalogCache(string version, Action<string>? log = null)
    {
        _version = version ?? string.Empty;
        _log = log ?? (_ => { });
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevReload");
        _path = Path.Combine(dir, "remote-tools.cache.json");
    }

    /// <summary>Persist the raw remote tools array (no pid injection — that
    /// happens at list-build time). Called after every successful live fetch.</summary>
    public void Save(JsonArray rawRemoteTools)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var doc = new JsonObject
                {
                    ["version"] = _version,
                    ["tools"] = rawRemoteTools.DeepClone(),
                };
                File.WriteAllText(_path, doc.ToJsonString(McpProtocol.JsonOptions));
            }
        }
        catch (Exception ex) { _log($"RemoteToolCatalogCache: save failed: {ex.Message}"); }
    }

    /// <summary>Last cached raw remote tools array, or null when absent or the
    /// version stamp no longer matches this build.</summary>
    public JsonArray? Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return null;
                if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject obj) return null;
                if (obj["version"]?.GetValue<string>() != _version) return null;
                return obj["tools"]?.DeepClone() as JsonArray;
            }
        }
        catch (Exception ex)
        {
            _log($"RemoteToolCatalogCache: load failed: {ex.Message}");
            return null;
        }
    }
}
