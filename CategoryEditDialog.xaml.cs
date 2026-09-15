using System.Windows;

using System.Windows.Controls;

using System.Windows.Input;

using System.Windows.Media;



namespace MDM

{

    public partial class CategoryEditDialog : UserControl

    {

        public event Action? Accepted;

        public event Action? Cancelled;



        private string _defaultIcon = "📁";

        private string _selectedIcon = "📁";

        private string _defaultName = "";

        private readonly List<Border> _iconCells = new();



        private Color _cellBg;

        private Color _cellBorder;

        private Color _cellSelBg;

        private Color _cellSelBorder;

        private Color _placeholderFg;



        public string CategoryName

        {

            get

            {

                string? t = TxtName.Text?.Trim();

                return string.IsNullOrEmpty(t) ? _defaultName : t;

            }

        }



        public string SelectedIcon => _selectedIcon;



        public CategoryEditDialog()

        {

            InitializeComponent();

            BuildIconGrid();

            ApplyThemeSurface(ThemeService.IsLight);

        }



        public void Configure(CategoryItem cat)

        {

            _defaultIcon = CategoryStore.GetDefaultIcon(cat.Id);

            _selectedIcon = string.IsNullOrWhiteSpace(cat.Icon) ? _defaultIcon : cat.Icon;

            _defaultName = CategoryStore.GetDefaultName(cat.Id) ?? cat.Name;

            TxtName.Text = cat.Name;

            TxtTitle.Text = cat.Id == "All" ? "Kategori" : "Kategori düzenle";

            UpdateNamePlaceholder();

            UpdateIconSelection();

            TxtName.Focus();

            Keyboard.Focus(TxtName);

        }



        public void ApplyThemeSurface(bool light)

        {

            var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1B, 0x1B, 0x1B);
            var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x99, 0x99, 0x99);
            var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);
            var inputBorder = ThemeService.Surface(light, 0xD0, 0xD0, 0xD6, 0x33, 0x33, 0x33);
            var listBg = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x14, 0x14, 0x14);
            var listBorder = ThemeService.Surface(light, 0xE0, 0xE0, 0xE4, 0x2A, 0x2A, 0x2A);
            var soft = ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x2D, 0x2D, 0x2D);
            var softHover = ThemeService.Surface(light, 0xE0, 0xE0, 0xE4, 0x3A, 0x3A, 0x3A);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC);
            _placeholderFg = light ? Color.FromArgb(0x88, 0x66, 0x66, 0x66) : Color.FromArgb(0x66, 0x99, 0x99, 0x99);
            _cellBg = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x22, 0x22, 0x22);
            _cellBorder = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);

            _cellSelBg = light ? Color.FromArgb(0x28, 0xFF, 0x6B, 0x00) : Color.FromArgb(0x30, 0xFF, 0x6B, 0x00);

            _cellSelBorder = Color.FromRgb(0xFF, 0x6B, 0x00);



            DlgChrome.Background = Brush(card);

            DlgChrome.BorderBrush = Brush(border);

            TxtTitle.Foreground = Brush(text);

            LblName.Foreground = Brush(muted);

            LblIcon.Foreground = Brush(muted);

            TxtName.Background = Brush(input);

            TxtName.Foreground = Brush(text);

            TxtName.BorderBrush = Brush(inputBorder);

            TxtName.CaretBrush = Brush(Color.FromRgb(0xFF, 0x6B, 0x00));

            IconListBorder.Background = Brush(listBg);

            IconListBorder.BorderBrush = Brush(listBorder);



            StyleSoftBtn(BtnCancel, soft, softHover, softFg);

            StyleSoftLinkBtn(BtnResetIcon, muted, text, softHover);

            StyleSoftLinkBtn(BtnResetName, muted, text, softHover);

            UpdateNamePlaceholder();

            UpdateIconSelection();

        }



        private void BuildIconGrid()

        {

            IconPanel.Children.Clear();

            _iconCells.Clear();

            foreach (string icon in CategoryIcons.PickerIcons)

            {

                var cell = new Border

                {

                    Width = 36,

                    Height = 36,

                    Margin = new Thickness(3),

                    CornerRadius = new CornerRadius(8),

                    Cursor = Cursors.Hand,

                    Tag = icon,

                    Child = new TextBlock

                    {

                        Text = icon,

                        FontSize = 16,

                        HorizontalAlignment = HorizontalAlignment.Center,

                        VerticalAlignment = VerticalAlignment.Center

                    }

                };

                cell.MouseLeftButtonUp += IconCell_Click;

                _iconCells.Add(cell);

                IconPanel.Children.Add(cell);

            }

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

                cell.BorderThickness = new Thickness(sel ? 2 : 1);

                cell.Background = Brush(sel ? _cellSelBg : _cellBg);

            }

        }



        private void BtnResetIcon_Click(object sender, RoutedEventArgs e)

        {

            _selectedIcon = _defaultIcon;

            UpdateIconSelection();

        }



        private void BtnResetName_Click(object sender, RoutedEventArgs e)

        {

            TxtName.Text = "";

            UpdateNamePlaceholder();

            TxtName.Focus();

        }



        private void TxtName_TextChanged(object sender, TextChangedEventArgs e) => UpdateNamePlaceholder();



        private void UpdateNamePlaceholder()

        {

            bool empty = string.IsNullOrWhiteSpace(TxtName.Text);

            TxtNamePlaceholder.Text = _defaultName;

            TxtNamePlaceholder.Foreground = Brush(_placeholderFg);

            TxtNamePlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        }



        private void BtnSave_Click(object sender, RoutedEventArgs e)

        {

            if (string.IsNullOrWhiteSpace(CategoryName))

            {

                TxtName.Focus();

                return;

            }

            Accepted?.Invoke();

        }



        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();



        private void TxtName_KeyDown(object sender, KeyEventArgs e)

        {

            if (e.Key == Key.Enter)

            {

                e.Handled = true;

                BtnSave_Click(sender, e);

            }

            else if (e.Key == Key.Escape)

            {

                e.Handled = true;

                Cancelled?.Invoke();

            }

        }



        private static void StyleSoftBtn(Button btn, Color soft, Color softHover, Color softFg)

        {

            btn.Foreground = Brush(softFg);

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

            template.Triggers.Add(over);

            btn.Template = template;

        }



        private static void StyleSoftLinkBtn(Button btn, Color idle, Color hoverFg, Color hoverBg)

        {

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



        private static SolidColorBrush Brush(Color c)

        {

            var b = new SolidColorBrush(c);

            b.Freeze();

            return b;

        }

    }

}


