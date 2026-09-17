using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MDM.Setup
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "mdm-setup-error.log");

        /// <summary>/uninstall ile açıldıysa sihirbaz kaldırma modunda başlar.</summary>
        public static bool UninstallMode { get; private set; }

        public static bool Silent { get; private set; }

        /// <summary>Windows "Uygulamalar" listesindeki Değiştir düğmesi bu kiple açar.</summary>
        public static bool MaintenanceMode { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            foreach (string arg in e.Args)
            {
                string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (a is "uninstall" or "remove") UninstallMode = true;
                else if (a is "silent" or "s" or "quiet") Silent = true;
                else if (a is "maintenance" or "modify" or "repair") MaintenanceMode = true;
            }

            // Kurulum klasöründeki Uninstall.exe çift tıklanınca doğrudan kaldırma açılır;
            // aynı dosya /maintenance ile çağrılınca onar/değiştir/kaldır ekranı gelir
            if (!UninstallMode && !MaintenanceMode
                && Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
                    .StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
                UninstallMode = true;

            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

            // Uygulama kuruluysa sihirbaz onun dilinde açılır (kaldırma, onarım, bakım);
            // ilk kurulumda Windows dili seçilir
            SetupLoc.Apply(Installer.ReadInstalledLanguage() ?? SetupLoc.DetectSystemLanguage());
            base.OnStartup(e);

            var window = new SetupWindow();
            MainWindow = window;
            window.Show();
        }

        /// <summary>Kurulum sessizce kapanmaz; hata kullanıcıya gösterilir ve loglanır.</summary>
        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception);
            MessageBox.Show(e.Exception.Message, SetupLoc.T("setup.error_title", "Kurulum hatası"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void Log(Exception? ex)
        {
            if (ex == null) return;
            try { File.AppendAllText(LogPath, $"{DateTime.Now:s} {ex}{Environment.NewLine}"); }
            catch { /* log yazılamazsa yut */ }
        }
    }
}
