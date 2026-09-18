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
            if (ChkGameMode != null)
                ChkGameMode.IsChecked = settings.GameModeEnabled;
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
            if (ChkSidebarCollapse != null)
            {
                _themePreviewBusy = true;
                try { ChkSidebarCollapse.IsChecked = settings.SidebarCollapseEnabled; }
                finally { _themePreviewBusy = false; }
            }

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
            if (ChkConfirmRepeat != null)
                ChkConfirmRepeat.IsChecked = settings.ConfirmRepeatDownloads;
            if (ChkWarnDangerous != null)
                ChkWarnDangerous.IsChecked = settings.WarnDangerousFiles;
            if (ChkMarkFromInternet != null)
                ChkMarkFromInternet.IsChecked = settings.MarkDownloadsFromInternet;
            FillLanguageCombo(settings.UiLanguage);
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

            TxtExtPath.Text = ExtensionInstaller.ExtensionHomeRoot;
            // Tarayıcı taraması UI'yi dondurmasın — eklenti sekmesine girilince / arka planda yüklenir
            ApplyVersionTexts();
            if (!_updateBusy)
                TxtUpdateStatus.Text = "";

            SelectTab(initialTab ?? "general");
            ApplyThemeSurface(string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase));
            if (string.Equals(initialTab, "extension", StringComparison.OrdinalIgnoreCase)
                || string.Equals(initialTab, "eklenti", StringComparison.OrdinalIgnoreCase))
                ScheduleBrowserStatusRefresh(forceDiscover: false);
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
                case "security":
                case "guvenlik":
                case "güvenlik":
                    NavSecurity.IsChecked = true;
                    break;
                case "language":
                case "dil":
                case "lang":
                    NavLanguage.IsChecked = true;
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
            if (PanelSecurity != null)
                PanelSecurity.Visibility = NavSecurity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (PanelLanguage != null)
                PanelLanguage.Visibility = NavLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelUpdate.Visibility = NavUpdate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavExtension.IsChecked == true)
                ScheduleBrowserStatusRefresh(forceDiscover: false);
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

        private void SidebarCollapse_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _themePreviewBusy || ChkSidebarCollapse == null) return;
            if (OwnerWindow is MainWindow mw)
                mw.ApplySidebarCollapseButton(ChkSidebarCollapse.IsChecked == true, save: false);
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
            TxtHotkey.Text = Loc.T("settings.keyboard.press_keys", "Tuşlara basın…");
            TxtHotkeyHint.Text = Loc.T("settings.keyboard.capture_hint", "Esc iptal eder. Örn. Ctrl+C veya Ctrl+Shift+C");
            HotkeyBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            Keyboard.Focus(HotkeyBox);
        }

        private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            StopHotkeyCapture();
            _copyHotkey = "Ctrl+C";
            TxtHotkey.Text = _copyHotkey;
            TxtHotkeyHint.Text = Loc.T("settings.keyboard.reset_done", "Varsayılan kısayol geri yüklendi.");
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
                TxtHotkeyHint.Text = Loc.T("settings.keyboard.capture_cancelled", "İptal edildi.");
                return;
            }

            var mods = Keyboard.Modifiers;
            bool allowNoModifier = key is >= Key.F1 and <= Key.F12
                or Key.Delete or Key.Insert or Key.Home or Key.End
                or Key.PageUp or Key.PageDown;

            if (mods == ModifierKeys.None && !allowNoModifier)
            {
                TxtHotkeyHint.Text = Loc.T("settings.keyboard.need_modifier", "En az bir değiştirici (Ctrl / Alt / Shift) kullanın.");
                return;
            }

            _copyHotkey = HotkeyParser.Format(key, mods);
            TxtHotkey.Text = _copyHotkey;
            StopHotkeyCapture();
            TxtHotkeyHint.Text = Loc.T("settings.keyboard.hotkey_saved", "Kısayol güncellendi. Kaydet'e basın.");
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
                await RefreshBrowserStatusAsync(forceDiscover: true, showBusy: true);
            }
            finally
            {
                BtnRefreshBrowsers.IsEnabled = true;
            }
        }

        private int _browserRefreshGen;

        private void ScheduleBrowserStatusRefresh(bool forceDiscover)
            => _ = RefreshBrowserStatusAsync(forceDiscover, showBusy: true);

        private void RefreshBrowserStatus()
            => ScheduleBrowserStatusRefresh(forceDiscover: false);

        private async Task RefreshBrowserStatusAsync(bool forceDiscover, bool showBusy)
        {
            if (BrowserStatusList == null) return;
            int gen = Interlocked.Increment(ref _browserRefreshGen);

            if (showBusy)
            {
                BrowserStatusList.Children.Clear();
                BrowserStatusList.Children.Add(new TextBlock
                {
                    Text = Loc.T("settings.ext.checking", "Kontrol ediliyor…"),
                    Foreground = TryFindResource("SettingsMutedBrush") as Brush
                                 ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }

            IReadOnlyList<BrowserExtensionStatus> list;
            try
            {
                list = await Task.Run(() => BrowserExtensionProbe.ProbeAll(forceDiscover)).ConfigureAwait(true);
            }
            catch
            {
                list = Array.Empty<BrowserExtensionStatus>();
            }

            if (gen != _browserRefreshGen || BrowserStatusList == null) return;

            BrowserStatusList.Children.Clear();
            foreach (var b in list)
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
                    Text = !b.BrowserInstalled
                        ? Loc.T("extstatus.none", "Yok")
                        : (b.ExtensionActive
                            ? Loc.T("extstatus.pill_active", "Aktif")
                            : Loc.T("extstatus.pill_off", "Devre dışı")),
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

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (b.BrowserInstalled && !b.ExtensionActive)
            {
                string captureId = b.Id;
                var installBtn = new Button
                {
                    Content = Loc.T("settings.ext.install", "Kur"),
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(12, 4, 12, 4),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    Style = TryFindResource("SoftButton") as Style
                };
                installBtn.Click += (_, _) => InstallBrowserExtension(captureId);
                right.Children.Add(installBtn);
            }

            right.Children.Add(statusPill);
            Grid.SetColumn(right, 2);
            row.Children.Add(icon);
            row.Children.Add(texts);
            row.Children.Add(right);

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

        private void BtnExtHowto_Click(object sender, RoutedEventArgs e)
        {
            try { ExtensionInstaller.PrepareChromiumStaging("chrome"); }
            catch { /* klasör yoksa yine yolu göster */ }
            string staging = Path.GetFullPath(ExtensionInstaller.ChromiumStagingRoot);
            ExtensionInstaller.RevealFolder(staging);
            InfoDialog.Show(OwnerWindow,
                Loc.T("settings.ext.howto_title", "Kurulum nasıl yapılır?"),
                Loc.T("settings.ext.chromium_message", "Kurulum — bir adım kaldı."),
                Loc.T("settings.ext.chromium_detail",
                    "1) Açılan eklentiler sayfasında «Geliştirici modu»nu aç.\n2) «Paketlenmemiş öğe yükle»ye tıkla.\n3) Explorer’da zaten seçili klasörü seç — klasörü başka yere kopyalaman gerekmez."),
                staging);
        }

        private void BtnInstallFirefox_Click(object sender, RoutedEventArgs e) => InstallBrowserExtension("firefox");

        private void InstallBrowserExtension(string? browserId)
        {
            if (string.IsNullOrWhiteSpace(browserId) || browserId is "firefox" or "firefox-developer")
            {
                InstallFirefoxExtension(browserId);
                return;
            }

            try
            {
                bool ok = ExtensionInstaller.TryInstallBrowser(
                    browserId, out string title, out string message, out string detail);
                InfoDialog.Show(OwnerWindow, title, message, detail, ExtensionInstaller.LastInstallPath);
                if (ok)
                    RefreshBrowserStatus();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.ext.dialog_title", "Eklenti"),
                    Loc.T("settings.ext.firefox_error_message", "Kurulum başlatılamadı."), ex.Message);
            }
        }

        /// <summary>
        /// Developer Edition indirme sayfasını açar ve kurulum bitene kadar bekler. Kullanıcı
        /// «Devam»a bastığında yeniden aranır; bulunursa eklenti kurulumu kesintisiz sürer.
        /// </summary>
        private bool WaitForDeveloperEdition()
        {
            FirefoxInstallDialog.OpenDeveloperEditionPage();

            while (true)
            {
                bool proceed = ConfirmDialog.Show(OwnerWindow,
                    "Firefox Developer Edition",
                    Loc.T("settings.ext.firefox_dev_message",
                        "İndirme sayfası açıldı. Developer Edition kurulumunu tamamladıktan sonra «Devam» deyin."),
                    Loc.T("settings.ext.firefox_dev_detail",
                        "Devam'a bastığınızda MDM eklentiyi Developer Edition'a kalıcı olarak kurar."),
                    confirmText: Loc.T("settings.ext.firefox_dev_continue", "Devam"),
                    cancelText: Loc.T("dialog.cancel", "İptal"),
                    forceFloating: true);

                if (!proceed) return false;
                if (ExtensionInstaller.IsDeveloperEditionInstalled()) return true;

                InfoDialog.Show(OwnerWindow, "Firefox Developer Edition",
                    Loc.T("settings.ext.firefox_dev_missing", "Developer Edition hâlâ bulunamadı."),
                    Loc.T("settings.ext.firefox_dev_missing_detail",
                        "Kurulumu tamamlayıp bir kez açtıktan sonra tekrar «Devam» deyin."));
            }
        }

        private void InstallFirefoxExtension(string? browserId)
        {
            try
            {
                ExtensionInstaller.FirefoxInstallMode mode;

                if (browserId == "firefox-developer")
                {
                    if (!ExtensionInstaller.IsDeveloperEditionInstalled()
                        && !WaitForDeveloperEdition())
                        return;
                    mode = ExtensionInstaller.FirefoxInstallMode.Permanent;
                }
                else
                {
                    bool devInstalled = ExtensionInstaller.IsDeveloperEditionInstalled();
                    var choice = FirefoxInstallDialog.Show(OwnerWindow, devInstalled);
                    if (choice == FirefoxInstallChoice.Cancel)
                        return;

                    if (choice == FirefoxInstallChoice.OpenDeveloperEdition)
                    {
                        if (!devInstalled && !WaitForDeveloperEdition())
                            return;
                        mode = ExtensionInstaller.FirefoxInstallMode.Permanent;
                    }
                    else
                    {
                        mode = ExtensionInstaller.FirefoxInstallMode.Temporary;
                    }
                }

                bool ok = ExtensionInstaller.TryInstallFirefox(
                    out string title, out string message, out string detail, mode);

                InfoDialog.Show(OwnerWindow, title, message, detail, ExtensionInstaller.LastInstallPath);
                if (ok)
                    RefreshBrowserStatus();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("firefox.title", "Firefox eklentisi"),
                    Loc.T("settings.ext.firefox_error_message", "Kurulum başlatılamadı."), ex.Message);
            }
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            ChkAutoStartMin.IsEnabled = ChkAutoStart.IsChecked == true;
        }

        private Window? OwnerWindow => Window.GetWindow(this);

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = Loc.T("settings.general.folder_label", "Varsayılan indirme klasörü")
            };
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
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.ext.dialog_title", "Eklenti"),
                    Loc.T("settings.ext.cleanup_done", "Hazır."),
                    Loc.T("settings.ext.cleanup_detail", "Tarayıcıdaki eklenti bu klasörü kullanıyor:")
                        + "\n" + ExtensionInstaller.ExtensionHomeRoot);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.ext.dialog_title", "Eklenti"),
                    Loc.T("settings.ext.cleanup_failed", "İşlem başarısız."), ex.Message);
            }
        }

        private void BtnOpenExtFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExtensionInstaller.EnsureInstalled();
                Directory.CreateDirectory(ExtensionInstaller.ExtensionHomeRoot);
                Directory.CreateDirectory(ExtensionInstaller.ChromiumStagingRoot);
                Directory.CreateDirectory(ExtensionInstaller.FirefoxStagingRoot);
                ExtensionInstaller.RevealFolder(ExtensionInstaller.ExtensionHomeRoot, force: true);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.general.folder_title", "Klasör"),
                    Loc.T("settings.ext.folder_open_failed", "Açılamadı."), ex.Message);
            }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            BtnCheckUpdate.IsEnabled = false;
            SetUpdateStatus(Loc.T("settings.update.updating", "Güncelleniyor…"), "#FF6B00");
            _updateCts = new CancellationTokenSource();
            var progress = new Progress<string>(_ =>
                SetUpdateStatus(Loc.T("settings.update.updating", "Güncelleniyor…"), "#FF6B00"));

            try
            {
                var result = await UpdateService.CheckAndApplyAsync(progress, _updateCts.Token);

                if (result.Applying)
                {
                    SetUpdateStatus(Loc.T("settings.update.updating", "Güncelleniyor…"), "#FF6B00");
                    UpdateApplying?.Invoke();
                    return;
                }

                if (result.HadError)
                {
                    SetUpdateStatus(result.Message, "#E07070");
                    InfoDialog.Show(OwnerWindow,
                        Loc.T("settings.update.dialog_title", "Güncelleme"),
                        Loc.T("settings.update.check_failed", "Denetim başarısız."), result.Message);
                }
                else if (result.IsUpToDate)
                {
                    SetUpdateStatus(Loc.T("settings.update.up_to_date", "Güncelsiniz"), "#8BC34A");
                }
                else
                {
                    SetUpdateStatus(result.Message, "#E0E0E0");
                }
            }
            catch (Exception ex)
            {
                SetUpdateStatus(ex.Message, "#E07070");
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.update.dialog_title", "Güncelleme"),
                    Loc.T("settings.update.check_failed", "Denetim başarısız."), ex.Message);
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
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.general.folder_title", "Klasör"),
                    Loc.T("settings.general.folder_invalid", "Geçerli bir indirme klasörü girin."));
                return false;
            }

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.general.folder_title", "Klasör"),
                    Loc.T("settings.general.folder_create_failed", "Klasör oluşturulamadı."), ex.Message);
                return false;
            }

            _settings = CaptureCurrentSettings();
            AppSettingsStore.Save(_settings);
            _loadedSnapshot = CloneSettings(_settings);
            Loc.Apply(_settings.UiLanguage);
            ApplyLocalizedChrome();

            if (_settings.AutoStart)
                AutoStartHelper.Enable(_settings.AutoStartMinimized);
            else
                AutoStartHelper.Disable();

            Saved?.Invoke();
            return true;
        }

        private void FillLanguageCombo(string? current)
        {
            if (CmbLanguage == null) return;
            string code = Loc.NormalizeCode(current);
            CmbLanguage.SelectionChanged -= CmbLanguage_SelectionChanged;
            CmbLanguage.Items.Clear();
            int selected = 0;
            for (int i = 0; i < Loc.Languages.Length; i++)
            {
                var (c, name) = Loc.Languages[i];
                CmbLanguage.Items.Add(new LanguageItem(c, name));
                if (c.Equals(code, StringComparison.OrdinalIgnoreCase))
                    selected = i;
            }
            CmbLanguage.SelectedIndex = selected;
            CmbLanguage.SelectionChanged += CmbLanguage_SelectionChanged;
            ApplyLocalizedChrome();
        }

        private string SelectedLanguageCode()
        {
            if (CmbLanguage?.SelectedItem is LanguageItem li)
                return li.Code;
            return Loc.NormalizeCode(_settings.UiLanguage);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || CmbLanguage?.SelectedItem is not LanguageItem li) return;
            // Anında önizleme — Kaydet kalıcılar
            Loc.Apply(li.Code);
            ApplyLocalizedChrome();
        }

        public void ApplyLocalizedChrome()
        {
            if (SettingsTitle != null) SettingsTitle.Text = Loc.T("settings.title", "Ayarlar");
            if (NavGeneral != null) NavGeneral.Content = Loc.T("settings.nav.general", "Genel");
            if (NavNotifications != null) NavNotifications.Content = Loc.T("settings.nav.notifications", "Bildirimler");
            if (NavTheme != null) NavTheme.Content = Loc.T("settings.nav.theme", "Görünüm");
            if (NavExtension != null) NavExtension.Content = Loc.T("settings.nav.extension", "Tarayıcı eklentisi");
            if (NavKeyboard != null) NavKeyboard.Content = Loc.T("settings.nav.keyboard", "Klavye ayarları");
            if (NavAdvanced != null) NavAdvanced.Content = Loc.T("settings.nav.advanced", "Gelişmiş");
            if (NavSecurity != null) NavSecurity.Content = Loc.T("settings.nav.security", "Güvenlik");
            if (NavLanguage != null) NavLanguage.Content = Loc.T("settings.nav.language", "Dil");
            if (NavUpdate != null) NavUpdate.Content = Loc.T("settings.nav.update", "Güncelleme");
            if (NavAbout != null) NavAbout.Content = Loc.T("settings.nav.about", "Hakkında");
            if (TxtSecurityTitle != null) TxtSecurityTitle.Text = Loc.T("settings.security.title", "Güvenlik");
            if (ChkConfirmRepeat != null) ChkConfirmRepeat.Content = Loc.T("settings.security.spam_confirm", "İndirme spam’inde güvenlik onayı (önerilir)");
            if (TxtSecurityHint != null) TxtSecurityHint.Text = Loc.T("settings.security.spam_hint",
                "Açıkken kısa sürede aynı bağlantıya veya toplu indirme isteklerine karşı tek bir onay penceresi gösterilir. Onaylamazsanız istekler sessizce engellenir; normal indirmeleri etkilemez.");
            if (ChkWarnDangerous != null) ChkWarnDangerous.Content = Loc.T("settings.security.dangerous_confirm",
                "Zararlı olabilecek dosyalarda izin sor (önerilir)");
            if (TxtDangerousHint != null) TxtDangerousHint.Text = Loc.T("settings.security.dangerous_hint",
                "Program, betik veya makro içeren dosyalar indirilmeden önce tarayıcıdaki gibi bir izin penceresi gösterilir. İzin vermezseniz indirme başlamaz.");
            if (ChkMarkFromInternet != null) ChkMarkFromInternet.Content = Loc.T("settings.security.mark_internet",
                "İndirilen dosyaları “internetten geldi” olarak işaretle");
            if (TxtMarkHint != null) TxtMarkHint.Text = Loc.T("settings.security.mark_internet_hint",
                "Windows ve Microsoft Defender dosyayı açarken kendi güvenlik denetimini uygular. Kapatırsanız bu uyarılar çıkmaz.");
            if (TxtLanguageTitle != null) TxtLanguageTitle.Text = Loc.T("settings.language.title", "Uygulama dili");
            if (TxtLanguageHint != null) TxtLanguageHint.Text = Loc.T("settings.language.hint",
                "Dil değişikliği uygulama arayüzüne ve tarayıcı eklentisine uygulanır.");
            if (BtnCancelSettings != null) BtnCancelSettings.Content = Loc.T("settings.cancel", "İptal");
            if (BtnSaveSettings != null) BtnSaveSettings.Content = Loc.T("settings.save", "Kaydet");
            if (TxtThemeTitle != null) TxtThemeTitle.Text = Loc.T("settings.theme.title", "Görünüm");
            if (TxtThemeAppearance != null) TxtThemeAppearance.Text = Loc.T("settings.theme.appearance", "Tema");
            if (TxtThemeDark != null) TxtThemeDark.Text = Loc.T("settings.theme.dark", "Koyu");
            if (TxtThemeDarkSub != null) TxtThemeDarkSub.Text = Loc.T("settings.theme.dark_sub", "Siyah mod");
            if (TxtThemeLight != null) TxtThemeLight.Text = Loc.T("settings.theme.light", "Açık");
            if (TxtThemeLightSub != null) TxtThemeLightSub.Text = Loc.T("settings.theme.light_sub", "Beyaz mod");
            if (ChkSidebarCollapse != null)
                ChkSidebarCollapse.Content = Loc.T("settings.theme.sidebar_collapse", "Kategori kenar çubuğunu daralt");
            if (TxtSidebarCollapseHint != null)
                TxtSidebarCollapseHint.Text = Loc.T("settings.theme.sidebar_collapse_hint",
                    "Açıkken sol üstteki ‹ düğmesi kenar çubuğunu yalnızca simgelere küçültür. Kapalıysa düğme gizlenir.");
            if (TxtBrightnessLabel != null) TxtBrightnessLabel.Text = Loc.T("settings.theme.brightness", "Beyaz ton parlaklığı");
            if (TxtBrightnessHint != null) TxtBrightnessHint.Text = Loc.T("settings.theme.brightness_hint",
                "Yalnızca yüzeyleri etkiler (ana pencere ve popup'lar). Yazılar tam kontrastta kalır.");
            if (TxtThemeHint != null) TxtThemeHint.Text = Loc.T("settings.theme.hint",
                "Seçince anında önizlenir. Kalıcı olması için Kaydet’e basın; İptal eski temaya döner.");
            if (TxtExtPanelTitle != null) TxtExtPanelTitle.Text = Loc.T("settings.ext.title", "Eklenti denetimi");
            if (BtnRefreshBrowsers != null) BtnRefreshBrowsers.Content = Loc.T("settings.ext.refresh", "Yenile");
            if (BtnExtHowto != null) BtnExtHowto.Content = Loc.T("settings.ext.howto_button", "Kurulum nasıl yapılır?");
            if (BtnInstallFirefox != null) BtnInstallFirefox.Content = Loc.T("settings.ext.firefox_install", "Firefox otomatik kur");

            // Genel
            if (TxtGenFolderLabel != null) TxtGenFolderLabel.Text = Loc.T("settings.general.folder_label", "Varsayılan indirme klasörü");
            if (BtnGenBrowse != null) BtnGenBrowse.Content = Loc.T("settings.general.browse", "Gözat");
            if (ChkAutoStart != null) ChkAutoStart.Content = Loc.T("settings.general.autostart", "Windows ile birlikte başlat");
            if (ChkAutoStartMin != null) ChkAutoStartMin.Content = Loc.T("settings.general.autostart_min", "Başlangıçta arka planda aç");
            if (ChkDeleteFromDisk != null) ChkDeleteFromDisk.Content = Loc.T("settings.general.delete_from_disk", "Silince dosyayı bilgisayardan da kaldır");
            if (TxtGenDeleteHint != null) TxtGenDeleteHint.Text = Loc.T("settings.general.delete_from_disk_hint",
                "Kapalıysa öğe yalnızca listeden çıkar; indirme klasöründeki dosya durur.");
            if (ChkAutoExtract != null) ChkAutoExtract.Content = Loc.T("settings.general.auto_extract", "Arşivi indirdikten sonra aç");
            if (ChkDeleteArchive != null) ChkDeleteArchive.Content = Loc.T("settings.general.delete_archive", "Kapattıktan sonra arşiv dosyasını sil");
            if (TxtGenArchiveHint != null) TxtGenArchiveHint.Text = Loc.T("settings.general.delete_archive_hint",
                "Arşiv görüntüleyicide açıldıktan sonra kapatınca .rar/.zip dosyası silinir.");
            if (ChkAutoFolders != null) ChkAutoFolders.Content = Loc.T("settings.general.auto_folders", "Kategori klasörlerini otomatik oluştur");
            if (TxtGenFoldersHint != null) TxtGenFoldersHint.Text = Loc.T("settings.general.auto_folders_hint",
                "Kapalıysa indirmeler doğrudan varsayılan klasöre yazılır; Videolar/Arşivler alt klasörü açılmaz.");

            // Bildirimler
            if (TxtNotifyTitle != null) TxtNotifyTitle.Text = Loc.T("settings.notify.title", "Bildirimler");
            if (ChkNotifyTray != null) ChkNotifyTray.Content = Loc.T("settings.notify.tray", "Alta alınca tepsi bildirimi göster");
            if (TxtNotifyTrayHint != null) TxtNotifyTrayHint.Text = Loc.T("settings.notify.tray_hint",
                "Uygulama gizli simgelere indiğinde «arka planda çalışıyor» balonu.");
            if (ChkNotifyDone != null) ChkNotifyDone.Content = Loc.T("settings.notify.done", "Dosya indince bildirim göster");
            if (TxtNotifyDoneHint != null) TxtNotifyDoneHint.Text = Loc.T("settings.notify.done_hint",
                "İndirme bitince Windows tepsi bildirimi (Windows sesi). Kapalıysa bildirim ve ses gelmez.");
            if (ChkGameMode != null) ChkGameMode.Content = Loc.T("settings.notify.game_mode", "Oyun modu");
            if (TxtGameModeHint != null) TxtGameModeHint.Text = Loc.T("settings.notify.game_mode_hint",
                "Açıkken tam ekran oyun veya sunum sırasında indirme bildirimi, tepsi balonu ve mini indirme penceresi çıkmaz. İndirmeler arka planda sürer. Varsayılan kapalıdır; isteğe bağlı açılır.");

            // Eklenti klasörü
            if (TxtExtFolderLabel != null) TxtExtFolderLabel.Text = Loc.T("settings.ext.folder_label", "Eklenti klasörü");
            if (BtnExtCleanup != null) BtnExtCleanup.Content = Loc.T("settings.ext.cleanup", "Kalıntı temizle");
            if (BtnExtOpenFolder != null) BtnExtOpenFolder.Content = Loc.T("settings.ext.open_folder", "Klasörü aç");

            // Klavye
            if (TxtKeyboardTitle != null) TxtKeyboardTitle.Text = Loc.T("settings.keyboard.title", "Uygulama içi kısayollar");
            if (ChkCopyHotkey != null) ChkCopyHotkey.Content = Loc.T("settings.keyboard.copy_files", "Seçili dosyaları panoya kopyala");
            if (TxtKeyCopyHint != null) TxtKeyCopyHint.Text = Loc.T("settings.keyboard.copy_files_hint",
                "Etkinse seçili tamamlanmış indirmeleri panoya dosya olarak koyar; Explorer veya başka uygulamaya Ctrl+V ile yapıştırabilirsiniz.");
            if (TxtKeyShortcutLabel != null) TxtKeyShortcutLabel.Text = Loc.T("settings.keyboard.shortcut_label", "Kısayol");
            if (BtnCaptureHotkey != null) BtnCaptureHotkey.Content = Loc.T("settings.keyboard.change", "Değiştir");
            if (BtnResetHotkey != null) BtnResetHotkey.Content = Loc.T("settings.keyboard.reset", "Sıfırla");
            // Yakalama sürerken ipucu metni ezilmez
            if (TxtHotkeyHint != null && !_capturingHotkey) TxtHotkeyHint.Text = Loc.T("settings.keyboard.hotkey_hint",
                "Kısayolu değiştirmek için «Değiştir»e basın, sonra tuşlara basın.");
            if (ChkDeleteKey != null) ChkDeleteKey.Content = Loc.T("settings.keyboard.delete_key", "Delete tuşu uygulama içi kısayol silmeye izin ver");
            if (TxtKeyDeleteHint != null) TxtKeyDeleteHint.Text = Loc.T("settings.keyboard.delete_key_hint",
                "Etkinse Delete tuşu seçili dosyaları, arşivleri ve kategorileri siler.");

            // Gelişmiş
            if (TxtAdvTitle != null) TxtAdvTitle.Text = Loc.T("settings.advanced.title", "Zamanlayıcı ve uzak denetim");
            if (ChkSchedule != null) ChkSchedule.Content = Loc.T("settings.advanced.schedule", "Yalnızca bu saatler arasında indir");
            if (TxtAdvSchedStartLabel != null) TxtAdvSchedStartLabel.Text = Loc.T("settings.advanced.sched_start", "Başlangıç saati");
            if (TxtAdvSchedEndLabel != null) TxtAdvSchedEndLabel.Text = Loc.T("settings.advanced.sched_end", "Bitiş");
            if (TxtAdvSchedHint != null) TxtAdvSchedHint.Text = Loc.T("settings.advanced.sched_hint",
                "Aynı saat = her zaman açık. 22 ve 6 gece penceresidir.");
            if (TxtAdvCrawlLabel != null) TxtAdvCrawlLabel.Text = Loc.T("settings.advanced.crawl_depth", "Tarama derinliği (0–3)");
            if (ChkRemoteLan != null) ChkRemoteLan.Content = Loc.T("settings.advanced.remote_lan", "REST API’yi LAN’dan dinle (token gerekir)");
            if (TxtAdvApiTokenLabel != null) TxtAdvApiTokenLabel.Text = Loc.T("settings.advanced.api_token", "API token");
            if (ChkHttp3 != null) ChkHttp3.Content = Loc.T("settings.advanced.http3", "HTTPS’te HTTP/3 dene (olmazsa 2 / 1.1)");
            if (ChkAutoReconnect != null) ChkAutoReconnect.Content = Loc.T("settings.advanced.auto_reconnect", "Ağ düşünce otomatik devam et");
            if (TxtAdvSpeedLabel != null) TxtAdvSpeedLabel.Text = Loc.T("settings.advanced.speed_limit", "Hız limiti KB/s");
            if (TxtAdvSpeedHint != null) TxtAdvSpeedHint.Text = Loc.T("settings.advanced.speed_limit_hint", "(0 = sınırsız)");
            if (TxtAdvConcurrentLabel != null) TxtAdvConcurrentLabel.Text = Loc.T("settings.advanced.concurrent", "Eşzamanlı indirme");
            if (TxtAdvConcurrentHint != null) TxtAdvConcurrentHint.Text = Loc.T("settings.advanced.concurrent_hint", "(0 = sınırsız, fazlası kuyruk)");
            if (TxtAdvHttpChannelsLabel != null) TxtAdvHttpChannelsLabel.Text = Loc.T("settings.advanced.http_channels", "HTTP kanal (0=otomatik)");
            if (TxtAdvTorrentTitle != null) TxtAdvTorrentTitle.Text = Loc.T("settings.advanced.torrent_title", "Torrent");
            if (TxtAdvTorrentPortLabel != null) TxtAdvTorrentPortLabel.Text = Loc.T("settings.advanced.torrent_port", "Dinleme portu");
            if (ChkTorrentDht != null) ChkTorrentDht.Content = Loc.T("settings.advanced.torrent_dht", "DHT (trackersız magnet)");
            if (ChkTorrentLpd != null) ChkTorrentLpd.Content = Loc.T("settings.advanced.torrent_lpd", "Yerel eş keşfi");
            if (ChkTorrentUpnp != null) ChkTorrentUpnp.Content = Loc.T("settings.advanced.torrent_upnp", "UPnP / NAT-PMP port yönlendirme");
            if (ChkTorrentSeq != null) ChkTorrentSeq.Content = Loc.T("settings.advanced.torrent_seq", "Sıralı indir (önce ilk dosya)");
            if (TxtAdvSeedLabel != null) TxtAdvSeedLabel.Text = Loc.T("settings.advanced.torrent_seed", "Paylaşım oranı");
            if (TxtAdvSeedHint != null) TxtAdvSeedHint.Text = Loc.T("settings.advanced.torrent_seed_hint", "(0 = indince dur)");
            if (ChkSkipDup != null) ChkSkipDup.Content = Loc.T("settings.advanced.skip_dup", "Aynı URL’yi ikinci kez ekleme");
            if (TxtAdvSkipExtLabel != null) TxtAdvSkipExtLabel.Text = Loc.T("settings.advanced.skip_ext", "Atlanan uzantılar (virgülle)");
            if (TxtSkipExt != null) TxtSkipExt.ToolTip = Loc.T("settings.advanced.skip_ext_tip", "Örn: exe, scr, bat");
            if (TxtAdvSkipUrlLabel != null) TxtAdvSkipUrlLabel.Text = Loc.T("settings.advanced.skip_url", "URL’de geçenleri atla");
            if (TxtAdvSkipDomainsLabel != null) TxtAdvSkipDomainsLabel.Text = Loc.T("settings.advanced.skip_domains", "Atlanan alan adları (virgülle)");
            if (TxtSkipDomains != null) TxtSkipDomains.ToolTip = Loc.T("settings.advanced.skip_domains_tip", "Örn: ads.example.com, tracker.net");
            if (TxtAdvSkipRegexLabel != null) TxtAdvSkipRegexLabel.Text = Loc.T("settings.advanced.skip_regex", "URL regex (boş = kapalı)");
            if (TxtAdvSkipMimeLabel != null) TxtAdvSkipMimeLabel.Text = Loc.T("settings.advanced.skip_mime", "Atlanan MIME (virgülle, örn: video/, audio/)");
            if (TxtAdvMinMbLabel != null) TxtAdvMinMbLabel.Text = Loc.T("settings.advanced.min_mb", "Min MB");
            if (TxtAdvMaxMbLabel != null) TxtAdvMaxMbLabel.Text = Loc.T("settings.advanced.max_mb", "Max MB");
            if (TxtAdvSizeHint != null) TxtAdvSizeHint.Text = Loc.T("settings.advanced.size_hint", "(0 = kapalı)");
            if (TxtAdvRenameLabel != null) TxtAdvRenameLabel.Text = Loc.T("settings.advanced.rename_label", "Yeniden adlandır {name} {ext} {date} {host}");
            if (TxtAdvCliHint != null) TxtAdvCliHint.Text = Loc.T("settings.advanced.cli_hint",
                "CLI: download URL  |  --add URL  |  --grab SAYFA  |  --list  |  --pause|--resume|--cancel ID");
            if (TxtAdvResetTitle != null) TxtAdvResetTitle.Text = Loc.T("settings.advanced.reset_title",
                "Gelişmiş ayarları varsayılana döndür");
            if (TxtAdvResetHint != null) TxtAdvResetHint.Text = Loc.T("settings.advanced.reset_hint",
                "Bu bölümdeki tüm alanlar kurulumdaki haline döner. Diğer ayarlar etkilenmez.");
            if (BtnAdvReset != null) BtnAdvReset.Content = Loc.T("settings.advanced.reset_button", "Varsayılana döndür");

            // Güncelleme
            if (TxtUpdateTitle != null) TxtUpdateTitle.Text = Loc.T("settings.update.title", "Uygulama güncellemesi");
            if (TxtUpdateHint != null) TxtUpdateHint.Text = Loc.T("settings.update.hint",
                "Güncelleme yalnızca buradan denetlenir; uygulama her açılışta otomatik aramaz.");
            if (BtnCheckUpdate != null) BtnCheckUpdate.Content = Loc.T("settings.update.check", "Güncellemeyi denetle");

            // Hakkında
            if (TxtAboutDesc != null) TxtAboutDesc.Text = Loc.T("settings.about.desc",
                "İndirmeleri kategorilere ayıran, tarayıcı yakalamalı masaüstü yöneticisi.");
            if (TxtUninstallTitle != null) TxtUninstallTitle.Text = Loc.T("settings.about.uninstall_title", "Uygulamayı kaldır");
            if (TxtUninstallHint != null) TxtUninstallHint.Text = Loc.T("settings.about.uninstall_hint",
                "MuckDownloadManager bilgisayarınızdan kaldırılır. İndirdiğiniz dosyalar silinmez.");
            if (BtnUninstallApp != null) BtnUninstallApp.Content = Loc.T("settings.about.uninstall_button", "Kaldır");
            if (CardUninstall != null)
                CardUninstall.Visibility = UninstallerPath() != null ? Visibility.Visible : Visibility.Collapsed;
            ApplyVersionTexts();

            FlowDirection = Loc.Flow;
            RefreshBrowserStatus();
        }

        /// <summary>
        /// Kaldırıcı yolu: önce uygulamanın yanı, sonra kayıt defterindeki kurulum klasörü.
        /// Derleme çıktısından çalıştırılan kopyada da kurulu sürüm kaldırılabilsin.
        /// </summary>
        private static string? UninstallerPath()
        {
            try
            {
                string local = Path.Combine(AppContext.BaseDirectory, "Uninstall.exe");
                if (File.Exists(local)) return local;

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MuckDownloadManager");
                if (key?.GetValue("InstallLocation") as string is not { Length: > 0 } dir) return null;

                string installed = Path.Combine(dir, "Uninstall.exe");
                return File.Exists(installed) ? installed : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gelişmiş bölümündeki alanları fabrika değerlerine çevirir. Değişiklik yalnızca
        /// forma yazılır; kullanıcı Kaydet'e basana kadar diske gitmez.
        /// </summary>
        private void BtnAdvReset_Click(object sender, RoutedEventArgs e)
        {
            var d = new AppSettings();

            ChkSchedule.IsChecked = d.ScheduleEnabled;
            TxtSchedStart.Text = d.ScheduleStartHour.ToString();
            TxtSchedEnd.Text = d.ScheduleEndHour.ToString();
            TxtCrawlDepth.Text = d.CrawlDepth.ToString();
            ChkRemoteLan.IsChecked = d.RemoteApiLan;
            TxtApiToken.Text = d.RemoteApiToken;
            ChkHttp3.IsChecked = d.PreferHttp3;
            ChkAutoReconnect.IsChecked = d.AutoReconnect;
            TxtSpeedLimit.Text = d.SpeedLimitKBps.ToString();
            TxtMaxConcurrent.Text = d.MaxConcurrentDownloads.ToString();
            TxtHttpChannels.Text = d.HttpMaxChannels.ToString();
            TxtTorrentPort.Text = d.TorrentListenPort.ToString();
            ChkTorrentDht.IsChecked = d.TorrentDht;
            ChkTorrentLpd.IsChecked = d.TorrentLocalPeers;
            ChkTorrentUpnp.IsChecked = d.TorrentPortForward;
            ChkTorrentSeq.IsChecked = d.TorrentSequential;
            TxtTorrentSeed.Text = d.TorrentSeedRatio.ToString("0.##");
            ChkSkipDup.IsChecked = d.SkipDuplicateUrls;
            TxtSkipExt.Text = d.SkipExtensions;
            TxtSkipUrl.Text = d.SkipUrlContains;
            TxtSkipDomains.Text = d.SkipDomains;
            TxtSkipRegex.Text = d.SkipUrlRegex;
            TxtSkipMime.Text = d.SkipMimeContains;
            TxtSkipMinMb.Text = d.SkipMinSizeMb.ToString();
            TxtSkipMaxMb.Text = d.SkipMaxSizeMb.ToString();
            TxtRename.Text = d.RenamePattern;

            InfoDialog.Show(OwnerWindow,
                Loc.T("settings.advanced.reset_title", "Gelişmiş ayarları varsayılana döndür"),
                Loc.T("settings.advanced.reset_done", "Alanlar varsayılana döndü."),
                Loc.T("settings.advanced.reset_done_detail", "Kalıcı olması için Kaydet'e basın."));
        }

        private void BtnUninstallApp_Click(object sender, RoutedEventArgs e)
        {
            string? exe = UninstallerPath();
            if (exe == null) return;

            try
            {
                // Kaldırıcı kendi onay ekranını gösterir ve uygulamayı kapatır
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow,
                    Loc.T("settings.about.uninstall_title", "Uygulamayı kaldır"),
                    Loc.T("settings.about.uninstall_failed", "Kaldırıcı başlatılamadı."), ex.Message);
            }
        }

        /// <summary>Sürüm metinleri — dil değişince de yenilenir.</summary>
        private void ApplyVersionTexts()
        {
            string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            if (TxtVersion != null)
                TxtVersion.Text = Fmt("settings.about.version", "Sürüm {0}", ver);
            if (TxtUpdateCurrent != null)
                TxtUpdateCurrent.Text = Fmt("settings.update.installed", "Yüklü sürüm: v{0}", UpdateService.CurrentVersionText);
        }

        /// <summary>Loc.T + string.Format — çeviride bozuk yer tutucu varsa çökmez.</summary>
        private static string Fmt(string key, string fallback, params object[] args)
        {
            string fmt = Loc.T(key, fallback);
            try { return string.Format(fmt, args); }
            catch { return fmt; }
        }

        private sealed class LanguageItem
        {
            public string Code { get; }
            public string Name { get; }
            public LanguageItem(string code, string name) { Code = code; Name = name; }
            public override string ToString() => Name;
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
            s.GameModeEnabled = ChkGameMode?.IsChecked == true;
            s.Theme = ThemeLight.IsChecked == true ? "Light" : "Dark";
            s.LightThemeBrightness = SldLightBrightness != null ? (int)SldLightBrightness.Value : 100;
            s.SidebarCollapseEnabled = ChkSidebarCollapse?.IsChecked != false;
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
            if (ChkConfirmRepeat != null)
                s.ConfirmRepeatDownloads = ChkConfirmRepeat.IsChecked == true;
            s.WarnDangerousFiles = ChkWarnDangerous?.IsChecked == true;
            s.MarkDownloadsFromInternet = ChkMarkFromInternet?.IsChecked == true;
            s.UiLanguage = SelectedLanguageCode();
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
