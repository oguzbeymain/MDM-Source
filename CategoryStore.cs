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
                    Extensions = GetDefaultExtensions("Documents")
                },
                new()
                {
                    Id = "Videos", Name = "Videolar", Icon = "🎬", IsBuiltin = true, Depth = 0,
                    Extensions = GetDefaultExtensions("Videos")
                },
                new()
                {
                    Id = "Audio", Name = "Sesler", Icon = "🎵", IsBuiltin = true, Depth = 0,
                    Extensions = GetDefaultExtensions("Audio")
                },
                new()
                {
                    Id = "Archives", Name = "Arşivler", Icon = "📦", IsBuiltin = true, Depth = 0,
                    Extensions = GetDefaultExtensions("Archives")
                },
                new()
                {
                    Id = "Images", Name = "Resimler", Icon = "🖼️", IsBuiltin = true, Depth = 0,
                    Extensions = GetDefaultExtensions("Images")
                },
                new()
                {
                    Id = "Apps", Name = "Uygulamalar", Icon = "🚀", IsBuiltin = true, Depth = 0,
                    Extensions = GetDefaultExtensions("Apps")
                }
            };
        }

        /// <summary>Native kategori varsayılan uzantıları (varsayılana dön).</summary>
        public static HashSet<string>? GetDefaultExtensions(string categoryId)
        {
            return categoryId switch
            {
                "Documents" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "pdf", "doc", "docx", "txt", "rtf", "odt", "xls", "xlsx", "ppt", "pptx", "csv", "md" },
                "Videos" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpeg", "mpg" },
                "Audio" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus" },
                "Archives" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "torrent" },
                "Images" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "tif", "tiff", "heic", "avif", "jfif" },
                "Apps" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "exe", "msi", "apk", "bat", "cmd", "msix", "appx", "dmg" },
                _ => null
            };
        }

        public static string GetDefaultIcon(string categoryId) => categoryId switch
        {
            "All" => "⚡",
            "Documents" => "📁",
            "Videos" => "🎬",
            "Audio" => "🎵",
            "Archives" => "📦",
            "Images" => "🖼️",
            "Apps" => "🚀",
            _ => "📁"
        };

        public static string? GetDefaultName(string categoryId) => categoryId switch
        {
            "All" => "Tüm İndirilenler",
            "Documents" => "Dökümanlar",
            "Videos" => "Videolar",
            "Audio" => "Sesler",
            "Archives" => "Arşivler",
            "Images" => "Resimler",
            "Apps" => "Uygulamalar",
            _ => null
        };

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
                var order = new List<string>();
                foreach (var d in dto)
                {
                    order.Add(d.Id);
                    map[d.Id] = new CategoryItem
                    {
                        Id = d.Id,
                        Name = d.Name,
                        Icon = string.IsNullOrWhiteSpace(d.Icon) ? "📁" : d.Icon,
                        IsBuiltin = d.IsBuiltin,
                        ParentId = d.ParentId,
                        IsExpanded = d.IsExpanded,
                        CustomFolderPath = string.IsNullOrWhiteSpace(d.CustomFolderPath) ? null : d.CustomFolderPath,
                        Extensions = d.Extensions == null
                            ? null
                            : new HashSet<string>(d.Extensions, StringComparer.OrdinalIgnoreCase)
                    };
                }

                var roots = new ObservableCollection<CategoryItem>();
                foreach (var id in order)
                {
                    if (!map.TryGetValue(id, out var item)) continue;
                    if (!string.IsNullOrEmpty(item.ParentId) && map.TryGetValue(item.ParentId, out var parent))
                    {
                        item.ParentId = parent.Id;
                        if (!parent.Children.Contains(item))
                        {
                            parent.Children.Add(item);
                            parent.NotifyChildrenChanged();
                        }
                    }
                    else
                    {
                        item.ParentId = null;
                        if (!roots.Contains(item))
                            roots.Add(item);
                    }
                }

                RecalcDepths(roots, 0);
                EnsureBuiltinCategories(roots);

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
                    Extensions = c.Extensions?.ToList(),
                    CustomFolderPath = c.CustomFolderPath
                }).ToList();
                File.WriteAllText(CategoriesPath, JsonSerializer.Serialize(dto, JsonOpts));
            }
            catch { /* ignore */ }
        }

        /// <summary>Eksik built-in kategorileri ekler (or. Resimler) ve uzanti listelerini gunceller.</summary>
        public static void EnsureBuiltinCategories(ObservableCollection<CategoryItem> roots)
        {
            var defaults = CreateDefaults().Where(c => c.Id != "All").ToList();
            foreach (var def in defaults)
            {
                var existing = FindById(roots, def.Id);
                if (existing == null)
                {
                    // All'dan sonra, varsayilan siraya yakin ekle
                    int insertAt = Math.Min(roots.Count, Math.Max(1, roots.Count));
                    var all = roots.FirstOrDefault(c => c.Id == "All");
                    insertAt = all != null ? roots.IndexOf(all) + 1 : 0;
                    // Videolar'dan once Resimler gibi: mevcut built-in'lerin arasina
                    int prefer = PreferBuiltinInsertIndex(roots, def.Id);
                    if (prefer >= 0) insertAt = prefer;
                    roots.Insert(Math.Clamp(insertAt, 0, roots.Count), def);
                }
                else
                {
                    existing.IsBuiltin = true;
                    // Kayitli uzantilari dokunma — kullanici ozel kurallarini / bos listeyi koru.
                    // Eksik kategori yeni eklenirken zaten def.Extensions gelir.
                    if (string.IsNullOrWhiteSpace(existing.Icon) || existing.Icon == "📁")
                        existing.Icon = def.Icon;
                    if (string.Equals(existing.Id, "Images", StringComparison.OrdinalIgnoreCase)
                        && existing.Name is "Images" or "Image")
                        existing.Name = "Resimler";
                }
            }
            RecalcDepths(roots, 0);
        }

        private static int PreferBuiltinInsertIndex(ObservableCollection<CategoryItem> roots, string id)
        {
            // Ideal sira: Documents, Videos, Audio, Archives, Images, Apps
            string[] order = ["Documents", "Videos", "Audio", "Archives", "Images", "Apps"];
            int want = Array.IndexOf(order, id);
            if (want < 0) return -1;
            for (int i = want + 1; i < order.Length; i++)
            {
                var next = roots.FirstOrDefault(c => c.Id == order[i] && string.IsNullOrEmpty(c.ParentId));
                if (next != null)
                {
                    int idx = roots.IndexOf(next);
                    return idx >= 0 ? idx : -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// Kategori agacini Downloads altinda gercek klasorler olarak olusturur.
        /// Ornek: Downloads\Videolar\AltKategori
        /// </summary>
        public static void EnsureDiskFolders(IEnumerable<CategoryItem> roots, string downloadsRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(downloadsRoot)) return;
                Directory.CreateDirectory(downloadsRoot);
                if (!AppSettingsStore.Load().AutoCreateCategoryFolders)
                    return;
                foreach (var root in roots)
                {
                    if (root.Id == "All") continue;
                    EnsureDiskFoldersRecursive(root, downloadsRoot);
                }
            }
            catch { /* ignore */ }
        }

        private static void EnsureDiskFoldersRecursive(CategoryItem item, string parentPath)
        {
            string path;
            if (!string.IsNullOrWhiteSpace(item.CustomFolderPath))
                path = item.CustomFolderPath!;
            else
                path = Path.Combine(parentPath, SanitizeFolderName(item.Name));

            Directory.CreateDirectory(path);
            foreach (var child in item.Children)
                EnsureDiskFoldersRecursive(child, path);
        }

        public static string GetCategoryFolderPath(IEnumerable<CategoryItem> roots, string categoryId, string downloadsRoot)
        {
            if (string.IsNullOrWhiteSpace(downloadsRoot))
                downloadsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            if (string.IsNullOrWhiteSpace(categoryId) || categoryId == "All")
                return downloadsRoot;

            var cat = FindById(roots, categoryId);
            if (cat == null) return downloadsRoot;

            if (!string.IsNullOrWhiteSpace(cat.CustomFolderPath))
            {
                if (AppSettingsStore.Load().AutoCreateCategoryFolders)
                {
                    try { Directory.CreateDirectory(cat.CustomFolderPath!); } catch { /* ignore */ }
                }
                return cat.CustomFolderPath!;
            }

            // Ustlerde custom path varsa onun altina isim zinciri
            var chain = new List<CategoryItem>();
            CategoryItem? cur = cat;
            while (cur != null && cur.Id != "All")
            {
                chain.Insert(0, cur);
                cur = string.IsNullOrEmpty(cur.ParentId) ? null : FindById(roots, cur.ParentId!);
            }

            bool create = AppSettingsStore.Load().AutoCreateCategoryFolders;
            if (!create)
                return downloadsRoot;

            string path = downloadsRoot;
            foreach (var node in chain)
            {
                if (!string.IsNullOrWhiteSpace(node.CustomFolderPath))
                {
                    path = node.CustomFolderPath!;
                    continue;
                }
                path = Path.Combine(path, SanitizeFolderName(node.Name));
            }

            try { Directory.CreateDirectory(path); } catch { /* ignore */ }
            return path;
        }

        public static void TryDeleteCategoryFolder(string? folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath)) return;
                if (!Directory.Exists(folderPath)) return;
                // Guvenlik: Downloads veya surucu kokunu silme
                string full = Path.GetFullPath(folderPath).TrimEnd('\\', '/');
                string downloads = Path.GetFullPath(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                if (string.Equals(full, downloads, StringComparison.OrdinalIgnoreCase)) return;
                if (full.Length <= 3) return; // C:\
                Directory.Delete(full, recursive: true);
            }
            catch { /* ignore locked files */ }
        }

        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Kategori";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) ? "Kategori" : name;
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
            public string? CustomFolderPath { get; set; }
        }
    }
}
