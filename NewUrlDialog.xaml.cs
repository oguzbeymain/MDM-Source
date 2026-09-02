using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DownloadMuck
{
    public partial class NewUrlDialog : UserControl
    {
        public event Action? Accepted;
        public event Action? Cancelled;

        public string Url => TxtUrl.Text?.Trim() ?? "";
        public IReadOnlyList<string> Urls => UrlClassifier.ExtractDownloadUrls(TxtUrl.Text);
        public bool GrabLinks { get; private set; }

        public NewUrlDialog()
        {
            InitializeComponent();
            ApplyThemeSurface(ThemeService.IsLight);
        }

        public void Reset()
        {
            GrabLinks = false;
            TxtUrl.Text = "";
            ClearValidation();
            TxtUrl.Focus();
            Keyboard.Focus(TxtUrl);
        }

        public void ApplyThemeSurface(bool light)
        {
            var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1B, 0x1B, 0x1B);
            var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x88, 0x88, 0x88);
            var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);
            var soft = ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x2D, 0x2D, 0x2D);
            var softHover = Color.FromRgb(0x1A, 0x1A, 0x1A);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC);
            var softHoverFg = Colors.White;

            if (DlgChrome != null)
            {
                DlgChrome.Background = Brush(card);
                DlgChrome.BorderBrush = Brush(border);
            }
            if (TxtTitle != null)
                TxtTitle.Foreground = Brush(text);
            if (TxtHint != null)
                TxtHint.Foreground = Brush(muted);
            if (TxtPlaceholder != null)
                TxtPlaceholder.Foreground = Brush(light ? Color.FromRgb(0x99, 0x99, 0x99) : Color.FromRgb(0x55, 0x55, 0x55));
            if (UrlBox != null)
            {
                UrlBox.Background = Brush(input);
                UrlBox.BorderBrush = Brush(Color.FromRgb(0xFF, 0x6B, 0x00));
            }
            if (TxtUrl != null)
            {
                TxtUrl.Foreground = Brush(text);
                TxtUrl.CaretBrush = Brush(Color.FromRgb(0xFF, 0x6B, 0x00));
            }

            StyleSoft(BtnTorrent, soft, softHover, softFg, softHoverFg);
            StyleSoft(BtnCancel, soft, softHover, softFg, softHoverFg);
            StyleSoft(BtnGrab, soft, softHover, softFg, softHoverFg);
        }

        private static void StyleSoft(Button? btn, Color soft, Color softHover, Color softFg, Color softHoverFg)
        {
            if (btn == null) return;
            btn.ClearValue(Control.ForegroundProperty);
            btn.ClearValue(FrameworkElement.StyleProperty);
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(softFg)));
            btn.Style = style;

            var template = new ControlTemplate(typeof(Button));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "bd";
            factory.SetValue(Border.BackgroundProperty, Brush(soft));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            factory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            template.VisualTree = factory;

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(softHover), "bd"));
            over.Setters.Add(new Setter(Control.ForegroundProperty, Brush(softHoverFg)));
            template.Triggers.Add(over);
            btn.Template = template;
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void BtnBrowseTorrent_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Torrent dosyası seçin",
                Filter = "Torrent (*.torrent)|*.torrent|Tüm dosyalar|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                TxtUrl.Text = dlg.FileName;
                ClearValidation();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TryAccept();

        private void BtnGrab_Click(object sender, RoutedEventArgs e)
        {
            GrabLinks = true;
            Accepted?.Invoke();
        }

        private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancelled?.Invoke();
                return;
            }
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TryAccept();
            }
        }

        private void TryAccept()
        {
            ClearValidation();
            var urls = Urls;
            if (urls.Count > 0)
            {
                GrabLinks = false;
                Accepted?.Invoke();
                return;
            }

            string raw = Url;
            if (string.IsNullOrWhiteSpace(raw))
            {
                ShowValidation("İndirmek için en az bir geçerli bağlantı girin.");
                TxtUrl.Focus();
                return;
            }

            var kind = UrlClassifier.Classify(raw);
            ShowValidation(UrlClassifier.UnsupportedMessage(kind));
            TxtUrl.Focus();
        }

        private void ShowValidation(string message)
        {
            TxtValidation.Text = message;
            TxtValidation.Visibility = Visibility.Visible;
        }

        private void ClearValidation()
        {
            TxtValidation.Text = "";
            TxtValidation.Visibility = Visibility.Collapsed;
        }
    }
}
