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
        private string _defaultFolder = "";
        private string _currentCategory = "All";
        private string _searchText = "";
        public ObservableCollection<CategoryItem> Categories { get; private set; } = CategoryStore.CreateDefaults();
        public ObservableCollection<CategoryItem> VisibleCategories { get; } = new();
        private Point _categoryDragStart;
        private CategoryItem? _categoryDragItem;
        private bool _isPseudoMaximized;
        private Rect _restoreBounds;
        private bool _catMarqueeArmed;
        private bool _catMarqueeActive;
        private Point _catMarqueeStart;
        private HashSet<CategoryItem>? _catMarqueeCtrlBase;
        private bool _suppressCategorySelection;

        public MainWindow()
        {
            InitializeComponent();

            _defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Categories = CategoryStore.Load();
            RebuildVisibleCategories();
            LstCategories.ItemsSource = VisibleCategories;

            _downloadView = CollectionViewSource.GetDefaultView(DownloadList);
            _downloadView.Filter = FilterByCategory;
            DgDownloads.ItemsSource = _downloadView;
            DgDownloads.GiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.PreviewGiveFeedback += DgDownloads_GiveFeedback;
            DgDownloads.LayoutUpdated += DgDownloads_LayoutUpdated;

            _currentCategory = "All";
            HighlightAllDownloadsButton(true);
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
            StartBrowserCaptureServer();
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
            string ext = Path.GetExtension(item.FileName)?.TrimStart('.') ?? "";

            // Acik atama
            if (!string.IsNullOrEmpty(item.CategoryId) && item.CategoryId != "All")
            {
                if (treeIds.Contains(item.CategoryId)) return true;
            }

            // Uzanti kurallari (secili kategori + altlar)
            foreach (var node in cat.Flatten())
            {
                if (node.Extensions is { Count: > 0 } && node.Extensions.Contains(ext))
                    return true;
            }

            return false;
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
            BtnAllDownloads.Opacity = selected ? 1 : 0.82;
            BtnAllDownloads.ApplyTemplate();
            if (BtnAllDownloads.Template?.FindName("bd", BtnAllDownloads) is Border bd)
            {
                bd.BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x55, 0x44, 0x33));
                bd.Background = selected
                    ? new SolidColorBrush(Color.FromRgb(0x25, 0x1A, 0x12))
                    : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
            }
        }

        private static readonly SolidColorBrush ColumnDragBrush =
            new(Color.FromRgb(0xFF, 0x6B, 0x00)) { Opacity = 0.85 };

        private void DgDownloads_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
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

        private void BtnAddCategory_Click(object sender, RoutedEventArgs e) => AddCategoryInteractive();

        private void MenuAddCategory_Click(object sender, RoutedEventArgs e) => AddCategoryInteractive();

        private void AddCategoryInteractive()
        {
            var dlg = new PromptDialog("Yeni kategori", "Kategori adı:", "Yeni kategori")
            {
                Owner = this
            };
            if (dlg.ShowDialog() != true) return;
            string name = dlg.ResultText.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            var item = new CategoryItem
            {
                Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Icon = "📁",
                IsBuiltin = false,
                Depth = 0
            };

            if (LstCategories.SelectedItem is CategoryItem parent && parent.Id != "All")
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

            CategoryStore.Save(Categories);
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
                // Mevcut dosyalari yeniden esle
                foreach (var item in DownloadList)
                {
                    string resolved = ResolveCategoryForNewFile(item.FileName);
                    if (resolved != "All")
                        item.CategoryId = resolved;
                }
                _downloadView?.Refresh();
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

            // Ghost popup mouse'u takip etsin
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
            }
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
            e.Effects = e.Data.GetDataPresent(typeof(CategoryItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void LstCategories_Drop(object sender, DragEventArgs e)
        {
            try
            {
                CategoryDragPopup.IsOpen = false;
                if (e.Data.GetData(typeof(CategoryItem)) is not CategoryItem dragged) return;
                if (dragged.Id == "All") return;

                var target = (e.OriginalSource as DependencyObject) is DependencyObject d
                    ? FindAncestor<ListBoxItem>(d)?.DataContext as CategoryItem
                    : null;

                // Bos alana birakma = kok seviyeye tasi
                if (target == null)
                {
                    DetachCategory(dragged);
                    dragged.ParentId = null;
                    dragged.Depth = 0;
                    if (!Categories.Contains(dragged))
                        Categories.Add(dragged);
                    CategoryStore.RecalcDepths(Categories, 0);
                    CategoryStore.Save(Categories);
                    RebuildVisibleCategories();
                    LstCategories.SelectedItem = dragged;
                    return;
                }

                if (ReferenceEquals(target, dragged) || target.IsDescendantOf(dragged))
                    return;

                DetachCategory(dragged);

                if (target.Id == "All")
                {
                    dragged.ParentId = null;
                    dragged.Depth = 0;
                    if (!Categories.Contains(dragged))
                    {
                        int insertAt = Math.Min(1, Categories.Count);
                        Categories.Insert(insertAt, dragged);
                    }
                }
                else
                {
                    dragged.ParentId = target.Id;
                    target.Children.Add(dragged);
                    target.IsExpanded = true;
                    target.NotifyChildrenChanged();
                }

                CategoryStore.RecalcDepths(Categories, 0);
                CategoryStore.Save(Categories);
                RebuildVisibleCategories();
                LstCategories.SelectedItem = dragged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LstCategories_Drop: {ex}");
                CategoryDragPopup.IsOpen = false;
                _categoryDragItem = null;
            }
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
                    Padding = new Thickness(12, 8, 12, 8)
                };
                mi.Template = CreateDarkMenuItemTemplate();
                mi.Click += (_, _) =>
                {
                    foreach (var item in selected)
                        item.CategoryId = (string)mi.Tag;
                    _downloadView?.Refresh();
                };
                menu.Items.Add(mi);
            }

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private static ControlTemplate CreateDarkMenuTemplate()
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));
            factory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            factory.SetValue(Border.PaddingProperty, new Thickness(6));
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
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            factory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(MenuItem.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(cp);
            template.VisualTree = factory;

            var trigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x21, 0x18)), "bd"));
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
            if (sender is not Border border) return;
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                14, 14);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }

            if (_isPseudoMaximized) return;
            DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            RootChrome.BeginAnimation(OpacityProperty, null);
            RootChrome.RenderTransform = null;

            if (_isPseudoMaximized)
            {
                Left = _restoreBounds.X;
                Top = _restoreBounds.Y;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
                BtnMaximize.Content = "☐";
                RootChrome.CornerRadius = new CornerRadius(16);
                ContentChrome.CornerRadius = new CornerRadius(0, 0, 16, 16);
                _isPseudoMaximized = false;
            }
            else
            {
                _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
                var wa = SystemParameters.WorkArea;
                Left = wa.Left;
                Top = wa.Top;
                Width = wa.Width;
                Height = wa.Height;
                BtnMaximize.Content = "❐";
                RootChrome.CornerRadius = new CornerRadius(0);
                ContentChrome.CornerRadius = new CornerRadius(0);
                _isPseudoMaximized = true;
            }
        }

        private void ApplyWindowStateToggle() => ToggleMaximize();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = TxtSearch.Text?.Trim() ?? "";
            _downloadView?.Refresh();
        }

        private async void ToolbarNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NewUrlDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            await StartDownloadProcess(dlg.Url, "", selectItem: true);
        }

        private void ToolbarSettings_Click(object sender, RoutedEventArgs e)
        {
            InfoDialog.Show(this, "Ayarlar", "Ayarlar yakında eklenecek.");
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
            // Ust URL cubugu kaldirildi; eklenti / oturum penceresi kullanilir
            await Task.CompletedTask;
        }

        private async Task StartDownloadProcess(string url, string incomingFilename, string? mimeHint = null, bool selectItem = true)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http"))
            {
                MessageBox.Show("Lütfen geçerli bir indirme bağlantısı girin!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string defaultFolder = _defaultFolder;
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
            session.Activate(); // yeni indirme: one al
            session.BringToFrontSoft();
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
                FileIcon = IconHelper.GetIconForExtension(finalFileName),
                CategoryId = ResolveCategoryForNewFile(finalFileName)
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

        private string ResolveCategoryForNewFile(string fileName)
        {
            // Secili kategori (All degilse) once
            if (_currentCategory != "All")
            {
                var selected = CategoryStore.FindById(Categories, _currentCategory);
                if (selected != null)
                    return selected.Id;
            }

            string ext = Path.GetExtension(fileName)?.TrimStart('.') ?? "";
            if (string.IsNullOrEmpty(ext)) return "All";

            // En derin eslesen kurali tercih et
            CategoryItem? best = null;
            foreach (var c in CategoryStore.AllFlat(Categories))
            {
                if (c.Id == "All" || c.Extensions == null || !c.Extensions.Contains(ext))
                    continue;
                if (best == null || c.Depth > best.Depth)
                    best = c;
            }

            return best?.Id ?? "All";
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
