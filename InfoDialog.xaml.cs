using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class InfoDialog : Window
    {
        public InfoDialog(string title, string message, string detail = "")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            if (string.IsNullOrWhiteSpace(detail))
                TxtDetail.Visibility = Visibility.Collapsed;
            else
            {
                TxtDetail.Text = detail;
                TxtDetail.Visibility = Visibility.Visible;
            }

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

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        public static void Show(Window? owner, string title, string message, string detail = "")
        {
            if (Application.Current?.MainWindow is MainWindow)
            {
                AppModal.Info(title, message, detail);
                return;
            }

            var dlg = new InfoDialog(title, message, detail);
            if (owner != null) dlg.Owner = owner;
            dlg.ShowDialog();
        }
    }
}
