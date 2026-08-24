using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadMuck
{
    public static class IconHelper
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        public static ImageSource? GetIconForExtension(string fileNameOrExtension, int pixelSize = 16)
        {
            string ext = Path.GetExtension(fileNameOrExtension);
            if (string.IsNullOrEmpty(ext))
                ext = fileNameOrExtension.StartsWith('.') ? fileNameOrExtension : "." + fileNameOrExtension;

            string key = ext + "@" + (pixelSize >= 24 ? 32 : 16);
            return Cache.GetOrAdd(key, _ => LoadIcon(ext, pixelSize >= 24 ? 32 : 16));
        }

        private static ImageSource? LoadIcon(string extension, int pixelSize)
        {
            SHFILEINFO shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (pixelSize >= 24 ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            SHGetFileInfo(extension, FILE_ATTRIBUTE_NORMAL, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (shfi.hIcon == IntPtr.Zero) return null;

            ImageSource icon = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(pixelSize, pixelSize));
            icon.Freeze();

            DestroyIcon(shfi.hIcon);
            return icon;
        }
    }
}
