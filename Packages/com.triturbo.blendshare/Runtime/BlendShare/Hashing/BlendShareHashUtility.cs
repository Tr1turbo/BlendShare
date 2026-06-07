using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Triturbo.BlendShare.Hashing
{
    /// <summary>
    /// Shared hashing helpers for persisted BlendShare asset identity values.
    /// </summary>
    public static class BlendShareHashUtility
    {
        private const string HexDigits = "0123456789abcdef";

        public static string Sha256File(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Sha256Stream(stream);
            }
        }

        public static string Sha256Stream(Stream stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using (var sha256 = SHA256.Create())
            {
                return ToLowerHex(sha256.ComputeHash(stream));
            }
        }

        public static string Sha256String(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                return ToLowerHex(sha256.ComputeHash(bytes));
            }
        }

        public static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte value = bytes[i];
                chars[i * 2] = HexDigits[value >> 4];
                chars[i * 2 + 1] = HexDigits[value & 0xF];
            }

            return new string(chars);
        }
    }
}
