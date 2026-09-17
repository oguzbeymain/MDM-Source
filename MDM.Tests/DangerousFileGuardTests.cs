using System.IO;
using MDM;
using Xunit;

namespace MDM.Tests
{
    public class DangerousFileGuardTests
    {
        [Theory]
        [InlineData("kurulum.exe", DangerousFileGuard.Risk.Executable)]
        [InlineData("script.PS1", DangerousFileGuard.Risk.Executable)]
        [InlineData("paket.msi", DangerousFileGuard.Risk.Executable)]
        [InlineData("fatura.pdf.exe", DangerousFileGuard.Risk.DisguisedExecutable)]
        [InlineData("tatil.jpg.scr", DangerousFileGuard.Risk.DisguisedExecutable)]
        [InlineData("butce.xlsm", DangerousFileGuard.Risk.Macro)]
        [InlineData("windows.iso", DangerousFileGuard.Risk.DiskImage)]
        [InlineData("film.mkv", DangerousFileGuard.Risk.None)]
        [InlineData("arsiv.rar", DangerousFileGuard.Risk.None)]
        [InlineData("rapor.pdf", DangerousFileGuard.Risk.None)]
        [InlineData("uzantisiz", DangerousFileGuard.Risk.None)]
        [InlineData("", DangerousFileGuard.Risk.None)]
        public void Risk_matches_file_kind(string fileName, DangerousFileGuard.Risk expected)
        {
            Assert.Equal(expected, DangerousFileGuard.Evaluate(fileName));
        }

        [Fact]
        public void Full_path_is_evaluated_by_file_name_only()
        {
            Assert.Equal(DangerousFileGuard.Risk.Executable,
                DangerousFileGuard.Evaluate(@"C:\Users\test\Downloads\setup.exe"));
            Assert.Equal(DangerousFileGuard.Risk.None,
                DangerousFileGuard.Evaluate(@"C:\exe.klasoru\belge.txt"));
        }

        [Fact]
        public void Internet_mark_is_written_as_zone_identifier()
        {
            string path = Path.Combine(Path.GetTempPath(), "mdm-motw-" + Path.GetRandomFileName() + ".exe");
            File.WriteAllText(path, "test");
            try
            {
                Assert.True(DangerousFileGuard.MarkAsFromInternet(path, "https://example.com/a/setup.exe"));

                string zone = File.ReadAllText(path + ":Zone.Identifier");
                Assert.Contains("[ZoneTransfer]", zone);
                Assert.Contains("ZoneId=3", zone);
                Assert.Contains("HostUrl=https://example.com/a/setup.exe", zone);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Missing_file_is_not_marked()
        {
            Assert.False(DangerousFileGuard.MarkAsFromInternet(
                Path.Combine(Path.GetTempPath(), "mdm-yok-" + Path.GetRandomFileName())));
        }
    }
}
