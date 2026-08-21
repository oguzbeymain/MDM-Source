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
using System.Windows.Input;
using System.Windows.Media;

namespace DownloadMuck
{
    public partial class MainWindow : Window
    {
        private BrowserCaptureServer? _captureServer;
        private readonly Dictionary<DownloadItem, DownloadEngine> _engines = new();
        private readonly Dictionary<DownloadItem, DownloadSessionWindow> _sessionWindows = new();
        private readonly Dictionary<DownloadItem, string> _itemUrls = new();
        public ObservableCollection<DownloadItem> DownloadList { get; set; } = new ObservableCollection<DownloadItem>();
        private ICollectionView? _downloadView;

        private Point _dragStartPoint;
        private bool _marqueeArmed;
        private bool _marqueeActive;
        private Point _marqueeStart;
        private HashSet<DownloadItem>? _marqueeCtrlBase;
        private string _currentCategory = "All";

        private static readonly HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase)
            { "pdf", "doc", "docx", "txt", "rtf", "odt", "xls", "xlsx", "ppt", "pptx", "csv", "md" };
        private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
            { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpeg", "mpg" };
        private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
            { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus" };
        private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
            { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab" };
        private static readonly HashSet<string> AppExts = new(StringComparer.OrdinalIgnoreCase)
            { "exe", "msi", "apk", "bat", "cmd", "msix", "appx", "dmg" };

        private readonly SolidColorBrush _catSelectedBg = new(Color.FromRgb(0x25, 0x1A, 0x12));
        private readonly SolidColorBrush _catSelectedFg = new(Color.FromRgb(0xFF, 0x6B, 0x00));
        private readonly SolidColorBrush _catNormalBg = Brushes.Transparent;
        private readonly SolidColorBrush _catNormalFg = new(Color.FromRgb(0xAA, 0xAA, 0xAA));

        public MainWindow()
        {
            InitializeComponent();

            string defaultDownloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            TxtDefaultFolder.Text = defaultDownloadsFolder;

            _downloadView = CollectionViewSource.GetDefaultView(DownloadList);
            _downloadView.Filter = FilterByCategory;
            DgDownloads.ItemsSource = _downloadView;

            HighlightCategoryButton(BtnCatAll);
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
            StartBrowserCaptureServer();
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
                            TxtUrl.Text = url;
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
            if (_currentCategory == "All") return true;

            string ext = (item.FileType ?? "").Trim().TrimStart('.');
            return _currentCategory switch
            {
                "Documents" => DocumentExts.Contains(ext),
                "Videos" => VideoExts.Contains(ext),
                "Audio" => AudioExts.Contains(ext),
                "Archives" => ArchiveExts.Contains(ext),
                "Apps" => AppExts.Contains(ext),
                _ => true
            };
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string category)
                return;

            _currentCategory = category;
            HighlightCategoryButton(btn);
            _downloadView?.Refresh();
            DgDownloads.SelectedItems.Clear();
        }

        private void HighlightCategoryButton(Button selected)
        {
            Button[] buttons = { BtnCatAll, BtnCatDocuments, BtnCatVideos, BtnCatAudio, BtnCatArchives, BtnCatApps };
            foreach (Button button in buttons)
            {
                bool isSelected = ReferenceEquals(button, selected);
                button.Background = isSelected ? _catSelectedBg : _catNormalBg;
                button.Foreground = isSelected ? _catSelectedFg : _catNormalFg;
                button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private void ListPanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border border) return;
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                14, 14);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "☐";
                RootChrome.CornerRadius = new CornerRadius(16);
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
                RootChrome.CornerRadius = new CornerRadius(0);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            TxtDefaultFolder.Focus();
            TxtDefaultFolder.SelectAll();
            BtnBrowseFolder_Click(sender, e);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Varsayılan İndirme Klasörünü Seçin"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtDefaultFolder.Text = dialog.FolderName;
            }
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
                        MessageBox.Show("Dosya veya dizin bulunamadı.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Klasör açılırken hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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
            var selectedItems = DgDownloads.SelectedItems.Cast<DownloadItem>().ToList();
            if (selectedItems.Count == 0) return;

            string message = selectedItems.Count == 1
                ? $"“{selectedItems[0].FileName}” kalıcı olarak silinsin mi?"
                : $"{selectedItems.Count} öğe kalıcı olarak silinsin mi?";

            string detail = selectedItems.Count == 1
                ? "Dosya listeden kaldırılır ve diskteki kopyası da silinir."
                : "Seçili dosyalar listeden kaldırılır ve diskteki kopyaları da silinir.";

            if (!ConfirmDialog.Show(this, "Silme onayı", message, detail))
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
                    if (File.Exists(selectedItem.FilePath))
                        File.Delete(selectedItem.FilePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"'{selectedItem.FileName}' silinemedi: {ex.Message}", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                DownloadList.Remove(selectedItem);
            }

            UpdateTransportButtons();
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
            string url = TxtUrl.Text.Trim();
            await StartDownloadProcess(url, "", selectItem: true);
        }

        private async Task StartDownloadProcess(string url, string incomingFilename, string? mimeHint = null, bool selectItem = true)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                MessageBox.Show("Lütfen geçerli bir indirme bağlantısı girin!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string defaultFolder = TxtDefaultFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(defaultFolder))
                defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            string solvedFileName = await ResolveFileNameAsync(url, incomingFilename, mimeHint);
            string sizeHint = await ProbeContentLengthLabelAsync(url);

            var session = new DownloadSessionWindow(this, url, solvedFileName, defaultFolder, sizeHint)
            {
                Owner = null,
                Topmost = false,
                ShowInTaskbar = true
            };
            session.Show();
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
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string savePath = GetUniqueFilePath(folder, fileName);
            string finalFileName = Path.GetFileName(savePath);

            var item = new DownloadItem
            {
                FileName = finalFileName,
                FilePath = savePath,
                FileType = FileNameHelper.FormatTypeLabel(finalFileName),
                DateAdded = DateTime.Now,
                Status = "İndiriliyor",
                FileIcon = IconHelper.GetIconForExtension(finalFileName)
            };

            DownloadList.Insert(0, item);
            _itemUrls[item] = url;

            int activeCount = Math.Max(1, _engines.Count + 1);
            int threadCount = activeCount >= 3 ? 4 : 8;
            var engine = new DownloadEngine(url, savePath, threadCount: threadCount);
            _engines[item] = engine;

            WireEngineEvents(item, engine);
            UpdateTransportButtons();

            _ = RunEngineAsync(item, engine);
            return new DownloadRun { Item = item, Engine = engine };
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
            }
        }

