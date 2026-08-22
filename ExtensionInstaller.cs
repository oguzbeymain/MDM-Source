using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DownloadMuck
{
    /// <summary>
    /// Eklenti çıktı klasörü: uygulamanın yanındaki DownloadMuck_Eklenti
    /// (tarayıcıda paketlenmemiş olarak bu klasör yüklenir). Kurulum HTML'i açılmaz.
    /// </summary>
    public static class ExtensionInstaller
    {
        public static string InstallRoot =>
            Path.Combine(AppContext.BaseDirectory, "DownloadMuck_Eklenti");

        public static void EnsureInstalled()
        {
            try
            {
                // Eski AppData ForceInstall / sahte ID kalıntılarını sessizce temizle
                CleanupBrokenAutoInstallArtifacts();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtensionInstaller: {ex.Message}");
            }
        }

        private static void CleanupBrokenAutoInstallArtifacts()
        {
            const string fakeId = "abcdefghijklmnopqrstuvwxyzabcdef";
            string appDataExt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "BrowserExtension");

            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google\\Chrome\\User Data\\External Extensions", fakeId + ".json"));
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft\\Edge\\User Data\\External Extensions", fakeId + ".json"));
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BraveSoftware\\Brave-Browser\\User Data\\External Extensions", fakeId + ".json"));

            TryDeleteRegistryValue(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist", "1");
            TryDeleteRegistryValue(@"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist", "1");
            TryDeleteRegistryValue(@"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist", "1");

            TryDeleteRegistryKey(@"Software\Google\Chrome\Extensions\" + fakeId);
            TryDeleteRegistryKey(@"Software\Microsoft\Edge\Extensions\" + fakeId);
            TryDeleteRegistryKey(@"Software\BraveSoftware\Brave\Extensions\" + fakeId);

            TryDeleteFile(Path.Combine(appDataExt, "update.xml"));
            TryDeleteFile(Path.Combine(appDataExt, "KURULUM.html"));
            TryDeleteFile(Path.Combine(appDataExt, "loader.html"));
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }

        private static void TryDeleteRegistryValue(string subKey, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { /* ignore */ }
        }

        private static void TryDeleteRegistryKey(string subKey)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
            catch { /* ignore */ }
        }
    }
}
