using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DownloadMuck
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // UI thread ve arka plan hatalarinda uygulamayi ayaga tut
            DispatcherUnhandledException += (_, args) =>
            {
                Debug.WriteLine($"UI exception: {args.Exception}");
                try
                {
                    InfoDialog.Show(MainWindow, "Beklenmeyen hata",
                        "İşlem sırasında bir hata oluştu. Uygulama açık kalmaya devam ediyor.",
                        args.Exception.Message);
                }
                catch { /* dialog da fail olursa yut */ }
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Debug.WriteLine($"Domain exception: {args.ExceptionObject}");
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Debug.WriteLine($"Task exception: {args.Exception}");
                args.SetObserved();
            };

            base.OnStartup(e);

            bool launchedByUpdater = e.Args.Any(a =>
                string.Equals(a, "--from-updater", StringComparison.OrdinalIgnoreCase));

            string updaterPath = Path.Combine(AppContext.BaseDirectory, "MDM.Updater.exe");
            bool updaterExists = File.Exists(updaterPath);

            if (updaterExists && !launchedByUpdater && !IsDevelopmentBuild())
            {
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

            _ = Task.Run(ExtensionInstaller.EnsureInstalled);
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
