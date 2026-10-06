using System;
using System.Collections.Generic;
using System.Linq;

namespace DevReload.Oarx.Payload
{
    /// <summary>
    /// The host's dynamic linker as the unload needs it. The production
    /// implementation sits in <c>OarxModuleHost</c>; tests give a fake.
    /// </summary>
    public interface IModuleLinker
    {
        /// <summary>Does the host still list this module (file name with
        /// extension) as a loaded app?</summary>
        bool IsRegistered(string moduleFileName);

        /// <summary>The full path the process has this image mapped from, or
        /// null when it is not mapped.</summary>
        string? MappedPath(string moduleFileName);

        /// <summary>Ask the host to unload the module. Returns the host's error
        /// text when the call failed, null when it returned. Either way the call
        /// proves nothing: the module's state afterwards is the answer.</summary>
        string? TryUnload(string moduleFileName);

        /// <summary>The module's <see cref="ModuleUnloader.MayUnloadExportName"/>
        /// export, ready to call, or null when the module is not mapped or has no
        /// such export (it is then asked nothing and counts as yes).</summary>
        MayUnloadExport? FindMayUnload(string moduleFileName);
    }

    /// <summary>
    /// A module's <c>DevReloadMayUnload_v1</c> export as DevReload calls it: the
    /// module writes one sentence, NUL-ended, into <paramref name="reason"/>
    /// (at most <c>reason.Length</c> characters with the NUL) and returns 1 when
    /// it may unload now, 0 when it refuses.
    /// </summary>
    public delegate int MayUnloadExport(char[] reason);

    /// <summary>Where a module stands after DevReload asked for its unload.</summary>
    public enum ModuleUnloadState
    {
        /// <summary>Neither listed by the host nor mapped: it is out.</summary>
        Gone,
        /// <summary>The host still lists it: the module declined its own unload
        /// (its kUnloadAppMsg handler answered with an error) or the host kept it.</summary>
        Refused,
        /// <summary>The host no longer lists it, but the image is still mapped:
        /// something else in the process imports from it.</summary>
        StillMapped,
        /// <summary>Asked first, through its <c>DevReloadMayUnload_v1</c> export,
        /// the module said no, so no module was asked to unload.</summary>
        Declined,
    }

    /// <summary>The outcome of one unload run. <see cref="StoppedAt"/> is null
    /// when every module came out.</summary>
    /// <param name="Unloaded">Modules proven gone, in the order they went.</param>
    /// <param name="StoppedAt">The first module that did not come out. Nothing
    /// after it in the order was asked.</param>
    /// <param name="StillLoaded">Every module of the order that is still in,
    /// <see cref="StoppedAt"/> first.</param>
    /// <param name="State">What <see cref="StoppedAt"/> is.</param>
    /// <param name="HostError">The host's own error text for that module, if
    /// its unload call failed.</param>
    /// <param name="Reason">The module's own sentence when it
    /// <see cref="ModuleUnloadState.Declined"/>.</param>
    public sealed record ModuleUnloadRun(
        IReadOnlyList<string> Unloaded,
        string? StoppedAt,
        IReadOnlyList<string> StillLoaded,
        ModuleUnloadState State,
        string? HostError,
        string? Reason = null)
    {
        public bool Complete => StoppedAt is null;
    }

    /// <summary>
    /// Unloads modules in the order given (.arx before the .dbx it uses) and
    /// proves each one left before asking for the next.
    /// </summary>
    /// <remarks>
    /// A module may refuse its own unload: BricsCAD keeps a module whose
    /// kUnloadAppMsg handler answers with an error, and NorsynDrawingTools' dbx
    /// does exactly that while its objects are in an open drawing. So the call
    /// returning is never taken as success: after every call the host's list of
    /// loaded apps and the process image are asked. The run stops at the first
    /// module still in, because the modules after it (a .dbx under a refusing
    /// .arx) are what it depends on. A retry asks the module that stopped it
    /// again, whether or not the host still lists it.
    ///
    /// <para>Before any of that, every module still in is ASKED whether it may
    /// unload now, through its optional <see cref="MayUnloadExportName"/>
    /// export. One no and nothing is unloaded, the .arx at the front included.
    /// A refusal from the unload handler itself cannot always be recovered:
    /// BricsCAD V26 then drops the module from its list, keeps it mapped, and
    /// never calls its unload handler again (2026-10-06), so the session is
    /// left without the modules that did come out. Asking first prevents it.</para>
    /// </remarks>
    public static class ModuleUnloader
    {
        /// <summary>The optional export a module answers "may I unload you now?"
        /// through: <c>extern "C" __declspec(dllexport) int
        /// DevReloadMayUnload_v1(wchar_t* reason, int reasonChars)</c>.</summary>
        public const string MayUnloadExportName = "DevReloadMayUnload_v1";

        /// <summary>The size of the buffer a module's sentence is written into,
        /// NUL included.</summary>
        public const int ReasonChars = 1024;

