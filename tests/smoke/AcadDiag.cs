// Tiny diagnostic harness: probes every known path for connecting to a
// running AutoCAD via COM. Helps localize why ROT enumeration returns
// nothing for a freshly-launched Civil 3D.
//
// Usage:
//   dotnet run --project tests/smoke/AcadDiag.csproj -- <pid>

using System;
using System.Runtime.InteropServices;
using Acad.Process;

if (args.Length < 1 || !int.TryParse(args[0], out int pid))
{
    Console.Error.WriteLine("usage: AcadDiag <pid>");
    return 2;
}

Console.WriteLine($"=== Installs ===");
foreach (var i in AcadInstallRegistry.Discover())
{
    Console.WriteLine($"  {i.Flavor} \"{i.ProductName}\" ({i.ReleaseKey}) -> {i.ExePath}");
}

Console.WriteLine($"\n=== ALL ROT entries (unfiltered) ===");
var rotType = typeof(AcadInstall).Assembly.GetType("Acad.Process.RotEnumerator")!;
var enumMethod = rotType.GetMethod("Enumerate",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
var rotEntries = (System.Collections.IEnumerable)enumMethod.Invoke(null, null)!;
int total = 0;
foreach (var entry in rotEntries)
{
    total++;
    var nameField = entry.GetType().GetField("Item1");
    string name = (string)(nameField?.GetValue(entry) ?? entry.ToString() ?? "?");
    Console.WriteLine($"  {name}");
}
Console.WriteLine($"  ({total} ROT entries total)");

Console.WriteLine($"\n=== GetActiveObject by ProgID (the canonical pattern) ===");
foreach (var progId in new[] { "AutoCAD.Application.25", "AutoCAD.Application.25.1", "AutoCAD.Application" })
{
    Console.WriteLine($"  ProgID '{progId}':");
    try
    {
        var clsid = ClsidFromProgId(progId);
        Console.WriteLine($"    CLSID resolved: {clsid:B}");
        var obj = GetActiveObjectByClsid(clsid);
        if (obj == null) { Console.WriteLine("    GetActiveObject returned null"); continue; }
        Console.WriteLine($"    GetActiveObject success: {obj.GetType().FullName}");
        try
        {
            dynamic app = obj;
            Console.WriteLine($"    .Name = '{app.Name}'");
            try { Console.WriteLine($"    .FullName = '{app.FullName}'"); } catch (Exception ex) { Console.WriteLine($"    .FullName threw {ex.Message}"); }
            try { Console.WriteLine($"    .Version = '{app.Version}'"); } catch (Exception ex) { Console.WriteLine($"    .Version threw {ex.Message}"); }
            try { int hwnd = (int)app.HWND; Console.WriteLine($"    .HWND = {hwnd}"); } catch (Exception ex) { Console.WriteLine($"    .HWND threw {ex.Message}"); }
            try
            {
                dynamic state = app.GetAcadState();
                Console.WriteLine($"    .GetAcadState().IsQuiescent = {(bool)state.IsQuiescent}");
            }
            catch (Exception ex) { Console.WriteLine($"    GetAcadState threw {ex.Message}"); }
            try
            {
                dynamic doc = app.ActiveDocument;
                Console.WriteLine($"    .ActiveDocument != null: {doc != null}, .Name = '{(doc != null ? (string)doc.Name : "<null>")}'");
            }
            catch (Exception ex) { Console.WriteLine($"    .ActiveDocument threw {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Late-bound property read failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    FAILED: {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"\n=== AttachByPid({pid}) via fixed RotEnumerator ===");
using var client = AcadComClient.AttachByPid(pid);
if (client == null)
{
    Console.WriteLine($"  AttachByPid returned null — moniker filter still doesn't match.");
    return 1;
}
Console.WriteLine($"  Attached: ProductName='{client.ProductName}'");
var s = client.GetState();
Console.WriteLine($"  GetState: quiescent={s.IsQuiescent}  hasDoc={s.HasActiveDocument}  doc='{s.ActiveDocumentName}'  visible={s.Visible}");

Console.WriteLine($"\n=== EnumerateInstances() — the bridge's acad_list_instances backing call ===");
foreach (var inst in AcadComClient.EnumerateInstances())
{
    Console.WriteLine($"  pid={inst.Pid}  '{inst.ProcessName}' window='{inst.MainWindowTitle}' pipe={(inst.PipeAvailable ? "yes" : "no")}");
}

return 0;

static Guid ClsidFromProgId(string progId)
{
    int hr = CLSIDFromProgID(progId, out Guid clsid);
    if (hr != 0) throw new COMException($"CLSIDFromProgID('{progId}') failed", hr);
    return clsid;
}

static object? GetActiveObjectByClsid(Guid clsid)
{
    int hr = GetActiveObject(ref clsid, IntPtr.Zero, out object? obj);
    if (hr == 0) return obj;
    if ((uint)hr == 0x800401E3) return null;  // MK_E_UNAVAILABLE = not running
    throw new COMException($"GetActiveObject({clsid:B}) HR=0x{hr:X8}", hr);
}

[DllImport("ole32.dll")]
static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

[DllImport("oleaut32.dll", PreserveSig = true)]
static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved,
    [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
