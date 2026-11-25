using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Explicit per-use affine coordinate contract from stored patch mesh-local space
    /// to <c>UnityMeshLocal</c>.
    /// Only <see cref="TargetIdentity"/> and <see cref="StoredToUnityMeshLocal"/> are serialized inside
    /// <see cref="VertexMappingPayload"/>; <see cref="StoredSpace"/> is hydrated at runtime from the
    /// owning patch's single shared <see cref="PatchDocument.SpaceProvenance"/> without duplicating it on disk.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class MappingSpaceContract
    {
        [BlendShareField(1)] public string TargetIdentity = string.Empty;
        [BlendShareField(2)] public FbxMatrix4x4 StoredToUnityMeshLocal = FbxMatrix4x4.Identity;

        /// <summary>
        /// In-memory reference to the shared stored patch space (never serialized inside per-mesh payloads).
        /// </summary>
        public PatchSpaceProvenance StoredSpace { get; set; }
    }

    /// <summary>
    /// Coincident-vertex equivalence proof allowing safe fallback when UV0/SubMesh changes without disclosing base 3D geometry.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class CoincidentClassProof
    {
        [BlendShareField(1)] public int[] UnityVertexIndices;
        [BlendShareField(2)] public string DiscriminatorDigest = string.Empty;
    }

    /// <summary>
    /// Lazy-loaded compressed payload of an embedded <see cref="VertexMappingEntry"/>.
    /// Holds the CSR vertex-to-control-point map, coordinate space contract, and coincident-vertex proofs.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class VertexMappingPayload
    {
        /// <summary>CSR row offsets (length = UnityVertexCount + 1).</summary>
        [BlendShareField(1)] public int[] CsrRowOffsets;

        /// <summary>CSR sorted unique FBX control-point indices.</summary>
        [BlendShareField(2)] public int[] CsrControlPointIndices;

        /// <summary>Optional face-corner correspondence for corner-domain features such as UV.</summary>
        [BlendShareField(3)] public int[] FaceCornerIndices;

        /// <summary>Explicit coordinate contract from stored patch local space to target UnityMeshLocal.</summary>
        [BlendShareField(4)] public MappingSpaceContract SpaceContract = new MappingSpaceContract();

        /// <summary>Coincident-vertex equivalence class proofs for UV0/SubMesh-altered fallback.</summary>
        [BlendShareField(5)] public List<CoincidentClassProof> CoincidentClasses = new List<CoincidentClassProof>();
    }

    /// <summary>
    /// Embedded vertex mapping entry stored inside <see cref="MeshDocument.Mappings"/>.
    /// Carries lightweight match digests in the mesh header so multiple embedded mappings can be filtered
    /// in-memory without decompressing non-matching <see cref="Payload"/> entries.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class VertexMappingEntry
    {
        /// <summary>Optional human-readable label identifying this mapping variant (e.g., importer preset name).</summary>
        [BlendShareField(1)] public string Label = string.Empty;

        [BlendShareField(2)] public int UnityVertexCount;
        [BlendShareField(3)] public int ControlPointCount;

        /// <summary>Ordered Unity vertex positions digest (does not store raw 3D coordinates).</summary>
        [BlendShareField(4)] public string OrderedPositionsDigest = string.Empty;

        /// <summary>Canonical triangle incidence digest (ignores submesh grouping, preserves multiplicity and winding).</summary>
        [BlendShareField(5)] public string CanonicalTriangleIncidenceDigest = string.Empty;

        /// <summary>UV0 channel digest for fast-path verification.</summary>
        [BlendShareField(6)] public string Uv0Digest = string.Empty;

        /// <summary>Lazy-loaded CSR mapping payload.</summary>
        [BlendShareField(7)] public LazyPayload<VertexMappingPayload> Payload = LazyPayload<VertexMappingPayload>.Empty;

        public bool HasPayload => Payload != null && Payload.HasPayload;

        /// <summary>
        /// Validates a mesh's embedded mapping list for extraction and container writing without forcing
        /// decompression of Ref-backed payloads.
        /// </summary>
        public static bool TryValidateEntries(
            IReadOnlyList<VertexMappingEntry> entries,
            TopologyDigestRecord meshTopology,
            out string error)
        {
            error = null;
            if (entries == null || entries.Count == 0)
            {
                return true;
            }

            int expectedCpCount = meshTopology != null && meshTopology.ControlPointCount > 0
                ? meshTopology.ControlPointCount
                : -1;

            var seenHeaders = new HashSet<(int VertexCount, int ControlPointCount, string Positions, string Incidence, string Uv0)>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    error = $"Embedded vertex mapping at index {i} is null.";
                    return false;
                }

                if (!entry.HasPayload)
                {
                    error = $"Embedded vertex mapping at index {i} has no payload.";
                    return false;
                }

                if (entry.UnityVertexCount <= 0)
                {
                    error = $"Embedded vertex mapping at index {i} has non-positive UnityVertexCount ({entry.UnityVertexCount}).";
                    return false;
                }

                if (entry.ControlPointCount <= 0)
                {
                    error = $"Embedded vertex mapping at index {i} has non-positive ControlPointCount ({entry.ControlPointCount}).";
                    return false;
                }

                if (expectedCpCount > 0 && entry.ControlPointCount != expectedCpCount)
                {
                    error = $"Embedded vertex mapping at index {i} ControlPointCount ({entry.ControlPointCount}) does not match mesh topology ({expectedCpCount}).";
                    return false;
                }

                if (string.IsNullOrEmpty(entry.OrderedPositionsDigest))
                {
                    error = $"Embedded vertex mapping at index {i} has empty OrderedPositionsDigest.";
                    return false;
                }

                if (string.IsNullOrEmpty(entry.CanonicalTriangleIncidenceDigest))
                {
                    error = $"Embedded vertex mapping at index {i} has empty CanonicalTriangleIncidenceDigest.";
                    return false;
                }

                var headerKey = (
                    entry.UnityVertexCount,
                    entry.ControlPointCount,
                    entry.OrderedPositionsDigest,
                    entry.CanonicalTriangleIncidenceDigest,
                    entry.Uv0Digest ?? string.Empty);
                if (!seenHeaders.Add(headerKey))
                {
                    error = $"Duplicate embedded vertex mapping header at index {i} (identical vertex count, control-point count, positions, incidence, and UV0 digests).";
                    return false;
                }

                // Validate direct in-memory payloads without decoding Ref-backed payloads during raw-copy rewrites.
                if (entry.Payload.State == LazyPayloadState.Value)
                {
                    var payload = entry.Payload.DirectValue;
                    if (!TryValidatePayloadStructure(entry, payload, i, out error))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryValidatePayloadStructure(
            VertexMappingEntry entry,
            VertexMappingPayload payload,
            int index,
            out string error)
        {
            error = null;
            if (payload == null)
            {
                error = $"Embedded vertex mapping at index {index} has null direct payload.";
                return false;
            }

            if (payload.CsrRowOffsets == null || payload.CsrRowOffsets.Length != entry.UnityVertexCount + 1)
            {
                error = $"Embedded vertex mapping at index {index} has invalid CsrRowOffsets length.";
                return false;
            }

            if (payload.CsrControlPointIndices == null)
            {
                error = $"Embedded vertex mapping at index {index} has null CsrControlPointIndices.";
                return false;
            }

            var offsets = payload.CsrRowOffsets;
            var targets = payload.CsrControlPointIndices;
            if (offsets[0] != 0 || offsets[entry.UnityVertexCount] != targets.Length)
            {
                error = $"Embedded vertex mapping at index {index} has invalid CSR boundary offsets.";
                return false;
            }

            for (int row = 0; row < entry.UnityVertexCount; row++)
            {
                int start = offsets[row];
                int end = offsets[row + 1];
                if (end < start || end > targets.Length)
                {
                    error = $"Embedded vertex mapping at index {index} has non-monotonic CSR row offsets at row {row}.";
                    return false;
                }

                int prev = -1;
                for (int k = start; k < end; k++)
                {
                    int cp = targets[k];
                    if ((uint)cp >= (uint)entry.ControlPointCount || cp <= prev)
                    {
                        error = $"Embedded vertex mapping at index {index} has out-of-range or unsorted CSR control-point index at row {row}.";
                        return false;
                    }

                    prev = cp;
                }
            }

            return true;
        }
    }
}
