using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Base class for scene-backed ufbx deformers attached to a mesh.</summary>
    public abstract class UfbxDeformer : UfbxElement, IFbxDeformer
    {
        /// <summary>Initializes a scene-backed deformer.</summary>
        protected UfbxDeformer(UfbxScene scene, FbxElementType elementType, UfbxMesh ownerMesh, int index, long id, string name)
            : base(scene, elementType, index, id, name)
        {
            OwnerUfbxMesh = ownerMesh ?? throw new ArgumentNullException(nameof(ownerMesh));
        }

        internal UfbxMesh OwnerUfbxMesh { get; }

        /// <inheritdoc />
        public IFbxMeshGeometry OwnerMesh => OwnerUfbxMesh;
    }

    /// <summary>Scene-backed ufbx skin deformer.</summary>
    public sealed class UfbxSkinDeformer : UfbxDeformer, IFbxSkinDeformer
    {
        private readonly object lazyLock = new object();
        private IReadOnlyList<IFbxSkinCluster> clusters;

        internal UfbxSkinDeformer(UfbxScene scene, UfbxMesh ownerMesh, int skinIndex, long id, string name, int clusterCount)
            : base(scene, FbxElementType.SkinDeformer, ownerMesh, skinIndex, id, name)
        {
            ClusterCount = Math.Max(0, clusterCount);
        }

        /// <inheritdoc />
        public int ClusterCount { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxSkinCluster> Clusters
        {
            get
            {
                var existing = clusters;
                if (existing != null)
                {
                    return existing;
                }

                lock (lazyLock)
                {
                    if (clusters == null)
                    {
                        clusters = BuildClusters();
                    }

                    return clusters;
                }
            }
        }

        private IReadOnlyList<IFbxSkinCluster> BuildClusters()
        {
            var handle = RequireHandle();
            var result = new List<IFbxSkinCluster>(ClusterCount);
            for (int i = 0; i < ClusterCount; i++)
            {
                if (UfbxNative.GetSkinClusterInfo(handle, OwnerUfbxMesh.Index, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyClusterName(handle, OwnerUfbxMesh.Index, Index, i, ref dst, size));
                var boneNode = info.BoneNodeIndex >= 0 && info.BoneNodeIndex < Scene.Nodes.Count
                    ? Scene.Nodes[info.BoneNodeIndex]
                    : null;
                var linkMode = info.LinkMode switch
                {
                    0 => FbxClusterLinkMode.Normalize,
                    1 => FbxClusterLinkMode.TotalOne,
                    2 => FbxClusterLinkMode.Additive,
                    _ => FbxClusterLinkMode.Unknown
                };
                result.Add(new UfbxSkinCluster(Scene, OwnerUfbxMesh, this, i, (long)info.Id, name, boneNode, info, linkMode));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }
    }

    /// <summary>Scene-backed ufbx skin cluster containing bind matrices and sparse control point weights.</summary>
    public sealed class UfbxSkinCluster : UfbxElement, IFbxSkinCluster
    {
        private readonly UfbxMesh ownerMesh;
        private readonly UfbxSkinDeformer ownerSkin;

        internal UfbxSkinCluster(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            int clusterIndex,
            long id,
            string name,
            IFbxNode boneNode,
            UfbxNative.ClusterInfo info,
            FbxClusterLinkMode linkMode = FbxClusterLinkMode.Normalize)
            : base(scene, FbxElementType.SkinCluster, clusterIndex, id, name)
        {
            this.ownerMesh = ownerMesh ?? throw new ArgumentNullException(nameof(ownerMesh));
            this.ownerSkin = ownerSkin ?? throw new ArgumentNullException(nameof(ownerSkin));
            BoneNode = boneNode;
            BonePath = boneNode?.Path ?? string.Empty;
            LinkMode = linkMode;
            WeightCount = Math.Max(0, info.WeightCount);
            MeshBindWorld = info.MeshBindWorld.ToFbxMatrix();
            BindToWorld = info.BoneBindWorld.ToFbxMatrix();
            MeshNodeToBone = info.MeshNodeToBone.ToFbxMatrix();
            GeometryToBone = info.GeometryToBone.ToFbxMatrix();
        }

        /// <inheritdoc />
        public IFbxMeshGeometry OwnerMesh => ownerMesh;

        /// <inheritdoc />
        public IFbxSkinDeformer OwnerSkin => ownerSkin;

        /// <inheritdoc />
        public IFbxNode BoneNode { get; }

        /// <inheritdoc />
        public string BonePath { get; }

        /// <inheritdoc />
        public FbxClusterLinkMode LinkMode { get; }

        /// <inheritdoc />
        public int WeightCount { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 MeshBindWorld { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 BindToWorld { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 MeshNodeToBone { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 GeometryToBone { get; }

        /// <inheritdoc />
        public bool TryCopyControlPointIndices(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < WeightCount)
            {
                written = 0;
                return false;
            }

            if (WeightCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyClusterIndices(
                handle,
                ownerMesh.Index,
                ownerSkin.Index,
                Index,
                ref MemoryMarshal.GetReference(destination),
                destination.Length);
            return UfbxMesh.FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyWeights(Span<double> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < WeightCount)
            {
                written = 0;
                return false;
            }

            if (WeightCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyClusterWeights(
                handle,
                ownerMesh.Index,
                ownerSkin.Index,
                Index,
                ref MemoryMarshal.GetReference(destination),
                destination.Length);
            return UfbxMesh.FinishCopy(result, out written);
        }
    }

    /// <summary>Scene-backed ufbx blend deformer.</summary>
    public sealed class UfbxBlendDeformer : UfbxDeformer, IFbxBlendDeformer
    {
        private readonly object lazyLock = new object();
        private IReadOnlyList<IFbxBlendChannel> channels;

        internal UfbxBlendDeformer(UfbxScene scene, UfbxMesh ownerMesh, int deformerIndex, long id, string name, int channelCount)
            : base(scene, FbxElementType.BlendDeformer, ownerMesh, deformerIndex, id, name)
        {
            ChannelCount = Math.Max(0, channelCount);
        }

        /// <inheritdoc />
        public int ChannelCount { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxBlendChannel> Channels
        {
            get
            {
                var existing = channels;
                if (existing != null)
                {
                    return existing;
                }

                lock (lazyLock)
                {
                    if (channels == null)
                    {
                        channels = BuildChannels();
                    }

                    return channels;
                }
            }
        }

        private IReadOnlyList<IFbxBlendChannel> BuildChannels()
        {
            var handle = RequireHandle();
            var result = new List<IFbxBlendChannel>(ChannelCount);
            for (int i = 0; i < ChannelCount; i++)
            {
                if (UfbxNative.GetBlendChannelInfo(handle, OwnerUfbxMesh.Index, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyBlendChannelName(handle, OwnerUfbxMesh.Index, Index, i, ref dst, size));
                result.Add(new UfbxBlendChannel(Scene, OwnerUfbxMesh, this, i, (long)info.Id, name, info.KeyframeCount));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }
    }

    /// <summary>Scene-backed ufbx blend channel containing ordered target keyframes.</summary>
    public sealed class UfbxBlendChannel : UfbxElement, IFbxBlendChannel
    {
        private readonly object lazyLock = new object();
        private readonly UfbxMesh ownerMesh;
        private readonly UfbxBlendDeformer ownerDeformer;
        private IReadOnlyList<IFbxBlendKeyframe> keyframes;

        internal UfbxBlendChannel(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            int channelIndex,
            long id,
            string name,
            int keyframeCount)
            : base(scene, FbxElementType.BlendChannel, channelIndex, id, name)
        {
            this.ownerMesh = ownerMesh ?? throw new ArgumentNullException(nameof(ownerMesh));
            this.ownerDeformer = ownerDeformer ?? throw new ArgumentNullException(nameof(ownerDeformer));
            KeyframeCount = Math.Max(0, keyframeCount);
        }

        /// <inheritdoc />
        public IFbxMeshGeometry OwnerMesh => ownerMesh;

        /// <inheritdoc />
        public IFbxBlendDeformer OwnerDeformer => ownerDeformer;

        /// <inheritdoc />
        public int KeyframeCount { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxBlendKeyframe> Keyframes
        {
            get
            {
                var existing = keyframes;
                if (existing != null)
                {
                    return existing;
                }

                lock (lazyLock)
                {
                    if (keyframes == null)
                    {
                        keyframes = BuildKeyframes();
                    }

                    return keyframes;
                }
            }
        }

        private IReadOnlyList<IFbxBlendKeyframe> BuildKeyframes()
        {
            var handle = RequireHandle();
            var result = new List<IFbxBlendKeyframe>(KeyframeCount);
            for (int i = 0; i < KeyframeCount; i++)
            {
                if (UfbxNative.GetBlendKeyframeInfo(handle, ownerMesh.Index, ownerDeformer.Index, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string shapeName = UfbxScene.CopyString(
                    info.ShapeNameLength,
                    (ref byte dst, int size) => UfbxNative.CopyBlendShapeName(handle, ownerMesh.Index, ownerDeformer.Index, Index, i, ref dst, size));
                var shape = new UfbxBlendShape(
                    Scene,
                    ownerMesh,
                    ownerDeformer,
                    this,
                    i,
                    (long)info.ShapeId,
                    shapeName,
                    info.OffsetCount,
                    info.HasNormalOffsets != 0,
                    info.HasOffsetWeights != 0);
                result.Add(new UfbxBlendKeyframe(info.TargetWeight, info.EffectiveWeight, shape));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }
    }

    /// <summary>Scene-backed blend channel keyframe pairing weights with its target shape.</summary>
    public sealed class UfbxBlendKeyframe : IFbxBlendKeyframe
    {
        internal UfbxBlendKeyframe(double targetWeight, double effectiveWeight, IFbxBlendShape shape)
        {
            TargetWeight = targetWeight;
            EffectiveWeight = effectiveWeight;
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        }

        /// <inheritdoc />
        public double TargetWeight { get; }

        /// <inheritdoc />
        public double EffectiveWeight { get; }

        /// <inheritdoc />
        public IFbxBlendShape Shape { get; }
    }

    /// <summary>Scene-backed blend target shape exposing sparse control point offsets.</summary>
    public sealed class UfbxBlendShape : UfbxElement, IFbxBlendShape
    {
        private readonly UfbxMesh ownerMesh;
        private readonly UfbxBlendDeformer ownerDeformer;
        private readonly UfbxBlendChannel ownerChannel;

        internal UfbxBlendShape(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            int keyframeIndex,
            long id,
            string name,
            int offsetCount,
            bool hasNormalOffsets,
            bool hasOffsetWeights)
            : base(scene, FbxElementType.BlendShape, keyframeIndex, id, name)
        {
            this.ownerMesh = ownerMesh ?? throw new ArgumentNullException(nameof(ownerMesh));
            this.ownerDeformer = ownerDeformer ?? throw new ArgumentNullException(nameof(ownerDeformer));
            this.ownerChannel = ownerChannel ?? throw new ArgumentNullException(nameof(ownerChannel));
            OffsetCount = Math.Max(0, offsetCount);
            HasNormalOffsets = hasNormalOffsets;
            HasOffsetWeights = hasOffsetWeights;
        }

        /// <inheritdoc />
        public IFbxMeshGeometry OwnerMesh => ownerMesh;

        /// <inheritdoc />
        public IFbxBlendDeformer OwnerDeformer => ownerDeformer;

        /// <inheritdoc />
        public IFbxBlendChannel OwnerChannel => ownerChannel;

        /// <inheritdoc />
        public int OffsetCount { get; }

        /// <inheritdoc />
        public bool HasNormalOffsets { get; }

        /// <inheritdoc />
        public bool HasOffsetWeights { get; }

        /// <inheritdoc />
        public bool TryCopyOffsets(
            Span<int> destinationControlPoints,
            Span<Vector3d> destinationPositionOffsets,
            Span<Vector3d> destinationNormalOffsets,
            Span<double> destinationOffsetWeights,
            out int written)
        {
            var handle = RequireHandle();
            uint outputMask = 0u;
            int minCapacity = int.MaxValue;

            if (!destinationControlPoints.IsEmpty)
            {
                outputMask |= UfbxNative.BlendOutIndices;
                minCapacity = Math.Min(minCapacity, destinationControlPoints.Length);
            }

            if (!destinationPositionOffsets.IsEmpty)
            {
                outputMask |= UfbxNative.BlendOutPositions;
                minCapacity = Math.Min(minCapacity, destinationPositionOffsets.Length);
            }

            if (!destinationNormalOffsets.IsEmpty)
            {
                outputMask |= UfbxNative.BlendOutNormals;
                minCapacity = Math.Min(minCapacity, destinationNormalOffsets.Length);
            }

            if (!destinationOffsetWeights.IsEmpty)
            {
                outputMask |= UfbxNative.BlendOutWeights;
                minCapacity = Math.Min(minCapacity, destinationOffsetWeights.Length);
            }

            if (outputMask == 0u)
            {
                written = 0;
                return OffsetCount == 0;
            }

            if (minCapacity < OffsetCount)
            {
                written = 0;
                return false;
            }

            if (OffsetCount == 0)
            {
                written = 0;
                return true;
            }

            // Use concrete IntPtr.Zero overloads for the two most common omitted-buffer paths,
            // and never pass MemoryMarshal.GetReference of an empty span as a native buffer.
            int result;
            if (outputMask == (UfbxNative.BlendOutIndices | UfbxNative.BlendOutPositions))
            {
                result = UfbxNative.CopyBlendShapeOffsetsIndicesAndPositions(
                    handle,
                    ownerMesh.Index,
                    ownerDeformer.Index,
                    ownerChannel.Index,
                    Index,
                    outputMask,
                    ref MemoryMarshal.GetReference(destinationControlPoints),
                    ref MemoryMarshal.GetReference(destinationPositionOffsets),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    minCapacity);
            }
            else if (outputMask == (UfbxNative.BlendOutIndices | UfbxNative.BlendOutPositions | UfbxNative.BlendOutNormals))
            {
                result = UfbxNative.CopyBlendShapeOffsetsIndicesPositionsNormals(
                    handle,
                    ownerMesh.Index,
                    ownerDeformer.Index,
                    ownerChannel.Index,
                    Index,
                    outputMask,
                    ref MemoryMarshal.GetReference(destinationControlPoints),
                    ref MemoryMarshal.GetReference(destinationPositionOffsets),
                    ref MemoryMarshal.GetReference(destinationNormalOffsets),
                    IntPtr.Zero,
                    minCapacity);
            }
            else
            {
                int dummyIndex = 0;
                Vector3d dummyPosition = default;
                Vector3d dummyNormal = default;
                double dummyWeight = 0d;

                ref int indicesRef = ref (outputMask & UfbxNative.BlendOutIndices) != 0u
                    ? ref MemoryMarshal.GetReference(destinationControlPoints)
                    : ref dummyIndex;
                ref Vector3d positionsRef = ref (outputMask & UfbxNative.BlendOutPositions) != 0u
                    ? ref MemoryMarshal.GetReference(destinationPositionOffsets)
                    : ref dummyPosition;
                ref Vector3d normalsRef = ref (outputMask & UfbxNative.BlendOutNormals) != 0u
                    ? ref MemoryMarshal.GetReference(destinationNormalOffsets)
                    : ref dummyNormal;
                ref double weightsRef = ref (outputMask & UfbxNative.BlendOutWeights) != 0u
                    ? ref MemoryMarshal.GetReference(destinationOffsetWeights)
                    : ref dummyWeight;

                result = UfbxNative.CopyBlendShapeOffsets(
                    handle,
                    ownerMesh.Index,
                    ownerDeformer.Index,
                    ownerChannel.Index,
                    Index,
                    outputMask,
                    ref indicesRef,
                    ref positionsRef,
                    ref normalsRef,
                    ref weightsRef,
                    minCapacity);
            }

            return UfbxMesh.FinishCopy(result, out written);
        }
    }
}
