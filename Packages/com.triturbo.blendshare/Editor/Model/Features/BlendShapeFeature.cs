using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Model.Features
{
    [BlendShareFeature(BlendShapeFeature.FeatureTypeId, SchemaVersion = 1, DefaultRequired = true)]
    public sealed class BlendShapeFeature : BlendShareFeatureData
    {
        public const string FeatureTypeId = "blendshare.blend-shapes";

        [BlendShareField(16)] public List<BlendShapeEntry> Shapes = new List<BlendShapeEntry>();

        public BlendShapeFeature()
        {
            TypeId = FeatureTypeId;
            SchemaVersion = 1;
            Required = true;
        }
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class BlendShapeEntry
    {
        [BlendShareField(1)] public string ShapeId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public List<BlendShapeFrame> Frames = new List<BlendShapeFrame>();
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class BlendShapeFrame
    {
        [BlendShareField(1)] public string FrameId = string.Empty;
        [BlendShareField(2)] public double Weight = 100d;
        [BlendShareField(3)] public LazyPayload<BlendShapeFramePayload> Payload = LazyPayload<BlendShapeFramePayload>.Empty;
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class BlendShapeFramePayload
    {
        [BlendShareField(1, Semantic = DataSemantic.PositionDelta)]
        public IndexedBuffer<Vector3d> DeltaPositions;

        [BlendShareField(2, Semantic = DataSemantic.NormalDelta)]
        public IndexedBuffer<Vector3d> DeltaNormals;
    }
}
