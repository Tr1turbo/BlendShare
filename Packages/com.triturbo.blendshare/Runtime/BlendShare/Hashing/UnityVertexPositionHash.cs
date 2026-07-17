using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Triturbo.BlendShare.Hashing
{
    /// <summary>
    /// Creates stable hashes for Unity mesh vertex positions.
    /// </summary>
    public static class UnityVertexPositionHash
    {
        private const int DefaultShortHashLength = 8;
        private const string AlgorithmVersion = "BlendShare.UnityVertexPositionHash.v2";

        public static string Calculate(Mesh mesh)
        {
            return TryCalculate(mesh, out string hash) ? hash : string.Empty;
        }

        /// <summary>
        /// Tries to calculate the position hash without treating unavailable vertex data as an empty mesh.
        /// </summary>
        public static bool TryCalculate(Mesh mesh, out string hash)
        {
            hash = string.Empty;
            if (mesh == null)
            {
                return false;
            }

            try
            {
                var vertices = mesh.vertices;
                int vertexCount = mesh.vertexCount;
                if (vertices == null || vertices.Length != vertexCount)
                {
                    return false;
                }

                hash = CalculateVertices(vertices);
                return true;
            }
            catch (Exception)
            {
                hash = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Calculates the stable position hash for an explicitly acquired Unity vertex array.
        /// </summary>
        public static string CalculateVertices(Vector3[] vertices)
        {
            using (var sha256 = SHA256.Create())
            using (var cryptoStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(cryptoStream))
            {
                writer.Write(AlgorithmVersion);
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

                return BlendShareHashUtility.ToLowerHex(sha256.Hash);
            }
        }

        public static string Shorten(string hash, int length = DefaultShortHashLength)
        {
            if (string.IsNullOrEmpty(hash) || length <= 0)
            {
                return string.Empty;
            }

            return hash.Length <= length ? hash : hash.Substring(0, length);
        }

        private static float NormalizeZero(float value)
        {
            return value == 0f ? 0f : value;
        }
    }
}
