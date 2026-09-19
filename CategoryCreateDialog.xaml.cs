using System.Collections.Generic;
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
        public string SelectedIcon => _selectedIcon;

        private string _selectedIcon = "📁";
        private readonly List<Border> _iconCells = new();
        private Color _cellBg;
        private Color _cellBorder;
        private Color _cellSelBg;
        private Color _cellSelBorder;

        public CategoryCreateDialog(string defaultFolder)
        {
            InitializeComponent();
            BuildIconGrid();
            ApplyThemeSurface(ThemeService.IsLight);
            string defaultName = Loc.T("catcreate.default_name", "Yeni kategori");
            TxtName.Text = defaultName;
            TxtName.SelectAll();
            string defaultFolderName = CategoryStore.SanitizeFolderName(defaultName);
            if (string.IsNullOrWhiteSpace(defaultFolderName)) defaultFolderName = "Yeni kategori";
            string suggested = Path.Combine(defaultFolder, defaultFolderName);
            TxtFolder.Text = suggested;
            TxtName.TextChanged += (_, _) =>
            {
                string n = CategoryStore.SanitizeFolderName(CategoryName);
                if (string.IsNullOrWhiteSpace(n)) return;
                if (TxtFolder.Text.StartsWith(defaultFolder, System.StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(TxtFolder.Text))
                    TxtFolder.Text = Path.Combine(defaultFolder, n);
            };
            Loaded += (_, _) =>
            {
                TxtName.Focus();
                Keyboard.Focus(TxtName);
            };
            ApplyLocalizedTexts();
        }

        private void ApplyLocalizedTexts()
        {
            FlowDirection = Loc.Flow;
            Title = Loc.T("catcreate.title", "Yeni kategori");
            if (TxtTitle != null) TxtTitle.Text = Loc.T("catcreate.title", "Yeni kategori");
            if (LblName != null) LblName.Text = Loc.T("catcreate.name", "Kategori adı");
            if (LblIcon != null) LblIcon.Text = Loc.T("catedit.icon", "Simge");
            if (BtnResetIcon != null) BtnResetIcon.Content = Loc.T("rules.reset", "Varsayılana dön");
            if (LblFolder != null) LblFolder.Text = Loc.T("session.folder", "Kayıt klasörü");
            if (BtnBrowse != null) BtnBrowse.Content = Loc.T("session.browse", "Gözat");
            if (BtnCancel != null) BtnCancel.Content = Loc.T("dialog.cancel", "İptal");
            if (BtnOk != null) BtnOk.Content = Loc.T("catcreate.create", "Oluştur");
        }

        private void BuildIconGrid()
        {
            IconPanel.Children.Clear();
            _iconCells.Clear();
            foreach (string icon in CategoryIcons.PickerIcons)
            {
                var cell = new Border
                {
                    Width = 34,
                    Height = 34,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(7),
                    Cursor = Cursors.Hand,
                    Tag = icon,
                    Child = new TextBlock
                    {
                        Text = icon,
                        FontSize = 16,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                cell.MouseLeftButtonUp += IconCell_Click;
                _iconCells.Add(cell);
                IconPanel.Children.Add(cell);
            }
            UpdateIconSelection();
        }

        private void IconCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string icon })
            {
                _selectedIcon = icon;
                UpdateIconSelection();
            }
        }

        private void UpdateIconSelection()
        {
            foreach (var cell in _iconCells)
            {
                bool sel = cell.Tag as string == _selectedIcon;
                cell.BorderBrush = Brush(sel ? _cellSelBorder : _cellBorder);
                cell.BorderThickness = new Thickness(sel ? 1.5 : 1);
                cell.Background = Brush(sel ? _cellSelBg : _cellBg);
            }
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
            var listBg = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x14, 0x14, 0x14);
            var listBorder = ThemeService.Surface(light, 0xE0, 0xE0, 0xE4, 0x2A, 0x2A, 0x2A);
            _cellBg = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x22, 0x22, 0x22);
            _cellBorder = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
            _cellSelBg = light ? Color.FromArgb(0x28, 0xFF, 0x6B, 0x00) : Color.FromArgb(0x30, 0xFF, 0x6B, 0x00);
            _cellSelBorder = Color.FromRgb(0xFF, 0x6B, 0x00);

            if (DlgChrome != null)
            {
                DlgChrome.Background = Brush(card);
                DlgChrome.BorderBrush = Brush(border);
            }

            if (TxtTitle != null) TxtTitle.Foreground = Brush(text);
            if (LblName != null) LblName.Foreground = Brush(muted);
            if (LblIcon != null) LblIcon.Foreground = Brush(muted);
            if (LblFolder != null) LblFolder.Foreground = Brush(muted);

            if (IconListBorder != null)
            {
                IconListBorder.Background = Brush(listBg);
                IconListBorder.BorderBrush = Brush(listBorder);
            }
            UpdateIconSelection();

            foreach (var box in new[] { TxtName, TxtFolder })
            {
                if (box == null) continue;
                box.Background = Brush(input);
                box.Foreground = Brush(text);
                box.BorderBrush = Brush(border);
            }

            StyleSoft(BtnCancel, soft, softHover, softFg, softHoverFg);
            StyleBrowse(BtnBrowse, browse, browseHover, text);
            StyleResetLink(BtnResetIcon, muted, text, soft);
        }

        private void BtnResetIcon_Click(object sender, RoutedEventArgs e)
        {
            _selectedIcon = "📁";
            UpdateIconSelection();
        }

        private static void StyleResetLink(Button? btn, Color idle, Color hoverFg, Color hoverBg)
        {
            if (btn == null) return;
            btn.Foreground = Brush(idle);
            btn.Background = Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);
            var template = new ControlTemplate(typeof(Button));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "bd";
            factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            factory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            template.VisualTree = factory;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hoverBg), "bd"));
            over.Setters.Add(new Setter(Control.ForegroundProperty, Brush(hoverFg)));
            template.Triggers.Add(over);
            btn.Template = template;
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
                Title = Loc.T("catcreate.pick_folder", "Kategori klasörünü seçin")
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
