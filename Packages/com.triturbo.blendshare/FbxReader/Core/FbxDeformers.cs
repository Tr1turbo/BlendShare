using System.Collections.Generic;
using System.Linq;

namespace Triturbo.FBX
{
    public enum FbxDeformerType
    {
        Unknown,
        Skin,
        BlendShape,
        VertexCache
    }

    public enum FbxClusterLinkMode
    {
        Unknown,
        Normalize,
        Additive,
        TotalOne
    }

    public enum FbxShapeValueMode
    {
        Relative,
        Absolute
    }

    public abstract class FbxDeformer
    {
        public long Id { get; }
        public string Name { get; }
        public FbxMeshGeometry OwnerMesh { get; internal set; }
        public FbxDeformerType Type { get; }

        protected FbxDeformer(long id, string name, FbxDeformerType type)
        {
            Id = id;
            Name = name ?? string.Empty;
            Type = type;
        }
    }

    public sealed class FbxUnknownDeformer : FbxDeformer
    {
        public string RawType { get; }

        internal FbxUnknownDeformer(long id, string name, string rawType)
            : base(id, name, FbxDeformerType.Unknown)
        {
            RawType = rawType ?? string.Empty;
        }
    }

    public sealed class FbxVertexCacheDeformer : FbxDeformer
    {
        internal FbxVertexCacheDeformer(long id, string name)
            : base(id, name, FbxDeformerType.VertexCache)
        {
        }
    }

    public sealed class FbxSkinDeformer : FbxDeformer
    {
        public IReadOnlyList<FbxBoneBinding> Bones { get; }
        public IReadOnlyList<string> BoneNames { get; }
        public IReadOnlyList<FbxCluster> Clusters { get; }
        public IReadOnlyList<IReadOnlyList<FbxControlPointBoneWeight>> ControlPointWeights { get; }
        public bool HasWeights => ControlPointWeights.Any(weights => weights.Count > 0);

        internal FbxSkinDeformer(
            long id,
            string name,
            IEnumerable<FbxBoneBinding> bones,
            IEnumerable<FbxCluster> clusters,
            IEnumerable<IReadOnlyList<FbxControlPointBoneWeight>> controlPointWeights)
            : base(id, name, FbxDeformerType.Skin)
        {
            Bones = FbxCollection.ToReadOnly(bones);
            BoneNames = FbxCollection.ToReadOnly(Bones.Select(bone => bone.Name));
            Clusters = FbxCollection.ToReadOnly(clusters);
            ControlPointWeights = FbxCollection.ToReadOnly(controlPointWeights);
        }

        public IReadOnlyList<FbxControlPointBoneWeight> GetWeights(int controlPointIndex)
        {
            return controlPointIndex >= 0 && controlPointIndex < ControlPointWeights.Count
                ? ControlPointWeights[controlPointIndex]
                : System.Array.AsReadOnly(System.Array.Empty<FbxControlPointBoneWeight>());
        }
    }

    public sealed class FbxBoneBinding
    {
        public int Index { get; }
        public long NodeId { get; }
        public string Name { get; }
        public string Path { get; }
        public FbxSceneNode Node { get; }

        internal FbxBoneBinding(int index, long nodeId, string name, string path, FbxSceneNode node)
        {
            Index = index;
            NodeId = nodeId;
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Node = node;
        }
    }

    public sealed class FbxCluster
    {
        public long Id { get; }
        public string Name { get; }
        public int BoneIndex { get; }
        public long BoneNodeId { get; }
        public string BoneName { get; }
        public string BonePath { get; }
        public FbxSceneNode BoneNode { get; }
        public FbxClusterLinkMode LinkMode { get; }
        public FbxMatrix4x4 TransformMatrix { get; }
        public FbxMatrix4x4 TransformLinkMatrix { get; }
        public FbxMatrix4x4 TransformAssociateModelMatrix { get; }
        public FbxMatrix4x4 BindPose { get; }
        public bool HasTransformMatrix { get; }
        public bool HasTransformLinkMatrix { get; }
        public bool HasTransformAssociateModelMatrix { get; }
        public bool HasBindPose { get; }
        public IReadOnlyList<int> ControlPointIndices { get; }
        public IReadOnlyList<double> Weights { get; }

