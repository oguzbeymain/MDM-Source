using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DownloadMuck
{
    /// <summary>Windows açılışında uygulamayı otomatik başlatır (HKCU Run).</summary>
    public static class AutoStartHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MuckDownloadManager";

        public static string ExePath =>
            Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "DownloadMuck.exe");

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch
            {
                return false;
            }
        }

        public static void Enable(bool startMinimized = true)
        {
            try
            {
                string path = ExePath;
                if (!File.Exists(path)) return;
                string args = startMinimized ? $"\"{path}\" --background" : $"\"{path}\"";
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key?.SetValue(ValueName, args);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AutoStart Enable: {ex.Message}");
            }
        }

        public static void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AutoStart Disable: {ex.Message}");
            }
        }

        /// <summary>Ilk kurulumda ayarlara göre kaydeder.</summary>
        public static void EnsureRegistered()
        {
            var s = AppSettingsStore.Load();
            if (s.AutoStart)
                Enable(s.AutoStartMinimized);
            else
                Disable();
        }
    }
}
