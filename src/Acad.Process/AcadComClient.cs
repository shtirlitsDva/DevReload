using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Acad.Process;

internal static class AcadComClsid
{
    private static Guid? _cached;

    /// <summary>
    /// The CLSID AutoCAD/Civil 3D/verticals all register under in the
    /// ROT. Resolved once via <c>CLSIDFromProgID("AutoCAD.Application")</c>
    /// — the version-agnostic ProgID returns the CLSID of whatever
    /// AutoCAD-family product is the latest install on the machine.
    /// Moniker display names look like <c>"!{CLSID-in-braces}"</c>.
    /// </summary>
    public static Guid GetOrResolve()
    {
        if (_cached.HasValue) return _cached.Value;
        int hr = Win32Interop.CLSIDFromProgID("AutoCAD.Application", out Guid clsid);
        if (hr != 0) throw new COMException(
            "CLSIDFromProgID(\"AutoCAD.Application\") failed — AutoCAD is not installed.", hr);
        _cached = clsid;
        return clsid;
    }
}

/// <summary>
/// Late-bound COM wrapper for an AutoCAD/Civil 3D process. Attaches
/// via the Running Object Table, correlates to a pid through the
/// application HWND, then exposes the small set of operations the agent
/// needs at the process level (state, commands, documents, quit).
///
/// One client = one bound AutoCAD instance. Dispose releases the COM
/// reference. The wrapped object is dynamic — every operation goes
/// through IDispatch, so version differences across AutoCAD/C3D builds
/// are accommodated without a PIA dependency.
/// </summary>
public sealed class AcadComClient : IDisposable
{
    private dynamic? _app;

    public int Pid { get; }
    public string ProductName { get; }
    public bool IsDisposed => _app == null;

    private AcadComClient(dynamic app, int pid, string productName)
    {
        _app = app;
        Pid = pid;
        ProductName = productName;
    }

    /// <summary>
    /// Find the AutoCAD COM object whose application HWND belongs to the
    /// requested pid. Returns null when no such instance is registered
    /// in the ROT — usually means the process isn't fully up yet, or
    /// isn't AutoCAD at all.
    /// </summary>
    public static AcadComClient? AttachByPid(int pid)
    {
        foreach (var (name, obj) in RotEnumerator.Enumerate())
        {
            if (!IsAutoCadMoniker(name))
            {
                TryRelease(obj);
                continue;
            }

            dynamic app = obj;
            try
            {
                int hwnd = (int)app.HWND;
                Win32Interop.GetWindowThreadProcessId(new IntPtr(hwnd), out uint comPid);
                if ((int)comPid != pid)
                {
                    TryRelease(obj);
                    continue;
                }
                string productName = TryReadString(() => (string)app.Name) ?? "AutoCAD";
                return new AcadComClient(app, pid, productName);
            }
            catch
            {
                TryRelease(obj);
            }
        }
        return null;
    }

    /// <summary>List every AutoCAD-family COM application currently in
    /// the ROT, correlated with its OS pid.</summary>
    public static IReadOnlyList<AcadProcessInfo> EnumerateInstances()
    {
        var result = new List<AcadProcessInfo>();
        foreach (var (name, obj) in RotEnumerator.Enumerate())
        {
            if (!IsAutoCadMoniker(name)) { TryRelease(obj); continue; }
            dynamic app = obj;
            try
            {
                int hwnd = (int)app.HWND;
                Win32Interop.GetWindowThreadProcessId(new IntPtr(hwnd), out uint pid);
                string productName = TryReadString(() => (string)app.Name) ?? "AutoCAD";
                string caption = TryReadString(() => (string)app.Caption) ?? productName;
                var pipeName = "acad-rpc-" + (int)pid;
                bool pipeAvailable = NamedPipeProbe.Exists(pipeName);
                result.Add(new AcadProcessInfo(
                    Pid: (int)pid,
                    ProcessName: productName,
                    MainWindowTitle: caption,
                    PipeName: pipeName,
                    PipeAvailable: pipeAvailable));
            }
            catch
            {
                // Skip unhealthy objects silently — we want a best-effort listing.
            }
            finally
            {
                TryRelease(obj);
            }
        }
        return result;
    }

    // ── Read-side operations ──────────────────────────────────────────