        internal FbxCluster(
            long id,
            string name,
            int boneIndex,
            FbxBoneBinding bone,
            FbxClusterLinkMode linkMode,
            FbxMatrix4x4 transformMatrix,
            bool hasTransformMatrix,
            FbxMatrix4x4 transformLinkMatrix,
            bool hasTransformLinkMatrix,
            FbxMatrix4x4 transformAssociateModelMatrix,
            bool hasTransformAssociateModelMatrix,
            IEnumerable<int> controlPointIndices,
            IEnumerable<double> weights)
        {
            Id = id;
            Name = name ?? string.Empty;
            BoneIndex = boneIndex;
            BoneNodeId = bone?.NodeId ?? 0;
            BoneName = bone?.Name ?? string.Empty;
            BonePath = bone?.Path ?? string.Empty;
            BoneNode = bone?.Node;
            LinkMode = linkMode;
            TransformMatrix = transformMatrix;
            TransformLinkMatrix = transformLinkMatrix;
            TransformAssociateModelMatrix = transformAssociateModelMatrix;
            HasTransformMatrix = hasTransformMatrix;
            HasTransformLinkMatrix = hasTransformLinkMatrix;
            HasTransformAssociateModelMatrix = hasTransformAssociateModelMatrix;
            FbxMatrix4x4 inverse = FbxMatrix4x4.Identity;
            HasBindPose = hasTransformMatrix && hasTransformLinkMatrix && transformLinkMatrix.TryInverse(out inverse);
            BindPose = HasBindPose ? inverse * transformMatrix : FbxMatrix4x4.Identity;
            ControlPointIndices = FbxCollection.ToReadOnly(controlPointIndices);
            Weights = FbxCollection.ToReadOnly(weights);
        }
    }

    public readonly struct FbxControlPointBoneWeight
    {
        public int BoneIndex { get; }
        public float Weight { get; }

        public FbxControlPointBoneWeight(int boneIndex, float weight)
        {
            BoneIndex = boneIndex;
            Weight = weight;
        }
    }

    public sealed class FbxBlendShapeDeformer : FbxDeformer
    {
        public IReadOnlyList<FbxBlendShapeChannel> Channels { get; }

        internal FbxBlendShapeDeformer(long id, string name, IEnumerable<FbxBlendShapeChannel> channels)
            : base(id, name, FbxDeformerType.BlendShape)
        {
            Channels = FbxCollection.ToReadOnly(channels);
        }
    }

    public sealed class FbxBlendShapeChannel
    {
        public long Id { get; }
        public string Name { get; }
        public IReadOnlyList<FbxShapeFrame> Frames { get; }

        internal FbxBlendShapeChannel(long id, string name, IEnumerable<FbxShapeFrame> frames)
        {
            Id = id;
            Name = name ?? string.Empty;
            Frames = FbxCollection.ToReadOnly(frames);
        }
    }

    public sealed class FbxShapeFrame
    {
        public double FrameWeight { get; }
        public FbxShapeValueMode SourceValueMode { get; }
        public IReadOnlyList<int> ControlPointIndices { get; }
        public IReadOnlyList<Vector3d> ControlPointDeltas { get; }
        public IReadOnlyList<Vector3d> ControlPointNormalDeltas { get; }
        public IReadOnlyList<Vector3d> ControlPointTangentDeltas { get; }

        internal FbxShapeFrame(
            double frameWeight,
            IEnumerable<int> controlPointIndices,
            IEnumerable<Vector3d> controlPointDeltas,
            IEnumerable<Vector3d> controlPointNormalDeltas = null,
            IEnumerable<Vector3d> controlPointTangentDeltas = null,
            FbxShapeValueMode sourceValueMode = FbxShapeValueMode.Relative)
        {
            FrameWeight = frameWeight;
            SourceValueMode = sourceValueMode;
            ControlPointIndices = FbxCollection.ToReadOnly(controlPointIndices);
            ControlPointDeltas = FbxCollection.ToReadOnly(controlPointDeltas);
            ControlPointNormalDeltas = FbxCollection.ToReadOnly(controlPointNormalDeltas);
            ControlPointTangentDeltas = FbxCollection.ToReadOnly(controlPointTangentDeltas);
        }
    }
}
