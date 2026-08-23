using System;
using System.Runtime.InteropServices;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

namespace DevReload.Hud
{
    /// <summary>
    /// Drives <see cref="ReloadHudOverrule"/> as an <see cref="IReloadProgress"/> sink:
    /// adds the transient at Begin, repaints on every step and line, and holds a
    /// closing verdict frame before erasing it.
    /// </summary>
    /// <remarks>
    /// Single-instance and single-threaded by construction. A reload cycle is not
    /// reentrant, every entry point runs on AutoCAD's main thread, and every one
    /// is a no-op while the HUD is not shown — so callers never have to guard.
    ///
    /// <para>The carrier and the overrule are STATIC, not fields of this class: the
    /// graphics system may hang a cached node off the drawable and come back to it
    /// later, so it has to outlive every AddTransient (ObjectARX developer's guide,
    /// "Using Drawables in your Object").</para>
    ///
    /// <para>What is actually added as the transient is a bare <see cref="DBPoint"/>
    /// carrier; <see cref="ReloadHudOverrule"/> draws the HUD in its place. See the
    /// remarks there for why a Drawable subclass cannot do this job from .NET.</para>
    /// </remarks>
    internal sealed class ReloadHud : IReloadProgress
    {
        /// <summary>The transient the graphics system is given. Nothing of the point
        /// itself is ever drawn — the overrule replaces its draw entirely.</summary>
        private static readonly DBPoint Carrier = new(Point3d.Origin);

        private static readonly ReloadHudOverrule Hud =
            new() { CarrierPtr = Carrier.UnmanagedObject };

        private static bool _shown;
        private static bool _overruleAdded;
        private static bool _previousOverruling;

        /// <summary>How long the closing frame stays up, ms. Long enough to read
        /// "reloaded" or "build failed" without feeling like a wait.</summary>
        private const int HoldMs = 3000;

        /// <summary>~25 fps: fast enough that the comet reads as motion, slow
        /// enough that the forced display updates stay a rounding error next to
        /// the compiler.</summary>
        internal const int FrameMs = 40;

        private readonly Action<string> _warn;

        public ReloadHud(Action<string> warn) => _warn = warn;

        public static bool IsShown => _shown;

        public void Begin(string title, ReloadCycle cycle)
        {
            Hud.Reset(title, cycle);
            if (_shown) { Tick(); return; }

            var mgr = TransientManager.CurrentTransientManager;
            if (mgr == null)
            {
                _warn("(no HUD: no transient manager)");
                return;
            }

            // Registered only for the duration of a cycle, so nothing in the
            // drawing is overruled while DevReload is idle. The filter narrows it
            // to the carrier anyway; this narrows it in time as well.
            Overrule.AddOverrule(RXObject.GetClass(typeof(DBPoint)), Hud, false);
            Hud.SetCustomFilter();
            _previousOverruling = Overrule.Overruling;
            Overrule.Overruling = true;
            _overruleAdded = true;

            // Empty collection = the current/active viewports.
            var viewports = new IntegerCollection();
            _shown = mgr.AddTransient(Carrier, TransientDrawingMode.DirectTopmost, 0, viewports);
            if (!_shown)
            {
                _warn("(no HUD: AddTransient refused)");
                RemoveOverrule();
                return;
            }
            Tick();
        }

        public void Step(ReloadStep step)
        {
            if (!_shown) return;

            int index = Hud.Cycle.IndexOf(step);
            if (index < 0)
            {
                // The host reported a step its own cycle never declared — a wiring
                // bug. Say so in the log rather than lighting the wrong chip or
                // silently ignoring it.
                Hud.AddLine($"(unplanned step: {step})");
                Tick();
                return;
            }

            Hud.StepIndex = index;
            Tick();
        }

        public void Line(string text)
        {
            if (!_shown) return;
            Hud.AddLine(text);
            Tick();
        }

