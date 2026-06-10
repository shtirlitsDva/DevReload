using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Acad.Process;
using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Bridge entry point. The bridge is a stdio JSON-RPC server (MCP)
/// that the agent connects to via its MCP client. The server's tools
/// split between:
///   - Local (process control via <see cref="AcadProcessTools"/>) —
///     callable BEFORE AutoCAD is running.
///   - Remote (the in-AutoCAD pipe — DevReload + any plugin's tools).
///
/// CLI:
///   --pipe &lt;name&gt;     bind immediately to that pipe name (legacy
///                       single-instance flow). Equivalent to attaching
///                       to a pid you already know about.
///   --pid &lt;pid&gt;       bind immediately to acad-rpc-&lt;pid&gt;.
///   (none)              start unbound. Use the acad_start / acad_attach
///                       tools from the agent side.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? initialPipe = null;
        int? initialPid = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    initialPipe = args[++i];
                    break;
                case "--pid" when i + 1 < args.Length:
                    initialPid = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
            }
        }

        // Compose: services → bind RpcCore → wire up forwarder → run.
        var controller = new AcadProcessController();
        var binding = new AcadInstanceBinding();
        var forwarder = new PipeForwarder(binding, Log);
        BridgeServices.Initialize(controller, binding, forwarder);

        var core = new RpcCore(
            mainThreadDispatcher: new InlineDispatcher(),
            serverName: "acad-agent",
            log: Log);
        core.RegisterAssembly(typeof(AcadProcessTools).Assembly);

        // Optional bootstrap binding: if the caller already knows a pid
        // or pipe name, bind it now so tools/list shows merged catalogue
        // from the first call.
        if (initialPid is int pidArg)
        {
            binding.TryBind(pidArg, "AutoCAD", $"acad-rpc-{pidArg}", out _);
        }
        else if (initialPipe != null)
        {
            // Best-effort: derive pid from pipe name suffix.
            int derivedPid = TryParsePidFromPipeName(initialPipe) ?? 0;
            binding.TryBind(
                pid: derivedPid,
                productName: "AutoCAD",
                pipeName: initialPipe,
                out _);
        }
        else
        {
            // No explicit binding requested — try the resume case: if
            // exactly one running AutoCAD already has DevReload's pipe
            // up, bind to it silently. Multiple candidates or none → stay
            // unbound and let the agent pick via acad_attach.
            AutoAttach.TryAttach(() => controller.EnumerateProcesses(), binding, Log);
        }

        using var host = new BridgeRpcHost(core, forwarder, Log);
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await host.RunAsync(stdin, stdout, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            forwarder.Dispose();
        }
        return 0;
    }

    private static int? TryParsePidFromPipeName(string pipeName)
    {
        const string prefix = "acad-rpc-";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = pipeName.AsSpan(prefix.Length);
        return int.TryParse(rest, out int pid) ? pid : null;
    }

    private static void Log(string s) => Console.Error.WriteLine("[Acad.Rpc.Bridge] " + s);

    private static void PrintHelp()
    {
        Console.Error.WriteLine("Acad.Rpc.Bridge — stdio↔acad-agent MCP server.");
        Console.Error.WriteLine("Usage: Acad.Rpc.Bridge [--pipe <name> | --pid <pid>]");
        Console.Error.WriteLine("  No args:    start unbound. Use acad_start / acad_attach from the agent.");
        Console.Error.WriteLine("  --pid n:    pre-bind to AutoCAD pid n.");
        Console.Error.WriteLine("  --pipe x:   pre-bind to pipe name x (legacy single-instance).");
    }
}
