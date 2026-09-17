using System.IO;
using Microsoft.Win32;

namespace MDM
{
    public enum BrowserFamily
    {
        Chromium,
        Gecko
    }

    public sealed class BrowserTarget
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string AccentHex { get; init; } = "#888888";
        public BrowserFamily Family { get; init; }
        public string ExtensionsPage { get; init; } = "";
        public string? ExePath { get; init; }
        public string? UserData { get; init; }
        public string? GeckoProfileRoot { get; init; }
        public string PingId => Id;

        public string? ResolveExe()
        {
            if (!string.IsNullOrWhiteSpace(ExePath) && File.Exists(ExePath))
                return ExePath;
            return BrowserTargets.ResolveKnownExe(Id);
        }

        public string? UserDataRoot()
        {
            if (!string.IsNullOrWhiteSpace(UserData) && Directory.Exists(UserData))
                return UserData;
            return BrowserTargets.UserDataRoot(Id);
        }

        public string? GeckoRoot()
        {
            if (!string.IsNullOrWhiteSpace(GeckoProfileRoot) && Directory.Exists(GeckoProfileRoot))
                return GeckoProfileRoot;
            return BrowserTargets.GeckoRoot(Id);
        }
    }

    /// <summary>
    /// Desteklenen tarayıcılar: yol, profil, eklenti ailesi.
    /// </summary>
    public static class BrowserTargets
    {
        public static readonly BrowserTarget[] All =
        {
            new() { Id = "edge", Name = "Microsoft Edge", AccentHex = "#0078D4", Family = BrowserFamily.Chromium, ExtensionsPage = "edge://extensions" },
            new() { Id = "chrome", Name = "Google Chrome", AccentHex = "#34A853", Family = BrowserFamily.Chromium, ExtensionsPage = "chrome://extensions" },
            new() { Id = "brave", Name = "Brave", AccentHex = "#FB542B", Family = BrowserFamily.Chromium, ExtensionsPage = "brave://extensions" },
            new() { Id = "opera", Name = "Opera", AccentHex = "#FF1B2D", Family = BrowserFamily.Chromium, ExtensionsPage = "opera://extensions" },
            new() { Id = "opera-gx", Name = "Opera GX", AccentHex = "#EE2B47", Family = BrowserFamily.Chromium, ExtensionsPage = "opera://extensions" },
            new() { Id = "zen", Name = "Zen Browser", AccentHex = "#F76B8A", Family = BrowserFamily.Gecko, ExtensionsPage = "about:addons" },
            new() { Id = "firefox", Name = "Mozilla Firefox", AccentHex = "#FF7139", Family = BrowserFamily.Gecko, ExtensionsPage = "about:debugging#/runtime/this-firefox" },
            new() { Id = "firefox-developer", Name = "Firefox Developer Edition", AccentHex = "#00D4AA", Family = BrowserFamily.Gecko, ExtensionsPage = "about:addons" },
        };

        public static BrowserTarget? Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var t in BrowserDiscovery.Installed(force: false))
            {
                if (t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return All.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public static string? ResolveExe(string id) => Find(id)?.ResolveExe() ?? ResolveKnownExe(id);

        internal static string? ResolveKnownExe(string id)
        {
            if (id == "firefox")
                return ExtensionInstaller.ResolveFirefoxReleaseExe();
            if (id == "firefox-developer")
                return ExtensionInstaller.ResolveFirefoxDeveloperExe(out _);

            string? fromReg = id switch
            {
                "edge" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),
                "chrome" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
                "brave" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe"),
                "opera" or "opera-gx" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\opera.exe"),
                "zen" => ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\zen.exe"),
                _ => null
            };

            if (id == "opera" && LooksLikeOperaGx(fromReg))
                fromReg = null;
            if (id == "opera-gx" && fromReg != null && !LooksLikeOperaGx(fromReg))
                fromReg = null;

            if (!string.IsNullOrWhiteSpace(fromReg) && File.Exists(fromReg))
                return fromReg;

            return Candidates(id).FirstOrDefault(File.Exists);
        }

        public static string? UserDataRoot(string id)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return id switch
            {
                "edge" => Path.Combine(local, "Microsoft", "Edge", "User Data"),
                "chrome" => Path.Combine(local, "Google", "Chrome", "User Data"),
                "brave" => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                "opera" => Path.Combine(roaming, "Opera Software", "Opera Stable"),
                "opera-gx" => Path.Combine(roaming, "Opera Software", "Opera GX Stable"),
                _ => null
            };
        }

        public static string? GeckoRoot(string id)
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return id switch
            {
                "firefox" or "firefox-developer" => Path.Combine(roaming, "Mozilla", "Firefox"),
                "zen" => FirstExisting(
                    Path.Combine(roaming, "zen"),
                    Path.Combine(roaming, "Zen")),
                _ => null
            };
        }

        public static bool IsInstalled(string id)
        {
            string? exe = ResolveExe(id);
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                return true;
            string? data = UserDataRoot(id);
            if (!string.IsNullOrWhiteSpace(data) && Directory.Exists(data))
                return true;
            string? gecko = GeckoRoot(id);
            return !string.IsNullOrWhiteSpace(gecko) && File.Exists(Path.Combine(gecko, "profiles.ini"));
        }

        private static IEnumerable<string> Candidates(string id)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return id switch
            {
                "edge" => new[]
                {
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")
                },
                "chrome" => ChromeCandidates(local, pf, pf86),
                "brave" => new[]
                {
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                },
                "opera" => new[]
                {
                    Path.Combine(local, "Programs", "Opera", "opera.exe"),
                    Path.Combine(pf, "Opera", "opera.exe"),
                    Path.Combine(pf86, "Opera", "opera.exe")
                },
                "opera-gx" => new[]
                {
                    Path.Combine(local, "Programs", "Opera GX", "opera.exe"),
                    Path.Combine(pf, "Opera GX", "opera.exe"),
                    Path.Combine(pf86, "Opera GX", "opera.exe")
                },
                "zen" => new[]
                {
                    Path.Combine(local, "Zen Browser", "zen.exe"),
                    Path.Combine(local, "zen", "zen.exe"),
                    Path.Combine(pf, "Zen Browser", "zen.exe"),
                    Path.Combine(pf86, "Zen Browser", "zen.exe")
                },
                _ => Array.Empty<string>()
            };
        }

        private static string[] ChromeCandidates(string local, string pf, string pf86)
        {
            var list = new List<string>
            {
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")
            };
            foreach (string root in new[]
                     {
                         Path.Combine(local, "Google", "Chrome", "Application"),
                         Path.Combine(pf, "Google", "Chrome", "Application"),
                         Path.Combine(pf86, "Google", "Chrome", "Application")
                     })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        string exe = Path.Combine(dir, "chrome.exe");
                        if (File.Exists(exe))
                            list.Add(exe);
                    }
                }
                catch { /* ignore */ }
            }
            return list.ToArray();
        }

        private static bool LooksLikeOperaGx(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Contains("Opera GX", StringComparison.OrdinalIgnoreCase)
                   || path.Contains("OperaGX", StringComparison.OrdinalIgnoreCase);
        }

        private static string? FirstExisting(params string[] paths)
            => paths.FirstOrDefault(Directory.Exists) ?? paths.FirstOrDefault();

        private static string? ReadAppPath(string key)
        {
            try
            {
                using var hk = Registry.LocalMachine.OpenSubKey(key)
                    ?? Registry.CurrentUser.OpenSubKey(key);
                return hk?.GetValue(null) as string;
            }
            catch { return null; }
        }
    }
}
