using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace DownloadMuck
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool launchedByUpdater = e.Args.Any(a =>
                string.Equals(a, "--from-updater", StringComparison.OrdinalIgnoreCase));

            // Updater yoksa (gelistirme) dogrudan ac
            string updaterPath = Path.Combine(AppContext.BaseDirectory, "MDM.Updater.exe");
            bool updaterExists = File.Exists(updaterPath);

            if (updaterExists && !launchedByUpdater && !IsDevelopmentBuild())
            {
                // Guncelleme ayri process'te yapilsin; ana exe kilitlenmesin
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
                Shutdown();
                return;
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
