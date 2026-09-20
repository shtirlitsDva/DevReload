using System;
using System.Collections.Generic;

namespace Acad.Rpc.Core;

/// <summary>
/// Construction-time options for <see cref="AcadRpcHost"/>. The pipe
/// name carries the AutoCAD PID by convention so multiple AutoCAD
/// instances coexist trivially.
/// </summary>
/// <param name="PipeName">Named-pipe identifier; lives at <c>\\.\pipe\{PipeName}</c>.</param>
/// <param name="MainThreadDispatcher">Dispatcher used by methods marked
///   <see cref="RunOnAcadMainThreadAttribute"/>.</param>
/// <param name="Log">Optional log sink. The host has no AutoCAD or
///   filesystem dependency, so DevReload supplies a callback that pipes
///   into its own log file. Null = silent.</param>
/// <param name="StateChecks">The host's implementation of the state keys its
///   tools declare with <see cref="RpcRequiresAttribute"/>. The engine knows
///   the keys; the host knows what they mean. A tool declaring a key this
///   host has no entry for is a wiring bug and fails loudly.</param>
public sealed record AcadRpcHostOptions(
    string PipeName,
    IAcadMainThreadDispatcher MainThreadDispatcher,
    Action<string>? Log = null,
    IReadOnlyDictionary<string, RpcStateCheck>? StateChecks = null);
