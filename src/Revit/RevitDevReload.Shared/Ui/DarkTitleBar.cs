using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RevitDevReload.Ui
{
    /// <summary>
    /// Paints a WPF window's OS-drawn title bar — the non-client area that DWM
    /// owns and WPF cannot style — to match the dark <c>Theme.xaml</c> palette.
    ///
    /// <para>The immersive-dark-mode flag (attribute 20) is honoured on Windows
    /// 10 20H1+ and turns the bar near-black. The explicit caption / text /
    /// border colour attributes (34-36) are Windows 11 (build 22000+) only; on
    /// older builds DWM returns a failure HRESULT for those and the bar simply
    /// stays at the immersive-dark default. That is the OS declining an
    /// unsupported attribute, not a substituted code path.</para>
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Applies the themed title bar to <paramref name="window"/>. If the
        /// window's HWND does not exist yet, the work is deferred until
        /// <see cref="Window.SourceInitialized"/> fires.
        /// </summary>
        public static void Apply(Window window, Color caption, Color text, Color border)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                ApplyToHandle(handle, caption, text, border);
                return;
            }

            window.SourceInitialized += (_, _) =>
                ApplyToHandle(new WindowInteropHelper(window).Handle, caption, text, border);
        }

        private static void ApplyToHandle(IntPtr hwnd, Color caption, Color text, Color border)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

            int captionRef = ToColorRef(caption);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionRef, sizeof(int));

            int textRef = ToColorRef(text);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textRef, sizeof(int));

            int borderRef = ToColorRef(border);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderRef, sizeof(int));
        }

        // DWM expects a COLORREF: 0x00BBGGRR.
        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }
}
