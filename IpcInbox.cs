using System.IO;
using System.Text.Json;

namespace DownloadMuck
{
    public static class IpcInbox
    {
        public static string Folder => Path.Combine(AppSettingsStore.StoreDir, "ipc");

        public static void Enqueue(string url, bool grab)
        {
            Directory.CreateDirectory(Folder);
            var dto = new IpcCommand { Url = url, Grab = grab, Action = grab ? "grab" : "add", Utc = DateTime.UtcNow };
            string path = Path.Combine(Folder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dto));
        }

        public static void EnqueueAction(string action, string url)
        {
            Directory.CreateDirectory(Folder);
            var dto = new IpcCommand
            {
                Url = url,
                Grab = string.Equals(action, "grab", StringComparison.OrdinalIgnoreCase),
                Action = action,
                Utc = DateTime.UtcNow
            };
            string path = Path.Combine(Folder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dto));
        }

        public static IReadOnlyList<IpcCommand> Drain()
        {
            var list = new List<IpcCommand>();
            if (!Directory.Exists(Folder))
                return list;

            foreach (string file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<IpcCommand>(File.ReadAllText(file));
                    if (dto != null && (!string.IsNullOrWhiteSpace(dto.Url)
                        || string.Equals(dto.Action, "list", StringComparison.OrdinalIgnoreCase)))
                        list.Add(dto);
                }
                catch { /* skip */ }
                try { File.Delete(file); } catch { /* ignore */ }
            }
            return list;
        }

        public sealed class IpcCommand
        {
            public string Url { get; set; } = "";
            public bool Grab { get; set; }
            public string Action { get; set; } = "add";
            public DateTime Utc { get; set; }
        }
    }
}
