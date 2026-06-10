using System;
using System.Threading;
using System.Threading.Tasks;

using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// <see cref="IAcadMainThreadDispatcher"/> for hosts that have no
/// dedicated "main" thread. Tools tagged <c>[RunOnAcadMainThread]</c>
/// inside the bridge would be a category error — the bridge is
/// outside AutoCAD — so we don't expect any dispatch traffic. If a
/// tool does ask, we run the work inline on the caller's thread and
/// move on. RpcCore requires a non-null dispatcher; this is the
/// minimal honest implementation.
/// </summary>
internal sealed class InlineDispatcher : IAcadMainThreadDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
        => Task.FromResult(work());

    public Task InvokeAsync(Action work, CancellationToken ct)
    {
        work();
        return Task.CompletedTask;
    }
}
