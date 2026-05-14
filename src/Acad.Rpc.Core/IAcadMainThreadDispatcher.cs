using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// Marshals a delegate onto AutoCAD's main thread. The AutoCAD-bound
/// implementation lives in DevReload (Application.Idle pump); a fake is
/// provided for unit tests in Acad.Rpc.Core. This keeps Acad.Rpc.Core
/// free of any AutoCAD reference and fully unit-testable.
/// </summary>
public interface IAcadMainThreadDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct);
    Task InvokeAsync(Action work, CancellationToken ct);
}
