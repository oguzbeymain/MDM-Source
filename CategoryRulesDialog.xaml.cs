using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MDM
{
    public partial class CategoryRulesDialog : UserControl
    {
        public sealed class ExtOption : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isChecked;
            public string Ext { get; set; } = "";
            public string Label => Ext;
            public string Group { get; set; } = "";
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

        public sealed class ExtGroup : System.ComponentModel.INotifyPropertyChanged
        {
            public string Title { get; set; } = "";
            public ObservableCollection<ExtOption> Items { get; set; } = new();
            private bool _suppress;

            public bool? AllChecked
            {
                get
                {
                    if (Items.Count == 0) return false;
                    int n = Items.Count(i => i.IsChecked);
                    if (n == 0) return false;
                    if (n == Items.Count) return true;
                    return null;
                }
                set
                {
                    bool on = value != false;
                    _suppress = true;
                    foreach (var item in Items)
                        item.IsChecked = on;
                    _suppress = false;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AllChecked)));
                }
            }

            public void Attach()
            {
                foreach (var item in Items)
                {
                    item.PropertyChanged -= ItemChanged;
                    item.PropertyChanged += ItemChanged;
                }
            }

            private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (_suppress || e.PropertyName != nameof(ExtOption.IsChecked)) return;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AllChecked)));
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        private CategoryItem? _category;
        private readonly List<ExtOption> _allOptions = new();
        private readonly ObservableCollection<ExtGroup> _groups = new();

        public event Action? Cancelled;
        public event Action? Saved;

        private static readonly (string GroupKey, string[] Exts)[] PresetGroups =
        {
            ("rules.group.documents", new[] { "pdf", "doc", "docx", "txt", "xls", "xlsx", "ppt", "pptx", "csv", "md", "rtf", "odt" }),
            ("rules.group.video", new[] { "mp4", "mkv", "avi", "mov", "webm", "m4v", "flv", "wmv" }),
            ("rules.group.audio", new[] { "mp3", "wav", "flac", "m4a", "aac", "ogg", "wma", "opus" }),
            ("rules.group.archive", new[] { "zip", "rar", "7z", "tar", "gz", "iso", "bz2" }),
            ("rules.group.image", new[] { "jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "ico" }),
            ("rules.group.app", new[] { "exe", "msi", "apk", "dmg", "appx", "msix" }),
            ("rules.group.dev", new[] { "json", "xml", "html", "css", "js", "ts", "dll", "bin" }),
        };

        private static string GroupTitle(string key) => key switch
        {
            "rules.group.documents" => Loc.T(key, "Belgeler"),
            "rules.group.video" => Loc.T(key, "Video"),
            "rules.group.audio" => Loc.T(key, "Ses"),
            "rules.group.archive" => Loc.T(key, "Arşiv"),
            "rules.group.image" => Loc.T(key, "Görsel"),
            "rules.group.app" => Loc.T(key, "Uygulama"),
            "rules.group.dev" => Loc.T(key, "Geliştirici"),
            "rules.group.other" => Loc.T(key, "Özel"),
            _ => Loc.T(key, key)
        };

        public CategoryRulesDialog()
        {
            InitializeComponent();
            LstGroups.ItemsSource = _groups;
        }

        public void Load(CategoryItem category)
        {
            _category = category;
            TxtTitle.Text = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                Loc.T("rules.title", "{0} — dosya türleri"),
                category.DisplayLabel);
            TxtHint.Text = Loc.T("rules.hint", "Bu kategoriye düşecek uzantıları seçin");
            if (BtnResetDefaults != null) BtnResetDefaults.Content = Loc.T("rules.reset", "Varsayılana dön");
            if (BtnAddCustom != null) BtnAddCustom.Content = Loc.T("rules.add", "Ekle");
            if (BtnCancel != null) BtnCancel.Content = Loc.T("rules.cancel", "İptal");
            if (BtnSave != null) BtnSave.Content = Loc.T("rules.save", "Kaydet");
            if (TxtCustom != null) TxtCustom.ToolTip = Loc.T("catrules.ext_hint", "Örn: pdf veya .pdf");
            TxtCustom?.Clear();

            var existing = category.Extensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RebuildOptions(existing);

            BtnResetDefaults.Visibility = category.IsBuiltin && CategoryStore.GetDefaultExtensions(category.Id) != null
                ? Visibility.Visible
                : Visibility.Collapsed;

            ApplyThemeSurface(ThemeService.IsLight);
        }

        private void RebuildOptions(HashSet<string> existing)
        {
            _allOptions.Clear();
            _groups.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (groupKey, exts) in PresetGroups)
            {
                string title = GroupTitle(groupKey);
                var g = new ExtGroup { Title = title };
                foreach (string ext in exts)
                {
                    seen.Add(ext);
                    var opt = new ExtOption
                    {
                        Ext = ext,
                        Group = title,
                        IsChecked = existing.Contains(ext)
                    };
                    _allOptions.Add(opt);
                    g.Items.Add(opt);
                }
                g.Attach();
                _groups.Add(g);
            }

            var extras = existing
                .Where(e => !seen.Contains(e))
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (extras.Count > 0)
            {
                string customTitle = GroupTitle("rules.group.other");
                var custom = new ExtGroup { Title = customTitle };
                foreach (string ext in extras)
                {
                    var opt = new ExtOption { Ext = ext, Group = customTitle, IsChecked = true };
                    _allOptions.Add(opt);
                    custom.Items.Add(opt);
                }
                _groups.Add(custom);
                custom.Attach();
            }
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
            SetBrush("ChipCheckedBg", light ? Color.FromRgb(0xF2, 0xF2, 0xF4) : Color.FromRgb(0x1C, 0x1C, 0x1C));
            SetBrush("ChipCheckedBorder", light ? Color.FromRgb(0x88, 0x88, 0x90) : Color.FromRgb(0x5C, 0x5C, 0x5C));
            SetBrush("ChipHoverBorder", light ? Color.FromRgb(0x99, 0x99, 0xA0) : Color.FromRgb(0x6A, 0x6A, 0x6A));
            SetBrush("ChipCheckedBoxBg", light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0x12, 0x12, 0x12));
            SetBrush("ChipCheckedFg", light ? Color.FromRgb(0x22, 0x22, 0x22) : Color.FromRgb(0xE8, 0xE8, 0xE8));
            SetBrush("ChipCheckMark", Colors.White);
            SetBrush("SoftBtnBg", softBtn);
            SetBrush("SoftBtnFg", softFg);
            SetBrush("GroupLabelFg", muted);
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
            RebuildOptions(defaults.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        private void BtnAddCustom_Click(object sender, RoutedEventArgs e)
        {
            string raw = (TxtCustom.Text ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(raw)) return;

            var existing = _allOptions.FirstOrDefault(o => o.Ext.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsChecked = true;
                TxtCustom.Clear();
                return;
            }

            string customTitle = GroupTitle("rules.group.other");
            var opt = new ExtOption { Ext = raw, Group = customTitle, IsChecked = true };
            _allOptions.Add(opt);

            var customGroup = _groups.FirstOrDefault(g => g.Title == customTitle);
            if (customGroup == null)
            {
                customGroup = new ExtGroup { Title = customTitle };
                _groups.Add(customGroup);
            }
            customGroup.Items.Add(opt);
            customGroup.Attach();
            TxtCustom.Clear();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_category == null) return;
            _category.Extensions = _allOptions.Where(o => o.IsChecked)
                .Select(o => o.Ext)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Saved?.Invoke();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    }
}
