using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DownloadMuck
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
                    if (i.IsDownloading || status.Contains("İndiriliyor", StringComparison.OrdinalIgnoreCase))
                        status = "Duraklatıldı";

                    return new DownloadDto
                    {
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

        private sealed class DownloadDto
        {
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
