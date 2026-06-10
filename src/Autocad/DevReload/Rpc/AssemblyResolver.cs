using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload.Rpc;

/// <summary>
/// Bridges AutoCAD's host runtime to DevReload's bundled dependency
/// graph. The MCP SDK and its Microsoft.Extensions.* deps target newer
/// BCL versions (System.Text.Json 10.x, etc.) than what AutoCAD's
/// .NET 8 shared framework ships. Without help, the default ALC can't
/// find them by name+version. We install a Resolving handler at
/// DevReload bootstrap that probes the bundle directory.
/// </summary>
internal static class AssemblyResolver
{
    private static bool _installed;
    private static string? _probeDir;

    public static void Install()
    {
        if (_installed) return;
        _probeDir = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
        if (string.IsNullOrEmpty(_probeDir))
        {
            DevReloadLog.Info("AssemblyResolver: cannot determine probe dir; not installing");
            return;
        }
        AssemblyLoadContext.Default.Resolving += OnResolving;
        _installed = true;
        DevReloadLog.Info($"AssemblyResolver: installed, probing {_probeDir}");
    }

    private static Assembly? OnResolving(AssemblyLoadContext alc, AssemblyName name)
    {
        if (_probeDir == null || string.IsNullOrEmpty(name.Name)) return null;
        var candidate = Path.Combine(_probeDir, name.Name + ".dll");
        if (!File.Exists(candidate)) return null;
        try
        {
            var loaded = alc.LoadFromAssemblyPath(candidate);
            DevReloadLog.Info($"AssemblyResolver: resolved {name.Name} {name.Version} ← {candidate}");
            return loaded;
        }
        catch (Exception ex)
        {
            DevReloadLog.Info($"AssemblyResolver: failed to load {candidate}: {ex.Message}");
            return null;
        }
    }
}
