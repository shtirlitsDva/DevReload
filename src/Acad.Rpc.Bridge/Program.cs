using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Acad.Rpc.Bridge: stateless stdio↔named-pipe forwarder.
//
// Claude Code talks MCP wire format on this process's stdin/stdout.
// We forward bytes bidirectionally to the in-AutoCAD pipe
// \\.\pipe\acad-rpc-<pid> hosted by Acad.Rpc.Core inside DevReload.
//
// CLI:
//   --pipe <name>       attach to that pipe by name
//   --pid <pid>         attach to acad-rpc-<pid>
//   (none)              enumerate acad-rpc-* pipes; if exactly one, attach
//
// No state. No parsing. Bytes in, bytes out. Exit on pipe disconnect.

namespace Acad.Rpc.Bridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        int? pid = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length: pipeName = args[++i]; break;
                case "--pid" when i + 1 < args.Length:
                    pid = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--help" or "-h":
                    PrintHelp(); return 0;
            }
        }

        if (pipeName == null)
        {
            if (pid != null)
            {
                pipeName = $"acad-rpc-{pid}";
            }
            else
            {
                pipeName = DiscoverPipe();
                if (pipeName == null) return 2;
            }
        }

        Console.Error.WriteLine($"[Acad.Rpc.Bridge] connecting to \\\\.\\pipe\\{pipeName}");

        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(5000).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"[Acad.Rpc.Bridge] timeout connecting to {pipeName}. " +
                                    "Is AutoCAD running with DevReload loaded?");
            return 3;
        }

        Console.Error.WriteLine("[Acad.Rpc.Bridge] connected; forwarding bytes");

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        var cts = new CancellationTokenSource();
        var t1 = CopyAsync(stdin, pipe, cts.Token);    // stdin → pipe
        var t2 = CopyAsync(pipe, stdout, cts.Token);   // pipe → stdout

        var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
        cts.Cancel();
        try { await finished.ConfigureAwait(false); } catch { }
        return 0;
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* pipe broken */ }
    }

    private static string? DiscoverPipe()
    {
        var pipes = Directory.GetFiles(@"\\.\pipe\")
            .Select(Path.GetFileName)
            .Where(n => n != null && n.StartsWith("acad-rpc-", StringComparison.Ordinal))
            .ToArray();

        if (pipes.Length == 0)
        {
            Console.Error.WriteLine("[Acad.Rpc.Bridge] no acad-rpc-* pipes found. " +
                                    "Is AutoCAD running with DevReload loaded?");
            return null;
        }

        if (pipes.Length == 1) return pipes[0];

        Console.Error.WriteLine("[Acad.Rpc.Bridge] multiple AutoCAD instances found:");
        foreach (var p in pipes) Console.Error.WriteLine($"    {p}");
        Console.Error.WriteLine("    pass --pid <n> to disambiguate.");
        return null;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("Acad.Rpc.Bridge — stdio↔named-pipe MCP forwarder.");
        Console.Error.WriteLine("Usage: Acad.Rpc.Bridge [--pipe <name> | --pid <pid>]");
    }
}
