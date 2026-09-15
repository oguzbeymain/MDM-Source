using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MDM
{
    public static class DownloadHistoryStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static string HistoryPath =>
            Path.Combine(CategoryStore.StoreDir, "downloads.json");

        public static void Save(IEnumerable<DownloadItem> items, IReadOnlyDictionary<DownloadItem, string> urls)
        {
            try
            {
                Directory.CreateDirectory(CategoryStore.StoreDir);
                var dto = items.Select(i =>
                {
                    urls.TryGetValue(i, out var url);
                    if (string.IsNullOrEmpty(url)) url = i.Url;
                    string status = i.Status;
                    // Acik indirmeleri yeniden acilista duraklatilmis say
                    if (i.IsDownloading || status.Contains("İndiriliyor", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("Kuyrukta", StringComparison.OrdinalIgnoreCase))
                        status = "Duraklatıldı";

                    return new DownloadDto
                    {
                        Id = string.IsNullOrWhiteSpace(i.Id) ? Guid.NewGuid().ToString("N")[..12] : i.Id,
                        FileName = i.FileName,
                        FilePath = i.FilePath,
                        FileSize = i.FileSize,
                        FileType = i.FileType,
                        DateAdded = i.DateAdded,
                        Status = status,
                        ProgressValue = i.ProgressValue,
                        CategoryId = i.CategoryId,
                        Url = url ?? ""
                    };
                }).ToList();

                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(dto, JsonOpts));
            }
            catch { /* ignore */ }
        }

        public static List<DownloadItem> Load(out Dictionary<DownloadItem, string> urls)
        {
            urls = new Dictionary<DownloadItem, string>();
            try
            {
                if (!File.Exists(HistoryPath)) return new List<DownloadItem>();

                var dto = JsonSerializer.Deserialize<List<DownloadDto>>(File.ReadAllText(HistoryPath));
                if (dto == null || dto.Count == 0) return new List<DownloadItem>();

                var list = new List<DownloadItem>();
                foreach (var d in dto)
                {
                    var item = new DownloadItem
                    {
                        Id = string.IsNullOrWhiteSpace(d.Id) ? Guid.NewGuid().ToString("N")[..12] : d.Id,
                        FileName = d.FileName,
                        FilePath = d.FilePath,
                        FileSize = string.IsNullOrWhiteSpace(d.FileSize) ? "-" : d.FileSize,
                        FileType = d.FileType,
                        DateAdded = d.DateAdded == default ? DateTime.Now : d.DateAdded,
                        Status = string.IsNullOrWhiteSpace(d.Status) ? "Tamamlandı" : d.Status,
                        ProgressValue = d.ProgressValue,
                        CategoryId = string.IsNullOrWhiteSpace(d.CategoryId) ? "All" : d.CategoryId,
                        Url = d.Url ?? "",
                        FileIcon = IconHelper.GetIconForExtension(d.FileName),
                        FileSizeBytes = ParseSizeBytes(d.FileSize),
                        IsDownloading = false,
                        CurrentSpeed = "",
                        StatusText = ""
                    };

                    if (item.Status.Contains("İndiriliyor", StringComparison.OrdinalIgnoreCase))
                        item.Status = "Duraklatıldı";

                    list.Add(item);
                    if (!string.IsNullOrWhiteSpace(d.Url))
                        urls[item] = d.Url;
                }
                return list;
            }
            catch
            {
                return new List<DownloadItem>();
            }
        }

        private static long ParseSizeBytes(string? label)
        {
            if (string.IsNullOrWhiteSpace(label) || label == "-" || label == "—") return -1;
            string t = label.Trim().Replace(',', '.');
            var m = Regex.Match(t, @"^([\d.]+)\s*([KMGT]B|B)$", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return -1;
            return m.Groups[2].Value.ToUpperInvariant() switch
            {
                "B" => (long)n,
                "KB" => (long)(n * 1024),
                "MB" => (long)(n * 1024 * 1024),
                "GB" => (long)(n * 1024 * 1024 * 1024),
                "TB" => (long)(n * 1024L * 1024 * 1024 * 1024),
                _ => -1
            };
        }

        private sealed class DownloadDto
        {
            public string Id { get; set; } = "";
            public string FileName { get; set; } = "";
            public string FilePath { get; set; } = "";
            public string FileSize { get; set; } = "-";
            public string FileType { get; set; } = "";
            public DateTime DateAdded { get; set; }
            public string Status { get; set; } = "";
            public double ProgressValue { get; set; }
            public string CategoryId { get; set; } = "All";
            public string Url { get; set; } = "";
        }
    }
}
