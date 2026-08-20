using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DownloadMuck
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            UpdateWindow? updateWindow = null;
            bool isDevBuild = IsDevelopmentBuild();

            try
            {
                if (!isDevBuild)
                {
                    updateWindow = new UpdateWindow();
                    updateWindow.Show();

                    var progress = new Progress<string>(msg => updateWindow.SetStatus(msg));
                    UpdateCheckResult result = await new UpdateService().CheckAndApplyUpdateAsync(progress);

                    if (result.RestartingForUpdate)
                    {
                        updateWindow.SetStatus(result.Message ?? "Yeniden başlatılıyor...");
                        await Task.Delay(600);
                        Shutdown();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Message) &&
                        result.Message.Contains("başarısız", StringComparison.OrdinalIgnoreCase))
                    {
                        updateWindow.SetStatus(result.Message);
                        await Task.Delay(1200);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update startup error: {ex}");
            }
            finally
            {
                updateWindow?.Close();
            }

            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }

        private static bool IsDevelopmentBuild()
        {
            if (Debugger.IsAttached) return true;

            string baseDir = AppContext.BaseDirectory;
            return baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
