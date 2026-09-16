using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MDM
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
            ApplyLocalizedTexts();
            ApplyThemeSurface(ThemeService.IsLight);
        }

        public void Reset()
        {
            GrabLinks = false;
            TxtUrl.Text = "";
            ClearValidation();
            ApplyLocalizedTexts();
            TxtUrl.Focus();
            Keyboard.Focus(TxtUrl);
        }

        public void ApplyLocalizedTexts()
        {
            FlowDirection = Loc.Flow;
            if (TxtTitle != null) TxtTitle.Text = Loc.T("newurl.title", "Yeni indirme");
            if (TxtHint != null) TxtHint.Text = Loc.T("newurl.hint",
                "HTTP, FTP, SFTP, metalink, torrent veya magnet yapıştırın. Birden fazla adres için Ctrl+Enter. Sayfa taramak için ‘Sayfayı tara’.");
            if (BtnTorrent != null) BtnTorrent.Content = Loc.T("newurl.torrent", "Torrent dosyası");
            if (BtnCancel != null) BtnCancel.Content = Loc.T("newurl.cancel", "İptal");
            if (BtnGrab != null) BtnGrab.Content = Loc.T("newurl.scan", "Sayfayı tara");
            if (BtnDownload != null) BtnDownload.Content = Loc.T("newurl.download", "İndir");
        }

        public void ApplyThemeSurface(bool light)
        {
            var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1E, 0x1E, 0x1E);
            var titleBar = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x24, 0x24, 0x24);
            var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x3A, 0x3A, 0x3A);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF2, 0xF2);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x9A, 0x9A, 0x9A);
            var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);
            var soft = ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x2A, 0x2A, 0x2A);
            var softHover = light ? Color.FromRgb(0xE0, 0xE0, 0xE4) : Color.FromRgb(0x33, 0x33, 0x33);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC);
            var softHoverFg = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);
            var caret = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;

            if (DlgChrome != null)
            {
                DlgChrome.Background = Brush(card);
                DlgChrome.BorderBrush = Brush(border);
            }
            if (DlgTitleBar != null)
                DlgTitleBar.Background = Brush(titleBar);
            if (TxtTitle != null)
                TxtTitle.Foreground = Brush(text);
            if (TxtTitleIcon != null)
                TxtTitleIcon.Foreground = Brush(Color.FromRgb(0xFF, 0x6B, 0x00));
            if (TxtHint != null)
                TxtHint.Foreground = Brush(muted);
            if (TxtPlaceholder != null)
                TxtPlaceholder.Foreground = Brush(light ? Color.FromRgb(0x99, 0x99, 0x99) : Color.FromRgb(0x55, 0x55, 0x55));
            if (UrlBox != null)
            {
                UrlBox.Background = Brush(input);
                UrlBox.ClearValue(Border.BorderBrushProperty);
            }
            if (TxtUrl != null)
            {
                TxtUrl.Foreground = Brush(text);
                TxtUrl.CaretBrush = Brush(caret);
            }

            StyleSoft(BtnTorrent, soft, softHover, softFg, softHoverFg);
            StyleSoft(BtnCancel, soft, softHover, softFg, softHoverFg);
            StyleSoft(BtnGrab, soft, softHover, softFg, Color.FromRgb(0xFF, 0x6B, 0x00));

            if (BtnCloseX != null)
                BtnCloseX.Foreground = Brush(Colors.White);
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
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            factory.SetValue(Border.BorderBrushProperty, Brush(Color.FromRgb(0x33, 0x33, 0x33)));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            factory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            factory.AppendChild(cp);
            template.VisualTree = factory;

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(softHover), "bd"));
            over.Setters.Add(new Setter(Control.ForegroundProperty, Brush(softHoverFg)));
            template.Triggers.Add(over);
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            var pressedBg = Color.FromRgb(
                (byte)Math.Max(0, softHover.R - 18),
                (byte)Math.Max(0, softHover.G - 18),
                (byte)Math.Max(0, softHover.B - 18));
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush(pressedBg), "bd"));
            template.Triggers.Add(pressed);
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
                Title = Loc.T("newurl.pick_torrent", "Torrent dosyası seçin"),
                Filter = Loc.T("newurl.torrent", "Torrent dosyası") + " (*.torrent)|*.torrent|"
                         + Loc.T("newurl.filter_all", "Tüm dosyalar") + "|*.*",
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
                ShowValidation(Loc.T("newurl.validation_empty", "İndirmek için en az bir geçerli bağlantı girin."));
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
