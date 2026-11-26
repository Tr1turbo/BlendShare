using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Root document stored in a BLSH v1 container.
    /// Carries a single shared <see cref="PatchSpaceProvenance"/>, an optional shared <see cref="SkeletonDocument"/>,
    /// and per-mesh <see cref="MeshDocument"/> records.
    /// The user-visible patch name comes solely from the <c>.blendshare</c> file name and imported <c>BlendShareAsset.name</c>.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class PatchDocument
    {
        [BlendShareField(1)] public string PatchId = string.Empty;
        [BlendShareField(2)] public PatchSpaceProvenance SpaceProvenance = new PatchSpaceProvenance();
        [BlendShareField(3)] public SkeletonDocument Skeleton;
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
    /// Records the single shared FBX coordinate space (axes and meters-per-unit scale)
    /// in which patch scene metadata and fallback provenances are declared.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class PatchSpaceProvenance
    {
        [BlendShareField(1)] public double MetersPerUnit = 1d;
        [BlendShareField(2)] public FbxCoordinateAxis RightAxis = FbxCoordinateAxis.PositiveX;
        [BlendShareField(3)] public FbxCoordinateAxis UpAxis = FbxCoordinateAxis.PositiveY;
        [BlendShareField(4)] public FbxCoordinateAxis FrontAxis = FbxCoordinateAxis.PositiveZ;

        public FbxCoordinateAxes Axes => new FbxCoordinateAxes(RightAxis, UpAxis, FrontAxis);

        public bool IsValid
        {
            get
            {
                if (double.IsNaN(MetersPerUnit) || double.IsInfinity(MetersPerUnit) || MetersPerUnit <= 0d)
                {
                    return false;
                }

                int r = (int)RightAxis;
                int u = (int)UpAxis;
                int f = (int)FrontAxis;
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
            Axes.Equals(other.Axes) &&
            MetersPerUnit.Equals(other.MetersPerUnit);

        public static PatchSpaceProvenance FromMetadata(FbxCoordinateAxes axes, double metersPerUnit) =>
            new PatchSpaceProvenance
            {
                MetersPerUnit = metersPerUnit,
                RightAxis = axes.Right,
                UpAxis = axes.Up,
                FrontAxis = axes.Front
            };
    }

    /// <summary>
    /// Per-mesh header, optional geometry sketch, effective physical quantization scale, embedded vertex mappings,
    /// and feature collection inside a <see cref="PatchDocument"/>.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class MeshDocument
    {
        [BlendShareField(1)] public string MeshId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public string NodePath = string.Empty;
        [BlendShareField(4)] public TopologyDigestRecord Topology = new TopologyDigestRecord();
        [BlendShareField(5)] public List<VertexMappingEntry> Mappings = new List<VertexMappingEntry>();
        [BlendShareField(6)] public List<BlendShareFeatureData> Features = new List<BlendShareFeatureData>();
        [BlendShareField(7)] public GeometrySketch Sketch;

        /// <summary>
        /// Effective physical meters per stored mesh-local coordinate unit (<c>originUnitMeters * sigma_max(GeometryToWorld)</c>).
        /// Preserves accurate physical quantization error budgets under Blender <c>All Local</c> (<c>0.01 * 100 = 1.0 m</c>)
        /// across both initial extraction and subsequent container re-encodes. Zero means unset (falls back to <see cref="PatchSpaceProvenance.MetersPerUnit"/>).
        /// </summary>
        [BlendShareField(8)] public double EffectivePositionUnitMeters;

        public bool HasEmbeddedMappings
        {
            get
            {
                if (Mappings == null)
                {
                    return false;
                }

                for (int i = 0; i < Mappings.Count; i++)
                {
                    if (Mappings[i]?.HasPayload == true)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