    public AcadProcessState GetState()
    {
        EnsureNotDisposed();
        bool quiescent = false;
        bool hasDoc = false;
        string docName = string.Empty;
        bool visible = false;

        try
        {
            dynamic state = _app!.GetAcadState();
            quiescent = (bool)state.IsQuiescent;
        }
        catch { /* COM busy / no state */ }

        try
        {
            dynamic? doc = _app!.ActiveDocument;
            if (doc != null)
            {
                hasDoc = true;
                docName = TryReadString(() => (string)doc.Name) ?? string.Empty;
            }
        }
        catch { /* no active doc */ }

        try { visible = (bool)_app!.Visible; } catch { /* property not exposed */ }

        return new AcadProcessState(Pid, quiescent, hasDoc, docName, ProductName, visible);
    }

    // ── Command issue ─────────────────────────────────────────────────

    /// <summary>Blocking — returns when the command has executed.</summary>
    public void SendCommand(string commandString)
    {
        EnsureNotDisposed();
        EnsureCommandText(commandString);
        _app!.ActiveDocument.SendCommand(commandString);
    }

    /// <summary>Non-blocking — queues the command and returns
    /// immediately. The string must end with a terminator (newline or
    /// space) for AutoCAD to dispatch it.</summary>
    public void PostCommand(string commandString)
    {
        EnsureNotDisposed();
        EnsureCommandText(commandString);
        _app!.ActiveDocument.PostCommand(commandString);
    }

    // ── Documents ─────────────────────────────────────────────────────

    public void OpenDocument(string path, bool readOnly = false)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is required", nameof(path));
        _app!.Documents.Open(path, readOnly);
    }

    public void NewDocument(string? templatePath)
    {
        EnsureNotDisposed();
        if (string.IsNullOrEmpty(templatePath)) _app!.Documents.Add();
        else _app!.Documents.Add(templatePath);
    }

    public void CloseActiveDocument(bool saveChanges)
    {
        EnsureNotDisposed();
        // AcadDocument.Close([SaveChanges]) handles the save flag
        // natively — no need to call Save() separately.
        _app!.ActiveDocument.Close(saveChanges);
    }

    /// <summary>
    /// Close every open document, propagating <paramref name="saveChanges"/>
    /// to each one's <c>AcadDocument.Close([SaveChanges], [FileName])</c>
    /// call. <c>SaveChanges = false</c> discards unsaved changes
    /// silently — no save-as dialog. Per the AutoCAD ActiveX reference,
    /// this is the parameter <c>AcadDocument.Close</c> exists FOR; the
    /// older trick of "set <c>doc.Saved = true</c> manually" is a
    /// workaround for code that didn't realize the overload existed.
    /// </summary>
    public void CloseAllDocuments(bool saveChanges)
    {
        EnsureNotDisposed();
        dynamic docs;
        try { docs = _app!.Documents; }
        catch { return; }

        // Snapshot to a list — Close mutates the live collection.
        var toClose = new List<dynamic>();
        try
        {
            int count = (int)docs.Count;
            for (int i = 0; i < count; i++)
            {
                try { toClose.Add(docs.Item(i)); }
                catch { /* skip transient indexer failure */ }
            }
        }
        catch { return; }

        foreach (var doc in toClose)
        {
            try { doc.Close(saveChanges); }
            catch { /* a single problematic doc shouldn't stop the rest */ }
        }
    }

    // ── Quit ──────────────────────────────────────────────────────────

    public void Quit()
    {
        EnsureNotDisposed();
        _app!.Quit();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_app == null) return;
        try { Marshal.FinalReleaseComObject(_app); } catch { /* already gone */ }
        _app = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void EnsureNotDisposed()
    {
        if (_app == null)
            throw new ObjectDisposedException(nameof(AcadComClient));
    }

    private static void EnsureCommandText(string commandString)
    {
        if (commandString == null) throw new ArgumentNullException(nameof(commandString));
        if (commandString.Length == 0) throw new ArgumentException(
            "commandString must be non-empty", nameof(commandString));
    }

    private static bool IsAutoCadMoniker(string displayName)
    {
        // AutoCAD/Civil 3D register in the ROT under their CLSID — the
        // moniker display name looks like "!{CLSID-in-braces}". The old
        // assumption that the moniker carried "AutoCAD.Application" was
        // wrong; that's the ProgID, not the moniker. Verified by direct
        // ROT enumeration against Civil 3D 2025.
        Guid clsid;
        try { clsid = AcadComClsid.GetOrResolve(); } catch { return false; }
        var needle = "{" + clsid.ToString().ToUpperInvariant() + "}";
        return displayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void TryRelease(object o)
    {
        try { Marshal.ReleaseComObject(o); } catch { /* not COM, or already gone */ }
    }

    private static string? TryReadString(Func<string> get)
    {
        try { return get(); } catch { return null; }
    }

}
