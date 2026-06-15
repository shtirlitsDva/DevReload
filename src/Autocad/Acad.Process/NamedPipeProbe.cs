using System;
using System.IO;
using System.Linq;

namespace Acad.Process;

/// <summary>
/// Reliable named-pipe existence check. <see cref="File.Exists"/> /
/// <see cref="Path"/>-based probes against <c>\\.\pipe\NAME</c> do not
/// work — Windows reports valid pipes as non-existent until you actually
/// open them. The canonical pattern is to enumerate the pipe-namespace
/// directory and match by filename. Used by the controller and the bridge
/// to tell "pipe is up" from "pipe was never created".
/// </summary>
public static class NamedPipeProbe
{
    public static bool Exists(string pipeName)
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Any(f => string.Equals(
                    Path.GetFileName(f), pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
