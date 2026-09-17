using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MDM.Setup
{
    public partial class SetupWindow : Window
    {
        private enum Step { Maintenance, Welcome, Options, Progress, Done, Uninstall }

        private readonly InstallOptions _options = new();
        private Step _step;
        private bool _busy;
        private bool _completed;
        private bool _syncingTheme;
        private bool _ready;
        /// <summary>Kurulu sürüm varsa setup bakım kipinde açılır (onar / değiştir / kaldır).</summary>
        private readonly string? _installedDir;
        private readonly string? _installedVersion;
        private bool _repairing;

        public SetupWindow()
        {
            InitializeComponent();

            _installedDir = Installer.FindExistingInstall();
            _installedVersion = Installer.ReadInstalledVersion();

            _options.Language = SetupLoc.Code;
            _options.InstallDir = _installedDir ?? InstallOptions.DefaultInstallDir;
            // Yeniden kurulumda kullanıcının klasörü varsayılana dönmesin
            _options.DownloadDir = Installer.ReadInstalledDownloadFolder() ?? _options.DownloadDir;

            foreach (var lang in SetupLoc.Languages)
                LstLanguage.Items.Add(new ListBoxItem { Content = lang.NativeName, Tag = lang.Code });
            SelectLanguageInList(SetupLoc.Code);

            TxtInstallPath.Text = _options.InstallDir;
            TxtDownloadPath.Text = _options.DownloadDir;
            TxtVersion.Text = "v" + Installer.Version;

            bool dark = !string.Equals(Installer.ReadInstalledTheme(), "Light", StringComparison.OrdinalIgnoreCase);
            _options.DarkTheme = dark;
            ApplyTheme(dark);
            _ready = true;
            if (dark) ChipDark.IsChecked = true; else ChipLight.IsChecked = true;

            _step = App.UninstallMode ? Step.Uninstall
                : _installedDir != null ? Step.Maintenance
                : Step.Welcome;
            ApplyTexts();
            ShowStep(_step);

            if (App.Silent)
                Loaded += RunSilentAsync;
        }

        /// <summary>
        /// /silent: Windows'un QuietUninstallString çağrısı ve otomatik testler onay
        /// beklemeden çalışır; iş bitince pencere kendini kapatır.
        /// </summary>
        private async void RunSilentAsync(object sender, RoutedEventArgs e)
        {
            Loaded -= RunSilentAsync;
            if (App.UninstallMode)
            {
                await RunUninstallAsync();
            }
            else if (ReadOptions())
            {
                // Uygulama içi güncelleme buradan geçer: mevcut tema/dil/klasör korunur
                _options.PreserveExistingSettings = true;
                await RunInstallAsync();

                // Güncelleme sonrası kullanıcı uygulamayı kapatmış gibi kalmasın
                if (_completed) Installer.LaunchApp(_options.InstallDir);
            }

            Close();
        }

        // --- Gezinme ---

        private void ShowStep(Step step)
        {
            _step = step;
            PageMaintenance.Visibility = Collapse(step == Step.Maintenance);
            PageWelcome.Visibility = Collapse(step == Step.Welcome);
            PageOptions.Visibility = Collapse(step == Step.Options);
            PageProgress.Visibility = Collapse(step == Step.Progress);
            PageDone.Visibility = Collapse(step == Step.Done);
            PageUninstall.Visibility = Collapse(step == Step.Uninstall);

            // Bakım ekranı varken dil ve kaldırma sayfalarından da geri dönülebilir
            bool fromMaintenance = _installedDir != null && !App.UninstallMode;
            BtnBack.Visibility = Collapse(step == Step.Options
                || (fromMaintenance && step is Step.Welcome or Step.Uninstall));
            BtnCancel.Visibility = Collapse(step is Step.Maintenance or Step.Welcome or Step.Options or Step.Uninstall);
            BtnNext.Visibility = Collapse(step is not (Step.Progress or Step.Maintenance));

            BtnNext.Content = step switch
            {
                Step.Welcome => SetupLoc.T("setup.next", "İleri"),
                Step.Options => SetupLoc.T("setup.install", "Kur"),
                Step.Done => SetupLoc.T("setup.close", "Kapat"),
                Step.Uninstall => SetupLoc.T("setup.uninstall_action", "Kaldır"),
                _ => SetupLoc.T("setup.next", "İleri")
            };

            TxtFooterHint.Text = step == Step.Options
                ? SetupLoc.T("setup.footer_hint", "Kurulum yönetici izni gerektirmez.")
                : "";
        }

        private static Visibility Collapse(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            switch (_step)
            {
                case Step.Welcome:
                    ShowStep(Step.Options);
                    break;

                case Step.Options:
                    if (!ReadOptions()) return;
                    await RunInstallAsync();
                    break;

                case Step.Uninstall:
                    await RunUninstallAsync();
                    break;

                case Step.Done:
                    if (_completed && !App.UninstallMode && ChkLaunch.IsChecked == true)
                        Installer.LaunchApp(_options.InstallDir);
                    Close();
                    break;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            bool fromMaintenance = _installedDir != null && !App.UninstallMode;
            ShowStep(_step switch
            {
                Step.Options => Step.Welcome,
                _ when fromMaintenance => Step.Maintenance,
                _ => Step.Welcome
            });
        }

        // --- Bakım ekranı ---

        /// <summary>Onar: paket yeniden açılır, kısayol ve kayıt girdileri yenilenir, ayarlar korunur.</summary>
        private async void BtnMaintRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            _repairing = true;
            _options.PreserveExistingSettings = true;
            _options.InstallDir = _installedDir ?? _options.InstallDir;
            await RunInstallAsync();
            _repairing = false;
        }

        /// <summary>
        /// Değiştir: dil sayfasından başlar (dil de değiştirilebilsin), ardından
        /// seçenekler gelir. Burada yapılan seçimler mevcut ayarların üzerine yazılır.
        /// </summary>
        private void BtnMaintModify_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _options.PreserveExistingSettings = false;
            ShowStep(Step.Welcome);
        }

        private void BtnMaintRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            ShowStep(Step.Uninstall);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        // --- Kurulum / kaldırma ---

        private bool ReadOptions()
        {
            string install = TxtInstallPath.Text.Trim();
            string download = TxtDownloadPath.Text.Trim();

            if (install.Length == 0 || !Path.IsPathFullyQualified(install))
            {
                ShowError(SetupLoc.T("setup.error_install_path", "Geçerli bir kurulum klasörü seçin."));
                return false;
            }
            if (download.Length == 0 || !Path.IsPathFullyQualified(download))
            {
                ShowError(SetupLoc.T("setup.error_download_path", "Geçerli bir indirme klasörü seçin."));
                return false;
            }

            _options.InstallDir = install;
            _options.DownloadDir = download;
            _options.DarkTheme = ChipDark.IsChecked == true;
            _options.DesktopShortcut = ChkDesktop.IsChecked == true;
            _options.StartMenuShortcut = ChkStartMenu.IsChecked == true;
            _options.AutoStart = ChkAutoStart.IsChecked == true;
            _options.CreateCategoryFolders = ChkFolders.IsChecked == true;
            return true;
        }

        private async Task RunInstallAsync()
        {
            _busy = true;
            ShowStep(Step.Progress);
            TxtProgressTitle.Text = _repairing
                ? SetupLoc.T("setup.repairing", "Onarılıyor…")
                : SetupLoc.T("setup.installing", "Kuruluyor…");
            BarProgress.Value = 0;

            var progress = new Progress<(string Step, double Percent)>(p =>
            {
                TxtProgressStep.Text = p.Step;
                BarProgress.Value = p.Percent;
            });

            try
            {
                await Installer.InstallAsync(_options, progress);
                _completed = true;
                TxtDoneTitle.Text = _repairing
                    ? SetupLoc.T("setup.repair_done", "Onarım tamamlandı")
                    : SetupLoc.T("setup.done_title", "Kurulum tamamlandı");
                TxtDoneText.Text = string.Format(
                    _repairing
                        ? SetupLoc.T("setup.repair_done_text", "{0} klasöründeki dosyalar yenilendi.")
                        : SetupLoc.T("setup.done_text", "MuckDownloadManager {0} klasörüne kuruldu."),
                    _options.InstallDir);
                _busy = false;
                ShowStep(Step.Done);
            }
            catch (Exception ex)
            {
                _busy = false;
                ShowStep(_repairing ? Step.Maintenance : Step.Options);
                ShowError(ex.Message);
            }
        }

        private async Task RunUninstallAsync()
        {
            _busy = true;
            bool removeData = ChkRemoveData.IsChecked == true;
            ShowStep(Step.Progress);
            TxtProgressTitle.Text = SetupLoc.T("setup.uninstalling", "Kaldırılıyor…");
            BarProgress.Value = 0;

            var progress = new Progress<(string Step, double Percent)>(p =>
            {
                TxtProgressStep.Text = p.Step;
                BarProgress.Value = p.Percent;
            });

            try
            {
                await Installer.UninstallAsync(removeData, progress);
                _completed = true;
                TxtDoneTitle.Text = SetupLoc.T("setup.uninstall_done", "Kaldırma tamamlandı");
                TxtDoneText.Text = SetupLoc.T("setup.uninstall_done_text",
                    "MuckDownloadManager bilgisayarınızdan kaldırıldı.");
                ChkLaunch.Visibility = Visibility.Collapsed;
                _busy = false;
                ShowStep(Step.Done);
            }
            catch (Exception ex)
            {
                _busy = false;
                ShowStep(Step.Uninstall);
                ShowError(ex.Message);
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, SetupLoc.T("setup.error_title", "Kurulum hatası"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // --- Dil / tema / klasör seçimi ---

        private void SelectLanguageInList(string code)
        {
            foreach (object item in LstLanguage.Items)
            {
                if (item is ListBoxItem lbi && (string?)lbi.Tag == code)
                {
                    LstLanguage.SelectedItem = lbi;
                    return;
                }
            }
            LstLanguage.SelectedIndex = 0;
        }

        private void LstLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstLanguage.SelectedItem is not ListBoxItem item || (string?)item.Tag is not string code) return;
            _options.Language = code;
            SetupLoc.Apply(code);
            FlowDirection = SetupLoc.Flow;
            ApplyTexts();
            ShowStep(_step);
        }

        private void ChipTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready || _syncingTheme) return;

            bool dark = ReferenceEquals(sender, ChipDark);
            _syncingTheme = true;
            try
            {
                ChipDark.IsChecked = dark;
                ChipLight.IsChecked = !dark;
            }
            finally { _syncingTheme = false; }

            _options.DarkTheme = dark;
            ApplyTheme(dark);
        }

        /// <summary>Seçili temanın kapatılması engellenir; biri her zaman açık kalır.</summary>
        private void ChipTheme_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_ready || _syncingTheme || sender is not ToggleButton chip) return;

            _syncingTheme = true;
            try { chip.IsChecked = true; }
            finally { _syncingTheme = false; }
        }

        private void BtnBrowseInstall_Click(object sender, RoutedEventArgs e)
        {
            string? picked = PickFolder(TxtInstallPath.Text);
            if (picked != null)
                TxtInstallPath.Text = Path.Combine(picked, Installer.AppName);
        }

        private void BtnBrowseDownload_Click(object sender, RoutedEventArgs e)
        {
            string? picked = PickFolder(TxtDownloadPath.Text);
            if (picked != null) TxtDownloadPath.Text = picked;
        }

        private string? PickFolder(string current)
        {
            var dialog = new OpenFolderDialog
            {
                Title = SetupLoc.T("setup.pick_folder", "Klasör seçin"),
                Multiselect = false
            };
            try
            {
                string? start = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                    dialog.InitialDirectory = start;
            }
            catch { /* başlangıç klasörü önemsiz */ }

            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }

        /// <summary>Sihirbaz, seçilen tema ile aynı görünür.</summary>
        private void ApplyTheme(bool dark)
        {
            Set("Surface", dark ? "#1E1E1E" : "#FFFFFF");
            Set("TitleBar", dark ? "#242424" : "#F5F5F7");
            Set("EdgeBorder", dark ? "#3A3A3A" : "#D8D8DE");
            Set("TextFg", dark ? "#F2F2F2" : "#1A1A1A");
            Set("MutedFg", dark ? "#9A9A9A" : "#666666");
            Set("InputBg", dark ? "#252525" : "#F0F0F3");
            Set("SoftBg", dark ? "#2A2A2A" : "#EEEEF0");
            Set("SoftHoverBg", dark ? "#333333" : "#E0E0E4");

            static SolidColorBrush Brush(string hex)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }

            void Set(string key, string hex) => Application.Current.Resources[key] = Brush(hex);
        }

        private void ApplyTexts()
        {
            string appName = Installer.AppName;

            TxtWindowTitle.Text = App.UninstallMode
                ? string.Format(SetupLoc.T("setup.window_title_uninstall", "{0} — Kaldır"), appName)
                : _installedDir != null
                    ? string.Format(SetupLoc.T("setup.window_title_maintenance", "{0} — Bakım"), appName)
                    : string.Format(SetupLoc.T("setup.window_title", "{0} — Kurulum"), appName);

            ApplyMaintenanceTexts(appName);

            TxtWelcomeTitle.Text = string.Format(SetupLoc.T("setup.welcome_title", "{0} Kurulumu"), appName);
            TxtLanguageLabel.Text = SetupLoc.T("setup.language", "Kurulum dili");
            TxtLanguageHint.Text = SetupLoc.T("setup.language_hint",
                "Seçtiğiniz dil uygulamada kullanılır ve indirme kategorisi klasörleri bu dilde oluşturulur.");

            TxtOptionsTitle.Text = SetupLoc.T("setup.options_title", "Kurulum seçenekleri");
            TxtInstallFolder.Text = SetupLoc.T("setup.install_folder", "Kurulum klasörü");
            TxtDownloadFolder.Text = SetupLoc.T("setup.download_folder", "İndirme klasörü");
            BtnBrowseInstall.Content = SetupLoc.T("setup.browse", "Gözat");
            BtnBrowseDownload.Content = SetupLoc.T("setup.browse", "Gözat");
            TxtThemeLabel.Text = SetupLoc.T("setup.theme", "Görünüm");
            ChipDark.Content = SetupLoc.T("setup.theme_dark", "Koyu tema");
            ChipLight.Content = SetupLoc.T("setup.theme_light", "Açık tema");
            ChkDesktop.Content = SetupLoc.T("setup.shortcut_desktop", "Masaüstüne kısayol ekle");
            ChkStartMenu.Content = SetupLoc.T("setup.shortcut_start", "Başlat menüsüne ekle");
            ChkAutoStart.Content = SetupLoc.T("setup.autostart", "Windows açılışında başlat");
            ChkFolders.Content = SetupLoc.T("setup.create_folders", "Kategori klasörlerini seçilen dilde oluştur");

            TxtProgressTitle.Text = App.UninstallMode
                ? SetupLoc.T("setup.uninstalling", "Kaldırılıyor…")
                : SetupLoc.T("setup.installing", "Kuruluyor…");

            TxtDoneTitle.Text = SetupLoc.T("setup.done_title", "Kurulum tamamlandı");
            TxtDoneText.Text = string.Format(
                SetupLoc.T("setup.done_text", "MuckDownloadManager {0} klasörüne kuruldu."), _options.InstallDir);
            ChkLaunch.Content = SetupLoc.T("setup.launch", "Uygulamayı başlat");

            TxtUninstallTitle.Text = string.Format(
                SetupLoc.T("setup.uninstall_title", "{0} kaldırılsın mı?"), appName);
            TxtUninstallText.Text = SetupLoc.T("setup.uninstall_text",
                "Uygulama dosyaları ve kısayollar bilgisayarınızdan kaldırılır. İndirdiğiniz dosyalar silinmez.");
            ChkRemoveData.Content = SetupLoc.T("setup.uninstall_remove_data", "Ayarları ve kategori bilgilerini de sil");

            BtnBack.Content = SetupLoc.T("setup.back", "Geri");
            BtnCancel.Content = SetupLoc.T("setup.cancel", "İptal");
        }

        /// <summary>
        /// Bakım ekranı: kurulu sürüm setup'takinden eskiyse ilk seçenek onarım yerine
        /// güncelleme olarak sunulur — ikisi de paketi yeniden açar.
        /// </summary>
        private void ApplyMaintenanceTexts(string appName)
        {
            bool upgrade = IsUpgrade();

            TxtMaintTitle.Text = string.Format(
                SetupLoc.T("setup.maint_title", "{0} zaten kurulu"), appName);
            TxtMaintVersions.Text = _installedVersion == null
                ? "v" + Installer.Version
                : string.Format(SetupLoc.T("setup.maint_versions", "Yüklü: v{0}  •  Bu kurulum: v{1}"),
                    _installedVersion, Installer.Version);
            TxtMaintHint.Text = SetupLoc.T("setup.maint_hint", "Ne yapmak istediğinizi seçin.");

            TxtRepairTitle.Text = upgrade
                ? SetupLoc.T("setup.maint_upgrade", "Güncelle")
                : SetupLoc.T("setup.maint_repair", "Onar");
            TxtRepairText.Text = upgrade
                ? SetupLoc.T("setup.maint_upgrade_text",
                    "Kurulu sürüm bu kurulumdaki sürüme yükseltilir. Ayarlarınız, kategorileriniz ve indirmeleriniz korunur.")
                : SetupLoc.T("setup.maint_repair_text",
                    "Eksik veya bozuk dosyalar orijinalleriyle değiştirilir, kısayollar ve kayıt defteri girdileri yenilenir. Ayarlarınız korunur.");

            TxtModifyTitle.Text = SetupLoc.T("setup.maint_modify", "Değiştir");
            TxtModifyText.Text = SetupLoc.T("setup.maint_modify_text",
                "Dil, tema, klasörler, kısayollar ve Windows açılışında başlatma seçeneklerini yeniden ayarlayın.");

            TxtRemoveTitle.Text = SetupLoc.T("setup.maint_remove", "Kaldır");
            TxtRemoveText.Text = SetupLoc.T("setup.maint_remove_text",
                "Uygulamayı, kısayolları ve kayıt defteri girdilerini bilgisayarınızdan siler. İndirdiğiniz dosyalar kalır.");
        }

        /// <summary>Setup'ın sürümü kurulu olandan yeni mi?</summary>
        private bool IsUpgrade()
        {
            if (_installedVersion == null) return false;
            return Version.TryParse(_installedVersion, out Version? installed)
                && Version.TryParse(Installer.Version, out Version? setup)
                && setup > installed;
        }
    }
}