        public void Finish(string verdict, bool ok)
        {
            if (!_shown) return;
            Hud.Finished = true;
            Hud.Ok = ok;
            Hud.Verdict = verdict;

            long until = Environment.TickCount64 + HoldMs;
            while (Environment.TickCount64 < until)
            {
                PumpPaint();
                Tick();
                System.Threading.Thread.Sleep(FrameMs);
            }
            Hide();
        }

        /// <summary>Advance the animation to the current clock and repaint. Cheap;
        /// call it as often as the caller can — the pumped build calls it every
        /// frame so the comet animates through the compile.</summary>
        public static void Tick()
        {
            if (!_shown) return;
            Hud.NowTick = Environment.TickCount64;

            var mgr = TransientManager.CurrentTransientManager;
            if (mgr == null) return;
            var viewports = new IntegerCollection();
            mgr.UpdateTransient(Carrier, viewports);

            // UpdateTransient only marks the transient dirty and UpdateScreen only
            // asks for the repaint — the repaint itself arrives as a message, and
            // while a cycle holds the main thread nothing dispatches it. Measured
            // live: without the pump the drawable is never elaborated at all.
            Application.UpdateScreen();
            PumpPaint();
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;

            if (Hud.Frames == 0)
                _warn("(HUD registered but never drawn — the graphics system did " +
                      "not elaborate the transient)");

            var mgr = TransientManager.CurrentTransientManager;
            if (mgr != null)
            {
                var viewports = new IntegerCollection();
                mgr.EraseTransient(Carrier, viewports);
                Application.UpdateScreen();
            }
            RemoveOverrule();
        }

        /// <summary>Take the overrule back out and restore the global overruling
        /// flag to whatever it was — other code in the session may depend on it.</summary>
        private static void RemoveOverrule()
        {
            if (!_overruleAdded) return;
            _overruleAdded = false;
            Overrule.RemoveOverrule(RXObject.GetClass(typeof(DBPoint)), Hud);
            Overrule.Overruling = _previousOverruling;
        }

        // ── Keeping AutoCAD painting without letting it act ───────────

        /// <summary>
        /// Dispatch every pending message EXCEPT mouse-button, mouse-wheel and
        /// keyboard input, which is dropped.
        /// </summary>
        /// <remarks>
        /// Both cycles need input held back, for different reasons. During an OARX
        /// cycle the modules are unloaded and their files are being rewritten by the
        /// linker, so a command started in that window has nothing to run against.
        /// During a .NET cycle the old plugin is still live, which is worse: a
        /// command started mid-build would be running in an ALC that is about to be
        /// torn down underneath it.
        ///
        /// <para>Paint, timer and mouse-move traffic keeps the window alive and the
        /// cursor tracking; input does not get through.</para>
        /// </remarks>
        internal static void PumpPaint()
        {
            // Bounded so a message storm cannot starve the animation; the rest is
            // picked up next frame.
            for (int guard = 0; guard < 256 && PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE); guard++)
            {
                if (msg.message == WM_QUIT)
                {
                    // Not ours to swallow: put the shutdown back and stop pumping.
                    PostQuitMessage((int)msg.wParam);
                    return;
                }
                if (IsSwallowedInput(msg.message)) continue;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private static bool IsSwallowedInput(uint m)
        {
            if (m >= WM_KEYFIRST && m <= WM_KEYLAST) return true;          // incl. WM_SYSKEY*
            if (m >= WM_LBUTTONDOWN && m <= WM_MOUSEHWHEEL) return true;   // buttons + wheels
            if (m >= WM_NCLBUTTONDOWN && m <= WM_NCXBUTTONDBLCLK) return true;
            return false;                                                  // WM_MOUSEMOVE passes
        }

        private const uint PM_REMOVE = 0x0001;
        private const uint WM_QUIT = 0x0012;
        private const uint WM_KEYFIRST = 0x0100;
        private const uint WM_KEYLAST = 0x0109;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_MOUSEHWHEEL = 0x020E;
        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private const uint WM_NCXBUTTONDBLCLK = 0x00AD;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);
    }
}
