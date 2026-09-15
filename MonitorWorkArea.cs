using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MDM
{
    /// <summary>Pencerenin bulunduğu monitörün görev çubuğu hariç çalışma alanı.</summary>
    internal static class MonitorWorkArea
    {
        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
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

        public static Rect Get(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        return new Rect(
                            mi.rcWork.Left,
                            mi.rcWork.Top,
                            Math.Max(1, mi.rcWork.Right - mi.rcWork.Left),
                            Math.Max(1, mi.rcWork.Bottom - mi.rcWork.Top));
                    }
                }
            }
            catch { /* fallback */ }

            var wa = SystemParameters.WorkArea;
            return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
        }
    }
}
