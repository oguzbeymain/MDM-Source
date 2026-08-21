using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog(string title, string message, string detail = "")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            if (string.IsNullOrWhiteSpace(detail))
            {
                TxtDetail.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtDetail.Text = detail;
                TxtDetail.Visibility = Visibility.Visible;
            }

            Owner = Application.Current?.MainWindow;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Confirmed = false; Close(); }
                else if (e.Key == Key.Enter) { Confirmed = true; Close(); }
            };
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        public static bool Show(Window? owner, string title, string message, string detail = "")
        {
            var dlg = new ConfirmDialog(title, message, detail);
            if (owner != null) dlg.Owner = owner;
            dlg.ShowDialog();
            return dlg.Confirmed;
        }
    }
}
