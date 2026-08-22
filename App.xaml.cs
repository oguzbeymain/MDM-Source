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

            if (!SingleInstance.TryAcquire())
            {
                Shutdown();
                return;
            }

            bool launchedByUpdater = e.Args.Any(a =>
                string.Equals(a, "--from-updater", StringComparison.OrdinalIgnoreCase));
            bool startBackground = e.Args.Any(a =>
                string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            string updaterPath = Path.Combine(AppContext.BaseDirectory, "MDM.Updater.exe");
            bool updaterExists = File.Exists(updaterPath);

            if (updaterExists && !launchedByUpdater && !IsDevelopmentBuild())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = true
                    });
                    SingleInstance.Release();
                    Shutdown();
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Updater baslatilamadi, ana uygulama aciliyor: {ex.Message}");
                }
            }

            try
            {
                AutoStartHelper.EnsureRegistered();

                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                if (startBackground)
                    main.HideToTray();

                SingleInstance.StartListening(() =>
                {
                    main.Dispatcher.BeginInvoke(() => main.ShowFromTray());
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Uygulama baslatilamadi:\n{ex.Message}",
                    "MDM",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                SingleInstance.Release();
                Shutdown(-1);
                return;
            }

            _ = Task.Run(ExtensionInstaller.EnsureInstalled);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SingleInstance.Release();
            base.OnExit(e);
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
