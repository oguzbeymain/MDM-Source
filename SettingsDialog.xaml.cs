using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DownloadMuck
{
    public partial class SettingsDialog : UserControl
    {
        private AppSettings _settings = new();
        private string _currentDefaultFolder = "";
        private CancellationTokenSource? _updateCts;
        private bool _updateBusy;

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
            _currentDefaultFolder = currentDefaultFolder;

            TxtFolder.Text = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
                ? currentDefaultFolder
                : settings.DefaultDownloadFolder;
            ChkAutoStart.IsChecked = settings.AutoStart;
            ChkAutoStartMin.IsChecked = settings.AutoStartMinimized;
            ChkAutoStartMin.IsEnabled = settings.AutoStart;
            ChkDeleteFromDisk.IsChecked = settings.DeleteFilesFromDisk;
            ChkAutoExtract.IsChecked = settings.AutoExtractArchives;
            ChkDeleteArchive.IsChecked = settings.DeleteArchiveAfterExtract;
            ChkSchedule.IsChecked = settings.ScheduleEnabled;
            TxtSchedStart.Text = settings.ScheduleStartHour.ToString();
            TxtSchedEnd.Text = settings.ScheduleEndHour.ToString();
            TxtCrawlDepth.Text = settings.CrawlDepth.ToString();
            ChkRemoteLan.IsChecked = settings.RemoteApiLan;
            TxtApiToken.Text = settings.RemoteApiToken ?? "";
            ChkHttp3.IsChecked = settings.PreferHttp3;
            ChkAutoReconnect.IsChecked = settings.AutoReconnect;
            ChkNotifyDone.IsChecked = settings.NotifyOnComplete;
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
            string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            TxtVersion.Text = $"Sürüm {ver}";
            TxtUpdateCurrent.Text = $"Yüklü sürüm: v{UpdateService.CurrentVersionText}";
            if (!_updateBusy)
                TxtUpdateStatus.Text = "";

            SelectTab(initialTab ?? "general");
        }

        public void SelectTab(string tab)
        {
            switch ((tab ?? "").Trim().ToLowerInvariant())
            {
                case "extension":
                case "eklenti":
                    NavExtension.IsChecked = true;
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
            PanelExtension.Visibility = NavExtension.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAdvanced.Visibility = NavAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelUpdate.Visibility = NavUpdate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
                TxtUpdateStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            }
            catch { /* ignore */ }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string folder = (TxtFolder.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                InfoDialog.Show(OwnerWindow, "Klasör", "Geçerli bir indirme klasörü girin.");
                return;
            }

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                InfoDialog.Show(OwnerWindow, "Klasör", "Klasör oluşturulamadı.", ex.Message);
                return;
            }

            _settings.DefaultDownloadFolder = folder;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.AutoStartMinimized = ChkAutoStartMin.IsChecked == true;
            _settings.DeleteFilesFromDisk = ChkDeleteFromDisk.IsChecked == true;
            _settings.AutoExtractArchives = ChkAutoExtract.IsChecked == true;
            _settings.DeleteArchiveAfterExtract = ChkDeleteArchive.IsChecked == true;
            _settings.ScheduleEnabled = ChkSchedule.IsChecked == true;
            _ = int.TryParse(TxtSchedStart.Text, out int sh);
            _ = int.TryParse(TxtSchedEnd.Text, out int eh);
            _settings.ScheduleStartHour = Math.Clamp(sh, 0, 23);
            _settings.ScheduleEndHour = Math.Clamp(eh, 0, 23);
            _ = int.TryParse(TxtCrawlDepth.Text, out int depth);
            _settings.CrawlDepth = Math.Clamp(depth, 0, 3);
            _settings.RemoteApiLan = ChkRemoteLan.IsChecked == true;
            _settings.RemoteApiToken = TxtApiToken.Text?.Trim() ?? "";
            _settings.PreferHttp3 = ChkHttp3.IsChecked == true;
            _settings.AutoReconnect = ChkAutoReconnect.IsChecked == true;
            _settings.NotifyOnComplete = ChkNotifyDone.IsChecked == true;
            _ = int.TryParse(TxtSpeedLimit.Text, out int speedKb);
            _settings.SpeedLimitKBps = Math.Clamp(speedKb, 0, 1_000_000);
            _ = int.TryParse(TxtMaxConcurrent.Text, out int maxJobs);
            _settings.MaxConcurrentDownloads = Math.Clamp(maxJobs, 0, 50);
            _ = int.TryParse(TxtHttpChannels.Text, out int httpCh);
            _settings.HttpMaxChannels = Math.Clamp(httpCh, 0, ChannelBudget.MaxPerJob);
            _ = int.TryParse(TxtTorrentPort.Text, out int tport);
            _settings.TorrentListenPort = tport <= 0 ? 6881 : Math.Clamp(tport, 1, 65535);
            _settings.TorrentDht = ChkTorrentDht.IsChecked == true;
            _settings.TorrentLocalPeers = ChkTorrentLpd.IsChecked == true;
            _settings.TorrentPortForward = ChkTorrentUpnp.IsChecked == true;
            _settings.TorrentSequential = ChkTorrentSeq.IsChecked == true;
            _ = double.TryParse(TxtTorrentSeed.Text?.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seed);
            _settings.TorrentSeedRatio = Math.Clamp(seed, 0, 100);
            _settings.SkipDuplicateUrls = ChkSkipDup.IsChecked == true;
            _settings.SkipExtensions = TxtSkipExt.Text?.Trim() ?? "";
            _settings.SkipUrlContains = TxtSkipUrl.Text?.Trim() ?? "";
            _settings.SkipDomains = TxtSkipDomains.Text?.Trim() ?? "";
            _settings.SkipUrlRegex = TxtSkipRegex.Text?.Trim() ?? "";
            _settings.SkipMimeContains = TxtSkipMime.Text?.Trim() ?? "";
            _ = int.TryParse(TxtSkipMinMb.Text, out int minMb);
            _ = int.TryParse(TxtSkipMaxMb.Text, out int maxMb);
            _settings.SkipMinSizeMb = Math.Max(0, minMb);
            _settings.SkipMaxSizeMb = Math.Max(0, maxMb);
            _settings.RenamePattern = string.IsNullOrWhiteSpace(TxtRename.Text) ? "{name}{ext}" : TxtRename.Text.Trim();

            AppSettingsStore.Save(_settings);

            if (_settings.AutoStart)
                AutoStartHelper.Enable(_settings.AutoStartMinimized);
            else
                AutoStartHelper.Disable();

            Saved?.Invoke();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    }
}
