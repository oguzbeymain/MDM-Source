using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace DownloadMuck
{
    public partial class CategoryRulesDialog : Window
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

        private readonly CategoryItem _category;
        private readonly ObservableCollection<ExtOption> _options = new();

        private static readonly string[] Presets =
        {
            "pdf", "doc", "docx", "txt", "xls", "xlsx", "ppt", "pptx", "csv", "md",
            "mp4", "mkv", "avi", "mov", "webm", "mp3", "wav", "flac", "m4a",
            "zip", "rar", "7z", "tar", "gz", "iso",
            "exe", "msi", "apk", "dmg", "jpg", "jpeg", "png", "gif", "webp", "svg",
            "json", "xml", "html", "css", "js", "ts", "dll", "bin"
        };

        public CategoryRulesDialog(CategoryItem category)
        {
            InitializeComponent();
            _category = category;
            TxtTitle.Text = $"{category.DisplayLabel} — dosya türleri";

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
            {
                _options.Add(new ExtOption { Ext = extra, IsChecked = true });
            }

            LstExt.ItemsSource = _options;

            if (category.IsBuiltin && CategoryStore.GetDefaultExtensions(category.Id) != null)
                BtnResetDefaults.Visibility = Visibility.Visible;
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var defaults = CategoryStore.GetDefaultExtensions(_category.Id);
            if (defaults == null) return;

            _options.Clear();
            foreach (var ext in Presets)
            {
                _options.Add(new ExtOption
                {
                    Ext = ext,
                    IsChecked = defaults.Contains(ext)
                });
            }
            foreach (var extra in defaults.Where(d => !Presets.Contains(d, StringComparer.OrdinalIgnoreCase)))
                _options.Add(new ExtOption { Ext = extra, IsChecked = true });
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnAddCustom_Click(object sender, RoutedEventArgs e)
        {
            string raw = (TxtCustom.Text ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (_options.Any(o => o.Ext.Equals(raw, StringComparison.OrdinalIgnoreCase)))
            {
                var hit = _options.First(o => o.Ext.Equals(raw, StringComparison.OrdinalIgnoreCase));
                hit.IsChecked = true;
                LstExt.Items.Refresh();
            }
            else
            {
                _options.Add(new ExtOption { Ext = raw, IsChecked = true });
            }
            TxtCustom.Clear();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var set = _options.Where(o => o.IsChecked)
                .Select(o => o.Ext.Trim().TrimStart('.').ToLowerInvariant())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _category.Extensions = set.Count > 0 ? set : null;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
