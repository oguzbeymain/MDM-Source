using MDM;
using Xunit;

namespace MDM.Tests;

public class BrowserDiscoveryTests
{
        [Fact]
        public void Installed_returns_unique_ids_with_real_exes()
        {
            var list = BrowserDiscovery.Installed(force: true);
            var dump = Path.Combine(Path.GetTempPath(), "mdm-browsers.txt");
            File.WriteAllLines(dump, list.Select(t => $"{t.Id}\t{t.Name}\t{t.Family}\t{t.ResolveExe()}"));
            Assert.Equal(
                list.Count,
                list.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var t in list)
            {
                string? exe = t.ResolveExe();
                Assert.False(string.IsNullOrWhiteSpace(exe), t.Name);
                Assert.True(File.Exists(exe), t.Name + " -> " + exe);
            }
        }
}
