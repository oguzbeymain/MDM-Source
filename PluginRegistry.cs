using System.IO;
using System.Linq;

namespace MDM
{
    public static class PluginRegistry
    {
        private static readonly List<IMdmPlugin> Plugins = new();
        private static readonly object Gate = new();

        public static void Register(IMdmPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            lock (Gate)
            {
                if (Plugins.Any(p => p.Name == plugin.Name))
                    return;
                Plugins.Add(plugin);
            }
        }

        public static IReadOnlyList<string> LoadedNames
        {
            get { lock (Gate) return Plugins.Select(p => p.Name).ToList(); }
        }

        public static ITransferBackend? TryCreate(string url, string savePath, int threadCount)
        {
            lock (Gate)
            {
                foreach (var plugin in Plugins)
                {
                    try
                    {
                        var backend = plugin.TryCreate(url, savePath, threadCount);
                        if (backend != null)
                            return backend;
                    }
                    catch
                    {
                        /* plugin fault is isolated */
                    }
                }
            }
            return null;
        }

        public static int LoadFromFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return 0;

            int loaded = 0;
            foreach (string dll in Directory.GetFiles(folder, "*.dll"))
            {
                try
                {
                    var alc = new System.Runtime.Loader.AssemblyLoadContext(Path.GetFileName(dll), isCollectible: true);
                    var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (!typeof(IMdmPlugin).IsAssignableFrom(type) || type.IsAbstract)
                            continue;
                        if (Activator.CreateInstance(type) is IMdmPlugin plugin)
                        {
                            Register(plugin);
                            loaded++;
                        }
                    }
                }
                catch
                {
                    /* skip bad plugin */
                }
            }
            return loaded;
        }

        internal static void ClearForTests()
        {
            lock (Gate) Plugins.Clear();
        }
    }
}
