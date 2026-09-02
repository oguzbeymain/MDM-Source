using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DownloadMuck
{
    public partial class CategoryRulesDialog : UserControl
    {
        public sealed class ExtOption : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isChecked;
            public string Ext { get; set; } = "";
            public string Label => Ext;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        private CategoryItem? _category;
        private readonly ObservableCollection<ExtOption> _options = new();

        public event Action? Cancelled;
        public event Action? Saved;

        private static readonly string[] Presets =
        {
            "pdf", "doc", "docx", "txt", "xls", "xlsx", "ppt", "pptx", "csv", "md",
            "mp4", "mkv", "avi", "mov", "webm", "mp3", "wav", "flac", "m4a",
            "zip", "rar", "7z", "tar", "gz", "iso",
            "exe", "msi", "apk", "dmg", "jpg", "jpeg", "png", "gif", "webp", "svg",
            "json", "xml", "html", "css", "js", "ts", "dll", "bin"
        };

        public CategoryRulesDialog()
        {
            InitializeComponent();
            LstExt.ItemsSource = _options;
        }

        public void Load(CategoryItem category)
        {
            _category = category;
            TxtTitle.Text = $"{category.DisplayLabel} — dosya türleri";
            TxtCustom.Clear();

            _options.Clear();
            var existing = category.Extensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in Presets)
            {
                _options.Add(new ExtOption
                {
                    Ext = ext,
                    IsChecked = existing.Contains(ext)
                });
            }

            foreach (var extra in existing.Where(e => !Presets.Contains(e, StringComparer.OrdinalIgnoreCase)))
                _options.Add(new ExtOption { Ext = extra, IsChecked = true });

            BtnResetDefaults.Visibility = category.IsBuiltin && CategoryStore.GetDefaultExtensions(category.Id) != null
                ? Visibility.Visible
                : Visibility.Collapsed;

            ApplyThemeSurface(ThemeService.IsLight);
        }

        public void ApplyThemeSurface(bool light)
        {
            var card = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1B, 0x1B, 0x1B);
            var header = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x1F, 0x1F, 0x1F);
            var footer = ThemeService.Surface(light, 0xF5, 0xF5, 0xF7, 0x1A, 0x1A, 0x1A);
            var panel = ThemeService.Surface(light, 0xF4, 0xF4, 0xF6, 0x14, 0x14, 0x14);
            var chip = ThemeService.Surface(light, 0xEE, 0xEE, 0xF0, 0x17, 0x17, 0x17);
            var chipBorder = ThemeService.Surface(light, 0xD0, 0xD0, 0xD4, 0x2A, 0x2A, 0x2A);
            var boxBg = ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x0C, 0x0C, 0x0C);
            var border = ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x88, 0x88, 0x88);
            var input = ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x25, 0x25, 0x25);
            var softBtn = ThemeService.Surface(light, 0xE8, 0xE8, 0xEC, 0x25, 0x25, 0x25);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xCC, 0xCC, 0xCC);

            if (RulesRoot != null)
            {
                RulesRoot.Background = Brush(card);
                RulesRoot.BorderBrush = Brush(border);
            }
            if (RulesHeader != null)
                RulesHeader.Background = Brush(header);
            if (RulesFooter != null)
            {
                RulesFooter.Background = Brush(footer);
                RulesFooter.BorderBrush = Brush(border);
            }
            if (RulesListPanel != null)
            {
                RulesListPanel.Background = Brush(panel);
                RulesListPanel.BorderBrush = Brush(border);
            }
            if (TxtTitle != null)
                TxtTitle.Foreground = Brush(text);
            if (TxtHint != null)
                TxtHint.Foreground = Brush(muted);
            if (TxtCustom != null)
            {
                TxtCustom.Background = Brush(input);
                TxtCustom.Foreground = Brush(text);
                TxtCustom.BorderBrush = Brush(border);
            }

            SetBrush("ChipBg", chip);
            SetBrush("ChipBorder", chipBorder);
            SetBrush("ChipBoxBg", boxBg);
            SetBrush("ChipFg", light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xD0, 0xD0, 0xD0));
            SetBrush("ChipCheckedBg", light ? Color.FromRgb(0xF0, 0xF0, 0xF2) : Color.FromRgb(0x22, 0x22, 0x22));
            SetBrush("ChipCheckedBorder", light ? Color.FromRgb(0x88, 0x88, 0x90) : Color.FromRgb(0x66, 0x66, 0x66));
            SetBrush("ChipHoverBorder", light ? Color.FromRgb(0x99, 0x99, 0xA0) : Color.FromRgb(0x77, 0x77, 0x77));
            SetBrush("ChipCheckedBoxBg", Color.FromRgb(0x1A, 0x1A, 0x1A));
            SetBrush("ChipCheckedFg", light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0));
            SetBrush("ChipCheckMark", Colors.White);
            SetBrush("SoftBtnBg", softBtn);
            SetBrush("SoftBtnFg", softFg);
        }

        private void SetBrush(string key, Color c)
        {
            var b = Brush(c);
            if (Resources.Contains(key)) Resources[key] = b;
            else Resources.Add(key, b);
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (_category == null) return;
            var defaults = CategoryStore.GetDefaultExtensions(_category.Id);
            if (defaults == null) return;

            _options.Clear();
            foreach (var ext in Presets)
                _options.Add(new ExtOption { Ext = ext, IsChecked = defaults.Contains(ext) });
        }

        private void BtnAddCustom_Click(object sender, RoutedEventArgs e)
        {
            string raw = (TxtCustom.Text ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (_options.Any(o => o.Ext.Equals(raw, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = _options.First(o => o.Ext.Equals(raw, StringComparison.OrdinalIgnoreCase));
                existing.IsChecked = true;
            }
            else
                _options.Add(new ExtOption { Ext = raw, IsChecked = true });
            TxtCustom.Clear();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_category == null) return;
            _category.Extensions = _options.Where(o => o.IsChecked)
                .Select(o => o.Ext)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Saved?.Invoke();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    }
}
