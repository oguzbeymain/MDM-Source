using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DownloadMuck
{
    public partial class DownloadSessionWindow : Window
    {
        private readonly MainWindow _host;
        private readonly string _url;
        private string _fileName;
        private DownloadItem? _item;
        private DownloadEngine? _engine;
        private bool _started;

        public DownloadItem? BoundItem => _item;
        public string SessionUrl => _url;
        public bool HasStarted => _started;

        public DownloadSessionWindow(MainWindow host, string url, string fileName, string defaultFolder, string sizeLabel)
        {
            InitializeComponent();
            _host = host;
            _url = url;
            _fileName = fileName;
            ApplyMeta(fileName, url, defaultFolder, sizeLabel);
        }

        /// <summary>Mevcut indirmeye bagli oturum (cift tik).</summary>
        public DownloadSessionWindow(MainWindow host, DownloadItem item, DownloadEngine? engine, string url)
        {
            InitializeComponent();
            _host = host;
            _url = url;
            _fileName = item.FileName;
            _item = item;
            _engine = engine;
            _started = true;

            string folder = Path.GetDirectoryName(item.FilePath) ?? "";
            ApplyMeta(item.FileName, url, folder, item.FileSize);

            TxtFolder.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            TxtFolder.IsHitTestVisible = false;
            BtnBrowse.IsHitTestVisible = false;
            _item.PropertyChanged += ItemOnPropertyChanged;

            if (item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
            {
                PanelActive.Visibility = Visibility.Collapsed;
                PanelDone.Visibility = Visibility.Visible;
                BtnMoveFile.Visibility = Visibility.Visible;
            }
            else if (engine != null && engine.IsPaused)
            {
                BtnStart.IsEnabled = true;
                BtnStart.Content = "Devam Et";
                BtnPause.IsEnabled = false;
                BtnCancelDl.IsEnabled = true;
            }
            else if (item.IsDownloading || (engine != null && engine.IsDownloading))
            {
                BtnStart.IsEnabled = false;
                BtnPause.IsEnabled = true;
                BtnCancelDl.IsEnabled = true;
            }
            else
            {
                BtnStart.IsEnabled = false;
                BtnPause.IsEnabled = false;
                BtnCancelDl.IsEnabled = false;
            }

            SyncFromItem();
        }

        private void ApplyMeta(string fileName, string url, string folder, string sizeLabel)
        {
            TxtUrl.Text = url;
            TxtFileName.Text = fileName;
            TxtFileType.Text = FileNameHelper.FormatTypeLabel(fileName);
            TxtFolder.Text = folder;
            TxtSize.Text = string.IsNullOrWhiteSpace(sizeLabel) || sizeLabel == "-" ? "—" : sizeLabel;
            ImgIcon.Source = IconHelper.GetIconForExtension(fileName);
            TxtUrl.ContextMenu = BuildEditMenu();
            TxtFolder.ContextMenu = BuildEditMenu();
            TxtUrl.CaretBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            TxtUrl.SelectionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            TxtFolder.CaretBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            TxtFolder.SelectionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00));
            TxtUrl.IsReadOnly = true;
            DataObject.AddCopyingHandler(TxtUrl, (_, _) => { /* allow copy */ });
            TxtUrl.PreviewMouseDoubleClick += (_, e) =>
            {
                TxtUrl.SelectAll();
                e.Handled = true;
            };
            CommandManager.AddPreviewExecutedHandler(TxtUrl, (_, e) =>
            {
                if (e.Command == ApplicationCommands.Copy && TxtUrl.SelectionLength == 0)
                {
                    TxtUrl.SelectAll();
                }
            });
        }

        public void BringToFrontSoft()
        {
            // Kisa sure one al, sonra topmost kapat — arka plan islerine engel olmasin
            try
            {
                Topmost = true;
                Activate();
                Dispatcher.BeginInvoke(() => Topmost = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch { /* ignore */ }
        }

        /// <summary>Ağdan gelen dosya adı / boyut bilgisini oturum başlamadan günceller.</summary>
        public void ApplyResolvedMeta(string fileName, string? sizeLabel)
        {
            if (_started) return;

            if (!string.IsNullOrWhiteSpace(fileName) &&
                !string.Equals(_fileName, fileName, StringComparison.Ordinal))
            {
                _fileName = fileName;
                TxtFileName.Text = fileName;
                TxtFileType.Text = FileNameHelper.FormatTypeLabel(fileName);
                ImgIcon.Source = IconHelper.GetIconForExtension(fileName);
            }

            if (!string.IsNullOrWhiteSpace(sizeLabel) && sizeLabel != "-")
                TxtSize.Text = sizeLabel;
        }

        public void ApplyDefaultFolderIfIdle(string folder)
        {
            if (_started || string.IsNullOrWhiteSpace(folder)) return;
            TxtFolder.Text = folder;
        }

        public void RebindEngine(DownloadEngine engine)
        {
            _engine = engine;
        }

        private static ContextMenu BuildEditMenu()
        {
            var menu = new ContextMenu
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null
            };
            menu.Template = CreateMenuTemplate();
            var itemTemplate = CreateMenuItemTemplate();

            MenuItem Make(string header, RoutedUICommand cmd) => new()
            {
                Header = header,
                Command = cmd,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                Background = Brushes.Transparent,
                FontSize = 11,
                Padding = new Thickness(10, 5, 10, 5),
                Height = 28,
                FocusVisualStyle = null,
                Template = itemTemplate
            };

            menu.Items.Add(Make("Kes", ApplicationCommands.Cut));
            menu.Items.Add(Make("Kopyala", ApplicationCommands.Copy));
            menu.Items.Add(Make("Yapıştır", ApplicationCommands.Paste));
            var sepFactory = new FrameworkElementFactory(typeof(Border));
            sepFactory.SetValue(Border.HeightProperty, 1.0);
            sepFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
            sepFactory.SetValue(Border.MarginProperty, new Thickness(6, 3, 6, 3));
            menu.Items.Add(new Separator { Template = new ControlTemplate(typeof(Separator)) { VisualTree = sepFactory } });
            menu.Items.Add(Make("Tümünü seç", ApplicationCommands.SelectAll));
            return menu;
        }

        private static ControlTemplate CreateMenuTemplate()
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

        private static ControlTemplate CreateMenuItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "itemBorder";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(MenuItem.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(MenuItem.PaddingProperty));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            template.VisualTree = bd;

            var hi = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hi.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x21, 0x18)), "itemBorder"));
            hi.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(hi);

            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x21, 0x18)), "itemBorder"));
            kf.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(kf);

            return template;
        }

        private void SessionChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border border) return;
            border.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                14, 14);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Kayıt klasörünü seçin"
            };
            if (!string.IsNullOrWhiteSpace(TxtFolder.Text) && Directory.Exists(TxtFolder.Text))
                dialog.InitialDirectory = TxtFolder.Text;

            if (dialog.ShowDialog() == true)
                TxtFolder.Text = dialog.FolderName;
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_started && _engine != null && _item != null
                && (_engine.IsPaused || _item.IsPausedState || _item.IsErrorState))
            {
                BtnPause.IsEnabled = true;
                BtnStart.IsEnabled = false;
                BtnStart.Content = "Başlat";
                BtnStart.Opacity = 0.55;
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "Devam ediyor...";
                await _host.ResumeFromSessionAsync(_item, _engine);
                return;
            }

            if (_started) return;

            string folder = TxtFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                InfoDialog.Show(_host, "Uyarı", "Kayıt klasörü seçin.");
                return;
            }

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _started = true;
            SetFolderPassive(true);
            BtnStart.IsEnabled = false;
            BtnStart.Opacity = 0.55;
            BtnPause.IsEnabled = true;
            BtnCancelDl.IsEnabled = true;
            BtnPause.Content = "Duraklat";
            TxtStatus.Visibility = Visibility.Visible;
            TxtStatus.Text = "İndiriliyor...";

            var run = _host.BeginDownloadFromSession(_url, _fileName, folder);
            _item = run.Item;
            _engine = run.Engine;
            _host.RegisterSessionWindow(_item, this);
            _item.PropertyChanged += ItemOnPropertyChanged;
            SyncFromItem();
        }

        public void RefreshFromHost()
        {
            Dispatcher.BeginInvoke(SyncFromItem);
        }

        private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
            => Dispatcher.BeginInvoke(SyncFromItem);

        private void SyncFromItem()
        {
            if (_item == null) return;

            TxtSize.Text = _item.FileSize;
            TxtSpeed.Text = string.IsNullOrWhiteSpace(_item.CurrentSpeed) ? "—" : _item.CurrentSpeed;
            BarProgress.Value = _item.ProgressValue;
            TxtPercent.Text = $"%{_item.ProgressValue:F0}";

            if (_item.Status.Contains("Tamamland", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "İndirme tamamlandı";
                PanelActive.Visibility = Visibility.Collapsed;
                PanelDone.Visibility = Visibility.Visible;
                BtnMoveFile.Visibility = Visibility.Visible;
                SetFolderPassive(true);
            }
            else if (_item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "İptal edildi";
                BtnPause.IsEnabled = false;
                BtnCancelDl.IsEnabled = true;
                BtnStart.IsEnabled = false;
                BtnMoveFile.Visibility = Visibility.Collapsed;
                SetFolderPassive(true);
            }
            else if (_item.Status.Contains("Duraklat", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "Duraklatıldı";
                BtnPause.IsEnabled = false;
                BtnStart.IsEnabled = true;
                BtnStart.Opacity = 1;
                BtnStart.Content = "Devam Et";
                BtnCancelDl.IsEnabled = true;
                BtnMoveFile.Visibility = Visibility.Collapsed;
                SetFolderPassive(true);
                PanelActive.Visibility = Visibility.Visible;
                PanelDone.Visibility = Visibility.Collapsed;
            }
            else if (_item.IsDownloading)
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = string.IsNullOrWhiteSpace(_item.StatusText) ? "İndiriliyor..." : _item.StatusText;
                BtnPause.IsEnabled = true;
                BtnPause.Content = "Duraklat";
                BtnStart.IsEnabled = false;
                BtnStart.Opacity = 0.55;
                BtnStart.Content = "Başlat";
                BtnCancelDl.IsEnabled = true;
                BtnMoveFile.Visibility = Visibility.Collapsed;
                SetFolderPassive(true);
                PanelActive.Visibility = Visibility.Visible;
                PanelDone.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnMoveFile.Visibility = Visibility.Collapsed;
                SetFolderPassive(_started);
            }
        }

        private void SetFolderPassive(bool passive)
        {
            TxtFolder.IsEnabled = !passive;
            BtnBrowse.IsEnabled = !passive;
            TxtFolder.IsHitTestVisible = !passive;
            BtnBrowse.IsHitTestVisible = !passive;
            TxtFolder.Cursor = passive ? Cursors.Arrow : Cursors.IBeam;
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_engine == null || _item == null) return;
            _host.PauseFromSession(_item, _engine);
            BtnPause.IsEnabled = false;
            BtnStart.IsEnabled = true;
            BtnStart.Content = "Devam Et";
            TxtStatus.Text = "Duraklatıldı";
        }

        private void BtnCancelDl_Click(object sender, RoutedEventArgs e)
        {
            // Baslatilmadiysa sadece pencereyi kapat
            if (!_started || _engine == null || _item == null)
            {
                Close();
                return;
            }

            _host.CancelFromSession(_item, _engine);
            TxtStatus.Text = "İptal edildi";
            BtnPause.IsEnabled = false;
            BtnStart.IsEnabled = false;
            Close();
        }

        private Point _moveDragStart;

        private void BtnMoveFile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _moveDragStart = e.GetPosition(null);
        }

        private void BtnMoveFile_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _item == null) return;
            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _moveDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _moveDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var list = new List<DownloadItem> { _item };
            var data = new DataObject();
            if (File.Exists(_item.FilePath))
                data.SetData(DataFormats.FileDrop, new[] { _item.FilePath });
            data.SetData("DownloadItems", list);
            data.SetData("DownloadItem", _item);
            DragDrop.DoDragDrop(BtnMoveFile, data, DragDropEffects.Copy | DragDropEffects.Move);
            e.Handled = true;
        }

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || !File.Exists(_item.FilePath)) return;
            try { Process.Start(new ProcessStartInfo(_item.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { InfoDialog.Show(_host, "Hata", ex.Message); }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null) return;
            string path = _item.FilePath;
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { InfoDialog.Show(_host, "Hata", ex.Message); }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null) return;
            if (!ConfirmDialog.Show(this, "Silme onayı",
                    $"“{_item.FileName}” silinsin mi?",
                    "Dosya listeden ve diskten kaldırılır.",
                    confirmText: "Sil", danger: true))
                return;

            _host.DeleteItemFromSession(_item);
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_item != null)
            {
                _item.PropertyChanged -= ItemOnPropertyChanged;
                _host.UnregisterSessionWindow(_item, this);
            }
            base.OnClosed(e);
        }
    }
}
