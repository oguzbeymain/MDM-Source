using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DownloadMuck
{
    /// <summary>
    /// DWM native min/max/restore için: work-area maximize, köşeler, transitions açık.
    /// SetWindowRgn KULLANILMAZ — DWM animasyonunu bozar.
    /// </summary>
    internal static class NativeWindowChrome
    {
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MonitorDefaultToNearest = 2;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmWcpRound = 2;
        private const int DwmwaTransitionsForceDisabled = 3;

        private const int GwlStyle = -16;
        private const int WsCaption = 0x00C00000;
        private const int WsThickFrame = 0x00040000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int WsSysMenu = 0x00080000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        public static void ApplyDwmNativeChrome(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int transitionsDisabled = 0;
                DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref transitionsDisabled, sizeof(int));

                int style = GetWindowLong(hwnd, GwlStyle);
                style |= WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu;
                SetWindowLong(hwnd, GwlStyle, style);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);

                int pref = DwmWcpRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            }
            catch { /* ignore */ }
        }

        private static void WmGetMinMaxInfo(Window window, IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (mon == IntPtr.Zero) return;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;

            RECT work = mi.rcWork;
            RECT monitor = mi.rcMonitor;

            mmi.ptMaxPosition.X = Math.Abs(work.Left - monitor.Left);
            mmi.ptMaxPosition.Y = Math.Abs(work.Top - monitor.Top);
            mmi.ptMaxSize.X = Math.Abs(work.Right - work.Left);
            mmi.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

            // WPF MinWidth/MinHeight — DPI ile fiziksel piksele çevir
            // (handled=true ile varsayılan min track silindiği için zorunlu)
            double dpiX = 96, dpiY = 96;
            try
            {
                var src = PresentationSource.FromVisual(window);
                if (src?.CompositionTarget != null)
                {
                    Matrix m = src.CompositionTarget.TransformToDevice;
                    dpiX = 96 * m.M11;
                    dpiY = 96 * m.M22;
                }
            }
            catch { /* ignore */ }

            double minW = window.MinWidth;
            double minH = window.MinHeight;
            if (double.IsNaN(minW) || minW <= 0) minW = 880;
            if (double.IsNaN(minH) || minH <= 0) minH = 480;

            mmi.ptMinTrackSize.X = Math.Max(200, (int)Math.Ceiling(minW * dpiX / 96.0));
            mmi.ptMinTrackSize.Y = Math.Max(200, (int)Math.Ceiling(minH * dpiY / 96.0));

            // Min, max'tan büyük olmasın
            if (mmi.ptMinTrackSize.X > mmi.ptMaxTrackSize.X)
                mmi.ptMinTrackSize.X = mmi.ptMaxTrackSize.X;
            if (mmi.ptMinTrackSize.Y > mmi.ptMaxTrackSize.Y)
                mmi.ptMinTrackSize.Y = mmi.ptMaxTrackSize.Y;

            Marshal.StructureToPtr(mmi, lParam, false);
        }

        public static void Attach(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_GETMINMAXINFO)
                    {
                        WmGetMinMaxInfo(window, h, lParam);
                        handled = true;
                    }
                    return IntPtr.Zero;
                });

                ApplyDwmNativeChrome(window);
            };
        }
    }
}
