using System;
using System.Runtime.InteropServices;

namespace Acad.Process;

/// <summary>
/// Tiny Win32 surface for COM ROT enumeration and HWND-to-PID
/// correlation. We do not depend on PIAs; AutoCAD COM is consumed
/// late-bound through <see cref="System.Object"/> (dynamic) on the
/// caller side. P/Invokes here are the unmanaged primitives those calls
/// need.
/// </summary>
internal static class Win32Interop
{
    [DllImport("ole32.dll")]
    internal static extern uint GetRunningObjectTable(uint reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    internal static extern uint CreateBindCtx(uint reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ctx);

    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
