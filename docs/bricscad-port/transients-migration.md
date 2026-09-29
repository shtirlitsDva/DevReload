<bricscad-transients-migration>

<summary>
How transient graphics (`TransientManager`) behave in BricsCAD V26 compared with AutoCAD, and
what to change when porting a plugin that draws them. Measured live in BricsCAD V26.2 with a
probe plugin while fixing DevReload's reload HUD; each claim below says whether it was
measured or only read somewhere. `design.md` `<hud>` has the DevReload-specific story.

The one rule to remember: **a custom-drawn transient must return `DrawableIsAnEntity` (1)
from `SetAttributes`, or BricsCAD draws nothing.** Everything else ports as is.
</summary>

<api-mapping>
Same API, different namespaces:

| AutoCAD | BricsCAD |
|---|---|
| `Autodesk.AutoCAD.GraphicsInterface.TransientManager` | `Teigha.GraphicsInterface.TransientManager` |
| `TransientDrawingMode` | same names (`DirectTopmost`, `DirectShortTerm`, `Main`, `Highlight`, …) |
| `DrawableAttributes` enum | `AttributesFlags` enum, **incomplete** (see `<flags>`) |
| `DrawableOverrule` | same, in `Teigha.GraphicsInterface` |
| `Drawable` (no usable managed ctor) | `Drawable` with a protected ctor, subclassable (see `<drawable-subclass>`) |

`AddTransient` / `UpdateTransient` / `EraseTransient` with an empty `IntegerCollection`
(meaning the current viewports) work as in AutoCAD.
</api-mapping>

<the-entity-bit>
MEASURED. A transient whose drawing is custom, whether a `DrawableOverrule` over a carrier or a
`Drawable` subclass, is only drawn if `SetAttributes` includes `DrawableIsAnEntity` (1).

Without it, BricsCAD calls `SetAttributes` once, at `AddTransient`, and then never calls
`WorldDraw` or `ViewportDraw`. Nothing is drawn: not the custom geometry, and with an overrule
not even the carrier entity's own geometry. No error or log line appears; `AddTransient` still
returns true.

| `SetAttributes` returns | AttributesCalls | WorldDraw / ViewportDraw calls (10 updates) |
|---|---|---|
| `0` | 1 | 0 / 0 |
| `256 \| 2048 \| 16384` (RegenDraw, ViewDependentViewportDraw, NotPlottable) | 1 | 0 / 0 |
| `1` | 20 | 10 / 10 |
| `1 \| 256 \| 2048 \| 16384` | 22 | 10 / 10 |

The failing flags failed in `DirectTopmost`, `Main` (subDrawingMode 128) and `Highlight`. The
working flags were tested in `DirectTopmost` and `Main`. Both a `DrawableOverrule` on a
`DBPoint` carrier (with `SetCustomFilter` + pointer match) and a `Drawable` subclass behave
this way.

It applies to transients only. An overrule on DATABASE entities returning `0` still got
`WorldDraw` and `ViewportDraw` on `REGEN` (measured).

AutoCAD draws the same overrule without the bit. Whether adding the bit changes anything in
AutoCAD was NOT tested, so DevReload adds it under `#if BRICSCAD` only:

```csharp
public override int SetAttributes(Drawable drawable, DrawableTraits traits)
{
    base.SetAttributes(drawable, traits);
#if BRICSCAD
    // kDrawableIsAnEntity is load-bearing on BricsCAD transients.
    return 1 | 256 | 2048 | 16384;
#else
    return (int)(DrawableAttributes.RegenDraw
               | DrawableAttributes.ViewDependentViewportDraw
               | DrawableAttributes.NotPlottable);
#endif
}
```

