using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MDM
{
    /// <summary>
    /// Tarayıcıların indirme uyarısıyla aynı işi yapar: çalıştırılabilir veya betik türünde
    /// dosyalar indirilmeden önce kullanıcıdan izin istenir, tamamlanan dosyalar da
    /// "internetten geldi" damgasıyla (Zone.Identifier) işaretlenir; böylece Windows ve
    /// Defender dosyayı açarken kendi denetimini uygular.
    /// </summary>
    public static class DangerousFileGuard
    {
        public enum Risk
        {
            None,
            /// <summary>Doğrudan çalışan dosya: exe, msi, bat, ps1…</summary>
            Executable,
            /// <summary>Görünen uzantının arkasına saklanmış çalıştırılabilir: "fatura.pdf.exe".</summary>
            DisguisedExecutable,
            /// <summary>Makro içerebilen belge: docm, xlsm…</summary>
            Macro,
            /// <summary>Bağlanınca içeriği çalıştırılabilen imaj: iso, img, vhd.</summary>
            DiskImage
        }

        /// <summary>Çift tıklandığında kod çalıştırabilen uzantılar.</summary>
        private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle", ".com", ".scr", ".pif",
            ".bat", ".cmd", ".ps1", ".psm1", ".psc1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".jar", ".lnk", ".url", ".reg", ".inf", ".cpl", ".msc", ".msp", ".dll", ".sys",
            ".ocx", ".drv", ".gadget", ".apk", ".scf", ".sct", ".job", ".pyw"
        };

        private static readonly HashSet<string> Macro = new(StringComparer.OrdinalIgnoreCase)
        {
            ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm", ".xll"
        };

        private static readonly HashSet<string> DiskImage = new(StringComparer.OrdinalIgnoreCase)
        {
            ".iso", ".img", ".vhd", ".vhdx", ".vmdk"
        };

        /// <summary>Çift uzantı denetiminde "gerçek dosya" gibi görünen ilk uzantılar.</summary>
        private static readonly HashSet<string> Innocent = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv",
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".mp3", ".mp4", ".mkv",
            ".avi", ".mov", ".wav", ".flac", ".zip", ".rar", ".7z"
        };

        public static Risk Evaluate(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return Risk.None;

            string name = Path.GetFileName(fileName.Trim());
            string ext = Path.GetExtension(name);
            if (ext.Length == 0) return Risk.None;

            if (Executable.Contains(ext))
            {
                // "fatura.pdf.exe" masum uzantıyla kandırır; ayrı uyarı metni hak eder
                string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name));
                return inner.Length > 0 && Innocent.Contains(inner)
                    ? Risk.DisguisedExecutable
                    : Risk.Executable;
            }

            if (Macro.Contains(ext)) return Risk.Macro;
            if (DiskImage.Contains(ext)) return Risk.DiskImage;
            return Risk.None;
        }

        public static bool IsRisky(string? fileName) => Evaluate(fileName) != Risk.None;

        /// <summary>Uyarı penceresinde gösterilecek açıklama.</summary>
        public static string DescribeRisk(Risk risk) => risk switch
        {
            Risk.Executable => Loc.T("dialog.dangerous_kind_exe",
                "Bu dosya bilgisayarınızda program çalıştırabilir."),
            Risk.DisguisedExecutable => Loc.T("dialog.dangerous_kind_disguised",
                "Dosya adı belge veya medya gibi görünüyor ama aslında çalıştırılabilir bir program."),
            Risk.Macro => Loc.T("dialog.dangerous_kind_macro",
                "Bu belge, açıldığında çalışabilecek makro içerebilir."),
            Risk.DiskImage => Loc.T("dialog.dangerous_kind_image",
                "Bu disk görüntüsü bağlandığında içindeki programlar çalışabilir."),
            _ => string.Empty
        };

        /// <summary>
        /// Dosyayı "İnternet bölgesinden indirildi" olarak işaretler. Windows bu damgayı
        /// gördüğünde açılışta uyarı gösterir ve SmartScreen/Defender denetimini uygular.
        /// </summary>
        public static bool MarkAsFromInternet(string filePath, string? sourceUrl = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

                var lines = new List<string> { "[ZoneTransfer]", "ZoneId=3" };
                if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "http" or "https")
                {
                    lines.Add("ReferrerUrl=" + uri.GetLeftPart(UriPartial.Authority));
                    lines.Add("HostUrl=" + uri.AbsoluteUri);
                }

                // Alternatif veri akışı: yalnızca NTFS'te vardır, FAT/exFAT'te sessizce atlanır
                File.WriteAllLines(filePath + ":Zone.Identifier", lines);
                return true;
            }
            catch { return false; }
        }
    }
}
