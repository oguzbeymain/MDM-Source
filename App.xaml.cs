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

            bool startBackground = e.Args.Any(a =>
                string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

            string verb = "";
            string cliValue = "";
            if (CliArgs.TryParseVerb(e.Args, out verb, out cliValue))
            {
                if (verb is "add" or "grab")
                    IpcInbox.Enqueue(cliValue, verb == "grab");
                else if (verb is "pause" or "resume" or "cancel")
                    IpcInbox.EnqueueAction(verb, cliValue);
            }

            if (!SingleInstance.TryAcquire())
            {
                if (CliArgs.IsRemoteQuery(verb))
                    RunCliRemote(verb, cliValue);
                else
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
                {
                    main.BeginBackgroundCaptureQuiet();
                    main.HideToTray();
                }

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
            NetworkWatcher.Shared.Start();
        }

        private static void RunCliRemote(string verb, string value)
        {
            try
            {
                string method = "GET";
                string path = "/jobs";
                string? body = null;
                if (verb == "pause" || verb == "resume" || verb == "cancel")
                {
                    method = "POST";
                    path = $"/jobs/{Uri.EscapeDataString(value)}/{verb}";
                }

                var (status, text) = CliRemote.CallAsync(method, path, body).GetAwaiter().GetResult();
                string outPath = Path.Combine(AppSettingsStore.StoreDir, "cli-last.json");
                Directory.CreateDirectory(AppSettingsStore.StoreDir);
                File.WriteAllText(outPath, $"{{\"status\":{status},\"body\":{System.Text.Json.JsonSerializer.Serialize(text)}}}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CLI remote: {ex.Message}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SingleInstance.Release();
            try { NetworkWatcher.Shared.Stop(); } catch { /* ignore */ }
            try { TorrentEngineHost.Shutdown(); } catch { /* ignore */ }
            base.OnExit(e);
        }
    }
}
