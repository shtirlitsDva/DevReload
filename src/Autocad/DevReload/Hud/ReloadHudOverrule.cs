using System;
using System.Collections.Generic;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

namespace DevReload.Hud
{
    /// <summary>
    /// A reload cycle's on-screen presence: a transient-graphics HUD anchored to
    /// the bottom of the drawing area, showing which step is running, an animated
    /// indeterminate bar, and the tail of the build log. Driven by both the OARX
    /// and the .NET lifecycles; the chip strip comes from the caller's
    /// <see cref="ReloadCycle"/>, since the two do not run the same steps.
    /// </summary>
    /// <remarks>
    /// A port of LoaderUnloader's <c>BuildHud</c> (utils/LoaderUnloader/BuildHud.cpp)
    /// to the managed AcGi surface. The layout, palette and timings are LU's — this
    /// is deliberately the same HUD, so the two loaders look like one tool.
    ///
    /// <para><b>Coordinate model.</b> The HUD is laid out in PIXELS from the drawing
    /// area's lower-left corner and drawn in EYE space: <see cref="ViewportDraw"/>
    /// pushes the viewport's eye-to-world matrix as the model transform, so every
    /// coordinate afterwards is read as eye space and lands screen-aligned whatever
    /// the view's rotation, zoom or UCS. <see cref="Frame.At"/> is the one place
    /// pixels become eye units.</para>
    ///
    /// <para><b>The viewport rectangle does not come from the DC corners.</b> LU takes
    /// the drawing area from <c>getViewportDcCorners</c>, whose corners are DRAWING
    /// UNITS (outside perspective the DCS is the eye coordinate system) that the pixel
    /// density converts. The managed wrapper does not carry them: measured live,
    /// <c>ImpViewport.DeviceContextViewportCorners</c> returns <c>((0,0),(0,0))</c> on
    /// every draw pass — which sized the HUD to nothing, so it was elaborated and then
    /// discarded by its own size guard. The rectangle is therefore read from SCREENSIZE,
    /// which is already in pixels, and the centre from the camera target: the two
    /// numbers the corners were ever a source of.</para>
    ///
    /// <para>SCREENSIZE describes the CURRENT viewport, while this runs once per
    /// visible viewport. In a tiled layout the HUD takes its size from the current
    /// viewport in all of them, though it still centres on each viewport's own camera
    /// target. LU makes the same assumption — an OARX cycle is driven from an ordinary
    /// editing view, not a tiled one.</para>
    ///
    /// <para>Why not the Dc primitives, which take pixels directly? Because there is
    /// no text-in-Dc: the text primitive only exists in model space, and a HUD whose
    /// panel and whose text are placed through two different pipelines drifts apart.
    /// One coordinate system for everything is worth the one matrix push.</para>
    ///
    /// <para><b>Why an overrule and not a Drawable subclass.</b> LU can simply
    /// derive from <c>AcGiDrawable</c> and override <c>subViewportDraw</c>, because in
    /// C++ the graphics system reaches it through the object's vtable. From .NET that
    /// route does not exist: <c>Drawable</c> and <c>Entity</c> have no managed
    /// constructor that does not already require an unmanaged pointer, and subclassing
    /// a CONCRETE type (DBPoint, Circle, ...) only subclasses the managed WRAPPER — the
    /// native object's vtable is untouched, so AutoCAD keeps calling its own
    /// implementation and the override never runs. Measured live: a DBPoint subclass
    /// added as a transient reports zero draw passes.
    ///
    /// A <see cref="DrawableOverrule"/> IS dispatched to managed code. So the transient
    /// carrier is a plain <c>DBPoint</c> with nothing to draw, and this overrule draws
    /// the HUD in its place. <see cref="IsApplicable"/> matches on the carrier's
    /// unmanaged pointer, so no other point in the drawing is affected — wrapper
    /// identity cannot be used, because AutoCAD hands the filter a different managed
    /// wrapper around the same native object.</para>
    /// </remarks>
    internal sealed class ReloadHudOverrule : DrawableOverrule
    {
        // ── Palette and timing (LU's) ─────────────────────────────────

