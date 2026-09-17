using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MDM
{
    /// <summary>
    /// Chromium CRX3 paketler. Aynı RSA anahtarı kalıcı id verir.
    /// </summary>
    public static class CrxPacker
    {
        private const string SignatureContext = "CRX3 SignedData\0";

        public static string KeyPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "mdm-extension.pem");

        public static string Pack(string sourceDir, string crxPath)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException(sourceDir);
            if (!File.Exists(Path.Combine(sourceDir, "manifest.json")))
                throw new InvalidOperationException("manifest.json yok.");

            Directory.CreateDirectory(Path.GetDirectoryName(crxPath)!);
            string zipPath = crxPath + ".zip.tmp";
            try
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                byte[] zip = File.ReadAllBytes(zipPath);

                using RSA rsa = LoadOrCreateKey();
                byte[] publicKey = rsa.ExportSubjectPublicKeyInfo();
                byte[] crxId = CrxIdFromPublicKey(publicKey);
                byte[] signedHeader = LengthDelimited(1, crxId);

                var signed = new MemoryStream();
                signed.Write(Encoding.ASCII.GetBytes(SignatureContext));
                WriteUInt32Le(signed, (uint)signedHeader.Length);
                signed.Write(signedHeader);
                signed.Write(zip);
                byte[] signature = rsa.SignData(signed.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                byte[] proof = Concat(LengthDelimited(1, publicKey), LengthDelimited(2, signature));
                byte[] header = Concat(LengthDelimited(2, proof), LengthDelimited(10000, signedHeader));

                using var output = new FileStream(crxPath, FileMode.Create, FileAccess.Write, FileShare.None);
                output.Write("Cr24"u8);
                WriteUInt32Le(output, 3);
                WriteUInt32Le(output, (uint)header.Length);
                output.Write(header);
                output.Write(zip);
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }
            }

            return crxPath;
        }

        public static byte[] CrxIdFromPublicKey(byte[] spki)
        {
            byte[] hash = SHA256.HashData(spki);
            var id = new byte[16];
            Buffer.BlockCopy(hash, 0, id, 0, 16);
            return id;
        }

        public static string ExtensionIdFromPublicKey(byte[] spki)
        {
            byte[] id = CrxIdFromPublicKey(spki);
            var sb = new StringBuilder(32);
            foreach (byte b in id)
            {
                sb.Append((char)('a' + (b >> 4)));
                sb.Append((char)('a' + (b & 0x0F)));
            }
            return sb.ToString();
        }

        private static RSA LoadOrCreateKey()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            if (File.Exists(KeyPath))
            {
                string pem = File.ReadAllText(KeyPath);
                var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                return rsa;
            }

            var created = RSA.Create(2048);
            File.WriteAllText(KeyPath, created.ExportPkcs8PrivateKeyPem());
            return created;
        }

        private static byte[] LengthDelimited(int fieldNumber, byte[] data)
        {
            byte[] tag = EncodeVarint((uint)((fieldNumber << 3) | 2));
            byte[] len = EncodeVarint((uint)data.Length);
            return Concat(tag, len, data);
        }

        private static byte[] EncodeVarint(uint value)
        {
            var list = new List<byte>(5);
            while (value >= 0x80)
            {
                list.Add((byte)(value | 0x80));
                value >>= 7;
            }
            list.Add((byte)value);
            return list.ToArray();
        }

        private static void WriteUInt32Le(Stream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var buf = new byte[n];
            int o = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, buf, o, p.Length);
                o += p.Length;
            }
            return buf;
        }
    }
}
