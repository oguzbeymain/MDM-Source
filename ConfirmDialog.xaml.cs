using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DownloadMuck
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog(string title, string message, string detail = "",
            string confirmText = "Onayla", string cancelText = "Vazgeç", bool danger = false)
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

            if (FindName("BtnConfirm") is System.Windows.Controls.Button conf)
            {
                conf.Content = confirmText;
                if (danger)
                    conf.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            }
            if (FindName("BtnCancelLabel") is System.Windows.Controls.Button cancel)
                cancel.Content = cancelText;

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

        /// <param name="forceFloating">
        /// true: her zaman ayrı pencere (mini indirme ekranı gibi).
        /// false: owner MainWindow ise uygulama içi karartmalı modal.
        /// </param>
        public static bool Show(Window? owner, string title, string message, string detail = "",
            string confirmText = "Onayla", string cancelText = "Vazgeç", bool danger = false,
            bool forceFloating = false)
        {
            bool useAppModal = !forceFloating && (
                owner is MainWindow ||
                (owner == null && Application.Current?.MainWindow is MainWindow));

            if (useAppModal)
                return AppModal.Confirm(title, message, detail, confirmText, cancelText, danger);

            var dlg = new ConfirmDialog(title, message, detail, confirmText, cancelText, danger);
            // WindowStyle.None owner (mini indirme) ShowDialog'u yutabiliyor — bağımsız aç
            bool attachOwner = owner != null && owner.IsLoaded && owner.WindowStyle != WindowStyle.None;
            if (attachOwner)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Topmost = true;
            dlg.ShowDialog();
            return dlg.Confirmed;
        }
    }
}
