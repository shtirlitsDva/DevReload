using System;

using Acad.Process;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Process-wide service locator for the bridge. The <c>[AcadRpcSurface]</c>
/// tool methods are static, so they reach their collaborators
/// (controller, binding, forwarder) through this single anchor.
/// Set once at <see cref="Program.Main"/> startup; never reassigned.
/// </summary>
internal static class BridgeServices
{
    private static AcadProcessController? _controller;
    private static AcadInstanceBinding? _binding;
    private static ForwarderPool? _pool;

    public static AcadProcessController Controller => _controller ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static AcadInstanceBinding Binding => _binding ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static ForwarderPool Pool => _pool ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static void Initialize(
        AcadProcessController controller,
        AcadInstanceBinding binding,
        ForwarderPool pool)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }
}
