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
    private static PipeForwarder? _pipeForwarder;

    public static AcadProcessController Controller => _controller ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static AcadInstanceBinding Binding => _binding ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static PipeForwarder PipeForwarder => _pipeForwarder ??
        throw new InvalidOperationException("BridgeServices not initialized.");

    public static void Initialize(
        AcadProcessController controller,
        AcadInstanceBinding binding,
        PipeForwarder pipeForwarder)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _pipeForwarder = pipeForwarder ?? throw new ArgumentNullException(nameof(pipeForwarder));
    }
}
