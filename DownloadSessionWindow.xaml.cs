using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;

namespace DownloadMuck
{
    public partial class DownloadSessionWindow : Window
    {
        private readonly MainWindow _host;
        private readonly string _url;
        private string _fileName;
        private DownloadItem? _item;
        private ITransferBackend? _engine;
        private bool _started;
        private TorrentFetchMode _torrentMode = TorrentFetchMode.FullContent;

        public DownloadItem? BoundItem => _item;
        public string SessionUrl => _url;
        public bool HasStarted => _started;

        public void ApplyThemeSurface(bool light)
        {
            var card = light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1B, 0x1B, 0x1B);
            var title = light ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x22, 0x22, 0x22);
            var border = light ? Color.FromRgb(0xD8, 0xD8, 0xDE) : Color.FromRgb(0x33, 0x33, 0x33);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);
            var muted = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0x88, 0x88, 0x88);
            var input = light ? Color.FromRgb(0xF0, 0xF0, 0xF3) : Color.FromRgb(0x25, 0x25, 0x25);
            var soft = light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x2D, 0x2D, 0x2D);
            // Açık: hover koyu; koyu: gri vurgu (siyah değil)
            var softHover = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0x40, 0x40, 0x40);
            var softHoverFg = light ? Colors.White : Color.FromRgb(0xF0, 0xF0, 0xF0);
            var softFg = light ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            var track = light ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x2A, 0x2A, 0x2A);

            if (SessionChrome != null)
            {
                SessionChrome.Background = Brush(card);
                SessionChrome.BorderBrush = Brush(border);
            }
            if (SessionTitleBar != null)
                SessionTitleBar.Background = Brush(title);

            TxtTitleFile.Foreground = Brush(text);
            TxtFileName.Foreground = Brush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xEE, 0xEE, 0xEE));
            TxtFileType.Foreground = Brush(muted);
            TxtSize.Foreground = Brush(light ? Color.FromRgb(0x22, 0x22, 0x22) : Color.FromRgb(0xDD, 0xDD, 0xDD));
            TxtSpeed.Foreground = Brush(light ? Color.FromRgb(0x22, 0x22, 0x22) : Color.FromRgb(0xDD, 0xDD, 0xDD));
            TxtStatus.Foreground = Brush(muted);
            TxtPercent.Foreground = Brush(muted);

            TxtUrl.Background = Brush(input);
            TxtUrl.Foreground = Brush(light ? Color.FromRgb(0x44, 0x44, 0x44) : Color.FromRgb(0x99, 0x99, 0x99));
            TxtUrl.BorderBrush = Brush(border);

            // Pasif klasör: koyu #1A1A1A yerine temaya uygun gri (beyazda yazı kaybolmasın)
            bool folderPassive = !TxtFolder.IsEnabled;
            TxtFolder.Background = Brush(folderPassive
                ? (light ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x22, 0x22, 0x22))
                : input);
            TxtFolder.Foreground = Brush(folderPassive
                ? (light ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0x99, 0x99, 0x99))
                : text);
            TxtFolder.BorderBrush = Brush(border);
            BarProgress.Background = Brush(track);

            foreach (var tb in FindNamedLabels())
                tb.Foreground = Brush(muted);

            foreach (var btn in new[] { BtnBrowse, BtnPause })
            {
                if (btn == null) continue;
                btn.ClearValue(Control.ForegroundProperty);
            }

            RemapSoftButtonTemplates(soft, softHover, softFg, softHoverFg, light);

            TxtUrl.ContextMenu = BuildEditMenu(light);
            TxtFolder.ContextMenu = BuildEditMenu(light);

            if (TorrentPickCard != null)
            {
                TorrentPickCard.Background = Brush(light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1E, 0x1E, 0x1E));
                TorrentPickCard.BorderBrush = Brush(border);
            }
            if (TorrentPickOverlay != null)
                TorrentPickOverlay.Background = Brush(light
                    ? Color.FromArgb(0x88, 0, 0, 0)
                    : Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1A));
            if (TxtTorrentPickTitle != null) TxtTorrentPickTitle.Foreground = Brush(text);
            if (TxtTorrentPickHint != null) TxtTorrentPickHint.Foreground = Brush(muted);
            if (RbTorrentFile != null) RbTorrentFile.Foreground = Brush(text);
            if (RbFullContent != null) RbFullContent.Foreground = Brush(text);
            if (TxtRbTorrentFile != null) TxtRbTorrentFile.Foreground = Brush(text);
            if (TxtRbFullContent != null) TxtRbFullContent.Foreground = Brush(text);
            if (TxtTorrentFileSize != null) TxtTorrentFileSize.Foreground = Brush(muted);
            if (TxtFullContentSize != null) TxtFullContentSize.Foreground = Brush(muted);
            StyleTorrentPickButton(BtnTorrentPickOk, light);
            StyleTorrentPickRadios(light);
        }

        private void StyleTorrentPickRadios(bool light)
        {
            var bullet = light ? Color.FromRgb(0x88, 0x88, 0x90) : Color.FromRgb(0x66, 0x66, 0x66);
            var bulletChecked = Color.FromRgb(0xFF, 0x6B, 0x00);
            var hoverBg = light ? Color.FromRgb(0xF0, 0xF0, 0xF2) : Color.FromRgb(0x2A, 0x2A, 0x2A);

            void Apply(RadioButton? rb)
            {
                if (rb == null) return;
                rb.FocusVisualStyle = null;
                rb.Template = CreateTorrentPickRadioTemplate(bullet, bulletChecked, hoverBg);
            }

            Apply(RbTorrentFile);
            Apply(RbFullContent);
        }

        private static ControlTemplate CreateTorrentPickRadioTemplate(Color bullet, Color bulletChecked, Color hoverBg)
        {
            var template = new ControlTemplate(typeof(RadioButton));
            var root = new FrameworkElementFactory(typeof(Border));
            root.Name = "root";
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            root.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var ellipse = new FrameworkElementFactory(typeof(Ellipse));
            ellipse.Name = "bullet";
            ellipse.SetValue(FrameworkElement.WidthProperty, 14.0);
            ellipse.SetValue(FrameworkElement.HeightProperty, 14.0);
            ellipse.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 8, 0));
            ellipse.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            ellipse.SetValue(Shape.StrokeProperty, new SolidColorBrush(bullet));
            ellipse.SetValue(Shape.StrokeThicknessProperty, 1.5);
            ellipse.SetValue(Shape.FillProperty, Brushes.Transparent);
            stack.AppendChild(ellipse);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            stack.AppendChild(content);

            root.AppendChild(stack);
            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverBg), "root"));
            template.Triggers.Add(hover);

            var selected = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
            selected.Setters.Add(new Setter(Shape.StrokeProperty, new SolidColorBrush(bulletChecked), "bullet"));
            selected.Setters.Add(new Setter(Shape.FillProperty, new SolidColorBrush(bulletChecked), "bullet"));
            template.Triggers.Add(selected);

            return template;
        }

        private void StyleTorrentPickButton(Button? btn, bool light)
        {
            if (btn == null) return;
            var accent = Color.FromRgb(0xFF, 0x6B, 0x00);
            var accentHover = Color.FromRgb(0xFF, 0x85, 0x33);
            var fg = Colors.White;
            btn.ClearValue(Control.ForegroundProperty);
            btn.Foreground = Brush(fg);
            var template = new ControlTemplate(typeof(Button));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "bd";
            factory.SetValue(Border.BackgroundProperty, Brush(accent));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            factory.SetValue(Border.PaddingProperty, new Thickness(14, 0, 14, 0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            factory.AppendChild(cp);
            template.VisualTree = factory;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(accentHover), "bd"));
            template.Triggers.Add(over);
            btn.Template = template;
        }

        private IEnumerable<TextBlock> FindNamedLabels()
        {
            // "Adres", "Kayıt klasörü", "Boyut", "Hız" etiketleri
            if (SessionChrome == null) yield break;
            foreach (var tb in EnumerateTextBlocks(SessionChrome))
            {
                string t = tb.Text ?? "";
                if (t is "Adres" or "Kayıt klasörü" or "Boyut" or "Hız")
                    yield return tb;
            }
        }

        private static IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject root)
        {
            // Logical tree — Loaded öncesi VisualTree boş olabilir
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject d) continue;
                if (child is TextBlock tb) yield return tb;
                foreach (var nested in EnumerateTextBlocks(d))
                    yield return nested;
            }
        }

        private void RemapSoftButtonTemplates(Color soft, Color softHover, Color softFg, Color softHoverFg, bool light)
        {
            void StyleSoft(Button? btn, bool danger)
            {
                if (btn == null) return;
                var bg = danger
                    ? (light ? Color.FromRgb(0xFF, 0xEB, 0xEE) : Color.FromRgb(0x3A, 0x1C, 0x1C))
                    : soft;
                var hover = danger
                    ? (light ? Color.FromRgb(0xFF, 0xCD, 0xD2) : Color.FromRgb(0x5A, 0x28, 0x28))
                    : softHover;
                var fg = danger
                    ? (light ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromRgb(0xFF, 0x6B, 0x6B))
                    : softFg;
                var hoverFg = danger ? fg : softHoverFg;

                btn.ClearValue(Control.ForegroundProperty);
                btn.ClearValue(FrameworkElement.StyleProperty);
                var style = new Style(typeof(Button));
                style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(fg)));
                btn.Style = style;

                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.Name = "bd";
                factory.SetValue(Border.BackgroundProperty, Brush(bg));
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                cp.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
                factory.AppendChild(cp);
                template.VisualTree = factory;

                var over = new MultiTrigger();
                over.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
                over.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true));
                over.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hover), "bd"));
                over.Setters.Add(new Setter(Control.ForegroundProperty, Brush(hoverFg)));
                template.Triggers.Add(over);

                var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
                template.Triggers.Add(disabled);

                btn.Template = template;
            }

            StyleSoft(BtnBrowse, false);
            StyleSoft(BtnPause, false);
            StyleSoft(BtnCancelDl, true);
            if (PanelDone != null)
            {
                foreach (var child in PanelDone.Children)
                {
                    if (child is not Button b) continue;
                    string c = b.Content?.ToString() ?? "";
                    if (c is "Klasör" or "Kapat") StyleSoft(b, false);
                    else if (c == "Sil") StyleSoft(b, true);
                }
            }
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public DownloadSessionWindow(MainWindow host, string url, string fileName, string defaultFolder, string sizeLabel)
        {
            InitializeComponent();
            _host = host;
            _url = url;
            _fileName = fileName;
            ApplyMeta(fileName, url, defaultFolder, sizeLabel);
            ThemeService.ApplyToWindow(this);
            Background = Brushes.Transparent;
            Loaded += SessionWindow_Loaded;
        }

        private async void SessionWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_started || _item != null) return;
            var kind = UrlClassifier.Classify(_url);
            if (kind is not TransferKind.Torrent and not TransferKind.Magnet)
                return;

            BtnStart.IsEnabled = false;
            TorrentPickOverlay.Visibility = Visibility.Visible;

            if (kind == TransferKind.Magnet)
                RbTorrentFile.Visibility = Visibility.Collapsed;

            await LoadTorrentPickerSizesAsync(kind);
        }

        private async Task LoadTorrentPickerSizesAsync(TransferKind kind)
        {
            try
            {
                long torrentFileBytes = 0;
                if (kind == TransferKind.Torrent)
                {
                    torrentFileBytes = await TryGetTorrentFileByteSizeAsync(_url);
                    TxtTorrentFileSize.Text = torrentFileBytes > 0
                        ? MainWindow.FormatFileSize(torrentFileBytes)
                        : "Boyut bilinmiyor";
                }

                var peek = await TorrentPeek.TryDescribeAsync(_url);
                long fullBytes = peek?.Size ?? 0;
                TxtFullContentSize.Text = fullBytes > 0
                    ? MainWindow.FormatFileSize(fullBytes)
                    : kind == TransferKind.Magnet
                        ? "Bağlantı kurulduktan sonra netleşir"
                        : "Boyut bilinmiyor";

                if (kind == TransferKind.Torrent && torrentFileBytes <= 0 && fullBytes > 0)
                    RbFullContent.IsChecked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TorrentPick sizes: {ex.Message}");
                TxtTorrentFileSize.Text = "Boyut bilinmiyor";
                TxtFullContentSize.Text = "Boyut bilinmiyor";
            }
        }

        private static async Task<long> TryGetTorrentFileByteSizeAsync(string url)
        {
            if (UrlClassifier.Classify(url) != TransferKind.Torrent)
                return 0;
            try
            {
                using var client = TransferHttp.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await client.SendAsync(req).ConfigureAwait(true);
                if (resp.Content.Headers.ContentLength is > 0 and long len)
                    return len;
            }
            catch { /* HEAD desteklenmeyebilir */ }

            return 0;
        }

        private void BtnTorrentPickOk_Click(object sender, RoutedEventArgs e)
        {
            _torrentMode = RbTorrentFile.IsChecked == true && RbTorrentFile.Visibility == Visibility.Visible
                ? TorrentFetchMode.TorrentFileOnly
                : TorrentFetchMode.FullContent;

            if (_torrentMode == TorrentFetchMode.TorrentFileOnly)
            {
                string name = _fileName;
                if (!name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(name) + ".torrent";
                ApplyResolvedMeta(name, TxtTorrentFileSize.Text);
            }
            else if (!string.IsNullOrWhiteSpace(TxtFullContentSize.Text))
            {
                ApplyResolvedMeta(_fileName, TxtFullContentSize.Text);
            }

            TorrentPickOverlay.Visibility = Visibility.Collapsed;
            BtnStart.IsEnabled = true;
        }

        /// <summary>Mevcut indirmeye bagli oturum (cift tik).</summary>
        public DownloadSessionWindow(MainWindow host, DownloadItem item, ITransferBackend? engine, string url)
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
            ThemeService.ApplyToWindow(this);
            Background = Brushes.Transparent;

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
            UpdateTitleBar();
        }

        private void UpdateTitleBar()
        {
            string name = !string.IsNullOrWhiteSpace(_item?.FileName) ? _item!.FileName : _fileName;
            if (string.IsNullOrWhiteSpace(name)) name = "İndirme";

            TxtTitleFile.Text = name;

            if (_started || (_item != null && _item.ProgressValue > 0))
            {
                double pct = _item?.ProgressValue ?? 0;
                TxtTitlePercent.Text = $"%{pct:F0}";
                Title = $"{name}  %{pct:F0}";
            }
            else
            {
                TxtTitlePercent.Text = "";
                Title = name;
            }
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
            if (string.IsNullOrWhiteSpace(fileName)) return;

            if (FileNameHelper.IsBetterName(fileName, _fileName))
            {
                _fileName = fileName;
                TxtFileName.Text = fileName;
                TxtFileType.Text = FileNameHelper.FormatTypeLabel(fileName);
                ImgIcon.Source = IconHelper.GetIconForExtension(fileName);
            }

            if (IsKnownSizeLabel(sizeLabel))
                TxtSize.Text = sizeLabel!;

            UpdateTitleBar();
        }

        private static bool IsKnownSizeLabel(string? sizeLabel)
        {
            if (string.IsNullOrWhiteSpace(sizeLabel) || sizeLabel is "-" or "—")
                return false;
            if (sizeLabel.Contains("netleşir", StringComparison.OrdinalIgnoreCase))
                return false;
            return !string.Equals(sizeLabel, "Boyut bilinmiyor", StringComparison.OrdinalIgnoreCase);
        }

        public void ApplyDefaultFolderIfIdle(string folder)
        {
            if (_started || string.IsNullOrWhiteSpace(folder)) return;
            TxtFolder.Text = folder;
        }

        public void RebindEngine(ITransferBackend engine)
        {
            _engine = engine;
        }

        private ContextMenu BuildEditMenu(bool? lightOverride = null)
        {
            bool light = lightOverride ?? ThemeService.IsLight;
            var menuBg = light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1C, 0x1C, 0x1C);
            var menuBorder = light ? Color.FromRgb(0xD8, 0xD8, 0xDE) : Color.FromRgb(0x33, 0x33, 0x33);
            var menuText = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xE0, 0xE0, 0xE0);
            var hoverBg = light ? Color.FromRgb(0xF0, 0xF0, 0xF2) : Color.FromRgb(0x2A, 0x21, 0x18);
            var sep = light ? Color.FromRgb(0xE4, 0xE4, 0xE8) : Color.FromRgb(0x33, 0x33, 0x33);

            var menu = new ContextMenu
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null
            };
            menu.Template = CreateMenuTemplate(menuBg, menuBorder);
            var itemTemplate = CreateMenuItemTemplate(hoverBg);

            MenuItem Make(string header, RoutedUICommand cmd) => new()
            {
                Header = header,
                Command = cmd,
                Foreground = Brush(menuText),
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
            sepFactory.SetValue(Border.BackgroundProperty, Brush(sep));
            sepFactory.SetValue(Border.MarginProperty, new Thickness(6, 3, 6, 3));
            menu.Items.Add(new Separator { Template = new ControlTemplate(typeof(Separator)) { VisualTree = sepFactory } });
            menu.Items.Add(Make("Tümünü seç", ApplicationCommands.SelectAll));
            return menu;
        }

        private static ControlTemplate CreateMenuTemplate(Color bg, Color border)
        {
            var template = new ControlTemplate(typeof(ContextMenu));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, Brush(bg));
            factory.SetValue(Border.BorderBrushProperty, Brush(border));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            factory.SetValue(Border.PaddingProperty, new Thickness(4));
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(Panel.IsItemsHostProperty, true);
            factory.AppendChild(panel);
            template.VisualTree = factory;
            return template;
        }

        private static ControlTemplate CreateMenuItemTemplate(Color hoverBg)
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
            hi.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hoverBg), "itemBorder"));
            hi.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brush(Color.FromRgb(0xFF, 0x6B, 0x00))));
            template.Triggers.Add(hi);

            var kf = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            kf.Setters.Add(new Setter(Border.BackgroundProperty, Brush(hoverBg), "itemBorder"));
            kf.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brush(Color.FromRgb(0xFF, 0x6B, 0x00))));
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

            BtnStart.IsEnabled = false;
            BtnStart.Opacity = 0.55;
            TxtStatus.Visibility = Visibility.Visible;
            TxtStatus.Text = "Dosya bilgisi alınıyor...";

            try
            {
                var (resolvedName, sizeLabel) = await _host.ResolveDownloadMetaAsync(_url, _fileName, null)
                    .ConfigureAwait(true);
                if (FileNameHelper.IsBetterName(resolvedName, _fileName))
                    ApplyResolvedMeta(resolvedName, sizeLabel);
                else if (IsKnownSizeLabel(sizeLabel))
                    ApplyResolvedMeta(_fileName, sizeLabel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pre-start meta: {ex.Message}");
            }

            _started = true;
            SetFolderPassive(true);
            BtnStart.IsEnabled = false;
            BtnPause.IsEnabled = true;
            BtnCancelDl.IsEnabled = true;
            BtnPause.Content = "Duraklat";
            TxtStatus.Text = "İndiriliyor...";

            var run = _host.BeginDownloadFromSession(_url, _fileName, folder,
                torrentMode: _torrentMode, sizeHint: TxtSize.Text);
            if (run == null)
            {
                _started = false;
                BtnStart.IsEnabled = true;
                BtnStart.Opacity = 1;
                BtnPause.IsEnabled = false;
                SetFolderPassive(false);
                TxtStatus.Text = "Kural nedeniyle eklenmedi";
                return;
            }
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

            if (!string.IsNullOrWhiteSpace(_item.FileName))
            {
                TxtFileName.Text = _item.FileName;
                TxtFileType.Text = FileNameHelper.FormatTypeLabel(_item.FileName);
                if (_item.FileIcon != null)
                    ImgIcon.Source = _item.FileIcon;
            }
            UpdateTitleBar();

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
            else if (_item.Status.Contains("Kuyrukta", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = "Kuyrukta bekleniyor...";
                BtnPause.IsEnabled = false;
                BtnStart.IsEnabled = false;
                BtnStart.Opacity = 0.55;
                BtnStart.Content = "Başlat";
                BtnCancelDl.IsEnabled = true;
                BtnMoveFile.Visibility = Visibility.Collapsed;
                SetFolderPassive(true);
                PanelActive.Visibility = Visibility.Visible;
                PanelDone.Visibility = Visibility.Collapsed;
            }
            else if (_item.IsErrorState)
            {
                TxtStatus.Visibility = Visibility.Visible;
                TxtStatus.Text = string.IsNullOrWhiteSpace(_item.Status) ? "Torrent hatası" : _item.Status;
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

            bool light = ThemeService.IsLight;
            var input = light ? Color.FromRgb(0xF0, 0xF0, 0xF3) : Color.FromRgb(0x25, 0x25, 0x25);
            var text = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);
            var border = light ? Color.FromRgb(0xD8, 0xD8, 0xDE) : Color.FromRgb(0x33, 0x33, 0x33);
            TxtFolder.Background = Brush(passive
                ? (light ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x22, 0x22, 0x22))
                : input);
            TxtFolder.Foreground = Brush(passive
                ? (light ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0x99, 0x99, 0x99))
                : text);
            TxtFolder.BorderBrush = Brush(border);
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
            string fileLabel = !string.IsNullOrWhiteSpace(_item?.FileName) ? _item!.FileName : _fileName;
            bool ok = ConfirmDialog.Show(
                this,
                DialogTexts.CancelDownloadTitle,
                DialogTexts.CancelDownloadMessage,
                DialogTexts.CancelDownloadDetail(fileLabel),
                confirmText: DialogTexts.CancelDownloadConfirm,
                cancelText: DialogTexts.CancelDownloadDismiss,
                danger: true,
                forceFloating: true);

            if (!ok) return;

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

            // Mini ekrana özel onay — ana uygulamadaki modal'a gitmez
            bool fromDisk = _host.DeletesFilesFromDisk;
            bool ok = ConfirmDialog.Show(
                this,
                DialogTexts.DeleteTitle,
                DialogTexts.DeleteMessage(_item.FileName),
                DialogTexts.DeleteDetail(fromDisk, 1),
                confirmText: DialogTexts.DeleteConfirm,
                cancelText: DialogTexts.DismissAlt,
                danger: true,
                forceFloating: true);

            if (!ok) return;

            try
            {
                _host.DeleteItemFromSession(_item);
            }
            catch (Exception ex)
            {
                InfoDialog.Show(this, "Silinemedi", ex.Message);
                return;
            }

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
