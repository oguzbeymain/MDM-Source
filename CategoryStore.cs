using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DownloadMuck
{
    public static class CategoryStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static string StoreDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuckDownloadManager");

        public static string CategoriesPath => Path.Combine(StoreDir, "categories.json");

        public static ObservableCollection<CategoryItem> CreateDefaults()
        {
            return new ObservableCollection<CategoryItem>
            {
                new() { Id = "All", Name = "Tüm İndirilenler", Icon = "⚡", IsBuiltin = true, Depth = 0 },
                new()
                {
                    Id = "Documents", Name = "Dökümanlar", Icon = "📁", IsBuiltin = true, Depth = 0,
                    Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "pdf", "doc", "docx", "txt", "rtf", "odt", "xls", "xlsx", "ppt", "pptx", "csv", "md" }
                },
                new()
                {
                    Id = "Videos", Name = "Videolar", Icon = "🎬", IsBuiltin = true, Depth = 0,
                    Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpeg", "mpg" }
                },
                new()
                {
                    Id = "Audio", Name = "Sesler", Icon = "🎵", IsBuiltin = true, Depth = 0,
                    Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus" }
                },
                new()
                {
                    Id = "Archives", Name = "Arşivler", Icon = "📦", IsBuiltin = true, Depth = 0,
                    Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab" }
                },
                new()
                {
                    Id = "Apps", Name = "Uygulamalar", Icon = "🚀", IsBuiltin = true, Depth = 0,
                    Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "exe", "msi", "apk", "bat", "cmd", "msix", "appx", "dmg" }
                }
            };
        }

        public static ObservableCollection<CategoryItem> Load()
        {
            try
            {
                if (!File.Exists(CategoriesPath))
                    return CreateDefaults();

                var dto = JsonSerializer.Deserialize<List<CategoryDto>>(File.ReadAllText(CategoriesPath));
                if (dto == null || dto.Count == 0)
                    return CreateDefaults();

                var map = new Dictionary<string, CategoryItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in dto)
                {
                    map[d.Id] = new CategoryItem
                    {
                        Id = d.Id,
                        Name = d.Name,
                        Icon = string.IsNullOrWhiteSpace(d.Icon) ? "📁" : d.Icon,
                        IsBuiltin = d.IsBuiltin,
                        ParentId = d.ParentId,
                        IsExpanded = d.IsExpanded,
                        Extensions = d.Extensions == null
                            ? null
                            : new HashSet<string>(d.Extensions, StringComparer.OrdinalIgnoreCase)
                    };
                }

                var roots = new ObservableCollection<CategoryItem>();
                foreach (var item in map.Values)
                {
                    if (!string.IsNullOrEmpty(item.ParentId) && map.TryGetValue(item.ParentId, out var parent))
                    {
                        item.ParentId = parent.Id;
                        parent.Children.Add(item);
                        parent.NotifyChildrenChanged();
                    }
                    else
                    {
                        item.ParentId = null;
                        roots.Add(item);
                    }
                }

                RecalcDepths(roots, 0);

                var all = roots.FirstOrDefault(c => c.Id == "All");
                if (all == null)
                {
                    roots.Insert(0, new CategoryItem { Id = "All", Name = "Tüm İndirilenler", Icon = "⚡", IsBuiltin = true });
                }
                else if (roots.IndexOf(all) != 0)
                {
                    roots.Remove(all);
                    roots.Insert(0, all);
                }

                return roots;
            }
            catch
            {
                return CreateDefaults();
            }
        }

        public static void Save(IEnumerable<CategoryItem> roots)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                var flat = roots.SelectMany(r => r.Flatten()).ToList();
                var dto = flat.Select(c => new CategoryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Icon = c.Icon,
                    IsBuiltin = c.IsBuiltin,
                    ParentId = c.ParentId,
                    IsExpanded = c.IsExpanded,
                    Extensions = c.Extensions?.ToList()
                }).ToList();
                File.WriteAllText(CategoriesPath, JsonSerializer.Serialize(dto, JsonOpts));
            }
            catch { /* ignore */ }
        }

        public static void RecalcDepths(IEnumerable<CategoryItem> items, int depth)
        {
            foreach (var item in items)
            {
                item.Depth = depth;
                RecalcDepths(item.Children, depth + 1);
            }
        }

        public static CategoryItem? FindById(IEnumerable<CategoryItem> roots, string id)
            => roots.SelectMany(r => r.Flatten()).FirstOrDefault(c => c.Id == id);

        public static IEnumerable<CategoryItem> AllFlat(IEnumerable<CategoryItem> roots)
            => roots.SelectMany(r => r.Flatten());

        private sealed class CategoryDto
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "📁";
            public bool IsBuiltin { get; set; }
            public string? ParentId { get; set; }
            public bool IsExpanded { get; set; } = true;
            public List<string>? Extensions { get; set; }
        }
    }
}
