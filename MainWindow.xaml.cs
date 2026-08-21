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
        private DownloadEngine? _currentEngine;
        public ObservableCollection<DownloadItem> DownloadList { get; set; } = new ObservableCollection<DownloadItem>();
        private ICollectionView? _downloadView;
        private DownloadItem? _activeItem;

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
                _captureServer = new BrowserCaptureServer((url, filename) =>
                {
                    Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            TxtUrl.Text = url;
                            await StartDownloadProcess(url, filename);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Capture download error: {ex.Message}");
                        }
                    });
                });
                _captureServer.Start();
                SetCaptureStatus(true, null);
            }
            catch (Exception ex)
            {
                SetCaptureStatus(false, ex.Message);
                // Sessizce durum cubugunda goster; korkutucu popup sadece gercekten hic port yoksa
                Debug.WriteLine($"Capture server failed: {ex.Message}");
            }
        }

        private void SetCaptureStatus(bool online, string? error)
        {
            if (TxtCaptureStatus == null) return;

            if (online && _captureServer != null)
            {
                TxtCaptureStatus.Text = $"Eklenti: hazır (:{_captureServer.ActivePort})";
                TxtCaptureStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0xCB, 0x6B));
                TxtCaptureStatus.ToolTip = $"127.0.0.1:{_captureServer.ActivePort} dinleniyor";
            }
            else
            {
                TxtCaptureStatus.Text = "Eklenti: kapalı";
                TxtCaptureStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                TxtCaptureStatus.ToolTip = error ?? "Yakalama sunucusu çalışmıyor";
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
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "❐";
            }
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
            var selectedItems = DgDownloads.SelectedItems.Cast<DownloadItem>().ToList();
            if (selectedItems.Count == 0) return;

            string message = selectedItems.Count == 1
                ? $"'{selectedItems[0].FileName}' dosyası diskten ve listeden silinsin mi?"
                : $"{selectedItems.Count} dosya diskten ve listeden silinsin mi?";

            var result = MessageBox.Show(message, "Silme Onayı", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            foreach (DownloadItem selectedItem in selectedItems)
            {
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

        private async Task<string> ResolveFileNameAsync(string url, string suggestedName)
        {
            if (!string.IsNullOrEmpty(suggestedName) && suggestedName != "downloaded_file.zip" && suggestedName != "download")
            {
                return suggestedName;
            }

            try
            {
                using var client = new HttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var response = await client.SendAsync(request);

                if (response.Content.Headers.ContentDisposition?.FileName != null)
                {
                    string fn = response.Content.Headers.ContentDisposition.FileName.Trim('"');
                    if (!string.IsNullOrEmpty(fn)) return fn;
                }
            }
            catch { }

            return !string.IsNullOrEmpty(suggestedName) ? suggestedName : "downloaded_file.rar";
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtUrl.Text.Trim();
            await StartDownloadProcess(url, "");
        }

        private async Task StartDownloadProcess(string url, string incomingFilename)
        {
            string saveFolder = TxtDefaultFolder.Text.Trim();

            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                MessageBox.Show("Lütfen geçerli bir indirme bağlantısı girin!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(saveFolder))
            {
                Directory.CreateDirectory(saveFolder);
            }

            string solvedFileName = await ResolveFileNameAsync(url, incomingFilename);
            string savePath = GetUniqueFilePath(saveFolder, solvedFileName);

            BtnDownload.IsEnabled = false;
            BtnPauseResume.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            BtnPauseResume.Content = "Duraklat";

            string finalFileName = Path.GetFileName(savePath);
            _activeItem = new DownloadItem
            {
                FileName = finalFileName,
                FilePath = savePath,
                FileType = Path.GetExtension(savePath).ToUpper().Replace(".", ""),
                DateAdded = DateTime.Now,
                Status = "İndiriliyor",
                FileIcon = IconHelper.GetIconForExtension(finalFileName)
            };

            DownloadList.Insert(0, _activeItem);

            _currentEngine = new DownloadEngine(url, savePath, threadCount: 8);

            _currentEngine.TotalSizeKnown += (totalBytes) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_activeItem != null)
                        _activeItem.FileSize = FormatFileSize(totalBytes);
                });
            };

            _currentEngine.ProgressChanged += (progress) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_activeItem == null) return;
                    _activeItem.ProgressValue = progress;
                    _activeItem.StatusText = $"İndiriliyor %{progress:F1}";
                    // Status'u her tick'te degistirme — IsDownloading flicker'ini onler
                    if (!_activeItem.IsDownloading)
                        _activeItem.Status = "İndiriliyor";
                });
            };

            _currentEngine.StatusChanged += (status) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_activeItem == null) return;
                    // Yuzde iceren ara durumlari StatusText'te tut; Status sabit kalsin
                    if (status.Contains('%'))
                        _activeItem.StatusText = status;
                    else
                        _activeItem.Status = status;
                });
            };

            _currentEngine.SpeedAndTimeChanged += (speed, time) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_activeItem != null)
                        _activeItem.CurrentSpeed = speed;
                });
            };

            try
            {
                await _currentEngine.StartOrResumeDownloadAsync();
            }
            catch (Exception ex)
            {
                if (_activeItem != null) _activeItem.Status = "Hata";
                MessageBox.Show($"Hata oluştu: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnDownload.IsEnabled = true;
                BtnPauseResume.IsEnabled = false;
                BtnCancel.IsEnabled = false;
                BtnPauseResume.Content = "Duraklat";

                if (_activeItem != null)
                {
                    _activeItem.IsDownloading = false;
                    if (_currentEngine != null && _currentEngine.IsCancelled)
                    {
                        _activeItem.Status = "İptal Edildi";
                        _activeItem.ProgressValue = 0;
                        _activeItem.StatusText = "";
                        _activeItem.CurrentSpeed = "";
                    }
                    else if (_currentEngine != null && !_currentEngine.IsPaused)
                    {
                        _activeItem.Status = "Tamamlandı";
                        _activeItem.StatusText = "";
                        _activeItem.CurrentSpeed = "";
                        if (File.Exists(_activeItem.FilePath))
                        {
                            long bytes = new FileInfo(_activeItem.FilePath).Length;
                            _activeItem.FileSize = FormatFileSize(bytes);
                        }
                    }
                }
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }

        private async void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEngine == null) return;

            if (_currentEngine.IsDownloading)
            {
                _currentEngine.Pause();
                BtnPauseResume.Content = "Devam Et";
                if (_activeItem != null) { _activeItem.Status = "Duraklatıldı"; _activeItem.IsDownloading = false; }
            }
            else if (_currentEngine.IsPaused)
            {
                BtnPauseResume.Content = "Duraklat";
                if (_activeItem != null) { _activeItem.Status = "İndiriliyor"; }
                await _currentEngine.StartOrResumeDownloadAsync();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEngine != null)
            {
                _currentEngine.Cancel();
                BtnCancel.IsEnabled = false;
                BtnPauseResume.IsEnabled = false;
            }
        }

        private void DgDownloads_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DgDownloads.SelectedItem is DownloadItem selectedItem)
            {
                if (File.Exists(selectedItem.FilePath))
                {
                    Process.Start(new ProcessStartInfo(selectedItem.FilePath) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show("Dosya belirtilen konumda bulunamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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
