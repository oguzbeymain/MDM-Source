using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace MDM
{
    public enum FirefoxInstallChoice
    {
        Cancel,
        Temporary,
        OpenDeveloperEdition
    }

    public partial class FirefoxInstallDialog : Window
    {
        public const string DeveloperEditionUrl = ExtensionInstaller.DeveloperEditionDownloadUrl;

        public FirefoxInstallChoice Choice { get; private set; } = FirefoxInstallChoice.Cancel;

        private readonly bool _developerAlreadyInstalled;

        public FirefoxInstallDialog(bool developerAlreadyInstalled)
        {
            InitializeComponent();
            _developerAlreadyInstalled = developerAlreadyInstalled;

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Choice = FirefoxInstallChoice.Cancel;
                    DialogResult = false;
                    e.Handled = true;
                }
            };
            Loaded += (_, _) => BtnTemporary.Focus();

            FlowDirection = Loc.Flow;
            ApplyLocalizedTexts();
        }

        private void ApplyLocalizedTexts()
        {
            Title = Loc.T("firefox.title", "Firefox eklentisi");
            if (TxtTitleBar != null) TxtTitleBar.Text = Loc.T("firefox.title", "Firefox eklentisi");
            if (TxtHeader != null) TxtHeader.Text = Loc.T("firefox.header", "Kurulum seçimi");
            if (TxtTempHead != null) TxtTempHead.Text = Loc.T("firefox.temp.head", "Geçici kurulum (normal Firefox)");
            if (TxtTempBody != null) TxtTempBody.Text = Loc.T("firefox.temp.body",
                "Bu oturumda çalışır. Firefox’u kapatınca eklenti silinir — tekrar kurman gerekir. Mozilla, normal Firefox’ta imzasız eklentiyi kalıcı tutmaya izin vermez.");
            if (TxtPermHead != null) TxtPermHead.Text = Loc.T("firefox.perm.head", "Kalıcı kurulum (önerilen)");
            if (TxtPermBody != null) TxtPermBody.Text = Loc.T("firefox.perm.body",
                "Firefox Developer Edition kurarsan eklenti Eklentiler listesinde kalır ve yeniden açılışta da çalışır. Kurduktan sonra MDM’de tekrar «Firefox otomatik kur»a bas.");
            if (TxtDevBtn != null)
                TxtDevBtn.Text = _developerAlreadyInstalled
                    ? Loc.T("firefox.btn.dev_install", "Developer Edition ile kalıcı kur")
                    : Loc.T("firefox.btn.dev_download", "Firefox Developer Edition’ı indir");
            if (TxtTempBtn != null) TxtTempBtn.Text = Loc.T("firefox.btn.temp", "Geçici kur (bu oturum)");
            if (BtnCancel != null) BtnCancel.Content = Loc.T("firefox.btn.cancel", "Vazgeç");
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = FirefoxInstallChoice.Cancel;
            DialogResult = false;
        }

        private void BtnTemporary_Click(object sender, RoutedEventArgs e)
        {
            Choice = FirefoxInstallChoice.Temporary;
            DialogResult = true;
        }

        private void BtnDevEdition_Click(object sender, RoutedEventArgs e)
        {
            Choice = FirefoxInstallChoice.OpenDeveloperEdition;
            DialogResult = true;
        }

        public static FirefoxInstallChoice Show(Window? owner, bool developerAlreadyInstalled)
        {
            var dlg = new FirefoxInstallDialog(developerAlreadyInstalled);
            if (owner != null) dlg.Owner = owner;
            dlg.ShowDialog();
            return dlg.Choice;
        }

        public static void OpenDeveloperEditionPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DeveloperEditionUrl,
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }
    }
}
