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
/// MCP tool surface for AutoCAD/Civil 3D process control. Lives in the
/// bridge because every method needs the bound-pid state and the
/// pipe forwarder, which only the bridge owns.
///
/// All methods are thin façades over <see cref="AcadProcessController"/>
/// + <see cref="AcadComClient"/> — no logic here beyond the binding
/// resolution. Compose-only.
/// </summary>
[AcadRpcSurface(Group = "acad")]
public static class AcadProcessTools
{
    // ── Discovery ─────────────────────────────────────────────────────

    [AcadRpcTool, Description("List AutoCAD/Civil 3D processes the OS reports + COM-visible state. Use when picking an instance to attach to, or to verify cleanup.")]
    public static IReadOnlyList<AcadInstanceListing> ListInstances()
    {
        var bound = BridgeServices.Binding.Current?.Pid;
        var osLevel = BridgeServices.Controller.EnumerateProcesses();
        var comLevel = AcadComClient.EnumerateInstances()
            .ToDictionary(c => c.Pid, c => c);

        var merged = new List<AcadInstanceListing>(osLevel.Count);
        foreach (var p in osLevel)
        {
            comLevel.TryGetValue(p.Pid, out var c);
            merged.Add(new AcadInstanceListing(
                Pid: p.Pid,
                ProductName: c?.ProcessName ?? p.ProcessName,
                MainWindowTitle: c?.MainWindowTitle ?? p.MainWindowTitle,
                PipeName: p.PipeName,
                PipeAvailable: p.PipeAvailable,
                ComAvailable: c != null,
                IsBound: bound == p.Pid));
        }
        return merged;
    }

    [AcadRpcTool, Description("List installed AutoCAD/Civil 3D releases discovered via the registry. Pick a flavor to launch with acad_start.")]
    public static IReadOnlyList<AcadInstall> LocateInstall() => BridgeServices.Controller.Installs;

    // ── Lifecycle ─────────────────────────────────────────────────────

    [AcadRpcTool, Description("Launch a new AutoCAD/Civil 3D instance and bind the bridge to it. Auto-binds on success — subsequent acad_* tools default to this pid. Use 'startupCommands' to NETLOAD DevReload at boot (the canonical agentic-dev pattern). Follow with acad_wait_pipe to wait for the plugin's RPC pipe to come up.")]
    public static AcadStartResult Start(
        [Description("Flavor to launch: AutoCAD, Civil3D, Plant3D, Mechanical, Electrical, Map3D, Architecture. Default Civil3D.")] string flavor = "Civil3D",
        [Description("Optional override of acad.exe location. If omitted, the newest registry-discovered install of the chosen flavor is used.")] string? installPath = null,
        [Description("Optional AutoCAD profile name (the /p switch).")] string? profile = null,
        [Description("Optional drawing path to open at startup. AutoCAD's COM API is reachable from cold start either way — pass a drawing only when the agent has a specific file to work with.")] string? drawingPath = null,
        [Description("Optional multi-line AutoCAD-script text executed at startup. Newlines separate commands. Typical use: \"FILEDIA\\n0\\nNETLOAD\\n<bundle-path>\\nFILEDIA\\n1\\n\" to load DevReload at boot.")] string? startupCommands = null,
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

    [AcadRpcTool, Description("Wait for the in-AutoCAD RPC pipe to appear. PRIMARY readiness check for plugin work: independent of COM/ROT, so it works even when AutoCAD opens to the Start tab or with an unsaved drawing. Returns elapsed time + success flag.")]
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

    [AcadRpcTool, Description("Bind the bridge to an already-running AutoCAD process. Subsequent acad_* tools default to this pid.")]
    public static BoundInstance Attach(
        [Description("Process ID of the AutoCAD instance to bind.")] int pid)
    {
        if (!BridgeServices.Controller.IsRunning(pid))
            throw new InvalidOperationException($"no acad.exe with pid {pid}");

        // Best effort: read product name through COM if we can.
        string productName = "AutoCAD";
        using (var client = AcadComClient.AttachByPid(pid))
            if (client != null) productName = client.ProductName;

        var pipeName = "acad-rpc-" + pid;
        BridgeServices.Binding.TryBind(pid, productName, pipeName, out var bound);
        return bound;
    }

    [AcadRpcTool, Description("Release the bridge's current binding. Future acad_* calls without an explicit pid will error until you bind again.")]
    public static DetachResult Detach()
    {
        var prev = BridgeServices.Binding.Current;
        bool released = BridgeServices.Binding.Detach();
        return new DetachResult(WasBound: released, PreviousPid: prev?.Pid ?? 0);
    }

    [AcadRpcTool, Description("Block until the target AutoCAD reports IsQuiescent (and, if requireActiveDocument, until a document is open). COM/ROT is reachable from cold start — no saved drawing required. Use requireActiveDocument=false to gate only on AcadApplication readiness (no document needed). For plugin pipe readiness, prefer acad_wait_pipe.")]
    public static async Task<AcadWaitResult> WaitQuiescent(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0,
        [Description("Max wait in seconds. Default 300 — Civil 3D can take several minutes on a cold cache.")] int timeoutSeconds = 300,
        [Description("Require an active document before returning ready. Default true; most plugins fail without one.")] bool requireActiveDocument = true,
        CancellationToken ct = default)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        return await BridgeServices.Controller.WaitForReadyAsync(
            resolved, timeout, requireActiveDocument, ct).ConfigureAwait(false);
    }

