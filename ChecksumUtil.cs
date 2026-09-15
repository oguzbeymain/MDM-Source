using System.IO;
using System.Security.Cryptography;

namespace MDM
{
    public static class ChecksumUtil
    {
        public static bool TryVerifyFile(string path, string? type, string? expectedHex)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(expectedHex))
                return true;
            if (!File.Exists(path))
                return false;

            string want = expectedHex.Trim().Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            string got = HashFile(path, type);
            return got.Length > 0 && got.Equals(want, StringComparison.OrdinalIgnoreCase);
        }

        public static string HashFile(string path, string type)
        {
            using var stream = File.OpenRead(path);
            byte[] hash = NormalizeType(type) switch
            {
                "md5" => MD5.HashData(stream),
                "sha1" => SHA1.HashData(stream),
                "sha256" => SHA256.HashData(stream),
                _ => Array.Empty<byte>()
            };
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string NormalizeType(string? type)
        {
            string t = (type ?? "").Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
            return t switch
            {
                "sha256" or "sha2" => "sha256",
                "sha1" or "sha" => "sha1",
                "md5" => "md5",
                _ => t
            };
        }
    }
}
