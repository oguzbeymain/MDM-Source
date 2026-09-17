using System.Runtime.InteropServices;
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
            string zip = $"MDM-1.0.39-{RuntimeTag()}.zip";
            Assert.True(UpdateService.TryPickAsset(Release("MDM-Setup-1.0.39.exe", zip),
                out string name, out _, out var kind));

            Assert.Equal(zip, name);
            Assert.Equal(UpdateService.PackageKind.Payload, kind);
        }

        [Fact]
        public void Foreign_architecture_zip_falls_back_to_setup()
        {
            string foreign = RuntimeInformation.ProcessArchitecture == Architecture.X86
                ? "MDM-1.0.39-win-x64.zip"
                : "MDM-1.0.39-win-x86.zip";

            Assert.True(UpdateService.TryPickAsset(Release(foreign, "MDM-Setup-1.0.39.exe"),
                out string name, out _, out var kind));

            Assert.Equal("MDM-Setup-1.0.39.exe", name);
            Assert.Equal(UpdateService.PackageKind.Installer, kind);
        }

        [Fact]
        public void Untagged_zip_is_accepted_for_every_architecture()
        {
            Assert.True(UpdateService.MatchesArchitecture("MDM-1.0.39.zip", Architecture.X64));
            Assert.True(UpdateService.MatchesArchitecture("MDM-1.0.39.zip", Architecture.X86));
        }

        [Theory]
        [InlineData("MDM-1.0.39-win-x64.zip", Architecture.X64, true)]
        [InlineData("MDM-1.0.39-win-x64.zip", Architecture.X86, false)]
        [InlineData("MDM-1.0.39-win-x86.zip", Architecture.X86, true)]
        [InlineData("MDM-1.0.39-win-x86.zip", Architecture.X64, false)]
        [InlineData("MDM-1.0.39-win-arm64.zip", Architecture.Arm64, true)]
        [InlineData("MDM-1.0.39-win-arm64.zip", Architecture.X64, false)]
        public void Architecture_tag_is_matched_exactly(string asset, Architecture arch, bool expected)
        {
            Assert.Equal(expected, UpdateService.MatchesArchitecture(asset, arch));
        }

        private static string RuntimeTag() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };

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