    [AcadRpcTool, Description("Quit AutoCAD. Every open document is closed with AcadDocument.Close(SaveChanges) first, then AcadApplication.Quit() is invoked — so the save-changes prompt cannot block the shutdown. Falls back to Process.Kill after timeoutSeconds if COM Quit() does not return (e.g. unrelated modal dialog). Auto-detaches the bridge on success.")]
    public static AcadQuitResult Quit(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0,
        [Description("Save unsaved drawings? Default false — discards all unsaved changes silently.")] bool saveChanges = false,
        [Description("Max seconds to wait for COM Quit before killing. Default 10.")] int timeoutSeconds = 10)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        if (!BridgeServices.Controller.IsRunning(resolved))
            return new AcadQuitResult(AcadQuitOutcome.NotRunning, resolved, $"pid {resolved} is not running");

        // Run the COM Quit on a worker task so a hang (unrelated modal
        // dialog, unresponsive server) doesn't block the tool — we ALWAYS
        // reach the kill fallback within timeoutSeconds.
        int budget = Math.Max(1, timeoutSeconds);
        var quitTask = Task.Run(() =>
        {
            try
            {
                using var client = AcadComClient.AttachByPid(resolved);
                if (client == null) return;
                client.CloseAllDocuments(saveChanges);
                client.Quit();
            }
            catch { /* COM unreachable or threw — kill path will catch */ }
        });
        bool comReturned = quitTask.Wait(TimeSpan.FromSeconds(budget));

