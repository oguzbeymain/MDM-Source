using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MDM
{
    /// <summary>
    /// Native koyu tema. Açık tema yalnızca DynamicResource + adlandırılmış chrome ile uygulanır
    /// (ağaç remapping yok — hover/stil bozulmaz, donma olmaz).
    /// </summary>
    public static class ThemeService
    {
        private static bool _appliedLight;

        /// <summary>Ayarlar önizlemesi sırasında geçici parlaklık (Kaydet/İptal sonrası temizlenir).</summary>
        public static int? PreviewLightBrightness { get; set; }

        public static int EffectiveLightBrightness => LightSurfaceDimmer.EffectiveBrightness;

        public static bool IsLight =>
            string.Equals(AppSettingsStore.Load().Theme, "Light", StringComparison.OrdinalIgnoreCase);

        public static Color Surface(bool light, byte lr, byte lg, byte lb, byte dr, byte dg, byte db)
            => light ? LightSurfaceDimmer.DimSurface(lr, lg, lb) : C(dr, dg, db);

        public static void ApplyFromSettings()
        {
            PreviewLightBrightness = null;
            Apply(AppSettingsStore.Load().Theme);
        }

        public static void Apply(string? theme)
        {
            bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
            var app = Application.Current;
            if (app == null) return;

            SetBrush(app.Resources, "ThemeBg", light ? C(0xF5, 0xF5, 0xF7) : C(0x12, 0x12, 0x12));
            SetBrush(app.Resources, "ThemeAccent", C(0xFF, 0x6B, 0x00));
            ApplyGlobalCheckBoxBrushes(app.Resources, light);

            foreach (Window w in app.Windows)
                ApplyToWindow(w, light);

            _appliedLight = light;
        }

        public static void ApplyToWindow(Window window, bool? lightOverride = null)
        {
            bool light = lightOverride ?? IsLight;
            ApplyWindowShell(window, light);
            NativeWindowChrome.ApplyNoOuterShadow(window);
            if (window is MainWindow mw)
                ApplyMainWindow(mw, light);
            else if (window is DownloadSessionWindow session)
                session.ApplyThemeSurface(light);
            else if (window is ConfirmDialog confirm)
                confirm.ApplyThemeSurface(light);
            else if (window is CategoryCreateDialog categoryCreate)
                categoryCreate.ApplyThemeSurface(light);
        }

        private static void ApplyWindowShell(Window window, bool light)
        {
            // AllowsTransparency pencerelerde opak Background köşe taşması yapar
            if (window.AllowsTransparency)
                window.Background = Brushes.Transparent;
            else
                window.Background = BrushOf(light ? Sf(0xF5, 0xF5, 0xF7) : C(0x12, 0x12, 0x12));
            window.Foreground = BrushOf(light ? C(0x1A, 0x1A, 0x1A) : C(0xE0, 0xE0, 0xE0));

            var rd = window.Resources;
            SetBrush(rd, "BgDark", light ? Sf(0xF5, 0xF5, 0xF7) : C(0x12, 0x12, 0x12));
            SetBrush(rd, "SidebarDark", light ? Sf(0xF0, 0xF0, 0xF3) : C(0x18, 0x18, 0x18));
            SetBrush(rd, "CardDark", light ? Sf(0xFF, 0xFF, 0xFF) : C(0x1E, 0x1E, 0x1E));
            SetBrush(rd, "InputDark", light ? Sf(0xF0, 0xF0, 0xF3) : C(0x26, 0x26, 0x26));

            // Dinamik yüzeyler — stiller/menüler bunları kullanır
            SetBrush(rd, "SidebarTextBrush", light ? C(0x2E, 0x2E, 0x2E) : C(0xB0, 0xB0, 0xB0));
            SetBrush(rd, "ToolbarTextBrush", light ? C(0x1A, 0x1A, 0x1A) : C(0xC8, 0xC8, 0xC8));
            SetBrush(rd, "HeaderBgBrush", light ? Sf(0xF3, 0xF3, 0xF5) : C(0x1E, 0x1E, 0x1E));
            SetBrush(rd, "HeaderTextBrush", light ? C(0x44, 0x44, 0x44) : C(0x88, 0x88, 0x88));
            // Hover: açık temada turuncu çerçeve vurgusu
            SetBrush(rd, "SidebarHoverBrush", C(0xFF, 0x6B, 0x00));
            SetBrush(rd, "SidebarHoverBrightBrush", C(0xFF, 0x85, 0x33));
            SetBrush(rd, "ToolbarHoverBgBrush", Colors.Transparent);
            SetBrush(rd, "NavTextBrush", light ? C(0x3A, 0x3A, 0x3A) : C(0xAA, 0xAA, 0xAA));
            SetBrush(rd, "NavHoverBrush", C(0xFF, 0x6B, 0x00));
            // Sol nav seçim — soft charcoal (kategori + ayarlar ortak dil)
            SetBrush(rd, "NavSelectedBgBrush", light ? Sf(0xEE, 0xEE, 0xF0) : C(0x25, 0x25, 0x25));
            SetBrush(rd, "CategorySelectedBgBrush", light ? Colors.Transparent : C(0x25, 0x25, 0x25));
            SetBrush(rd, "CheckBoxIdleBgBrush", light ? Sf(0xFF, 0xFF, 0xFF) : C(0x2A, 0x2A, 0x2A));
            SetBrush(rd, "CheckBoxIdleBorderBrush", light ? Sf(0xC0, 0xC0, 0xC6) : C(0x55, 0x55, 0x55));
            // Tik zemini — koyu siyah yerine yumuşak gri; açık temada soft charcoal
            SetBrush(rd, "CheckBoxCheckedFillBrush", light ? Sf(0x5A, 0x5A, 0x62) : C(0x3E, 0x3E, 0x3E));
            SetBrush(rd, "CheckBoxCheckedBorderBrush", light ? Sf(0x88, 0x88, 0x90) : C(0x66, 0x66, 0x66));
            SetBrush(rd, "CheckBoxHoverBorderBrush", light ? Sf(0x99, 0x99, 0xA0) : C(0x77, 0x77, 0x77));

            // Liste satırları — seçili: açıkta soft turuncu, koyuda #1A1A1A
            SetBrush(rd, "RowTextBrush", light ? C(0x1A, 0x1A, 0x1A) : C(0xE0, 0xE0, 0xE0));
            SetBrush(rd, "RowHoverBgBrush", light ? Sf(0xF0, 0xF0, 0xF2) : C(0x22, 0x22, 0x22));
            SetBrush(rd, "RowHoverFgBrush", light ? C(0x1A, 0x1A, 0x1A) : C(0xE0, 0xE0, 0xE0));
            SetBrush(rd, "RowSelectedBgBrush", light ? Sf(0xEE, 0xEE, 0xF0) : C(0x25, 0x25, 0x25));
            SetBrush(rd, "RowSelectedFgBrush", C(0xFF, 0x6B, 0x00));
            SetBrush(rd, "RowSelectedHoverBgBrush", light ? Sf(0xE4, 0xE4, 0xE8) : C(0x2C, 0x2C, 0x2C));
            SetBrush(rd, "RowSelectedHoverFgBrush", light ? C(0xE0, 0x55, 0x00) : C(0xFF, 0x85, 0x33));
            SetBrush(rd, "RowActionChromeBgBrush", light ? Sf(0xF0, 0xF0, 0xF3) : C(0x1A, 0x1A, 0x1A));
            SetBrush(rd, "RowActionChromeBorderBrush", light ? Sf(0xD8, 0xD8, 0xDE) : C(0x2A, 0x2A, 0x2A));
            SetBrush(rd, "RowActionSepBrush", light ? Sf(0xD0, 0xD0, 0xD6) : C(0x2E, 0x2E, 0x2E));
            SetBrush(rd, "SearchBorderBrush", light ? Sf(0xD0, 0xD0, 0xD6) : C(0x2E, 0x2E, 0x2E));
            SetBrush(rd, "SearchFocusBorderBrush", light ? Sf(0xA8, 0xA8, 0xB0) : C(0x55, 0x55, 0x55));
            SetBrush(rd, "SearchHoverBorderBrush", light ? Sf(0xB8, 0xB8, 0xC0) : C(0x44, 0x44, 0x44));
            SetBrush(rd, "AccentBorderBrush", light ? C(0xFF, 0x6B, 0x00) : Colors.Transparent);
            SetBrush(rd, "HeaderLineBrush", light ? Sf(0xE4, 0xE4, 0xE8) : C(0x22, 0x22, 0x22));
            SetBrush(rd, "HeaderHoverBorderBrush", light ? C(0xFF, 0x6B, 0x00) : Colors.Transparent);
            SetBrush(rd, "HeaderHoverBgBrush", light ? Sfa(0x10, 0xFF, 0x6B, 0x00) : C(0x22, 0x22, 0x22));
            SetBrush(rd, "HeaderHoverForegroundBrush", light ? C(0x44, 0x44, 0x44) : C(0xCC, 0xCC, 0xCC));
            SetBrush(rd, "ToolbarHoverBorderBrush", Colors.Transparent);
            SetBrush(rd, "ToolbarHoverForegroundBrush", C(0xFF, 0x6B, 0x00));
            SetBrush(rd, "ToolbarPressedForegroundBrush", C(0xE5, 0x5A, 0x00));
            SetBrush(rd, "TitleBarButtonIdleBorderBrush", Colors.Transparent);
            SetBrush(rd, "TitleBarButtonHoverBorderBrush", Colors.Transparent);
            SetBrush(rd, "TitleBarButtonHoverBgBrush", C(0x40, 0x40, 0x40));
            SetBrush(rd, "TitleBarButtonHoverForegroundBrush", C(0xFF, 0x6B, 0x00));
            SetBrush(rd, "TitleBarButtonPressedBgBrush", C(0x2E, 0x2E, 0x2E));
            SetBrush(rd, "TitleBarButtonPressedForegroundBrush", C(0xE5, 0x5A, 0x00));
            SetBrush(rd, "MenuSeparatorBrush", light ? Sf(0xE4, 0xE4, 0xE8) : C(0x33, 0x33, 0x33));
            SetBrush(rd, "MarqueeStrokeBrush", Colors.Transparent);
            SetBrush(rd, "MarqueeFillBrush", light ? Sfa(0x66, 0x18, 0x18, 0x18) : C(0xCC, 0x18, 0x18, 0x18));
            SetBrush(rd, "DownloadingStatusBrush", light ? C(0x1A, 0x1A, 0x1A) : C(0xE0, 0xE0, 0xE0));
            SetBrush(rd, "DownloadingTrackBrush", light ? Sf(0xE0, 0xE0, 0xE4) : C(0x33, 0x33, 0x33));

            if (light)
            {
                SetBrush(rd, "MenuBgBrush", Sf(0xFF, 0xFF, 0xFF));
                SetBrush(rd, "MenuBorderBrush", Sf(0xD8, 0xD8, 0xDE));
                SetBrush(rd, "MenuTextBrush", C(0x1A, 0x1A, 0x1A));
                SetBrush(rd, "MenuHoverBgBrush", Sf(0xF0, 0xF0, 0xF2));
            }
            else
            {
                SetBrush(rd, "MenuBgBrush", C(0x1C, 0x1C, 0x1C));
                SetBrush(rd, "MenuBorderBrush", C(0x33, 0x33, 0x33));
                SetBrush(rd, "MenuTextBrush", C(0xE0, 0xE0, 0xE0));
                SetBrush(rd, "MenuHoverBgBrush", C(0x2A, 0x21, 0x18));
            }

            // Seçim: dark #1A1A1A + turuncu yazı; light soft turuncu
            SetSys(rd, SystemColors.HighlightBrushKey, light ? Sf(0xFF, 0xE8, 0xD6) : C(0x1A, 0x1A, 0x1A));
            SetSys(rd, SystemColors.HighlightTextBrushKey, C(0xFF, 0x6B, 0x00));
            SetSys(rd, SystemColors.InactiveSelectionHighlightBrushKey,
                light ? Sf(0xFF, 0xE8, 0xD6) : C(0x1A, 0x1A, 0x1A));
            SetSys(rd, SystemColors.InactiveSelectionHighlightTextBrushKey, C(0xFF, 0x6B, 0x00));
        }

        private static void ApplyMainWindow(MainWindow mw, bool light)
        {
            Color bg = light ? Sf(0xF5, 0xF5, 0xF7) : C(0x12, 0x12, 0x12);
            Color title = light ? Sf(0xFF, 0xFF, 0xFF) : C(0x18, 0x18, 0x18);
            Color border = light ? Sf(0xD0, 0xD0, 0xD6) : C(0x2A, 0x2A, 0x2A);
            Color sidebar = light ? Sf(0xF0, 0xF0, 0xF3) : C(0x18, 0x18, 0x18);
            Color card = light ? Sf(0xFF, 0xFF, 0xFF) : C(0x1E, 0x1E, 0x1E);
            Color text = light ? C(0x1A, 0x1A, 0x1A) : C(0xE0, 0xE0, 0xE0);
            Color muted = light ? C(0x55, 0x55, 0x55) : C(0x88, 0x88, 0x88);
            Color line = light ? Sf(0xE4, 0xE4, 0xE8) : C(0x2A, 0x2A, 0x2A);
            Color searchBg = light ? Sf(0xF0, 0xF0, 0xF3) : C(0x25, 0x25, 0x25);

            if (mw.RootChrome != null)
            {
                mw.RootChrome.Background = BrushOf(bg);
                mw.RootChrome.BorderBrush = BrushOf(border);
            }
            if (mw.TitleBarChrome != null)
                mw.TitleBarChrome.Background = BrushOf(title);
            if (mw.ContentChrome != null)
                mw.ContentChrome.Background = BrushOf(bg);
            if (mw.ContentPanel != null)
            {
                mw.ContentPanel.Background = BrushOf(light ? Sf(0xFF, 0xFF, 0xFF) : C(0x16, 0x16, 0x16));
                mw.ContentPanel.BorderBrush = BrushOf(border);
            }
            if (mw.SidebarPanel != null)
            {
                mw.SidebarPanel.Background = BrushOf(sidebar);
                mw.SidebarPanel.BorderBrush = BrushOf(border);
            }
            if (mw.ListPanelBorder != null)
            {
                mw.ListPanelBorder.Background = BrushOf(card);
                mw.ListPanelBorder.BorderBrush = BrushOf(Colors.Transparent);
                mw.ListPanelBorder.BorderThickness = new Thickness(0);
            }
            if (mw.TitleBarChromeButtons != null)
                mw.TitleBarChromeButtons.Background = BrushOf(Colors.Transparent);
            if (mw.SearchBoxBorder != null)
            {
                mw.SearchBoxBorder.Background = BrushOf(searchBg);
                // BorderBrush Style/DynamicResource’ta kalsın — local set focus trigger’ı ezer
                mw.SearchBoxBorder.ClearValue(Border.BorderBrushProperty);
            }

            if (mw.DgDownloads != null)
            {
                mw.DgDownloads.Background = BrushOf(card);
                mw.DgDownloads.Foreground = BrushOf(text);
                mw.DgDownloads.RowBackground = BrushOf(Colors.Transparent);
                mw.DgDownloads.AlternatingRowBackground = BrushOf(Colors.Transparent);
                mw.DgDownloads.HorizontalGridLinesBrush = BrushOf(line);
                mw.DgDownloads.VerticalGridLinesBrush = BrushOf(line);

                // DataGrid kendi resource sözlüğünde Highlight tutuyor — pencere brush’ı yetmez
                Color selBg = light ? Sf(0xFF, 0xE8, 0xD6) : C(0x1A, 0x1A, 0x1A);
                Color selFg = C(0xFF, 0x6B, 0x00);
                SetSys(mw.DgDownloads.Resources, SystemColors.HighlightBrushKey, selBg);
                SetSys(mw.DgDownloads.Resources, SystemColors.HighlightTextBrushKey, selFg);
                SetSys(mw.DgDownloads.Resources, SystemColors.InactiveSelectionHighlightBrushKey, selBg);
                SetSys(mw.DgDownloads.Resources, SystemColors.InactiveSelectionHighlightTextBrushKey, selFg);
            }

            if (mw.TxtSearch != null)
                mw.TxtSearch.Foreground = BrushOf(text);

            if (mw.IcoToolbarSettings != null)
                mw.IcoToolbarSettings.Foreground = BrushOf(light ? C(0xFF, 0x6B, 0x00) : C(0xC8, 0xC8, 0xC8));

            var titleBarChromeStyle = (Style)mw.FindResource(light ? "TitleBarChromeButtonStyleLight" : "TitleBarChromeButtonStyle");
            if (mw.BtnMinimize != null)
                mw.BtnMinimize.Style = titleBarChromeStyle;
            if (mw.BtnMaximize != null)
                mw.BtnMaximize.Style = titleBarChromeStyle;
            if (mw.BtnClose != null)
            {
                mw.BtnClose.Style = (Style)mw.FindResource("TitleBarCloseButtonStyle");
                mw.BtnClose.Foreground = BrushOf(Colors.White);
            }

            if (mw.TxtSearchPlaceholder != null)
                mw.TxtSearchPlaceholder.Foreground = BrushOf(muted);

            foreach (var ico in new[] { mw.IcoSearchGlyph })
            {
                if (ico != null)
                    ico.Foreground = BrushOf(light ? C(0x77, 0x77, 0x77) : C(0x77, 0x77, 0x77));
            }

            if (mw.SelectionRect != null)
            {
                mw.SelectionRect.Stroke = mw.TryFindResource("MarqueeStrokeBrush") as Brush
                    ?? BrushOf(light ? C(0x1A, 0x1A, 0x1A) : C(0xFF, 0x6B, 0x00));
                mw.SelectionRect.Fill = mw.TryFindResource("MarqueeFillBrush") as Brush
                    ?? BrushOf(light ? C(0x18, 0x1A, 0x1A, 0x1A) : C(0x33, 0xFF, 0x6B, 0x00));
            }
            if (mw.CatSelectionRect != null)
            {
                mw.CatSelectionRect.Stroke = mw.TryFindResource("MarqueeStrokeBrush") as Brush
                    ?? BrushOf(light ? C(0x1A, 0x1A, 0x1A) : C(0xFF, 0x6B, 0x00));
                mw.CatSelectionRect.Fill = mw.TryFindResource("MarqueeFillBrush") as Brush
                    ?? BrushOf(light ? C(0x18, 0x1A, 0x1A, 0x1A) : C(0x33, 0xFF, 0x6B, 0x00));
            }

            // Title bar yazıları — buton içeriğine dokunma (ikonlar beyaz kalsın)
            if (mw.TitleBarChrome != null)
            {
                foreach (var tb in EnumerateVisualChildren<TextBlock>(mw.TitleBarChrome, 40))
                {
                    if (FindParentButton(tb) != null) continue;
                    if (tb.Foreground is SolidColorBrush sb && IsNeutralGray(sb.Color))
                        tb.Foreground = BrushOf(muted);
                }
                var chromeFg = light ? C(0x1A, 0x1A, 0x1A) : Colors.White;
                foreach (var btn in EnumerateVisualChildren<Button>(mw.TitleBarChrome, 12))
                    btn.Foreground = BrushOf(chromeFg);
            }

            // Splitter çizgisi
            if (mw.MainSplitter != null)
            {
                foreach (var b in EnumerateVisualChildren<Border>(mw.MainSplitter, 8))
                {
                    if (b.Background is SolidColorBrush)
                        b.Background = BrushOf(border);
                }
            }

            try { mw.SettingsPanel?.ApplyThemeSurface(light); }
            catch { /* ignore */ }
            try { mw.RulesPanel?.ApplyThemeSurface(light); }
            catch { /* ignore */ }

            ApplyModalSurface(mw, light);

            _ = _appliedLight;
        }

        public static void ApplyModalSurface(MainWindow mw, bool light)
        {
            var border = light ? Sf(0xD8, 0xD8, 0xDE) : C(0x3A, 0x3A, 0x3A);
            var text = light ? C(0x1A, 0x1A, 0x1A) : C(0xF2, 0xF2, 0xF2);
            var muted = light ? C(0x55, 0x55, 0x55) : C(0xB0, 0xB0, 0xB0);

            if (mw.ModalCard != null)
            {
                mw.ModalCard.Background = BrushOf(light ? Sf(0xFF, 0xFF, 0xFF) : C(0x1E, 0x1E, 0x1E));
                mw.ModalCard.BorderBrush = BrushOf(border);
            }
            if (mw.ModalTitleBar != null)
                mw.ModalTitleBar.Background = BrushOf(light ? Sf(0xF5, 0xF5, 0xF7) : C(0x24, 0x24, 0x24));
            if (mw.ModalTitle != null)
                mw.ModalTitle.Foreground = BrushOf(text);
            if (mw.ModalMessage != null)
                mw.ModalMessage.Foreground = BrushOf(text);
            if (mw.ModalDetail != null)
                mw.ModalDetail.Foreground = BrushOf(muted);
            if (mw.ModalCancelBtn != null)
            {
                mw.ModalCancelBtn.Foreground = BrushOf(light ? C(0x33, 0x33, 0x33) : C(0xEE, 0xEE, 0xEE));
                var soft = light ? Sf(0xEE, 0xEE, 0xF0) : C(0x2F, 0x2F, 0x2F);
                var softHover = light ? Sf(0xE0, 0xE0, 0xE4) : C(0x3A, 0x3A, 0x3A);
                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.Name = "bd";
                factory.SetValue(Border.BackgroundProperty, BrushOf(soft));
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                factory.SetValue(Border.PaddingProperty, new Thickness(12, 0, 12, 0));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                factory.AppendChild(cp);
                template.VisualTree = factory;
                var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                over.Setters.Add(new Setter(Border.BackgroundProperty, BrushOf(softHover), "bd"));
                template.Triggers.Add(over);
                mw.ModalCancelBtn.Template = template;
            }

            if (mw.ModalOverlay != null)
                mw.ModalOverlay.Background = BrushOf(light ? C(0x88, 0x00, 0x00, 0x00) : C(0xCC, 0x00, 0x00, 0x00));
        }

        public static void ApplyGlobalCheckBoxBrushes(ResourceDictionary rd, bool light)
        {
            SetBrush(rd, "CheckBoxIdleBgBrush", light ? Sf(0xFF, 0xFF, 0xFF) : C(0x2A, 0x2A, 0x2A));
            SetBrush(rd, "CheckBoxIdleBorderBrush", light ? Sf(0xB8, 0xB8, 0xBE) : C(0x55, 0x55, 0x55));
            SetBrush(rd, "CheckBoxCheckedFillBrush", light ? Sf(0x5A, 0x5A, 0x62) : C(0x3E, 0x3E, 0x3E));
            SetBrush(rd, "CheckBoxCheckedBorderBrush", light ? Sf(0x88, 0x88, 0x90) : C(0x66, 0x66, 0x66));
            SetBrush(rd, "CheckBoxHoverBorderBrush", light ? Sf(0x99, 0x99, 0xA0) : C(0x77, 0x77, 0x77));
            SetBrush(rd, "CheckBoxMarkBrush", C(0xFF, 0xFF, 0xFF));
        }

        private static void SetBrush(ResourceDictionary rd, string key, Color color)
        {
            var brush = BrushOf(color);
            if (rd.Contains(key)) rd[key] = brush;
            else rd.Add(key, brush);
        }

        private static void SetSys(ResourceDictionary rd, object key, Color color)
        {
            var brush = BrushOf(color);
            if (rd.Contains(key)) rd[key] = brush;
            else rd.Add(key, brush);
        }

        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
        private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

        private static Color Sf(byte r, byte g, byte b) => LightSurfaceDimmer.DimSurface(r, g, b);

        private static Color Sfa(byte a, byte r, byte g, byte b) => LightSurfaceDimmer.DimSurface(a, r, g, b);

        private static bool IsNeutralGray(Color c)
            => Math.Abs(c.R - c.G) < 20 && Math.Abs(c.G - c.B) < 20 && c.R >= 0x55 && c.R <= 0xC0;

        private static Button? FindParentButton(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is Button b) return b;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static SolidColorBrush BrushOf(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static IEnumerable<T> EnumerateVisualChildren<T>(DependencyObject? root, int max) where T : DependencyObject
        {
            if (root == null) yield break;
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            int n = 0;
            while (stack.Count > 0 && n < max)
            {
                var node = stack.Pop();
                n++;
                if (node is T match) yield return match;
                if (node is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++)
                    stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