        public async Task ResumeFromSessionAsync(DownloadItem item, DownloadEngine engine)
        {
            item.Status = "İndiriliyor";
            item.IsDownloading = true;
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
                if (File.Exists(item.FilePath))
                    File.Delete(item.FilePath);
            }
            catch { /* ignore */ }

            DownloadList.Remove(item);
            _itemUrls.Remove(item);
            _sessionWindows.Remove(item);
            UpdateTransportButtons();
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
                    }
                    else if (status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = "Tamamlandı";
                        item.StatusText = "";
                        item.CurrentSpeed = "";
                        item.IsDownloading = false;
                        item.ProgressValue = 100;
                        UpdateTransportButtons();
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
                item.Status = "Hata";
                MessageBox.Show($"Hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (engine.IsPaused && !engine.IsCancelled)
                {
                    item.Status = "Duraklatıldı";
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                }
                else
                {
                    item.IsDownloading = false;
                    item.CurrentSpeed = "";
                    item.StatusText = "";

                    if (engine.IsCancelled)
                    {
                        item.Status = "İptal Edildi";
                        item.ProgressValue = 0;
                    }
                    else if (!engine.IsPaused)
                    {
                        item.Status = "Tamamlandı";
                        if (File.Exists(item.FilePath))
                            item.FileSize = FormatFileSize(new FileInfo(item.FilePath).Length);
                    }

                    _engines.Remove(item);
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
            var selected = GetSelectedEngines();
            bool hasSelection = selected.Count > 0;

            BtnPauseResume.IsEnabled = hasSelection;
            BtnCancel.IsEnabled = hasSelection;

            if (hasSelection)
            {
                bool anyDownloading = selected.Any(x => x.Engine.IsDownloading && !x.Engine.IsPaused);
                BtnPauseResume.Content = anyDownloading ? "Duraklat" : "Devam Et";
            }
            else
            {
                BtnPauseResume.Content = "Duraklat";
            }

            BtnDownload.IsEnabled = true;
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
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DgDownloads.Focus();
                DgDownloads.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                     && DgDownloads.SelectedItems.Count > 0
                     && !(Keyboard.FocusedElement is TextBox))
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
                target.Status = "Hata";
                MessageBox.Show($"Hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
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
                else if (!engine.IsPaused)
                {
                    target.Status = "Tamamlandı";
                    target.IsDownloading = false;
                    target.StatusText = "";
                    target.CurrentSpeed = "";
                    if (File.Exists(target.FilePath))
                        target.FileSize = FormatFileSize(new FileInfo(target.FilePath).Length);
                    _engines.Remove(target);
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
                MessageBox.Show("Dosya belirtilen konumda bulunamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Marquee_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            if (FindParent<ScrollBar>(source) != null) return;
            if (FindParent<DataGridColumnHeader>(source) != null) return;
            if (FindParent<Thumb>(source) != null) return;

            _marqueeStart = e.GetPosition(DownloadListHost);
            _marqueeArmed = true;
            _marqueeActive = false;
            _marqueeCtrlBase = null;
            _dragStartPoint = e.GetPosition(null);

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var row = FindParent<DataGridRow>(source);

            // Bos alana tiklaninca secimi kaldir — yazi turuncuda kalmasin
            if (row == null)
            {
                if (!ctrl)
                {
                    DgDownloads.UnselectAll();
                    DgDownloads.CurrentCell = new DataGridCellInfo();
                }
            }
            else if (ctrl)
            {
                _marqueeCtrlBase = DgDownloads.SelectedItems.Cast<DownloadItem>().ToHashSet();
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

        private void DataGridRow_MouseMove(object sender, MouseEventArgs e)
        {
            // Marquee aktifken dosya sürükleme başlatma
            if (_marqueeActive) return;
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _dragStartPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is DataGridRow row && row.Item is DownloadItem)
                    {
                        var selected = DgDownloads.SelectedItems.Cast<DownloadItem>().ToList();
                        if (selected.Count == 0) return;

                        string[] existingFiles = selected
                            .Select(i => i.FilePath)
                            .Where(File.Exists)
                            .ToArray();

                        DataObject dataObj = new DataObject();
                        if (existingFiles.Length > 0)
                            dataObj.SetData(DataFormats.FileDrop, existingFiles);

                        dataObj.SetData("DownloadItem", selected[0]);
                        DragDrop.DoDragDrop(row, dataObj, DragDropEffects.Copy);
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