        private readonly struct Rgb
        {
            public readonly byte R, G, B;
            public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private static readonly Rgb Panel      = new(18, 20, 25);    // panel body
        private static readonly Rgb Shadow     = new(0, 0, 0);
        private static readonly Rgb Edge       = new(58, 64, 76);    // panel outline
        private static readonly Rgb Track      = new(34, 38, 46);    // unlit cells / chip outline
        private static readonly Rgb Accent     = new(255, 176, 66);  // amber: the running colour
        private static readonly Rgb AccentHot  = new(255, 240, 208); // the comet's core
        private static readonly Rgb OkColor    = new(108, 203, 122);
        private static readonly Rgb Fail       = new(233, 92, 92);
        private static readonly Rgb Warn       = new(226, 178, 92);
        private static readonly Rgb TextBright = new(226, 232, 240);
        private static readonly Rgb TextDim    = new(118, 126, 140);
        private static readonly Rgb Ink        = new(12, 13, 16);    // text ON the accent

        /// <summary>One full sweep of the comet, ms.</summary>
        private const double SweepMs = 1500.0;

        private const int LogRows = 7;   // log lines visible in the panel
        private const int LogKeep = 24;  // lines retained (headroom, not shown)

        /// <summary>Stand-in until a host calls Begin. The field must never be
        /// null: the overrule is a long-lived static and the graphics system can
        /// elaborate a frame at any point between cycles.</summary>
        private static readonly ReloadCycle EmptyCycle =
            new((ReloadStep.Preflight, "starting"));

        // ── State the draw reads ──────────────────────────────────────
        // Written by ReloadHud's entry points and read inside ViewportDraw —
        // both on AutoCAD's main thread, so no locking.

        private readonly List<string> _lines = new();

        public string Title = "";

        /// <summary>The steps this cycle runs, in order — one chip each. Supplied
        /// by the host at Begin, because the two cycles neither run the same steps
        /// nor run them in the same order.</summary>
        public ReloadCycle Cycle = EmptyCycle;

        /// <summary>Index into <see cref="Cycle"/> of the step now running.</summary>
        public int StepIndex;

        public long StartTick;
        public long NowTick;
        public bool Finished;
        public bool Ok;
        public string Verdict = "";

        /// <summary>Frames the graphics system actually elaborated. A cycle that
        /// ends on zero means the transient was accepted but never drawn — worth
        /// reporting, because a HUD that silently does not exist is the one
        /// failure mode nobody would notice.</summary>
        public int Frames;

        public void Reset(string title, ReloadCycle cycle)
        {
            Title = title;
            Cycle = cycle;
            StepIndex = 0;
            _lines.Clear();
            StartTick = Environment.TickCount64;
            NowTick = StartTick;
            Finished = false;
            Ok = false;
            Frames = 0;
            Verdict = "";
        }

        public void AddLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _lines.Add(text.Trim());
            while (_lines.Count > LogKeep) _lines.RemoveAt(0);
        }

        // ── Drawable contract ─────────────────────────────────────────

        /// <summary>The unmanaged pointer of the carrier this overrule speaks for.
        /// Compared by pointer, not by wrapper identity — see the remarks above.</summary>
        public IntPtr CarrierPtr;

        public override bool IsApplicable(RXObject overruledSubject)
            => overruledSubject.UnmanagedObject == CarrierPtr;

        public override int SetAttributes(Drawable drawable, DrawableTraits traits)
        {
            base.SetAttributes(drawable, traits);
            // Never cached, never plotted, and the geometry depends on the
            // viewport — so it must be re-elaborated on every view change.
            return (int)(DrawableAttributes.RegenDraw
                       | DrawableAttributes.ViewDependentViewportDraw
                       | DrawableAttributes.NotPlottable);
        }

        // All the geometry is viewport-dependent; returning false is what makes
        // the graphics system call ViewportDraw per viewport.
        public override bool WorldDraw(Drawable drawable, WorldDraw wd) => false;

        public override void ViewportDraw(Drawable drawable, ViewportDraw vd)
        {
            Matrix3d eyeToWorld = vd.Viewport.EyeToWorldTransform;

            // Pixels per drawing unit, measured at the point the view is centred on.
            Point2d density = vd.Viewport.GetNumPixelsInUnitSquare(
                eyeToWorld * Point3d.Origin);
            if (density.X <= 1e-12 || density.Y <= 1e-12) return;

            // The drawing area in pixels, already in the unit the layout is written
            // in — NOT DeviceContextViewportCorners, which is degenerate here (see
            // the note above).
            var screen = (Point2d)Application.GetSystemVariable("SCREENSIZE");

            // The camera target IS the centre of the view, so the centre needs no
            // corners either. Taken through this viewport's own transform it stays
            // right under a rotated UCS, which VIEWCTR — UCS coordinates — would not.
            Point3d centre = vd.Viewport.WorldToEyeTransform * vd.Viewport.CameraTarget;

            var f = new Frame(
                ux: 1.0 / density.X,
                uy: 1.0 / density.Y,
                vpW: screen.X,
                vpH: screen.Y,
                centre: new Point2d(centre.X, centre.Y));

            // No room for a legible HUD; drawing a squashed one is worse.
            if (f.VpW < 340.0 || f.VpH < 220.0) return;

            if (!vd.Geometry.PushModelTransform(eyeToWorld)) return;
            try
            {
                Frames++;
                DrawPanel(vd, f);
            }
            finally
            {
                vd.Geometry.PopModelTransform();
            }
        }

