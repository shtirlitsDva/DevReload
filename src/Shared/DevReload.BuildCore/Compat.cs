// Compiler shims so the shared sources build identically on net48 and net8.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    // Required by the C# compiler for records / init-only setters on
    // pre-.NET5 target frameworks. Internal on purpose: each consuming
    // assembly gets its own copy.
    internal static class IsExternalInit { }
}
#endif
