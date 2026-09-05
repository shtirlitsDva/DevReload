using System;
using DevReload.Diagnostics;

namespace DevReload.Hud
{
    /// <summary>
    /// The vocabulary of reload steps, shared by every host that drives the HUD.
    /// </summary>
    /// <remarks>
    /// A vocabulary, NOT an order. No cycle runs all of these, and the two that
    /// exist disagree about sequence:
    ///
    /// <para>OARX is Preflight -> Unload -> Verify -> Build -> Load, because a
    /// loaded native module locks its own file and must come out before the linker
    /// can write over it. The .NET cycle is Build -> Unload -> Load, because a
    /// stream-loaded assembly locks nothing, so building FIRST lets a failed build
    /// leave the running plugin untouched. <see cref="ReloadCycle"/> is what states
    /// the order; this enum only names the steps.</para>
    ///
    /// <para>LoaderUnloader's HUD has an extra <c>Unmap</c> step between Unload and
    /// Build because <c>acedArxUnload</c> defers its FreeLibrary to AutoCAD's next
    /// idle. DevReload unloads through the dynamic linker instead, which unmaps
    /// synchronously (research F2), so there is nothing to wait for.
    /// <see cref="Verify"/> replaces it: the unload claimed success, and this is
    /// where that claim is checked against the file system.</para>
    /// </remarks>
    public enum ReloadStep
    {
        Preflight,
        Unload,
        Verify,
        Build,
        Load,
    }

    /// <summary>
    /// One host's cycle: which steps it runs, in order, and the phrase the HUD
    /// shows while each is active.
    /// </summary>
    /// <remarks>
    /// The HUD draws one chip per entry, so naming a step the host never reports
    /// leaves a chip that never lights. Declare only the steps actually walked.
    /// </remarks>
    public sealed class ReloadCycle
    {
        private readonly (ReloadStep Step, string Phrase)[] _steps;

        public ReloadCycle(params (ReloadStep Step, string Phrase)[] steps)
        {
            if (steps == null || steps.Length == 0)
                throw new ArgumentException("A cycle needs at least one step.", nameof(steps));
            _steps = steps;
        }

        public int Count => _steps.Length;

        /// <summary>The chip caption. Derived from the enum name, so there is no
        /// second list to drift out of sync with <see cref="ReloadStep"/>.</summary>
        public string LabelAt(int index) => _steps[index].Step.ToString().ToUpperInvariant();

        /// <summary>What the header says while this step is the active one.</summary>
        public string PhraseAt(int index) => _steps[index].Phrase;

        /// <summary>Position of <paramref name="step"/> in this cycle, or -1 if the
        /// cycle does not run it.</summary>
        public int IndexOf(ReloadStep step)
        {
            for (int i = 0; i < _steps.Length; i++)
                if (_steps[i].Step == step) return i;
            return -1;
        }
    }

    /// <summary>
    /// Where a reload cycle reports itself. Keeps the lifecycle free of any
    /// opinion about whether that is a HUD, the command line, or nothing.
    /// </summary>
    public interface IReloadProgress
    {
        void Begin(string title, ReloadCycle cycle);
        void Step(ReloadStep step);
        void Line(string text);
        void Finish(string verdict, bool ok);
    }

    /// <summary>Discards everything. Used when a cycle runs headless — tests, and
    /// startup auto-load, where one HUD per auto-loaded plugin would hold the
    /// session hostage for three seconds each before the user can do anything.</summary>
    public sealed class NullReloadProgress : IReloadProgress
    {
        public static readonly NullReloadProgress Instance = new();
        public void Begin(string title, ReloadCycle cycle) { }
        public void Step(ReloadStep step) { }
        public void Line(string text) { }
        public void Finish(string verdict, bool ok) { }
    }

    /// <summary>Writes the cycle to the AutoCAD command line under a host tag.</summary>
    public sealed class EditorReloadProgress : IReloadProgress
    {
        private readonly Action<string> _write;
        private readonly string _tag;

        public EditorReloadProgress(Action<string> write, string tag)
        {
            _write = write;
            _tag = tag;
        }

        public void Begin(string title, ReloadCycle cycle) => _write($"[{_tag}] {title}");
        public void Step(ReloadStep step) => _write($"[{_tag}] {step}...");
        public void Line(string text) => _write("  " + text);
        public void Finish(string verdict, bool ok)
            => _write($"[{_tag}] {(ok ? "OK" : "FAILED")} — {verdict}");
    }

    /// <summary>Sends the cycle to several sinks (HUD + command line).</summary>
    public sealed class CompositeReloadProgress : IReloadProgress
    {
        private readonly IReloadProgress[] _sinks;
        public CompositeReloadProgress(params IReloadProgress[] sinks) => _sinks = sinks;

        public void Begin(string t, ReloadCycle c) { foreach (var s in _sinks) Safe(() => s.Begin(t, c)); }
        public void Step(ReloadStep step) { foreach (var s in _sinks) Safe(() => s.Step(step)); }
        public void Line(string text) { foreach (var s in _sinks) Safe(() => s.Line(text)); }
        public void Finish(string v, bool ok) { foreach (var s in _sinks) Safe(() => s.Finish(v, ok)); }

        // A reporting sink must never be the reason a reload fails.
        /// <summary>
        /// Category B — report, do not rethrow. Every call here paints the
        /// transient HUD. A drawing-surface glitch must not abort the reload the
        /// HUD is merely narrating, so the failure is recorded and painting
        /// continues.
        /// </summary>
        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { DevReloadDiagnostics.Report("ReloadHud paint", ex); }
        }
    }
}
