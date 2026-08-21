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
        private readonly string _fileName;
        private DownloadItem? _item;
        private DownloadEngine? _engine;
        private bool _started;

        public DownloadItem? BoundItem => _item;

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

        private static ContextMenu BuildEditMenu()
        {
            var menu = new ContextMenu { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            menu.Template = CreateMenuTemplate();
            void StyleItem(MenuItem mi)
            {
                mi.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
                mi.Background = Brushes.Transparent;
                mi.Padding = new Thickness(12, 8, 12, 8);
            }
            var cut = new MenuItem { Header = "Kes", Command = ApplicationCommands.Cut };
            var copy = new MenuItem { Header = "Kopyala", Command = ApplicationCommands.Copy };
            var paste = new MenuItem { Header = "Yapıştır", Command = ApplicationCommands.Paste };
            var selectAll = new MenuItem { Header = "Tümünü seç", Command = ApplicationCommands.SelectAll };
            foreach (var mi in new[] { cut, copy, paste, selectAll }) StyleItem(mi);
            menu.Items.Add(cut);
            menu.Items.Add(copy);
            menu.Items.Add(paste);
            menu.Items.Add(new Separator());
            menu.Items.Add(selectAll);
            // Sag tikta kopyala her zaman gorunsun
            copy.IsEnabled = true;
            return menu;
        }

        private static ControlTemplate CreateMenuTemplate()
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
                MessageBox.Show("Kayıt klasörü seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                SetFolderPassive(true);
            }
            else if (_item.Status.Contains("İptal", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "İptal edildi";
                BtnPause.IsEnabled = false;
                BtnCancelDl.IsEnabled = true;
                BtnStart.IsEnabled = false;
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
                SetFolderPassive(true);
                PanelActive.Visibility = Visibility.Visible;
                PanelDone.Visibility = Visibility.Collapsed;
            }
            else
            {
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

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || !File.Exists(_item.FilePath)) return;
            try { Process.Start(new ProcessStartInfo(_item.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
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
            catch (Exception ex) { MessageBox.Show(ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null) return;
            if (!ConfirmDialog.Show(this, "Silme onayı",
                    $"“{_item.FileName}” silinsin mi?",
                    "Dosya listeden ve diskten kaldırılır."))
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
