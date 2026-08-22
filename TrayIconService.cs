using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DownloadMuck
{
    /// <summary>Win32 NotifyIcon — WinForms bağımlılığı olmadan gizli simgeler.</summary>
    public sealed class TrayIconService : IDisposable
    {
        private const int WmTrayCallback = 0x8001; // WM_APP + 1
        private const uint NimAdd = 0x00000000;
        private const uint NimModify = 0x00000001;
        private const uint NimDelete = 0x00000002;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint NifInfo = 0x00000010;
        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDblClk = 0x0203;
        private const int WmRButtonUp = 0x0205;
        private const int WmCommand = 0x0111;
        private const uint TpmLeftAlign = 0x0000;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmReturnCmd = 0x0100;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const int IdOpen = 1001;
        private const int IdExit = 1002;

        private readonly HwndSource _source;
        private NOTIFYICONDATA _data;
        private bool _added;
        private bool _disposed;
        private bool _balloonShown;
        private IntPtr _iconHandle;
        private bool _iconFromExe;
        private bool _ownsIcon;

        public event Action? OpenRequested;
        public event Action? ExitRequested;

        public TrayIconService()
        {
            var parms = new HwndSourceParameters("MDM_Tray")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(parms);
            _source.AddHook(WndProc);

            _iconHandle = LoadAppIcon();
            _ownsIcon = _iconHandle != IntPtr.Zero && _iconFromExe;
            _data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _source.Handle,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = WmTrayCallback,
                hIcon = _iconHandle,
                szTip = "MDM — Muck Download Manager",
                szInfo = "",
                szInfoTitle = ""
            };

            _added = Shell_NotifyIcon(NimAdd, ref _data);
        }

        public void ShowHiddenTipOnce()
        {
            if (_balloonShown || !_added || _disposed) return;
            _balloonShown = true;
            try
            {
                _data.uFlags = NifMessage | NifIcon | NifTip | NifInfo;
                _data.szInfoTitle = "MDM arka planda";
                _data.szInfo = "Gizli simgelerde çalışmaya devam ediyor. Çıkmak için tepsi menüsünden «Çıkış».";
                _data.dwInfoFlags = 1; // NIIF_INFO
                Shell_NotifyIcon(NimModify, ref _data);
                _data.uFlags = NifMessage | NifIcon | NifTip;
            }
            catch { /* ignore */ }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmTrayCallback)
            {
                int mouseMsg = lParam.ToInt32() & 0xFFFF;
                if (mouseMsg == WmLButtonUp || mouseMsg == WmLButtonDblClk)
                {
                    OpenRequested?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WmRButtonUp)
                {
                    ShowContextMenu();
                    handled = true;
                }
            }
            else if (msg == WmCommand)
            {
                int id = wParam.ToInt32() & 0xFFFF;
                if (id == IdOpen) OpenRequested?.Invoke();
                else if (id == IdExit) ExitRequested?.Invoke();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            AppendMenu(menu, MfString, (UIntPtr)IdOpen, "MDM'yi aç");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)IdExit, "Çıkış");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_source.Handle);
            uint cmd = (uint)TrackPopupMenuEx(
                menu,
                TpmLeftAlign | TpmRightButton | TpmReturnCmd,
                pt.X, pt.Y,
                _source.Handle,
                IntPtr.Zero);
            DestroyMenu(menu);

            if (cmd == IdOpen) OpenRequested?.Invoke();
            else if (cmd == IdExit) ExitRequested?.Invoke();
        }

        private IntPtr LoadAppIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
                    if (ExtractIconEx(exe, 0, ref large, ref small, 1) > 0)
                    {
                        if (large != IntPtr.Zero && small != IntPtr.Zero)
                        {
                            DestroyIcon(large);
                            _iconFromExe = true;
                            return small;
                        }
                        if (small != IntPtr.Zero)
                        {
                            _iconFromExe = true;
                            return small;
                        }
                        if (large != IntPtr.Zero)
                        {
                            _iconFromExe = true;
                            return large;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            _iconFromExe = false;
            return LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_added)
                    Shell_NotifyIcon(NimDelete, ref _data);
            }
            catch { /* ignore */ }

            try
            {
                if (_ownsIcon && _iconHandle != IntPtr.Zero)
                    DestroyIcon(_iconHandle);
            }
            catch { /* ignore */ }

            try { _source.RemoveHook(WndProc); _source.Dispose(); }
            catch { /* ignore */ }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, ref IntPtr phiconLarge, ref IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    }
}
