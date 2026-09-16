using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MDM
{
    /// <summary>
    /// Sayfa taraması sonuçları: tür filtreleri, kategoriye göre gruplu liste,
    /// tek tek veya toplu seçimle indirme.
    /// </summary>
    public partial class ScanResultsWindow : Window
    {
        private static ScanResultsWindow? _instance;

        private readonly MainWindow _host;
        private readonly ObservableCollection<ScanItem> _items = new();
        private readonly HashSet<ScanKind> _kinds = new();
        private readonly CollectionViewSource _view = new();
        private CancellationTokenSource? _cts;
        private string _pageUrl = "";
        private bool _syncingChips;

        public ScanResultsWindow(MainWindow host)
        {
            InitializeComponent();
            _host = host;
            Owner = host;
            FlowDirection = Loc.Flow;

            _view.Source = _items;
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScanItem.GroupName)));
            _view.Filter += (_, e) => e.Accepted = e.Item is ScanItem item && _kinds.Contains(item.Kind);
            LstItems.ItemsSource = _view.View;

            ApplyLocalizedTexts();
            ThemeService.ApplyToWindow(this);
            UpdateChips();
            UpdateSummary();
        }

        /// <summary>Uygulama içinden: sayfayı tarayıp sonuçları göster.</summary>
        public static void ShowForPage(MainWindow host, string pageUrl)
        {
            var win = Ensure(host);
            win.StartPageScan(pageUrl);
        }

        /// <summary>Eklentiden: sayfa DOM'undan gelen adayları göster.</summary>
        public static void ShowForItems(MainWindow host, string pageUrl, IReadOnlyList<ScanItem> items)
        {
            var win = Ensure(host);
            win.LoadItems(pageUrl, items);
        }

        private static ScanResultsWindow Ensure(MainWindow host)
        {
            if (_instance == null)
            {
                _instance = new ScanResultsWindow(host);
                _instance.Show();
                return _instance;
            }

            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Show();
            _instance.Activate();
            return _instance;
        }

        public async void StartPageScan(string pageUrl)
        {
            CancelWork();
            _pageUrl = pageUrl ?? "";
            TxtScanPage.Text = _pageUrl;
            ClearItems();
            SetBusy(true, Loc.T("scan.scanning", "Taranıyor…"));

            var cts = new CancellationTokenSource();
            _cts = cts;
            try
            {
                int depth = AppSettingsStore.Load().CrawlDepth;
                var progress = new Progress<int>(n => TxtScanBusy.Text =
                    string.Format(Loc.T("scan.scanning_found", "Taranıyor… {0} dosya bulundu"), n));
                var found = await PageScanService.ScanPageAsync(_pageUrl, depth, 12, progress, cts.Token);
                if (cts.IsCancellationRequested) return;
                Fill(found);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Page scan failed: {ex.Message}");
                TxtScanEmpty.Text = Loc.T("scan.failed", "Sayfa taranamadı.");
                TxtScanEmpty.Visibility = Visibility.Visible;
            }
            finally
            {
                if (ReferenceEquals(_cts, cts))
                    SetBusy(false);
            }
        }

        public void LoadItems(string pageUrl, IReadOnlyList<ScanItem> items)
        {
            CancelWork();
            _pageUrl = pageUrl ?? "";
            TxtScanPage.Text = _pageUrl;
            ClearItems();
            SetBusy(false);
            _cts = new CancellationTokenSource();
            Fill(items);
        }

        private void Fill(IReadOnlyList<ScanItem> items)
        {
            foreach (var item in items)
            {
                item.PropertyChanged += Item_PropertyChanged;
                _items.Add(item);
            }

            _kinds.Clear();
            foreach (var item in _items)
                _kinds.Add(item.Kind);

            _view.View?.Refresh();
            UpdateChips();
            UpdateSummary();
            UpdateEmptyState();

            var token = _cts?.Token ?? CancellationToken.None;
            _ = FillDetailsAsync(_items.ToList(), token);
        }

        private async Task FillDetailsAsync(List<ScanItem> snapshot, CancellationToken token)
        {
            if (snapshot.Count == 0) return;
            try
            {
                var sizes = PageScanService.FillSizesAsync(snapshot,
                    (item, size) => Dispatcher.BeginInvoke(() => item.SizeBytes = size), token);
                var thumbs = PageScanService.LoadThumbnailsAsync(snapshot, _pageUrl,
                    (item, image) => Dispatcher.BeginInvoke(() => item.Thumbnail = image), token);
                await Task.WhenAll(sizes, thumbs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scan details failed: {ex.Message}");
            }
        }

        private void ClearItems()
        {
            foreach (var item in _items)
                item.PropertyChanged -= Item_PropertyChanged;
            _items.Clear();
            _kinds.Clear();
            TxtScanEmpty.Visibility = Visibility.Collapsed;
            UpdateChips();
            UpdateSummary();
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScanItem.IsSelected))
                UpdateSummary();
        }

        private void CancelWork()
        {
            try { _cts?.Cancel(); }
            catch { /* ignore */ }
            _cts = null;
        }

        private void SetBusy(bool busy, string? text = null)
        {
            if (text != null)
                TxtScanBusy.Text = text;
            ScanBusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ScanBusyBar.IsIndeterminate = busy;
        }

        private void UpdateEmptyState()
        {
            bool empty = _items.Count == 0;
            TxtScanEmpty.Text = empty
                ? Loc.T("scan.no_results", "Sayfada indirilebilir dosya bulunamadı.")
                : Loc.T("scan.empty_filter", "Bu türde dosya bulunamadı.");
            TxtScanEmpty.Visibility = VisibleCount() == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private int VisibleCount()
        {
            int n = 0;
            if (_view.View == null) return 0;
            foreach (object _ in _view.View)
                n++;
            return n;
        }

        private (ToggleButton Chip, ScanKind Kind)[] KindChips() => new[]
        {
            (ChipImage, ScanKind.Image),
            (ChipVideo, ScanKind.Video),
            (ChipAudio, ScanKind.Audio),
            (ChipArchive, ScanKind.Archive),
            (ChipDocument, ScanKind.Document),
            (ChipApp, ScanKind.App),
            (ChipOther, ScanKind.Other)
        };

        private void UpdateChips()
        {
            // IsChecked'i biz yazarken Checked/Unchecked geri beslemesi olmasın
            _syncingChips = true;
            try
            {
                SyncChips();
            }
            finally
            {
                _syncingChips = false;
            }
        }

        private void SyncChips()
        {
            int available = 0;
            int active = 0;
            foreach (var (chip, kind) in KindChips())
            {
                int count = _items.Count(i => i.Kind == kind);
                chip.Content = count > 0
                    ? $"{PageScanService.GroupLabel(kind)}  {count}"
                    : PageScanService.GroupLabel(kind);
                chip.IsEnabled = count > 0;
                chip.IsChecked = count > 0 && _kinds.Contains(kind);
                if (count > 0)
                {
                    available++;
                    if (chip.IsChecked == true) active++;
                }
            }

            ChipAll.Content = _items.Count > 0
                ? $"{Loc.T("scan.filter_all", "Tümü")}  {_items.Count}"
                : Loc.T("scan.filter_all", "Tümü");
            ChipAll.IsEnabled = _items.Count > 0;
            ChipAll.IsChecked = available > 0 && active == available;
        }

        private void UpdateSummary()
        {
            int visible = VisibleCount();
            int selected = _items.Count(i => i.IsSelected);

            TxtScanSummary.Text = string.Format(
                Loc.T("scan.summary", "{0} dosya · {1} seçili"), visible, selected);

            BtnScanDownload.Content = selected > 0
                ? string.Format(Loc.T("scan.download_selected_n", "Seçilenleri indir ({0})"), selected)
                : Loc.T("scan.download_selected", "Seçilenleri indir");
            BtnScanDownload.IsEnabled = selected > 0;

            ChkSelectAll.IsChecked = visible > 0 && VisibleItems().All(i => i.IsSelected);
        }

        private IEnumerable<ScanItem> VisibleItems()
        {
            if (_view.View == null) yield break;
            foreach (object o in _view.View)
            {
                if (o is ScanItem item)
                    yield return item;
            }
        }

        private void Chip_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingChips) return;
            if (sender is not ToggleButton chip || chip.Tag is not string tag) return;
            if (!Enum.TryParse(tag, out ScanKind kind)) return;

            if (chip.IsChecked == true) _kinds.Add(kind);
            else _kinds.Remove(kind);

            RefreshView();
        }

        private void ChipAll_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingChips) return;
            bool all = ChipAll.IsChecked == true;
            _kinds.Clear();
            if (all)
            {
                foreach (var item in _items)
                    _kinds.Add(item.Kind);
            }
            RefreshView();
        }

        private void RefreshView()
        {
            _view.View?.Refresh();
            UpdateChips();
            UpdateSummary();
            UpdateEmptyState();
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool select = ChkSelectAll.IsChecked == true;
            foreach (var item in VisibleItems().ToList())
                item.IsSelected = select;
            UpdateSummary();
        }

        private void GroupSelect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not CollectionViewGroup group) return;
            var groupItems = group.Items.OfType<ScanItem>().ToList();
            if (groupItems.Count == 0) return;

            bool select = !groupItems.All(i => i.IsSelected);
            foreach (var item in groupItems)
                item.IsSelected = select;
            UpdateSummary();
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var chosen = _items.Where(i => i.IsSelected).ToList();
            if (chosen.Count == 0) return;

            CancelWork();
            Close();
            _host.EnqueueScanItems(chosen);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            CancelWork();
            foreach (var item in _items)
                item.PropertyChanged -= Item_PropertyChanged;
            if (ReferenceEquals(_instance, this))
                _instance = null;
            base.OnClosed(e);
        }

        private void ApplyLocalizedTexts()
        {
            Title = Loc.T("scan.title", "Sayfa taraması");
            TxtScanTitle.Text = Title;
            TxtScanFilterLabel.Text = Loc.T("scan.filter_label", "Aranacak dosya türleri");
            ChkSelectAll.Content = Loc.T("scan.select_all", "Tümünü seç");
            BtnScanCancel.Content = Loc.T("scan.close", "Kapat");
            Resources["ScanGroupSelectText"] = Loc.T("scan.group_select", "Seç");
            TxtScanBusy.Text = Loc.T("scan.scanning", "Taranıyor…");
            TxtScanEmpty.Text = Loc.T("scan.no_results", "Sayfada indirilebilir dosya bulunamadı.");
        }

        /// <summary>ThemeService buradan koyu/açık yüzeyleri uygular.</summary>
        public void ApplyThemeSurface(bool light)
        {
            var rd = Resources;
            Set(rd, "ScanText", ThemeService.Surface(light, 0x1A, 0x1A, 0x1A, 0xE4, 0xE4, 0xE4));
            Set(rd, "ScanMuted", ThemeService.Surface(light, 0x5A, 0x5A, 0x5A, 0x8A, 0x8A, 0x8A));
            Set(rd, "ScanLine", ThemeService.Surface(light, 0xE2, 0xE2, 0xE6, 0x2A, 0x2A, 0x2A));
            Set(rd, "ScanChipBg", ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x1A, 0x1A, 0x1A));
            Set(rd, "ScanChipBorder", ThemeService.Surface(light, 0xD8, 0xD8, 0xDE, 0x2E, 0x2E, 0x2E));
            Set(rd, "ScanChipFg", ThemeService.Surface(light, 0x3A, 0x3A, 0x3A, 0xB8, 0xB8, 0xB8));
            Set(rd, "ScanChipHoverBorder", ThemeService.Surface(light, 0xBC, 0xBC, 0xC4, 0x45, 0x45, 0x45));
            Set(rd, "ScanRowHover", ThemeService.Surface(light, 0xF0, 0xF0, 0xF3, 0x23, 0x23, 0x23));
            Set(rd, "ScanThumbBg", ThemeService.Surface(light, 0xF3, 0xF3, 0xF6, 0x16, 0x16, 0x16));
            Set(rd, "ScanSoftBg", ThemeService.Surface(light, 0xED, 0xED, 0xF0, 0x2A, 0x2A, 0x2A));
            Set(rd, "ScanSoftHover", ThemeService.Surface(light, 0xDF, 0xDF, 0xE4, 0x35, 0x35, 0x35));
            Set(rd, "ScanSoftFg", ThemeService.Surface(light, 0x33, 0x33, 0x33, 0xD0, 0xD0, 0xD0));

            if (ScanTitleBar != null)
                ScanTitleBar.Background = BrushOf(ThemeService.Surface(light, 0xFF, 0xFF, 0xFF, 0x18, 0x18, 0x18));
            if (BtnScanClose != null)
                BtnScanClose.Foreground = BrushOf(ThemeService.Surface(light, 0x55, 0x55, 0x55, 0x99, 0x99, 0x99));
            if (ScanBusyOverlay != null)
                ScanBusyOverlay.Background = new SolidColorBrush(light
                    ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
        }

        private static void Set(ResourceDictionary rd, string key, Color color)
        {
            var brush = BrushOf(color);
            if (rd.Contains(key)) rd[key] = brush;
            else rd.Add(key, brush);
        }

        private static SolidColorBrush BrushOf(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
