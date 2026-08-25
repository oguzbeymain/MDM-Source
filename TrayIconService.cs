using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DownloadMuck
{
    /// <summary>Win32 NotifyIcon + koyu WPF tepsi menüsü.</summary>
    public sealed class TrayIconService : IDisposable
    {
        private const int WmTrayCallback = 0x8001;
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

        private readonly HwndSource _source;
        private NOTIFYICONDATA _data;
        private bool _added;
        private bool _disposed;
        private bool _balloonShown;
        private IntPtr _iconHandle;
        private bool _iconFromExe;
        private bool _ownsIcon;
        private Window? _menuWindow;

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
            if (!AppSettingsStore.Load().NotifyOnTrayMinimize)
                return;
            _balloonShown = true;
            ShowBalloon("MDM arka planda",
                "Gizli simgelerde çalışmaya devam ediyor. Çıkmak için tepsi menüsünden «Çıkış».");
        }

        public void ShowBalloon(string title, string message)
        {
            if (!_added || _disposed) return;
            try
            {
                _data.uFlags = NifMessage | NifIcon | NifTip | NifInfo;
                _data.szInfoTitle = Truncate(title, 63);
                _data.szInfo = Truncate(message, 255);
                _data.dwInfoFlags = 1;
                Shell_NotifyIcon(NimModify, ref _data);
                _data.uFlags = NifMessage | NifIcon | NifTip;
            }
            catch { /* ignore */ }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text[..max];
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmTrayCallback)
            {
                int mouseMsg = lParam.ToInt32() & 0xFFFF;
                if (mouseMsg == WmLButtonUp || mouseMsg == WmLButtonDblClk)
                {
                    CloseMenu();
                    OpenRequested?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WmRButtonUp)
                {
                    Application.Current?.Dispatcher.BeginInvoke(ShowDarkContextMenu);
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private void ShowDarkContextMenu()
        {
            CloseMenu();
            GetCursorPos(out POINT pt);

            var openBtn = CreateMenuButton("MDM'yi aç", () =>
            {
                CloseMenu();
                OpenRequested?.Invoke();
            });
            var exitBtn = CreateMenuButton("Çıkış", () =>
            {
                CloseMenu();
                ExitRequested?.Invoke();
            });

            var stack = new StackPanel { Margin = new Thickness(6) };
            stack.Children.Add(openBtn);
            stack.Children.Add(exitBtn);

            // Gölge ayrı — metin bulanık olmasın
            var shadow = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(4),
                Opacity = 0.01,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.5,
                    Color = Colors.Black
                }
            };

            var chrome = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = stack,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };

            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(chrome);

            _menuWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = root,
                Left = pt.X - 8,
                Top = pt.Y - 8,
                Opacity = 0,
                UseLayoutRounding = true
            };

            _menuWindow.Deactivated += (_, _) => CloseMenu();
            _menuWindow.Show();
            _menuWindow.Activate();

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _menuWindow.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static Button CreateMenuButton(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var border = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Height = 32,
                Child = label
            };

            var btn = new Button
            {
                Content = border,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 1, 0, 1),
                MinWidth = 148,
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            btn.Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            };

            // Anlık hover (animasyonsuz) — sadece menü açılış fade kalır
            border.MouseEnter += (_, _) =>
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = Brushes.Transparent;
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }

        private void CloseMenu()
        {
            try
            {
                if (_menuWindow != null)
                {
                    var win = _menuWindow;
                    _menuWindow = null;
                    var fade = new DoubleAnimation(win.Opacity, 0, TimeSpan.FromMilliseconds(90))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };
                    fade.Completed += (_, _) =>
                    {
                        try { win.Close(); } catch { /* ignore */ }
                    };
                    win.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            catch { /* ignore */ }
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
            return LoadIcon(IntPtr.Zero, (IntPtr)32512);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_menuWindow != null)
                {
                    _menuWindow.Close();
                    _menuWindow = null;
                }
            }
            catch { /* ignore */ }
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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
