using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DownloadMuck
{
    public partial class MainWindow : Window
    {
        private BrowserCaptureServer? _captureServer;
        private readonly Dictionary<DownloadItem, DownloadEngine> _engines = new();
        private readonly Dictionary<DownloadItem, DownloadSessionWindow> _sessionWindows = new();
        private readonly HashSet<string> _pendingSessionUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _recentCaptureUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _captureGate = new();
        private static readonly TimeSpan CaptureDebounce = TimeSpan.FromSeconds(12);
        private readonly Dictionary<DownloadItem, string> _itemUrls = new();
        public ObservableCollection<DownloadItem> DownloadList { get; set; } = new ObservableCollection<DownloadItem>();
        private ICollectionView? _downloadView;

        private Point _dragStartPoint;
        private bool _marqueeArmed;
        private bool _marqueeActive;
        private Point _marqueeStart;
        private HashSet<DownloadItem>? _marqueeCtrlBase;
        private string _defaultFolder = "";
        private string _currentCategory = "All";
        private string _searchText = "";
        public ObservableCollection<CategoryItem> Categories { get; private set; } = CategoryStore.CreateDefaults();
        public ObservableCollection<CategoryItem> VisibleCategories { get; } = new();
        private Point _categoryDragStart;
        private CategoryItem? _categoryDragItem;
        private bool _isPseudoMaximized;
        // Restore bounds for drag-from-maximize (OS restores size; we keep a fallback)
        private Rect _restoreBounds;
        private bool _catMarqueeArmed;
        private bool _catMarqueeActive;
        private Point _catMarqueeStart;
        private HashSet<CategoryItem>? _catMarqueeCtrlBase;
        private bool _suppressCategorySelection;
        private enum CatDropKind { None, Nest, InsertBefore, InsertAfter, ToRoot }
        private CatDropKind _catDropKind;
        private CategoryItem? _catDropTarget;
        private bool _isFileDragging;
        private DispatcherTimer? _copyToastTimer;
        private bool _historySaveQueued;
        private Point? _titleDragStart;
        private bool _titleDragRestoring;
        private TrayIconService? _tray;
        private bool _exitRequested;
        private bool _trayTipShown;

        public MainWindow()
        {
            InitializeComponent();

            _defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var appSettings = AppSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(appSettings.DefaultDownloadFolder)
                && Directory.Exists(appSettings.DefaultDownloadFolder))
                _defaultFolder = appSettings.DefaultDownloadFolder;

            Categories = CategoryStore.Load();
            CategoryStore.EnsureBuiltinCategories(Categories);
            CategoryStore.Save(Categories);
            CategoryStore.EnsureDiskFolders(Categories, _defaultFolder);
            RebuildVisibleCategories();
            LstCategories.ItemsSource = VisibleCategories;

            _downloadView = CollectionViewSource.GetDefaultView(DownloadList);
            _downloadView.Filter = FilterByCategory;
            DgDownloads.ItemsSource = _downloadView;
            DgDownloads.GiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.PreviewGiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.LayoutUpdated += DgDownloads_LayoutUpdated;

            LoadDownloadHistory();
            RestorePendingDownloads();
            DownloadList.CollectionChanged += (_, _) =>
            {
                RefreshCategoryCounts();
                QueueHistorySave();
            };
            RefreshCategoryCounts();

            _currentCategory = "All";
            HighlightAllDownloadsButton(true);
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
            StartBrowserCaptureServer();

            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;
            NativeWindowChrome.Attach(this);
            InitTray();
        }

        private void InitTray()
        {
            try
            {
                _tray = new TrayIconService();
                _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
                _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitFromTray);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray init failed: {ex.Message}");
            }
        }

        public void HideToTray()
        {
            try
            {
                ShowInTaskbar = false;
                Hide();
                if (!_trayTipShown)
                {
                    _trayTipShown = true;
                    _tray?.ShowHiddenTipOnce();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HideToTray: {ex.Message}");
            }
        }

        public void ShowFromTray()
        {
            try
            {
                Show();
                ShowInTaskbar = true;
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowFromTray: {ex.Message}");
            }
        }

        private void ExitFromTray()
        {
            _exitRequested = true;
            Close();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AutoStartHelper.EnsureRegistered();
                    AutoResumeIncompleteDownloads();
                }
                catch (Exception ex) { Debug.WriteLine($"Startup tasks: {ex.Message}"); }
            }, DispatcherPriority.Background);
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            foreach (var kv in _engines.ToList())
            {
                try
                {
                    if (kv.Value.IsDownloading && !kv.Value.IsPaused)
                        kv.Value.Pause();
                    kv.Key.Status = "Duraklatıldı";
                    kv.Key.IsDownloading = false;
                }
                catch { /* ignore */ }
            }
            PersistDownloadHistory();
            try { _captureServer?.Stop(); } catch { /* ignore */ }
            try { _tray?.Dispose(); } catch { /* ignore */ }
            _tray = null;
        }

        public void ApplyBackgroundStart()
        {
            // App.OnStartup HideToTray çağırır; geriye dönük uyumluluk
            HideToTray();
        }

        // --- Uygulama içi karartmalı modal ---
        private DispatcherFrame? _modalFrame;
        private bool _modalResult;

        public bool ShowModalConfirm(string title, string message, string detail,
            string confirmText, string cancelText, bool danger)
        {
            _modalResult = false;
            ModalTitle.Text = title;
            ModalMessage.Text = message;
            if (string.IsNullOrWhiteSpace(detail))
            {
                ModalDetail.Visibility = Visibility.Collapsed;
                ModalDetail.Text = "";
            }
            else
            {
                ModalDetail.Visibility = Visibility.Visible;
                ModalDetail.Text = detail;
            }

            ModalCancelBtn.Content = cancelText;
            ModalCancelBtn.Visibility = Visibility.Visible;
            ModalConfirmBtn.Content = confirmText;
            ModalConfirmBtn.Visibility = Visibility.Visible;
            ModalConfirmBtn.Background = new SolidColorBrush(
                danger ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromRgb(0xFF, 0x6B, 0x00));

            ModalOverlay.Visibility = Visibility.Visible;
            ModalOverlay.Focusable = true;
            Keyboard.Focus(ModalConfirmBtn);

            _modalFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_modalFrame);
            return _modalResult;
        }

        public void ShowModalInfo(string title, string message, string detail)
        {
            _modalResult = true;
            ModalTitle.Text = title;
            ModalMessage.Text = message;
            if (string.IsNullOrWhiteSpace(detail))
            {
                ModalDetail.Visibility = Visibility.Collapsed;
                ModalDetail.Text = "";
            }
            else
            {
                ModalDetail.Visibility = Visibility.Visible;
                ModalDetail.Text = detail;
            }

            ModalCancelBtn.Visibility = Visibility.Collapsed;
            ModalConfirmBtn.Content = "Tamam";
            ModalConfirmBtn.Visibility = Visibility.Visible;
            ModalConfirmBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));

            ModalOverlay.Visibility = Visibility.Visible;
            ModalOverlay.Focusable = true;
            Keyboard.Focus(ModalConfirmBtn);

            _modalFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_modalFrame);
        }

        private void CloseModal(bool result)
        {
            _modalResult = result;
            ModalOverlay.Visibility = Visibility.Collapsed;
            if (_modalFrame != null)
            {
                _modalFrame.Continue = false;
                _modalFrame = null;
            }
        }

        private void ModalConfirm_Click(object sender, RoutedEventArgs e) => CloseModal(true);
        private void ModalCancel_Click(object sender, RoutedEventArgs e) => CloseModal(false);
        private void ModalClose_Click(object sender, RoutedEventArgs e) => CloseModal(false);

        private void ModalOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseModal(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CloseModal(true);
                e.Handled = true;
            }
        }

        private void CategoryItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem lbi && lbi.DataContext is CategoryItem cat)
            {
                if (!lbi.IsSelected)
                {
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                        && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        LstCategories.SelectedItems.Clear();
                    lbi.IsSelected = true;
                }
            }
        }

        private void RestorePendingDownloads()
        {
            // Engine'leri hazirla; otomatik devam Loaded'da
            foreach (var item in DownloadList)
            {
                if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = item.Url;
                if (string.IsNullOrWhiteSpace(url))
                    _itemUrls.TryGetValue(item, out url!);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (string.IsNullOrWhiteSpace(item.FilePath)) continue;

                if (!_engines.ContainsKey(item))
                {
                    var engine = new DownloadEngine(url, item.FilePath, threadCount: 8);
                    _engines[item] = engine;
                    WireEngineEvents(item, engine);
                    _itemUrls[item] = url;
                    item.Url = url;
                }
            }
        }

        private void AutoResumeIncompleteDownloads()
        {
            // Sadece gerçek kısmi indirme state'i olanları sessizce devam ettir (pencere açma)
            foreach (var item in DownloadList.ToList())
            {
                if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(item.FilePath))
                    continue;

                string statePath = item.FilePath + ".mdmstate";
                if (!File.Exists(statePath))
                    continue; // State yoksa otomatik başlatma — döngü/yeniden indirme riski

                if (!_engines.TryGetValue(item, out var engine))
                    continue;

                item.Status = "İndiriliyor";
                item.IsDownloading = true;
                _ = RunEngineAsync(item, engine);
            }
        }

        private void LoadDownloadHistory()
        {
            try
            {
                var loaded = DownloadHistoryStore.Load(out var urls);
                foreach (var item in loaded)
                {
                    DownloadList.Add(item);
                    if (urls.TryGetValue(item, out var url) && !string.IsNullOrWhiteSpace(url))
                    {
                        _itemUrls[item] = url;
                        item.Url = url;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"History load: {ex.Message}");
            }
        }

        private void PersistDownloadHistory()
        {
            try
            {
                // Canli URL alanini senkronla
                foreach (var kv in _itemUrls)
                    kv.Key.Url = kv.Value;
                DownloadHistoryStore.Save(DownloadList, _itemUrls);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"History save: {ex.Message}");
            }
        }

        private void QueueHistorySave()
        {
            if (_historySaveQueued) return;
            _historySaveQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _historySaveQueued = false;
                PersistDownloadHistory();
            }, DispatcherPriority.Background);
        }

        private void RebuildVisibleCategories()
        {
            var selectedId = (LstCategories?.SelectedItem as CategoryItem)?.Id;
            _suppressCategorySelection = true;
            try
            {
                VisibleCategories.Clear();
                foreach (var root in Categories)
                {
                    if (root.Id == "All") continue; // ustte ayri buton
                    AppendVisible(root);
                }

                if (selectedId != null && selectedId != "All")
                {
                    var match = VisibleCategories.FirstOrDefault(c => c.Id == selectedId);
                    if (match != null) LstCategories.SelectedItem = match;
                }
            }
            finally
            {
                _suppressCategorySelection = false;
            }
        }

        private void AppendVisible(CategoryItem item)
        {
            VisibleCategories.Add(item);
            if (item.IsExpanded)
            {
                foreach (var child in item.Children)
                    AppendVisible(child);
            }
        }

        private void StartBrowserCaptureServer()
        {
            try
            {
                _captureServer?.Stop();
                _captureServer = new BrowserCaptureServer((url, filename, mime) =>
                {
                    Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            await StartDownloadProcess(url, filename, mime, selectItem: false);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Capture download error: {ex.Message}");
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                });
                _captureServer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Capture server failed: {ex.Message}");
            }
        }

        private bool FilterByCategory(object obj)
        {
            if (obj is not DownloadItem item) return false;

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                if (item.FileName?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) != true
                    && item.FileType?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) != true)
                    return false;
            }

            if (_currentCategory == "All") return true;

            var cat = CategoryStore.FindById(Categories, _currentCategory);
            if (cat == null) return true;

            var treeIds = new HashSet<string>(cat.Flatten().Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            // Acik kategori atamasi varsa SADECE ona gore filtrele (uzanti kurali ezmesin)
            if (!string.IsNullOrEmpty(item.CategoryId) && item.CategoryId != "All")
                return treeIds.Contains(item.CategoryId);

            // Atanmamis dosyalar: uzanti kurallari
            string ext = Path.GetExtension(item.FileName)?.TrimStart('.') ?? "";
            if (string.IsNullOrEmpty(ext)) return false;

            foreach (var node in cat.Flatten())
            {
                if (node.Extensions is { Count: > 0 } && node.Extensions.Contains(ext))
                    return true;
            }

            return false;
        }

        public void RefreshCategoryCounts()
        {
            foreach (var cat in CategoryStore.AllFlat(Categories))
            {
                if (cat.Id == "All")
                {
                    cat.FileCount = DownloadList.Count;
                    continue;
                }

                var treeIds = new HashSet<string>(cat.Flatten().Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (var item in DownloadList)
                {
                    if (!string.IsNullOrEmpty(item.CategoryId) && item.CategoryId != "All")
                    {
                        if (treeIds.Contains(item.CategoryId)) n++;
                        continue;
                    }
                    // Atanmamis: uzanti ile bu kategoriye dusenler
                    string ext = Path.GetExtension(item.FileName)?.TrimStart('.') ?? "";
                    if (cat.Flatten().Any(node => node.Extensions is { Count: > 0 } && node.Extensions.Contains(ext)))
                        n++;
                }
                cat.FileCount = n;
            }
        }

        private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCategorySelection) return;
            try
            {
                if (LstCategories.SelectedItem is not CategoryItem cat) return;
                _currentCategory = cat.Id;
                HighlightAllDownloadsButton(false);
                // Secim degisirken DataGrid ile cakismasin
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _downloadView?.Refresh();
                        DgDownloads.SelectedItems.Clear();
                    }
                    catch (Exception ex) { Debug.WriteLine($"Category filter refresh: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LstCategories_SelectionChanged: {ex}");
            }
        }

        private void BtnAllDownloads_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentCategory = "All";
                _suppressCategorySelection = true;
                try { LstCategories.SelectedItems.Clear(); }
                finally { _suppressCategorySelection = false; }
                HighlightAllDownloadsButton(true);
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _downloadView?.Refresh();
                        DgDownloads.SelectedItems.Clear();
                    }
                    catch (Exception ex) { Debug.WriteLine($"All downloads refresh: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BtnAllDownloads_Click: {ex}");
            }
        }

        private void HighlightAllDownloadsButton(bool selected)
        {
            BtnAllDownloads.Tag = selected ? "selected" : "idle";
        }

        private static readonly SolidColorBrush ColumnDragBrush =
            new(Color.FromRgb(0xFF, 0x6B, 0x00));

        private void DgDownloads_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_isFileDragging)
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.Arrow);
                e.Handled = true;

                Point screen = GetMouseScreenPoint();
                FileDragPopup.HorizontalOffset = screen.X + 12;
                FileDragPopup.VerticalOffset = screen.Y + 8;
                if (!FileDragPopup.IsOpen)
                    FileDragPopup.IsOpen = true;
                return;
            }

            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.SizeWE);
            e.Handled = true;
            RecolorColumnDragAdorners();
        }

        private DateTime _lastDragRecolor = DateTime.MinValue;

        private void DgDownloads_LayoutUpdated(object? sender, EventArgs e)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            // Her layout'ta gorsel agaci gezmek donmaya yol acar — throttle
            if ((DateTime.UtcNow - _lastDragRecolor).TotalMilliseconds < 50) return;
            _lastDragRecolor = DateTime.UtcNow;
            RecolorColumnDragAdorners();
        }

        private void RecolorColumnDragAdorners()
        {
            RecolorBlueVisuals(DgDownloads);

            var layer = AdornerLayer.GetAdornerLayer(DgDownloads);
            if (layer != null)
                RecolorBlueVisuals(layer);

            // Header presenter uzerindeki gostergeler
            if (FindVisualChild<DataGridColumnHeadersPresenter>(DgDownloads) is { } headers)
                RecolorBlueVisuals(headers);
        }

        private void RecolorBlueVisuals(DependencyObject root)
        {
            foreach (var sep in FindVisualChildren<Separator>(root))
            {
                sep.Background = ColumnDragBrush;
                sep.BorderBrush = ColumnDragBrush;
                sep.Foreground = ColumnDragBrush;
            }

            foreach (var border in FindVisualChildren<Border>(root))
            {
                if (border.Background is SolidColorBrush sb && LooksLikeSystemBlue(sb.Color))
                    border.Background = ColumnDragBrush;
                if (border.BorderBrush is SolidColorBrush bb && LooksLikeSystemBlue(bb.Color))
                    border.BorderBrush = ColumnDragBrush;
            }

            foreach (var rect in FindVisualChildren<System.Windows.Shapes.Rectangle>(root))
            {
                if (rect.Fill is SolidColorBrush fb && LooksLikeSystemBlue(fb.Color))
                    rect.Fill = ColumnDragBrush;
                if (rect.Stroke is SolidColorBrush st && LooksLikeSystemBlue(st.Color))
                    rect.Stroke = ColumnDragBrush;
            }
        }

        private static bool LooksLikeSystemBlue(Color c)
        {
            return c.B > 180 && c.B > c.R + 40 && c.B > c.G + 20;
        }

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in FindVisualChildren<T>(root))
                return child;
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        private void BtnAddCategory_Click(object sender, RoutedEventArgs e) => AddCategoryInteractive(asChild: false);

        private void MenuAddCategory_Click(object sender, RoutedEventArgs e) => AddCategoryInteractive(asChild: false);

        private void MenuAddChildCategory_Click(object sender, RoutedEventArgs e) => AddCategoryInteractive(asChild: true);

        private void MenuUnnestCategory_Click(object sender, RoutedEventArgs e)
        {
            var targets = LstCategories.SelectedItems
                .OfType<CategoryItem>()
                .Where(c => !c.IsBuiltin && !string.IsNullOrEmpty(c.ParentId))
                .ToList();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, "Kategori", "Çıkarılacak iç içe özel kategori seçin.");
                return;
            }
            foreach (var cat in targets)
                MoveCategoryToRoot(cat);
        }

        private void MenuRenameCategory_Click(object sender, RoutedEventArgs e)
        {
            if (LstCategories.SelectedItem is not CategoryItem cat || cat.Id == "All")
            {
                InfoDialog.Show(this, "Yeniden adlandır", "Yeniden adlandırmak için bir kategori seçin.");
                return;
            }

            var dlg = new PromptDialog("Yeniden adlandır", "Yeni kategori adı:", cat.Name)
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true) return;
            string name = dlg.ResultText.Trim();
            if (string.IsNullOrWhiteSpace(name) || name == cat.Name) return;

            cat.Name = name;
            CategoryStore.Save(Categories);
            RebuildVisibleCategories();
            LstCategories.SelectedItem = cat;
        }

        private void MenuOpenCategoryFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LstCategories.SelectedItem is not CategoryItem cat || cat.Id == "All")
            {
                InfoDialog.Show(this, "Klasör", "Klasörünü açmak için bir kategori seçin.");
                return;
            }

            try
            {
                string folder = CategoryStore.GetCategoryFolderPath(Categories, cat.Id, _defaultFolder);
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Klasör", ex.Message);
            }
        }

        private void AddCategoryInteractive(bool asChild)
        {
            CategoryItem? parent = null;
            if (asChild)
            {
                if (LstCategories.SelectedItem is not CategoryItem p || p.Id == "All")
                {
                    InfoDialog.Show(this, "Alt kategori", "Alt kategori eklemek için bir üst kategori seçin.");
                    return;
                }
                parent = p;
            }

            string suggestRoot = parent != null
                ? CategoryStore.GetCategoryFolderPath(Categories, parent.Id, _defaultFolder)
                : _defaultFolder;

            var dlg = new CategoryCreateDialog(suggestRoot) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string name = dlg.CategoryName;
            if (string.IsNullOrWhiteSpace(name)) return;

            var item = new CategoryItem
            {
                Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Icon = "📁",
                IsBuiltin = false,
                Depth = 0,
                CustomFolderPath = dlg.FolderPath
            };

            if (parent != null)
            {
                item.ParentId = parent.Id;
                item.Depth = parent.Depth + 1;
                parent.Children.Add(item);
                parent.IsExpanded = true;
                parent.NotifyChildrenChanged();
            }
            else
            {
                Categories.Add(item);
            }

            try { Directory.CreateDirectory(dlg.FolderPath); } catch { /* ignore */ }
            CategoryStore.Save(Categories);
            CategoryStore.EnsureDiskFolders(Categories, _defaultFolder);
            RebuildVisibleCategories();
            LstCategories.SelectedItem = item;
        }

        private void MenuCategoryRules_Click(object sender, RoutedEventArgs e)
        {
            if (LstCategories.SelectedItem is not CategoryItem cat || cat.Id == "All")
            {
                InfoDialog.Show(this, "Kategori", "Dosya türü ayarlamak için bir kategori seçin.");
                return;
            }

            var dlg = new CategoryRulesDialog(cat) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                CategoryStore.Save(Categories);
                // Sadece bu kategoriye bagli / kurala yeni dusen oge'leri guncelle — manuel tasimalari koru
                foreach (var item in DownloadList)
                {
                    string resolved = ResolveCategoryForNewFile(item.FileName);
                    if (item.CategoryId == cat.Id)
                    {
                        if (resolved != cat.Id)
                            item.CategoryId = resolved;
                        continue;
                    }

                    if (resolved != cat.Id) continue;

                    var current = CategoryStore.FindById(Categories, item.CategoryId);
                    bool autoBucket = item.CategoryId == "All"
                        || (current?.IsBuiltin == true && current.Id != "All");
                    if (autoBucket)
                        item.CategoryId = resolved;
                }
                _downloadView?.Refresh();
                RefreshCategoryCounts();
                QueueHistorySave();
            }
        }

        private void LstCategories_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                MenuDeleteCategories_Click(sender, e);
                e.Handled = true;
            }
        }

        private void CatMarquee_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
                return; // satirda — normal secim/surukle

            _catMarqueeArmed = true;
            _catMarqueeActive = false;
            _catMarqueeStart = e.GetPosition((IInputElement)sender);
            _catMarqueeCtrlBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? LstCategories.SelectedItems.OfType<CategoryItem>().ToHashSet()
                : null;
            ((UIElement)sender).CaptureMouse();
        }

        private void CatMarquee_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_catMarqueeArmed || e.LeftButton != MouseButtonState.Pressed) return;
            try
            {
                Point pos = e.GetPosition((IInputElement)sender);
                if (!_catMarqueeActive)
                {
                    if (Math.Abs(pos.X - _catMarqueeStart.X) < 4 && Math.Abs(pos.Y - _catMarqueeStart.Y) < 4)
                        return;
                    _catMarqueeActive = true;
                    CatSelectionRect.Visibility = Visibility.Visible;
                    if (_catMarqueeCtrlBase == null)
                    {
                        _suppressCategorySelection = true;
                        try { LstCategories.SelectedItems.Clear(); }
                        finally { _suppressCategorySelection = false; }
                    }
                }

                double x = Math.Min(_catMarqueeStart.X, pos.X);
                double y = Math.Min(_catMarqueeStart.Y, pos.Y);
                double w = Math.Abs(pos.X - _catMarqueeStart.X);
                double h = Math.Abs(pos.Y - _catMarqueeStart.Y);
                Canvas.SetLeft(CatSelectionRect, x);
                Canvas.SetTop(CatSelectionRect, y);
                CatSelectionRect.Width = w;
                CatSelectionRect.Height = h;

                var rect = new Rect(x, y, w, h);
                var keep = _catMarqueeCtrlBase ?? new HashSet<CategoryItem>();
                var hits = new List<CategoryItem>();
                foreach (var cat in VisibleCategories.ToList())
                {
                    if (LstCategories.ItemContainerGenerator.ContainerFromItem(cat) is not ListBoxItem lbi)
                        continue;
                    if (!lbi.IsVisible || lbi.ActualWidth <= 0) continue;
                    Point tl = lbi.TranslatePoint(new Point(0, 0), (UIElement)sender);
                    var itemRect = new Rect(tl.X, tl.Y, lbi.ActualWidth, lbi.ActualHeight);
                    if (rect.IntersectsWith(itemRect))
                        hits.Add(cat);
                }

                _suppressCategorySelection = true;
                try
                {
                    LstCategories.SelectedItems.Clear();
                    foreach (var item in keep)
                        LstCategories.SelectedItems.Add(item);
                    foreach (var cat in hits)
                    {
                        if (!LstCategories.SelectedItems.Contains(cat))
                            LstCategories.SelectedItems.Add(cat);
                    }
                }
                finally { _suppressCategorySelection = false; }

                // Son secilen kategoriye filtre uygula
                if (LstCategories.SelectedItem is CategoryItem last)
                {
                    _currentCategory = last.Id;
                    HighlightAllDownloadsButton(false);
                    _downloadView?.Refresh();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CatMarquee_PreviewMouseMove: {ex.Message}");
            }
        }

        private void CatMarquee_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            => EndCatMarquee(sender);

        private void CatMarquee_LostMouseCapture(object sender, MouseEventArgs e)
            => EndCatMarquee(sender);

        private void EndCatMarquee(object sender)
        {
            if (!_catMarqueeArmed) return;
            _catMarqueeArmed = false;
            _catMarqueeActive = false;
            CatSelectionRect.Visibility = Visibility.Collapsed;
            if (sender is UIElement el && el.IsMouseCaptured)
                el.ReleaseMouseCapture();
        }

        private void MenuDeleteCategories_Click(object sender, RoutedEventArgs e)
        {
            var toDelete = LstCategories.SelectedItems
                .OfType<CategoryItem>()
                .Where(c => !c.IsBuiltin && c.Id != "All")
                .ToList();

            if (toDelete.Count == 0)
            {
                InfoDialog.Show(this, "Kategori",
                    "Silmek için özel (eklediğiniz) kategorileri seçin.",
                    "Ctrl ile çoklu seçim yapabilirsiniz. Varsayılan kategoriler silinemez.");
                return;
            }

            if (!ConfirmDialog.Show(this, "Kategori sil",
                    $"{toDelete.Count} kategori silinsin mi?",
                    "Kategori listeden kaldırılır ve bilgisayardaki ilgili klasör de silinir.",
                    confirmText: "Sil", danger: true))
                return;

            foreach (var cat in toDelete.ToList())
                RemoveCategoryRecursive(cat);

            CategoryStore.Save(Categories);
            RebuildVisibleCategories();
            BtnAllDownloads_Click(BtnAllDownloads, new RoutedEventArgs());
            _downloadView?.Refresh();
        }

        private void RemoveCategoryRecursive(CategoryItem cat)
        {
            foreach (var child in cat.Children.ToList())
                RemoveCategoryRecursive(child);

            foreach (var dl in DownloadList.Where(d => d.CategoryId == cat.Id))
                dl.CategoryId = "All";

            // Disk klasorunu sil (built-in degilse)
            if (!cat.IsBuiltin)
            {
                string folder = CategoryStore.GetCategoryFolderPath(Categories, cat.Id, _defaultFolder);
                CategoryStore.TryDeleteCategoryFolder(folder);
            }

            if (!string.IsNullOrEmpty(cat.ParentId))
            {
                var parent = CategoryStore.FindById(Categories, cat.ParentId);
                parent?.Children.Remove(cat);
                parent?.NotifyChildrenChanged();
            }
            else
            {
                Categories.Remove(cat);
            }
        }

        private void CategoryExpand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: CategoryItem cat }) return;
            cat.IsExpanded = !cat.IsExpanded;
            CategoryStore.Save(Categories);
            RebuildVisibleCategories();
            e.Handled = true;
        }

        private void LstCategories_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _categoryDragStart = e.GetPosition(null);
            _categoryDragItem = null;
            // Expand butonundan surukleme baslatma
            if (e.OriginalSource is DependencyObject d0 && FindAncestor<Button>(d0) != null)
                return;
            if (e.OriginalSource is DependencyObject d)
            {
                var lbi = FindAncestor<ListBoxItem>(d);
                if (lbi?.DataContext is CategoryItem cat)
                    _categoryDragItem = cat;
            }
        }

        private void LstCategories_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _categoryDragItem == null) return;
            if (_catMarqueeActive) return;
            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _categoryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _categoryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            if (_categoryDragItem.Id == "All") return;

            var dragged = _categoryDragItem;
            TxtDragGhost.Text = dragged.DisplayLabel;
            CategoryDragPopup.IsOpen = true;

            if (LstCategories.ItemContainerGenerator.ContainerFromItem(dragged) is ListBoxItem lbi)
                lbi.Opacity = 0.35;

            try
            {
                DragDrop.DoDragDrop(LstCategories, dragged, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Category drag: {ex.Message}");
            }
            finally
            {
                CategoryDragPopup.IsOpen = false;
                if (LstCategories.ItemContainerGenerator.ContainerFromItem(dragged) is ListBoxItem lbi2)
                    lbi2.Opacity = 1;
                _categoryDragItem = null;
            }
        }

        private void LstCategories_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.Hand);
            e.Handled = true;

            // Sadece kategori suruklerken ghost
            if (_categoryDragItem == null)
            {
                CategoryDragPopup.IsOpen = false;
                return;
            }

            Point screen = GetMouseScreenPoint();
            CategoryDragPopup.HorizontalOffset = screen.X + 12;
            CategoryDragPopup.VerticalOffset = screen.Y + 12;
            if (!CategoryDragPopup.IsOpen)
                CategoryDragPopup.IsOpen = true;
        }

        private void LstCategories_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed)
            {
                e.Action = DragAction.Cancel;
                CategoryDragPopup.IsOpen = false;
                ClearCatDropVisuals();
            }
        }

        private void LstCategories_DragLeave(object sender, DragEventArgs e)
        {
            // Panel disina cikinca turuncu glow takili kalmasin
            Point p = e.GetPosition(LstCategories);
            if (p.X < 0 || p.Y < 0 || p.X > LstCategories.ActualWidth || p.Y > LstCategories.ActualHeight)
                ClearCatDropVisuals();
        }

        private static Point GetMouseScreenPoint()
        {
            GetCursorPos(out var pt);
            return new Point(pt.X, pt.Y);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        private void LstCategories_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(typeof(CategoryItem)))
                {
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                    var dragged = e.Data.GetData(typeof(CategoryItem)) as CategoryItem;
                    UpdateCatDropVisuals(e.GetPosition(LstCategories), dragged);
                    return;
                }

                if (e.Data.GetDataPresent("DownloadItems") || e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                    // Dosya birakma: hedef kategoriyi sadece ince cerceve ile goster (glow yok)
                    ClearCatDropVisuals();
                    var hit = HitCategoryAt(e.GetPosition(LstCategories));
                    if (hit.lbi != null)
                    {
                        Point local = hit.lbi.TranslatePoint(new Point(0, 0), LstCategories);
                        Canvas.SetLeft(CatDropNestHighlight, local.X);
                        Canvas.SetTop(CatDropNestHighlight, local.Y);
                        CatDropNestHighlight.Width = hit.lbi.ActualWidth;
                        CatDropNestHighlight.Height = hit.lbi.ActualHeight;
                        CatDropNestHighlight.Visibility = Visibility.Visible;
                    }
                    return;
                }

                e.Effects = DragDropEffects.None;
                ClearCatDropVisuals();
                e.Handled = true;
            }
            catch
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private (ListBoxItem? lbi, CategoryItem? cat) HitCategoryAt(Point posInList)
        {
            foreach (var cat in VisibleCategories.ToList())
            {
                if (LstCategories.ItemContainerGenerator.ContainerFromItem(cat) is not ListBoxItem lbi)
                    continue;
                Point tl = lbi.TranslatePoint(new Point(0, 0), LstCategories);
                var r = new Rect(tl.X, tl.Y, lbi.ActualWidth, Math.Max(1, lbi.ActualHeight));
                if (r.Contains(posInList)) return (lbi, cat);
            }
            return (null, null);
        }

        private void BtnAllDownloads_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(CategoryItem)))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.Move;
            _catDropKind = CatDropKind.ToRoot;
            _catDropTarget = null;
            ClearCatDropVisuals();
            e.Handled = true;
        }

        private void BtnAllDownloads_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetData(typeof(CategoryItem)) is not CategoryItem dragged) return;
                if (dragged.Id == "All") return;
                MoveCategoryToRoot(dragged);
                e.Handled = true;
            }
            finally
            {
                ClearCatDropVisuals();
                CategoryDragPopup.IsOpen = false;
            }
        }

        private void UpdateCatDropVisuals(Point posInList, CategoryItem? dragged)
        {
            _catDropKind = CatDropKind.None;
            _catDropTarget = null;

            // Suruklenen ogeyi atla; Y konumuna gore en yakin satir / aralik
            ListBoxItem? hitLbi = null;
            CategoryItem? hitCat = null;
            double bestDist = double.MaxValue;

            foreach (var cat in VisibleCategories.ToList())
            {
                if (ReferenceEquals(cat, dragged)) continue;
                if (dragged != null && cat.IsDescendantOf(dragged)) continue;
                if (LstCategories.ItemContainerGenerator.ContainerFromItem(cat) is not ListBoxItem lbi)
                    continue;

                Point tl = lbi.TranslatePoint(new Point(0, 0), LstCategories);
                var r = new Rect(tl.X, tl.Y, lbi.ActualWidth, Math.Max(1, lbi.ActualHeight));
                if (r.Contains(posInList))
                {
                    hitLbi = lbi;
                    hitCat = cat;
                    bestDist = 0;
                    break;
                }

                double midY = tl.Y + lbi.ActualHeight / 2;
                double dist = Math.Abs(posInList.Y - midY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    hitLbi = lbi;
                    hitCat = cat;
                }
            }

            if (hitLbi == null || hitCat == null)
            {
                ClearCatDropVisuals();
                if (VisibleCategories.Count > 0)
                {
                    var firstVis = VisibleCategories.FirstOrDefault(c => !ReferenceEquals(c, dragged));
                    var lastVis = VisibleCategories.LastOrDefault(c => !ReferenceEquals(c, dragged));
                    if (firstVis != null
                        && LstCategories.ItemContainerGenerator.ContainerFromItem(firstVis) is ListBoxItem firstLbi
                        && posInList.Y < firstLbi.TranslatePoint(new Point(0, 0), LstCategories).Y + 8)
                    {
                        _catDropKind = CatDropKind.InsertBefore;
                        _catDropTarget = firstVis;
                        Point tl = firstLbi.TranslatePoint(new Point(0, 0), LstCategories);
                        ShowInsertLine(tl.Y, firstLbi.ActualWidth);
                    }
                    else if (lastVis != null
                             && LstCategories.ItemContainerGenerator.ContainerFromItem(lastVis) is ListBoxItem lastLbi)
                    {
                        _catDropKind = CatDropKind.InsertAfter;
                        _catDropTarget = lastVis;
                        Point tl = lastLbi.TranslatePoint(new Point(0, 0), LstCategories);
                        ShowInsertLine(tl.Y + lastLbi.ActualHeight, lastLbi.ActualWidth);
                    }
                }
                return;
            }

            Point local = hitLbi.TranslatePoint(new Point(0, 0), LstCategories);
            double h = Math.Max(1, hitLbi.ActualHeight);
            double ratioY = (posInList.Y - local.Y) / h;

            // Built-in kategoriler kökte kalır — sadece siralama
            // Özel kategoriler: ortada içine al, kenarlarda sıraya koy
            bool canNest = dragged != null && !dragged.IsBuiltin && hitCat.Id != "All"
                           && !hitCat.IsDescendantOf(dragged!);
            bool nestZone = canNest && ratioY >= 0.28 && ratioY <= 0.72;

            if (nestZone)
            {
                _catDropKind = CatDropKind.Nest;
                _catDropTarget = hitCat;
                CatDropInsertLine.Visibility = Visibility.Collapsed;
                Canvas.SetLeft(CatDropNestHighlight, local.X);
                Canvas.SetTop(CatDropNestHighlight, local.Y);
                CatDropNestHighlight.Width = hitLbi.ActualWidth;
                CatDropNestHighlight.Height = hitLbi.ActualHeight;
                CatDropNestHighlight.Visibility = Visibility.Visible;
            }
            else if (ratioY < 0.5)
            {
                _catDropKind = CatDropKind.InsertBefore;
                _catDropTarget = hitCat;
                CatDropNestHighlight.Visibility = Visibility.Collapsed;
                ShowInsertLine(local.Y, hitLbi.ActualWidth);
            }
            else
            {
                _catDropKind = CatDropKind.InsertAfter;
                _catDropTarget = hitCat;
                CatDropNestHighlight.Visibility = Visibility.Collapsed;
                ShowInsertLine(local.Y + hitLbi.ActualHeight, hitLbi.ActualWidth);
            }
        }

        private void ShowInsertLine(double y, double width)
        {
            Canvas.SetLeft(CatDropInsertLine, 4);
            Canvas.SetTop(CatDropInsertLine, y - 1.5);
            CatDropInsertLine.Width = Math.Max(80, width - 8);
            CatDropInsertLine.Visibility = Visibility.Visible;
        }

        private List<DownloadItem> ExtractDroppedDownloadItems(IDataObject data)
        {
            var result = new List<DownloadItem>();
            var seen = new HashSet<DownloadItem>();

            void Add(DownloadItem? item)
            {
                if (item == null || !seen.Add(item)) return;
                result.Add(item);
            }

            try
            {
                if (data.GetDataPresent("DownloadItems"))
                {
                    switch (data.GetData("DownloadItems"))
                    {
                        case List<DownloadItem> list:
                            foreach (var i in list) Add(i);
                            break;
                        case DownloadItem[] arr:
                            foreach (var i in arr) Add(i);
                            break;
                        case IEnumerable<DownloadItem> en:
                            foreach (var i in en) Add(i);
                            break;
                    }
                }

                if (data.GetDataPresent("DownloadItem") && data.GetData("DownloadItem") is DownloadItem one)
                    Add(one);

                // Secili satirlar da dahil (coklu surukle sirasinda)
                if (result.Count <= 1 && DgDownloads.SelectedItems.Count > 1)
                {
                    foreach (DownloadItem sel in DgDownloads.SelectedItems)
                        Add(sel);
                }

                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
                {
                    foreach (string path in paths)
                    {
                        var match = DownloadList.FirstOrDefault(d =>
                            string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
                        Add(match);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractDroppedDownloadItems: {ex.Message}");
            }

            return result;
        }

        private void ClearCatDropVisuals()
        {
            CatDropInsertLine.Visibility = Visibility.Collapsed;
            CatDropNestHighlight.Visibility = Visibility.Collapsed;
        }

        private void LstCategories_Drop(object sender, DragEventArgs e)
        {
            try
            {
                CategoryDragPopup.IsOpen = false;

                // Dosya(lar) kategoriye birakildi
                if (e.Data.GetDataPresent("DownloadItems") || e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var items = ExtractDroppedDownloadItems(e.Data);
                    var hit = HitCategoryAt(e.GetPosition(LstCategories));
                    if (items.Count > 0 && hit.cat != null && hit.cat.Id != "All")
                    {
                        MoveItemsToCategory(items, hit.cat.Id);
                        e.Handled = true;
                    }
                    return;
                }

                if (e.Data.GetData(typeof(CategoryItem)) is not CategoryItem dragged) return;
                if (dragged.Id == "All") return;

                UpdateCatDropVisuals(e.GetPosition(LstCategories), dragged);

                switch (_catDropKind)
                {
                    case CatDropKind.Nest when _catDropTarget != null:
                        NestCategoryInto(dragged, _catDropTarget);
                        break;
                    case CatDropKind.InsertBefore when _catDropTarget != null:
                        InsertCategoryBeside(dragged, _catDropTarget, before: true);
                        break;
                    case CatDropKind.InsertAfter when _catDropTarget != null:
                        InsertCategoryBeside(dragged, _catDropTarget, before: false);
                        break;
                    case CatDropKind.ToRoot:
                        MoveCategoryToRoot(dragged);
                        break;
                    default:
                        // Hedef belirsizse en yakin konumda birak (yeniden hesap)
                        UpdateCatDropVisuals(e.GetPosition(LstCategories), dragged);
                        if (_catDropKind == CatDropKind.InsertBefore && _catDropTarget != null)
                            InsertCategoryBeside(dragged, _catDropTarget, before: true);
                        else if (_catDropKind == CatDropKind.InsertAfter && _catDropTarget != null)
                            InsertCategoryBeside(dragged, _catDropTarget, before: false);
                        else if (_catDropKind == CatDropKind.Nest && _catDropTarget != null)
                            NestCategoryInto(dragged, _catDropTarget);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LstCategories_Drop: {ex}");
                CategoryDragPopup.IsOpen = false;
                _categoryDragItem = null;
            }
            finally
            {
                ClearCatDropVisuals();
                _catDropKind = CatDropKind.None;
                _catDropTarget = null;
            }
        }

        private void MoveCategoryToRoot(CategoryItem dragged)
        {
            // Built-in zaten kökteyse sadece sırayı koru
            DetachCategory(dragged);
            dragged.ParentId = null;
            dragged.Depth = 0;
            if (!Categories.Contains(dragged))
            {
                int insertAt = Categories.Any(c => c.Id == "All") ? 1 : 0;
                // Built-in'leri diğer built-in'lerin yanına, özelleri sona yakın
                if (!dragged.IsBuiltin)
                    insertAt = Categories.Count;
                else
                {
                    int lastBuiltin = -1;
                    for (int i = 0; i < Categories.Count; i++)
                    {
                        if (Categories[i].IsBuiltin && Categories[i].Id != "All")
                            lastBuiltin = i;
                    }
                    if (lastBuiltin >= 0) insertAt = lastBuiltin + 1;
                }
                insertAt = Math.Clamp(insertAt, Categories.Any(c => c.Id == "All") ? 1 : 0, Categories.Count);
                Categories.Insert(insertAt, dragged);
            }
            FinishCategoryMove(dragged);
        }

        private void NestCategoryInto(CategoryItem dragged, CategoryItem target)
        {
            if (ReferenceEquals(target, dragged) || target.IsDescendantOf(dragged) || target.Id == "All")
                return;
            // Native (built-in) kategoriler iç içe girmez
            if (dragged.IsBuiltin)
            {
                InsertCategoryBeside(dragged, target, before: false);
                return;
            }

            DetachCategory(dragged);
            dragged.ParentId = target.Id;
            if (!target.Children.Contains(dragged))
                target.Children.Add(dragged);
            target.IsExpanded = true;
            target.NotifyChildrenChanged();
            FinishCategoryMove(dragged);
        }

        private void InsertCategoryBeside(CategoryItem dragged, CategoryItem target, bool before)
        {
            if (ReferenceEquals(target, dragged) || target.IsDescendantOf(dragged))
                return;

            // Built-in başka bir kategorinin altına (sibling nested) girmesin
            if (dragged.IsBuiltin && !string.IsNullOrEmpty(target.ParentId))
            {
                // Hedefin kök seviyesindeki atasına göre sırala
                var rootTarget = target;
                while (!string.IsNullOrEmpty(rootTarget.ParentId))
                {
                    var p = CategoryStore.FindById(Categories, rootTarget.ParentId!);
                    if (p == null) break;
                    rootTarget = p;
                }
                target = rootTarget;
            }

            DetachCategory(dragged);

            if (string.IsNullOrEmpty(target.ParentId) || dragged.IsBuiltin)
            {
                dragged.ParentId = null;
                dragged.Depth = 0;
                int idx = Categories.IndexOf(target);
                if (idx < 0) idx = Categories.Count;
                if (!before) idx++;
                int minIdx = Categories.Any(c => c.Id == "All") ? 1 : 0;
                idx = Math.Clamp(idx, minIdx, Categories.Count);
                if (Categories.Contains(dragged))
                    Categories.Remove(dragged);
                Categories.Insert(Math.Clamp(idx, minIdx, Categories.Count), dragged);
            }
            else
            {
                var parent = CategoryStore.FindById(Categories, target.ParentId!);
                if (parent == null)
                {
                    MoveCategoryToRoot(dragged);
                    return;
                }
                dragged.ParentId = parent.Id;
                int idx = parent.Children.IndexOf(target);
                if (idx < 0) idx = parent.Children.Count;
                if (!before) idx++;
                idx = Math.Clamp(idx, 0, parent.Children.Count);
                if (parent.Children.Contains(dragged))
                    parent.Children.Remove(dragged);
                parent.Children.Insert(idx, dragged);
                parent.NotifyChildrenChanged();
            }

            FinishCategoryMove(dragged);
        }

        private void FinishCategoryMove(CategoryItem dragged)
        {
            CategoryStore.RecalcDepths(Categories, 0);
            CategoryStore.Save(Categories);
            CategoryStore.EnsureDiskFolders(Categories, _defaultFolder);
            RebuildVisibleCategories();
            LstCategories.SelectedItem = dragged;
        }

        private void DetachCategory(CategoryItem item)
        {
            if (!string.IsNullOrEmpty(item.ParentId))
            {
                var parent = CategoryStore.FindById(Categories, item.ParentId);
                parent?.Children.Remove(item);
                parent?.NotifyChildrenChanged();
            }
            else
            {
                Categories.Remove(item);
            }
            item.ParentId = null;
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void MenuMoveCategory_Click(object sender, RoutedEventArgs e)
        {
            var selected = DgDownloads.SelectedItems.OfType<DownloadItem>().ToList();
            if (selected.Count == 0 && DgDownloads.SelectedItem is DownloadItem one)
                selected.Add(one);
            if (selected.Count == 0) return;

            var choices = CategoryStore.AllFlat(Categories).Where(c => c.Id != "All").ToList();
            if (choices.Count == 0) return;

            var menu = new ContextMenu
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            menu.Template = CreateDarkMenuTemplate();

            foreach (var cat in choices)
            {
                var mi = new MenuItem
                {
                    Header = new string(' ', cat.Depth * 2) + cat.DisplayLabel,
                    Tag = cat.Id,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(10, 5, 10, 5),
                    FontSize = 11,
                    Height = 28,
                    FocusVisualStyle = null
                };
                mi.Template = CreateDarkMenuItemTemplate();
                mi.Click += (_, _) =>
                {
                    MoveItemsToCategory(selected, (string)mi.Tag);
                };
                menu.Items.Add(mi);
            }

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        public void MoveItemsToCategory(IEnumerable<DownloadItem> items, string newCatId)
        {
            foreach (var item in items)
            {
                item.CategoryId = newCatId;
                try
                {
                    string destDir = CategoryStore.GetCategoryFolderPath(Categories, newCatId, _defaultFolder);
                    if (File.Exists(item.FilePath))
                    {
                        string dest = Path.Combine(destDir, item.FileName);
                        if (!string.Equals(Path.GetFullPath(item.FilePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        {
                            dest = GetUniqueFilePath(destDir, item.FileName);
                            string oldPath = item.FilePath;
                            DownloadPathHelper.MoveFileAndState(oldPath, dest);
                            item.FilePath = dest;
                            item.FileName = Path.GetFileName(dest);
                            RebindEngineAfterPathChange(item, dest);
                        }
                    }
                    else
                    {
                        // Dosya yoksa yine de kategori klasor yolunu guncelle
                        item.FilePath = Path.Combine(destDir, item.FileName);
                    }
                }
                catch { /* ignore move errors */ }
            }
            _downloadView?.Refresh();
            RefreshCategoryCounts();
            QueueHistorySave();
        }

        private static ControlTemplate CreateDarkMenuTemplate()
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));
            factory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            factory.SetValue(Border.PaddingProperty, new Thickness(4));
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(Panel.IsItemsHostProperty, true);
            factory.AppendChild(panel);
            template.VisualTree = factory;
            return template;
        }

        private static ControlTemplate CreateDarkMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "bd";
            factory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(MenuItem.BackgroundProperty));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            factory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(MenuItem.PaddingProperty));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            template.VisualTree = factory;

            var trigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x21, 0x18)), "bd"));
            trigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(trigger);

            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x21, 0x18)), "bd"));
            kf.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(kf);
            return template;
        }

        private void RowPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DownloadItem item }) return;
            if (!_engines.TryGetValue(item, out var engine)) return;

            if (engine.IsPaused || item.IsPausedState || item.IsErrorState)
            {
                _ = ResumeFromSessionAsync(item, engine);
            }
            else if (engine.IsDownloading && !engine.IsPaused)
            {
                PauseFromSession(item, engine);
            }
        }

        private void ListPanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border border) return;
            double r = _isPseudoMaximized ? 0 : 14;
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                r, r);
        }

        private static double SafeClamp(double value, double min, double max)
        {
            if (max < min) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeNative();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                ToggleMaximizeNative();
                e.Handled = true;
                return;
            }

            _titleDragStart = e.GetPosition(this);

            if (WindowState == WindowState.Maximized)
            {
                // Basılı tutup sürükleyince Normal'e inecek
                _titleDragRestoring = true;
                e.Handled = true;
                return;
            }

            try { DragMove(); }
            catch { /* ignore */ }
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!_titleDragRestoring && WindowState != WindowState.Maximized) return;
            if (_titleDragStart == null && !_titleDragRestoring) return;

            try
            {
                Point local = e.GetPosition(this);
                if (_titleDragStart != null)
                {
                    Vector delta = local - _titleDragStart.Value;
                    if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
                }

                // Oran ve ekran konumu — henüz Maximized iken ölç
                double percentX = ActualWidth > 1 ? local.X / ActualWidth : 0.5;
                percentX = SafeClamp(percentX, 0.05, 0.95);
                Point mouseScreen = PointToScreen(local);

                _titleDragRestoring = false;
                _titleDragStart = null;

                // WPF'nin sakladığı restore boyutu (maximize öncesi)
                Rect rb = RestoreBounds;
                var wa = MonitorWorkArea.Get(this);

                double w = rb.Width > MinWidth ? rb.Width : Math.Min(980, wa.Width * 0.7);
                double h = rb.Height > MinHeight ? rb.Height : Math.Min(600, wa.Height * 0.7);
                w = Math.Min(w, Math.Max(MinWidth, wa.Width - 16));
                h = Math.Min(h, Math.Max(MinHeight, wa.Height - 16));

                WindowState = WindowState.Normal;

                double left = mouseScreen.X - (w * percentX);
                double top = mouseScreen.Y - 12;
                left = SafeClamp(left, wa.Left, Math.Max(wa.Left, wa.Right - w));
                top = SafeClamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - 48));

                Width = w;
                Height = h;
                Left = left;
                Top = top;
                _restoreBounds = new Rect(left, top, w, h);

                UpdateChromeForWindowState();
                e.Handled = true;

                // Sürüklemeye devam et (basılı kaldığı sürece)
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TitleBar drag-from-max: {ex}");
                _titleDragRestoring = false;
                _titleDragStart = null;
                try
                {
                    if (WindowState == WindowState.Maximized)
                        WindowState = WindowState.Normal;
                }
                catch { /* ignore */ }
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _titleDragStart = null;
            _titleDragRestoring = false;
        }

        private bool IsEffectivelyMaximized() => WindowState == WindowState.Maximized;

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            // Maximize sırasında restore bounds'u bozma; Normal'de ve sürükleme değilken sakla
            if (WindowState == WindowState.Normal
                && !_titleDragRestoring
                && ActualWidth > 100 && ActualHeight > 100
                && ActualWidth < SystemParameters.WorkArea.Width - 2)
            {
                _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            }

            if (WindowState == WindowState.Minimized && !_exitRequested)
            {
                Dispatcher.BeginInvoke(HideToTray, DispatcherPriority.Background);
            }

            UpdateChromeForWindowState();
        }

        private void ToggleMaximizeNative()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                if (WindowState == WindowState.Normal && ActualWidth > 50)
                    _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
                WindowState = WindowState.Maximized;
            }
        }

        private void ToggleMaximizeFast() => ToggleMaximizeNative();
        private void ToggleMaximize() => ToggleMaximizeNative();
        private void ToggleMaximizeAnimated() => ToggleMaximizeNative();
        private void ApplyWindowStateToggle() => ToggleMaximizeNative();
        private void ApplyPseudoMaximized(bool maximized)
        {
            if (maximized && WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
            else if (!maximized && WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }

        private void UpdateChromeForWindowState()
        {
            bool max = WindowState == WindowState.Maximized;
            _isPseudoMaximized = max;
            BtnMaximize.Content = max ? "❐" : "☐";
            RootChrome.CornerRadius = new CornerRadius(0);
            TitleBarChrome.CornerRadius = new CornerRadius(0);
            ContentChrome.CornerRadius = new CornerRadius(0);
            RootChrome.BorderThickness = max ? new Thickness(0) : new Thickness(1);
            if (ListPanelBorder.ActualWidth > 0)
            {
                ListPanelBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, ListPanelBorder.ActualWidth, ListPanelBorder.ActualHeight),
                    max ? 0 : 14, max ? 0 : 14);
            }
        }

        private Rect GetCurrentWorkArea() => MonitorWorkArea.Get(this);

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = TxtSearch.Text?.Trim() ?? "";
            _downloadView?.Refresh();
        }

        private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TxtSearch.SelectAll();
                e.Handled = true;
                return;
            }
        }

        private async void ToolbarNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NewUrlDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            await StartDownloadProcess(dlg.Url, "", selectItem: true);
        }

        private void ToolbarSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            var settings = AppSettingsStore.Load();
            var menu = new ContextMenu
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null,
                PlacementTarget = fe,
                Placement = PlacementMode.Bottom
            };
            menu.Template = CreateDarkMenuTemplate();

            MenuItem Make(string header, RoutedEventHandler? click = null, bool checkable = false, bool isChecked = false)
            {
                var mi = new MenuItem
                {
                    Header = header,
                    IsCheckable = checkable,
                    IsChecked = isChecked,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    Background = Brushes.Transparent,
                    FontSize = 11,
                    Padding = new Thickness(10, 5, 10, 5),
                    Height = 28,
                    FocusVisualStyle = null,
                    Template = CreateDarkMenuItemTemplate()
                };
                if (click != null) mi.Click += click;
                return mi;
            }

            var openAll = Make("Tüm ayarlar…", (_, _) => OpenSettingsDialog());
            openAll.FontWeight = FontWeights.SemiBold;
            openAll.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            menu.Items.Add(openAll);
            menu.Items.Add(new Separator());

            var autoStart = Make("Windows ile birlikte başlat", checkable: true, isChecked: settings.AutoStart);
            autoStart.Click += (_, _) =>
            {
                settings.AutoStart = autoStart.IsChecked;
                if (settings.AutoStart)
                    AutoStartHelper.Enable(settings.AutoStartMinimized);
                else
                    AutoStartHelper.Disable();
                AppSettingsStore.Save(settings);
            };
            menu.Items.Add(autoStart);
            menu.Items.Add(new Separator());

            menu.Items.Add(Make("Varsayılan klasörü aç", (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(_defaultFolder);
                    Process.Start(new ProcessStartInfo(_defaultFolder) { UseShellExecute = true });
                }
                catch (Exception ex) { InfoDialog.Show(this, "Klasör", "Açılamadı.", ex.Message); }
            }));

            menu.Items.Add(Make("Eklenti klasörünü aç", (_, _) =>
            {
                try
                {
                    string dir = ExtensionInstaller.InstallRoot;
                    if (!Directory.Exists(dir))
                    {
                        InfoDialog.Show(this, "Eklenti", "Eklenti klasörü bulunamadı.", dir);
                        return;
                    }
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch (Exception ex) { InfoDialog.Show(this, "Eklenti", "Klasör açılamadı.", ex.Message); }
            }));

            menu.Items.Add(new Separator());
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string verText = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
            menu.Items.Add(Make($"Hakkında  ·  {verText}", (_, _) =>
            {
                InfoDialog.Show(this, "MuckDownloadManager",
                    $"Sürüm {verText}",
                    "İndirmeleri kategorilere ayıran masaüstü indirme yöneticisi.");
            }));

            menu.IsOpen = true;
        }

        private void OpenSettingsDialog()
        {
            var settings = AppSettingsStore.Load();
            var dlg = new SettingsDialog(settings, _defaultFolder) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            settings = AppSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder))
            {
                _defaultFolder = settings.DefaultDownloadFolder;
                try { Directory.CreateDirectory(_defaultFolder); } catch { /* ignore */ }
                CategoryStore.EnsureDiskFolders(Categories, _defaultFolder);
            }
        }

        private List<DownloadItem> GetToolbarTargets()
        {
            var checkedItems = DownloadList.Where(i => i.IsChecked).ToList();
            if (checkedItems.Count > 0) return checkedItems;

            return DgDownloads.SelectedItems.OfType<DownloadItem>().ToList();
        }

        private void ToolbarDelete_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, "Sil", "İşlem için dosya seçin veya tikleyin.");
                return;
            }
            DeleteItems(targets);
        }

        private void ToolbarOpen_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, "Aç", "İşlem için dosya seçin veya tikleyin.");
                return;
            }

            foreach (var item in targets)
            {
                if (!File.Exists(item.FilePath))
                {
                    InfoDialog.Show(this, "Dosya", $"Dosya bulunamadı: {item.FileName}");
                    continue;
                }
                try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
                catch (Exception ex) { InfoDialog.Show(this, "Hata", ex.Message); }
            }
        }

        private void ToolbarOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, "Klasör", "İşlem için dosya seçin veya tikleyin.");
                return;
            }

            foreach (var selectedItem in targets)
            {
                try
                {
                    if (File.Exists(selectedItem.FilePath))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectedItem.FilePath}\"") { UseShellExecute = true });
                    else if (Directory.Exists(Path.GetDirectoryName(selectedItem.FilePath)))
                        Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(selectedItem.FilePath)!) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    InfoDialog.Show(this, "Hata", ex.Message);
                }
            }
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = ChkSelectAll.IsChecked == true;
            foreach (var item in DownloadList)
                item.IsChecked = check;
        }

        private void RowOpenDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DownloadItem item }) return;
            ShowSessionForItem(item);
        }

        private void RowCancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DownloadItem item }) return;
            if (_engines.TryGetValue(item, out var engine))
                CancelFromSession(item, engine);
        }

        private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DgDownloads.SelectedItem is DownloadItem selectedItem)
            {
                try
                {
                    if (File.Exists(selectedItem.FilePath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectedItem.FilePath}\"") { UseShellExecute = true });
                    }
                    else if (Directory.Exists(Path.GetDirectoryName(selectedItem.FilePath)))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(selectedItem.FilePath)!) { UseShellExecute = true });
                    }
                    else
                    {
                        InfoDialog.Show(this, "Hata", "Dosya veya dizin bulunamadı.");
                    }
                }
                catch (Exception ex)
                {
                    InfoDialog.Show(this, "Hata", $"Klasör açılırken hata oluştu: {ex.Message}");
                }
            }
        }

        private void MenuCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var item = DgDownloads.SelectedItem as DownloadItem;
            if (item == null) return;
            string url = item.Url;
            if (string.IsNullOrWhiteSpace(url))
                _itemUrls.TryGetValue(item, out url!);
            if (string.IsNullOrWhiteSpace(url))
            {
                InfoDialog.Show(this, "Bağlantı", "Bu indirme için kayıtlı bağlantı yok.");
                return;
            }
            try
            {
                Clipboard.SetText(url);
                ShowCopyToast();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Hata", ex.Message);
            }
        }

        private void ShowCopyToast()
        {
            _copyToastTimer?.Stop();
            CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
            CopyToast.Visibility = Visibility.Visible;
            CopyToast.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            CopyToast.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _copyToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
            _copyToastTimer.Tick += (_, _) =>
            {
                _copyToastTimer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) =>
                {
                    CopyToast.Visibility = Visibility.Collapsed;
                    CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
                };
                CopyToast.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            _copyToastTimer.Start();
        }

        private async void MenuRedownload_Click(object sender, RoutedEventArgs e)
        {
            if (DgDownloads.SelectedItem is not DownloadItem item) return;
            string url = item.Url;
            if (string.IsNullOrWhiteSpace(url))
                _itemUrls.TryGetValue(item, out url!);
            if (string.IsNullOrWhiteSpace(url))
            {
                InfoDialog.Show(this, "Yeniden indir", "Bu öğe için bağlantı bulunamadı.");
                return;
            }
            await StartDownloadProcess(url, item.FileName, selectItem: true);
        }

        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (DgDownloads.SelectedItem is not DownloadItem item) return;

            string currentName = item.FileName;
            var dlg = new PromptDialog("Yeniden adlandır", "Yeni dosya adı:", currentName, extensionLockMode: true)
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true) return;

            string newName = dlg.ResultText.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == currentName) return;
            foreach (char c in Path.GetInvalidFileNameChars())
                newName = newName.Replace(c, '_');

            // Tik yoksa uzantiyi zorla koru
            if (!dlg.AllowExtensionChange)
            {
                string origExt = Path.GetExtension(currentName);
                string baseName = Path.GetFileNameWithoutExtension(newName);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = Path.GetFileNameWithoutExtension(currentName);
                newName = baseName + origExt;
            }

            if (newName == currentName) return;

            try
            {
                string? dir = Path.GetDirectoryName(item.FilePath);
                if (string.IsNullOrEmpty(dir))
                {
                    InfoDialog.Show(this, "Yeniden adlandır", "Dosya yolu geçersiz.");
                    return;
                }

                string dest = Path.Combine(dir, newName);
                if (File.Exists(dest))
                {
                    InfoDialog.Show(this, "Yeniden adlandır", "Bu isimde bir dosya zaten var.");
                    return;
                }

                if (File.Exists(item.FilePath))
                    DownloadPathHelper.MoveFileAndState(item.FilePath, dest);
                else if (File.Exists(DownloadPathHelper.StatePath(item.FilePath)))
                    DownloadPathHelper.MoveFileAndState(item.FilePath, dest);

                item.FileName = newName;
                item.FilePath = dest;
                item.FileType = FileNameHelper.FormatTypeLabel(newName);
                item.FileIcon = IconHelper.GetIconForExtension(newName);
                RebindEngineAfterPathChange(item, dest);
                QueueHistorySave();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Hata", ex.Message);
            }
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedItems();
        }

        private void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            _downloadView?.Refresh();
            DgDownloads.Items.Refresh();
            UpdateTransportButtons();
        }

        private void DeleteSelectedItems()
        {
            var selectedItems = GetToolbarTargets();
            if (selectedItems.Count == 0) return;
            DeleteItems(selectedItems);
        }

        private void DeleteItems(List<DownloadItem> selectedItems)
        {
            if (selectedItems.Count == 0) return;

            string message = selectedItems.Count == 1
                ? $"“{selectedItems[0].FileName}” kalıcı olarak silinsin mi?"
                : $"{selectedItems.Count} öğe kalıcı olarak silinsin mi?";

            string detail = selectedItems.Count == 1
                ? "Dosya listeden kaldırılır ve diskteki kopyası da silinir."
                : "Seçili dosyalar listeden kaldırılır ve diskteki kopyaları da silinir.";

            if (!ConfirmDialog.Show(this, "Silme onayı", message, detail,
                    confirmText: "Sil", danger: true))
                return;

            foreach (DownloadItem selectedItem in selectedItems)
            {
                if (_engines.TryGetValue(selectedItem, out var engine))
                {
                    engine.Cancel();
                    _engines.Remove(selectedItem);
                }

                try
                {
                    DownloadPathHelper.DeleteFileAndState(selectedItem.FilePath);
                }
                catch (Exception ex)
                {
                    InfoDialog.Show(this, "Uyarı", $"'{selectedItem.FileName}' silinemedi: {ex.Message}");
                }

                DownloadList.Remove(selectedItem);
                _itemUrls.Remove(selectedItem);
                _sessionWindows.Remove(selectedItem);
            }

            UpdateTransportButtons();
            QueueHistorySave();
        }

        private string GetUniqueFilePath(string folderPath, string filename)
        {
            string fullPath = Path.Combine(folderPath, filename);
            if (!File.Exists(fullPath)) return fullPath;

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            string extension = Path.GetExtension(filename);
            int count = 1;

            while (File.Exists(fullPath))
            {
                string newFileName = $"{nameWithoutExtension} ({count}){extension}";
                fullPath = Path.Combine(folderPath, newFileName);
                count++;
            }

            return fullPath;
        }

        private async Task<string> ResolveFileNameAsync(string url, string suggestedName, string? mimeHint = null)
        {
            string? contentType = null;
            string? fromHeader = null;

            string decodedSuggested = FileNameHelper.DecodeDisplayName(suggestedName);

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                HttpResponseMessage? response = null;
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                }
                catch { /* bazi sunucular HEAD kabul etmez */ }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    response?.Dispose();
                    using var get = new HttpRequestMessage(HttpMethod.Get, url);
                    response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }

                using (response)
                {
                    contentType = response.Content.Headers.ContentType?.MediaType;
                    fromHeader = FileNameHelper.ExtractFromContentDisposition(response.Content.Headers);
                }
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(contentType) && !string.IsNullOrWhiteSpace(mimeHint))
                contentType = mimeHint;

            string? fromUrl = FileNameHelper.TryFileNameFromUrl(url);

            string chosen = PickBestFileName(fromHeader, decodedSuggested, fromUrl) ?? "download";
            chosen = FileNameHelper.EnsureExtension(chosen, contentType);

            if (string.IsNullOrEmpty(Path.GetExtension(chosen)))
            {
                string ext = FileNameHelper.GuessExtensionFromContentType(contentType);
                if (FileNameHelper.IsPlaceholderName(chosen))
                    chosen = "download" + (string.IsNullOrEmpty(ext) ? ".bin" : ext);
                else if (!string.IsNullOrEmpty(ext))
                    chosen += ext;
            }

            return chosen;
        }

        private static string? PickBestFileName(params string?[] candidates)
        {
            string? strong = candidates.FirstOrDefault(n =>
                !string.IsNullOrWhiteSpace(n) && !FileNameHelper.IsPlaceholderName(n));
            if (strong != null) return strong;

            string? withExt = candidates.FirstOrDefault(n =>
                !string.IsNullOrWhiteSpace(n) && !string.IsNullOrEmpty(Path.GetExtension(n!)));
            if (withExt != null) return withExt;

            return candidates.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            // Ust URL cubugu kaldirildi; eklenti / oturum penceresi kullanilir
            await Task.CompletedTask;
        }

        private async Task StartDownloadProcess(string url, string incomingFilename, string? mimeHint = null, bool selectItem = true)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                InfoDialog.Show(this, "Uyarı", "Lütfen geçerli bir indirme bağlantısı girin!");
                return;
            }

            string urlKey = NormalizeCaptureUrl(url);

            // Aynı URL için kısa sürede tekrar pencere açma (eklenti spam / redirect)
            lock (_captureGate)
            {
                if (_recentCaptureUrls.TryGetValue(urlKey, out DateTime last)
                    && DateTime.UtcNow - last < CaptureDebounce)
                {
                    Debug.WriteLine($"Capture debounce: {urlKey}");
                    return;
                }

                if (_pendingSessionUrls.Contains(urlKey))
                {
                    Debug.WriteLine($"Capture already pending: {urlKey}");
                    return;
                }

                // Açık oturum varsa öne getir
                foreach (var kv in _sessionWindows.ToList())
                {
                    if (NormalizeCaptureUrl(kv.Value.SessionUrl) == urlKey)
                    {
                        try
                        {
                            if (!kv.Value.IsVisible) kv.Value.Show();
                            kv.Value.Activate();
                            kv.Value.BringToFrontSoft();
                        }
                        catch { /* ignore */ }
                        _recentCaptureUrls[urlKey] = DateTime.UtcNow;
                        return;
                    }
                }

                // Listede aynı URL ile aktif/bekleyen indirme varsa yeni pencere açma
                foreach (var item in DownloadList)
                {
                    string u = item.Url;
                    if (string.IsNullOrWhiteSpace(u))
                        _itemUrls.TryGetValue(item, out u!);
                    if (NormalizeCaptureUrl(u) != urlKey) continue;
                    if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase)) continue;

                    _recentCaptureUrls[urlKey] = DateTime.UtcNow;
                    ShowSessionForItem(item);
                    return;
                }

                _pendingSessionUrls.Add(urlKey);
                _recentCaptureUrls[urlKey] = DateTime.UtcNow;
            }

            try
            {
                // Ağ beklemeden hemen göster — isim/boyut arka planda netleşir
                string quickName = BuildQuickFileName(url, incomingFilename, mimeHint);
                string categoryId = ResolveCategoryForNewFile(quickName);
                string defaultFolder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                if (string.IsNullOrWhiteSpace(defaultFolder))
                    defaultFolder = _defaultFolder;

                var session = new DownloadSessionWindow(this, url, quickName, defaultFolder, "—")
                {
                    Owner = null,
                    Topmost = false,
                    ShowInTaskbar = true
                };
                session.Closed += (_, _) =>
                {
                    lock (_captureGate)
                        _pendingSessionUrls.Remove(urlKey);
                };
                session.Show();
                session.Activate();
                session.BringToFrontSoft();

                _ = EnrichSessionMetaAsync(session, url, quickName, mimeHint);
            }
            catch
            {
                lock (_captureGate)
                    _pendingSessionUrls.Remove(urlKey);
                throw;
            }
        }

        private static string BuildQuickFileName(string url, string incomingFilename, string? mimeHint)
        {
            string decodedSuggested = FileNameHelper.DecodeDisplayName(incomingFilename);
            string? fromUrl = FileNameHelper.TryFileNameFromUrl(url);
            string chosen = PickBestFileName(decodedSuggested, fromUrl) ?? "download";
            chosen = FileNameHelper.EnsureExtension(chosen, mimeHint);

            if (string.IsNullOrEmpty(Path.GetExtension(chosen)))
            {
                string ext = FileNameHelper.GuessExtensionFromContentType(mimeHint);
                if (FileNameHelper.IsPlaceholderName(chosen))
                    chosen = "download" + (string.IsNullOrEmpty(ext) ? ".bin" : ext);
                else if (!string.IsNullOrEmpty(ext))
                    chosen += ext;
            }

            return chosen;
        }

        private async Task EnrichSessionMetaAsync(
            DownloadSessionWindow session, string url, string currentName, string? mimeHint)
        {
            try
            {
                var (resolvedName, sizeLabel) = await ResolveMetaAsync(url, currentName, mimeHint)
                    .ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!session.IsVisible || session.HasStarted) return;

                    session.ApplyResolvedMeta(resolvedName, sizeLabel);

                    string categoryId = ResolveCategoryForNewFile(resolvedName);
                    string folder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                    if (!string.IsNullOrWhiteSpace(folder))
                        session.ApplyDefaultFolderIfIdle(folder);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnrichSessionMeta: {ex.Message}");
            }
        }

        private async Task<(string FileName, string SizeLabel)> ResolveMetaAsync(
            string url, string suggestedName, string? mimeHint)
        {
            string? contentType = null;
            string? fromHeader = null;
            long? contentLength = null;
            string decodedSuggested = FileNameHelper.DecodeDisplayName(suggestedName);

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                HttpResponseMessage? response = null;
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                }
                catch { /* bazı sunucular HEAD kabul etmez */ }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    response?.Dispose();
                    using var get = new HttpRequestMessage(HttpMethod.Get, url);
                    response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }

                using (response)
                {
                    contentType = response.Content.Headers.ContentType?.MediaType;
                    fromHeader = FileNameHelper.ExtractFromContentDisposition(response.Content.Headers);
                    contentLength = response.Content.Headers.ContentLength;
                }
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(contentType) && !string.IsNullOrWhiteSpace(mimeHint))
                contentType = mimeHint;

            string? fromUrl = FileNameHelper.TryFileNameFromUrl(url);
            string chosen = PickBestFileName(fromHeader, decodedSuggested, fromUrl) ?? "download";
            chosen = FileNameHelper.EnsureExtension(chosen, contentType);

            if (string.IsNullOrEmpty(Path.GetExtension(chosen)))
            {
                string ext = FileNameHelper.GuessExtensionFromContentType(contentType);
                if (FileNameHelper.IsPlaceholderName(chosen))
                    chosen = "download" + (string.IsNullOrEmpty(ext) ? ".bin" : ext);
                else if (!string.IsNullOrEmpty(ext))
                    chosen += ext;
            }

            string sizeLabel = contentLength is > 0 ? FormatFileSize(contentLength.Value) : "—";
            return (chosen, sizeLabel);
        }

        private void RebindEngineAfterPathChange(DownloadItem item, string newPath)
        {
            if (!_engines.TryGetValue(item, out var oldEngine))
                return;

            if (oldEngine.IsDownloading && !oldEngine.IsPaused)
                return;

            string url = item.Url;
            if (string.IsNullOrWhiteSpace(url))
                _itemUrls.TryGetValue(item, out url!);
            if (string.IsNullOrWhiteSpace(url))
                return;

            var engine = new DownloadEngine(url, newPath, threadCount: 8);
            _engines[item] = engine;
            WireEngineEvents(item, engine);

            if (_sessionWindows.TryGetValue(item, out var session))
                session.RebindEngine(engine);
        }

        private static string NormalizeCaptureUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                var uri = new Uri(url.Trim());
                // Query'deki geçici token'ları koru ama trailing slash / fragment temizle
                string path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                return path + (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query);
            }
            catch
            {
                return url.Trim();
            }
        }

        public void RegisterSessionWindow(DownloadItem item, DownloadSessionWindow window)
        {
            _sessionWindows[item] = window;
        }

        public void UnregisterSessionWindow(DownloadItem item, DownloadSessionWindow window)
        {
            if (_sessionWindows.TryGetValue(item, out var current) && ReferenceEquals(current, window))
                _sessionWindows.Remove(item);
        }

        public void ShowSessionForItem(DownloadItem item)
        {
            if (_sessionWindows.TryGetValue(item, out var existing))
            {
                if (!existing.IsVisible) existing.Show();
                existing.Activate();
                existing.BringToFrontSoft();
                return;
            }

            _itemUrls.TryGetValue(item, out string? url);
            url ??= "";
            _engines.TryGetValue(item, out var engine);

            var session = new DownloadSessionWindow(this, item, engine, url)
            {
                Owner = null,
                Topmost = false,
                ShowInTaskbar = true
            };
            _sessionWindows[item] = session;
            session.Show();
            session.Activate();
            session.BringToFrontSoft();
        }

        private async Task<string> ProbeContentLengthLabelAsync(string url)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                long? len = response.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > 0)
                    return FormatFileSize(len.Value);
            }
            catch { /* ignore */ }
            return "—";
        }

        public sealed class DownloadRun
        {
            public required DownloadItem Item { get; init; }
            public required DownloadEngine Engine { get; init; }
        }

        public DownloadRun BeginDownloadFromSession(string url, string fileName, string folder)
        {
            // Uzantıya göre native kategori klasörü (rar→Arşivler, mp3→Sesler, …)
            string categoryId = ResolveCategoryForNewFile(fileName);
            string autoFolder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);

            string saveFolder = autoFolder;
            try
            {
                string normFolder = Path.GetFullPath(folder).TrimEnd('\\', '/');
                string normDownloads = Path.GetFullPath(_defaultFolder).TrimEnd('\\', '/');
                // Kullanıcı Downloads dışında özel yol seçtiyse ona uy
                if (!normFolder.StartsWith(normDownloads, StringComparison.OrdinalIgnoreCase))
                    saveFolder = folder;
            }
            catch { saveFolder = autoFolder; }

            if (!Directory.Exists(saveFolder))
                Directory.CreateDirectory(saveFolder);

            string savePath = GetUniqueFilePath(saveFolder, fileName);
            string finalFileName = Path.GetFileName(savePath);

            var item = new DownloadItem
            {
                FileName = finalFileName,
                FilePath = savePath,
                FileType = FileNameHelper.FormatTypeLabel(finalFileName),
                DateAdded = DateTime.Now,
                Status = "İndiriliyor",
                FileIcon = IconHelper.GetIconForExtension(finalFileName),
                CategoryId = categoryId,
                Url = url
            };

            DownloadList.Insert(0, item);
            _itemUrls[item] = url;
            QueueHistorySave();

            int activeCount = Math.Max(1, _engines.Count + 1);
            int threadCount = activeCount >= 3 ? 4 : 8;
            var engine = new DownloadEngine(url, savePath, threadCount: threadCount);
            _engines[item] = engine;

            WireEngineEvents(item, engine);
            UpdateTransportButtons();

            _ = RunEngineAsync(item, engine);
            return new DownloadRun { Item = item, Engine = engine };
        }

        private string ResolveCategoryForNewFile(string fileName)
        {
            string ext = Path.GetExtension(fileName)?.TrimStart('.') ?? "";

            // 1) Uzantı kuralı — native kategoriler (rar → Arşivler, mp3 → Sesler, …)
            if (!string.IsNullOrEmpty(ext))
            {
                CategoryItem? best = null;
                foreach (var c in CategoryStore.AllFlat(Categories))
                {
                    if (c.Id == "All" || c.Extensions == null || !c.Extensions.Contains(ext))
                        continue;
                    if (best == null || c.Depth > best.Depth)
                        best = c;
                }
                if (best != null)
                    return best.Id;
            }

            // 2) Uzantı eşleşmezse: seçili özel (custom) kategori
            if (_currentCategory != "All")
            {
                var selected = CategoryStore.FindById(Categories, _currentCategory);
                if (selected != null && !selected.IsBuiltin)
                    return selected.Id;
            }

            return "All";
        }

        public void PauseFromSession(DownloadItem item, DownloadEngine engine)
        {
            if (engine.IsDownloading && !engine.IsPaused)
            {
                engine.Pause();
                item.Status = "Duraklatıldı";
                item.IsDownloading = false;
                item.CurrentSpeed = "";
                item.StatusText = "";
                UpdateTransportButtons();
                if (_sessionWindows.TryGetValue(item, out var session))
                    session.RefreshFromHost();
            }
            else if (engine.IsPaused)
            {
                item.Status = "Duraklatıldı";
                item.IsDownloading = false;
                if (_sessionWindows.TryGetValue(item, out var session))
                    session.RefreshFromHost();
            }
        }

        public async Task ResumeFromSessionAsync(DownloadItem item, DownloadEngine engine)
        {
            item.Status = "İndiriliyor";
            item.IsDownloading = true;
            if (_sessionWindows.TryGetValue(item, out var session))
                session.RefreshFromHost();
            await RunEngineAsync(item, engine);
        }

        public void CancelFromSession(DownloadItem item, DownloadEngine engine)
        {
            item.Status = "İptal Edildi";
            item.StatusText = "";
            item.CurrentSpeed = "";
            item.IsDownloading = false;
            item.ProgressValue = 0;
            engine.Cancel();
            UpdateTransportButtons();
        }

        public void DeleteItemFromSession(DownloadItem item)
        {
            if (_engines.TryGetValue(item, out var engine))
            {
                engine.Cancel();
                _engines.Remove(item);
            }

            try
            {
                DownloadPathHelper.DeleteFileAndState(item.FilePath);
            }
            catch { /* ignore */ }

            DownloadList.Remove(item);
            _itemUrls.Remove(item);
            _sessionWindows.Remove(item);
            UpdateTransportButtons();
            QueueHistorySave();
        }

        private void WireEngineEvents(DownloadItem item, DownloadEngine engine)
        {
            engine.TotalSizeKnown += (totalBytes) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    () => item.FileSize = FormatFileSize(totalBytes));
            };

            engine.ProgressChanged += (progress) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (engine.IsCancelled) return;
                    if (!engine.IsDownloading) return;
                    if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase)) return;
                    if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase)) return;
                    if (item.Status.Contains("Duraklat", StringComparison.OrdinalIgnoreCase)) return;

                    item.ProgressValue = progress;
                    if (progress >= 99.9)
                    {
                        item.StatusText = "İndiriliyor %100";
                        return;
                    }

                    item.StatusText = $"İndiriliyor %{progress:F1}";
                    if (!item.IsDownloading)
                        item.Status = "İndiriliyor";
                });
            };

            engine.StatusChanged += (status) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
                {
                    if (status.Contains("İptal", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "İptal Edildi";
                        item.StatusText = "";
                        item.CurrentSpeed = "";
                        item.IsDownloading = false;
                        item.ProgressValue = 0;
                        UpdateTransportButtons();
                        QueueHistorySave();
                    }
                    else if (status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "Tamamlandı";
                        item.StatusText = "";
                        item.CurrentSpeed = "";
                        item.IsDownloading = false;
                        item.ProgressValue = 100;
                        UpdateTransportButtons();
                        QueueHistorySave();
                        RefreshCategoryCounts();
                        // Tamamlandi ekranini goster
                        ShowSessionForItem(item);
                    }
                    else if (status.Contains('%'))
                    {
                        if (!engine.IsCancelled && engine.IsDownloading)
                            item.StatusText = status;
                    }
                    else if (status.Contains("Duraklat", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "Duraklatıldı";
                        item.StatusText = "";
                        item.IsDownloading = false;
                        UpdateTransportButtons();
                        QueueHistorySave();
                    }
                    else if (!status.StartsWith("İndiriliyor", StringComparison.OrdinalIgnoreCase)
                             && !status.Contains("kanal", StringComparison.OrdinalIgnoreCase)
                             && !status.Contains("Dosya bilgileri", StringComparison.OrdinalIgnoreCase)
                             && !status.Contains("Tek kanal", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = status;
                        UpdateTransportButtons();
                    }
                });
            };

            engine.SpeedAndTimeChanged += (speed, time) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (engine.IsCancelled) return;
                    if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase)) return;
                    item.CurrentSpeed = speed;
                });
            };
        }

        private async Task RunEngineAsync(DownloadItem item, DownloadEngine engine)
        {
            try
            {
                await engine.StartOrResumeDownloadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Duraklatma kaynakli hatalari kullaniciya gosterme
                if (engine.IsPaused || engine.IsCancelled)
                    return;

                item.Status = "Hata";
                item.StatusText = ex.Message;
                // Engine kalir — Devam Et ile yeniden denenebilir
            }
            finally
            {
                if (engine.IsPaused && !engine.IsCancelled)
                {
                    item.Status = "Duraklatıldı";
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                }
                else if (engine.IsCancelled)
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                    item.Status = "İptal Edildi";
                    item.ProgressValue = 0;
                    _engines.Remove(item);
                }
                else if (item.Status.Contains("Hata", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    // engine tutulur
                }
                else if (engine.CompletedSuccessfully)
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                    item.Status = "Tamamlandı";
                    item.ProgressValue = 100;
                    if (File.Exists(item.FilePath))
                        item.FileSize = FormatFileSize(new FileInfo(item.FilePath).Length);
                    _engines.Remove(item);
                }
                else if (engine.IsPaused)
                {
                    item.Status = "Duraklatıldı";
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                }
                else
                {
                    // Belirsiz bitis — tamamlandi sayma, duraklatilmis gibi tut
                    item.Status = "Duraklatıldı";
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                }

                UpdateTransportButtons();
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }

        private List<(DownloadItem Item, DownloadEngine Engine)> GetSelectedEngines()
        {
            return DgDownloads.SelectedItems
                .OfType<DownloadItem>()
                .Where(i => _engines.ContainsKey(i))
                .Select(i => (i, _engines[i]))
                .ToList();
        }

        private void UpdateTransportButtons()
        {
            // Ust transport cubugu kaldirildi; satir aksiyonlari kullanilir
        }

        private void DgDownloads_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTransportButtons();
        }

        private void DgDownloads_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DgDownloads.Focus();
                DgDownloads.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                DeleteSelectedItems();
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Arama / TextBox odakliyken Ctrl+A metni secmeli — grid'e gitmesin
            if (Keyboard.FocusedElement is TextBoxBase)
                return;

            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DgDownloads.Focus();
                DgDownloads.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                     && DgDownloads.SelectedItems.Count > 0)
            {
                DeleteSelectedItems();
                e.Handled = true;
            }
        }

        private async void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedEngines();
            if (selected.Count == 0) return;

            bool anyDownloading = selected.Any(x => x.Engine.IsDownloading && !x.Engine.IsPaused);

            if (anyDownloading)
            {
                foreach (var (item, engine) in selected)
                {
                    if (engine.IsDownloading && !engine.IsPaused)
                    {
                        engine.Pause();
                        item.Status = "Duraklatıldı";
                        item.IsDownloading = false;
                        item.CurrentSpeed = "";
                        item.StatusText = "";
                    }
                }
                UpdateTransportButtons();
                return;
            }

            // Hepsi duraklatilmis veya devam edilebilir — secilenleri devam ettir
            foreach (var (item, engine) in selected.Where(x => x.Engine.IsPaused).ToList())
            {
                item.Status = "İndiriliyor";
                item.IsDownloading = true;
                _ = ResumeDownloadAsync(item, engine);
            }

            UpdateTransportButtons();
        }

        private async Task ResumeDownloadAsync(DownloadItem target, DownloadEngine engine)
        {
            try
            {
                await engine.StartOrResumeDownloadAsync();
            }
            catch (Exception ex)
            {
                if (engine.IsPaused || engine.IsCancelled)
                    return;
                target.Status = "Hata";
                target.StatusText = ex.Message;
            }
            finally
            {
                if (engine.IsPaused && !engine.IsCancelled)
                {
                    target.Status = "Duraklatıldı";
                    target.IsDownloading = false;
                }
                else if (engine.IsCancelled)
                {
                    target.Status = "İptal Edildi";
                    target.ProgressValue = 0;
                    target.IsDownloading = false;
                    target.StatusText = "";
                    _engines.Remove(target);
                }
                else if (target.Status.Contains("Hata", StringComparison.OrdinalIgnoreCase))
                {
                    target.IsDownloading = false;
                }
                else if (engine.CompletedSuccessfully)
                {
                    target.Status = "Tamamlandı";
                    target.IsDownloading = false;
                    target.ProgressValue = 100;
                    if (File.Exists(target.FilePath))
                        target.FileSize = FormatFileSize(new FileInfo(target.FilePath).Length);
                    _engines.Remove(target);
                }
                else
                {
                    target.Status = "Duraklatıldı";
                    target.IsDownloading = false;
                }

                UpdateTransportButtons();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedEngines();
            if (selected.Count == 0) return;

            foreach (var (target, engine) in selected)
            {
                target.Status = "İptal Edildi";
                target.StatusText = "";
                target.CurrentSpeed = "";
                target.IsDownloading = false;
                target.ProgressValue = 0;
                engine.Cancel();
            }

            UpdateTransportButtons();
        }

        private void DgDownloads_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DgDownloads.SelectedItem is not DownloadItem selectedItem)
                return;

            // Indirme / duraklatma / yeni tamamlanan: oturum penceresini ac
            if (selectedItem.IsDownloading
                || selectedItem.Status.Contains("Duraklat", StringComparison.OrdinalIgnoreCase)
                || selectedItem.Status.Contains("İndiriliyor", StringComparison.OrdinalIgnoreCase)
                || _engines.ContainsKey(selectedItem)
                || _sessionWindows.ContainsKey(selectedItem))
            {
                ShowSessionForItem(selectedItem);
                return;
            }

            if (File.Exists(selectedItem.FilePath))
            {
                Process.Start(new ProcessStartInfo(selectedItem.FilePath) { UseShellExecute = true });
            }
            else
            {
                InfoDialog.Show(this, "Hata", "Dosya belirtilen konumda bulunamadı!");
            }
        }

        private void Marquee_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            if (FindParent<ScrollBar>(source) != null) return;
            if (FindParent<DataGridColumnHeader>(source) != null) return;
            if (FindParent<Thumb>(source) != null) return;
            if (FindParent<Button>(source) != null) return;
            if (FindParent<CheckBox>(source) != null) return;

            var row = FindParent<DataGridRow>(source);
            // Satirda marquee KAPALI — dosya surukleme (kategoriye atma) calissin
            if (row != null)
            {
                _marqueeArmed = false;
                _marqueeActive = false;
                _dragStartPoint = e.GetPosition(null);
                return;
            }

            _marqueeStart = e.GetPosition(DownloadListHost);
            _marqueeArmed = true;
            _marqueeActive = false;
            _marqueeCtrlBase = null;
            _dragStartPoint = e.GetPosition(null);

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!ctrl)
            {
                DgDownloads.UnselectAll();
                DgDownloads.CurrentCell = new DataGridCellInfo();
            }
        }

        private void Marquee_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_marqueeArmed || e.LeftButton != MouseButtonState.Pressed)
                return;

            // Alt basılıysa dosya sürükleme için marquee'yi bırak
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                _marqueeArmed = false;
                return;
            }

            Point current = e.GetPosition(DownloadListHost);
            double dx = Math.Abs(current.X - _marqueeStart.X);
            double dy = Math.Abs(current.Y - _marqueeStart.Y);

            if (!_marqueeActive)
            {
                if (dx < SystemParameters.MinimumHorizontalDragDistance &&
                    dy < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _marqueeActive = true;
                DownloadListHost.CaptureMouse();

                if (_marqueeCtrlBase == null)
                    DgDownloads.SelectedItems.Clear();

                SelectionRect.Visibility = Visibility.Visible;
            }

            UpdateMarqueeRectangle(_marqueeStart, current);
            SelectItemsInsideMarquee();
            e.Handled = true;
        }

        private void Marquee_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndMarquee();
        }

        private void Marquee_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_marqueeArmed && !_marqueeActive) return;

            _marqueeArmed = false;
            _marqueeActive = false;
            _marqueeCtrlBase = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;
        }

        private void EndMarquee()
        {
            _marqueeArmed = false;
            _marqueeActive = false;
            _marqueeCtrlBase = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            SelectionRect.Width = 0;
            SelectionRect.Height = 0;

            if (DownloadListHost.IsMouseCaptured)
                DownloadListHost.ReleaseMouseCapture();
        }

        private void UpdateMarqueeRectangle(Point start, Point end)
        {
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double w = Math.Abs(end.X - start.X);
            double h = Math.Abs(end.Y - start.Y);

            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = w;
            SelectionRect.Height = h;
        }

        private void SelectItemsInsideMarquee()
        {
            Rect marquee = new Rect(
                Canvas.GetLeft(SelectionRect),
                Canvas.GetTop(SelectionRect),
                SelectionRect.Width,
                SelectionRect.Height);

            var hitItems = new List<DownloadItem>();

            for (int i = 0; i < DgDownloads.Items.Count; i++)
            {
                if (DgDownloads.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row)
                    continue;

                if (!row.IsVisible || row.ActualHeight <= 0)
                    continue;

                GeneralTransform transform = row.TransformToVisual(DownloadListHost);
                Rect rowBounds = transform.TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));

                if (marquee.IntersectsWith(rowBounds) && row.Item is DownloadItem item)
                    hitItems.Add(item);
            }

            DgDownloads.SelectedItems.Clear();

            if (_marqueeCtrlBase != null)
            {
                foreach (DownloadItem item in _marqueeCtrlBase)
                    DgDownloads.SelectedItems.Add(item);
            }

            foreach (DownloadItem item in hitItems)
            {
                if (!DgDownloads.SelectedItems.Contains(item))
                    DgDownloads.SelectedItems.Add(item);
            }
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row || row.Item is not DownloadItem item) return;

            // Sag tikta satiri sec — menu dogru ogeye baglansin
            if (!row.IsSelected)
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                    && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    DgDownloads.SelectedItems.Clear();
                row.IsSelected = true;
            }

            DgDownloads.CurrentItem = item;
            row.Focus();
            // Handled etme — ContextMenu acilsin
        }

        private void DataGridRow_MouseMove(object sender, MouseEventArgs e)
        {
            if (_marqueeActive) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point mousePos = e.GetPosition(null);
            Vector diff = _dragStartPoint - mousePos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is DataGridRow row && row.Item is DownloadItem item)
                {
                    if (!row.IsSelected)
                    {
                        DgDownloads.SelectedItems.Clear();
                        row.IsSelected = true;
                    }

                    var selected = DgDownloads.SelectedItems.Cast<DownloadItem>().ToList();
                    if (selected.Count == 0) selected.Add(item);

                    string[] existingFiles = selected
                        .Select(i => i.FilePath)
                        .Where(File.Exists)
                        .ToArray();

                    var dataObj = new DataObject();
                    if (existingFiles.Length > 0)
                        dataObj.SetData(DataFormats.FileDrop, existingFiles);
                    dataObj.SetData("DownloadItems", selected);
                    dataObj.SetData("DownloadItem", selected[0]);

                    string label = selected.Count == 1
                        ? selected[0].FileName
                        : $"{selected.Count} dosya";
                    TxtFileDragGhost.Text = label;
                    ImgFileDragGhost.Source = selected[0].FileIcon
                        ?? IconHelper.GetIconForExtension(selected[0].FileName);
                    FileDragPopup.IsOpen = true;
                    row.Opacity = 0.45;

                    _isFileDragging = true;
                    try
                    {
                        DragDrop.DoDragDrop(row, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                    finally
                    {
                        _isFileDragging = false;
                        FileDragPopup.IsOpen = false;
                        ImgFileDragGhost.Source = null;
                        row.Opacity = 1;
                        ClearCatDropVisuals();
                    }
                }
            }
        }

        private void DgDownloads_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("DownloadItem"))
            {
                var draggedItem = e.Data.GetData("DownloadItem") as DownloadItem;
                if (draggedItem == null) return;

                Point dropPosition = e.GetPosition(DgDownloads);
                HitTestResult result = VisualTreeHelper.HitTest(DgDownloads, dropPosition);

                if (result != null)
                {
                    DataGridRow? targetRow = FindParent<DataGridRow>(result.VisualHit);
                    if (targetRow != null && targetRow.Item is DownloadItem targetItem)
                    {
                        int oldIndex = DownloadList.IndexOf(draggedItem);
                        int newIndex = DownloadList.IndexOf(targetItem);

                        if (oldIndex != -1 && newIndex != -1 && oldIndex != newIndex)
                        {
                            DownloadList.Move(oldIndex, newIndex);
                        }
                    }
                }
            }
        }

        private T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _captureServer?.Stop();
        }
    }
}
