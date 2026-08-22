using System.Diagnostics;
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

            bool startBackground = e.Args.Any(a =>
                string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            // Güncelleme artık açılışta değil; Ayarlar → Güncelleme'den denetlenir.
            if (!SingleInstance.TryAcquire())
            {
                SingleInstance.RequestShow();
                Shutdown();
                return;
            }

            try
            {
                AutoStartHelper.EnsureRegistered();

                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                if (startBackground)
                    main.HideToTray();

                SingleInstance.StartListening(
                    onShowRequested: () => main.Dispatcher.BeginInvoke(() => main.ShowFromTray()),
                    onExitRequested: () => main.Dispatcher.BeginInvoke(() => main.ExitForUpdate()));
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
    }
}