Where the lead came from: Bricsys's own rhino.inside-bricscad plugin returns
`AttributesFlags.DrawableIsAnEntity` from its transient `CompoundDrawable`
(https://github.com/Bricsys/rhino.inside-bricscad, `Grasshopper-BricsCAD/Visualization`).
</the-entity-bit>

<flags>
The managed `Teigha.GraphicsInterface.AttributesFlags` enum has `DrawableNone` (0),
`DrawableIsAnEntity` (1) and so on up to `DrawableRegenDraw` (256), but not the higher values.
Use the native `SetAttributesFlags` numbers for those:

| Flag | Value |
|---|---|
| kDrawableIsAnEntity | 1 |
| kDrawableRegenDraw | 256 |
| kDrawableViewDependentViewportDraw | 2048 |
| kDrawableNotPlottable | 16384 |

BricsCAD accepts them (measured: `1|256|2048|16384` draws).
</flags>

<plain-entities>
MEASURED. Ordinary entities added as transients (`Circle`, `Solid`, `DBText`,
`Polyline`) need no flags. They draw at idle and while a command holds the main thread,
provided the loop calls:

```csharp
tm.UpdateTransient(entity, vps);
Application.UpdateScreen();
// + pump messages (PeekMessage/DispatchMessage, or Forms Application.DoEvents)
```

Changing the entity (`SetPointAt`, `TextString`, …) and calling `UpdateTransient` animates it
live. DevReload prototyped a filled panel + text + progress bar this way before the entity bit
was found. It remains a fallback that needs no custom drawing code.
</plain-entities>

<drawable-subclass>
MEASURED. Unlike AutoCAD, a managed class derived from `Teigha.GraphicsInterface.Drawable` is
dispatched by BricsCAD: `SubSetAttributes`, `SubWorldDraw` and `SubViewportDraw` are called
(with the entity bit). In AutoCAD, `Drawable` has no managed ctor without an unmanaged pointer,
and subclassing a concrete entity only subclasses the wrapper. That is why DevReload uses the
overrule-on-a-carrier pattern, which works on both hosts.

A minimal subclass must override:

```csharp
class HudDrawable : Drawable
{
    public override bool IsPersistent => false;
    public override ObjectId Id { get; }   // ObjectId.Null
    protected override int SubSetAttributes(DrawableTraits t) => (int)AttributesFlags.DrawableIsAnEntity;
    protected override bool SubWorldDraw(WorldDraw wd) => false;   // false => ViewportDraw is called
    protected override void SubViewportDraw(ViewportDraw vd) { /* draw */ }
    protected override int SubViewportDrawLogicalFlags(ViewportDraw vd) => 0;   // abstract
}
```

`Bounds` is not virtual and cannot be overridden.

Keep the drawable (and an overrule) alive in a static or field for as long as it is a transient.
The graphics system calls back into it.
</drawable-subclass>

<viewport-data>
MEASURED in `ViewportDraw` on a plan view (V26.2):
- `EyeToWorldTransform` and `WorldToEyeTransform` are consistent. The camera target maps to
  eye `(0,0,0)`.
- `GetNumPixelsInUnitSquare` agrees with `SCREENSIZE.Y / VIEWSIZE`.
- `DeviceContextViewportCorners` returns REAL corners in BricsCAD: drawing units, centred on 0,
  e.g. `(-925,-148.5)/(925,148.5)` for a 2212×355 px area. In AutoCAD it returns
  `((0,0),(0,0))`, which is why DevReload's HUD sizes itself from `SCREENSIZE`. That works on
  both hosts.

So eye-space, screen-anchored layouts (push `EyeToWorldTransform` as the model transform, then
place in pixels × units-per-pixel) port unchanged.

Release notes mention `ViewportDraw.Viewport.ViewDirection` returning the Z axis regardless of
the view, fixed in V26.1.07. That comes from the notes only, not measured. Prefer the transforms
over `ViewDirection` on older builds.
</viewport-data>

<driving-updates>
- A transient repaints when you call `UpdateTransient` + `Application.UpdateScreen()` and let
  messages be dispatched. Inside a long main-thread operation you must pump. DevReload's
  `ReloadHud.PumpPaint` dispatches everything except keyboard and mouse-button input.
- Do NOT drive transient refreshes from `Application.Idle`. In a BricsCAD started by a script or
  agent, Idle never fires (see `design.md` `<main-thread>`). Rhino.inside-bricscad does redraw
  from Idle, so its previews would freeze in such an instance. Post to the main thread's
  `SynchronizationContext` instead, or update from the command that changes the state.
- Getting the manager with `HostApplicationServices.WorkingDatabase` temporarily set to the
  document's database (rhino.inside's `TransientGraphicsManager`) made no difference in the
  probe. Plain `TransientManager.CurrentTransientManager` works.
</driving-updates>

<diagnosing>
Count the calls. Put counters in `SetAttributes`, `WorldDraw` and `ViewportDraw` and log them
after a few updates:
- `SetAttributes` once and no draws: the entity bit is missing.
- Draw calls but nothing visible: geometry or placement (eye versus world, units per pixel), or
  the drawing area is too small for a pixel-sized layout.
- `AddTransient` returned false: no current transient manager (no document).

DevReload's HUD logs "HUD registered but never drawn" when a cycle ends with zero frames. That
is the symptom this bit caused.

A probe plugin loaded through DevReload (register it, `{PREFIX}DEV`) is the fast way to answer
these questions live. Screenshot with `ui_screenshot_window` (PrintWindow), which captures
BricsCAD even when other windows cover it.
</diagnosing>

<release-notes>
NOT MEASURED; from BricsCAD release notes (https://boa.bricscad.octave.com/common/releasenotes.jsp),
useful when supporting older versions:
- V18.2.14: a custom Drawable added via AddTransient errored on pan/zoom. Fixed.
- V24.2.03: `subRegenFlags` overrides were only honoured on classes derived directly from
  AcGiDrawable. Fixed.
- V24.2.04: crash after `eraseTransient`. Fixed.
- V26.1.07: `ViewportDraw.Viewport.ViewDirection` always the Z axis. Fixed.
- V26.2.03: crash when removing a transient drawable. Fixed.

Forum history: in V17/V18 .NET transients were ignored entirely
(https://forum.bricsys.com/discussion/33727). After `EraseTransient`, gile's advice is
`Application.UpdateScreen()` then `Editor.Regen()` if a ghost remains
(https://forum.bricsys.com/discussion/35236).
</release-notes>

<checklist>
Porting a transient-drawing plugin to BricsCAD:
1. Swap namespaces to `Teigha.GraphicsInterface` / `Teigha.DatabaseServices` under `#if BRICSCAD`.
2. Every custom-drawn transient: OR `1` (`DrawableIsAnEntity`) into the `SetAttributes` return.
3. Replace `DrawableAttributes.*` above 256 with the native numbers from `<flags>`.
4. Plain entity transients: no change.
5. Refresh with `UpdateTransient` + `UpdateScreen` and pump if you hold the main thread. Never
   rely on `Application.Idle`.
6. Verify with draw-call counters, not just by looking. An invisible transient and a missing
   bit look the same.
</checklist>

</bricscad-transients-migration>