        // ── Pixels → eye space ────────────────────────────────────────

        private readonly struct Frame
        {
            public readonly double VpW;    // drawing area width, PIXELS
            public readonly double VpH;    // drawing area height, PIXELS
            private readonly double _ux;   // eye units per pixel, x
            private readonly double _uy;   // eye units per pixel, y
            private readonly Point2d _centre;

            public Frame(double ux, double uy, double vpW, double vpH, Point2d centre)
            {
                _ux = ux; _uy = uy; VpW = vpW; VpH = vpH; _centre = centre;
            }

            /// <summary>Pixels from the drawing area's lower-left corner → eye
            /// space. The centre comes from the DC corners rather than being
            /// assumed to be the origin, so a viewport whose DCS origin sits
            /// elsewhere still lands right.</summary>
            public Point3d At(double px, double py) => new(
                _centre.X + (px - VpW * 0.5) * _ux,
                _centre.Y + (py - VpH * 0.5) * _uy,
                0.0);

            /// <summary>A text height in pixels as the text style's size. Text is
            /// sized off the y density; on the square pixels every real display
            /// has, x agrees.</summary>
            public double TextSize(double px) => px * _uy;

            /// <summary>A width the text style reported (eye units) back into pixels.</summary>
            public double ToPixels(double units) => units / _ux;
        }

        // ── Primitives ────────────────────────────────────────────────
        // Every one sets colour, transparency AND fill type, because the traits
        // are sticky: whatever the previous primitive left behind applies next.

        private static void SetInk(ViewportDraw vd, Rgb c, byte alpha)
        {
            vd.SubEntityTraits.TrueColor = new EntityColor(c.R, c.G, c.B);
            vd.SubEntityTraits.Transparency = new Transparency(alpha);
        }

        private static void FillRect(ViewportDraw vd, in Frame f, double x, double y,
            double w, double h, Rgb c, byte alpha)
        {
            if (w <= 0.0 || h <= 0.0) return;
            SetInk(vd, c, alpha);
            vd.SubEntityTraits.FillType = FillType.FillAlways;
            using var pts = new Point3dCollection
            {
                f.At(x, y), f.At(x + w, y), f.At(x + w, y + h), f.At(x, y + h),
            };
            vd.Geometry.Polygon(pts);
        }

        private static void StrokeRect(ViewportDraw vd, in Frame f, double x, double y,
            double w, double h, Rgb c, byte alpha)
        {
            if (w <= 0.0 || h <= 0.0) return;
            SetInk(vd, c, alpha);
            vd.SubEntityTraits.FillType = FillType.FillNever;
            using var pts = new Point3dCollection
            {
                f.At(x, y), f.At(x + w, y), f.At(x + w, y + h), f.At(x, y + h), f.At(x, y),
            };
            vd.Geometry.Polyline(pts, Vector3d.ZAxis, IntPtr.Zero);
        }

        /// <summary><paramref name="x"/>,<paramref name="y"/> is the baseline-left
        /// insertion point, in pixels.</summary>
        private static void DrawText(ViewportDraw vd, in Frame f, double x, double y,
            string s, Rgb c, TextStyle style)
        {
            if (string.IsNullOrEmpty(s)) return;
            SetInk(vd, c, 255);
            vd.SubEntityTraits.FillType = FillType.FillNever;
            // raw: build output is full of % and \, and none of it is a
            // formatting code here.
            vd.Geometry.Text(f.At(x, y), Vector3d.ZAxis, Vector3d.XAxis, s, true, style);
        }

        /// <summary>Ink width of <paramref name="s"/> in eye units, or 0 if the
        /// style cannot measure it.</summary>
        private static double TextWidthUnits(TextStyle style, string s)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            try
            {
                Extents2d box = style.ExtentsBox(s, false, true, null);
                return box.MaxPoint.X - box.MinPoint.X;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return 0.0;
            }
        }

