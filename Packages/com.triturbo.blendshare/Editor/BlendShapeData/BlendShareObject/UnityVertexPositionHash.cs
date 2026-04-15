using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal static class UnityVertexPositionHash
    {
        private const int DefaultShortHashLength = 8;
        private const string HexDigits = "0123456789abcdef";

        public static string Calculate(Mesh mesh)
        {
            if (mesh == null)
            {
                return string.Empty;
            }

            return Calculate(mesh.vertices);
        }

        public static string Shorten(string hash, int length = DefaultShortHashLength)
        {
            if (string.IsNullOrEmpty(hash) || length <= 0)
            {
                return string.Empty;
            }

            return hash.Length <= length ? hash : hash.Substring(0, length);
        }

        private static string Calculate(Vector3[] vertices)
        {
            using (SHA1 sha1 = SHA1.Create())
            using (var cryptoStream = new CryptoStream(Stream.Null, sha1, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(cryptoStream))
            {
                writer.Write(vertices?.Length ?? 0);

                if (vertices != null)
                {
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 vertex = vertices[i];
                        writer.Write(NormalizeZero(vertex.x));
                        writer.Write(NormalizeZero(vertex.y));
                        writer.Write(NormalizeZero(vertex.z));
                    }
                }

                writer.Flush();
                cryptoStream.FlushFinalBlock();

                return ToHexString(sha1.Hash);
            }
        }

        private static float NormalizeZero(float value)
        {
            return value == 0f ? 0f : value;
        }

        private static string ToHexString(byte[] bytes)
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
