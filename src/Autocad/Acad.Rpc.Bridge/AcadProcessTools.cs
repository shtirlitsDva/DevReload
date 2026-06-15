using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Acad.Process;
using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// MCP tool surface for AutoCAD/Civil 3D process control that the bridge
/// owns directly: discovery, launch, binding, and pipe-readiness. These run
/// in the bridge process and need no in-AutoCAD pipe, so they are callable
/// before any instance is up. Per-instance work (commands, state, documents)
/// is served by the in-AutoCAD <c>acad_*</c> tools and routed by pid.
/// </summary>
[AcadRpcSurface(Group = "acad")]
public static class AcadProcessTools
{
    // ── Discovery ─────────────────────────────────────────────────────

    [AcadRpcTool, Description("List AutoCAD/Civil 3D processes with pipe availability and which one the bridge is bound to.")]
    public static IReadOnlyList<AcadInstanceListing> ListInstances()
    {
        var bound = BridgeServices.Binding.Current?.Pid;
        return BridgeServices.Controller.EnumerateProcesses()
            .Select(p => new AcadInstanceListing(
                Pid: p.Pid,
                ProductName: p.ProcessName,
                MainWindowTitle: p.MainWindowTitle,
                PipeName: p.PipeName,
                PipeAvailable: p.PipeAvailable,
                IsBound: bound == p.Pid))
            .ToList();
    }

    [AcadRpcTool, Description("List installed AutoCAD/Civil 3D releases discovered via the registry. Pick a flavor to launch with acad_start.")]
    public static IReadOnlyList<AcadInstall> LocateInstall() => BridgeServices.Controller.Installs;

    // ── Lifecycle ─────────────────────────────────────────────────────

    [AcadRpcTool, Description("Launch a new AutoCAD/Civil 3D instance and bind the bridge to it. Auto-binds on success — pid-less acad_*/devreload_* calls default to this instance. Follow with acad_wait_pipe before driving it.")]
    public static AcadStartResult Start(
        [Description("Flavor to launch: AutoCAD, Civil3D, Plant3D, Mechanical, Electrical, Map3D, Architecture. Default Civil3D.")] string flavor = "Civil3D",
        [Description("Optional override of acad.exe location. If omitted, the newest registry-discovered install of the chosen flavor is used.")] string? installPath = null,
        [Description("Optional AutoCAD profile name (the /p switch).")] string? profile = null,
        [Description("Optional drawing path to open at startup.")] string? drawingPath = null,
        [Description("Optional multi-line AutoCAD-script text executed at startup. Newlines separate commands.")] string? startupCommands = null,
        [Description("Start visible (true, default) or minimized (false). Headless is unsupported for plugins with UI.")] bool visible = true)
    {
        var parsedFlavor = ParseFlavor(flavor);
        AcadInstall? install = null;
        if (!string.IsNullOrEmpty(installPath))
        {
            install = BridgeServices.Controller.Installs
                .FirstOrDefault(i =>
                    string.Equals(i.InstallPath, installPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.ExePath, installPath, StringComparison.OrdinalIgnoreCase));
            if (install == null)
                throw new InvalidOperationException(
                    $"installPath '{installPath}' does not match any discovered install. " +
                    $"Use acad_locate_install to see available paths.");
        }

        var options = new AcadLaunchOptions(
            Flavor: parsedFlavor,
            Install: install,
            Profile: profile,
            DrawingPath: drawingPath,
            StartupCommands: startupCommands,
            Visible: visible);

        var proc = BridgeServices.Controller.Launch(options);
        var pipeName = "acad-rpc-" + proc.Id;
        BridgeServices.Binding.TryBind(
            pid: proc.Id,
            productName: install?.ProductName ?? parsedFlavor.ToString(),
            pipeName: pipeName,
            bound: out var bound);

        return new AcadStartResult(
            Pid: proc.Id,
            ProductName: bound.ProductName,
            PipeName: bound.PipeName,
            ExePath: install?.ExePath ?? string.Empty);
    }

    [AcadRpcTool, Description("Wait for an instance's RPC pipe to appear. PRIMARY readiness gate: pid-specific and independent of any one instance's UI state, so it works while AutoCAD is on the Start tab or with no document. Returns elapsed time + success flag.")]
    public static async Task<AcadWaitResult> WaitPipe(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0,
        [Description("Max seconds to wait. Default 120.")] int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        var pipeName = "acad-rpc-" + resolved;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        return await BridgeServices.Controller.WaitForPipeAsync(pipeName, timeout, ct).ConfigureAwait(false);
    }

    [AcadRpcTool, Description("Bind the bridge to an already-running AutoCAD process. Sets the default instance for pid-less calls.")]
    public static BoundInstance Attach(
        [Description("Process ID of the AutoCAD instance to bind.")] int pid)
    {
        if (!BridgeServices.Controller.IsRunning(pid))
            throw new InvalidOperationException($"no acad.exe with pid {pid}");

        var match = BridgeServices.Controller.EnumerateProcesses().FirstOrDefault(p => p.Pid == pid);
        string productName = match?.ProcessName ?? "AutoCAD";

        var pipeName = "acad-rpc-" + pid;
        BridgeServices.Binding.TryBind(pid, productName, pipeName, out var bound);
        return bound;
    }

    [AcadRpcTool, Description("Release the bridge's default binding. Pid-less calls will error until you bind again; explicit-pid calls still work.")]
    public static DetachResult Detach()
    {
        var prev = BridgeServices.Binding.Current;
        bool released = BridgeServices.Binding.Detach();
        return new DetachResult(WasBound: released, PreviousPid: prev?.Pid ?? 0);
    }

    [AcadRpcTool, Description("Quit an AutoCAD instance by ending its process. Targets the given pid, or the bound instance. Detaches the bridge if the bound instance was quit.")]
    public static AcadQuitResult Quit(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        if (!BridgeServices.Controller.IsRunning(resolved))
            return new AcadQuitResult(AcadQuitOutcome.NotRunning, resolved, $"pid {resolved} is not running");

        bool killed = BridgeServices.Controller.Kill(resolved);
        MaybeDetach(resolved);
        return new AcadQuitResult(
            killed ? AcadQuitOutcome.Killed : AcadQuitOutcome.NotRunning,
            resolved,
            killed ? "process ended" : "could not end process");
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static AcadFlavor ParseFlavor(string flavor)
    {
        if (string.IsNullOrWhiteSpace(flavor)) return AcadFlavor.Civil3D;
        if (Enum.TryParse<AcadFlavor>(flavor, ignoreCase: true, out var f) && f != AcadFlavor.Unknown)
            return f;
        throw new ArgumentException(
            $"Unknown flavor '{flavor}'. Valid: AutoCAD, Civil3D, Plant3D, Mechanical, Electrical, Map3D, Architecture.",
            nameof(flavor));
    }

    private static void MaybeDetach(int pid)
    {
        var bound = BridgeServices.Binding.Current;
        if (bound?.Pid == pid) BridgeServices.Binding.Detach();
    }
}

public sealed record AcadInstanceListing(
    int Pid,
    string ProductName,
    string MainWindowTitle,
    string PipeName,
    bool PipeAvailable,
    bool IsBound);

public sealed record AcadStartResult(
    int Pid,
    string ProductName,
    string PipeName,
    string ExePath);

public sealed record DetachResult(
    bool WasBound,
    int PreviousPid);
