using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Runtime.Assets
{
    /// <summary>
    /// Unity-serializable byte range (offset + length) cached in <see cref="BlendShareAsset"/> TOC records.
    /// Keeps Runtime 100% decoupled from Editor-only Primitives and Fbx assemblies.
    /// </summary>
    [Serializable]
    public struct BlendShareByteRange
    {
        public long Offset;
        public long Length;

        public long End => checked(Offset + Length);

        public BlendShareByteRange(long offset, long length)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// Serialized TOC index record cached inside BlendShareAsset (Library/Artifacts).
    /// Contains only deterministic offsets, lengths, identifiers, and digests—zero paths, InstanceIDs, or timestamps.
    /// </summary>
    [Serializable]
    public struct BlendShareTocEntry
    {
        public int EntryIndex;
        public byte SectionKind;
        public string OwnerMeshId;
        public string FeatureId;
        public string TypeId;
        public int SchemaVersion;
        public bool Required;
        public string CodecId;
        public int CodecVersion;
        public BlendShareByteRange Slice;
        public long UncompressedBytes;
        public string Sha256Hex;
    }

    [Serializable]
    public sealed class BlendShareMeshToc
    {
        [SerializeField] private string m_MeshId = string.Empty;
        [SerializeField] private string m_Name = string.Empty;
        [SerializeField] private int m_HeaderEntryIndex = -1;
        [SerializeField] private int[] m_FeatureEntryIndices = Array.Empty<int>();

        public string MeshId => m_MeshId ?? string.Empty;
        public string Name => m_Name ?? string.Empty;
        public int HeaderEntryIndex => m_HeaderEntryIndex;
        public IReadOnlyList<int> FeatureEntryIndices => m_FeatureEntryIndices ?? Array.Empty<int>();

        public BlendShareMeshToc()
        {
        }

        public BlendShareMeshToc(string meshId, string name, int headerEntryIndex, int[] featureEntryIndices)
        {
            m_MeshId = meshId ?? string.Empty;
            m_Name = name ?? string.Empty;
            m_HeaderEntryIndex = headerEntryIndex;
            m_FeatureEntryIndices = featureEntryIndices ?? Array.Empty<int>();
        }
    }

    /// <summary>
    /// Ultra-thin main imported ScriptableObject for a .blendshare file.
    /// Acts strictly as an identity shell and pre-indexed byte-offset TOC cache; holds zero heavy payloads and zero AppliedSettings.
    /// </summary>
    public sealed class BlendShareAsset : ScriptableObject
    {
        [SerializeField] private int m_FormatVersion = 5;
        [SerializeField] private string m_PatchId = string.Empty;
        [SerializeField] private string m_DisplayName = string.Empty;
        [SerializeField] private string m_SourceRevision = string.Empty;
        [SerializeField] private long m_TotalFileLength;
        [SerializeField] private BlendShareByteRange m_ManifestSlice;
        [SerializeField] private BlendShareByteRange m_IndexTableSlice;
        [SerializeField] private BlendShareByteRange m_PayloadSectionSlice;
        [SerializeField] private int m_ArmaturesEntryIndex = -1;
        [SerializeField] private BlendShareMeshToc[] m_Meshes = Array.Empty<BlendShareMeshToc>();
        [SerializeField] private BlendShareTocEntry[] m_Entries = Array.Empty<BlendShareTocEntry>();

        public int FormatVersion => m_FormatVersion;
        public string PatchId => m_PatchId ?? string.Empty;
        public string DisplayName => m_DisplayName ?? string.Empty;
        public string SourceRevision => m_SourceRevision ?? string.Empty;
        public long TotalFileLength => m_TotalFileLength;
        public BlendShareByteRange ManifestSlice => m_ManifestSlice;
        public BlendShareByteRange IndexTableSlice => m_IndexTableSlice;
        public BlendShareByteRange PayloadSectionSlice => m_PayloadSectionSlice;
        public int ArmaturesEntryIndex => m_ArmaturesEntryIndex;
        public IReadOnlyList<BlendShareMeshToc> Meshes => m_Meshes ?? Array.Empty<BlendShareMeshToc>();
        public IReadOnlyList<BlendShareTocEntry> Entries => m_Entries ?? Array.Empty<BlendShareTocEntry>();

        public void InitializeToc(
            int formatVersion,
            string patchId,
            string displayName,
            string sourceRevision,
            long totalFileLength,
            BlendShareByteRange manifestSlice,
            BlendShareByteRange indexTableSlice,
            BlendShareByteRange payloadSectionSlice,
            int armaturesEntryIndex,
            BlendShareMeshToc[] meshes,
            BlendShareTocEntry[] entries)
        {
            m_FormatVersion = formatVersion;
            m_PatchId = patchId ?? string.Empty;
            m_DisplayName = displayName ?? string.Empty;
            m_SourceRevision = sourceRevision ?? string.Empty;
            m_TotalFileLength = totalFileLength;
            m_ManifestSlice = manifestSlice;
            m_IndexTableSlice = indexTableSlice;
            m_PayloadSectionSlice = payloadSectionSlice;
            m_ArmaturesEntryIndex = armaturesEntryIndex;
            m_Meshes = meshes ?? Array.Empty<BlendShareMeshToc>();
            m_Entries = entries ?? Array.Empty<BlendShareTocEntry>();
        }

        public bool TryGetMesh(string meshId, out BlendShareMeshToc mesh)
        {
            if (m_Meshes != null && !string.IsNullOrEmpty(meshId))
            {
                for (int i = 0; i < m_Meshes.Length; i++)
                {
                    if (m_Meshes[i] != null && string.Equals(m_Meshes[i].MeshId, meshId, StringComparison.Ordinal))
                    {
                        mesh = m_Meshes[i];
                        return true;
                    }
                }
            }

            mesh = null;
            return false;
        }
    }
}
