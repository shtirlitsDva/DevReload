using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// Convenience surface for tool method authors. <c>AcadRpc.OnMainThread(...)</c>
/// marshals a delegate onto AutoCAD's main thread via the configured
/// dispatcher. Tool methods that touch AutoCAD APIs wrap their work in
/// this call.
/// </summary>
/// <example>
/// <code>
/// [McpServerTool]
/// public static Task&lt;string&gt; GetActiveDocName(CancellationToken ct)
///     =&gt; AcadRpc.OnMainThread(() =&gt;
///        Application.DocumentManager.MdiActiveDocument?.Name ?? "", ct);
/// </code>
/// </example>
public static class AcadRpc
{
    public static Task<T> OnMainThread<T>(Func<T> work, CancellationToken ct = default)
        => AcadRpcHost.Current.Dispatcher.InvokeAsync(work, ct);

    public static Task OnMainThread(Action work, CancellationToken ct = default)
        => AcadRpcHost.Current.Dispatcher.InvokeAsync(work, ct);
}
