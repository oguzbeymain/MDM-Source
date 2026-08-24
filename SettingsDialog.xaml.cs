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
                case "update":
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
            PanelGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelExtension.Visibility = NavExtension.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
