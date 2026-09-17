using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MDM
{
    public static class BrowserIconHelper
    {
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiLargeIcon = 0x000000000;
        private const uint ShgfiSmallIcon = 0x000000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
            IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static ImageSource ForBrowser(string id)
        {
            string? exe = ResolveExe(id);
            if (exe != null && File.Exists(exe))
            {
                var fromShell = FromShell(exe, large: true) ?? FromShell(exe, large: false);
                if (fromShell != null)
                    return fromShell;

                // Chrome ikonu çoğu zaman chrome.dll içinde
                string? dll = Path.Combine(Path.GetDirectoryName(exe) ?? "", "chrome.dll");
                if (File.Exists(dll))
                {
                    var fromDll = FromExtractIconEx(dll) ?? FromShell(dll, large: true);
                    if (fromDll != null)
                        return fromDll;
                }

                var fromExe = FromExtractIconEx(exe);
                if (fromExe != null)
                    return fromExe;
            }
            return BrandFallback(id);
        }

        public static ImageSource? FromExe(string exePath)
            => FromShell(exePath, large: true) ?? FromExtractIconEx(exePath);

        private static ImageSource? FromShell(string path, bool large)
        {
            try
            {
                var info = new ShFileInfo();
                uint flags = ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
                IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                    return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        info.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(40, 40));
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? FromExtractIconEx(string path)
        {
            try
            {
                var large = new IntPtr[1];
                uint count = ExtractIconEx(path, 0, large, null, 1);
                if (count == 0 || large[0] == IntPtr.Zero)
                    return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        large[0],
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(40, 40));
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DestroyIcon(large[0]);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Tarayıcı yüklü olmasa bile marka ikonu (DrawingImage).</summary>
        public static ImageSource BrandFallback(string id)
        {
            return id switch
            {
                "chrome" => ChromeMark(),
                "edge" => EdgeMark(),
                "brave" => BraveMark(),
                "opera" or "opera-gx" => OperaMark(),
                "zen" => ZenMark(),
                "firefox" or "firefox-developer" => FirefoxMark(),
                _ => GenericMark()
            };
        }

        private static ImageSource FirefoxMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0x71, 0x39)),
                null,
                Geometry.Parse(
                    "M 8,28 C 10,14 18,6 28,8 C 34,10 38,16 36,24 C 34,30 28,34 22,34 " +
                    "C 14,34 8,32 6,26 C 12,30 20,28 24,22 C 20,26 12,26 8,22 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)),
                null,
                Geometry.Parse("M 12,22 C 16,26 22,26 26,22 C 24,16 18,12 14,14 C 12,16 11,19 12,22 Z")));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource ChromeMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xEA, 0x43, 0x35)),
                null,
                new EllipseGeometry(new Point(20, 20), 18, 18)));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFB, 0xBC, 0x05)),
                null,
                Geometry.Parse("M 20,2 A 18,18 0 0 1 35.5,29 L 20,20 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x34, 0xA8, 0x53)),
                null,
                Geometry.Parse("M 35.5,29 A 18,18 0 0 1 4.5,29 L 20,20 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                null,
                new EllipseGeometry(new Point(20, 20), 9, 9)));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4)),
                null,
                new EllipseGeometry(new Point(20, 20), 6.5, 6.5)));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource EdgeMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                null,
                Geometry.Parse(
                    "M 6,28 C 6,14 16,4 28,6 C 34,8 38,14 38,22 C 38,30 32,36 24,36 " +
                    "C 16,36 10,32 8,26 C 14,30 22,28 26,22 C 22,26 14,26 10,22 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x2B, 0xD0, 0xFF)),
                null,
                Geometry.Parse("M 10,22 C 14,26 22,26 26,22 C 24,16 18,12 12,14 C 12,16 11,19 10,22 Z")));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource BraveMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFB, 0x54, 0x2B)),
                null,
                Geometry.Parse(
                    "M 20,4 L 32,10 L 34,18 L 28,34 L 20,38 L 12,34 L 6,18 L 8,10 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                null,
                Geometry.Parse("M 20,12 L 26,16 L 24,28 L 20,32 L 16,28 L 14,16 Z")));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource OperaMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0x1B, 0x2D)),
                null,
                new EllipseGeometry(new Point(20, 20), 16, 16)));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                null,
                Geometry.Parse("M 12,20 A 8,10 0 1 1 28,20 A 8,10 0 1 1 12,20 Z")));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource ZenMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xF7, 0x6B, 0x8A)),
                null,
                Geometry.Parse("M 20,4 L 34,14 L 30,34 L 10,34 L 6,14 Z")));
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0xD4)),
                null,
                Geometry.Parse("M 20,10 L 28,16 L 26,28 L 14,28 L 12,16 Z")));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static ImageSource GenericMark()
        {
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                null,
                new EllipseGeometry(new Point(20, 20), 16, 16)));
            dg.Freeze();
            var img = new DrawingImage(dg);
            img.Freeze();
            return img;
        }

        private static string? ResolveExe(string id) => BrowserTargets.ResolveExe(id);
    }
}
