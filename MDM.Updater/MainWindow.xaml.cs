using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MDM.Updater
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public void SetStatus(string message) => TxtStatus.Text = message;

        public async Task RunUpdateFlowAsync()
        {
            var progress = new Progress<string>(SetStatus);
            UpdateResult result = await AppUpdater.CheckAndApplyAsync(progress);

            if (result.ApplyingUpdate)
            {
                SetStatus(result.Message ?? "Guncelleme uygulanıyor...");
                await Task.Delay(500);
                Application.Current.Shutdown();
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
                SetStatus(result.Message);

            await Task.Delay(result.HadError ? 1500 : 400);
            LaunchMainAppAndExit();
        }

        public void LaunchMainAppAndExit()
        {
            string appDir = AppUpdater.GetInstallDirectory();
            string mainExe = Path.Combine(appDir, "DownloadMuck.exe");

            if (!File.Exists(mainExe))
            {
                SetStatus("DownloadMuck.exe bulunamadi.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                WorkingDirectory = appDir,
                Arguments = "--from-updater",
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
    }
}
