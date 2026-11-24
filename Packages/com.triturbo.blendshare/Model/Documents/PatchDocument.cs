using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Root document stored in a BLSHARE1 v5 container.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class PatchDocument
    {
        [BlendShareField(1)] public string PatchId = string.Empty;
        [BlendShareField(2)] public string DisplayName = string.Empty;
        [BlendShareField(3)] public List<MeshDocument> Meshes = new List<MeshDocument>();
        [BlendShareField(4)] public List<ArmatureDocument> Armatures = new List<ArmatureDocument>();
    }

    /// <summary>
    /// Portable FBX topology identity record stored inside a MeshDocument.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class TopologyDigestRecord
    {
        [BlendShareField(1)] public string Hash = string.Empty;
        [BlendShareField(2)] public int ControlPointCount;
        [BlendShareField(3)] public int FaceCount;
        [BlendShareField(4)] public bool IsValid;
    }

    /// <summary>
    /// Records the stored mesh coordinate frame, origin raw axes/unit scale, and provenance transform.
    /// FrameVersion 1 = CanonicalMeshLocalV1 (meters, +X/+Y/+Z);
    /// FrameVersion 2 = OriginRawLocalV2 (native Origin FBX local coordinates &amp; units).
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class MeshSpaceProvenance
    {
        public const int CanonicalMeshLocalV1 = 1;
        public const int OriginRawLocalV2 = 2;

        [BlendShareField(1)] public int FrameVersion = CanonicalMeshLocalV1;
        [BlendShareField(2)] public double OriginRawMetersPerUnit = 1d;
        [BlendShareField(3)] public FbxCoordinateAxis OriginRightAxis = FbxCoordinateAxis.PositiveX;
        [BlendShareField(4)] public FbxCoordinateAxis OriginUpAxis = FbxCoordinateAxis.PositiveY;
        [BlendShareField(5)] public FbxCoordinateAxis OriginFrontAxis = FbxCoordinateAxis.PositiveZ;
        [BlendShareField(6)] public FbxMatrix4x4 OriginRawToCanonical = FbxMatrix4x4.Identity;

        public FbxCoordinateAxes OriginAxes => new FbxCoordinateAxes(OriginRightAxis, OriginUpAxis, OriginFrontAxis);

        public static bool IsSupportedFrameVersion(int frameVersion) =>
            frameVersion == CanonicalMeshLocalV1 || frameVersion == OriginRawLocalV2;
    }

    /// <summary>
    /// Per-mesh header and feature collection inside a PatchDocument.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class MeshDocument
    {
        [BlendShareField(1)] public string MeshId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public string NodePath = string.Empty;
        [BlendShareField(4)] public TopologyDigestRecord Topology = new TopologyDigestRecord();
        [BlendShareField(5)] public MeshSpaceProvenance SpaceProvenance = new MeshSpaceProvenance();
        [BlendShareField(6)] public LazyPayload<OriginBindingDocument> OriginBinding = LazyPayload<OriginBindingDocument>.Empty;
        [BlendShareField(7)] public List<BlendShareFeatureData> Features = new List<BlendShareFeatureData>();
    }
}
