using System;
using System.IO;

namespace DevReload.Rpc;

/// <summary>
/// File-based diagnostic log for the RPC bootstrap. Survives the
/// editor not being attached at IExtensionApplication.Initialize.
/// Path: %LOCALAPPDATA%\DevReload\rpc.log
/// </summary>
internal static class DevReloadLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevReload", "rpc.log");

    public static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:O} [{System.Diagnostics.Process.GetCurrentProcess().Id}] {message}{Environment.NewLine}");
        }
        catch { /* never throw from a logger */ }
    }
}
