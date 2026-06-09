using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShare.Fbx.Ufbx;

namespace Triturbo.BlendShare.Hashing
{
    /// <summary>
    /// Creates stable hashes for FBX control-point polygon topology.
    /// </summary>
    public static class FbxTopologyHash
    {
        private const string AlgorithmVersion = "BlendShare.FbxTopologyHash.v1";

        public static FbxTopologySignature Calculate(UfbxMesh mesh, int controlPointLimit = -1)
        {
            if (mesh == null)
            {
                return new FbxTopologySignature();
            }

            try
            {
                return Calculate(
                    mesh.ControlPointCount,
                    mesh.GetFaceSizes(),
                    mesh.GetFaceControlPointIndices(),
                    controlPointLimit);
            }
            catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                return Invalid(controlPointLimit >= 0 ? controlPointLimit : mesh.ControlPointCount, mesh.FaceCount);
            }
        }

        public static FbxTopologySignature Calculate(
            int controlPointCount,
            int[] faceSizes,
            int[] faceControlPointIndices,
            int controlPointLimit = -1)
        {
            if (controlPointCount < 0 || faceSizes == null || faceControlPointIndices == null)
            {
                return Invalid(controlPointLimit >= 0 ? controlPointLimit : controlPointCount, faceSizes?.Length ?? -1);
            }

            int effectiveLimit = controlPointLimit >= 0 ? controlPointLimit : controlPointCount;
            if (effectiveLimit < 0 || effectiveLimit > controlPointCount)
            {
                return Invalid(effectiveLimit, faceSizes.Length);
            }

            if (!TryBuildCanonicalFaces(faceSizes, faceControlPointIndices, effectiveLimit, out var faces))
            {
                return Invalid(effectiveLimit, faceSizes.Length);
            }

            faces.Sort(CompareFaces);
            string hash = CalculateHash(effectiveLimit, faces);
            return new FbxTopologySignature(hash, effectiveLimit, faces.Count, true);
        }

        private static FbxTopologySignature Invalid(int controlPointCount, int faceCount)
        {
            return new FbxTopologySignature(string.Empty, controlPointCount, faceCount, false);
        }

        private static bool TryBuildCanonicalFaces(
            int[] faceSizes,
            int[] faceControlPointIndices,
            int controlPointLimit,
            out List<int[]> faces)
        {
            faces = new List<int[]>(faceSizes.Length);
            int offset = 0;
            for (int i = 0; i < faceSizes.Length; i++)
            {
                int size = faceSizes[i];
                if (size < 0 || offset + size > faceControlPointIndices.Length)
                {
                    return false;
                }

                bool include = true;
                for (int j = 0; j < size; j++)
                {
                    int index = faceControlPointIndices[offset + j];
                    if (index < 0)
                    {
                        return false;
                    }

                    if (index >= controlPointLimit)
                    {
                        include = false;
                    }
                }

                if (include)
                {
                    var face = new int[size];
                    Array.Copy(faceControlPointIndices, offset, face, 0, size);
                    faces.Add(CanonicalizeCyclic(face));
                }

                offset += size;
            }

            return offset == faceControlPointIndices.Length;
        }

        private static int[] CanonicalizeCyclic(int[] face)
        {
            if (face == null || face.Length <= 1)
            {
                return face ?? Array.Empty<int>();
            }

            int bestStart = 0;
            for (int candidate = 1; candidate < face.Length; candidate++)
            {
                if (CompareRotation(face, candidate, bestStart) < 0)
                {
                    bestStart = candidate;
                }
            }

            var result = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
            {
                result[i] = face[(bestStart + i) % face.Length];
            }

            return result;
        }

        private static int CompareRotation(int[] face, int leftStart, int rightStart)
        {
            for (int i = 0; i < face.Length; i++)
            {
                int left = face[(leftStart + i) % face.Length];
                int right = face[(rightStart + i) % face.Length];
                int compare = left.CompareTo(right);
                if (compare != 0)
                {
                    return compare;
                }
            }

            return 0;
        }

        private static int CompareFaces(int[] left, int[] right)
        {
            int lengthCompare = (left?.Length ?? 0).CompareTo(right?.Length ?? 0);
            if (lengthCompare != 0)
            {
                return lengthCompare;
            }

            for (int i = 0; i < (left?.Length ?? 0); i++)
            {
                int compare = left[i].CompareTo(right[i]);
                if (compare != 0)
                {
                    return compare;
                }
            }

            return 0;
        }

        private static string CalculateHash(int controlPointCount, IReadOnlyList<int[]> faces)
        {
            using (var sha256 = SHA256.Create())
            using (var cryptoStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(cryptoStream))
            {
                writer.Write(AlgorithmVersion);
                writer.Write(controlPointCount);
                writer.Write(faces?.Count ?? 0);

                foreach (var face in faces ?? Enumerable.Empty<int[]>())
                {
                    writer.Write(face?.Length ?? 0);
                    if (face == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < face.Length; i++)
                    {
                        writer.Write(face[i]);
                    }
                }

                writer.Flush();
                cryptoStream.FlushFinalBlock();
                return BlendShareHashUtility.ToLowerHex(sha256.Hash);
            }
        }
    }
}
