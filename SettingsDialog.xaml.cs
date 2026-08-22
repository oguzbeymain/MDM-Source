using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace DownloadMuck
{
    public partial class SettingsDialog : Window
    {
        private readonly AppSettings _settings;

        public SettingsDialog(AppSettings settings, string currentDefaultFolder)
        {
            InitializeComponent();
            _settings = settings;

            TxtFolder.Text = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder)
                ? currentDefaultFolder
                : settings.DefaultDownloadFolder;
            ChkAutoStart.IsChecked = settings.AutoStart;
            ChkAutoStartMin.IsChecked = settings.AutoStartMinimized;
            ChkAutoStartMin.IsEnabled = settings.AutoStart;

            TxtExtPath.Text = ExtensionInstaller.InstallRoot;
            string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            TxtVersion.Text = $"Sürüm {ver}";
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            ChkAutoStartMin.IsEnabled = ChkAutoStart.IsChecked == true;
        }

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
                InfoDialog.Show(this, "Eklenti", "Hazır.",
                    "Tarayıcıdaki eklenti bu klasörü kullanıyor:\n" + ExtensionInstaller.InstallRoot);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Eklenti", "İşlem başarısız.", ex.Message);
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
                InfoDialog.Show(this, "Klasör", "Açılamadı.", ex.Message);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string folder = (TxtFolder.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                InfoDialog.Show(this, "Klasör", "Geçerli bir indirme klasörü girin.");
                return;
            }

            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Klasör", "Klasör oluşturulamadı.", ex.Message);
                return;
            }

            _settings.DefaultDownloadFolder = folder;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.AutoStartMinimized = ChkAutoStartMin.IsChecked == true;

            AppSettingsStore.Save(_settings);

            if (_settings.AutoStart)
                AutoStartHelper.Enable(_settings.AutoStartMinimized);
            else
                AutoStartHelper.Disable();

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
