using System.Windows;
using System.Windows.Controls;
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

            if (FindName("BtnConfirm") is Button conf)
            {
                conf.Content = confirmText;
                if (danger)
                    conf.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            }
            if (FindName("BtnCancelLabel") is Button cancel)
                cancel.Content = cancelText;

            ApplyThemeSurface(ThemeService.IsLight);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Confirmed = false; Close(); }
                else if (e.Key == Key.Enter) { Confirmed = true; Close(); }
            };
        }

        public void ApplyThemeSurface(bool light)
        {
            var card = light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1B, 0x1B, 0x1B);
            var title = light ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x22, 0x22, 0x22);
            var border = light ? Color.FromRgb(0xD8, 0xD8, 0xDE) : Color.FromRgb(0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x88, 0x88, 0x88);
            var soft = light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x2A, 0x2A, 0x2A);
            var softHover = light ? Color.FromRgb(0xE0, 0xE0, 0xE4) : Color.FromRgb(0x3A, 0x3A, 0x3A);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xDD, 0xDD, 0xDD);

            if (DlgChrome != null)
            {
                DlgChrome.Background = Solid(card);
                DlgChrome.BorderBrush = Solid(border);
            }
            if (DlgTitleBar != null)
                DlgTitleBar.Background = Solid(title);

            TxtTitle.Foreground = Solid(text);
            TxtMessage.Foreground = Solid(text);
            TxtDetail.Foreground = Solid(muted);

            if (BtnCancelLabel != null)
            {
                BtnCancelLabel.Foreground = Solid(softFg);
                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.Name = "bd";
                factory.SetValue(Border.BackgroundProperty, Solid(soft));
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                factory.AppendChild(cp);
                template.VisualTree = factory;
                var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                over.Setters.Add(new Setter(Border.BackgroundProperty, Solid(softHover), "bd"));
                template.Triggers.Add(over);
                BtnCancelLabel.Template = template;
            }
        }

        private static SolidColorBrush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
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
