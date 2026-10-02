using System;
using System.Collections.Generic;
using System.Linq;

#if BRICSCAD
using Bricscad.ApplicationServices;
#endif

using DevReload.Diagnostics;
using DevReload.Hud;

using Exception = System.Exception;

namespace DevReload.Oarx
{
    /// <summary>What a cycle does with a drawing that has unsaved changes.</summary>
    public enum ModifiedDrawings
    {
        /// <summary>Close nothing and say which drawings are unsaved.</summary>
        Refuse,
        /// <summary>Save each named drawing before closing it.</summary>
        Save,
        /// <summary>Close without saving.</summary>
        Discard,
    }

    /// <summary>
    /// Closes the drawings around a module unload, and reopens them after the load.
    /// </summary>
    /// <remarks>
    /// BricsCAD will not unload a native module while an open drawing holds objects
    /// of its classes. It sends kUnloadAppMsg, lets the module tear itself down and
    /// answer OK, and only then refuses to free it, so a module without its own
    /// guard is left torn down under live objects, and the next regen crashes
    /// (measured 2026-10-02 with NorsynDrawingTools' NSNorsynDistrictHeating.dbx).
    /// AutoCAD unloads and keeps the objects as stand-ins, so there this does
    /// nothing. Which drawings hold a module's objects cannot be asked generically,
    /// so every NAMED drawing is closed, before the unload is even tried.
    ///
    /// <para>One drawing always stays open: BricsCAD on its Start tab has no
    /// document to run anything in. An unnamed drawing with no unsaved changes is
    /// kept (it holds no objects); without one, a blank drawing is added.</para>
    ///
    /// <para>Everything is decided before anything is closed: a refusal leaves
    /// every drawing as it was.</para>
    /// </remarks>
    internal sealed class OarxDrawingCycle
    {
        private readonly List<string> _closed;
        private readonly string? _active;

        private OarxDrawingCycle(List<string> closed, string? active)
        {
            _closed = closed;
            _active = active;
        }

        /// <summary>The drawings this cycle closed, by full path, in the order they were open.</summary>
        public IReadOnlyList<string> Closed => _closed;

        /// <summary>
        /// Close the drawings, or refuse. Returns null with <paramref name="refusal"/>
        /// set when a drawing's unsaved changes stop the cycle; nothing is closed then.
        /// </summary>
        public static OarxDrawingCycle? Close(
            ModifiedDrawings policy, IReloadProgress ui, out string? refusal)
        {
#if BRICSCAD
            var docs = Application.DocumentManager;
            var all = docs.Cast<Document>().ToList();
            string? active = docs.MdiActiveDocument?.Name;

            var unnamedUnsaved = all.Where(d => !d.IsNamedDrawing && !IsSaved(d)).ToList();
            var namedUnsaved = all.Where(d => d.IsNamedDrawing && !IsSaved(d)).ToList();

            if (policy != ModifiedDrawings.Discard && unnamedUnsaved.Count > 0)
            {
                refusal =
                    $"{Names(unnamedUnsaved)} has unsaved changes and no file to reopen it from. " +
                    "BricsCAD will not unload a module while a drawing holds its objects, so the " +
                    "drawings are closed and reopened around it. Save the drawing to a path (or work " +
                    "in a named copy), or pass modifiedDrawings=discard. Nothing was closed or unloaded.";
                return null;
            }
            if (policy == ModifiedDrawings.Refuse && namedUnsaved.Count > 0)
            {
                refusal =
                    $"{Names(namedUnsaved)} has unsaved changes. BricsCAD will not unload a module " +
                    "while a drawing holds its objects, so the drawings are closed and reopened around " +
                    "it. Save first, or pass modifiedDrawings=save or discard. Nothing was closed or unloaded.";
                return null;
            }

            var keep = all.FirstOrDefault(d => !d.IsNamedDrawing && IsSaved(d))
                       ?? docs.Add(string.Empty);

            var closed = new List<string>();
            foreach (var d in all)
            {
                if (d == keep) continue;
                bool unsaved = !IsSaved(d);
                if (!d.IsNamedDrawing)
                {
                    // Unnamed and saved holds nothing: it stays. Unnamed and unsaved
                    // only reaches here under Discard.
                    if (unsaved) { ui.Line($"closed {d.Name} (discarded)"); d.CloseAndDiscard(); }
                    continue;
                }
                string path = d.Name;
                if (unsaved && policy == ModifiedDrawings.Save)
                {
                    d.CloseAndSave(path);
                    ui.Line($"closed {path} (saved)");
                }
                else
                {
                    d.CloseAndDiscard();
                    ui.Line(unsaved ? $"closed {path} (discarded)" : $"closed {path}");
                }
                closed.Add(path);
            }
            refusal = null;
            return new OarxDrawingCycle(closed, active);
#else
            refusal = null;
            return new OarxDrawingCycle(new List<string>(), null);
#endif
        }

        /// <summary>Open the closed drawings again, in their order, and give back
        /// the active one. A drawing that will not open is reported, and the rest
        /// still open.</summary>
        public void Reopen(IReloadProgress ui)
        {
#if BRICSCAD
            var docs = Application.DocumentManager;
            foreach (string path in _closed)
            {
                try
                {
                    docs.Open(path, false);
                    ui.Line($"reopened {path}");
                }
                catch (Exception ex)
                {
                    ui.Line($"could NOT reopen {path}: {ex.Message}");
                    DevReloadDiagnostics.Report($"OarxDrawingCycle: reopen {path}", ex);
                }
            }
            if (_active == null) return;
            foreach (Document d in docs)
            {
                if (string.Equals(d.Name, _active, StringComparison.OrdinalIgnoreCase))
                {
                    docs.MdiActiveDocument = d;
                    return;
                }
            }
#endif
        }

#if BRICSCAD
        /// <summary>Has the drawing no unsaved changes? The COM document's Saved
        /// flag answers without making the drawing active (the managed Document has
        /// no such property). If it cannot be read, the drawing counts as unsaved:
        /// the cycle then refuses rather than discard work.</summary>
        private static bool IsSaved(Document d)
        {
            try
            {
                dynamic com = d.AcadDocument;
                return (bool)com.Saved;
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report($"OarxDrawingCycle: Saved flag of {d.Name}", ex);
                return false;
            }
        }

        private static string Names(IEnumerable<Document> docs) =>
            string.Join(", ", docs.Select(d => $"'{d.Name}'"));
#endif
    }
}
