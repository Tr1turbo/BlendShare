using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Explicit per-use affine coordinate contract from stored patch mesh-local space
    /// (Origin FBX native local space) to <c>UnityMeshLocal</c>.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class OriginSpaceContract
    {
        [BlendShareField(1)] public string TargetIdentity = string.Empty;
        [BlendShareField(2)] public FbxMatrix4x4 StoredToUnityMeshLocal = FbxMatrix4x4.Identity;
        [BlendShareField(3)] public MeshSpaceProvenance StoredSpace = new MeshSpaceProvenance();
    }

    /// <summary>
    /// Coincident-vertex equivalence proof allowing safe fallback when UV0/SubMesh changes without disclosing base 3D geometry.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class OriginCoincidentClassProof
    {
        [BlendShareField(1)] public int[] UnityVertexIndices;
        [BlendShareField(2)] public bool AllRowsShareIdenticalCsr;
        [BlendShareField(3)] public string DiscriminatorDigest = string.Empty;
    }

    /// <summary>
    /// Frozen OriginBinding payload schema stored lazily inside BLSHARE1 v5.
    /// Enables exact standalone Unity mesh binding without distributing the base FBX.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class OriginBindingDocument
    {
        [BlendShareField(1)] public int BindingSchemaVersion = 1;
        [BlendShareField(2)] public int UnityVertexCount;
        [BlendShareField(3)] public int OriginControlPointCount;

        /// <summary>Ordered Unity vertex positions digest (does not store raw 3D coordinates).</summary>
        [BlendShareField(4)] public string OrderedPositionsDigest = string.Empty;

        /// <summary>Canonical triangle incidence digest (ignores submesh grouping, preserves multiplicity and winding).</summary>
        [BlendShareField(5)] public string CanonicalTriangleIncidenceDigest = string.Empty;

        /// <summary>UV0 channel digest for fast-path verification.</summary>
        [BlendShareField(6)] public string Uv0Digest = string.Empty;

        /// <summary>CSR row offsets (length = UnityVertexCount + 1).</summary>
        [BlendShareField(7)] public int[] CsrRowOffsets;

        /// <summary>CSR sorted unique Origin control-point indices.</summary>
        [BlendShareField(8)] public int[] CsrControlPointIndices;

        /// <summary>Optional face-corner correspondence for corner-domain features such as UV.</summary>
        [BlendShareField(9)] public int[] FaceCornerIndices;

        /// <summary>Explicit coordinate contract from stored Origin FBX local space to Origin UnityMeshLocal.</summary>
        [BlendShareField(10)] public OriginSpaceContract SpaceContract = new OriginSpaceContract();

        /// <summary>Coincident-vertex equivalence class proofs for UV0/SubMesh-altered fallback.</summary>
        [BlendShareField(11)] public List<OriginCoincidentClassProof> CoincidentClasses = new List<OriginCoincidentClassProof>();
    }
}
