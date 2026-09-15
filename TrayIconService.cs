using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MDM
{
    public sealed class TrayRecentFile
    {
        public string DisplayName { get; init; } = "";
        public string FilePath { get; init; } = "";
        public ImageSource? Icon { get; init; }
    }

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
        private int _menuGeneration;
        private bool _openingMenu;

        public event Action? OpenRequested;
        public event Action? ExitRequested;
        public event Action? CheckUpdateRequested;
        public event Action? SettingsRequested;
        public Func<IReadOnlyList<TrayRecentFile>>? RecentFilesProvider { get; set; }

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
                    CloseMenu(immediate: true);
                    OpenRequested?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WmRButtonUp)
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(ShowDarkContextMenuSafe));
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private void ShowDarkContextMenuSafe()
        {
            try
            {
                if (_disposed) return;
                ShowDarkContextMenu();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray menu: {ex.Message}");
            }
        }

        private void ShowDarkContextMenu()
        {
            if (_openingMenu) return;
            _openingMenu = true;
            try
            {
                CloseMenu(immediate: true);
                GetCursorPos(out POINT pt);
                int gen = ++_menuGeneration;

                var openBtn = CreateMenuButton("MDM'yi aç", () =>
                {
                    CloseMenu(immediate: true);
                    OpenRequested?.Invoke();
                });
                var updateBtn = CreateMenuButton("Güncellemeleri kontrol et", () =>
                {
                    CloseMenu(immediate: true);
                    CheckUpdateRequested?.Invoke();
                });
                var settingsBtn = CreateMenuButton("Ayarlar", () =>
                {
                    CloseMenu(immediate: true);
                    SettingsRequested?.Invoke();
                });
                var exitBtn = CreateMenuButton("Çıkış", () =>
                {
                    CloseMenu(immediate: true);
                    ExitRequested?.Invoke();
                });

                var stack = new StackPanel { Margin = new Thickness(6) };
                stack.Children.Add(openBtn);
                stack.Children.Add(updateBtn);
                stack.Children.Add(settingsBtn);
                stack.Children.Add(CreateSeparator());
                stack.Children.Add(CreateFilesMenuItem());
                stack.Children.Add(CreateSeparator());
                stack.Children.Add(exitBtn);

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

                var win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = chrome,
                    Opacity = 0,
                    UseLayoutRounding = true
                };

                win.Deactivated += (_, _) =>
                {
                    if (_menuGeneration == gen)
                        CloseMenu(immediate: false);
                };

                _menuWindow = win;
                try { NativeWindowChrome.ApplyNoOuterShadow(win); } catch { /* ignore */ }

                // Önce ölç, ekran dışına taşmayacak şekilde yerleştir (tepsi altta → menü yukarı açılır)
                win.Show();
                win.UpdateLayout();
                PlaceMenuInWorkArea(win, pt.X, pt.Y);
                win.Activate();

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                win.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            finally
            {
                _openingMenu = false;
            }
        }

        private static void PlaceMenuInWorkArea(Window win, int screenX, int screenY)
        {
            // GetCursorPos fiziksel piksel; WPF DIP kullanır
            var dpi = VisualTreeHelper.GetDpi(win);
            double x = screenX / dpi.DpiScaleX;
            double y = screenY / dpi.DpiScaleY;

            var work = SystemParameters.WorkArea;
            double w = Math.Max(win.ActualWidth, 1);
            double h = Math.Max(win.ActualHeight, 1);

            // Varsayılan: imlecin sol-üstüne (tepsiden yukarı)
            double left = x - w + 12;
            double top = y - h + 8;

            if (left < work.Left + 4)
                left = work.Left + 4;
            if (left + w > work.Right - 4)
                left = Math.Max(work.Left + 4, work.Right - w - 4);

            if (top < work.Top + 4)
                top = work.Top + 4;
            if (top + h > work.Bottom - 4)
                top = Math.Max(work.Top + 4, work.Bottom - h - 4);

            win.Left = left;
            win.Top = top;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E))
            };
        }

        /// <summary>
        /// Dosyalar satırı her zaman görünür; hover’da solda (veya sağda) son indirmeler açılır.
        /// </summary>
        private FrameworkElement CreateFilesMenuItem()
        {
            var recent = SafeRecentFiles();

            var label = new TextBlock
            {
                Text = "Dosyalar",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var rowGrid = new Grid { Height = 32 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(chevron, 1);
            rowGrid.Children.Add(label);
            rowGrid.Children.Add(chevron);

            var hit = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Child = rowGrid,
                MinWidth = 196,
                Cursor = Cursors.Hand
            };

            Popup? flyout = null;
            Border? flyoutChrome = null;

            void EnsureFlyout()
            {
                if (flyout != null) return;

                var list = new StackPanel { Margin = new Thickness(6) };
                if (recent.Count == 0)
                {
                    list.Children.Add(new TextBlock
                    {
                        Text = "Henüz dosya yok",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                        Margin = new Thickness(10, 8, 10, 8)
                    });
                }
                else
                {
                    foreach (var file in recent.Take(8))
                        list.Children.Add(CreateFileRow(file));
                }

                flyoutChrome = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = list,
                    MinWidth = 200,
                    MaxHeight = Math.Min(280, SystemParameters.WorkArea.Height * 0.45),
                    SnapsToDevicePixels = true
                };

                // Uzun listede kaydır
                var scroller = new ScrollViewer
                {
                    Content = list,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = Math.Min(280, SystemParameters.WorkArea.Height * 0.45)
                };
                flyoutChrome.Child = scroller;

                flyout = new Popup
                {
                    Child = flyoutChrome,
                    Placement = PlacementMode.Left,
                    PlacementTarget = hit,
                    StaysOpen = true,
                    AllowsTransparency = true,
                    PopupAnimation = PopupAnimation.Fade,
                    HorizontalOffset = -4,
                    VerticalOffset = -4
                };

                flyoutChrome.MouseEnter += (_, _) =>
                {
                    label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
                    chevron.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
                    hit.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                };
                flyoutChrome.MouseLeave += (_, _) =>
                {
                    if (!hit.IsMouseOver)
                        HideFlyout();
                };
            }

            void ShowFlyout()
            {
                EnsureFlyout();
                if (flyout == null) return;

                // Ekranın solunda yer yoksa sağa aç
                try
                {
                    var origin = hit.PointToScreen(new Point(0, 0));
                    var dpi = VisualTreeHelper.GetDpi(hit);
                    double screenLeft = origin.X / dpi.DpiScaleX;
                    if (screenLeft < SystemParameters.WorkArea.Left + 220)
                    {
                        flyout.Placement = PlacementMode.Right;
                        flyout.HorizontalOffset = 4;
                    }
                    else
                    {
                        flyout.Placement = PlacementMode.Left;
                        flyout.HorizontalOffset = -4;
                    }
                }
                catch
                {
                    flyout.Placement = PlacementMode.Left;
                }

                flyout.IsOpen = true;
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
                chevron.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
                hit.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            }

            void HideFlyout()
            {
                if (flyout != null)
                    flyout.IsOpen = false;
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
                chevron.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                hit.Background = Brushes.Transparent;
            }

            hit.MouseEnter += (_, _) => ShowFlyout();
            hit.MouseLeave += (_, e) =>
            {
                // Flyout’a geçişte kapanmasın
                var pos = e.GetPosition(hit);
                if (pos.X < -2 || pos.Y < 0 || pos.Y > hit.ActualHeight)
                {
                    // kısa gecikme — popup’a mouse geçsin
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(180)
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        if (flyout is { IsMouseOver: true } || hit.IsMouseOver)
                            return;
                        HideFlyout();
                    };
                    timer.Start();
                }
            };

            return hit;
        }

        private IReadOnlyList<TrayRecentFile> SafeRecentFiles()
        {
            try
            {
                return RecentFilesProvider?.Invoke() ?? Array.Empty<TrayRecentFile>();
            }
            catch
            {
                return Array.Empty<TrayRecentFile>();
            }
        }

        private Button CreateFileRow(TrayRecentFile file)
        {
            var icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
                Source = file.Icon
            };

            var name = new TextBlock
            {
                Text = Truncate(file.DisplayName, 28),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(icon);
            row.Children.Add(name);

            var border = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Height = 30,
                Child = row
            };

            var btn = new Button
            {
                Content = border,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 1, 0, 1),
                MinWidth = 196,
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = file.FilePath
            };
            btn.Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            };

            border.MouseEnter += (_, _) =>
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                name.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = Brushes.Transparent;
                name.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            };
            btn.Click += (_, _) =>
            {
                CloseMenu(immediate: true);
                OpenRecentFile(file.FilePath);
            };
            return btn;
        }

        private static void OpenRecentFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { /* ignore */ }
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
                MinWidth = 196,
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            btn.Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            };

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

        private void CloseMenu(bool immediate = false)
        {
            try
            {
                var win = _menuWindow;
                if (win == null) return;
                _menuWindow = null;
                _menuGeneration++;

                if (immediate)
                {
                    try { win.Close(); } catch { /* ignore */ }
                    return;
                }

                var fade = new DoubleAnimation(win.Opacity, 0, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fade.Completed += (_, _) =>
                {
                    try { win.Close(); } catch { /* ignore */ }
                };
                try { win.BeginAnimation(UIElement.OpacityProperty, fade); }
                catch
                {
                    try { win.Close(); } catch { /* ignore */ }
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
            try { CloseMenu(immediate: true); } catch { /* ignore */ }
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
