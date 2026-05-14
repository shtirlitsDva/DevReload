using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using SysProcessWindowStyle = System.Diagnostics.ProcessWindowStyle;

namespace Acad.Process;

/// <summary>
/// OS-level (no COM) operations on AutoCAD processes: launch,
/// enumerate, terminate, idle-wait. The COM-side companion is
/// <see cref="AcadComClient"/>. This class does not require AutoCAD to
/// be reachable via COM — it stays useful while AutoCAD is still
/// starting up, while a modal dialog is blocking COM, or after a
/// crash.
/// </summary>
public sealed class AcadProcessController
{
    private readonly IReadOnlyList<AcadInstall> _installs;

    public AcadProcessController()
        : this(AcadInstallRegistry.Discover()) { }

    public AcadProcessController(IReadOnlyList<AcadInstall> installs)
    {
        _installs = installs ?? throw new ArgumentNullException(nameof(installs));
    }

    public IReadOnlyList<AcadInstall> Installs => _installs;

    /// <summary>
    /// Launch a new AutoCAD process. The flavor's product code resolves
    /// to the right install + <c>/product</c> argument. <paramref name="drawingPath"/>
    /// is appended last so AutoCAD opens it on startup; <paramref name="profile"/>
    /// becomes <c>/p &lt;name&gt;</c>. The returned <see cref="System.Diagnostics.Process"/>
    /// reflects the launched process — callers usually keep only its
    /// pid and discard the handle.
    /// </summary>
    public SysProcess Launch(AcadLaunchOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var install = options.Install ??
            AcadInstallRegistry.PickDefault(options.Flavor, _installs) ??
            throw new InvalidOperationException(
                $"No installed AutoCAD/{options.Flavor} found. Pass an explicit install or installPath.");

        if (!File.Exists(install.ExePath))
            throw new FileNotFoundException(
                $"acad.exe not found at the discovered location: {install.ExePath}");

        var psi = new SysProcessStartInfo
        {
            FileName = install.ExePath,
            UseShellExecute = false,
            WorkingDirectory = install.InstallPath,
            WindowStyle = options.Visible
                ? SysProcessWindowStyle.Normal
                : SysProcessWindowStyle.Minimized,
            // Redirect AutoCAD's stdio away from our own stdio. The
            // bridge (likely caller) uses stdout to speak JSON-RPC over
            // the MCP wire — if AutoCAD inherits those handles, its
            // native diagnostic prints ("m_kernelList still has N
            // entries." etc., emitted at shutdown by AcCm or similar)
            // land in the agent's tool-call response stream and break
            // JSON parsing. Discarding both streams immediately on the
            // child side is the right fix.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrEmpty(install.ProductCmdLineArg))
            AppendArg(psi, install.ProductCmdLineArg);
        AppendArg(psi, "/nologo");
        if (!string.IsNullOrEmpty(options.Profile))
            AppendArg(psi, $"/p \"{options.Profile}\"");

        // Argument order matters: per AutoCAD CLI, the optional drawing
        // path must be FIRST positional, the optional /b script LAST.
        if (!string.IsNullOrEmpty(options.DrawingPath))
        {
            // Saved drawing path is the cleanest startup — AutoCAD opens
            // it, bypasses the Start tab, registers in the ROT (because
            // the doc has a real on-disk path), and COM becomes reachable
            // for the agent's process-control tools.
            AppendArg(psi, $"\"{options.DrawingPath}\"");
        }

        if (!string.IsNullOrEmpty(options.StartupCommands))
        {
            // Pass commands AutoCAD executes after startup. Common use:
            // NETLOAD-ing DevReload from a known bundle path so the
            // plugin's RPC pipe comes up without manual intervention.
            // AutoCAD must have a document open to run a script — it
            // creates Drawing1.dwg automatically before running the .scr,
            // even when no drawingPath is supplied.
            var scriptPath = WriteStartupScript(options.StartupCommands);
            AppendArg(psi, $"/b \"{scriptPath}\"");
        }

        // Drain handlers — empty bodies, just to prevent the redirected
        // pipe buffer from filling (otherwise AutoCAD's prints
        // eventually block its main thread).
        var proc = new SysProcess { StartInfo = psi };
        proc.OutputDataReceived += static (_, _) => { };
        proc.ErrorDataReceived += static (_, _) => { };
        if (!proc.Start())
            throw new InvalidOperationException("Process.Start returned false");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static string WriteStartupScript(string commands)
    {
        var path = Path.Combine(Path.GetTempPath(), $"acad-startup-{Guid.NewGuid():N}.scr");
        // AutoCAD .scr scripts are ASCII / CRLF and must end with a
        // newline so the last command actually fires.
        var content = commands.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!content.EndsWith("\r\n", StringComparison.Ordinal)) content += "\r\n";
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            try { File.Delete(path); } catch { /* tempdir cleanup will reap eventually */ }
        });
        return path;
    }

    /// <summary>
    /// Poll the named-pipe namespace for the given pipe name. Returns
    /// elapsed time + whether the pipe appeared. This is the **primary**
    /// "AutoCAD plugin is ready" signal — independent of COM/ROT, which
    /// only register saved drawings.
    /// </summary>
    public async Task<AcadWaitResult> WaitForPipeAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("pipeName is required", nameof(pipeName));

        var deadline = DateTime.UtcNow + timeout;
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (NamedPipeProbe.Exists(pipeName))
                return new AcadWaitResult(
                    Succeeded: true,
                    ElapsedSeconds: (DateTime.UtcNow - start).TotalSeconds,
                    LastState: null,
                    Reason: null);
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
        return new AcadWaitResult(
            Succeeded: false,
            ElapsedSeconds: (DateTime.UtcNow - start).TotalSeconds,
            LastState: null,
            Reason: $"timeout waiting for pipe '{pipeName}' to appear");
    }

    /// <summary>Enumerate AutoCAD-family processes by exe name (no COM
    /// dependency). Use this when COM isn't yet up.</summary>
    public IReadOnlyList<AcadProcessInfo> EnumerateProcesses()
    {
        var result = new List<AcadProcessInfo>();
        SysProcess[] procs;
        try { procs = SysProcess.GetProcessesByName("acad"); }
        catch { return result; }

        foreach (var p in procs)
        {
            try
            {
                var pipeName = "acad-rpc-" + p.Id;
                result.Add(new AcadProcessInfo(
                    Pid: p.Id,
                    ProcessName: SafeMainModuleName(p),
                    MainWindowTitle: p.MainWindowTitle ?? string.Empty,
                    PipeName: pipeName,
                    PipeAvailable: NamedPipeProbe.Exists(pipeName)));
            }
            finally { p.Dispose(); }
        }
        return result;
    }

    /// <summary>True iff a process with the given pid exists AND is an
    /// acad.exe. We don't trust raw pid existence because pids recycle.</summary>
    public bool IsRunning(int pid)
    {
        try
        {
            using var p = SysProcess.GetProcessById(pid);
            return string.Equals(
                SafeProcessName(p), "acad", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Hard kill — last-resort when COM Quit doesn't return in
    /// time. Idempotent: returns true on first call, false if the pid is
    /// already gone.</summary>
    public bool Kill(int pid)
    {
        // GetProcessById throws ArgumentException when pid is gone;
        // Kill/WaitForExit throw InvalidOperationException if the
        // handle was reaped between calls. Both mean the same thing
        // (the pid is no longer ours to kill), so collapse both.
        try
        {
            using var p = SysProcess.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Poll a target instance's COM state until <see cref="AcadProcessState.IsQuiescent"/>
    /// (and, when <paramref name="requireActiveDocument"/> is true,
    /// <see cref="AcadProcessState.HasActiveDocument"/>) become true.
    /// Returns the elapsed time and last observed state. COM attach
    /// failures count as "still warming up" until the timeout fires.
    /// </summary>
    public async Task<AcadWaitResult> WaitForReadyAsync(
        int pid,
        TimeSpan timeout,
        bool requireActiveDocument,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var start = DateTime.UtcNow;
        AcadProcessState? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsRunning(pid))
                return new AcadWaitResult(
                    Succeeded: false,
                    ElapsedSeconds: (DateTime.UtcNow - start).TotalSeconds,
                    LastState: last,
                    Reason: $"process {pid} exited before reaching ready state");

            using var client = AcadComClient.AttachByPid(pid);
            if (client != null)
            {
                last = client.GetState();
                if (last.IsQuiescent && (!requireActiveDocument || last.HasActiveDocument))
                    return new AcadWaitResult(
                        Succeeded: true,
                        ElapsedSeconds: (DateTime.UtcNow - start).TotalSeconds,
                        LastState: last,
                        Reason: null);
            }

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }

        return new AcadWaitResult(
            Succeeded: false,
            ElapsedSeconds: (DateTime.UtcNow - start).TotalSeconds,
            LastState: last,
            Reason: requireActiveDocument
                ? "timeout waiting for IsQuiescent AND HasActiveDocument"
                : "timeout waiting for IsQuiescent");
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static void AppendArg(SysProcessStartInfo psi, string arg)
    {
        if (psi.Arguments.Length == 0) psi.Arguments = arg;
        else psi.Arguments = psi.Arguments + " " + arg;
    }

    private static string SafeMainModuleName(SysProcess p)
    {
        try { return p.MainModule?.ModuleName ?? "acad.exe"; }
        catch { return "acad.exe"; }
    }

    private static string SafeProcessName(SysProcess p)
    {
        try { return p.ProcessName; }
        catch { return string.Empty; }
    }

}

/// <summary>Launch parameters. <see cref="Install"/> overrides flavor
/// discovery when caller knows exactly which install to use.
/// <see cref="StartupCommands"/> is a multi-line AutoCAD-script string
/// (e.g. <c>FILEDIA\n0\nNETLOAD\n&lt;path&gt;\nFILEDIA\n1\n</c>); when
/// set, the launcher writes it to a temp .scr and passes <c>/b</c>.</summary>
public sealed record AcadLaunchOptions(
    AcadFlavor Flavor = AcadFlavor.Civil3D,
    AcadInstall? Install = null,
    string? Profile = null,
    string? DrawingPath = null,
    string? StartupCommands = null,
    bool Visible = true);
