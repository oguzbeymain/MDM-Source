using System.Text.Json;
using MDM;
using Xunit;

namespace MDM.Tests
{
    /// <summary>
    /// Yayındaki dosya seçimi: setup exe'si uygulama paketi sanılıp kurulum klasörüne
    /// kopyalanınca güncelleme sessizce başarısız oluyordu.
    /// </summary>
    public class UpdateAssetTests
    {
        private static JsonElement Release(params string[] assetNames)
        {
            var assets = string.Join(",", assetNames.Select(n =>
                $"{{\"name\":\"{n}\",\"browser_download_url\":\"https://example.test/{n}\"}}"));
            var doc = JsonDocument.Parse($"{{\"tag_name\":\"v1.0.39\",\"assets\":[{assets}]}}");
            return doc.RootElement.Clone();
        }

        [Fact]
        public void Setup_only_release_is_installed_not_copied()
        {
            Assert.True(UpdateService.TryPickAsset(Release("MDM-Setup-1.0.39.exe"),
                out string name, out string url, out var kind));

            Assert.Equal("MDM-Setup-1.0.39.exe", name);
            Assert.Equal("https://example.test/MDM-Setup-1.0.39.exe", url);
            Assert.Equal(UpdateService.PackageKind.Installer, kind);
        }

        [Fact]
        public void Zip_wins_over_setup_exe()
        {
            Assert.True(UpdateService.TryPickAsset(Release("MDM-Setup-1.0.39.exe", "MDM-1.0.39-win-x86.zip"),
                out string name, out _, out var kind));

            Assert.Equal("MDM-1.0.39-win-x86.zip", name);
            Assert.Equal(UpdateService.PackageKind.Payload, kind);
        }

        [Fact]
        public void Updater_exe_is_never_picked()
        {
            Assert.False(UpdateService.TryPickAsset(Release("MDM.Updater.exe"), out _, out _, out _));
        }

        [Fact]
        public void Plain_app_exe_is_treated_as_payload()
        {
            Assert.True(UpdateService.TryPickAsset(Release("MDM.exe"), out string name, out _, out var kind));

            Assert.Equal("MDM.exe", name);
            Assert.Equal(UpdateService.PackageKind.Payload, kind);
        }

        [Fact]
        public void Release_without_usable_asset_fails()
        {
            Assert.False(UpdateService.TryPickAsset(Release("notes.txt", "checksums.sha256"), out _, out _, out _));
        }
    }
}
