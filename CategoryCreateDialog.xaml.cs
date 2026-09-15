using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MDM
{
    public partial class CategoryCreateDialog : Window
    {
        public string CategoryName => TxtName.Text?.Trim() ?? "";
        public string FolderPath => TxtFolder.Text?.Trim() ?? "";

        public CategoryCreateDialog(string defaultFolder)
        {
            InitializeComponent();
            ApplyThemeSurface(ThemeService.IsLight);
            TxtName.Text = "Yeni kategori";
            TxtName.SelectAll();
            string suggested = Path.Combine(defaultFolder, "Yeni kategori");
            TxtFolder.Text = suggested;
            TxtName.TextChanged += (_, _) =>
            {
                string n = CategoryStore.SanitizeFolderName(CategoryName);
                if (string.IsNullOrWhiteSpace(n)) return;
                string parent = Path.GetDirectoryName(TxtFolder.Text) ?? defaultFolder;
                if (TxtFolder.Text.StartsWith(defaultFolder, System.StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(TxtFolder.Text))
                    TxtFolder.Text = Path.Combine(defaultFolder, n);
            };
            Loaded += (_, _) =>
            {
                TxtName.Focus();
                Keyboard.Focus(TxtName);
            };
        }

        public void ApplyThemeSurface(bool light)
        {
            var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1B, 0x1B, 0x1B);
            var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x99, 0x99, 0x99);
            var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);
            var soft = ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x2D, 0x2D, 0x2D);
            var softHover = Color.FromRgb(0x1A, 0x1A, 0x1A);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC);
            var softHoverFg = Colors.White;
            var browse = ThemeService.Surface(light, 0xE4, 0xE4, 0xE8, 0x2D, 0x2D, 0x2D);
            var browseHover = ThemeService.Surface(light, 0xD4, 0xD4, 0xDA, 0x3A, 0x3A, 0x3A);

            if (DlgChrome != null)
            {
                DlgChrome.Background = Brush(card);
                DlgChrome.BorderBrush = Brush(border);
            }

            if (TxtTitle != null) TxtTitle.Foreground = Brush(text);
            if (LblName != null) LblName.Foreground = Brush(muted);
            if (LblFolder != null) LblFolder.Foreground = Brush(muted);

            foreach (var box in new[] { TxtName, TxtFolder })
            {
                if (box == null) continue;
                box.Background = Brush(input);
                box.Foreground = Brush(text);
                box.BorderBrush = Brush(border);
            }

            StyleSoft(BtnCancel, soft, softHover, softFg, softHoverFg);
            StyleBrowse(BtnBrowse, browse, browseHover, text);
        }

        private static void StyleSoft(Button? btn, Color soft, Color softHover, Color softFg, Color softHoverFg)
        {
            if (btn == null) return;
            btn.ClearValue(Control.ForegroundProperty);
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

        private static void StyleBrowse(Button? btn, Color normal, Color hover, Color fg)
        {
            if (btn == null) return;
            btn.ClearValue(Control.ForegroundProperty);
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(fg)));
            btn.Style = style;

            var template = new ControlTemplate(typeof(Button));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "bd";
            factory.SetValue(Border.BackgroundProperty, Brush(normal));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(0, 8, 8, 0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            template.VisualTree = factory;

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hover), "bd"));
            template.Triggers.Add(over);
            btn.Template = template;
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Kategori klasörünü seçin"
            };
            if (!string.IsNullOrWhiteSpace(TxtFolder.Text) && Directory.Exists(Path.GetDirectoryName(TxtFolder.Text)))
                dialog.InitialDirectory = Path.GetDirectoryName(TxtFolder.Text)!;
            else if (Directory.Exists(TxtFolder.Text))
                dialog.InitialDirectory = TxtFolder.Text;

            if (dialog.ShowDialog() == true)
            {
                string chosen = dialog.FolderName;
                string name = CategoryStore.SanitizeFolderName(CategoryName);
                TxtFolder.Text = Path.Combine(chosen, name);
            }
        }

        private void Txt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
            else if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(CategoryName))
            {
                TxtName.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(FolderPath))
            {
                TxtFolder.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