        // ── The panel ─────────────────────────────────────────────────

        private void DrawPanel(ViewportDraw vd, in Frame f)
        {
            // The panel takes a fixed share of the drawing area, and ONE scale
            // factor — its width against the design width — drives every dimension
            // inside it. The drawing area's pixel size already carries the display's
            // DPI, so the HUD comes out the same apparent size on a laptop panel and
            // on a 4K monitor at 250% without ever asking Windows about scaling.
            double panelW = Clamp(f.VpW * 0.55, 420.0, f.VpW - 40.0);
            double s = panelW / 620.0;
            double pad = 14.0 * s;
            double titlePx = 15.0 * s;
            double textPx = 11.0 * s;
            double rowH = 16.0 * s;
            double chipH = 15.0 * s;
            double barH = 9.0 * s;

            double panelH = pad + titlePx + 11.0 * s + chipH + 13.0 * s + barH
                + 13.0 * s + LogRows * rowH + pad;
            double x0 = (f.VpW - panelW) * 0.5;
            double y0 = 54.0 * s;

            using var titleStyle = MonoStyle(f.TextSize(titlePx));
            using var textStyle = MonoStyle(f.TextSize(textPx));

            // monotxt.shx is monospaced, so one measurement gives the advance of
            // every character — which is what the log-line clipping below needs.
            double charW = f.ToPixels(TextWidthUnits(textStyle, new string('M', 20))) / 20.0;

            // Body: a shadow, the panel, a hairline of the running colour along
            // the top edge, and an outline.
            FillRect(vd, f, x0 + 4.0 * s, y0 - 4.0 * s, panelW, panelH, Shadow, 110);
            FillRect(vd, f, x0, y0, panelW, panelH, Panel, 236);
            Rgb rule = Finished ? (Ok ? OkColor : Fail) : Accent;
            FillRect(vd, f, x0, y0 + panelH - 2.0 * s, panelW, 2.0 * s, rule, 255);
            StrokeRect(vd, f, x0, y0, panelW, panelH, Edge, 255);

            // Header: the cycle's title, the step (or the verdict), and the clock.
            double y = y0 + panelH - pad - titlePx;
            DrawText(vd, f, x0 + pad, y, Title, Accent, titleStyle);
            double titleW = f.ToPixels(TextWidthUnits(titleStyle, Title));

            string headline = string.IsNullOrEmpty(Verdict)
                ? Cycle.PhraseAt(StepIndex)
                : Verdict;

            // A dim slash between the two. ExtentsBox measures INK, not the
            // advance, so titleW stops at the last stroke of the title — without a
            // separator and a real gap the accent title and the phase text read as
            // one word.
            double gap = 9.0 * s;
            double hx = x0 + pad + titleW + gap;
            DrawText(vd, f, hx, y, "/", TextDim, titleStyle);
            hx += f.ToPixels(TextWidthUnits(titleStyle, "/")) + gap;
            DrawText(vd, f, hx, y, headline,
                Finished ? (Ok ? OkColor : Fail) : TextBright, titleStyle);

            string clock = $"{(NowTick - StartTick) / 1000.0:0.0} s";
            DrawText(vd, f, x0 + panelW - pad - f.ToPixels(TextWidthUnits(titleStyle, clock)),
                y, clock, TextDim, titleStyle);

            // Step chips.
            y -= 11.0 * s + chipH;
            DrawChips(vd, f, x0 + pad, y, s, textStyle);

            // The bar.
            y -= 13.0 * s + barH;
            DrawBar(vd, f, x0 + pad, y, panelW - 2.0 * pad, barH, s);

            // Log tail: oldest at the top, newest at the bottom, older lines faded.
            y -= 13.0 * s;
            int shown = Math.Min(_lines.Count, LogRows);
            int first = _lines.Count - shown;
            double maxChars = charW > 0.0 ? (panelW - 2.0 * pad) / charW : 0.0;

            for (int i = 0; i < shown; i++)
            {
                string line = _lines[first + i];
                if (maxChars > 4.0 && line.Length > (int)maxChars)
                    line = line.Substring(0, (int)maxChars - 3) + "...";

                // Age fade, then let diagnostics override it — an error line has to
                // be readable even when it has scrolled up.
                double age = shown > 1 ? (double)i / (shown - 1) : 1.0;
                Rgb ink = LerpRgb(TextDim, TextBright, 0.35 + 0.65 * age);
                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    ink = Fail;
                else if (line.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
                    ink = Warn;

                double baseline = y - (i + 1) * rowH + 4.0 * s;
                DrawText(vd, f, x0 + pad, baseline, line, ink, textStyle);

                // A terminal cursor after the newest line while the cycle runs.
                if (i + 1 == shown && !Finished && CursorOn(NowTick))
                {
                    double w = f.ToPixels(TextWidthUnits(textStyle, line));
                    FillRect(vd, f, x0 + pad + w + 4.0 * s, baseline - 1.0 * s,
                        charW * 0.8, textPx, Accent, 255);
                }
            }
        }

        private void DrawChips(ViewportDraw vd, in Frame f, double x, double y,
            double s, TextStyle style)
        {
            int active = StepIndex;
            double chipH = 15.0 * s;
            double padX = 7.0 * s;
            double gap = 5.0 * s;

            // The active chip breathes so a long step never looks stalled.
            double breath = 0.5 + 0.5 * Math.Sin((NowTick - StartTick) / 260.0);

            double cx = x;
            for (int i = 0; i < Cycle.Count; i++)
            {
                string label = Cycle.LabelAt(i);
                double w = f.ToPixels(TextWidthUnits(style, label)) + 2.0 * padX;
                bool done = Finished ? Ok : i < active;
                bool running = !Finished && i == active;

                Rgb fill = Track;
                Rgb ink = TextDim;
                if (Finished && !Ok && i == active) { fill = Fail; ink = Ink; }
                else if (done) { fill = LerpRgb(Track, OkColor, 0.35); ink = OkColor; }
                else if (running)
                {
                    fill = LerpRgb(LerpRgb(Track, Accent, 0.55), Accent, breath);
                    ink = Ink;
                }

                FillRect(vd, f, cx, y, w, chipH, fill, 255);
                if (!done && !running) StrokeRect(vd, f, cx, y, w, chipH, Edge, 255);
                DrawText(vd, f, cx + padX, y + 4.5 * s, label, ink, style);
                cx += w + gap;
            }
        }

        private void DrawBar(ViewportDraw vd, in Frame f, double x, double y,
            double w, double h, double s)
        {
            FillRect(vd, f, x, y, w, h, Track, 255);

            if (Finished)
            {
                // No comet on the closing frame: a solid verdict-coloured bar.
                FillRect(vd, f, x + s, y + s, w - 2.0 * s, h - 2.0 * s,
                    Ok ? OkColor : Fail, 255);
                return;
            }

            // Indeterminate comet: a Gaussian bright spot travelling left to right
            // and wrapping. Nothing here claims to know how much work is left,
            // because MSBuild never says.
            const int cells = 72;
            double cellW = w / cells;
            double head = ((NowTick - StartTick) / SweepMs) % 1.0;
            const double sigma = 0.055;

            for (int i = 0; i < cells; i++)
            {
                double u = (i + 0.5) / cells;
                double d = Math.Abs(u - head);
                if (d > 0.5) d = 1.0 - d; // the track wraps, so distance does too
                double t = Math.Exp(-(d * d) / (2.0 * sigma * sigma));
                if (t < 0.04) continue;
                Rgb hot = LerpRgb(Accent, AccentHot, Clamp((t - 0.7) / 0.3, 0.0, 1.0));
                FillRect(vd, f, x + i * cellW + 0.5 * s, y + s,
                    cellW - 1.0 * s, h - 2.0 * s, LerpRgb(Track, hot, t), 255);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        /// <summary>The style both text sizes are cut from. monotxt.shx is
        /// monospaced, which is what makes the log tail clippable by character
        /// count. TrackingPercent 1.0 leaves the font's own character spacing
        /// alone — it is NOT an "off" switch; 0.0 is outside the valid range.</summary>
        private static TextStyle MonoStyle(double height)
        {
            var style = new TextStyle
            {
                FileName = "monotxt.shx",
                BigFontFileName = "",
                TextSize = height,
                XScale = 1.0,
                ObliquingAngle = 0.0,
                TrackingPercent = 1.0,
            };
            _ = style.LoadStyleRec; // a property, not a method — reading it loads
            return style;
        }

        /// <summary>True while the last-drawn frame should show its cursor block (2 Hz).</summary>
        private static bool CursorOn(long ms) => (ms / 450L) % 2L == 0L;

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private static Rgb LerpRgb(Rgb a, Rgb b, double t)
        {
            t = Clamp(t, 0.0, 1.0);
            return new Rgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

    }
}
