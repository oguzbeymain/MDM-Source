using System.IO.Compression;
using System.Text;
using MDM;
using Xunit;

namespace MDM.Tests;

public class CrxPackerTests
{
    [Fact]
    public void Pack_writes_crx3_header_and_valid_zip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mdm-crx-" + Guid.NewGuid().ToString("N"));
        string crx = Path.Combine(Path.GetTempPath(), "mdm-" + Guid.NewGuid().ToString("N") + ".crx");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                """{"manifest_version":3,"name":"MDM","version":"1.0.0"}""", Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "background.js"), "self.ok = true;\n", Encoding.UTF8);

            CrxPacker.Pack(dir, crx);

            byte[] bytes = File.ReadAllBytes(crx);
            Assert.True(bytes.Length > 16);
            Assert.Equal((byte)'C', bytes[0]);
            Assert.Equal((byte)'r', bytes[1]);
            Assert.Equal((byte)'2', bytes[2]);
            Assert.Equal((byte)'4', bytes[3]);
            Assert.Equal(3, BitConverter.ToInt32(bytes, 4));
            int headerSize = BitConverter.ToInt32(bytes, 8);
            Assert.True(headerSize > 0 && headerSize < bytes.Length - 12);

            using var zip = new MemoryStream(bytes, 12 + headerSize, bytes.Length - 12 - headerSize);
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
            try { File.Delete(crx); } catch { /* ignore */ }
        }
    }
}
