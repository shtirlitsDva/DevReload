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
    }

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
    public sealed record ModuleUnloadRun(
        IReadOnlyList<string> Unloaded,
        string? StoppedAt,
        IReadOnlyList<string> StillLoaded,
        ModuleUnloadState State,
        string? HostError)
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
    /// .arx) are what it depends on.
    /// </remarks>
    public static class ModuleUnloader
    {
        public static ModuleUnloadRun Run(IReadOnlyList<string> order, IModuleLinker linker)
        {
            var gone = new List<string>();
            for (int i = 0; i < order.Count; i++)
            {
                string m = order[i];
                string? error = null;
                // A module the host no longer lists cannot be unloaded through it
                // (the call would only fail); its state is already the answer.
                if (linker.IsRegistered(m))
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

        /// <summary>
        /// The refusal as the caller reads it, or null for a complete run. It never
        /// calls a module that is still in "unloaded".
        /// <paramref name="hostName"/> is the CAD as the user knows it;
        /// <paramref name="hostHint"/> is appended as given.
        /// </summary>
        public static string? Describe(ModuleUnloadRun run, string hostName, string hostHint = "")
        {
            if (run.StoppedAt is not string m) return null;

            string why = run.State == ModuleUnloadState.Refused
                ? $"'{m}' refused its own unload: {hostName} still lists it as loaded after the unload call" +
                  (run.HostError is null ? "" : $" ({run.HostError})") +
                  ". That is the module's own decision, not a DevReload failure; the module's own log says why."
                : $"'{m}' is still mapped into {hostName} after the unload call, although {hostName} no longer " +
                  "lists it: another loaded module imports from it, or the module refused its own unload " +
                  "(its own log says so if it did).";

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
