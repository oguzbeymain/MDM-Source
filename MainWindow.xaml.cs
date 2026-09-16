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

namespace MDM
{
    public partial class MainWindow : Window
    {
        private BrowserCaptureServer? _captureServer;
        private readonly Dictionary<DownloadItem, ITransferBackend> _engines = new();
        private readonly Dictionary<DownloadItem, DownloadSessionWindow> _sessionWindows = new();
        private readonly HashSet<string> _pendingSessionUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _recentCaptureUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _captureGate = new();
        private static readonly TimeSpan CaptureDebounce = TimeSpan.FromMilliseconds(900);
        private const double ActionColumnWidth = 100;
        private string _listDensity = "Medium";
        private string _listSort = "Date";
        private int _listIconPx = 22;
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
        private List<DownloadItem>? _fileDragItems;
        private List<DownloadItem>? _dragSelectionSnapshot;
        private bool _syncingSelection;
        private DispatcherTimer? _resizeLayoutTimer;
        private double _pendingLayoutWidth;
        private DispatcherTimer? _copyToastTimer;
        private bool _historySaveQueued;
        private Point? _titleDragStart;
        private bool _titleDragRestoring;
        private TrayIconService? _tray;
        private bool _sidebarUserSized;
        private bool? _dateColumnShort;
        private bool _sidebarInputActive;
        private bool _exitRequested;
        private bool _trayTipShown;
        private DateTime _captureQuietUntilUtc = DateTime.MinValue;

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
            RebuildVisibleCategories();
            LstCategories.ItemsSource = VisibleCategories;

            _downloadView = CollectionViewSource.GetDefaultView(DownloadList);
            _downloadView.Filter = FilterByCategory;
            if (_downloadView is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = true;
            }
            DgDownloads.ItemsSource = _downloadView;
            DgDownloads.GiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.PreviewGiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.LayoutUpdated += DgDownloads_LayoutUpdated;

            LoadDownloadHistory();
            RestorePendingDownloads();
            ApplyListDensity(appSettings.ListDensity);
            ApplyListSort(appSettings.ListSort);
            SyncListViewMenuChecks();
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

            SettingsPanel.Saved += OnSettingsSaved;
            SettingsPanel.Cancelled += TryCloseSettingsOverlay;
            SettingsPanel.UpdateApplying += OnSettingsUpdateApplying;
            Loc.Changed += OnLanguageChanged;
            ApplyLocalizedTexts();
            RulesPanel.Saved += OnRulesSaved;
            RulesPanel.Cancelled += CloseRulesOverlay;
            NewUrlPanel.Accepted += OnNewUrlAccepted;
            NewUrlPanel.Cancelled += CloseNewUrlOverlay;
            PromptPanel.Accepted += OnPromptAccepted;
            PromptPanel.Cancelled += ClosePromptOverlay;
            CategoryEditPanel.Accepted += OnCategoryEditAccepted;
            CategoryEditPanel.Cancelled += CloseCategoryEditOverlay;
        }

