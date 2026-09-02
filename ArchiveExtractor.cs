using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DownloadMuck
{
    public sealed class ArchivePasswordRequiredException : Exception
    {
        public ArchivePasswordRequiredException() : base("Arşiv şifreli.") { }
    }

    public static class ArchiveExtractor
    {
        public static bool IsArchive(string? path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz";
        }

        public static bool LooksEncrypted(string archivePath)
        {
            try
            {
                using var archive = ArchiveFactory.Open(archivePath);
                return archive.Entries.Any(e => e.IsEncrypted);
            }
            catch
            {
                return false;
            }
        }

        public static string Extract(string archivePath, bool deleteArchive = false, string? password = null)
        {
            if (!File.Exists(archivePath))
                throw new FileNotFoundException("Arşiv yok.", archivePath);

            if (LooksEncrypted(archivePath) && string.IsNullOrEmpty(password))
                throw new ArchivePasswordRequiredException();

            string dir = Path.Combine(
                Path.GetDirectoryName(archivePath) ?? ".",
                Path.GetFileNameWithoutExtension(archivePath));
            Directory.CreateDirectory(dir);

            string ext = Path.GetExtension(archivePath).ToLowerInvariant();
            if (ext == ".zip" && string.IsNullOrEmpty(password))
            {
                ZipFile.ExtractToDirectory(archivePath, dir, overwriteFiles: true);
            }
            else
            {
                var options = new ReaderOptions();
                if (!string.IsNullOrEmpty(password))
                    options.Password = password;

                using var archive = ArchiveFactory.Open(archivePath, options);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    string key = entry.Key ?? "";
                    if (key.Length == 0)
                        continue;
                    string destPath = Path.GetFullPath(Path.Combine(dir, key.Replace('/', Path.DirectorySeparatorChar)));
                    string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                    if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && !destPath.Equals(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.WriteToFile(destPath, new ExtractionOptions { Overwrite = true });
                }
            }

            if (deleteArchive)
                _ = TryDeleteFile(archivePath);

            return dir;
        }

        public static void OpenArchive(string archivePath)
        {
            if (!File.Exists(archivePath)) return;
            Process.Start(new ProcessStartInfo(archivePath) { UseShellExecute = true });
        }

        /// <summary>
        /// Arşivi varsayılan uygulamayla açar; kullanıcı kapattıktan sonra dosyayı siler.
        /// </summary>
        public static void OpenAndDeleteWhenClosed(string archivePath)
        {
            if (!File.Exists(archivePath)) return;

            try
            {
                OpenArchive(archivePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Archive open: {ex.Message}");
                return;
            }

            _ = Task.Run(async () =>
            {
                // Görüntüleyici açılsın; WinRAR vb. dosyayı her zaman kilitlemez
                await Task.Delay(2500).ConfigureAwait(false);

                for (int i = 0; i < 7200; i++)
                {
                    if (!File.Exists(archivePath))
                        return;

                    if (TryDeleteFile(archivePath))
                        return;

                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
