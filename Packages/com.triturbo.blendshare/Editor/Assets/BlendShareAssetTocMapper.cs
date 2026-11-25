using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Primitives;
using Triturbo.BlendShare.Assets;

namespace Triturbo.BlendShare.EditorAssets
{
    /// <summary>
    /// Converts between Runtime's ultra-thin BlendShareAsset TOC cache and Container.Editor's immutable ContainerToc.
    /// Keeps Container.Editor 100% free of UnityEngine.Object / AssetDatabase dependencies.
    /// </summary>
    public static class BlendShareAssetTocMapper
    {
        public static PatchSpaceProvenance GetSpaceProvenance(BlendShareAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            return new PatchSpaceProvenance
            {
                MetersPerUnit = asset.MetersPerUnit,
                RightAxis = (FbxCoordinateAxis)asset.RightAxis,
                UpAxis = (FbxCoordinateAxis)asset.UpAxis,
                FrontAxis = (FbxCoordinateAxis)asset.FrontAxis
            };
        }

        public static void PopulateAsset(BlendShareAsset asset, ContainerToc toc, IReadOnlyDictionary<string, string> meshNamesById)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (toc == null) throw new ArgumentNullException(nameof(toc));

            var tocEntries = new BlendShareTocEntry[toc.Entries.Count];
            for (int i = 0; i < toc.Entries.Count; i++)
            {
                var src = toc.Entries[i];
                tocEntries[i] = new BlendShareTocEntry
                {
                    EntryIndex = src.EntryIndex,
                    SectionKind = (byte)src.SectionKind,
                    OwnerMeshId = src.OwnerMeshId ?? string.Empty,
                    FeatureId = src.FeatureId ?? string.Empty,
                    TypeId = src.TypeId ?? string.Empty,
                    SchemaVersion = src.SchemaVersion,
                    Required = src.Required,
                    CodecId = src.CodecId ?? string.Empty,
                    CodecVersion = src.CodecVersion,
                    Slice = ToByteRange(src.Slice),
                    UncompressedBytes = src.UncompressedBytes,
                    Sha256Hex = ToLowerHex(src.Sha256)
                };
            }

            var meshes = new BlendShareMeshToc[toc.Meshes.Count];
            for (int m = 0; m < toc.Meshes.Count; m++)
            {
                var mesh = toc.Meshes[m];
                var featIndices = new int[mesh.Features.Count];
                for (int f = 0; f < mesh.Features.Count; f++)
                {
                    featIndices[f] = mesh.Features[f].Entry.EntryIndex;
                }

                string displayMeshName = mesh.MeshId;
                if (meshNamesById != null &&
                    meshNamesById.TryGetValue(mesh.MeshId, out string resolvedName) &&
                    !string.IsNullOrWhiteSpace(resolvedName))
                {
                    displayMeshName = resolvedName;
                }

                meshes[m] = new BlendShareMeshToc(mesh.MeshId, displayMeshName, mesh.HeaderEntry.EntryIndex, featIndices);
            }

            asset.InitializeToc(
                toc.FormatVersion,
                toc.PatchId,
                toc.SourceRevision,
                toc.SpaceProvenance.MetersPerUnit,
                (byte)toc.SpaceProvenance.RightAxis,
                (byte)toc.SpaceProvenance.UpAxis,
                (byte)toc.SpaceProvenance.FrontAxis,
                toc.TotalFileLength,
                ToByteRange(toc.ManifestSlice),
                ToByteRange(toc.IndexTableSlice),
                ToByteRange(toc.PayloadSectionSlice),
                toc.SkeletonEntry != null ? toc.SkeletonEntry.EntryIndex : -1,
                meshes,
                tocEntries);
        }

        public static ContainerToc ToContainerToc(BlendShareAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            byte[] revisionBytes = ParseHex32(asset.SourceRevision, nameof(asset.SourceRevision));
            var rawEntries = asset.Entries;
            var entries = new List<ContainerIndexEntry>(rawEntries.Count);
            for (int i = 0; i < rawEntries.Count; i++)
            {
                var src = rawEntries[i];
                entries.Add(ContainerIndexEntry.Create(
                    src.EntryIndex,
                    (ContainerSectionKind)src.SectionKind,
                    src.OwnerMeshId,
                    src.FeatureId,
                    src.TypeId,
                    src.SchemaVersion,
                    src.Required,
                    src.CodecId,
                    src.CodecVersion,
                    ToByteSlice(src.Slice),
                    src.UncompressedBytes,
                    ParseHex32(src.Sha256Hex, "Sha256Hex")));
            }

            return ContainerToc.FromCachedEntries(
                asset.PatchId,
                GetSpaceProvenance(asset),
                revisionBytes,
                ToByteSlice(asset.ManifestSlice),
                ToByteSlice(asset.IndexTableSlice),
                ToByteSlice(asset.PayloadSectionSlice),
                entries);
        }

        private static BlendShareByteRange ToByteRange(ByteSlice slice) => new BlendShareByteRange(slice.Offset, slice.Length);
        private static ByteSlice ToByteSlice(BlendShareByteRange range) => new ByteSlice(range.Offset, range.Length);

        public static string ToLowerHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var chars = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }
            return new string(chars);
        }

        public static byte[] ParseHex32(string hex, string fieldName)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 64)
            {
                throw new InvalidDataException($"Expected 64-character hex SHA-256 for {fieldName}.");
            }

            var bytes = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                int hi = HexNibble(hex[i * 2]);
                int lo = HexNibble(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                {
                    throw new InvalidDataException($"Invalid hex character in {fieldName}.");
                }
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
