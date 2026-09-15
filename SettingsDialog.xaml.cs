using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MDM
{
    public partial class SettingsDialog : UserControl
    {
        private AppSettings _settings = new();
        private AppSettings _loadedSnapshot = new();
        private string _currentDefaultFolder = "";
        private CancellationTokenSource? _updateCts;
        private bool _updateBusy;
        private bool _capturingHotkey;
        private string _copyHotkey = "Ctrl+C";
        private bool _themePreviewBusy;

        public event Action? Cancelled;
        public event Action? Saved;
        /// <summary>Güncelleme uygulanacak; ana pencere ExitForUpdate çağırmalı.</summary>
        public event Action? UpdateApplying;

        public SettingsDialog()
        {
            InitializeComponent();
        }

        public void Load(AppSettings settings, string currentDefaultFolder, string? initialTab = null)
        {
            _settings = settings;
            _loadedSnapshot = CloneSettings(settings);
            _currentDefaultFolder = currentDefaultFolder;
            _capturingHotkey = false;

            TxtFolder.Text = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
                ? currentDefaultFolder
                : settings.DefaultDownloadFolder;
            ChkAutoStart.IsChecked = settings.AutoStart;
            ChkAutoStartMin.IsChecked = settings.AutoStartMinimized;
            ChkAutoStartMin.IsEnabled = settings.AutoStart;
            ChkDeleteFromDisk.IsChecked = settings.DeleteFilesFromDisk;
            ChkAutoExtract.IsChecked = settings.AutoExtractArchives;
            ChkDeleteArchive.IsChecked = settings.DeleteArchiveAfterExtract;
            ChkAutoFolders.IsChecked = settings.AutoCreateCategoryFolders;
            ChkNotifyTray.IsChecked = settings.NotifyOnTrayMinimize;
            ChkNotifyDone.IsChecked = settings.NotifyOnComplete;
            _themePreviewBusy = true;
            try
            {
                if (string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase))
                    ThemeLight.IsChecked = true;
                else
                    ThemeDark.IsChecked = true;
            }
            finally { _themePreviewBusy = false; }

            int brightness = settings.LightThemeBrightness <= 0 ? 100 : settings.LightThemeBrightness;
            SldLightBrightness.Value = Math.Clamp(brightness, 70, 100);
            TxtLightBrightnessValue.Text = $"{(int)SldLightBrightness.Value}%";
            UpdateLightBrightnessPanelVisibility();

            ChkCopyHotkey.IsChecked = settings.CopyFilesHotkeyEnabled;
            _copyHotkey = string.IsNullOrWhiteSpace(settings.CopyFilesHotkey) ? "Ctrl+C" : settings.CopyFilesHotkey.Trim();
            TxtHotkey.Text = _copyHotkey;
            ChkDeleteKey.IsChecked = settings.DeleteKeyShortcutsEnabled;
            UpdateHotkeyUiEnabled();

            ChkSchedule.IsChecked = settings.ScheduleEnabled;
            TxtSchedStart.Text = settings.ScheduleStartHour.ToString();
            TxtSchedEnd.Text = settings.ScheduleEndHour.ToString();
            TxtCrawlDepth.Text = settings.CrawlDepth.ToString();
            ChkRemoteLan.IsChecked = settings.RemoteApiLan;
            TxtApiToken.Text = settings.RemoteApiToken ?? "";
            ChkHttp3.IsChecked = settings.PreferHttp3;
            ChkAutoReconnect.IsChecked = settings.AutoReconnect;
            TxtSpeedLimit.Text = Math.Max(0, settings.SpeedLimitKBps).ToString();
            TxtMaxConcurrent.Text = Math.Max(0, settings.MaxConcurrentDownloads).ToString();
            TxtHttpChannels.Text = Math.Max(0, settings.HttpMaxChannels).ToString();
            TxtTorrentPort.Text = (settings.TorrentListenPort <= 0 ? 6881 : settings.TorrentListenPort).ToString();
            ChkTorrentDht.IsChecked = settings.TorrentDht;
            ChkTorrentLpd.IsChecked = settings.TorrentLocalPeers;
            ChkTorrentUpnp.IsChecked = settings.TorrentPortForward;
            ChkTorrentSeq.IsChecked = settings.TorrentSequential;
            TxtTorrentSeed.Text = settings.TorrentSeedRatio <= 0 ? "0" : settings.TorrentSeedRatio.ToString("0.##");
            ChkSkipDup.IsChecked = settings.SkipDuplicateUrls;
            TxtSkipExt.Text = settings.SkipExtensions ?? "";
            TxtSkipUrl.Text = settings.SkipUrlContains ?? "";
            TxtSkipDomains.Text = settings.SkipDomains ?? "";
            TxtSkipRegex.Text = settings.SkipUrlRegex ?? "";
            TxtSkipMime.Text = settings.SkipMimeContains ?? "";
            TxtSkipMinMb.Text = Math.Max(0, settings.SkipMinSizeMb).ToString();
            TxtSkipMaxMb.Text = Math.Max(0, settings.SkipMaxSizeMb).ToString();
            TxtRename.Text = string.IsNullOrWhiteSpace(settings.RenamePattern) ? "{name}{ext}" : settings.RenamePattern;

            TxtExtPath.Text = ExtensionInstaller.InstallRoot;
            RefreshBrowserStatus();
            string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            TxtVersion.Text = $"Sürüm {ver}";
            TxtUpdateCurrent.Text = $"Yüklü sürüm: v{UpdateService.CurrentVersionText}";
            if (!_updateBusy)
                TxtUpdateStatus.Text = "";

            SelectTab(initialTab ?? "general");
            ApplyThemeSurface(string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase));
        }

        public void SelectTab(string tab)
        {
            switch ((tab ?? "").Trim().ToLowerInvariant())
            {
                case "notifications":
                case "bildirimler":
                    NavNotifications.IsChecked = true;
                    break;
                case "theme":
                case "tema":
                case "gorunum":
                case "görünüm":
                    NavTheme.IsChecked = true;
                    break;
                case "extension":
                case "eklenti":
                    NavExtension.IsChecked = true;
                    break;
                case "keyboard":
                case "klavye":
                case "kisayol":
                case "kısayol":
                    NavKeyboard.IsChecked = true;
                    break;
                case "advanced":
                case "gelismis":
                case "gelişmiş":
                    NavAdvanced.IsChecked = true;
                    break;
                case "guncelleme":
                case "güncelleme":
                    NavUpdate.IsChecked = true;
                    break;
                case "about":
                case "hakkinda":
                    NavAbout.IsChecked = true;
                    break;
                default:
                    NavGeneral.IsChecked = true;
                    break;
            }
            ApplyNavVisibility();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e) => ApplyNavVisibility();

        private void ApplyNavVisibility()
        {
            if (PanelGeneral == null) return;
            if (PanelAdvanced == null) return;
            PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelNotifications.Visibility = NavNotifications.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelTheme.Visibility = NavTheme.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelExtension.Visibility = NavExtension.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelKeyboard.Visibility = NavKeyboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAdvanced.Visibility = NavAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelUpdate.Visibility = NavUpdate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavExtension.IsChecked == true)
                RefreshBrowserStatus();
            if (NavKeyboard.IsChecked != true)
                StopHotkeyCapture();
        }

        private void ThemePick_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _themePreviewBusy) return;
            if (ThemeLight == null || ThemeDark == null) return;
            // Anında önizleme — Kaydet kalıcılar; İptal ayarı geri yükler
            string preview = ThemeLight.IsChecked == true ? "Light" : "Dark";
            if (ThemeLight.IsChecked == true && SldLightBrightness != null)
                ThemeService.PreviewLightBrightness = (int)SldLightBrightness.Value;
            else
                ThemeService.PreviewLightBrightness = null;
            ThemeService.Apply(preview);
            ApplyThemeSurface(ThemeLight.IsChecked == true);
            UpdateLightBrightnessPanelVisibility();
        }

        private void LightBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _themePreviewBusy) return;
            if (SldLightBrightness == null || ThemeLight?.IsChecked != true) return;
            int v = (int)SldLightBrightness.Value;
            if (TxtLightBrightnessValue != null)
                TxtLightBrightnessValue.Text = $"{v}%";
            ThemeService.PreviewLightBrightness = v;
            ThemeService.Apply("Light");
            ApplyThemeSurface(true);
        }

        private void UpdateLightBrightnessPanelVisibility()
        {
            if (LightBrightnessPanel == null) return;
            bool light = ThemeLight?.IsChecked == true;
            LightBrightnessPanel.Visibility = light ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Ayarlar kartını açık/koyu temaya uyarlar — yalnızca kaynak fırçaları; görsel ağaçta statik renk yazılmaz.</summary>
        public void ApplyThemeSurface(bool light)
        {
            _themePreviewBusy = true;
            try
            {
                var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1B, 0x1B, 0x1B);
                var header = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x1F, 0x1F, 0x1F);
                var nav = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x16, 0x16, 0x16);
                var footer = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x1A, 0x1A, 0x1A);
                var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
                var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0);
                var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);

                if (SettingsCard != null)
                {
                    SettingsCard.Background = Solid(card);
                    SettingsCard.BorderBrush = Solid(border);
                }
                if (SettingsHeader != null)
                    SettingsHeader.Background = Solid(header);
                if (SettingsNavPane != null)
                {
                    SettingsNavPane.Background = Solid(nav);
                    SettingsNavPane.BorderBrush = Solid(border);
                }
                if (SettingsFooter != null)
                {
                    SettingsFooter.Background = Solid(footer);
                    SettingsFooter.BorderBrush = Solid(border);
                }
                if (SettingsTitle != null)
                    SettingsTitle.Foreground = Solid(text);

                foreach (var rb in new[] { NavGeneral, NavNotifications, NavTheme, NavExtension,
                             NavKeyboard, NavAdvanced, NavUpdate, NavAbout })
                {
                    if (rb == null) continue;
                    rb.ClearValue(Control.ForegroundProperty);
                    rb.ClearValue(Control.BackgroundProperty);
                    rb.ClearValue(Control.FontWeightProperty);
                }

                SetLocalBrush("NavTextBrush", light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0xAA, 0xAA, 0xAA));
                SetLocalBrush("NavHoverBrush", Color.FromRgb(0xFF, 0x6B, 0x00));
                SetLocalBrush("NavHoverBgBrush", light ? Color.FromArgb(0x18, 0xFF, 0x6B, 0x00) : Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                SetLocalBrush("NavSelectedBgBrush", ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x25, 0x25, 0x25));
                SetLocalBrush("SoftBtnBgBrush", ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x25, 0x25, 0x25));
                SetLocalBrush("SoftBtnHoverBgBrush", ThemeService.Surface(light, 0xE0, 0xE0, 0xE4, 0x33, 0x33, 0x33));
                SetLocalBrush("SoftBtnFgBrush", light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xDD, 0xDD, 0xDD));
                SetLocalBrush("SettingsPanelBgBrush", ThemeService.Surface(light, 0xF7, 0xF7, 0xF9, 0x14, 0x14, 0x14));
                SetLocalBrush("SettingsPanelBorderBrush", ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x2A, 0x2A, 0x2A));
                SetLocalBrush("SettingsMutedBrush", light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x77, 0x77, 0x77));
                SetLocalBrush("SettingsLabelBrush", light ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xBB, 0xBB, 0xBB));
                SetLocalBrush("SettingsBodyBrush", light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0));
                SetLocalBrush("SettingsInputBgBrush", input);
                // Arama kutusu ile aynı koyu çerçeve — sistem mavisi yok
                SetLocalBrush("SettingsInputBorderBrush", light
                    ? ThemeService.Surface(light, 0xD0, 0xD0, 0xD6, 0x2E, 0x2E, 0x2E)
                    : Color.FromRgb(0x2E, 0x2E, 0x2E));
                SetLocalBrush("BrowserCardBgBrush", ThemeService.Surface(light, 0xF7, 0xF7, 0xF9, 0x14, 0x14, 0x14));
                SetLocalBrush("BrowserCardBorderBrush", ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x2A, 0x2A, 0x2A));
                SetLocalBrush("BrowserCardTextBrush", light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0));
                SetLocalBrush("BrowserCardMutedBrush", light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x88, 0x88, 0x88));
                SetLocalBrush("BrowserPillIdleBgBrush", ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x2A, 0x2A, 0x2A));
                SetLocalBrush("BrowserPillIdleFgBrush", light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x99, 0x99, 0x99));
                SetLocalBrush("BrowserPillActiveBgBrush", light ? Color.FromRgb(0xE8, 0xF5, 0xE9) : Color.FromRgb(0x1B, 0x3A, 0x24));
                SetLocalBrush("BrowserPillActiveFgBrush", light ? Color.FromRgb(0x2E, 0x7D, 0x32) : Color.FromRgb(0x8B, 0xC3, 0x4A));

                if (NavExtension?.IsChecked == true)
                    RefreshBrowserStatus();
            }
            finally
            {
                _themePreviewBusy = false;
            }
        }

        private void SetLocalBrush(string key, Color color)
        {
            var brush = Solid(color);
            if (Resources.Contains(key))
                Resources[key] = brush;
            else
                Resources.Add(key, brush);
        }

        private static SolidColorBrush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void ChkCopyHotkey_Changed(object sender, RoutedEventArgs e) => UpdateHotkeyUiEnabled();

        private void UpdateHotkeyUiEnabled()
        {
            bool on = ChkCopyHotkey.IsChecked == true;
            HotkeyBox.IsEnabled = on;
            HotkeyBox.Opacity = on ? 1 : 0.45;
            if (!on) StopHotkeyCapture();
        }

        private void BtnCaptureHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCopyHotkey.IsChecked != true) return;
            _capturingHotkey = true;
            TxtHotkey.Text = "Tuşlara basın…";
            TxtHotkeyHint.Text = "Esc iptal eder. Örn. Ctrl+C veya Ctrl+Shift+C";
            HotkeyBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            Keyboard.Focus(HotkeyBox);
        }

        private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            StopHotkeyCapture();
            _copyHotkey = "Ctrl+C";
            TxtHotkey.Text = _copyHotkey;
            TxtHotkeyHint.Text = "Varsayılan kısayol geri yüklendi.";
        }

        private void HotkeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ChkCopyHotkey.IsChecked == true)
                BtnCaptureHotkey_Click(sender, e);
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) => ProcessHotkeyCapture(e);

        private void SettingsDialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_capturingHotkey)
                ProcessHotkeyCapture(e);
        }

        private void ProcessHotkeyCapture(KeyEventArgs e)
        {
            if (!_capturingHotkey) return;
            e.Handled = true;

            Key key = HotkeyParser.NormalizeKey(e);
            if (HotkeyParser.IsModifierKey(key))
                return;

            if (key == Key.Escape)
            {
                StopHotkeyCapture();
                TxtHotkey.Text = _copyHotkey;
                TxtHotkeyHint.Text = "İptal edildi.";
                return;
            }

            var mods = Keyboard.Modifiers;
            bool allowNoModifier = key is >= Key.F1 and <= Key.F12
                or Key.Delete or Key.Insert or Key.Home or Key.End
                or Key.PageUp or Key.PageDown;

            if (mods == ModifierKeys.None && !allowNoModifier)
            {
                TxtHotkeyHint.Text = "En az bir değiştirici (Ctrl / Alt / Shift) kullanın.";
                return;
            }

            _copyHotkey = HotkeyParser.Format(key, mods);
            TxtHotkey.Text = _copyHotkey;
            StopHotkeyCapture();
            TxtHotkeyHint.Text = "Kısayol güncellendi. Kaydet'e basın.";
        }

        private void StopHotkeyCapture()
        {
            _capturingHotkey = false;
            try
            {
                HotkeyBox.BorderBrush = TryFindResource("SettingsInputBorderBrush") as Brush
                    ?? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            }
            catch { /* ignore */ }
        }

        private async void BtnRefreshBrowsers_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserStatusList == null) return;
            BtnRefreshBrowsers.IsEnabled = false;
            try
            {
                BrowserStatusList.Children.Clear();
                BrowserStatusList.Children.Add(new TextBlock
                {
                    Text = "Kontrol ediliyor…",
                    Foreground = TryFindResource("SettingsMutedBrush") as Brush
                                 ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                // Edge prefs yazımı gecikebilir — birkaç kez oku
                IReadOnlyList<BrowserExtensionStatus>? last = null;
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0)
                        await Task.Delay(350);
                    last = BrowserExtensionProbe.ProbeAll();
                }

                BrowserStatusList.Children.Clear();
                foreach (var b in last!)
                    BrowserStatusList.Children.Add(BuildBrowserCard(b));
            }
            finally
            {
                BtnRefreshBrowsers.IsEnabled = true;
            }
        }

        private void RefreshBrowserStatus()
        {
            if (BrowserStatusList == null) return;
            BrowserStatusList.Children.Clear();
            foreach (var b in BrowserExtensionProbe.ProbeAll())
                BrowserStatusList.Children.Add(BuildBrowserCard(b));
        }

        private Border BuildBrowserCard(BrowserExtensionStatus b)
        {
            var accent = (Color)ColorConverter.ConvertFromString(b.AccentHex);
            UIElement iconChild;
            var iconSrc = BrowserIconHelper.ForBrowser(b.Id);
            if (iconSrc != null)
            {
                iconChild = new Image
                {
                    Source = iconSrc,
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(iconChild, BitmapScalingMode.HighQuality);
            }
            else
            {
                iconChild = new Image
                {
                    Source = BrowserIconHelper.BrandFallback(b.Id),
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var icon = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                Child = iconChild
            };

            Brush Res(string key, Color fallback)
            {
                if (TryFindResource(key) is SolidColorBrush sb) return sb;
                return new SolidColorBrush(fallback);
            }

            var statusPill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = b.ExtensionActive
                    ? Res("BrowserPillActiveBgBrush", Color.FromRgb(0x1B, 0x3A, 0x24))
                    : Res("BrowserPillIdleBgBrush", Color.FromRgb(0x2A, 0x2A, 0x2A)),
                Child = new TextBlock
                {
                    Text = !b.BrowserInstalled ? "Yok" : (b.ExtensionActive ? "Aktif" : "Devre dışı"),
                    Foreground = b.ExtensionActive
                        ? Res("BrowserPillActiveFgBrush", Color.FromRgb(0x8B, 0xC3, 0x4A))
                        : Res("BrowserPillIdleFgBrush", Color.FromRgb(0x99, 0x99, 0x99)),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                }
            };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = b.Name,
                Foreground = Res("BrowserCardTextBrush", Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            texts.Children.Add(new TextBlock
            {
                Text = b.Detail,
                Foreground = Res("BrowserCardMutedBrush", Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(statusPill, 2);
            row.Children.Add(icon);
            row.Children.Add(texts);
            row.Children.Add(statusPill);

            return new Border
            {
                Background = Res("BrowserCardBgBrush", Color.FromRgb(0x14, 0x14, 0x14)),
                BorderBrush = b.ExtensionActive
                    ? new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B))
                    : Res("BrowserCardBorderBrush", Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = row
            };
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            ChkAutoStartMin.IsEnabled = ChkAutoStart.IsChecked == true;
        }

        private Window? OwnerWindow => Window.GetWindow(this);

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Varsayılan indirme klasörü" };
            if (!string.IsNullOrWhiteSpace(TxtFolder.Text) && Directory.Exists(TxtFolder.Text))
                dialog.InitialDirectory = TxtFolder.Text;
            if (dialog.ShowDialog() == true)
                TxtFolder.Text = dialog.FolderName;
        }

        private void BtnReinstallExt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExtensionInstaller.EnsureInstalled();
                InfoDialog.Show(OwnerWindow, "Eklenti", "Hazır.",
                    "Tarayıcıdaki eklenti bu klasörü kullanıyor:\n" + ExtensionInstaller.InstallRoot);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow, "Eklenti", "İşlem başarısız.", ex.Message);
            }
        }

        private void BtnOpenExtFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(ExtensionInstaller.InstallRoot);
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExtensionInstaller.InstallRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow, "Klasör", "Açılamadı.", ex.Message);
            }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            BtnCheckUpdate.IsEnabled = false;
            SetUpdateStatus("Güncelleniyor…", "#FF6B00");
            _updateCts = new CancellationTokenSource();
            var progress = new Progress<string>(_ => SetUpdateStatus("Güncelleniyor…", "#FF6B00"));

            try
            {
                var result = await UpdateService.CheckAndApplyAsync(progress, _updateCts.Token);

                if (result.Applying)
                {
                    SetUpdateStatus("Güncelleniyor…", "#FF6B00");
                    UpdateApplying?.Invoke();
                    return;
                }

                if (result.HadError)
                {
                    SetUpdateStatus(result.Message, "#E07070");
                    InfoDialog.Show(OwnerWindow, "Güncelleme", "Denetim başarısız.", result.Message);
                }
                else if (result.IsUpToDate)
                {
                    SetUpdateStatus("Güncelsiniz", "#8BC34A");
                }
                else
                {
                    SetUpdateStatus(result.Message, "#E0E0E0");
                }
            }
            catch (Exception ex)
            {
                SetUpdateStatus(ex.Message, "#E07070");
                InfoDialog.Show(OwnerWindow, "Güncelleme", "Denetim başarısız.", ex.Message);
            }
            finally
            {
                _updateBusy = false;
                BtnCheckUpdate.IsEnabled = true;
                _updateCts?.Dispose();
                _updateCts = null;
            }
        }

        private void SetUpdateStatus(string text, string hex)
        {
            TxtUpdateStatus.Text = text;
            try
            {
                TxtUpdateStatus.Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hex));
            }
            catch { /* ignore */ }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e) => TrySave();

        public bool HasUnsavedChanges() =>
            !SettingsEqual(_loadedSnapshot, CaptureCurrentSettings());

        public bool TrySave()
        {
            string folder = (TxtFolder.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                InfoDialog.Show(OwnerWindow, "Klasör", "Geçerli bir indirme klasörü girin.");
                return false;
            }

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow, "Klasör", "Klasör oluşturulamadı.", ex.Message);
                return false;
            }

            _settings = CaptureCurrentSettings();
            AppSettingsStore.Save(_settings);
            _loadedSnapshot = CloneSettings(_settings);

            if (_settings.AutoStart)
                AutoStartHelper.Enable(_settings.AutoStartMinimized);
            else
                AutoStartHelper.Disable();

            Saved?.Invoke();
            return true;
        }

        private AppSettings CaptureCurrentSettings()
        {
            var s = CloneSettings(_settings);
            string folder = (TxtFolder.Text ?? "").Trim();
            s.DefaultDownloadFolder = folder;
            s.AutoStart = ChkAutoStart.IsChecked == true;
            s.AutoStartMinimized = ChkAutoStartMin.IsChecked == true;
            s.DeleteFilesFromDisk = ChkDeleteFromDisk.IsChecked == true;
            s.AutoExtractArchives = ChkAutoExtract.IsChecked == true;
            s.DeleteArchiveAfterExtract = ChkDeleteArchive.IsChecked == true;
            s.AutoCreateCategoryFolders = ChkAutoFolders.IsChecked == true;
            s.NotifyOnTrayMinimize = ChkNotifyTray.IsChecked == true;
            s.NotifyOnComplete = ChkNotifyDone.IsChecked == true;
            s.Theme = ThemeLight.IsChecked == true ? "Light" : "Dark";
            s.LightThemeBrightness = SldLightBrightness != null ? (int)SldLightBrightness.Value : 100;
            s.CopyFilesHotkeyEnabled = ChkCopyHotkey.IsChecked == true;
            s.CopyFilesHotkey = string.IsNullOrWhiteSpace(_copyHotkey) ? "Ctrl+C" : _copyHotkey;
            s.DeleteKeyShortcutsEnabled = ChkDeleteKey.IsChecked == true;
            s.ScheduleEnabled = ChkSchedule.IsChecked == true;
            _ = int.TryParse(TxtSchedStart.Text, out int sh);
            _ = int.TryParse(TxtSchedEnd.Text, out int eh);
            s.ScheduleStartHour = Math.Clamp(sh, 0, 23);
            s.ScheduleEndHour = Math.Clamp(eh, 0, 23);
            _ = int.TryParse(TxtCrawlDepth.Text, out int depth);
            s.CrawlDepth = Math.Clamp(depth, 0, 3);
            s.RemoteApiLan = ChkRemoteLan.IsChecked == true;
            s.RemoteApiToken = TxtApiToken.Text?.Trim() ?? "";
            s.PreferHttp3 = ChkHttp3.IsChecked == true;
            s.AutoReconnect = ChkAutoReconnect.IsChecked == true;
            _ = int.TryParse(TxtSpeedLimit.Text, out int speedKb);
            s.SpeedLimitKBps = Math.Clamp(speedKb, 0, 1_000_000);
            _ = int.TryParse(TxtMaxConcurrent.Text, out int maxJobs);
            s.MaxConcurrentDownloads = Math.Clamp(maxJobs, 0, 50);
            _ = int.TryParse(TxtHttpChannels.Text, out int httpCh);
            s.HttpMaxChannels = Math.Clamp(httpCh, 0, ChannelBudget.MaxPerJob);
            _ = int.TryParse(TxtTorrentPort.Text, out int tport);
            s.TorrentListenPort = tport <= 0 ? 6881 : Math.Clamp(tport, 1, 65535);
            s.TorrentDht = ChkTorrentDht.IsChecked == true;
            s.TorrentLocalPeers = ChkTorrentLpd.IsChecked == true;
            s.TorrentPortForward = ChkTorrentUpnp.IsChecked == true;
            s.TorrentSequential = ChkTorrentSeq.IsChecked == true;
            _ = double.TryParse(TxtTorrentSeed.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seed);
            s.TorrentSeedRatio = Math.Clamp(seed, 0, 100);
            s.SkipDuplicateUrls = ChkSkipDup.IsChecked == true;
            s.SkipExtensions = TxtSkipExt.Text?.Trim() ?? "";
            s.SkipUrlContains = TxtSkipUrl.Text?.Trim() ?? "";
            s.SkipDomains = TxtSkipDomains.Text?.Trim() ?? "";
            s.SkipUrlRegex = TxtSkipRegex.Text?.Trim() ?? "";
            s.SkipMimeContains = TxtSkipMime.Text?.Trim() ?? "";
            _ = int.TryParse(TxtSkipMinMb.Text, out int minMb);
            _ = int.TryParse(TxtSkipMaxMb.Text, out int maxMb);
            s.SkipMinSizeMb = Math.Max(0, minMb);
            s.SkipMaxSizeMb = Math.Max(0, maxMb);
            s.RenamePattern = string.IsNullOrWhiteSpace(TxtRename.Text) ? "{name}{ext}" : TxtRename.Text.Trim();
            return s;
        }

        private static AppSettings CloneSettings(AppSettings source)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        private static bool SettingsEqual(AppSettings a, AppSettings b) =>
            JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    }
}
