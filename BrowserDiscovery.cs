using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MDM
{
    /// <summary>
    /// Bu makinede yüklü tarayıcıları tarar: bilinenler + kayıt defteri +
    /// App Paths + Chromium User Data + Gecko profiles.ini. Electron uygulamaları elenir.
    /// </summary>
    public static class BrowserDiscovery
    {
        private static List<BrowserTarget>? _cache;
        private static DateTime _cacheUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(45);

        private static readonly HashSet<string> BrowserExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "msedge.exe", "msedgewebview2.exe", "brave.exe", "opera.exe",
            "vivaldi.exe", "firefox.exe", "zen.exe", "librewolf.exe", "waterfox.exe",
            "floorp.exe", "palemoon.exe", "basilisk.exe", "thorium.exe", "chromium.exe",
            "browser.exe", "duckduckgo.exe", "arc.exe", "iridium.exe", "slimjet.exe",
            "dragon.exe", "coccoc.exe", "avastbrowser.exe", "avgbrowser.exe",
            "sidekick.exe", "wavebox.exe", "maxthon.exe", "seamonkey.exe",
            "midori.exe", "falkon.exe", "ungoogled-chromium.exe", "chrome-beta.exe",
            "chrome-dev.exe", "msedge_proxy.exe"
        };

        private static readonly HashSet<string> ElectronSkip = new(StringComparer.OrdinalIgnoreCase)
        {
            "code.exe", "cursor.exe", "discord.exe", "slack.exe", "spotify.exe",
            "teams.exe", "ms-teams.exe", "whatsapp.exe", "telegram.exe", "steamwebhelper.exe",
            "figma.exe", "notion.exe", "obsidian.exe", "postman.exe", "githubdesktop.exe",
            "electron.exe", "slack-update.exe", "mdm.exe", "mdm.setup.exe"
        };

        private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Temp", "tmp", "Cache", "Code Cache", "GPUCache", "CrashDumps", "node_modules",
            "Packages", "NuGet", "pip", "cargo", "npm-cache", "Windows",
            "TempNet", "INetCache", "D3DSCache"
        };

        public static IReadOnlyList<BrowserTarget> Installed(bool force = false)
        {
            if (!force && _cache != null && DateTime.UtcNow - _cacheUtc < CacheFor)
                return _cache;
            _cache = Scan();
            _cacheUtc = DateTime.UtcNow;
            return _cache;
        }

        public static IReadOnlyList<BrowserTarget> Cached => _cache ?? Installed(force: true);

        private static List<BrowserTarget> Scan()
        {
            var byExe = new Dictionary<string, BrowserTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (string exe in EnumerateCandidateExes())
            {
                if (!IsBrowserExecutable(exe, out var family))
                    continue;
                string full = NormalizeExe(exe);
                if (byExe.ContainsKey(full))
                    continue;
                if (IsHelperExe(full))
                    continue;

                var target = Describe(full, family);
                if (target == null)
                    continue;
                byExe[full] = target;
            }

            DeduplicateChromeVersionFolders(byExe);

            var knownOrder = BrowserTargets.All.Select(t => t.Id).ToList();
            return byExe.Values
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(t =>
                {
                    int i = knownOrder.FindIndex(id => id.Equals(t.Id, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : 1000;
                })
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> EnumerateCandidateExes()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var recipe in Recipes())
            {
                foreach (string exe in recipe.Exes())
                {
                    if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                        yield return exe;
                }
            }

            foreach (string exe in AppPathExes())
            {
                if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                    yield return exe;
            }

            foreach (string exe in UninstallExes())
            {
                if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                    yield return exe;
            }

            foreach (string exe in StartMenuExes())
            {
                if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                    yield return exe;
            }

            foreach (string exe in SweepChromiumApplications())
            {
                if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                    yield return exe;
            }

            foreach (string exe in SweepGeckoExes())
            {
                if (File.Exists(exe) && seen.Add(NormalizeExe(exe)))
                    yield return exe;
            }
        }

        private static BrowserTarget? Describe(string exe, BrowserFamily family)
        {
            if (family == BrowserFamily.Gecko)
            {
                var gecko = DescribeFirefoxChannel(exe);
                if (gecko != null)
                    return gecko;
            }

            var matched = MatchRecipe(exe);
            if (matched != null)
            {
                return new BrowserTarget
                {
                    Id = matched.Id,
                    Name = matched.Name,
                    AccentHex = matched.Accent,
                    Family = matched.Family,
                    ExtensionsPage = matched.ExtensionsPage,
                    ExePath = exe,
                    UserData = matched.UserData(),
                    GeckoProfileRoot = matched.GeckoRoot()
                };
            }

            string name = ProductName(exe);
            string id = UniqueId(Slug(name), exe);
            string accent = family == BrowserFamily.Gecko ? "#FF7139" : "#5B8DEF";
            string page = family == BrowserFamily.Gecko
                ? "about:addons"
                : ExtensionsPageFor(exe);

            return new BrowserTarget
            {
                Id = id,
                Name = name,
                AccentHex = accent,
                Family = family,
                ExtensionsPage = page,
                ExePath = exe,
                UserData = family == BrowserFamily.Chromium ? GuessChromiumUserData(exe) : null,
                GeckoProfileRoot = family == BrowserFamily.Gecko ? GuessGeckoRoot(exe, name) : null
            };
        }

        private static BrowserTarget? DescribeFirefoxChannel(string exe)
        {
            string file = Path.GetFileName(exe);
            bool zen = file.Equals("zen.exe", StringComparison.OrdinalIgnoreCase)
                       || exe.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase);
            if (zen)
                return FromRecipe("zen", exe);

            if (!file.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            string channel = ExtensionInstaller.DetectChannel(exe);
            try
            {
                string? dev = ExtensionInstaller.ResolveFirefoxDeveloperExe(out _);
                string? rel = ExtensionInstaller.ResolveFirefoxReleaseExe();
                if (!string.IsNullOrWhiteSpace(dev) && PathsEqual(exe, dev))
                    channel = "developer";
                else if (!string.IsNullOrWhiteSpace(rel) && PathsEqual(exe, rel))
                    channel = "release";
            }
            catch { /* ignore */ }

            string id = channel switch
            {
                "developer" => "firefox-developer",
                "nightly" => "firefox-nightly",
                _ => "firefox"
            };
            return FromRecipe(id, exe);
        }

        private static BrowserTarget? FromRecipe(string id, string exe)
        {
            Recipe? matched = null;
            foreach (var r in Recipes())
            {
                if (r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    matched = r;
                    break;
                }
            }
            if (matched == null) return null;
            return new BrowserTarget
            {
                Id = matched.Id,
                Name = matched.Name,
                AccentHex = matched.Accent,
                Family = matched.Family,
                ExtensionsPage = matched.ExtensionsPage,
                ExePath = exe,
                UserData = matched.UserData(),
                GeckoProfileRoot = matched.GeckoRoot()
            };
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static Recipe? MatchRecipe(string exe)
        {
            string p = exe.Replace('/', '\\');
            foreach (var r in Recipes())
            {
                if (r.Matches(p))
                    return r;
            }
            return null;
        }

        private static IEnumerable<Recipe> Recipes()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            yield return Chromium("edge", "Microsoft Edge", "#0078D4", "edge://extensions",
                new[] { "msedge.exe" },
                new[]
                {
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")
                },
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                p => ContainsAll(p, "Microsoft", "Edge") && !ContainsAny(p, "Edge Beta", "Edge Dev", "Edge SxS", "EdgeCore", "WebView"));

            yield return Chromium("edge-beta", "Microsoft Edge Beta", "#8AB4F8", "edge://extensions",
                new[] { "msedge.exe" },
                new[]
                {
                    Path.Combine(pf, "Microsoft", "Edge Beta", "Application", "msedge.exe"),
                    Path.Combine(local, "Microsoft", "Edge Beta", "Application", "msedge.exe")
                },
                Path.Combine(local, "Microsoft", "Edge Beta", "User Data"),
                p => ContainsAll(p, "Edge Beta"));

            yield return Chromium("edge-dev", "Microsoft Edge Dev", "#8AB4F8", "edge://extensions",
                new[] { "msedge.exe" },
                new[] { Path.Combine(pf, "Microsoft", "Edge Dev", "Application", "msedge.exe") },
                Path.Combine(local, "Microsoft", "Edge Dev", "User Data"),
                p => ContainsAll(p, "Edge Dev") && !ContainsAll(p, "Edge Developer"));

            yield return Chromium("edge-canary", "Microsoft Edge Canary", "#8AB4F8", "edge://extensions",
                new[] { "msedge.exe" },
                new[] { Path.Combine(local, "Microsoft", "Edge SxS", "Application", "msedge.exe") },
                Path.Combine(local, "Microsoft", "Edge SxS", "User Data"),
                p => ContainsAll(p, "Edge SxS") || (ContainsAll(p, "Edge") && ContainsAll(p, "Canary")));

            yield return Chromium("chrome", "Google Chrome", "#34A853", "chrome://extensions",
                new[] { "chrome.exe" },
                new[]
                {
                    Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")
                },
                Path.Combine(local, "Google", "Chrome", "User Data"),
                p => ContainsAll(p, "Google", "Chrome") && !ContainsAny(p, "Chrome Beta", "Chrome Dev", "Chrome SxS", "Chrome Core"));

            yield return Chromium("chrome-beta", "Google Chrome Beta", "#34A853", "chrome://extensions",
                new[] { "chrome.exe" },
                new[]
                {
                    Path.Combine(local, "Google", "Chrome Beta", "Application", "chrome.exe"),
                    Path.Combine(pf, "Google", "Chrome Beta", "Application", "chrome.exe")
                },
                Path.Combine(local, "Google", "Chrome Beta", "User Data"),
                p => ContainsAll(p, "Chrome Beta"));

            yield return Chromium("chrome-dev", "Google Chrome Dev", "#34A853", "chrome://extensions",
                new[] { "chrome.exe" },
                new[] { Path.Combine(local, "Google", "Chrome Dev", "Application", "chrome.exe") },
                Path.Combine(local, "Google", "Chrome Dev", "User Data"),
                p => ContainsAll(p, "Chrome Dev"));

            yield return Chromium("chrome-canary", "Google Chrome Canary", "#34A853", "chrome://extensions",
                new[] { "chrome.exe" },
                new[] { Path.Combine(local, "Google", "Chrome SxS", "Application", "chrome.exe") },
                Path.Combine(local, "Google", "Chrome SxS", "User Data"),
                p => ContainsAll(p, "Chrome SxS") || ContainsAll(p, "Chrome Canary"));

            yield return Chromium("brave", "Brave", "#FB542B", "chrome://extensions/",
                new[] { "brave.exe" },
                new[]
                {
                    Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                },
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                p => ContainsAll(p, "Brave-Browser") && !ContainsAny(p, "Beta", "Nightly"));

            yield return Chromium("brave-beta", "Brave Beta", "#FB542B", "chrome://extensions/",
                new[] { "brave.exe" },
                new[] { Path.Combine(local, "BraveSoftware", "Brave-Browser-Beta", "Application", "brave.exe") },
                Path.Combine(local, "BraveSoftware", "Brave-Browser-Beta", "User Data"),
                p => ContainsAll(p, "Brave-Browser-Beta"));

            yield return Chromium("brave-nightly", "Brave Nightly", "#FB542B", "chrome://extensions/",
                new[] { "brave.exe" },
                new[] { Path.Combine(local, "BraveSoftware", "Brave-Browser-Nightly", "Application", "brave.exe") },
                Path.Combine(local, "BraveSoftware", "Brave-Browser-Nightly", "User Data"),
                p => ContainsAll(p, "Brave-Browser-Nightly"));

            yield return Chromium("opera", "Opera", "#FF1B2D", "chrome://extensions/",
                new[] { "opera.exe" },
                new[]
                {
                    Path.Combine(local, "Programs", "Opera", "opera.exe"),
                    Path.Combine(pf, "Opera", "opera.exe")
                },
                Path.Combine(roaming, "Opera Software", "Opera Stable"),
                p => NameIs(p, "opera.exe") && !ContainsAny(p, "Opera GX", "OperaGX", "Opera Air", "Opera Beta"));

            yield return Chromium("opera-gx", "Opera GX", "#EE2B47", "chrome://extensions/",
                new[] { "opera.exe" },
                new[]
                {
                    Path.Combine(local, "Programs", "Opera GX", "opera.exe"),
                    Path.Combine(pf, "Opera GX", "opera.exe")
                },
                Path.Combine(roaming, "Opera Software", "Opera GX Stable"),
                p => ContainsAny(p, "Opera GX", "OperaGX"));

            yield return Chromium("opera-air", "Opera Air", "#FF1B2D", "chrome://extensions/",
                new[] { "opera.exe" },
                new[] { Path.Combine(local, "Programs", "Opera Air", "opera.exe") },
                Path.Combine(roaming, "Opera Software", "Opera Air Stable"),
                p => ContainsAll(p, "Opera Air"));

            yield return Chromium("vivaldi", "Vivaldi", "#EF3939", "chrome://extensions",
                new[] { "vivaldi.exe" },
                new[]
                {
                    Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"),
                    Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe")
                },
                Path.Combine(local, "Vivaldi", "User Data"),
                p => NameIs(p, "vivaldi.exe") || ContainsAll(p, "Vivaldi"));

            yield return Chromium("yandex", "Yandex Browser", "#FFCC00", "chrome://extensions",
                new[] { "browser.exe" },
                new[]
                {
                    Path.Combine(local, "Yandex", "YandexBrowser", "Application", "browser.exe"),
                    Path.Combine(pf, "Yandex", "YandexBrowser", "Application", "browser.exe")
                },
                Path.Combine(local, "Yandex", "YandexBrowser", "User Data"),
                p => ContainsAll(p, "YandexBrowser") || (NameIs(p, "browser.exe") && ContainsAll(p, "Yandex")));

            yield return Chromium("duckduckgo", "DuckDuckGo", "#DE5833", "chrome://extensions",
                new[] { "duckduckgo.exe" },
                new[]
                {
                    Path.Combine(local, "DuckDuckGo", "DuckDuckGo", "Release", "DuckDuckGo.exe"),
                    Path.Combine(local, "DuckDuckGo", "DuckDuckGo.exe")
                },
                Path.Combine(local, "DuckDuckGo", "User Data"),
                p => ContainsAll(p, "DuckDuckGo"));

            yield return Chromium("thorium", "Thorium", "#2E86AB", "chrome://extensions",
                new[] { "thorium.exe" },
                new[]
                {
                    Path.Combine(local, "Thorium", "Application", "thorium.exe"),
                    Path.Combine(pf, "Thorium", "Application", "thorium.exe")
                },
                Path.Combine(local, "Thorium", "User Data"),
                p => NameIs(p, "thorium.exe") || ContainsAll(p, "Thorium"));

            yield return Chromium("chromium", "Chromium", "#8AB4F8", "chrome://extensions",
                new[] { "chrome.exe", "chromium.exe" },
                new[]
                {
                    Path.Combine(local, "Chromium", "Application", "chrome.exe"),
                    Path.Combine(pf, "Chromium", "Application", "chrome.exe")
                },
                Path.Combine(local, "Chromium", "User Data"),
                p => ContainsAll(p, "Chromium") && !ContainsAny(p, "ungoogled", "Chrome"));

            yield return Chromium("arc", "Arc", "#F0544C", "chrome://extensions",
                new[] { "arc.exe" },
                new[] { Path.Combine(local, "Arc", "Arc.exe") },
                Path.Combine(local, "Arc", "User Data"),
                p => NameIs(p, "arc.exe") || (ContainsAll(p, "\\Arc\\") && NameIs(p, "arc.exe")));

            yield return Gecko("firefox", "Mozilla Firefox", "#FF7139", "about:debugging#/runtime/this-firefox",
                () => NonEmpty(ExtensionInstaller.ResolveFirefoxReleaseExe()),
                Path.Combine(roaming, "Mozilla", "Firefox"),
                p => NameIs(p, "firefox.exe") && ContainsAll(p, "Mozilla Firefox") && !ContainsAny(p, "Developer", "Nightly", "ESR"));

            yield return Gecko("firefox-developer", "Firefox Developer Edition", "#00D4AA", "about:addons",
                () => NonEmpty(ExtensionInstaller.ResolveFirefoxDeveloperExe(out _)),
                Path.Combine(roaming, "Mozilla", "Firefox"),
                p => ContainsAny(p, "Developer Edition", "Firefox Developer"));

            yield return Gecko("firefox-nightly", "Firefox Nightly", "#00D4AA", "about:addons",
                () => new[]
                {
                    Path.Combine(local, "Firefox Nightly", "firefox.exe"),
                    Path.Combine(pf, "Firefox Nightly", "firefox.exe")
                },
                Path.Combine(roaming, "Mozilla", "Firefox"),
                p => ContainsAll(p, "Nightly") && NameIs(p, "firefox.exe"));

            yield return Gecko("zen", "Zen Browser", "#F76B8A", "about:addons",
                () => new[]
                {
                    Path.Combine(local, "Zen Browser", "zen.exe"),
                    Path.Combine(local, "zen", "zen.exe"),
                    Path.Combine(pf, "Zen Browser", "zen.exe")
                },
                FirstExistingDir(Path.Combine(roaming, "zen"), Path.Combine(roaming, "Zen")),
                p => NameIs(p, "zen.exe") || ContainsAny(p, "Zen Browser", "\\zen\\"));

            yield return Gecko("librewolf", "LibreWolf", "#00ACFF", "about:addons",
                () => new[]
                {
                    Path.Combine(pf, "LibreWolf", "librewolf.exe"),
                    Path.Combine(local, "LibreWolf", "librewolf.exe")
                },
                Path.Combine(roaming, "librewolf"),
                p => NameIs(p, "librewolf.exe") || ContainsAll(p, "LibreWolf"));

            yield return Gecko("waterfox", "Waterfox", "#178BDB", "about:addons",
                () => new[]
                {
                    Path.Combine(pf, "Waterfox", "waterfox.exe"),
                    Path.Combine(local, "Waterfox", "waterfox.exe")
                },
                Path.Combine(roaming, "Waterfox"),
                p => NameIs(p, "waterfox.exe") || ContainsAll(p, "Waterfox"));

            yield return Gecko("floorp", "Floorp", "#4C8BF5", "about:addons",
                () => new[]
                {
                    Path.Combine(pf, "Floorp", "floorp.exe"),
                    Path.Combine(local, "Floorp", "floorp.exe")
                },
                Path.Combine(roaming, "Floorp"),
                p => NameIs(p, "floorp.exe") || ContainsAll(p, "Floorp"));

            yield return Gecko("palemoon", "Pale Moon", "#ABD3F2", "about:addons",
                () => new[]
                {
                    Path.Combine(pf, "Pale Moon", "palemoon.exe"),
                    Path.Combine(pf86, "Pale Moon", "palemoon.exe")
                },
                Path.Combine(roaming, "Moonchild Productions", "Pale Moon"),
                p => NameIs(p, "palemoon.exe") || ContainsAll(p, "Pale Moon"));
        }

        private static IEnumerable<string> AppPathExes()
        {
            var found = new List<string>();
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            foreach (var hive in hives)
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (root == null) continue;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            continue;
                        using var sub = root.OpenSubKey(name);
                        if (sub?.GetValue(null) is string path && path.Length > 0)
                            found.Add(path.Trim().Trim('"'));
                    }
                }
                catch { /* ignore */ }
            }
            return found;
        }

        private static IEnumerable<string> UninstallExes()
        {
            var found = new List<string>();
            string[] keys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (var hive in hives)
            foreach (var view in views)
            foreach (string key in keys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(key);
                    if (root == null) continue;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using var sub = root.OpenSubKey(name);
                        if (sub == null) continue;
                        string display = (sub.GetValue("DisplayName") as string) ?? "";
                        string icon = (sub.GetValue("DisplayIcon") as string) ?? "";
                        string loc = (sub.GetValue("InstallLocation") as string) ?? "";
                        string? exe = ExeFromDisplayIcon(icon);
                        if (string.IsNullOrEmpty(exe) && Directory.Exists(loc))
                            exe = GuessExeInDir(loc);
                        if (string.IsNullOrEmpty(exe))
                            continue;
                        if (!LooksLikeBrowserName(display) && !BrowserExeNames.Contains(Path.GetFileName(exe)))
                            continue;
                        found.Add(exe);
                    }
                }
                catch { /* ignore */ }
            }
            return found;
        }

        private static IEnumerable<string> StartMenuExes()
        {
            var found = new List<string>();
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                     })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string lnk in EnumerateFilesSafe(folder, "*.lnk"))
                {
                    string title = Path.GetFileNameWithoutExtension(lnk);
                    if (!LooksLikeBrowserName(title))
                        continue;
                    string? target = ShortcutTarget(lnk);
                    if (!string.IsNullOrWhiteSpace(target)
                        && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        found.Add(target);
                }
            }
            return found;
        }

        private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = Array.Empty<string>();
                string[] children = Array.Empty<string>();
                try { files = Directory.GetFiles(dir, pattern); } catch { /* erişim yok */ }
                try { children = Directory.GetDirectories(dir); } catch { /* erişim yok */ }
                foreach (string f in files)
                    yield return f;
                foreach (string c in children)
                    stack.Push(c);
            }
        }

        private static string? ShortcutTarget(string lnk)
        {
            try
            {
                Type? ty = Type.GetTypeFromProgID("WScript.Shell");
                if (ty == null) return null;
                dynamic shell = Activator.CreateInstance(ty)!;
                dynamic sc = shell.CreateShortcut(lnk);
                return sc.TargetPath as string;
            }
            catch { return null; }
        }

        private static IEnumerable<string> SweepChromiumApplications()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string userData in FindNamedDirs(local, "User Data", maxDepth: 4))
            {
                string? exe = GuessChromiumExeFromUserData(userData);
                if (!string.IsNullOrEmpty(exe))
                    yield return exe;
            }
        }

        private static IEnumerable<string> SweepGeckoExes()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string root in FindGeckoRoots(roaming, maxDepth: 3))
            {
                string? exe = GuessGeckoExeFromRoot(root);
                if (!string.IsNullOrEmpty(exe))
                    yield return exe;
            }
        }

        private static IEnumerable<string> FindNamedDirs(string root, string leafName, int maxDepth)
        {
            var found = new List<string>();
            Walk(root, 0);
            return found;

            void Walk(string dir, int depth)
            {
                if (depth > maxDepth || found.Count > 80) return;
                string name = Path.GetFileName(dir);
                if (depth > 0 && SkipDirNames.Contains(name)) return;
                if (name.Equals(leafName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(dir, "Local State")))
                {
                    found.Add(dir);
                    return;
                }
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(dir))
                        Walk(child, depth + 1);
                }
                catch { /* ignore */ }
            }
        }

        private static IEnumerable<string> FindGeckoRoots(string root, int maxDepth)
        {
            var found = new List<string>();
            Walk(root, 0);
            return found;

            void Walk(string dir, int depth)
            {
                if (depth > maxDepth || found.Count > 40) return;
                string name = Path.GetFileName(dir);
                if (depth > 0 && SkipDirNames.Contains(name)) return;
                if (File.Exists(Path.Combine(dir, "profiles.ini")))
                {
                    found.Add(dir);
                    return;
                }
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(dir))
                        Walk(child, depth + 1);
                }
                catch { /* ignore */ }
            }
        }

        private static bool IsBrowserExecutable(string exe, out BrowserFamily family)
        {
            family = BrowserFamily.Chromium;
            try
            {
                if (!File.Exists(exe)) return false;
                string name = Path.GetFileName(exe);
                if (ElectronSkip.Contains(name)) return false;
                if (name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("webview", StringComparison.OrdinalIgnoreCase))
                    return false;

                string? dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) return false;
                if (File.Exists(Path.Combine(dir, "resources", "app.asar"))
                    || Directory.Exists(Path.Combine(dir, "resources", "app.asar.unpacked")))
                    return false;

                bool geckoDll = HasGeckoRuntime(dir);
                bool chromiumRuntime = HasChromiumRuntime(dir);
                bool knownGecko = NameLooksGecko(name);
                bool knownChromium = NameLooksChromium(name, exe);

                if (knownGecko || geckoDll)
                {
                    family = BrowserFamily.Gecko;
                    return true;
                }

                if (knownChromium || (chromiumRuntime && LooksLikeBrowserName(ProductName(exe))))
                {
                    family = BrowserFamily.Chromium;
                    return true;
                }
            }
            catch { return false; }
            return false;
        }

        private static bool NameLooksChromium(string exeName, string exePath)
        {
            if (exeName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("opera.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("thorium.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("chromium.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("duckduckgo.exe", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("arc.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            if (exeName.Equals("browser.exe", StringComparison.OrdinalIgnoreCase)
                && exePath.Contains("Yandex", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool HasGeckoRuntime(string dir)
            => File.Exists(Path.Combine(dir, "mozglue.dll"))
               || File.Exists(Path.Combine(dir, "xul.dll"))
               || File.Exists(Path.Combine(dir, "gkmedias.dll"));

        /// <summary>
        /// Chromium başlatıcısının yanında DLL olmaz; chrome.dll sürüm klasöründedir.
        /// </summary>
        private static bool HasChromiumRuntime(string dir)
        {
            if (File.Exists(Path.Combine(dir, "chrome.dll"))
                || File.Exists(Path.Combine(dir, "msedge.dll")))
                return true;
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (!Regex.IsMatch(Path.GetFileName(sub), @"^\d+\."))
                        continue;
                    if (File.Exists(Path.Combine(sub, "chrome.dll"))
                        || File.Exists(Path.Combine(sub, "msedge.dll")))
                        return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private static bool NameLooksGecko(string exeName)
            => exeName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("zen.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("librewolf.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("waterfox.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("floorp.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("palemoon.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("basilisk.exe", StringComparison.OrdinalIgnoreCase)
               || exeName.Equals("seamonkey.exe", StringComparison.OrdinalIgnoreCase);

        private static bool IsHelperExe(string exe)
        {
            string n = Path.GetFileName(exe);
            if (n.Contains("crashpad", StringComparison.OrdinalIgnoreCase)
                || n.Contains("crashreporter", StringComparison.OrdinalIgnoreCase)
                || n.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                || n.Contains("update", StringComparison.OrdinalIgnoreCase)
                || n.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                || n.Contains("notification_helper", StringComparison.OrdinalIgnoreCase)
                || n.Contains("elevation", StringComparison.OrdinalIgnoreCase)
                || n.Contains("webview", StringComparison.OrdinalIgnoreCase)
                || n.Contains("private_browsing", StringComparison.OrdinalIgnoreCase)
                || n.Contains("plugin-container", StringComparison.OrdinalIgnoreCase)
                || n.Contains("default-browser-agent", StringComparison.OrdinalIgnoreCase)
                || n.Contains("ping-sender", StringComparison.OrdinalIgnoreCase))
                return true;

            string dir = Path.GetDirectoryName(exe) ?? "";
            string leaf = Path.GetFileName(dir);
            if (Regex.IsMatch(leaf, @"^\d+\.\d+")
                && File.Exists(Path.Combine(Directory.GetParent(dir)?.FullName ?? "", Path.GetFileName(exe))))
                return true;
            return false;
        }

        private static void DeduplicateChromeVersionFolders(Dictionary<string, BrowserTarget> byExe)
        {
            var drop = new List<string>();
            foreach (var kv in byExe)
            {
                string dir = Path.GetDirectoryName(kv.Key) ?? "";
                string parent = Directory.GetParent(dir)?.FullName ?? "";
                string sibling = Path.Combine(parent, Path.GetFileName(kv.Key));
                if (!kv.Key.Equals(sibling, StringComparison.OrdinalIgnoreCase)
                    && byExe.ContainsKey(sibling)
                    && Regex.IsMatch(Path.GetFileName(dir), @"^\d+\.\d+"))
                    drop.Add(kv.Key);
            }
            foreach (string d in drop)
                byExe.Remove(d);
        }

        private static string? GuessChromiumUserData(string exe)
        {
            string? appDir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(appDir)) return null;
            string? productDir = Directory.GetParent(appDir)?.FullName;
            if (!string.IsNullOrEmpty(productDir))
            {
                string ud = Path.Combine(productDir, "User Data");
                if (Directory.Exists(ud)) return ud;
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                string rel = Path.GetRelativePath(local, appDir);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                {
                    string guess = Path.Combine(local, Directory.GetParent(rel)?.ToString() ?? rel, "User Data");
                    if (Directory.Exists(guess)) return guess;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static string? GuessChromiumExeFromUserData(string userData)
        {
            string? product = Directory.GetParent(userData)?.FullName;
            if (string.IsNullOrEmpty(product)) return null;
            string app = Path.Combine(product, "Application");
            if (!Directory.Exists(app)) return null;
            try
            {
                foreach (string exe in Directory.EnumerateFiles(app, "*.exe"))
                {
                    if (IsHelperExe(exe)) continue;
                    if (IsBrowserExecutable(exe, out _))
                        return exe;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static string? GuessGeckoRoot(string exe, string productName)
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string slug = Slug(productName);
            foreach (string candidate in new[]
                     {
                         Path.Combine(roaming, slug),
                         Path.Combine(roaming, productName),
                         Path.Combine(roaming, Path.GetFileNameWithoutExtension(exe) ?? slug)
                     })
            {
                if (File.Exists(Path.Combine(candidate, "profiles.ini")))
                    return candidate;
            }

            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "profiles.ini")))
                return dir;
            return null;
        }

        private static string? GuessGeckoExeFromRoot(string root)
        {
            string name = Path.GetFileName(root);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string exe in new[]
                     {
                         Path.Combine(pf, name, name + ".exe"),
                         Path.Combine(pf, name, "firefox.exe"),
                         Path.Combine(local, name, name + ".exe"),
                         Path.Combine(local, name, "firefox.exe")
                     })
            {
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        private static string? GuessExeInDir(string dir)
        {
            try
            {
                foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (!IsHelperExe(exe) && IsBrowserExecutable(exe, out _))
                        return exe;
                }
                string app = Path.Combine(dir, "Application");
                if (Directory.Exists(app))
                {
                    foreach (string exe in Directory.EnumerateFiles(app, "*.exe"))
                    {
                        if (!IsHelperExe(exe) && IsBrowserExecutable(exe, out _))
                            return exe;
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static string? ExeFromDisplayIcon(string icon)
        {
            if (string.IsNullOrWhiteSpace(icon)) return null;
            string t = icon.Trim().Trim('"');
            int comma = t.LastIndexOf(',');
            if (comma > 2 && t[..comma].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                t = t[..comma].Trim().Trim('"');
            return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
        }

        private static bool LooksLikeBrowserName(string display)
        {
            if (string.IsNullOrWhiteSpace(display)) return false;
            return Regex.IsMatch(display,
                @"chrome|firefox|edge|brave|opera|vivaldi|yandex|duckduckgo|zen|librewolf|waterfox|floorp|thorium|chromium|pale\s*moon|basilisk|arc|midori|falkon|maxthon|seamonkey|tor browser|mullvad",
                RegexOptions.IgnoreCase);
        }

        private static string ProductName(string exe)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (UsefulLabel(info.FileDescription))
                    return info.FileDescription!.Trim();
                if (UsefulLabel(info.ProductName))
                    return info.ProductName!.Trim();
            }
            catch { /* ignore */ }
            string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? "") ?? "";
            if (!string.IsNullOrWhiteSpace(folder) && !folder.Equals("Application", StringComparison.OrdinalIgnoreCase))
                return folder;
            return Path.GetFileNameWithoutExtension(exe) ?? "Browser";
        }

        private static bool UsefulLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return !value.Contains("Helper", StringComparison.OrdinalIgnoreCase)
                   && !value.Contains("Install", StringComparison.OrdinalIgnoreCase)
                   && !value.Contains("Crashpad", StringComparison.OrdinalIgnoreCase)
                   && !value.Contains("Update", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtensionsPageFor(string exe)
        {
            string p = exe.ToLowerInvariant();
            if (p.Contains("msedge")) return "edge://extensions/";
            return "chrome://extensions/";
        }

        private static string Slug(string name)
        {
            var chars = name.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray();
            string s = new string(chars);
            return string.IsNullOrEmpty(s) ? "browser" : s.Length > 28 ? s[..28] : s;
        }

        private static string UniqueId(string slug, string exe)
        {
            bool taken = Recipes().Any(r => r.Id.Equals(slug, StringComparison.OrdinalIgnoreCase));
            if (!taken)
                return slug;
            uint h = 2166136261;
            foreach (char c in exe.ToLowerInvariant())
                h = (h ^ c) * 16777619;
            return slug + "-" + (h & 0xFFFF).ToString("x4");
        }

        private static string NormalizeExe(string exe)
        {
            try { return Path.GetFullPath(exe); }
            catch { return exe; }
        }

        private static bool ContainsAll(string path, params string[] parts)
            => parts.All(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));

        private static bool ContainsAny(string path, params string[] parts)
            => parts.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));

        private static bool NameIs(string path, string exeName)
            => Path.GetFileName(path).Equals(exeName, StringComparison.OrdinalIgnoreCase);

        private static string FirstExistingDir(params string[] dirs)
            => dirs.FirstOrDefault(Directory.Exists) ?? dirs[0];

        private static IEnumerable<string> NonEmpty(string? path)
            => string.IsNullOrWhiteSpace(path) ? Array.Empty<string>() : new[] { path };

        private static Recipe Chromium(
            string id, string name, string accent, string page,
            string[] exeNames, string[] candidates, string userData, Func<string, bool> match)
            => new(id, name, accent, BrowserFamily.Chromium, page, exeNames, candidates, userData, null, match);

        private static Recipe Gecko(
            string id, string name, string accent, string page,
            Func<IEnumerable<string>> exes, string geckoRoot, Func<string, bool> match)
            => new(id, name, accent, BrowserFamily.Gecko, page, Array.Empty<string>(), Array.Empty<string>(), null, geckoRoot, match, exes);

        private sealed class Recipe
        {
            public string Id { get; }
            public string Name { get; }
            public string Accent { get; }
            public BrowserFamily Family { get; }
            public string ExtensionsPage { get; }
            private readonly string[] _candidates;
            private readonly string? _userData;
            private readonly string? _geckoRoot;
            private readonly Func<string, bool> _match;
            private readonly Func<IEnumerable<string>>? _exes;

            public Recipe(
                string id, string name, string accent, BrowserFamily family, string page,
                string[] exeNames, string[] candidates, string? userData, string? geckoRoot,
                Func<string, bool> match, Func<IEnumerable<string>>? exes = null)
            {
                Id = id;
                Name = name;
                Accent = accent;
                Family = family;
                ExtensionsPage = page;
                _candidates = candidates;
                _userData = userData;
                _geckoRoot = geckoRoot;
                _match = match;
                _exes = exes;
                _ = exeNames;
            }

            public IEnumerable<string> Exes()
            {
                if (_exes != null)
                {
                    foreach (string e in _exes())
                        if (!string.IsNullOrWhiteSpace(e)) yield return e;
                }
                foreach (string e in _candidates)
                    yield return e;
            }

            public bool Matches(string exePath) => _match(exePath);
            public string? UserData() => _userData;
            public string? GeckoRoot() => _geckoRoot;
        }
    }
}
