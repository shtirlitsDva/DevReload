namespace Acad.Rpc.Core;

/// <summary>
/// Construction-time options for <see cref="AcadRpcHost"/>. The pipe
/// name carries the AutoCAD PID by convention so multiple AutoCAD
/// instances coexist trivially.
/// </summary>
public sealed record AcadRpcHostOptions(
    string PipeName,
    IAcadMainThreadDispatcher MainThreadDispatcher);
