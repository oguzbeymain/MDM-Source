using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MDM
{
    public partial class InfoDialog : Window
    {
        private DispatcherTimer? _copiedTimer;

        public InfoDialog(string title, string message, string detail = "", string? copyPath = null)
        {
            InitializeComponent();
            FlowDirection = Loc.Flow;
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            BtnOk.Content = Loc.T("dialog.ok", "Tamam");
            if (BtnCopyPath != null)
                BtnCopyPath.Content = Loc.T("dialog.copy", "Kopyala");
            if (string.IsNullOrWhiteSpace(detail))
                TxtDetail.Visibility = Visibility.Collapsed;
            else
            {
                TxtDetail.Text = detail;
                TxtDetail.Visibility = Visibility.Visible;
            }

            ApplyCopyPath(copyPath);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Escape or Key.Enter)
                {
                    DialogResult = true;
                    e.Handled = true;
                }
            };
            Loaded += (_, _) => BtnOk.Focus();
        }

        private void ApplyCopyPath(string? copyPath)
        {
            if (PathPanel == null || TxtPath == null) return;
            if (string.IsNullOrWhiteSpace(copyPath))
            {
                PathPanel.Visibility = Visibility.Collapsed;
                return;
            }

            TxtPath.Text = copyPath;
            PathPanel.Visibility = Visibility.Visible;
            TxtPath.ToolTip = Loc.T("dialog.copy_path_tip", "Yolu kopyalamak için seçin veya Kopyala’ya basın.");
        }

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(TxtPath.Text) || BtnCopyPath == null) return;
                Clipboard.SetText(TxtPath.Text);
                BtnCopyPath.Content = Loc.T("dialog.copied", "Kopyalandı");
                _copiedTimer?.Stop();
                _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                _copiedTimer.Tick += (_, _) =>
                {
                    _copiedTimer.Stop();
                    if (BtnCopyPath != null)
                        BtnCopyPath.Content = Loc.T("dialog.copy", "Kopyala");
                };
                _copiedTimer.Start();
            }
            catch { /* ignore */ }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        public static void Show(Window? owner, string title, string message, string detail = "", string? copyPath = null)
        {
            // Mini indirme vb. pencereler kendi üzerinde açsın
            if (owner is not null and not MainWindow)
            {
                var floating = new InfoDialog(title, message, detail, copyPath) { Owner = owner, Topmost = true };
                floating.ShowDialog();
                return;
            }

            if (Application.Current?.MainWindow is MainWindow)
            {
                AppModal.Info(title, message, detail, copyPath);
                return;
            }

            var dlg = new InfoDialog(title, message, detail, copyPath);
            if (owner != null) dlg.Owner = owner;
            dlg.ShowDialog();
        }
    }
}
