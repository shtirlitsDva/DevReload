using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;
using UiMcp.Dto;

namespace UiMcp.Wpf;

/// <summary>
/// In-process WPF introspection + deterministic interaction. Because this runs
/// inside AutoCAD's process on its UI thread, it reads the live visual tree AND
/// the bound ViewModel directly (no UIA projection, no virtualization gap, no
/// missing-AutomationId problem), and drives controls through their automation
/// peers (coordinate-free, DPI-proof). All members must be called on the WPF
/// thread — the tools that use them are marked [RunOnAcadMainThread].
/// </summary>
public static class WpfInspector
{
    // ── Surfaces ───────────────────────────────────────────────────────
    public static List<SurfaceInfo> ListSurfaces()
    {
        var list = new List<SurfaceInfo>();
        foreach (PresentationSource src in PresentationSource.CurrentSources.OfType<PresentationSource>())
        {
            if (src.RootVisual is not Visual root) continue;
            long hwnd = src is HwndSource hs ? hs.Handle.ToInt64() : 0;
            string title = (root as FrameworkElement)?.Name is { Length: > 0 } n ? n : "";
            int w = 0, h = 0;
            if (root is FrameworkElement fe) { w = (int)fe.ActualWidth; h = (int)fe.ActualHeight; }
            list.Add(new SurfaceInfo(hwnd, title, root.GetType().Name, w, h));
        }
        return list;
    }

    private static Visual? RootForHwnd(long hwnd)
    {
        foreach (PresentationSource src in PresentationSource.CurrentSources.OfType<PresentationSource>())
        {
            if (src is HwndSource hs && (hwnd == 0 || hs.Handle.ToInt64() == hwnd) && src.RootVisual is Visual v)
                return v;
        }
        // Fall back to the first source with a root visual.
        return PresentationSource.CurrentSources.OfType<PresentationSource>()
            .Select(s => s.RootVisual).OfType<Visual>().FirstOrDefault();
    }

    // ── Snapshot ───────────────────────────────────────────────────────
    public static SurfaceSnapshot Snapshot(long hwnd, int maxDepth, bool includeViewModel)
    {
        Visual? root = RootForHwnd(hwnd)
            ?? throw new InvalidOperationException("no WPF surface found"
                + (hwnd != 0 ? $" for hwnd {hwnd}" : ""));

        var rootNode = BuildNode(root, "0", 0, maxDepth);
        var surfaces = ListSurfaces();
        var surface = surfaces.FirstOrDefault(s => hwnd == 0 || s.Hwnd == hwnd) ?? surfaces.FirstOrDefault()
            ?? new SurfaceInfo(hwnd, "", root.GetType().Name, 0, 0);

        var vm = new Dictionary<string, string?>();
        if (includeViewModel && root is FrameworkElement fe && fe.DataContext is { } dc)
            DumpViewModel(dc, vm);

        return new SurfaceSnapshot(surface, rootNode, vm);
    }

