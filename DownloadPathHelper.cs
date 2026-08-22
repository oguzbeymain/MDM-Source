using System.IO;

namespace DownloadMuck
{
    internal static class DownloadPathHelper
    {
        public static string StatePath(string filePath) => filePath + ".mdmstate";

        public static void MoveFileAndState(string fromPath, string toPath)
        {
            if (string.Equals(
                    Path.GetFullPath(fromPath),
                    Path.GetFullPath(toPath),
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (File.Exists(fromPath))
                File.Move(fromPath, toPath);

            string fromState = StatePath(fromPath);
            string toState = StatePath(toPath);
            if (!File.Exists(fromState)) return;

            if (File.Exists(toState))
                File.Delete(toState);
            File.Move(fromState, toState);
        }

        public static void DeleteFileAndState(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { /* ignore */ }

            try
            {
                string state = StatePath(filePath);
                if (File.Exists(state))
                    File.Delete(state);
            }
            catch { /* ignore */ }
        }
    }
}
