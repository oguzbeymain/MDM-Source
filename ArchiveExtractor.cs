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
            {
                try { File.Delete(archivePath); } catch { /* ignore */ }
            }

            return dir;
        }
    }
}