        private void InitTray()
        {
            try
            {
                _tray = new TrayIconService();
                _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
                _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitFromTray);
                _tray.CheckUpdateRequested += () => Dispatcher.BeginInvoke(CheckUpdatesFromTray);
                _tray.SettingsRequested += () => Dispatcher.BeginInvoke(OpenSettingsFromTray);
                _tray.RecentFilesProvider = GetTrayRecentFiles;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray init failed: {ex.Message}");
            }
        }

        public void ShowDownloadCompleteTip(string fileName)
        {
            if (!AppSettingsStore.Load().NotifyOnComplete)
                return;
            try
            {
                _tray?.ShowBalloon(Loc.T("main.download_complete", "İndirme tamamlandı"), fileName);
            }
            catch { /* ignore */ }
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
                    if (AppSettingsStore.Load().NotifyOnTrayMinimize)
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

        private bool _trayUpdateBusy;

        private async void CheckUpdatesFromTray()
        {
            if (_trayUpdateBusy) return;
            _trayUpdateBusy = true;
            try
            {
                ShowFromTray();
                var result = await UpdateService.CheckAndApplyAsync(null, CancellationToken.None);
                if (result.Applying)
                {
                    ExitForUpdate();
                    return;
                }

                if (result.HadError)
                    InfoDialog.Show(this, Loc.T("title.update", "Güncelleme"), Loc.T("msg.update.check_failed", "Denetim başarısız."), result.Message);
                else if (result.IsUpToDate)
                    InfoDialog.Show(this, Loc.T("title.update", "Güncelleme"), Loc.T("msg.update.up_to_date", "Güncelsiniz."),
                        string.Format(Loc.T("msg.update.installed_version", "Yüklü sürüm: v{0}"), UpdateService.CurrentVersionText));
                else if (!string.IsNullOrWhiteSpace(result.Message))
                    InfoDialog.Show(this, Loc.T("title.update", "Güncelleme"), result.Message);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, Loc.T("title.update", "Güncelleme"), Loc.T("msg.update.check_failed", "Denetim başarısız."), ex.Message);
            }
            finally
            {
                _trayUpdateBusy = false;
            }
        }

        private void OpenSettingsFromTray()
        {
            ShowFromTray();
            OpenSettingsOverlay();
        }

        private IReadOnlyList<TrayRecentFile> GetTrayRecentFiles()
        {
            return DownloadList
                .Where(i => i.IsCompleted && !string.IsNullOrWhiteSpace(i.FilePath) && File.Exists(i.FilePath))
                .OrderByDescending(i => i.DateAdded)
                .Take(6)
                .Select(i => new TrayRecentFile
                {
                    DisplayName = string.IsNullOrWhiteSpace(i.FileName) ? Path.GetFileName(i.FilePath) : i.FileName,
                    FilePath = i.FilePath,
                    Icon = i.FileIcon ?? IconHelper.GetIconForExtension(i.FileName ?? i.FilePath, 16)
                })
                .ToList();
        }

        /// <summary>Updater yeni sürüm kurmadan önce eski örneği kapatır.</summary>
        public void ExitForUpdate()
        {
            _exitRequested = true;
            try
            {
                foreach (var kv in _engines.ToList())
                {
                    try
                    {
                        if (kv.Value.IsDownloading && !kv.Value.IsPaused)
                            kv.Value.Pause();
                    }
                    catch { /* ignore */ }
                }
                PersistDownloadHistory();
            }
            catch { /* ignore */ }

            try { _captureServer?.Stop(); } catch { /* ignore */ }
            try { TorrentEngineHost.Shutdown(); } catch { /* ignore */ }
            try { _tray?.Dispose(); } catch { /* ignore */ }
            _tray = null;
            Application.Current.Shutdown();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveLayout(ActualWidth);
            UpdateListPanelClip();
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    BeginBackgroundCaptureQuiet(45);
                    AutoResumeIncompleteDownloads();
                    _ = Task.Run(() =>
                    {
                        try { CategoryStore.EnsureDiskFolders(Categories, _defaultFolder); }
                        catch (Exception ex) { Debug.WriteLine($"EnsureDiskFolders: {ex.Message}"); }
                    });
                    // YouTube kalite listesi için yt-dlp'yi arka planda hazırla (soğuk açılış)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await YtDlpHelper.EnsureAvailableAsync(TimeSpan.FromSeconds(90));
                            await YtDlpHelper.EnsureFfmpegAsync(TimeSpan.FromMinutes(4));
                            await YtDlpHelper.EnsureDenoAsync(TimeSpan.FromMinutes(4));
                        }
                        catch (Exception ex) { Debug.WriteLine($"yt-dlp/ffmpeg/deno ensure: {ex.Message}"); }
                    });
                    try
                    {
                        PluginRegistry.LoadFromFolder(Path.Combine(AppSettingsStore.StoreDir, "plugins"));
                    }
                    catch { /* ignore */ }
                    _ = DrainIpcLoopAsync();
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
            try { TorrentEngineHost.Shutdown(); } catch { /* ignore */ }
            try { _tray?.Dispose(); } catch { /* ignore */ }
            _tray = null;
        }

        public void ApplyBackgroundStart()
        {
            // App.OnStartup HideToTray çağırır; geriye dönük uyumluluk
            HideToTray();
        }

        /// <summary>
        /// Windows açılışında tarayıcı eski indirmeleri yeniden yakalar.
        /// Bu süre boyunca eklentiden gelen yakalamalar oturum penceresi açmaz.
        /// </summary>
        public void BeginBackgroundCaptureQuiet(int seconds = 25)
        {
            _captureQuietUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        }

        // --- Uygulama içi karartmalı modal ---
        private DispatcherFrame? _modalFrame;
        private bool _modalResult;

        public bool ShowModalConfirm(string title, string message, string detail,
            string confirmText, string cancelText, bool danger, bool accentCancel = false)
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

            // Devam et (dismiss) — Başlat gibi turuncu
            if (accentCancel)
            {
                ModalCancelBtn.Foreground = Brushes.White;
                ModalCancelBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            }
            else
            {
                ModalCancelBtn.ClearValue(Control.BackgroundProperty);
                ModalCancelBtn.ClearValue(Control.ForegroundProperty);
            }

            bool modalLight = ThemeService.IsLight;
            if (SettingsOverlay.Visibility == Visibility.Visible)
                modalLight = SettingsPanel.ThemeLight?.IsChecked == true;
            ThemeService.ApplyModalSurface(this, modalLight);

            Panel.SetZIndex(ModalOverlay, 1200);
            ModalOverlay.Visibility = Visibility.Visible;
            ModalOverlay.Focusable = true;
            ModalOverlay.FocusVisualStyle = null;
            ModalOverlay.UpdateLayout();
            ModalOverlay.BringIntoView();
            ModalConfirmBtn.FocusVisualStyle = null;
            Keyboard.Focus(ModalOverlay);

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
            ModalConfirmBtn.Content = DialogTexts.Ok;
            ModalConfirmBtn.Visibility = Visibility.Visible;
            ModalConfirmBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));

            Panel.SetZIndex(ModalOverlay, 1200);
            ModalOverlay.Visibility = Visibility.Visible;
            ModalOverlay.Focusable = true;
            ModalOverlay.FocusVisualStyle = null;
            ModalOverlay.UpdateLayout();
            ModalOverlay.BringIntoView();
            ModalConfirmBtn.FocusVisualStyle = null;
            // Focus kırmızı noktalı kutu çıkmasın — Enter zaten PreviewKeyDown'da
            Keyboard.Focus(ModalOverlay);

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

        private void ModalOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != ModalOverlay) return;
            bool infoOnly = ModalCancelBtn.Visibility != Visibility.Visible;
            CloseModal(infoOnly);
            e.Handled = true;
        }

        // --- Yeni indirme / metin girişi overlay ---
        private DispatcherFrame? _promptFrame;
        private bool _promptAccepted;
        private PromptDialogResult? _lastPromptResult;

        private sealed record PromptDialogResult(string Text, bool AllowExtensionChange);

        private bool ShowNewUrlOverlay(out NewUrlDialog panel)
        {
            panel = NewUrlPanel;
            panel.Reset();
            panel.ApplyThemeSurface(ThemeService.IsLight);
            NewUrlOverlay.Visibility = Visibility.Visible;
            NewUrlOverlay.Focusable = true;
            Keyboard.Focus(NewUrlOverlay);
            _promptAccepted = false;
            _promptFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_promptFrame);
            return _promptAccepted;
        }

        private void OnNewUrlAccepted()
        {
            _promptAccepted = true;
            CloseNewUrlOverlay();
        }

        private void CloseNewUrlOverlay()
        {
            NewUrlOverlay.Visibility = Visibility.Collapsed;
            if (_promptFrame != null)
            {
                _promptFrame.Continue = false;
                _promptFrame = null;
            }
        }

        private void NewUrlOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == NewUrlOverlay)
                CloseNewUrlOverlay();
        }

        private void NewUrlOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseNewUrlOverlay();
                e.Handled = true;
            }
        }

        private bool ShowPromptOverlay(string title, string prompt, string defaultValue = "", bool extensionLockMode = false)
        {
            PromptPanel.Configure(title, prompt, defaultValue, extensionLockMode);
            _lastPromptResult = null;
            _promptAccepted = false;
            PromptOverlay.Visibility = Visibility.Visible;
            PromptOverlay.Focusable = true;
            Keyboard.Focus(PromptPanel);
            _promptFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_promptFrame);
            return _promptAccepted;
        }

        private void OnPromptAccepted()
        {
            _promptAccepted = true;
            _lastPromptResult = new PromptDialogResult(PromptPanel.ResultText, PromptPanel.AllowExtensionChange);
            ClosePromptOverlay();
        }

        private void ClosePromptOverlay()
        {
            PromptOverlay.Visibility = Visibility.Collapsed;
            if (_promptFrame != null)
            {
                _promptFrame.Continue = false;
                _promptFrame = null;
            }
        }

        private void PromptOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == PromptOverlay)
                ClosePromptOverlay();
        }

        private CategoryItem? _categoryEditTarget;

        private bool ShowCategoryEditOverlay(CategoryItem cat)
        {
            _categoryEditTarget = cat;
            _promptAccepted = false;
            CategoryEditPanel.Configure(cat);
            CategoryEditPanel.ApplyThemeSurface(ThemeService.IsLight);
            CategoryEditOverlay.Visibility = Visibility.Visible;
            CategoryEditOverlay.Focusable = true;
            Keyboard.Focus(CategoryEditPanel);
            _promptFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_promptFrame);
            return _promptAccepted;
        }

        private void OnCategoryEditAccepted()
        {
            _promptAccepted = true;
            CloseCategoryEditOverlay();
        }

        private void CloseCategoryEditOverlay()
        {
            CategoryEditOverlay.Visibility = Visibility.Collapsed;
            if (_promptFrame != null)
            {
                _promptFrame.Continue = false;
                _promptFrame = null;
            }
        }

        private void CategoryEditOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == CategoryEditOverlay)
                CloseCategoryEditOverlay();
        }

        private void CategoryEditOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseCategoryEditOverlay();
                e.Handled = true;
            }
        }

        private void PromptOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClosePromptOverlay();
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

        private void CategoryItemContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;

            foreach (var item in menu.Items.OfType<MenuItem>())
                item.Visibility = Visibility.Visible;

            var selected = LstCategories.SelectedItems.OfType<CategoryItem>().ToList();
            if (selected.Count == 0 && LstCategories.SelectedItem is CategoryItem one)
                selected.Add(one);

            bool showDelete = selected.Any(c => !c.IsBuiltin && c.Id != "All");
            SetCategoryMenuItemVisible(menu, "MenuCatDelete", showDelete);

            bool showUnnest = selected.Any(c => !c.IsBuiltin && !string.IsNullOrEmpty(c.ParentId));
            SetCategoryMenuItemVisible(menu, "MenuCatUnnest", showUnnest);
        }

        /// <summary>Başlık yerelleştirildiği için ada göre eşleştirir.</summary>
        private static void SetCategoryMenuItemVisible(ContextMenu menu, string itemName, bool visible)
        {
            if (FindMenuItemByName(menu.Items, itemName) is MenuItem item)
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool IsSelectableCustomCategory(CategoryItem cat)
            => !cat.IsBuiltin && cat.Id != "All";

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
                    var engine = TransferFactory.Create(url, item.FilePath, threadCount: DownloadQueue.HttpChannels());
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

                string url = item.Url;
                if (string.IsNullOrWhiteSpace(url))
                    _itemUrls.TryGetValue(item, out url!);

                string statePath = item.FilePath + ".mdmstate";
                bool torrentJob = UrlClassifier.Classify(url) is TransferKind.Magnet or TransferKind.Torrent;
                if (!File.Exists(statePath) && !torrentJob)
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
                }, AppSettingsStore.Load().RemoteApiLan, AppSettingsStore.Load().RemoteApiToken, new WindowJobHost(this),
                onExtCapture: ext =>
                {
                    Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            await StartExtCaptureDownloadAsync(ext);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ext capture error: {ex.Message}");
                        }
                    }, System.Windows.Threading.DispatcherPriority.Normal);
                },
                onExtScan: scan =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            ShowExtensionScan(scan);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ext scan error: {ex.Message}");
                        }
                    }, System.Windows.Threading.DispatcherPriority.Normal);
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
            new(Color.FromArgb(0xB3, 0xFF, 0x6B, 0x00)); // saydam turuncu

        private void DgDownloads_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_isFileDragging)
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.Arrow);
                e.Handled = true;

                Point screen = GetMouseScreenPoint();
                FileDragPopup.HorizontalOffset = screen.X + 14;
                FileDragPopup.VerticalOffset = screen.Y + 10;
                if (!FileDragPopup.IsOpen)
                    FileDragPopup.IsOpen = true;
                return;
            }

            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.SizeWE);
            e.Handled = true;
            RecolorColumnDragAdorners();
        }

        private sealed class DragRowVisual
        {
            public required DataGridRow Row { get; init; }
            public double Opacity { get; init; }
            public Brush? BorderBrush { get; init; }
            public Thickness BorderThickness { get; init; }
            public Brush? Background { get; init; }
        }

        private void UpdateFileDragGhost(IReadOnlyList<DownloadItem> items)
        {
            if (items.Count == 0) return;

            ApplyFileDragGhostTheme();

            ImageSource? IconFor(DownloadItem i) =>
                i.FileIcon ?? IconHelper.GetIconForExtension(i.FileName, _listIconPx);

            ImgFileDragGhost.Source = IconFor(items[0]);

            if (items.Count == 1)
            {
                ImgFileDragGhost2.Visibility = Visibility.Collapsed;
                ImgFileDragGhost2.Source = null;
                ImgFileDragGhost3.Visibility = Visibility.Collapsed;
                ImgFileDragGhost3.Source = null;
                FileDragCountBadge.Visibility = Visibility.Collapsed;
                TxtFileDragGhost.Text = items[0].FileName;
                TxtFileDragSub.Visibility = Visibility.Collapsed;
                return;
            }

            if (items.Count >= 2)
            {
                ImgFileDragGhost2.Visibility = Visibility.Visible;
                ImgFileDragGhost2.Source = IconFor(items[1]);
            }
            else
            {
                ImgFileDragGhost2.Visibility = Visibility.Collapsed;
                ImgFileDragGhost2.Source = null;
            }

            if (items.Count >= 3)
            {
                ImgFileDragGhost3.Visibility = Visibility.Visible;
                ImgFileDragGhost3.Source = IconFor(items[2]);
            }
            else
            {
                ImgFileDragGhost3.Visibility = Visibility.Collapsed;
                ImgFileDragGhost3.Source = null;
            }

            FileDragCountBadge.Visibility = Visibility.Visible;
            TxtFileDragCount.Text = items.Count.ToString();
            TxtFileDragGhost.Text = string.Format(Loc.T("msg.drag.files_selected", "{0} dosya seçildi"), items.Count);
            TxtFileDragSub.Text = items[0].FileName;
            TxtFileDragSub.Visibility = Visibility.Visible;
        }

        private void ApplyFileDragGhostTheme()
        {
            bool light = ThemeService.IsLight;
            var cardBg = light ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A);
            var cardBorder = light ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            var title = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);
            var sub = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x99, 0x99, 0x99);
            var badgeBg = light ? Color.FromArgb(0xEE, 0xE8, 0xE8, 0xEC) : Color.FromArgb(0xCC, 0x2E, 0x2E, 0x2E);
            var badgeBorder = light ? Color.FromArgb(0x88, 0x00, 0x00, 0x00) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            var badgeFg = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);

            FileDragGhostCard.Background = new SolidColorBrush(cardBg);
            FileDragGhostCard.BorderBrush = new SolidColorBrush(cardBorder);
            TxtFileDragGhost.Foreground = new SolidColorBrush(title);
            TxtFileDragSub.Foreground = new SolidColorBrush(sub);
            FileDragCountBadge.Background = new SolidColorBrush(badgeBg);
            FileDragCountBadge.BorderBrush = new SolidColorBrush(badgeBorder);
            TxtFileDragCount.Foreground = new SolidColorBrush(badgeFg);
        }

        private (SolidColorBrush Accent, SolidColorBrush Bg) GetFileDragRowBrushes()
        {
            bool light = ThemeService.IsLight;
            var accent = light
                ? new SolidColorBrush(Color.FromArgb(0xAA, 0x1A, 0x1A, 0x1A))
                : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            var bg = light
                ? new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x00, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            accent.Freeze();
            bg.Freeze();
            return (accent, bg);
        }

        private List<DragRowVisual> ApplyFileDragRowVisuals(DataGrid grid, IReadOnlyList<DownloadItem> items)
        {
            grid.UpdateLayout();
            foreach (var di in items)
                grid.ScrollIntoView(di);
            grid.UpdateLayout();

            var saved = new List<DragRowVisual>();
            var (accent, dragBg) = GetFileDragRowBrushes();

            foreach (var di in items)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(di) is not DataGridRow row)
                    continue;

                saved.Add(new DragRowVisual
                {
                    Row = row,
                    Opacity = row.Opacity,
                    BorderBrush = row.BorderBrush,
                    BorderThickness = row.BorderThickness,
                    Background = row.Background
                });

                row.Opacity = 0.62;
                row.Background = dragBg;
                row.BorderBrush = accent;
                row.BorderThickness = new Thickness(2, 0, 0, 0);
            }

            return saved;
        }

        private static void RestoreFileDragRowVisuals(IEnumerable<DragRowVisual> saved)
        {
            foreach (var v in saved)
            {
                v.Row.Opacity = v.Opacity;
                v.Row.BorderBrush = v.BorderBrush;
                v.Row.BorderThickness = v.BorderThickness;
                v.Row.Background = v.Background;
            }
        }

        private DateTime _lastDragRecolor = DateTime.MinValue;

        private void DgDownloads_LayoutUpdated(object? sender, EventArgs e)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            // Her layout'ta gorsel agaci gezmek donmaya yol acar — throttle
            if ((DateTime.UtcNow - _lastDragRecolor).TotalMilliseconds < 80) return;
            _lastDragRecolor = DateTime.UtcNow;
            RecolorColumnDragAdorners();
        }

        private void RecolorColumnDragAdorners()
        {
            // Tüm DataGrid ağacını gezmek donmaya yol açar — yalnızca adorner katmanı
            var layer = AdornerLayer.GetAdornerLayer(DgDownloads);
            if (layer != null)
                RecolorBlueVisuals(layer);

            if (FindVisualChild<DataGridColumnHeadersPresenter>(DgDownloads) is { } headers)
                RecolorBlueVisuals(headers);
        }

        private static bool LooksLikeSystemBlue(Color c)
        {
            if (c.A < 30) return false;
            // Klasik Aero / Win11 vurgu mavisi ve yarı saydam tonlar
            if (c.B >= 140 && c.B > c.R + 25 && c.B > c.G + 10)
                return true;
            if (c.B >= 100 && c.R <= 120 && c.G <= 170 && c.B >= c.G && c.B > c.R)
                return true;
            // #0078D7 ailesi
            if (c.R <= 80 && c.G >= 90 && c.G <= 160 && c.B >= 180)
                return true;
            return false;
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
                    border.Background = OrangeOf(sb.Color.A);
                if (border.BorderBrush is SolidColorBrush bb && LooksLikeSystemBlue(bb.Color))
                    border.BorderBrush = OrangeOf(bb.Color.A);
                if (border.Background is LinearGradientBrush lgb)
                    border.Background = RemapGradientToOrange(lgb);
                if (border.BorderBrush is LinearGradientBrush lgb2)
                    border.BorderBrush = RemapGradientToOrange(lgb2);
            }

            foreach (var rect in FindVisualChildren<System.Windows.Shapes.Rectangle>(root))
            {
                if (rect.Fill is SolidColorBrush fb && LooksLikeSystemBlue(fb.Color))
                    rect.Fill = OrangeOf(fb.Color.A);
                if (rect.Stroke is SolidColorBrush st && LooksLikeSystemBlue(st.Color))
                    rect.Stroke = OrangeOf(st.Color.A);
                if (rect.Fill is LinearGradientBrush lg)
                    rect.Fill = RemapGradientToOrange(lg);
            }

            foreach (var path in FindVisualChildren<System.Windows.Shapes.Path>(root))
            {
                if (path.Fill is SolidColorBrush pf && LooksLikeSystemBlue(pf.Color))
                    path.Fill = OrangeOf(pf.Color.A);
                if (path.Stroke is SolidColorBrush ps && LooksLikeSystemBlue(ps.Color))
                    path.Stroke = OrangeOf(ps.Color.A);
            }
        }

        private static SolidColorBrush OrangeOf(byte alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb(Math.Max(alpha, (byte)0x66), 0xFF, 0x6B, 0x00));
            b.Freeze();
            return b;
        }

        private static Brush RemapGradientToOrange(LinearGradientBrush src)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = src.StartPoint,
                EndPoint = src.EndPoint,
                Opacity = src.Opacity
            };
            foreach (GradientStop stop in src.GradientStops)
            {
                Color c = LooksLikeSystemBlue(stop.Color)
                    ? Color.FromArgb(stop.Color.A, 0xFF, 0x6B, 0x00)
                    : stop.Color;
                b.GradientStops.Add(new GradientStop(c, stop.Offset));
            }
            b.Freeze();
            return b;
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
                InfoDialog.Show(this, Loc.T("title.category", "Kategori"), Loc.T("msg.category.unnest_select", "Çıkarılacak iç içe özel kategori seçin."));
                return;
            }
            foreach (var cat in targets)
                UnnestCategory(cat);
        }

        private void MenuRenameCategory_Click(object sender, RoutedEventArgs e)
        {
            if (LstCategories.SelectedItem is not CategoryItem cat || cat.Id == "All")
            {
                InfoDialog.Show(this, Loc.T("title.rename", "Yeniden adlandır"), Loc.T("msg.category.rename_select", "Yeniden adlandırmak için bir kategori seçin."));
                return;
            }

            if (!ShowCategoryEditOverlay(cat)) return;

            string name = CategoryEditPanel.CategoryName;
            string icon = CategoryEditPanel.SelectedIcon;

            bool nameChanged = !string.Equals(name, cat.Name, StringComparison.Ordinal);
            bool iconChanged = !string.Equals(icon, cat.Icon, StringComparison.Ordinal);
            if (!nameChanged && !iconChanged) return;

            if (iconChanged)
                cat.Icon = icon;

            if (nameChanged)
                RenameCategory(cat, name);
            else if (iconChanged)
                CategoryStore.Save(Categories);

            RebuildVisibleCategories();
            LstCategories.SelectedItem = cat;
        }

        private void RenameCategory(CategoryItem cat, string newName)
        {
            string oldFolderPath = CategoryStore.GetCategoryFolderPath(Categories, cat.Id, _defaultFolder);
            string? oldCustomPath = cat.CustomFolderPath;

            cat.Name = newName;
            string newFolderPath = CategoryStore.GetCategoryFolderPath(Categories, cat.Id, _defaultFolder);

            if (!string.IsNullOrWhiteSpace(oldCustomPath))
            {
                string? parentDir = Path.GetDirectoryName(oldCustomPath);
                string newCustom = !string.IsNullOrWhiteSpace(parentDir)
                    ? Path.Combine(parentDir, CategoryStore.SanitizeFolderName(newName))
                    : CategoryStore.SanitizeFolderName(newName);
                if (Directory.Exists(oldCustomPath)
                    && !string.Equals(oldCustomPath, newCustom, StringComparison.OrdinalIgnoreCase)
                    && !Directory.Exists(newCustom))
                {
                    try { Directory.Move(oldCustomPath, newCustom); } catch { /* ignore */ }
                }
                cat.CustomFolderPath = newCustom;
                newFolderPath = newCustom;
            }
            else if (Directory.Exists(oldFolderPath)
                     && !string.Equals(oldFolderPath, newFolderPath, StringComparison.OrdinalIgnoreCase)
                     && !Directory.Exists(newFolderPath))
            {
                try { Directory.Move(oldFolderPath, newFolderPath); } catch { /* ignore */ }
            }

            foreach (var item in DownloadList.Where(i => i.CategoryId == cat.Id))
            {
                if (string.IsNullOrWhiteSpace(item.FileName)) continue;
                string newPath = Path.Combine(newFolderPath, item.FileName);
                if (string.IsNullOrWhiteSpace(item.FilePath))
                {
                    item.FilePath = newPath;
                    continue;
                }
                if (string.Equals(Path.GetFullPath(item.FilePath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (File.Exists(item.FilePath))
                    {
                        Directory.CreateDirectory(newFolderPath);
                        DownloadPathHelper.MoveFileAndState(item.FilePath, newPath);
                    }
                    item.FilePath = newPath;
                    RebindEngineAfterPathChange(item, newPath);
                }
                catch { /* ignore */ }
            }

            CategoryStore.Save(Categories);
        }

        private void MenuOpenCategoryFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LstCategories.SelectedItem is not CategoryItem cat || cat.Id == "All")
            {
                InfoDialog.Show(this, Loc.T("title.folder", "Klasör"), Loc.T("msg.category.open_folder_select", "Klasörünü açmak için bir kategori seçin."));
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
                InfoDialog.Show(this, Loc.T("title.folder", "Klasör"), ex.Message);
            }
        }

        private void AddCategoryInteractive(bool asChild)
        {
            CategoryItem? parent = null;
            if (asChild)
            {
                if (LstCategories.SelectedItem is not CategoryItem p || p.Id == "All")
                {
                    InfoDialog.Show(this, Loc.T("title.subcategory", "Alt kategori"), Loc.T("msg.category.child_parent_select", "Alt kategori eklemek için bir üst kategori seçin."));
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
                InfoDialog.Show(this, Loc.T("title.category", "Kategori"), Loc.T("msg.category.rules_select", "Dosya türü ayarlamak için bir kategori seçin."));
                return;
            }

            OpenRulesOverlay(cat);
        }

        private CategoryItem? _rulesCategory;

        private void OpenRulesOverlay(CategoryItem cat)
        {
            _rulesCategory = cat;
            RulesPanel.Load(cat);
            RulesOverlay.Visibility = Visibility.Visible;
            RulesOverlay.Focusable = true;
            Keyboard.Focus(RulesOverlay);
        }

        private void CloseRulesOverlay()
        {
            RulesOverlay.Visibility = Visibility.Collapsed;
            _rulesCategory = null;
        }

        private void OnRulesSaved()
        {
            var cat = _rulesCategory;
            if (cat == null)
            {
                CloseRulesOverlay();
                return;
            }

            CategoryStore.Save(Categories);
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
            CloseRulesOverlay();
        }

        private void RulesOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == RulesOverlay)
            {
                CloseRulesOverlay();
                e.Handled = true;
            }
        }

        private void RulesOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseRulesOverlay();
                e.Handled = true;
            }
        }

        private void LstCategories_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && DeleteKeyEnabled())
            {
                MenuDeleteCategories_Click(sender, e);
                e.Handled = true;
            }
        }

        private static bool DeleteKeyEnabled() => AppSettingsStore.Load().DeleteKeyShortcutsEnabled;

        private void CatMarquee_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
                return; // satirda — normal secim/surukle

            _catMarqueeArmed = true;
            _catMarqueeActive = false;
            _catMarqueeStart = e.GetPosition((IInputElement)sender);
            _catMarqueeCtrlBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? LstCategories.SelectedItems.OfType<CategoryItem>()
                    .Where(IsSelectableCustomCategory)
                    .ToHashSet()
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
                    if (rect.IntersectsWith(itemRect) && IsSelectableCustomCategory(cat))
                        hits.Add(cat);
                }

                _suppressCategorySelection = true;
                try
                {
                    LstCategories.SelectedItems.Clear();
                    foreach (var item in keep.Where(IsSelectableCustomCategory))
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
                InfoDialog.Show(this, Loc.T("title.category", "Kategori"),
                    Loc.T("msg.category.delete_select", "Silmek için özel (eklediğiniz) kategorileri seçin."),
                    Loc.T("msg.category.delete_select_detail", "Ctrl ile çoklu seçim yapabilirsiniz. Varsayılan kategoriler silinemez."));
                return;
            }

            if (!ConfirmDialog.Show(this, DialogTexts.DeleteCategoryTitle,
                    DialogTexts.DeleteCategoryMessage(toDelete.Count),
                    DialogTexts.DeleteCategoryDetail(),
                    confirmText: DialogTexts.DeleteConfirm, cancelText: DialogTexts.DismissAlt, danger: true))
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
            _sidebarInputActive = true;
            LstCategories.Focus();
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
                ClearCatDropVisuals();
                _catDropKind = CatDropKind.None;
                _catDropTarget = null;
                if (LstCategories.ItemContainerGenerator.ContainerFromItem(dragged) is ListBoxItem lbi2)
                    lbi2.Opacity = 1;
                _categoryDragItem = null;
            }
        }

        private void LstCategories_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Sol tık + sağ tık veya iptal: turuncu çizgi takılmasın
            CategoryDragPopup.IsOpen = false;
            ClearCatDropVisuals();
            _catDropKind = CatDropKind.None;
            _catDropTarget = null;
            _categoryDragItem = null;
        }

        private void LstCategories_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(_isFileDragging ? Cursors.Arrow : Cursors.Hand);
            e.Handled = true;

            if (_isFileDragging)
            {
                Point screen = GetMouseScreenPoint();
                FileDragPopup.HorizontalOffset = screen.X + 14;
                FileDragPopup.VerticalOffset = screen.Y + 10;
                if (!FileDragPopup.IsOpen)
                    FileDragPopup.IsOpen = true;
                return;
            }

            // Sadece kategori suruklerken ghost
            if (_categoryDragItem == null)
            {
                CategoryDragPopup.IsOpen = false;
                return;
            }

            Point catScreen = GetMouseScreenPoint();
            CategoryDragPopup.HorizontalOffset = catScreen.X + 12;
            CategoryDragPopup.VerticalOffset = catScreen.Y + 12;
            if (!CategoryDragPopup.IsOpen)
                CategoryDragPopup.IsOpen = true;
        }

        private void LstCategories_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed || e.Action != DragAction.Continue)
            {
                if (e.EscapePressed)
                    e.Action = DragAction.Cancel;
                CategoryDragPopup.IsOpen = false;
                ClearCatDropVisuals();
                _catDropKind = CatDropKind.None;
                _catDropTarget = null;
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

                if (e.Data.GetDataPresent("DownloadItems") || e.Data.GetDataPresent("DownloadItem") || e.Data.GetDataPresent(DataFormats.FileDrop))
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
            if (_fileDragItems is { Count: > 0 })
                return _fileDragItems.ToList();

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
                        case System.Collections.IList rawList:
                            foreach (var item in rawList)
                                if (item is DownloadItem di) Add(di);
                            break;
                    }
                }

                if (data.GetDataPresent("DownloadItem") && data.GetData("DownloadItem") is DownloadItem one)
                    Add(one);

                foreach (var item in GetToolbarTargets())
                    Add(item);

                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
                {
                    foreach (string path in paths)
                    {
                        foreach (var match in DownloadList.Where(d =>
                                     string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
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
            _catDropKind = CatDropKind.None;
            _catDropTarget = null;
        }

        private void LstCategories_Drop(object sender, DragEventArgs e)
        {
            try
            {
                CategoryDragPopup.IsOpen = false;

                // Dosya(lar) kategoriye birakildi
                if (e.Data.GetDataPresent("DownloadItems") || e.Data.GetDataPresent("DownloadItem") || e.Data.GetDataPresent(DataFormats.FileDrop))
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

        private void UnnestCategory(CategoryItem cat)
        {
            if (string.IsNullOrEmpty(cat.ParentId)) return;
            var parent = CategoryStore.FindById(Categories, cat.ParentId!);
            if (parent == null)
            {
                MoveCategoryToRoot(cat);
                return;
            }

            DetachCategory(cat);
            cat.ParentId = parent.ParentId;
            cat.Depth = parent.Depth;

            if (string.IsNullOrEmpty(parent.ParentId))
            {
                int idx = Categories.IndexOf(parent);
                if (idx < 0) idx = Categories.Count - 1;
                Categories.Insert(idx + 1, cat);
            }
            else
            {
                var grandParent = CategoryStore.FindById(Categories, parent.ParentId!);
                if (grandParent != null)
                {
                    int idx = grandParent.Children.IndexOf(parent);
                    if (idx < 0) idx = grandParent.Children.Count - 1;
                    grandParent.Children.Insert(idx + 1, cat);
                    grandParent.NotifyChildrenChanged();
                }
                else
                {
                    int idx = Categories.IndexOf(parent);
                    if (idx < 0) idx = Categories.Count - 1;
                    cat.ParentId = null;
                    Categories.Insert(idx + 1, cat);
                }
            }

            FinishCategoryMove(cat);
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
                    Foreground = TryFindResource("MenuTextBrush") as Brush
                                 ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
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
            factory.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("MenuBgBrush"));
            factory.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("MenuBorderBrush"));
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

            var trigger = new Trigger { SourceName = "bd", Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("MenuHoverBgBrush"), "bd"));
            trigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(trigger);
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
            ScheduleResponsiveLayout();
        }

        private void DgDownloads_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleResponsiveLayout();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded) return;

            if (e.NewSize.Width > 0 && e.NewSize.Width < MinWidth)
                Width = MinWidth;
            if (e.NewSize.Height > 0 && e.NewSize.Height < MinHeight)
                Height = MinHeight;

            ScheduleResponsiveLayout();
        }

        private void ScheduleResponsiveLayout()
        {
            _pendingLayoutWidth = ActualWidth > 0 ? ActualWidth : Width;
            if (_resizeLayoutTimer == null)
            {
                _resizeLayoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
                _resizeLayoutTimer.Tick += (_, _) =>
                {
                    _resizeLayoutTimer!.Stop();
                    ApplyResponsiveLayout(_pendingLayoutWidth);
                    UpdateListPanelClip();
                };
            }
            _resizeLayoutTimer.Stop();
            _resizeLayoutTimer.Start();
        }

        private void UpdateListPanelClip()
        {
            if (ListPanelBorder == null) return;
            const double r = 14;
            double w = Math.Max(0, ListPanelBorder.ActualWidth);
            double h = Math.Max(0, ListPanelBorder.ActualHeight);
            if (w <= 0 || h <= 0) return;
            ListPanelBorder.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }

        private void MainSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _sidebarUserSized = true;
            ApplyResponsiveLayout(ActualWidth);
        }

        private void ApplyResponsiveLayout(double windowWidth)
        {
            if (SidebarCol == null || ContentInner == null || DgDownloads == null) return;

            double windowHeight = ActualHeight > 0 ? ActualHeight : 600;
            bool shortScreen = windowHeight < 800;

            // Kısa ekranlarda (ör. 1360x768) üst boşluğu boğma — title bar nefes alsın
            double outer = shortScreen
                ? (windowWidth < 760 ? 6 : 8)
                : (windowWidth < 760 ? 4 : 8);
            double topOuter = shortScreen ? Math.Max(outer, 8) : outer;

            SidebarPanel.Margin = new Thickness(outer, topOuter, 0, outer);
            ContentPanel.Margin = new Thickness(2, topOuter, outer, outer);

            double pad = shortScreen ? 8 : (windowWidth < 760 ? 6 : 10);
            ContentInner.Margin = new Thickness(pad);
            SidebarInner.Margin = new Thickness(pad * 0.8, pad, pad * 0.8, pad * 0.8);

            if (TitleBarRow != null)
                TitleBarRow.Height = new GridLength(36);

            if (!_sidebarUserSized)
            {
                double target = windowWidth switch
                {
                    < 960 => 168,
                    < 1100 => 188,
                    _ => 200
                };
                target = SafeClamp(target, SidebarCol.MinWidth, SidebarCol.MaxWidth);
                if (Math.Abs(SidebarCol.Width.Value - target) > 0.5 || !SidebarCol.Width.IsAbsolute)
                    SidebarCol.Width = new GridLength(target);
            }
            else
            {
                double maxSidebar = SafeClamp(windowWidth - 280, SidebarCol.MinWidth, SidebarCol.MaxWidth);
                if (SidebarCol.ActualWidth > maxSidebar + 2)
                    SidebarCol.Width = new GridLength(maxSidebar);
            }

            bool compact = windowWidth < 900;
            var labelVis = compact ? Visibility.Collapsed : Visibility.Visible;
            LblToolbarNew.Visibility = labelVis;
            LblToolbarDelete.Visibility = labelVis;
            LblToolbarOpen.Visibility = labelVis;
            LblToolbarFolder.Visibility = labelVis;

            if (SearchCol != null)
            {
                double sw = windowWidth switch
                {
                    < 700 => 88,
                    < 820 => 120,
                    < 980 => 150,
                    _ => 180
                };
                SearchCol.Width = new GridLength(sw);
            }

            double gridW = DgDownloads.ActualWidth;
            if (gridW < 40)
                gridW = Math.Max(120, windowWidth - SidebarCol.Width.Value - 48);

            if (ColFileName != null)
            {
                // Sutunlar hicbir genislikte kaybolmaz: daralinca minimumlar kuculur, tarih kisa bicime doner
                ColSize.Visibility = Visibility.Visible;
                ColType.Visibility = Visibility.Visible;
                ColDate.Visibility = Visibility.Visible;
                ColStatus.Visibility = Visibility.Visible;
                ColActions.Visibility = Visibility.Visible;
                ColActions.MinWidth = ActionColumnWidth;
                ColActions.MaxWidth = ActionColumnWidth;
                ColActions.Width = new DataGridLength(ActionColumnWidth);

                bool tight = gridW < 760;
                bool veryTight = gridW < 640;
                ApplyDateColumnFormat(veryTight);

                double sizeMin = veryTight ? 58 : (tight ? 64 : 70);
                double typeMin = veryTight ? 40 : (tight ? 44 : 46);
                double dateMin = veryTight ? 68 : (tight ? 88 : 96);
                double statusMin = veryTight ? 86 : (tight ? 104 : Math.Min(180, Math.Max(120, gridW * 0.26)));

                ColSize.MinWidth = sizeMin;
                ColType.MinWidth = typeMin;
                ColDate.MinWidth = dateMin;
                ColDate.MaxWidth = veryTight ? 96 : 150;

                double reserved = 40 + ActionColumnWidth + sizeMin + typeMin + dateMin + statusMin;
                double fileMin = SafeClamp(gridW - reserved - 8, 56, 160);
                ColFileName.MinWidth = fileMin;
                ColFileName.Width = new DataGridLength(1, DataGridLengthUnitType.Star);

                ColStatus.MinWidth = statusMin;
                ColStatus.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                ColSize.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
        }

        /// <summary>Dar listede tarih "gg.AA.yy", genisde "gg.AA.yyyy SS:dd" gosterilir.</summary>
        private void ApplyDateColumnFormat(bool shortForm)
        {
            if (ColDate == null || _dateColumnShort == shortForm) return;
            _dateColumnShort = shortForm;
            ColDate.Binding = new Binding(nameof(DownloadItem.DateAdded))
            {
                StringFormat = shortForm ? "{0:dd.MM.yy}" : "{0:dd.MM.yyyy HH:mm}"
            };
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
            NativeWindowChrome.MinimizeToTaskbar(this);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeNative();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.OriginalSource is System.Windows.Controls.Button)
                return;
            if (e.OriginalSource is FrameworkElement fe && fe.TemplatedParent is System.Windows.Controls.Button)
                return;

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
            BtnMaximize.Content = max ? "\uE923" : "\uE922"; // ChromeRestore / ChromeMaximize
            BtnMaximize.ToolTip = max ? Loc.T("main.restore", "Geri yükle") : Loc.T("main.maximize", "Büyüt");
            RootChrome.CornerRadius = new CornerRadius(0);
            TitleBarChrome.CornerRadius = new CornerRadius(0);
            ContentChrome.CornerRadius = new CornerRadius(0);
            RootChrome.BorderThickness = max ? new Thickness(0) : new Thickness(1, 0, 1, 1);
            // Tam ekranda da liste paneli oval kalsın
            if (ListPanelBorder.ActualWidth > 0)
            {
                const double r = 14;
                ListPanelBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, ListPanelBorder.ActualWidth, ListPanelBorder.ActualHeight),
                    r, r);
            }
            ApplyResponsiveLayout(ActualWidth);
        }

        private Rect GetCurrentWorkArea() => MonitorWorkArea.Get(this);

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TxtSearch.IsKeyboardFocusWithin) return;
            if (e.OriginalSource is DependencyObject src && IsUnderElement(src, SearchBoxBorder))
                return;

            ClearSearchFocus();
        }

        private static bool IsUnderElement(DependencyObject? src, DependencyObject? ancestor)
        {
            while (src != null)
            {
                if (ReferenceEquals(src, ancestor)) return true;
                src = VisualTreeHelper.GetParent(src) ??
                      (src is FrameworkElement fe ? fe.Parent as DependencyObject : null);
            }
            return false;
        }

        private void ClearSearchFocus()
        {
            if (!TxtSearch.IsKeyboardFocusWithin) return;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, DgDownloads);
            Keyboard.Focus(DgDownloads);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = TxtSearch.Text?.Trim() ?? "";
            _downloadView?.Refresh();
        }

        private void TxtSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearSearchFocus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TxtSearch.SelectAll();
                e.Handled = true;
            }
        }

        private async void ToolbarNew_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowNewUrlOverlay(out var dlg)) return;

            var urls = dlg.Urls;
            if (dlg.GrabLinks)
            {
                string page = urls.Count > 0 ? urls[0] : dlg.Url;
                if (string.IsNullOrWhiteSpace(page))
                {
                    InfoDialog.Show(this, Loc.T("title.linkgrabber", "LinkGrabber"), Loc.T("msg.grab.paste_page_url", "Taranacak sayfa adresini yapıştırın."));
                    return;
                }
                ScanResultsWindow.ShowForPage(this, page);
                return;
            }
            if (urls.Count == 0)
            {
                var kind = UrlClassifier.Classify(dlg.Url);
                InfoDialog.Show(this, Loc.T("newurl.title", "Yeni indirme"), UrlClassifier.UnsupportedMessage(kind));
                return;
            }

            if (urls.Count == 1)
            {
                await StartDownloadProcess(urls[0], "", selectItem: true);
                return;
            }

            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                string name = BuildQuickFileName(url, "", null);
                if (FileNameHelper.NeedsResolution(name))
                {
                    try
                    {
                        var (resolved, _) = await ResolveDownloadMetaAsync(url, name, null);
                        if (!string.IsNullOrWhiteSpace(resolved))
                            name = resolved;
                    }
                    catch { /* quick name fallback */ }
                }
                string categoryId = ResolveCategoryForNewFile(name);
                string folder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                if (string.IsNullOrWhiteSpace(folder))
                    folder = _defaultFolder;
                BeginDownloadFromSession(url, name, folder, notify: false);
            }
        }

        private async Task DrainIpcLoopAsync()
        {
            while (true)
            {
                try
                {
                    foreach (var cmd in IpcInbox.Drain())
                    {
                        string action = string.IsNullOrWhiteSpace(cmd.Action)
                            ? (cmd.Grab ? "grab" : "add")
                            : cmd.Action.ToLowerInvariant();
                        if (action == "grab")
                            await GrabAndEnqueueAsync(cmd.Url);
                        else if (action == "pause")
                            PauseJobById(cmd.Url);
                        else if (action == "resume")
                            ResumeJobById(cmd.Url);
                        else if (action == "cancel")
                            CancelJobById(cmd.Url);
                        else
                            await StartDownloadProcess(cmd.Url, "", selectItem: true);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"IPC: {ex.Message}"); }
                await Task.Delay(1000);
            }
        }

        public async Task GrabAndEnqueueAsync(string pageUrl)
        {
            try
            {
                var settings = AppSettingsStore.Load();
                var links = await LinkGrabberService.FetchLinksAsync(
                    pageUrl, settings.CrawlDepth, 30, CancellationToken.None);
                if (links.Count == 0)
                {
                    InfoDialog.Show(this, Loc.T("title.linkgrabber", "LinkGrabber"), Loc.T("msg.grab.no_files", "Sayfada indirilebilir dosya bulunamadı."));
                    return;
                }

                int added = 0;
                foreach (string url in links)
                {
                    string name = BuildQuickFileName(url, "", null);
                    string categoryId = ResolveCategoryForNewFile(name);
                    string folder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                    if (string.IsNullOrWhiteSpace(folder))
                        folder = _defaultFolder;
                    if (BeginDownloadFromSession(url, name, folder, notify: false) != null)
                        added++;
                }
                if (added == 0)
                    InfoDialog.Show(this, Loc.T("title.linkgrabber", "LinkGrabber"), Loc.T("msg.grab.all_filtered", "Bulunan bağlantılar kurallara takıldı veya zaten listede."));
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, Loc.T("title.linkgrabber", "LinkGrabber"), Loc.T("msg.grab.scan_failed", "Sayfa taranamadı."), ex.Message);
            }
        }

        /// <summary>Sayfa tarama ekranında seçilenleri kuyruğa alır.</summary>
        public int EnqueueScanItems(IEnumerable<ScanItem> items)
        {
            int added = 0;
            foreach (var scan in items)
            {
                string name = string.IsNullOrWhiteSpace(scan.FileName)
                    ? BuildQuickFileName(scan.Url, "", null)
                    : scan.FileName;
                string categoryId = ResolveCategoryForNewFile(name);
                string folder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                if (string.IsNullOrWhiteSpace(folder))
                    folder = _defaultFolder;
                if (BeginDownloadFromSession(scan.Url, name, folder, notify: false) != null)
                    added++;
            }

            if (added == 0)
                InfoDialog.Show(this, Loc.T("scan.title", "Sayfa taraması"),
                    Loc.T("scan.none_added", "Seçilen bağlantılar kurallara takıldı veya zaten listede."));
            return added;
        }

        /// <summary>Eklentiden gelen sayfa taraması sonuçlarını gösterir.</summary>
        private void ShowExtensionScan(ScanRequest request)
        {
            var items = PageScanService.FromCandidates(request.Items);
            if (items.Count == 0)
            {
                ShowFromTray();
                InfoDialog.Show(this, Loc.T("scan.title", "Sayfa taraması"),
                    Loc.T("scan.no_results", "Sayfada indirilebilir dosya bulunamadı."));
                return;
            }

            ScanResultsWindow.ShowForItems(this, request.PageUrl, items);
        }

        private void TryAutoExtract(DownloadItem item)
        {
            var settings = AppSettingsStore.Load();
            if (!File.Exists(item.FilePath) || !ArchiveExtractor.IsArchive(item.FilePath))
                return;

            bool openAfterDownload = settings.AutoExtractArchives;
            bool deleteOnClose = settings.DeleteArchiveAfterExtract;
            if (!openAfterDownload && !deleteOnClose)
                return;

            try
            {
                if (deleteOnClose)
                    ArchiveExtractor.OpenAndDeleteWhenClosed(item.FilePath);
                else
                    ArchiveExtractor.OpenArchive(item.FilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Archive open: {ex.Message}");
                item.StatusText = "Arşiv açılamadı";
            }
        }

        private void ToolbarSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsOverlay();

        private void ToolbarSettings_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (IcoToolbarSettings != null)
                IcoToolbarSettings.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
        }

        private void ToolbarSettings_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (IcoToolbarSettings == null) return;
            bool light = ThemeService.IsLight;
            IcoToolbarSettings.Foreground = new SolidColorBrush(
                light ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xC8, 0xC8, 0xC8));
        }

        private void OpenSettingsOverlay(string? tab = null)
        {
            var settings = AppSettingsStore.Load();
            SettingsPanel.Load(settings, _defaultFolder, tab);
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.Focusable = true;
            Keyboard.Focus(SettingsOverlay);
        }

        private void CloseSettingsOverlay()
        {
            // Dil önizlemesi Kaydet olmadan kapanırsa kalıcı ayara dön
            try
            {
                string saved = AppSettingsStore.Load().UiLanguage ?? "tr";
                Loc.Apply(saved);
            }
            catch { /* ignore */ }
            SettingsOverlay.Visibility = Visibility.Collapsed;
            ThemeService.ApplyFromSettings();
        }

        private void TryCloseSettingsOverlay()
        {
            if (SettingsPanel.HasUnsavedChanges())
            {
                bool save = ShowModalConfirm(
                    DialogTexts.UnsavedSettingsTitle,
                    DialogTexts.UnsavedSettingsMessage,
                    "",
                    DialogTexts.UnsavedSettingsSave, DialogTexts.UnsavedSettingsDiscard, danger: false);
                if (save)
                {
                    if (!SettingsPanel.TrySave())
                        return;
                    return;
                }
            }
            CloseSettingsOverlay();
        }

        private void OnLanguageChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(OnLanguageChanged);
                return;
            }
            ApplyLocalizedTexts();
            foreach (var item in DownloadList)
                item.NotifyLanguageChanged();
            foreach (var cat in CategoryStore.AllFlat(Categories))
                cat.NotifyLanguageChanged();
            try { NewUrlPanel?.ApplyLocalizedTexts(); } catch { /* ignore */ }
            try { SettingsPanel?.ApplyLocalizedChrome(); } catch { /* ignore */ }
        }

        private void ApplyLocalizedTexts()
        {
            FlowDirection = Loc.Flow;
            if (LblToolbarNew != null) LblToolbarNew.Text = Loc.T("main.new", "Yeni");
            if (LblToolbarDelete != null) LblToolbarDelete.Text = Loc.T("main.delete", "Sil");
            if (LblToolbarOpen != null) LblToolbarOpen.Text = Loc.T("main.open", "Aç");
            if (LblToolbarFolder != null) LblToolbarFolder.Text = Loc.T("main.open_folder", "Klasörü aç");
            if (LblCategoriesHeader != null) LblCategoriesHeader.Text = Loc.T("main.categories", "Kategoriler");
            if (TxtSearchPlaceholder != null) TxtSearchPlaceholder.Text = Loc.T("main.search", "Ara");
            if (BtnMinimize != null) BtnMinimize.ToolTip = Loc.T("main.minimize", "Küçült");
            if (BtnMaximize != null) BtnMaximize.ToolTip = Loc.T("main.maximize", "Büyüt");
            if (BtnClose != null) BtnClose.ToolTip = Loc.T("main.close", "Kapat");
            if (BtnToolbarSettings != null) BtnToolbarSettings.ToolTip = Loc.T("main.settings", "Ayarlar");
            if (BtnToolbarNew != null) BtnToolbarNew.ToolTip = Loc.T("main.new_tip", "Yeni indirme");
            if (BtnToolbarDelete != null) BtnToolbarDelete.ToolTip = Loc.T("main.delete_tip", "Seçilenleri sil");
            if (BtnToolbarOpen != null) BtnToolbarOpen.ToolTip = Loc.T("main.open_tip", "Dosyayı aç");
            if (BtnToolbarFolder != null) BtnToolbarFolder.ToolTip = Loc.T("main.folder_tip", "Klasörde aç");
            if (TxtCopyToast != null) TxtCopyToast.Text = Loc.T("main.link_copied", "Bağlantı kopyalandı");
            if (ModalCancelBtn != null) ModalCancelBtn.Content = Loc.T("main.cancel", "İptal");

            if (ColFileName != null) ColFileName.Header = Loc.T("col.filename", "Dosya Adı");
            if (ColSize != null) ColSize.Header = Loc.T("col.size", "Boyut");
            if (ColType != null) ColType.Header = Loc.T("col.type", "Tür");
            if (ColDate != null) ColDate.Header = Loc.T("col.date", "Tarih");
            if (ColStatus != null) ColStatus.Header = Loc.T("col.status", "Durum");

            SetMenuHeader("CategoryItemContextMenu", "MenuCatRename", Loc.T("menu.cat.rename", "Yeniden adlandır"));
            SetMenuHeader("CategoryItemContextMenu", "MenuCatOpenFolder", Loc.T("menu.cat.open_folder", "Dosya konumunu aç"));
            SetMenuHeader("CategoryItemContextMenu", "MenuCatAddChild", Loc.T("menu.cat.add_child", "İçine alt kategori ekle"));
            SetMenuHeader("CategoryItemContextMenu", "MenuCatUnnest", Loc.T("menu.cat.unnest", "Kategoriyi çıkar"));
            SetMenuHeader("CategoryItemContextMenu", "MenuCatRules", Loc.T("menu.cat.rules", "Dosya türlerini ayarla"));
            SetMenuHeader("CategoryItemContextMenu", "MenuCatDelete", Loc.T("menu.cat.delete", "Seçilen kategorileri sil"));
            SetMenuHeader("CategoryPanelEmptyContextMenu", "MenuCatAdd", Loc.T("menu.cat.add", "Yeni kategori ekle"));

            SetMenuHeader("RowContextMenu", "MenuFileOpenFolder", Loc.T("menu.file.open_folder", "Dosya konumunu aç"));
            SetMenuHeader("RowContextMenu", "MenuFileCopy", Loc.T("menu.file.copy", "Dosyayı kopyala"));
            SetMenuHeader("RowContextMenu", "MenuFileCopyUrl", Loc.T("menu.file.copy_url", "Bağlantıyı kopyala"));
            SetMenuHeader("RowContextMenu", "MenuFileRedownload", Loc.T("menu.file.redownload", "Yeniden indir"));
            SetMenuHeader("RowContextMenu", "MenuFileRename", Loc.T("menu.file.rename", "Yeniden adlandır"));
            SetMenuHeader("RowContextMenu", "MenuFileMove", Loc.T("menu.file.move", "Kategoriye taşı"));
            SetMenuHeader("RowContextMenu", "MenuFileDelete", Loc.T("menu.file.delete", "Sil"));

            SetMenuHeader("ColumnHeaderContextMenu", "MenuResetColumns", Loc.T("menu.col.reset", "Varsayılan"));

            SetMenuHeader("ModernEditContextMenu", "MenuEditCut", Loc.T("menu.edit.cut", "Kes"));
            SetMenuHeader("ModernEditContextMenu", "MenuEditCopy", Loc.T("menu.edit.copy", "Kopyala"));
            SetMenuHeader("ModernEditContextMenu", "MenuEditPaste", Loc.T("menu.edit.paste", "Yapıştır"));
            SetMenuHeader("ModernEditContextMenu", "MenuEditSelectAll", Loc.T("menu.edit.select_all", "Tümünü seç"));

            SetMenuHeader("EmptyContextMenu", "MenuViewGroup", Loc.T("menu.list.view", "Görünüm"));
            SetMenuHeader("EmptyContextMenu", "MenuViewSmall", Loc.T("menu.list.view_small", "Küçük"));
            SetMenuHeader("EmptyContextMenu", "MenuViewMedium", Loc.T("menu.list.view_medium", "Orta"));
            SetMenuHeader("EmptyContextMenu", "MenuViewLarge", Loc.T("menu.list.view_large", "Büyük"));
            SetMenuHeader("EmptyContextMenu", "MenuSortGroup", Loc.T("menu.list.sort", "Sıralama ölçütü"));
            SetMenuHeader("EmptyContextMenu", "MenuSortName", Loc.T("menu.list.sort_name", "İsim"));
            SetMenuHeader("EmptyContextMenu", "MenuSortSize", Loc.T("menu.list.sort_size", "Boyut"));
            SetMenuHeader("EmptyContextMenu", "MenuSortType", Loc.T("menu.list.sort_type", "Tür"));
            SetMenuHeader("EmptyContextMenu", "MenuSortDate", Loc.T("menu.list.sort_date", "Tarih"));
            SetMenuHeader("EmptyContextMenu", "MenuListSelectAll", Loc.T("menu.list.select_all", "Tümünü seç"));

            // "Tüm indirilenler" label inside template
            try
            {
                if (BtnAllDownloads != null)
                {
                    BtnAllDownloads.ToolTip = Loc.T("main.all_downloads", "Tüm indirilenler");
                    if (FindVisualChild<TextBlock>(BtnAllDownloads) is TextBlock allLbl
                        && (allLbl.Name == "lbl" || allLbl.Text.Contains("indirilen", StringComparison.OrdinalIgnoreCase)
                            || allLbl.Text.Contains("download", StringComparison.OrdinalIgnoreCase)
                            || allLbl.Text == Loc.T("main.all_downloads", "Tüm indirilenler")))
                        allLbl.Text = Loc.T("main.all_downloads", "Tüm indirilenler");
                }
            }
            catch { /* ignore */ }

            foreach (var cat in CategoryStore.AllFlat(Categories))
                cat.NotifyLanguageChanged();
        }

        private void SetMenuHeader(string menuResourceKey, string itemName, string header)
        {
            try
            {
                if (TryFindResource(menuResourceKey) is not ContextMenu menu) return;
                if (menu.FindName(itemName) is MenuItem named)
                {
                    named.Header = header;
                    return;
                }
                if (FindMenuItemByName(menu.Items, itemName) is MenuItem mi)
                    mi.Header = header;
            }
            catch { /* ignore */ }
        }

        /// <summary>Alt menüler dahil ada göre MenuItem arar.</summary>
        private static MenuItem? FindMenuItemByName(System.Windows.Controls.ItemCollection items, string name)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Name == name) return item;
                if (FindMenuItemByName(item.Items, name) is MenuItem child) return child;
            }
            return null;
        }

        private void OnSettingsSaved()
        {
            var settings = AppSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder))
            {
                _defaultFolder = settings.DefaultDownloadFolder;
                try { Directory.CreateDirectory(_defaultFolder); } catch { /* ignore */ }
            }
            SettingsOverlay.Visibility = Visibility.Collapsed;
            StartBrowserCaptureServer();
            TorrentEngineHost.ReloadIfIdle();
            ThemeService.ApplyFromSettings();
            ApplyLocalizedTexts();
            foreach (var item in DownloadList)
                item.NotifyLanguageChanged();
            foreach (var cat in CategoryStore.AllFlat(Categories))
                cat.NotifyLanguageChanged();
            if (settings.AutoCreateCategoryFolders)
            {
                _ = Task.Run(() =>
                {
                    try { CategoryStore.EnsureDiskFolders(Categories, _defaultFolder); }
                    catch { /* ignore */ }
                });
            }
        }

        private void OnSettingsUpdateApplying()
        {
            CloseSettingsOverlay();
            ExitForUpdate();
        }

        private void SettingsOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == SettingsOverlay)
            {
                TryCloseSettingsOverlay();
                e.Handled = true;
            }
        }

        private void SettingsOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TryCloseSettingsOverlay();
                e.Handled = true;
            }
        }

        private void OverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Kart tıklaması arka plana yayılmasın
            e.Handled = true;
        }

        private List<DownloadItem> GetToolbarTargets()
        {
            var result = new List<DownloadItem>();
            var seen = new HashSet<DownloadItem>();

            void Add(DownloadItem? item)
            {
                if (item != null && seen.Add(item))
                    result.Add(item);
            }

            foreach (var item in DownloadList.Where(i => i.IsChecked))
                Add(item);

            foreach (DownloadItem item in DgDownloads.SelectedItems)
                Add(item);

            return result;
        }

        private List<DownloadItem> GetDragTargets(DownloadItem fallback)
        {
            if (_dragSelectionSnapshot is { Count: > 0 })
                return _dragSelectionSnapshot.ToList();

            var targets = GetToolbarTargets();
            if (targets.Count > 0) return targets;
            return new List<DownloadItem> { fallback };
        }

        private void RowCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not CheckBox { DataContext: DownloadItem item }) return;

            e.Handled = true;

            bool willCheck = !item.IsChecked;
            _syncingSelection = true;
            try
            {
                item.IsChecked = willCheck;

                if (willCheck)
                {
                    if (!DgDownloads.SelectedItems.Contains(item))
                        DgDownloads.SelectedItems.Add(item);
                }
                else if (DgDownloads.SelectedItems.Contains(item))
                {
                    DgDownloads.SelectedItems.Remove(item);
                }

                DgDownloads.CurrentItem = item;
                SyncSelectAllCheckbox();
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void ToolbarDelete_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, Loc.T("title.delete", "Sil"), DialogTexts.SelectFilesHint);
                return;
            }
            DeleteItems(targets);
        }

        private void ToolbarOpen_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, Loc.T("title.open", "Aç"), DialogTexts.SelectFilesHint);
                return;
            }

            foreach (var item in targets)
            {
                if (!File.Exists(item.FilePath))
                {
                    InfoDialog.Show(this, Loc.T("title.file", "Dosya"),
                        string.Format(Loc.T("msg.file.not_found_named", "Dosya bulunamadı: {0}"), item.FileName));
                    continue;
                }
                try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
                catch (Exception ex) { InfoDialog.Show(this, Loc.T("title.error", "Hata"), ex.Message); }
            }
        }

        private void ToolbarOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetToolbarTargets();
            if (targets.Count == 0)
            {
                InfoDialog.Show(this, Loc.T("title.folder", "Klasör"), DialogTexts.SelectFilesHint);
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
                    InfoDialog.Show(this, Loc.T("title.error", "Hata"), ex.Message);
                }
            }
        }

        private List<DownloadItem> GetVisibleDownloadItems()
        {
            if (_downloadView != null)
                return _downloadView.Cast<DownloadItem>().ToList();
            return DownloadList.ToList();
        }

        private void SyncSelectAllCheckbox()
        {
            var visible = GetVisibleDownloadItems();
            if (visible.Count == 0)
            {
                ChkSelectAll.IsChecked = false;
                return;
            }

            int checkedCount = visible.Count(i => i.IsChecked);
            ChkSelectAll.IsChecked = checkedCount switch
            {
                0 => false,
                _ when checkedCount == visible.Count => true,
                _ => null
            };
        }

        private void ApplySelectionToItems(IEnumerable<DownloadItem> items, bool selected)
        {
            DgDownloads.SelectedItems.Clear();
            if (!selected) return;

            foreach (var item in items)
                DgDownloads.SelectedItems.Add(item);
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = ChkSelectAll.IsChecked == true;
            var items = GetVisibleDownloadItems();
            _syncingSelection = true;
            try
            {
                foreach (var item in items)
                    item.IsChecked = check;
                ApplySelectionToItems(items, check);
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void RowOpenDownload_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DownloadItem item }) return;
            ShowSessionForItem(item);
        }

        private void RowCancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DownloadItem item }) return;

            bool ok = ConfirmDialog.Show(
                this,
                DialogTexts.CancelDownloadTitle,
                DialogTexts.CancelDownloadMessage,
                DialogTexts.CancelDownloadDetail(item.FileName),
                confirmText: DialogTexts.CancelDownloadConfirm,
                cancelText: DialogTexts.CancelDownloadDismiss,
                danger: true,
                accentCancel: true);
            if (!ok) return;

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
                        InfoDialog.Show(this, Loc.T("title.error", "Hata"), Loc.T("msg.folder.not_found", "Dosya veya dizin bulunamadı."));
                    }
                }
                catch (Exception ex)
                {
                    InfoDialog.Show(this, Loc.T("title.error", "Hata"),
                        string.Format(Loc.T("msg.folder.open_failed", "Klasör açılırken hata oluştu: {0}"), ex.Message));
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
                InfoDialog.Show(this, Loc.T("title.link", "Bağlantı"), Loc.T("msg.link.none_saved", "Bu indirme için kayıtlı bağlantı yok."));
                return;
            }
            try
            {
                Clipboard.SetText(url);
                ShowCopyToast(Loc.T("main.link_copied", "Bağlantı kopyalandı"));
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, Loc.T("title.error", "Hata"), ex.Message);
            }
        }

        private void MenuCopyFile_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCopySelectedFilesToClipboard())
                ShowCopyToast(Loc.T("msg.file.no_copy_target", "Kopyalanacak dosya yok"));
        }

        private void ShowCopyToast(string? message = null)
        {
            if (CopyToast.Child is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is { } label)
                label.Text = string.IsNullOrWhiteSpace(message) ? Loc.T("main.link_copied", "Bağlantı kopyalandı") : message;

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
                InfoDialog.Show(this, Loc.T("title.redownload", "Yeniden indir"), Loc.T("msg.redownload.no_url", "Bu öğe için bağlantı bulunamadı."));
                return;
            }
            await StartDownloadProcess(url, item.FileName, selectItem: true);
        }

        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (DgDownloads.SelectedItem is not DownloadItem item) return;

            string currentName = item.FileName;
            if (ShowPromptOverlay(Loc.T("title.rename", "Yeniden adlandır"), Loc.T("msg.rename.new_name_label", "Yeni dosya adı:"), currentName, extensionLockMode: true) != true)
                return;

            string newName = _lastPromptResult?.Text.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(newName) || newName == currentName) return;
            foreach (char c in Path.GetInvalidFileNameChars())
                newName = newName.Replace(c, '_');

            // Tik yoksa uzantiyi zorla koru
            if (_lastPromptResult is { AllowExtensionChange: false })
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
                    InfoDialog.Show(this, Loc.T("title.rename", "Yeniden adlandır"), Loc.T("msg.rename.invalid_path", "Dosya yolu geçersiz."));
                    return;
                }

                string dest = Path.Combine(dir, newName);
                if (File.Exists(dest))
                {
                    InfoDialog.Show(this, Loc.T("title.rename", "Yeniden adlandır"), Loc.T("msg.rename.exists", "Bu isimde bir dosya zaten var."));
                    return;
                }

                if (File.Exists(item.FilePath))
                    DownloadPathHelper.MoveFileAndState(item.FilePath, dest);
                else if (File.Exists(DownloadPathHelper.StatePath(item.FilePath)))
                    DownloadPathHelper.MoveFileAndState(item.FilePath, dest);

                item.FileName = newName;
                item.FilePath = dest;
                item.FileType = FileNameHelper.FormatTypeLabel(newName);
                item.FileIcon = IconHelper.GetIconForExtension(newName, _listIconPx);
                RebindEngineAfterPathChange(item, dest);
                QueueHistorySave();
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, Loc.T("title.error", "Hata"), ex.Message);
            }
        }

        private void MenuDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedItems();
        }

        private void MenuSelectAll_Click(object sender, RoutedEventArgs e)
        {
            DgDownloads.Focus();
            var items = GetVisibleDownloadItems();
            _syncingSelection = true;
            try
            {
                foreach (var item in items)
                    item.IsChecked = true;
                ApplySelectionToItems(items, true);
                ChkSelectAll.IsChecked = true;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void MenuViewDensity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            // Başlık yerelleştirildiği için ad üzerinden eşleştir
            string tag = mi.Name switch
            {
                "MenuViewSmall" => "Small",
                "MenuViewLarge" => "Large",
                _ => "Medium"
            };
            ApplyListDensity(tag);
            PersistListViewSettings();
            SyncListViewMenuChecks();
        }

        private void MenuSortBy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            // Başlık yerelleştirildiği için ad üzerinden eşleştir
            string tag = mi.Name switch
            {
                "MenuSortName" => "Name",
                "MenuSortSize" => "Size",
                "MenuSortType" => "Type",
                _ => "Date"
            };
            ApplyListSort(tag);
            PersistListViewSettings();
            SyncListViewMenuChecks();
        }

        private void PersistListViewSettings()
        {
            var s = AppSettingsStore.Load();
            s.ListDensity = _listDensity;
            s.ListSort = _listSort;
            AppSettingsStore.Save(s);
        }

        private void ApplyListDensity(string? density)
        {
            _listDensity = density switch
            {
                "Small" or "Küçük" => "Small",
                "Large" or "Büyük" => "Large",
                _ => "Medium"
            };

            double icon;
            double row;
            double font;
            switch (_listDensity)
            {
                case "Small":
                    icon = 14; row = 34; font = 12; _listIconPx = 16;
                    break;
                case "Large":
                    icon = 32; row = 64; font = 14; _listIconPx = 32;
                    break;
                default:
                    icon = 22; row = 46; font = 13; _listIconPx = 24;
                    break;
            }

            Resources["ListIconSize"] = icon;
            Resources["ListNameFontSize"] = font;
            DgDownloads.RowHeight = row;
            DgDownloads.MinRowHeight = row;

            foreach (var item in DownloadList)
                item.FileIcon = IconHelper.GetIconForExtension(item.FileName, _listIconPx);

            ApplyResponsiveLayout(ActualWidth);
        }

        private void ApplyListSort(string? sort)
        {
            _listSort = sort switch
            {
                "Size" or "Boyut" => "Size",
                "Type" or "Tür" => "Type",
                "Name" or "İsim" => "Name",
                _ => "Date"
            };

            if (_downloadView == null) return;
            _downloadView.SortDescriptions.Clear();
            switch (_listSort)
            {
                case "Size":
                    _downloadView.SortDescriptions.Add(
                        new SortDescription(nameof(DownloadItem.FileSizeBytes), ListSortDirection.Descending));
                    break;
                case "Type":
                    _downloadView.SortDescriptions.Add(
                        new SortDescription(nameof(DownloadItem.FileType), ListSortDirection.Ascending));
                    break;
                case "Name":
                    _downloadView.SortDescriptions.Add(
                        new SortDescription(nameof(DownloadItem.FileName), ListSortDirection.Ascending));
                    break;
                default:
                    _downloadView.SortDescriptions.Add(
                        new SortDescription(nameof(DownloadItem.DateAdded), ListSortDirection.Descending));
                    break;
            }
        }

        private void SyncListViewMenuChecks()
        {
            if (Resources["EmptyContextMenu"] is not ContextMenu menu) return;
            foreach (var top in menu.Items.OfType<MenuItem>())
            {
                foreach (var sub in top.Items.OfType<MenuItem>())
                {
                    // Yerelleştirme sonrası başlık değişebilir; ad sabit
                    sub.IsChecked = sub.Name switch
                    {
                        "MenuViewSmall" => _listDensity == "Small",
                        "MenuViewMedium" => _listDensity == "Medium",
                        "MenuViewLarge" => _listDensity == "Large",
                        "MenuSortName" => _listSort == "Name",
                        "MenuSortSize" => _listSort == "Size",
                        "MenuSortType" => _listSort == "Type",
                        "MenuSortDate" => _listSort == "Date",
                        _ => false
                    };
                }
            }
        }

        private static void SetItemFileSize(DownloadItem item, long bytes)
        {
            item.FileSizeBytes = bytes;
            item.FileSize = FormatFileSize(bytes);
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

            bool fromDisk = DeletesFilesFromDisk;
            string message = selectedItems.Count == 1
                ? DialogTexts.DeleteMessage(selectedItems[0].FileName)
                : DialogTexts.DeleteManyMessage(selectedItems.Count);

            string detail = DialogTexts.DeleteDetail(fromDisk, selectedItems.Count);

            if (!ConfirmDialog.Show(this, DialogTexts.DeleteTitle, message, detail,
                    confirmText: DialogTexts.DeleteConfirm, cancelText: DialogTexts.DismissAlt, danger: true))
                return;

            foreach (DownloadItem selectedItem in selectedItems)
            {
                if (_engines.TryGetValue(selectedItem, out var engine))
                {
                    engine.Cancel();
                    _engines.Remove(selectedItem);
                }

                if (fromDisk)
                {
                    try
                    {
                        DownloadPathHelper.DeleteFileAndState(selectedItem.FilePath);
                    }
                    catch (Exception ex)
                    {
                        InfoDialog.Show(this, Loc.T("title.warning", "Uyarı"),
                            string.Format(Loc.T("msg.file.delete_failed", "'{0}' silinemedi: {1}"), selectedItem.FileName, ex.Message));
                    }
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
            return FileNameHelper.ChooseDisplayName(fromHeader, decodedSuggested, fromUrl, contentType);
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

        private async Task StartDownloadProcess(string url, string incomingFilename, string? mimeHint = null, bool selectItem = true,
            ExtCaptureRequest? capture = null)
        {
            if (string.IsNullOrEmpty(url) || !UrlClassifier.CanDownloadNow(UrlClassifier.Classify(url)))
            {
                InfoDialog.Show(this, Loc.T("title.warning", "Uyarı"), UrlClassifier.UnsupportedMessage(UrlClassifier.Classify(url)));
                return;
            }

            string urlKey = NormalizeCaptureUrl(url);
            bool fromCapture = !selectItem;

            if (RepeatDownloadGuard.IsNoiseCapture(url, incomingFilename))
            {
                Debug.WriteLine($"Noise capture dropped: {url}");
                return;
            }

            if (!TryConfirmRepeatDownload(url, incomingFilename))
                return;

            // Aynı URL için kısa sürede tekrar pencere açma (eklenti spam / redirect)
            lock (_captureGate)
            {
                if (fromCapture && DateTime.UtcNow < _captureQuietUntilUtc)
                {
                    Debug.WriteLine($"Capture quiet (boot replay): {urlKey}");
                    return;
                }

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

                // Açık oturum varsa — boot quiet sırasında öne getirme
                foreach (var kv in _sessionWindows.ToList())
                {
                    if (NormalizeCaptureUrl(kv.Value.SessionUrl) != urlKey)
                        continue;
                    _recentCaptureUrls[urlKey] = DateTime.UtcNow;
                    if (fromCapture && DateTime.UtcNow < _captureQuietUntilUtc)
                        return;
                    try
                    {
                        if (!kv.Value.IsVisible) kv.Value.Show();
                        kv.Value.Activate();
                        kv.Value.BringToFrontSoft();
                    }
                    catch { /* ignore */ }
                    return;
                }

                // Listede aynı URL ile devam edilebilir kayıt varsa pencere açma (açılış replay)
                foreach (var item in DownloadList)
                {
                    string u = item.Url;
                    if (string.IsNullOrWhiteSpace(u))
                        _itemUrls.TryGetValue(item, out u!);
                    if (NormalizeCaptureUrl(u) != urlKey) continue;
                    if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase)) continue;

                    _recentCaptureUrls[urlKey] = DateTime.UtcNow;
                    Debug.WriteLine($"Capture matched existing item, no popup: {urlKey}");
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

                var session = new DownloadSessionWindow(this, url, quickName, defaultFolder, "—", capture)
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
            return FileNameHelper.ChooseDisplayName(null, decodedSuggested, fromUrl, mimeHint);
        }

        private static bool NeedsYtDlpBackend(ExtCaptureRequest capture, string url)
            => CaptureNeedsYtDlp(capture, url);

        /// <summary>YouTube / HLS / DASH / yt-dlp kind → yt-dlp motoru; düz dosya (Drive rar vb.) değil.</summary>
        public static bool CaptureNeedsYtDlp(ExtCaptureRequest? capture, string? url)
        {
            if (capture == null) return false;
            string kind = (capture.Kind ?? "").Trim().ToLowerInvariant();
            if (kind is "yt-dlp" or "hls" or "dash") return true;
            // Görsel/doküman (örn. YouTube kapak fotoğrafı) düz dosyadır — yt-dlp gerekmez
            if (MediaFormatService.IsDirectFileCapture(capture)) return false;
            string page = capture.PageUrl ?? "";
            string u = !string.IsNullOrWhiteSpace(capture.Url) ? capture.Url : (url ?? "");
            if (YtDlpHelper.IsYouTubeUrl(page) || YtDlpHelper.IsYouTubeUrl(u)) return true;
            if (MediaFormatService.LooksLikeHlsUrl(u) || MediaFormatService.LooksLikeDashUrl(u)) return true;
            return false;
        }

        private async Task StartExtCaptureDownloadAsync(ExtCaptureRequest ext)
        {
            string kind = (ext.Kind ?? "").Trim().ToLowerInvariant();
            string name = BuildExtCaptureFilename(ext);
            string url = ext.Url;
            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(ext.PageUrl))
                url = ext.PageUrl;

            // Görsel/doküman: sayfa video sayfası olsa da düz dosya olarak inilir
            if (MediaFormatService.IsDirectFileCapture(ext))
            {
                if (string.IsNullOrWhiteSpace(url) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                    return;
                EnsureCaptureReferer(ext);
                await StartDownloadProcess(url, name, ext.Mime, selectItem: false, capture: ext);
                return;
            }

            // Altyazı / junk URL → master adayı dene veya reddet
            if (MediaFormatService.IsJunkMediaUrl(url)
                && !YtDlpHelper.IsYouTubeUrl(ext.PageUrl)
                && !YtDlpHelper.IsYouTubeUrl(url))
            {
                string? fixedUrl = null;
                foreach (string cand in MediaFormatService.GuessHlsMasterUrls(url))
                {
                    fixedUrl = cand;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(fixedUrl))
                {
                    url = fixedUrl;
                    ext.Url = fixedUrl;
                    kind = "hls";
                }
                else
                    return;
            }

            // Header'a PageUrl Referer ekle (CDN hotlink)
            EnsureCaptureReferer(ext);

            bool isYouTube = YtDlpHelper.IsYouTubeUrl(ext.PageUrl) || YtDlpHelper.IsYouTubeUrl(url);
            bool urlIsHls = MediaFormatService.LooksLikeHlsUrl(url);
            bool urlIsDash = MediaFormatService.LooksLikeDashUrl(url);
            bool isPlaylist = urlIsHls || urlIsDash || kind is "hls" or "dash";
            bool isHtmlPage = MediaFormatService.LooksLikeHtmlPageUrl(url);

            // Progressive HTTP dosya → normal mini oturum (yt-dlp yok)
            // HTML player sayfası progressive sayılmaz
            if (!isYouTube && !isPlaylist && kind is not "yt-dlp" && !isHtmlPage)
            {
                if (string.IsNullOrWhiteSpace(url) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                    return;
                EnsureCaptureReferer(ext);
                await StartDownloadProcess(url, name, ext.Mime, selectItem: false, capture: ext);
                return;
            }

            // kind=hls ama URL gerçekten mp4 ise (yanlış etiket) → düz indirme
            if (!isYouTube && kind is "hls" or "dash"
                && MediaFormatService.LooksLikeProgressiveFile(url)
                && !urlIsHls && !urlIsDash)
            {
                await StartDownloadProcess(url, name, ext.Mime, selectItem: true);
                return;
            }

            // kind boş/progressive ama URL /hls/... → yine yt-dlp
            if (!isYouTube && isPlaylist)
                kind = urlIsDash || kind == "dash" ? "dash" : "hls";

            // Film sitelerinde player HTML → yt-dlp sayfa URL (extractor yoksa yine fail; en azından HTML kaydetme)
            string targetUrl = url;
            string pageKeep = ext.PageUrl;
            if (isYouTube)
            {
                targetUrl = !string.IsNullOrWhiteSpace(ext.PageUrl) ? ext.PageUrl! : url;
                if (YtDlpHelper.IsYouTubeUrl(targetUrl))
                    targetUrl = YtDlpHelper.NormalizeYouTubeWatchUrl(targetUrl) ?? targetUrl;
                if (string.IsNullOrWhiteSpace(ext.FormatId) || ext.FormatId is "best" or "playing")
                    ext.FormatId = "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b";
                ext.PageUrl = targetUrl;
                ext.Url = targetUrl;
                ext.Kind = "yt-dlp";
            }
            else
            {
                // Film / HLS / DASH: medya URL ile yt-dlp; player HTML asla hedef olmasın
                if (isHtmlPage
                    || string.IsNullOrWhiteSpace(targetUrl)
                    || targetUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isPlaylist)
                        return; // gerçek stream yakalanmadan indirme başlatma
                }
                if (string.IsNullOrWhiteSpace(ext.FormatId) || ext.FormatId is "playing")
                    ext.FormatId = "best";
                ext.FormatId = YtDlpHelper.NormalizeFormatForProbe(ext.FormatId, targetUrl);
                if (!string.IsNullOrWhiteSpace(pageKeep))
                    ext.PageUrl = pageKeep;
                ext.Url = targetUrl;
                ext.Kind = "yt-dlp";
            }

            // Mini indirme ekranı — kullanıcı Başlat'a basınca iner
            OpenYtDlpSessionWindow(ext, name, targetUrl);
            await Task.CompletedTask;
        }

        private static void EnsureCaptureReferer(ExtCaptureRequest ext)
        {
            string page = !string.IsNullOrWhiteSpace(ext.Referrer) ? ext.Referrer
                : (!string.IsNullOrWhiteSpace(ext.PageUrl) ? ext.PageUrl : "");
            if (string.IsNullOrWhiteSpace(page)) return;
            ext.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!ext.Headers.TryGetValue("Referer", out string? r) || string.IsNullOrWhiteSpace(r)
                || MediaFormatService.LooksLikeHlsUrl(r) || MediaFormatService.LooksLikeDashUrl(r))
                ext.Headers["Referer"] = page;
            if (string.IsNullOrWhiteSpace(ext.Referrer))
                ext.Referrer = page;
        }

        private bool TryConfirmRepeatDownload(string url, string? filename = null)
        {
            var decision = RepeatDownloadGuard.Evaluate(url, filename: filename);
            if (decision == RepeatDownloadGuard.AdmitResult.Allow)
                return true;
            if (decision == RepeatDownloadGuard.AdmitResult.DropSilent)
                return false;

            try
            {
                int n = Math.Max(RepeatDownloadGuard.SpamThreshold, RepeatDownloadGuard.PeekRecentCount(url));
                bool ok = ConfirmDialog.Show(this,
                    Loc.T("dialog.security_title", "Güvenlik onayı"),
                    Loc.T("dialog.security_message", "Şüpheli indirme etkinliği tespit edildi. Bu işlemi sizin başlattığınızı doğrulayın."),
                    string.Format(Loc.T("msg.security.detail_count", "Kısa sürede art arda {0}+ indirme isteği geldi.\nOnaylamazsanız istekler bir süre sessizce engellenir; ekran pencerelerle doldurulmaz."), n),
                    confirmText: Loc.T("dialog.security_allow", "İndirmeyi onayla"),
                    cancelText: Loc.T("dialog.security_block", "Engelle"),
                    danger: true,
                    forceFloating: true);

                if (ok)
                {
                    RepeatDownloadGuard.OnConfirmAllowed(url);
                    return true;
                }

                RepeatDownloadGuard.OnConfirmDenied(url);
                return false;
            }
            catch
            {
                RepeatDownloadGuard.ReleaseConfirmLock();
                throw;
            }
        }

        private void OpenYtDlpSessionWindow(ExtCaptureRequest ext, string fileName, string sessionUrl)
        {
            if (!TryConfirmRepeatDownload(sessionUrl))
                return;

            string urlKey = NormalizeCaptureUrl(sessionUrl) + "|" + (ext.FormatId ?? "");
            lock (_captureGate)
            {
                if (_pendingSessionUrls.Contains(urlKey))
                    return;
                _pendingSessionUrls.Add(urlKey);
                _recentCaptureUrls[NormalizeCaptureUrl(sessionUrl)] = DateTime.UtcNow;
            }

            try
            {
                string categoryId = ResolveCategoryForNewFile(fileName);
                string defaultFolder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
                if (string.IsNullOrWhiteSpace(defaultFolder))
                    defaultFolder = _defaultFolder;

                string sizeLabel = "—"; // yt-dlp: eklenti filesize güvenilmez; pencere probe ile doldurur
                var session = new DownloadSessionWindow(this, sessionUrl, fileName, defaultFolder, sizeLabel, ext)
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
            }
            catch
            {
                lock (_captureGate)
                    _pendingSessionUrls.Remove(urlKey);
                throw;
            }
        }
        private static string BuildExtCaptureFilename(ExtCaptureRequest ext)
        {
            bool directFile = MediaFormatService.IsDirectFileCapture(ext);
            string baseName = FileNameHelper.DecodeDisplayName(ext.Filename);

            // Görsel/doküman: URL'deki dosya adı sayfa başlığından daha doğru
            if (directFile && (string.IsNullOrWhiteSpace(baseName) || !baseName.Contains('.')))
            {
                string? fromUrl = FileNameHelper.TryFileNameFromUrl(ext.Url);
                if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.'))
                    baseName = FileNameHelper.DecodeDisplayName(fromUrl);
            }
            if (string.IsNullOrWhiteSpace(baseName) && !string.IsNullOrWhiteSpace(ext.Title))
                baseName = FileNameHelper.DecodeDisplayName(ext.Title);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = directFile ? "dosya" : "video";

            // FormatId teknik id ise (h-1080-..., bv*+ba) veya yükseklik etiketi ise isme ekleme
            bool technicalId = !string.IsNullOrWhiteSpace(ext.FormatId)
                && (ext.FormatId.Contains('+') || ext.FormatId.StartsWith("h-", StringComparison.Ordinal)
                    || ext.FormatId.StartsWith("hls-", StringComparison.Ordinal)
                    || ext.FormatId.StartsWith("dash-", StringComparison.Ordinal)
                    || ext.FormatId.StartsWith("prog-", StringComparison.Ordinal)
                    || ext.FormatId is "best" or "playing" or "progressive"
                    || YtDlpHelper.TryParseHeightHint(ext.FormatId, out _));
            if (!technicalId && !string.IsNullOrWhiteSpace(ext.FormatId)
                && !baseName.Contains(ext.FormatId, StringComparison.OrdinalIgnoreCase))
                baseName += $" - {ext.FormatId}";

            if (!baseName.Contains('.'))
            {
                // Görsel/doküman asla .mp4 almaz — uzantı URL'den veya MIME'dan gelir
                if (directFile)
                {
                    string fileExt = PageScanService.ExtensionOfUrl(ext.Url);
                    if (string.IsNullOrWhiteSpace(fileExt))
                        fileExt = ExtensionFromMime(ext.Mime);
                    return baseName + (string.IsNullOrWhiteSpace(fileExt) ? ".bin" : "." + fileExt);
                }

                string mime = ext.Mime ?? "";
                if (mime.Contains("audio", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                    baseName += ".m4a";
                else if (mime.Contains("zip", StringComparison.OrdinalIgnoreCase)
                         || mime.Contains("rar", StringComparison.OrdinalIgnoreCase)
                         || mime.Contains("7z", StringComparison.OrdinalIgnoreCase)
                         || mime.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    // Uzantıyı URL / Content-Disposition'dan tahmin et; yoksa .bin
                    string? fromUrl = FileNameHelper.TryFileNameFromUrl(ext.Url);
                    if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.'))
                        baseName = FileNameHelper.DecodeDisplayName(fromUrl);
                    else
                        baseName += ".bin";
                }
                else
                    baseName += ".mp4";
            }
            return baseName;
        }

        /// <summary>image/png → png, application/pdf → pdf. Bilinmiyorsa boş.</summary>
        private static string ExtensionFromMime(string? mime)
        {
            string m = (mime ?? "").Trim().ToLowerInvariant();
            int semi = m.IndexOf(';');
            if (semi > 0) m = m[..semi].Trim();
            if (m.Length == 0) return "";

            return m switch
            {
                "image/jpeg" or "image/jpg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                "image/gif" => "gif",
                "image/avif" => "avif",
                "image/bmp" => "bmp",
                "image/tiff" => "tiff",
                "image/svg+xml" => "svg",
                "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
                "application/pdf" => "pdf",
                _ => m.StartsWith("image/", StringComparison.Ordinal)
                    ? m[6..].Replace("+xml", "", StringComparison.Ordinal)
                    : ""
            };
        }

        private async Task EnrichSessionMetaAsync(
            DownloadSessionWindow session, string url, string currentName, string? mimeHint)
        {
            try
            {
                var (resolvedName, sizeLabel) = await ResolveDownloadMetaAsync(url, currentName, mimeHint)
                    .ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!session.IsVisible || session.HasStarted) return;

                    var kind = UrlClassifier.Classify(url);
                    string? sizeToApply = kind is TransferKind.Torrent or TransferKind.Magnet ? null : sizeLabel;
                    session.ApplyResolvedMeta(resolvedName, sizeToApply);

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

        public async Task<(string FileName, string SizeLabel)> ResolveDownloadMetaAsync(
            string url, string suggestedName, string? mimeHint = null)
            => await ResolveMetaAsync(url, suggestedName, mimeHint).ConfigureAwait(false);

        private async Task<(string FileName, string SizeLabel)> ResolveMetaAsync(
            string url, string suggestedName, string? mimeHint)
        {
            string decodedSuggested = FileNameHelper.DecodeDisplayName(suggestedName);
            var kind = UrlClassifier.Classify(url);
            var goFile = await GoFileResolver.TryResolveAsync(url).ConfigureAwait(false);
            if (goFile != null)
            {
                string goFileName = string.IsNullOrWhiteSpace(goFile.FileName)
                    ? BuildQuickFileName(url, decodedSuggested, mimeHint)
                    : goFile.FileName;
                string goFileSize = goFile.Size > 0 ? FormatFileSize(goFile.Size) : "—";
                return (goFileName, goFileSize);
            }

            if (kind is TransferKind.Magnet or TransferKind.Torrent)
            {
                var peek = await TorrentPeek.TryDescribeAsync(url).ConfigureAwait(false);
                string name = peek != null && !string.IsNullOrWhiteSpace(peek.Name)
                    ? peek.Name
                    : FileNameHelper.ChooseDisplayName(null, decodedSuggested, FileNameHelper.TryFileNameFromUrl(url), mimeHint);
                string size = peek is { Size: > 0 } ? FormatFileSize(peek.Size) : "—";
                return (name, size);
            }

            string? contentType = null;
            string? fromHeader = null;
            long? contentLength = null;

            try
            {
                using var client = CreateMetaHttpClient(url);
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
                    // bytes 0-0 → Content-Range Length = toplam boyut; 0-8191 ise Content-Length=8192 yanıltır
                    get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }

                using (response)
                {
                    contentType = response.Content.Headers.ContentType?.MediaType;
                    fromHeader = FileNameHelper.ExtractFromContentDisposition(response.Content.Headers);
                    contentLength = ResolveTotalContentLength(response);

                    bool isHtml = !string.IsNullOrWhiteSpace(contentType)
                        && contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
                    if (isHtml && string.IsNullOrWhiteSpace(fromHeader))
                    {
                        try
                        {
                            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            byte[] buf = new byte[96 * 1024];
                            int n = await stream.ReadAsync(buf).ConfigureAwait(false);
                            if (n > 0)
                            {
                                string html = Encoding.UTF8.GetString(buf, 0, n);
                                fromHeader = FileNameHelper.TryFileNameFromHtml(html);
                            }
                        }
                        catch { /* ignore */ }
                        contentType = null;
                        contentLength = null;
                    }
                    else if (string.IsNullOrWhiteSpace(fromHeader)
                             && FileNameHelper.NeedsResolution(decodedSuggested))
                    {
                        try
                        {
                            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            byte[] buf = new byte[16];
                            int n = await stream.ReadAsync(buf).ConfigureAwait(false);
                            if (n > 0)
                            {
                                string? magicExt = FileNameHelper.GuessExtensionFromMagic(buf.AsSpan(0, n));
                                if (!string.IsNullOrEmpty(magicExt))
                                {
                                    string baseName = FileNameHelper.IsPlaceholderName(decodedSuggested)
                                        ? "download"
                                        : Path.GetFileNameWithoutExtension(decodedSuggested);
                                    fromHeader = baseName + magicExt;
                                }
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }

            if (string.IsNullOrWhiteSpace(contentType) && !string.IsNullOrWhiteSpace(mimeHint))
                contentType = mimeHint;

            string? fromUrl = FileNameHelper.TryFileNameFromUrl(url);
            string chosen = FileNameHelper.ChooseDisplayName(fromHeader, decodedSuggested, fromUrl, contentType);

            // ZIP/RAR vb. için şüpheli küçük boyut (partial/HTML) gösterme
            if (contentLength is > 0 and < 64 * 1024
                && LooksLikeArchiveName(chosen, url, contentType))
                contentLength = null;

            string sizeLabel = contentLength is > 0 ? FormatFileSize(contentLength.Value) : "—";
            return (chosen, sizeLabel);
        }

        /// <summary>Content-Range Length öncelikli; partial Content-Length (8 KB) dosya boyutu değildir.</summary>
        private static long? ResolveTotalContentLength(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentRange?.Length is long rangeTotal && rangeTotal > 0)
                return rangeTotal;

            long? len = response.Content.Headers.ContentLength;
            if (len is null or <= 0)
                return null;

            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && len <= 64 * 1024)
                return null;

            return len;
        }

        private static bool LooksLikeArchiveName(string? fileName, string? url, string? contentType)
        {
            string s = $"{fileName}|{url}|{contentType}".ToLowerInvariant();
            return s.Contains(".zip") || s.Contains(".rar") || s.Contains(".7z")
                   || s.Contains(".tar") || s.Contains("application/zip")
                   || s.Contains("x-rar") || s.Contains("x-7z") || s.Contains("x-tar");
        }

        private static HttpClient CreateMetaHttpClient(string url)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://drive.google.com/");
            }
            return client;
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

            var engine = TransferFactory.Create(url, newPath, threadCount: DownloadQueue.HttpChannels());
            _engines[item] = engine;
            WireEngineEvents(item, engine);

            if (_sessionWindows.TryGetValue(item, out var session))
                session.RebindEngine(engine);
        }

        private static string NormalizeCaptureUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string t = url.Trim();
            if (t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return t;
            try
            {
                var uri = new Uri(t);
                // Query'deki geçici token'ları koru ama trailing slash / fragment temizle
                string path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                return path + (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query);
            }
            catch
            {
                return t;
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
                using var client = CreateMetaHttpClient(url);
                HttpResponseMessage? response = null;
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                }
                catch { /* ignore */ }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    response?.Dispose();
                    using var get = new HttpRequestMessage(HttpMethod.Get, url);
                    get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }

                using (response)
                {
                    long? len = ResolveTotalContentLength(response);
                    if (len is > 0)
                    {
                        if (len < 64 * 1024 && LooksLikeArchiveName(null, url, response.Content.Headers.ContentType?.MediaType))
                            return "—";
                        return FormatFileSize(len.Value);
                    }
                }
            }
            catch { /* ignore */ }
            return "—";
        }

        public sealed class DownloadRun
        {
            public required DownloadItem Item { get; init; }
            public required ITransferBackend Engine { get; init; }
        }

        public DownloadRun? BeginDownloadFromSession(string url, string fileName, string folder, bool notify = true,
            TorrentFetchMode torrentMode = TorrentFetchMode.FullContent, string? sizeHint = null,
            ExtCaptureRequest? ytdlpCapture = null)
        {
            var settings = AppSettingsStore.Load();
            fileName = SmartRules.ApplyRename(fileName, url, DateTime.Now, settings.RenamePattern);
            if (SmartRules.ShouldSkip(url, fileName, settings, ActiveUrls(), out string why))
            {
                if (notify)
                    InfoDialog.Show(this, Loc.T("title.rule", "Kural"), why);
                return null;
            }

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
                Id = Guid.NewGuid().ToString("N")[..12],
                FileName = finalFileName,
                FilePath = savePath,
                FileType = FileNameHelper.FormatTypeLabel(finalFileName),
                DateAdded = DateTime.Now,
                Status = "Hazır",
                FileIcon = IconHelper.GetIconForExtension(finalFileName, _listIconPx),
                CategoryId = categoryId,
                Url = url
            };

            if (TryParseSizeLabel(sizeHint, out long hintBytes, out string hintLabel))
            {
                item.FileSize = hintLabel;
                item.FileSizeBytes = hintBytes;
            }

            DownloadList.Insert(0, item);
            _itemUrls[item] = url;
            QueueHistorySave();

            int threadCount = DownloadQueue.HttpChannels(settings);
            var kind = UrlClassifier.Classify(url);
            ITransferBackend engine;
            bool useYtDlp = ytdlpCapture != null && NeedsYtDlpBackend(ytdlpCapture, url);
            if (useYtDlp && ytdlpCapture != null)
            {
                string pageUrl = !string.IsNullOrWhiteSpace(ytdlpCapture.PageUrl)
                    ? ytdlpCapture.PageUrl
                    : url;
                bool yt = YtDlpHelper.IsYouTubeUrl(pageUrl) || YtDlpHelper.IsYouTubeUrl(url);
                // İndirme adresi: YouTube=watch; HLS/film=medya URL (m3u8/mp4)
                string downloadUrl = yt
                    ? (YtDlpHelper.NormalizeYouTubeWatchUrl(pageUrl) ?? pageUrl)
                    : (!string.IsNullOrWhiteSpace(ytdlpCapture.Url) ? ytdlpCapture.Url : url);
                if (yt && YtDlpHelper.IsYouTubeUrl(pageUrl))
                    pageUrl = YtDlpHelper.NormalizeYouTubeWatchUrl(pageUrl) ?? pageUrl;
                string formatId = string.IsNullOrWhiteSpace(ytdlpCapture.FormatId)
                                  || ytdlpCapture.FormatId is "best" or "playing"
                    ? (yt
                        ? "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b"
                        : "best")
                    : YtDlpHelper.NormalizeFormatForProbe(ytdlpCapture.FormatId, downloadUrl);
                string stem = Path.Combine(saveFolder, Path.GetFileNameWithoutExtension(finalFileName));
                long expectedBytes = ytdlpCapture.Filesize;
                if (expectedBytes <= 0 && TryParseSizeLabel(sizeHint, out long ytdlpHintBytes, out _))
                    expectedBytes = ytdlpHintBytes;
                engine = new YtDlpTransferBackend(
                    downloadUrl, formatId, stem, ytdlpCapture.Cookies, ytdlpCapture.Headers, expectedBytes,
                    sitePageUrl: pageUrl);
            }
            else if (torrentMode == TorrentFetchMode.TorrentFileOnly && kind == TransferKind.Torrent)
            {
                if (!finalFileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    finalFileName = Path.GetFileNameWithoutExtension(finalFileName) + ".torrent";
                    savePath = GetUniqueFilePath(saveFolder, finalFileName);
                    item.FileName = finalFileName;
                    item.FilePath = savePath;
                    item.FileType = FileNameHelper.FormatTypeLabel(finalFileName);
                    item.FileIcon = IconHelper.GetIconForExtension(finalFileName, _listIconPx);
                }
                engine = new DownloadEngine(new[] { url }, savePath, threadCount);
            }
            else
            {
                engine = TransferFactory.Create(url, savePath, threadCount: threadCount);
                if (engine is DownloadEngine httpEngine && ytdlpCapture != null)
                    httpEngine.ApplyBrowserCapture(ytdlpCapture.Cookies, ytdlpCapture.Headers);
            }
            _engines[item] = engine;

            WireEngineEvents(item, engine);
            UpdateTransportButtons();

            if (DownloadQueue.IsFull(CountActiveDownloads(), settings.MaxConcurrentDownloads))
            {
                item.Status = "Kuyrukta";
                item.IsDownloading = false;
            }
            else
            {
                item.Status = "İndiriliyor";
                item.IsDownloading = true;
                _ = RunEngineAsync(item, engine);
            }
            return new DownloadRun { Item = item, Engine = engine };
        }

        private IEnumerable<string> ActiveUrls()
        {
            foreach (var item in DownloadList)
            {
                if (item.IsCompleted || item.IsCancelled)
                    continue;
                string u = item.Url;
                if (string.IsNullOrWhiteSpace(u))
                    _itemUrls.TryGetValue(item, out u!);
                if (!string.IsNullOrWhiteSpace(u))
                    yield return u;
            }
        }

        internal IReadOnlyList<RemoteJobDto> SnapshotJobs()
        {
            var list = new List<RemoteJobDto>(DownloadList.Count);
            foreach (var item in DownloadList)
            {
                string u = item.Url;
                if (string.IsNullOrWhiteSpace(u))
                    _itemUrls.TryGetValue(item, out u!);
                list.Add(new RemoteJobDto
                {
                    Id = item.Id,
                    Url = u ?? "",
                    FileName = item.FileName,
                    Status = item.Status,
                    Progress = item.ProgressValue,
                    Speed = item.CurrentSpeed ?? ""
                });
            }
            return list;
        }

        internal string? EnqueueFromApi(string url, string? filename)
        {
            if (string.IsNullOrWhiteSpace(url) || !UrlClassifier.CanDownloadNow(UrlClassifier.Classify(url)))
                return null;
            string name = BuildQuickFileName(url, filename ?? "", null);
            string folder = _defaultFolder;
            try
            {
                string categoryId = ResolveCategoryForNewFile(name);
                folder = CategoryStore.GetCategoryFolderPath(Categories, categoryId, _defaultFolder);
            }
            catch { /* keep default */ }
            if (string.IsNullOrWhiteSpace(folder))
                folder = _defaultFolder;
            var run = BeginDownloadFromSession(url, name, folder, notify: false);
            return run?.Item.Id;
        }

        internal bool PauseJobById(string id)
        {
            var item = FindJob(id);
            if (item == null || !_engines.TryGetValue(item, out var engine))
                return false;
            PauseFromSession(item, engine);
            return true;
        }

        internal bool ResumeJobById(string id)
        {
            var item = FindJob(id);
            if (item == null)
                return false;
            if (!_engines.TryGetValue(item, out var engine))
            {
                string url = item.Url;
                if (string.IsNullOrWhiteSpace(url))
                    _itemUrls.TryGetValue(item, out url!);
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(item.FilePath))
                    return false;
                engine = TransferFactory.Create(url, item.FilePath, threadCount: DownloadQueue.HttpChannels());
                _engines[item] = engine;
                WireEngineEvents(item, engine);
            }
            _ = ResumeFromSessionAsync(item, engine);
            return true;
        }

        internal bool CancelJobById(string id)
        {
            var item = FindJob(id);
            if (item == null)
                return false;
            if (_engines.TryGetValue(item, out var engine))
                CancelFromSession(item, engine);
            else
            {
                item.Status = "İptal Edildi";
                item.IsDownloading = false;
            }
            return true;
        }

        private DownloadItem? FindJob(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return DownloadList.FirstOrDefault(i =>
                string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Url, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.FileName, id, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class WindowJobHost : IRemoteJobHost
        {
            private readonly MainWindow _w;
            public WindowJobHost(MainWindow w) => _w = w;

            private T OnUi<T>(Func<T> fn)
            {
                if (_w.Dispatcher.CheckAccess())
                    return fn();
                return _w.Dispatcher.Invoke(fn, DispatcherPriority.Normal);
            }

            public IReadOnlyList<RemoteJobDto> ListJobs() => OnUi(_w.SnapshotJobs);
            public RemoteJobDto? GetJob(string id)
                => OnUi(() => _w.SnapshotJobs().FirstOrDefault(j =>
                    string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase)));
            public string? AddJob(string url, string? filename) => OnUi(() => _w.EnqueueFromApi(url, filename));
            public bool Pause(string id) => OnUi(() => _w.PauseJobById(id));
            public bool Resume(string id) => OnUi(() => _w.ResumeJobById(id));
            public bool Cancel(string id) => OnUi(() => _w.CancelJobById(id));
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

        public void PauseFromSession(DownloadItem item, ITransferBackend engine)
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

        public async Task ResumeFromSessionAsync(DownloadItem item, ITransferBackend engine)
        {
            item.Status = "İndiriliyor";
            item.IsDownloading = true;
            if (_sessionWindows.TryGetValue(item, out var session))
                session.RefreshFromHost();
            await RunEngineAsync(item, engine);
        }

        public void CancelFromSession(DownloadItem item, ITransferBackend engine)
        {
            item.Status = "İptal Edildi";
            item.StatusText = "";
            item.CurrentSpeed = "";
            item.IsDownloading = false;
            item.ProgressValue = 0;
            engine.Cancel();
            UpdateTransportButtons();
        }

        public bool DeletesFilesFromDisk => AppSettingsStore.Load().DeleteFilesFromDisk;

        public void DeleteItemFromSession(DownloadItem item)
        {
            if (_engines.TryGetValue(item, out var engine))
            {
                engine.Cancel();
                _engines.Remove(item);
            }

            if (DeletesFilesFromDisk)
            {
                try
                {
                    DownloadPathHelper.DeleteFileAndState(item.FilePath);
                }
                catch { /* ignore */ }
            }

            DownloadList.Remove(item);
            _itemUrls.Remove(item);
            _sessionWindows.Remove(item);
            UpdateTransportButtons();
            QueueHistorySave();
        }

        private void WireEngineEvents(DownloadItem item, ITransferBackend engine)
        {
            engine.TotalSizeKnown += (totalBytes) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    // Boyut zaten biliniyorsa (session probe / sizeHint) indirme boyunca sabitle —
                    // anlık stream tahmini ile güncelleme.
                    if (item.FileSizeBytes > 0)
                        return;
                    SetItemFileSize(item, totalBytes);
                    TrySkipBySize(item, engine, totalBytes);
                });
            };

            engine.OutputResolved += (fileName, path) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (string.IsNullOrWhiteSpace(fileName))
                        return;
                    item.FileName = fileName;
                    item.FileType = FileNameHelper.FormatTypeLabel(fileName);
                    item.FileIcon = IconHelper.GetIconForExtension(fileName, _listIconPx);
                    if (!string.IsNullOrWhiteSpace(path))
                        item.FilePath = path;
                    QueueHistorySave();
                });
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

                    // Tam sayı — %48,0 gibi titreşimli ondalık gösterme
                    item.StatusText = $"İndiriliyor %{progress:F0}";
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
                        // Bildirim RunEngineAsync finally içinde (çift tetiklenmesin)
                    }
                    else if (status.StartsWith("Hata", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "Hata";
                        item.StatusText = status.Length > 5
                            ? status[5..].Trim().TrimStart(':').Trim()
                            : status;
                        item.CurrentSpeed = "";
                        item.IsDownloading = false;
                        UpdateTransportButtons();
                        QueueHistorySave();
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
                    else if (status.StartsWith("Birleştir", StringComparison.OrdinalIgnoreCase))
                    {
                        if (engine.IsDownloading && !engine.IsCancelled)
                            item.StatusText = status;
                    }
                    else if (status.StartsWith("İndiriliyor", StringComparison.OrdinalIgnoreCase)
                             || status.StartsWith("Hazırlan", StringComparison.OrdinalIgnoreCase)
                             || status.StartsWith("Yeniden", StringComparison.OrdinalIgnoreCase)
                             || status.StartsWith("Tek dosya", StringComparison.OrdinalIgnoreCase)
                             || status.StartsWith("YouTube için Deno", StringComparison.OrdinalIgnoreCase))
                    {
                        // ProgressChanged "İndiriliyor %N" kalsın — yt-dlp faz metni
                        // (görüntü/ses) normal indirmeden farklı görünmesin
                    }
                    else if (!status.Contains("kanal", StringComparison.OrdinalIgnoreCase)
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

        private async Task RunEngineAsync(DownloadItem item, ITransferBackend engine)
        {
            try
            {
                var settings = AppSettingsStore.Load();
                if (settings.ScheduleEnabled
                    && !SchedulerGate.IsInsideWindow(DateTime.Now, settings.ScheduleStartHour, settings.ScheduleEndHour))
                {
                    item.Status = "Zamanlandı";
                    item.IsDownloading = false;
                    while (!engine.IsCancelled
                           && !SchedulerGate.IsInsideWindow(DateTime.Now, settings.ScheduleStartHour, settings.ScheduleEndHour))
                        await Task.Delay(8000).ConfigureAwait(true);
                    if (engine.IsCancelled)
                        return;
                    item.Status = "İndiriliyor";
                    item.IsDownloading = true;
                }

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
                if (item.Status.Contains("Kural", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    _engines.Remove(item);
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
                    // engine tutulur — Devam Et ile devam
                }
                else if (engine.IsPaused)
                {
                    item.Status = "Duraklatıldı";
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                }
                else if (engine.CompletedSuccessfully)
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";
                    item.Status = "Tamamlandı";
                    item.ProgressValue = 100;
                    // Boyut zaten probe ile sabitlendiyse dokunma
                    if (item.FileSizeBytes <= 0 && File.Exists(item.FilePath))
                        SetItemFileSize(item, new FileInfo(item.FilePath).Length);
                    TryAutoExtract(item);
                    _engines.Remove(item);
                    CompleteNotify.PlayIfEnabled(item.FileName);
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
                TryStartNextQueued();
            }
        }

        private int CountActiveDownloads()
        {
            int n = 0;
            foreach (var kv in _engines)
            {
                if (kv.Value.IsDownloading && !kv.Value.IsPaused && !kv.Value.IsCancelled)
                    n++;
            }
            return n;
        }

        private void TryStartNextQueued()
        {
            var settings = AppSettingsStore.Load();
            while (!DownloadQueue.IsFull(CountActiveDownloads(), settings.MaxConcurrentDownloads))
            {
                var next = DownloadList.FirstOrDefault(i =>
                    i.Status.Contains("Kuyrukta", StringComparison.OrdinalIgnoreCase)
                    && _engines.ContainsKey(i));
                if (next == null)
                    return;
                if (!_engines.TryGetValue(next, out var engine))
                    return;
                next.Status = "İndiriliyor";
                next.IsDownloading = true;
                _ = RunEngineAsync(next, engine);
            }
        }

        private void TrySkipBySize(DownloadItem item, ITransferBackend engine, long totalBytes)
        {
            if (totalBytes <= 0 || engine.IsCancelled || engine.CompletedSuccessfully)
                return;
            var settings = AppSettingsStore.Load();
            if (settings.SkipMinSizeMb <= 0 && settings.SkipMaxSizeMb <= 0)
                return;
            string url = item.Url;
            if (string.IsNullOrWhiteSpace(url))
                _itemUrls.TryGetValue(item, out url!);
            if (SmartRules.ShouldSkip(url ?? "", item.FileName, settings, Array.Empty<string>(), out string why, totalBytes))
            {
                item.Status = "Kural — " + why;
                item.IsDownloading = false;
                item.CurrentSpeed = "";
                engine.Cancel();
            }
        }

        internal static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }

        internal static bool TryParseSizeLabel(string? label, out long bytes, out string normalizedLabel)
        {
            bytes = -1;
            normalizedLabel = label ?? "";
            if (string.IsNullOrWhiteSpace(label) || label is "-" or "—")
                return false;
            if (label.Contains("netleşir", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Boyut bilinmiyor", StringComparison.OrdinalIgnoreCase))
                return false;

            string t = label.Trim().Replace(',', '.');
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^([\d.]+)\s*([KMGT]B|B)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double n))
                return false;

            bytes = m.Groups[2].Value.ToUpperInvariant() switch
            {
                "B" => (long)n,
                "KB" => (long)(n * 1024),
                "MB" => (long)(n * 1024 * 1024),
                "GB" => (long)(n * 1024 * 1024 * 1024),
                "TB" => (long)(n * 1024L * 1024 * 1024 * 1024),
                _ => -1
            };
            if (bytes < 0) return false;
            normalizedLabel = FormatFileSize(bytes);
            return true;
        }

        private List<(DownloadItem Item, ITransferBackend Engine)> GetSelectedEngines()
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
            if (_syncingSelection || _isFileDragging) return;

            foreach (DownloadItem item in e.RemovedItems)
                item.IsChecked = false;
            foreach (DownloadItem item in e.AddedItems)
                item.IsChecked = true;
            SyncSelectAllCheckbox();
        }

        private void DgDownloads_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sidebarInputActive = false;
        }

        private void DgDownloads_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DgDownloads.Focus();
                var items = GetVisibleDownloadItems();
                _syncingSelection = true;
                try
                {
                    foreach (var item in items)
                        item.IsChecked = true;
                    ApplySelectionToItems(items, true);
                    ChkSelectAll.IsChecked = true;
                }
                finally
                {
                    _syncingSelection = false;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && DeleteKeyEnabled())
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

            var settings = AppSettingsStore.Load();
            if (settings.CopyFilesHotkeyEnabled
                && HotkeyParser.Matches(settings.CopyFilesHotkey, e))
            {
                // Native Ctrl+C metin kopyasından ayır: her zaman işle, dosya yoksa bildir
                e.Handled = true;
                if (!TryCopySelectedFilesToClipboard())
                    ShowCopyToast(Loc.T("msg.file.no_copy_target", "Kopyalanacak dosya yok"));
                return;
            }

            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DgDownloads.Focus();
                DgDownloads.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && DeleteKeyEnabled())
            {
                var deletableCats = LstCategories.SelectedItems.OfType<CategoryItem>()
                    .Where(c => !c.IsBuiltin && c.Id != "All").ToList();
                bool sidebarActive = _sidebarInputActive
                    || LstCategories.IsKeyboardFocusWithin
                    || (SidebarPanel?.IsKeyboardFocusWithin ?? false);

                if (sidebarActive && deletableCats.Count > 0)
                {
                    MenuDeleteCategories_Click(sender, e);
                    e.Handled = true;
                }
                else if (DgDownloads.SelectedItems.Count > 0)
                {
                    DeleteSelectedItems();
                    e.Handled = true;
                }
                else if (deletableCats.Count > 0)
                {
                    MenuDeleteCategories_Click(sender, e);
                    e.Handled = true;
                }
            }
        }

        private bool TryCopySelectedFilesToClipboard()
        {
            var files = DgDownloads.SelectedItems.OfType<DownloadItem>()
                .Select(i => i.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
                return false;

            try
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(files);
                Clipboard.SetFileDropList(list);
                ShowCopyToast(Loc.T("msg.file.copied", "Dosya kopyalandı"));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy files: {ex.Message}");
                return false;
            }
        }

        private void MenuResetColumns_Click(object sender, RoutedEventArgs e)
            => ResetColumnsToDefault();

        private void ResetColumnsToDefault()
        {
            // Dosya adı, Boyut, Tür, Tarih, Durum (+ seçim ve aksiyon)
            try
            {
                ColCheck.Visibility = Visibility.Visible;
                ColFileName.Visibility = Visibility.Visible;
                ColSize.Visibility = Visibility.Visible;
                ColType.Visibility = Visibility.Visible;
                ColDate.Visibility = Visibility.Visible;
                ColStatus.Visibility = Visibility.Visible;
                ColActions.Visibility = Visibility.Visible;

                ColCheck.DisplayIndex = 0;
                ColFileName.DisplayIndex = 1;
                ColFileName.Width = new DataGridLength(2, DataGridLengthUnitType.Star);
                ColSize.DisplayIndex = 2;
                ColSize.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                ColType.DisplayIndex = 3;
                ColType.Width = DataGridLength.Auto;
                ColDate.DisplayIndex = 4;
                ColDate.Width = DataGridLength.Auto;
                ColStatus.DisplayIndex = 5;
                ColStatus.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                ColActions.DisplayIndex = 6;

                ApplyResponsiveLayout(ActualWidth);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reset columns: {ex.Message}");
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

        private async Task ResumeDownloadAsync(DownloadItem target, ITransferBackend engine)
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
                if (engine.IsCancelled)
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
                else if (engine.IsPaused)
                {
                    target.Status = "Duraklatıldı";
                    target.IsDownloading = false;
                }
                else if (engine.CompletedSuccessfully)
                {
                    target.Status = "Tamamlandı";
                    target.IsDownloading = false;
                    target.ProgressValue = 100;
                    if (File.Exists(target.FilePath))
                        SetItemFileSize(target, new FileInfo(target.FilePath).Length);
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
                InfoDialog.Show(this, Loc.T("title.error", "Hata"), Loc.T("msg.file.not_found", "Dosya belirtilen konumda bulunamadı!"));
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
                ClearColumnSortHighlight();
            }
        }

        private void ClearColumnSortHighlight()
        {
            if (DgDownloads == null) return;
            foreach (var col in DgDownloads.Columns)
                col.SortDirection = null;
            if (_downloadView != null)
                _downloadView.SortDescriptions.Clear();

            if (FindVisualChild<DataGridColumnHeadersPresenter>(DgDownloads) is { } headers)
            {
                foreach (var h in FindVisualChildren<DataGridColumnHeader>(headers))
                {
                    h.Tag = null;
                    h.ClearValue(Control.ForegroundProperty);
                }
            }
        }

        private void ColumnHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridColumnHeader clicked) return;
            if (FindParent<Thumb>(e.OriginalSource as DependencyObject) != null) return;

            if (FindVisualChild<DataGridColumnHeadersPresenter>(DgDownloads) is { } headers)
            {
                foreach (var h in FindVisualChildren<DataGridColumnHeader>(headers))
                    h.Tag = null;
            }

            clicked.Tag = "active";
            clicked.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
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

            if (sender is not DataGridRow row || row.Item is not DownloadItem item) return;

            // Tunnel aşamasında seçim henüz değişmedi — sürükleme için anlık görüntü al
            if (row.IsSelected && DgDownloads.SelectedItems.Count > 1
                && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _dragSelectionSnapshot = DgDownloads.SelectedItems.Cast<DownloadItem>().ToList();
                return;
            }

            // Tek tıkla satırı seç (turuncu vurgu)
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (!row.IsSelected || DgDownloads.SelectedItems.Count != 1)
                {
                    DgDownloads.SelectedItems.Clear();
                    row.IsSelected = true;
                    DgDownloads.CurrentItem = item;
                }
            }

            var snap = GetToolbarTargets();
            if (snap.Count == 0)
                _dragSelectionSnapshot = new List<DownloadItem> { item };
            else if (!row.IsSelected
                     && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                     && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _dragSelectionSnapshot = new List<DownloadItem> { item };
            else
                _dragSelectionSnapshot = snap;
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
                    var selected = GetDragTargets(item);

                    string[] existingFiles = selected
                        .Select(i => i.FilePath)
                        .Where(File.Exists)
                        .ToArray();

                    var dataObj = new DataObject();
                    if (existingFiles.Length > 0)
                        dataObj.SetData(DataFormats.FileDrop, existingFiles);
                    var dragBatch = selected.ToArray();
                    dataObj.SetData("DownloadItems", dragBatch);
                    dataObj.SetData("DownloadItem", dragBatch[0]);

                    UpdateFileDragGhost(selected);
                    FileDragPopup.IsOpen = true;

                    var dragVisuals = ApplyFileDragRowVisuals(DgDownloads, selected);
                    if (dragVisuals.Count == 0)
                    {
                        var (accent, dragBg) = GetFileDragRowBrushes();
                        dragVisuals.Add(new DragRowVisual
                        {
                            Row = row,
                            Opacity = row.Opacity,
                            BorderBrush = row.BorderBrush,
                            BorderThickness = row.BorderThickness,
                            Background = row.Background
                        });
                        row.Opacity = 0.62;
                        row.Background = dragBg;
                        row.BorderBrush = accent;
                        row.BorderThickness = new Thickness(2, 0, 0, 0);
                    }

                    _isFileDragging = true;
                    _fileDragItems = selected;
                    try
                    {
                        DragDrop.DoDragDrop(row, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                    finally
                    {
                        _isFileDragging = false;
                        _fileDragItems = null;
                        _dragSelectionSnapshot = null;
                        FileDragPopup.IsOpen = false;
                        ImgFileDragGhost.Source = null;
                        ImgFileDragGhost2.Source = null;
                        ImgFileDragGhost3.Source = null;
                        RestoreFileDragRowVisuals(dragVisuals);
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
            TorrentEngineHost.Shutdown();
        }
    }
}
