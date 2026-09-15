using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MDM
{
    public static class BrowserIconHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static ImageSource ForBrowser(string id)
        {
            string? exe = ResolveExe(id);
            if (exe != null && File.Exists(exe))
            {
                var fromExe = FromExe(exe);
                if (fromExe != null)
                    return fromExe;
            }
            return BrandFallback(id);
        }

        public static ImageSource? FromExe(string exePath)
        {
            try
            {
                IntPtr hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
                if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1) || hIcon == new IntPtr(2))
                    return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(40, 40));
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DestroyIcon(hIcon);
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
                _ => GenericMark()
            };
        }

        private static ImageSource ChromeMark()
        {
            // Basit Chrome renkli halka
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
                new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4)),
                null,
                new EllipseGeometry(new Point(20, 20), 7.5, 7.5)));
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
                Geometry.Parse("M 10,22 C 14,26 22,26 26,22 C 24,16 18,12 12,14 C 10,16 9,19 10,22 Z")));
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

        private static string? ResolveExe(string id)
        {
            string? fromReg = id switch
            {
                "edge" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),
                "chrome" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
                "brave" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(fromReg) && File.Exists(fromReg))
                return fromReg;

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] candidates = id switch
            {
                "edge" => new[]
                {
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")
                },
                "chrome" => new[]
                {
                    Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")
                },
                "brave" => new[]
                {
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                },
                _ => Array.Empty<string>()
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string? ReadAppPath(string key)
        {
            try
            {
                using var hk = Registry.LocalMachine.OpenSubKey(key)
                    ?? Registry.CurrentUser.OpenSubKey(key);
                return hk?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