        // Either the COM call returned, or we've burned the budget.
        // Process exit is itself asynchronous after Quit() — give it
        // a brief chance to fall out, then kill the pid if it lingers.
        var procDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < procDeadline)
        {
            if (!BridgeServices.Controller.IsRunning(resolved))
            {
                MaybeDetach(resolved);
                return new AcadQuitResult(AcadQuitOutcome.Graceful, resolved,
                    comReturned ? null : "COM Quit() did not return but process exited anyway");
            }
            Thread.Sleep(100);
        }

        bool killed = BridgeServices.Controller.Kill(resolved);
        MaybeDetach(resolved);
        return new AcadQuitResult(
            killed ? AcadQuitOutcome.Killed : AcadQuitOutcome.NotRunning,
            resolved,
            comReturned
                ? "graceful Quit() returned but process did not exit; killed"
                : "graceful Quit() timed out; killed");
    }

    // ── State query ───────────────────────────────────────────────────

    [AcadRpcTool, Description("One-call snapshot of the target instance: IsQuiescent, HasActiveDocument, ActiveDocumentName, ProductName, Visible. Returns null state if COM isn't reachable (modal dialog, still starting).")]
    public static AcadProcessState GetState(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = AcadComClient.AttachByPid(resolved)
            ?? throw new InvalidOperationException(
                $"COM unreachable for pid {resolved}. Process may still be starting, or a modal dialog is blocking COM.");
        return client.GetState();
    }

    // ── Commands ──────────────────────────────────────────────────────

    [AcadRpcTool, Description("Send an AutoCAD command. BLOCKING — returns when the command has executed. Use when you need to know the command finished before the next call.")]
    public static string SendCommand(
        [Description("Command string including terminators (e.g. \"_.LINE\\n0,0\\n10,10\\n\\n\").")] string commandString,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.SendCommand(commandString);
        return "ok";
    }

    [AcadRpcTool, Description("Queue an AutoCAD command. NON-BLOCKING — returns immediately; AutoCAD executes on its next pump. Pair with acad_wait_quiescent if you need to observe the result.")]
    public static string PostCommand(
        [Description("Command string including terminators.")] string commandString,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.PostCommand(commandString);
        return "queued";
    }

    // ── Documents ─────────────────────────────────────────────────────

    [AcadRpcTool, Description("Open a drawing file in the bound AutoCAD. Necessary after acad_start because the default startup mode (Start screen) leaves no active document.")]
    public static string OpenDrawing(
        [Description("Absolute path to a .dwg/.dwt/.dws file.")] string path,
        [Description("Open read-only? Default false.")] bool readOnly = false,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.OpenDocument(path, readOnly);
        return "opened";
    }

    [AcadRpcTool, Description("Create a new empty drawing in the bound AutoCAD. Optional template path; null uses the AutoCAD default.")]
    public static string NewDrawing(
        [Description("Optional template path (.dwt).")] string? templatePath = null,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.NewDocument(templatePath);
        return "created";
    }

    [AcadRpcTool, Description("Close the active drawing. Use saveChanges=false for transient test drawings.")]
    public static string CloseActiveDrawing(
        [Description("Save unsaved changes before closing? Default false.")] bool saveChanges = false,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.CloseActiveDocument(saveChanges);
        return "closed";
    }

    [AcadRpcTool, Description("List every open drawing in the bound AutoCAD, with its unique identifier. 'fullName' is the path on disk and is what you pass to acad_activate_document; a never-saved drawing has an empty fullName and is identified by 'name' instead. 'isActive' marks the current document; 'saved' is false when it has unsaved changes.")]
    public static IReadOnlyList<AcadDocumentInfo> ListOpenDocuments(
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        return client.ListDocuments();
    }

    [AcadRpcTool, Description("Switch the active document to an already-open drawing. Identify it by the 'fullName' (full path) from acad_list_open_documents; for a never-saved drawing with no path, pass its 'name'. Errors if no open document matches or if the identifier is ambiguous.")]
    public static string ActivateDocument(
        [Description("The drawing's fullName (full path), or its name if it has never been saved.")] string documentId,
        [Description("Pid of the target instance. Omit to use the bound pid.")] int pid = 0)
    {
        int resolved = BridgeServices.Binding.ResolvePid(pid > 0 ? pid : null);
        using var client = RequireClient(resolved);
        client.ActivateDocument(documentId);
        return "activated";
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static AcadComClient RequireClient(int pid)
    {
        return AcadComClient.AttachByPid(pid)
            ?? throw new InvalidOperationException(
                $"COM unreachable for pid {pid}. Process may still be starting, or a modal dialog is blocking COM.");
    }

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
    bool ComAvailable,
    bool IsBound);

public sealed record AcadStartResult(
    int Pid,
    string ProductName,
    string PipeName,
    string ExePath);

public sealed record DetachResult(
    bool WasBound,
    int PreviousPid);