    private static ElementNode BuildNode(Visual v, string id, int depth, int maxDepth)
    {
        var fe = v as FrameworkElement;
        string? name = fe?.Name is { Length: > 0 } n ? n : null;
        string? autoId = fe != null && AutomationProperties.GetAutomationId(fe) is { Length: > 0 } a ? a : null;

        var (text, value) = PeerTextValue(v);
        (int x, int y, int w, int h) = ScreenBounds(v, fe);

        var children = new List<ElementNode>();
        if (depth < maxDepth)
        {
            int count = VisualTreeHelper.GetChildrenCount(v);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(v, i) is Visual cv)
                    children.Add(BuildNode(cv, $"{id}/{i}", depth + 1, maxDepth));
            }
        }

        return new ElementNode(
            Id: id,
            Type: v.GetType().Name,
            Name: name,
            AutomationId: autoId,
            Text: text,
            Value: value,
            IsEnabled: fe?.IsEnabled ?? true,
            IsVisible: fe == null || fe.IsVisible,
            X: x, Y: y, Width: w, Height: h,
            Children: children);
    }

    private static (int, int, int, int) ScreenBounds(Visual v, FrameworkElement? fe)
    {
        try
        {
            if (fe == null || !fe.IsVisible) return (0, 0, 0, 0);
            var size = fe.RenderSize;
            Point tl = v.PointToScreen(new Point(0, 0));
            Point br = v.PointToScreen(new Point(size.Width, size.Height));
            return ((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
                    (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y));
        }
        catch { return (0, 0, 0, 0); }
    }

    private static (string? text, string? value) PeerTextValue(Visual v)
    {
        if (v is not UIElement uie) return (null, null);
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(uie);
            if (peer == null) return (null, null);
            string? text = peer.GetName() is { Length: > 0 } nm ? nm : null;
            string? value = null;
            if (peer.GetPattern(PatternInterface.Value) is IValueProvider vp) value = vp.Value;
            return (text, value);
        }
        catch { return (null, null); }
    }

    private static void DumpViewModel(object dc, Dictionary<string, string?> into)
    {
        foreach (var p in dc.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            try
            {
                object? val = p.GetValue(dc);
                into[p.Name] = val switch
                {
                    null => null,
                    string s => s,
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => val.ToString(),
                };
            }
            catch (Exception ex) { into[p.Name] = $"<err: {ex.GetType().Name}>"; }
        }
    }

    // ── Element resolution + actions ────────────────────────────────────
    public static Visual Resolve(long hwnd, string elementRef)
    {
        Visual root = RootForHwnd(hwnd) ?? throw new InvalidOperationException("no WPF surface");

        // 1) tree path "0/3/1"
        if (elementRef.Length > 0 && (char.IsDigit(elementRef[0]) || elementRef[0] == '/'))
        {
            var parts = elementRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Visual cur = root;
            // first part indexes the root itself ("0"); subsequent parts descend
            for (int k = 1; k < parts.Length; k++)
            {
                int idx = int.Parse(parts[k], CultureInfo.InvariantCulture);
                if (idx < 0 || idx >= VisualTreeHelper.GetChildrenCount(cur))
                    throw new InvalidOperationException($"path '{elementRef}' out of range at segment {k}");
                cur = VisualTreeHelper.GetChild(cur, idx) as Visual
                      ?? throw new InvalidOperationException($"path '{elementRef}' hit a non-visual at segment {k}");
            }
            return cur;
        }

        // 2) x:Name or AutomationId search
        var hit = FindBy(root, elementRef);
        return hit ?? throw new InvalidOperationException($"no element named/identified '{elementRef}'");
    }

    private static Visual? FindBy(Visual v, string key)
    {
        if (v is FrameworkElement fe &&
            (fe.Name == key || AutomationProperties.GetAutomationId(fe) == key))
            return v;
        int count = VisualTreeHelper.GetChildrenCount(v);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(v, i) is Visual cv && FindBy(cv, key) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Physical-pixel screen bounds of a resolved element (for
    /// element-scoped screenshots). Throws if the element is not rendered.</summary>
    public static UiMcp.Core.Geometry.PixelRect ElementScreenBounds(long hwnd, string elementRef)
    {
        var v = Resolve(hwnd, elementRef);
        var (x, y, w, h) = ScreenBounds(v, v as FrameworkElement);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"element '{elementRef}' has no rendered bounds");
        return new UiMcp.Core.Geometry.PixelRect(x, y, w, h);
    }

    private static AutomationPeer Peer(Visual v)
    {
        if (v is not UIElement uie) throw new InvalidOperationException($"{v.GetType().Name} is not interactable");
        return UIElementAutomationPeer.CreatePeerForElement(uie)
            ?? throw new InvalidOperationException("no automation peer for element");
    }

    public static ActionResult Invoke(long hwnd, string elementRef)
    {
        var peer = Peer(Resolve(hwnd, elementRef));
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv) { inv.Invoke(); return new(true, "invoked"); }
        if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog) { tog.Toggle(); return new(true, "toggled (no Invoke pattern)"); }
        if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel) { sel.Select(); return new(true, "selected (no Invoke pattern)"); }
        return new(false, "element exposes no Invoke/Toggle/SelectionItem pattern");
    }

    public static ActionResult SetValue(long hwnd, string elementRef, string value)
    {
        var peer = Peer(Resolve(hwnd, elementRef));
        if (peer.GetPattern(PatternInterface.Value) is IValueProvider vp)
        {
            if (vp.IsReadOnly) return new(false, "value is read-only");
            vp.SetValue(value);
            return new(true, $"value set to '{value}'");
        }
        return new(false, "element exposes no Value pattern");
    }

    public static ActionResult Toggle(long hwnd, string elementRef)
    {
        var peer = Peer(Resolve(hwnd, elementRef));
        if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog) { tog.Toggle(); return new(true, "toggled"); }
        return new(false, "element exposes no Toggle pattern");
    }

    public static ActionResult Select(long hwnd, string elementRef)
    {
        var peer = Peer(Resolve(hwnd, elementRef));
        if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel) { sel.Select(); return new(true, "selected"); }
        return new(false, "element exposes no SelectionItem pattern");
    }
}
