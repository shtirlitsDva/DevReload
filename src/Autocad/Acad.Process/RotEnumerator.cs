using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Acad.Process;

/// <summary>
/// Enumerates entries in the COM Running Object Table. Each yielded
/// entry is (moniker display name, live object). Callers are
/// responsible for releasing objects they keep references to.
/// </summary>
internal static class RotEnumerator
{
    public static IEnumerable<(string Name, object Object)> Enumerate()
    {
        Win32Interop.GetRunningObjectTable(0, out IRunningObjectTable rot);
        rot.EnumRunning(out IEnumMoniker enumerator);
        enumerator.Reset();

        var monikers = new IMoniker[1];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
        {
            Win32Interop.CreateBindCtx(0, out IBindCtx ctx);
            try
            {
                monikers[0].GetDisplayName(ctx, null, out string displayName);
                if (!seen.Add(displayName)) continue;

                object value;
                try { rot.GetObject(monikers[0], out value); }
                catch { continue; }

                yield return (displayName, value);
            }
            finally
            {
                Marshal.ReleaseComObject(ctx);
                Marshal.ReleaseComObject(monikers[0]);
            }
        }
    }
}
