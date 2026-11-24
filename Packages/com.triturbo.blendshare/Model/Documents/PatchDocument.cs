using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Root document stored in a BLSHARE1 v5 container.
    /// Carries a single shared <see cref="PatchSpaceProvenance"/> and a single shared <see cref="SkeletonDocument"/>
    /// referenced by all meshes without duplication. The user-visible patch name comes from the <c>.blendshare</c>
    /// file name and imported <c>BlendShareAsset.name</c>, never stored in the binary header.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class PatchDocument
    {
        [BlendShareField(1)] public string PatchId = string.Empty;
        [BlendShareField(2)] public PatchSpaceProvenance SpaceProvenance = new PatchSpaceProvenance();
        [BlendShareField(3)] public SkeletonDocument Skeleton = new SkeletonDocument();
        [BlendShareField(4)] public List<MeshDocument> Meshes = new List<MeshDocument>();
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
    /// Records the single shared Origin FBX coordinate space (axes and meters-per-unit scale)
    /// in which all mesh deltas, bind matrices, and skeleton bone transforms in a patch are stored.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class PatchSpaceProvenance
    {
        [BlendShareField(1)] public double OriginRawMetersPerUnit = 1d;
        [BlendShareField(2)] public FbxCoordinateAxis OriginRightAxis = FbxCoordinateAxis.PositiveX;
        [BlendShareField(3)] public FbxCoordinateAxis OriginUpAxis = FbxCoordinateAxis.PositiveY;
        [BlendShareField(4)] public FbxCoordinateAxis OriginFrontAxis = FbxCoordinateAxis.PositiveZ;

        public FbxCoordinateAxes OriginAxes => new FbxCoordinateAxes(OriginRightAxis, OriginUpAxis, OriginFrontAxis);

        public bool IsValid
        {
            get
            {
                if (double.IsNaN(OriginRawMetersPerUnit) || double.IsInfinity(OriginRawMetersPerUnit) || OriginRawMetersPerUnit <= 0d)
                {
                    return false;
                }

                int r = (int)OriginRightAxis;
                int u = (int)OriginUpAxis;
                int f = (int)OriginFrontAxis;
                int unknown = (int)FbxCoordinateAxis.Unknown;
                if (r < 0 || r >= unknown || u < 0 || u >= unknown || f < 0 || f >= unknown)
                {
                    return false;
                }

                int rd = r / 2;
                int ud = u / 2;
                int fd = f / 2;
                return rd != ud && rd != fd && ud != fd;
            }
        }

        public bool MatchesSpace(PatchSpaceProvenance other) =>
            other != null &&
            OriginAxes.Equals(other.OriginAxes) &&
            OriginRawMetersPerUnit.Equals(other.OriginRawMetersPerUnit);

        public static PatchSpaceProvenance FromMetadata(FbxCoordinateAxes axes, double metersPerUnit) =>
            new PatchSpaceProvenance
            {
                OriginRawMetersPerUnit = metersPerUnit,
                OriginRightAxis = axes.Right,
                OriginUpAxis = axes.Up,
                OriginFrontAxis = axes.Front
            };
    }

    /// <summary>
    /// Per-mesh header and feature collection inside a PatchDocument.
    /// Shares coordinate provenance with its owning <see cref="PatchDocument.SpaceProvenance"/>.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class MeshDocument
    {
        [BlendShareField(1)] public string MeshId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public string NodePath = string.Empty;
        [BlendShareField(4)] public TopologyDigestRecord Topology = new TopologyDigestRecord();
        [BlendShareField(5)] public LazyPayload<OriginBindingDocument> OriginBinding = LazyPayload<OriginBindingDocument>.Empty;
        [BlendShareField(6)] public List<BlendShareFeatureData> Features = new List<BlendShareFeatureData>();
    }
}
