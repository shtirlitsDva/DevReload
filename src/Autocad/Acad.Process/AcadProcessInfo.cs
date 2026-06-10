namespace Acad.Process;

/// <summary>
/// Snapshot of one running AutoCAD process discovered at the OS level.
/// <see cref="PipeName"/> is the conventional in-AutoCAD RPC pipe name
/// for this pid; <see cref="PipeAvailable"/> reflects whether such a
/// pipe currently exists in the local namespace (i.e. DevReload is
/// loaded and the host is running).
/// </summary>
public sealed record AcadProcessInfo(
    int Pid,
    string ProcessName,
    string MainWindowTitle,
    string PipeName,
    bool PipeAvailable);

/// <summary>
/// Live state for one bound AutoCAD instance. <see cref="HasActiveDocument"/>
/// + <see cref="IsQuiescent"/> together = "ready to drive".
/// </summary>
public sealed record AcadProcessState(
    int Pid,
    bool IsQuiescent,
    bool HasActiveDocument,
    string ActiveDocumentName,
    string ProductName,
    bool Visible);

/// <summary>
/// One open drawing in a bound AutoCAD instance. <see cref="FullName"/> is
/// the path on disk and is the unique identifier you pass to
/// acad_activate_document; it is empty for a never-saved drawing, which is
/// instead unique by <see cref="Name"/>. <see cref="IsActive"/> marks the
/// current ActiveDocument; <see cref="Saved"/> is false when the drawing has
/// unsaved changes.
/// </summary>
public sealed record AcadDocumentInfo(
    string Name,
    string FullName,
    bool IsActive,
    bool Saved);

public enum AcadQuitOutcome
{
    Graceful,
    Killed,
    NotRunning,
}

public sealed record AcadQuitResult(
    AcadQuitOutcome Outcome,
    int Pid,
    string? Message);

public sealed record AcadWaitResult(
    bool Succeeded,
    double ElapsedSeconds,
    AcadProcessState? LastState,
    string? Reason);