        public static ModuleUnloadRun Run(IReadOnlyList<string> order, IModuleLinker linker)
        {
            // Ask first, unload after: one module that says no and nothing is
            // asked to unload.
            foreach (string m in order)
            {
                if (StateOf(m, linker) == ModuleUnloadState.Gone) continue;
                if (Declines(m, linker) is string sentence)
                {
                    var still = order.Where(r => StateOf(r, linker) != ModuleUnloadState.Gone).ToList();
                    return new ModuleUnloadRun(Array.Empty<string>(), m, still,
                                               ModuleUnloadState.Declined, null, sentence);
                }
            }

            var gone = new List<string>();
            for (int i = 0; i < order.Count; i++)
            {
                string m = order[i];
                string? error = null;
                // Every module still in is asked, listed or not. Mapped but no
                // longer listed is how BricsCAD leaves a module that refused its
                // unload earlier, so on a retry that is the normal state, and only
                // the module's own unload handler can let it go. Only a module
                // already out (neither listed nor mapped) is skipped.
                if (StateOf(m, linker) != ModuleUnloadState.Gone)
                    error = linker.TryUnload(m);

                var state = StateOf(m, linker);
                if (state != ModuleUnloadState.Gone)
                {
                    var still = new List<string> { m };
                    still.AddRange(order.Skip(i + 1).Where(r => StateOf(r, linker) != ModuleUnloadState.Gone));
                    return new ModuleUnloadRun(gone, m, still, state, error);
                }
                gone.Add(m);
            }
            return new ModuleUnloadRun(gone, null, Array.Empty<string>(), ModuleUnloadState.Gone, null);
        }

        private static ModuleUnloadState StateOf(string m, IModuleLinker linker) =>
            linker.IsRegistered(m) ? ModuleUnloadState.Refused
            : linker.MappedPath(m) is not null ? ModuleUnloadState.StillMapped
            : ModuleUnloadState.Gone;

        /// <summary>The module's sentence when it says no, null when it may
        /// unload now or has no export to ask. Only 1 is a yes: an export that
        /// throws or answers anything else holds the unload, because unloading
        /// on an undecided answer is the very thing the question prevents.</summary>
        private static string? Declines(string m, IModuleLinker linker)
        {
            if (linker.FindMayUnload(m) is not MayUnloadExport ask) return null;

            var buffer = new char[ReasonChars];
            int answer;
            try
            {
                answer = ask(buffer);
            }
            catch (Exception ex)
            {
                return $"its {MayUnloadExportName} export failed when called ({ex.Message}).";
            }
            if (answer == 1) return null;

            int end = Array.IndexOf(buffer, '\0');
            string said = new string(buffer, 0, end < 0 ? buffer.Length : end).Trim();
            return said.Length > 0
                ? said
                : $"its {MayUnloadExportName} export answered {answer} and gave no reason.";
        }

        /// <summary>
        /// The refusal as the caller reads it, or null for a complete run. It never
        /// calls a module that is still in "unloaded".
        /// <paramref name="hostName"/> is the CAD as the user knows it;
        /// <paramref name="hostHint"/> is appended as given.
        /// </summary>
        public static string? Describe(ModuleUnloadRun run, string hostName, string hostHint = "")
        {
            if (run.StoppedAt is not string m) return null;

            // Asked first and said no: the module's own sentence is the answer,
            // and nothing came out.
            if (run.State == ModuleUnloadState.Declined)
                return $"'{m}' may not unload now: {run.Reason} Nothing was unloaded. " +
                       $"Still loaded: {string.Join(", ", run.StillLoaded)}.";

            string hostSaid = run.HostError is null ? "" : $" ({run.HostError})";
            string why = run.State == ModuleUnloadState.Refused
                ? $"'{m}' refused its own unload: {hostName} still lists it as loaded after the unload call" +
                  hostSaid +
                  ". That is the module's own decision, not a DevReload failure; the module's own log says why."
                : $"'{m}' is still mapped into {hostName} after the unload call" + hostSaid +
                  $", although {hostName} no longer lists it: another loaded module imports from it, or the " +
                  "module refused its own unload (its own log says so if it did).";

            string left = run.Unloaded.Count == 0
                ? " Nothing was unloaded."
                : $" Unloaded before it: {string.Join(", ", run.Unloaded)}.";

            return why + left +
                   $" Still loaded: {string.Join(", ", run.StillLoaded)}; DevReload still records " +
                   (run.StillLoaded.Count == 1 ? "it" : "them") +
                   ", so a later reload or unload continues from here." + hostHint;
        }

        /// <summary>The payload record after a run: <paramref name="previous"/>
        /// with only the modules proven gone taken out. Companions are never
        /// unloaded, so they stay as recorded.</summary>
        public static PayloadManifest Remaining(PayloadManifest previous, IEnumerable<string> unloaded)
        {
            var gone = new HashSet<string>(unloaded, StringComparer.OrdinalIgnoreCase);
            return previous with { Modules = previous.Modules.Where(m => !gone.Contains(m.FileName)).ToList() };
        }
    }
}
