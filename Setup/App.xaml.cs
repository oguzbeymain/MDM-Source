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

        protected override void OnStartup(StartupEventArgs e)
        {
            foreach (string arg in e.Args)
            {
                string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (a is "uninstall" or "remove") UninstallMode = true;
                else if (a is "silent" or "s" or "quiet") Silent = true;
            }

            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

            // Kaldırmada kurulumda seçilen dil kullanılır; kurulumda Windows dili
            SetupLoc.Apply(UninstallMode
                ? Installer.ReadInstalledLanguage() ?? SetupLoc.DetectSystemLanguage()
                : SetupLoc.DetectSystemLanguage());
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
