using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public abstract class UfbxDeformer : UfbxElement
    {
        protected UfbxDeformer(UfbxScene scene, UfbxElementType elementType, UfbxMesh ownerMesh, int index, long id, string name)
            : base(scene, elementType, index, id, name)
        {
            OwnerMesh = ownerMesh;
        }

        public UfbxMesh OwnerMesh { get; }
    }

    public sealed class UfbxSkinDeformer : UfbxDeformer
    {
        private IReadOnlyList<UfbxSkinCluster> snapshotClusters;
        private IReadOnlyList<UfbxSkinCluster> clusters;

        internal UfbxSkinDeformer(UfbxScene scene, UfbxMesh ownerMesh, int skinIndex, long id, string name, int clusterCount)
            : base(scene, UfbxElementType.SkinDeformer, ownerMesh, skinIndex, id, name)
        {
            ClusterCount = Math.Max(0, clusterCount);
        }

        internal UfbxSkinDeformer(
            UfbxSkinDeformer source,
            UfbxMesh ownerMesh)
            : base(source.Scene, UfbxElementType.SkinDeformer, ownerMesh, source.Index, source.Id, source.Name)
        {
            ClusterCount = source.ClusterCount;
        }

        public int ClusterCount { get; }
        public IReadOnlyList<UfbxSkinCluster> Clusters => snapshotClusters ?? (clusters ??= BuildClusters());

        internal void SetSnapshotClusters(IEnumerable<UfbxSkinCluster> clusters)
        {
            snapshotClusters = FbxCollection.ToReadOnly(clusters);
        }

        private IReadOnlyList<UfbxSkinCluster> BuildClusters()
        {
            EnsureAlive();
            var result = new List<UfbxSkinCluster>(ClusterCount);
            for (int i = 0; i < ClusterCount; i++)
            {
                if (UfbxNative.GetSkinClusterInfo(Scene.Handle, OwnerMesh.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, buffer => UfbxNative.CopyClusterName(Scene.Handle, OwnerMesh.Index, Index, i, buffer, buffer.Length));
                var boneNode = info.BoneNodeIndex >= 0 && info.BoneNodeIndex < Scene.Nodes.Count ? Scene.Nodes[info.BoneNodeIndex] : null;
                result.Add(new UfbxSkinCluster(Scene, OwnerMesh, this, i, (long)info.Id, name, boneNode, info));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxSkinCluster : UfbxElement
    {
        private readonly int[] snapshotIndices;
        private readonly double[] snapshotWeights;

        internal UfbxSkinCluster(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            int clusterIndex,
            long id,
            string name,
            UfbxNode boneNode,
            UfbxNative.ClusterInfo info)
            : base(scene, UfbxElementType.SkinCluster, clusterIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerSkin = ownerSkin;
            BoneNode = boneNode;
            WeightCount = Math.Max(0, info.WeightCount);
            MeshBindWorld = UfbxScene.ToMatrix(info.MeshBindWorld);
            BindToWorld = UfbxScene.ToMatrix(info.BoneBindWorld);
            MeshNodeToBone = UfbxScene.ToMatrix(info.MeshNodeToBone);
            GeometryToBone = UfbxScene.ToMatrix(info.GeometryToBone);
        }

        internal UfbxSkinCluster(
            UfbxSkinCluster source,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            int[] indices,
            double[] weights)
            : base(source.Scene, UfbxElementType.SkinCluster, source.Index, source.Id, source.Name)
        {
            OwnerMesh = ownerMesh;
            OwnerSkin = ownerSkin;
            BoneNode = source.BoneNode;
            snapshotIndices = indices?.ToArray() ?? Array.Empty<int>();
            snapshotWeights = weights?.ToArray() ?? Array.Empty<double>();
            WeightCount = Math.Min(snapshotIndices.Length, snapshotWeights.Length);
            MeshBindWorld = source.MeshBindWorld;
            BindToWorld = source.BindToWorld;
            MeshNodeToBone = source.MeshNodeToBone;
            GeometryToBone = source.GeometryToBone;
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxSkinDeformer OwnerSkin { get; }
        public UfbxNode BoneNode { get; }
        public int WeightCount { get; }
        public FbxMatrix4x4 MeshBindWorld { get; }
        public FbxMatrix4x4 BindToWorld { get; }
        public FbxMatrix4x4 MeshNodeToBone { get; }
        public FbxMatrix4x4 GeometryToBone { get; }

        public int CopyIndices(int[] destination)
        {
            if (snapshotIndices != null)
            {
                return CopyIntArray(snapshotIndices, destination, WeightCount);
            }

            EnsureAlive();
            return UfbxNative.CopyClusterIndices(Scene.Handle, OwnerMesh.Index, OwnerSkin.Index, Index, destination, destination?.Length ?? 0);
        }

        public int CopyVertices(int[] destination)
        {
            return CopyIndices(destination);
        }

        public int CopyWeights(double[] destination)
        {
            if (snapshotWeights != null)
            {
                return CopyDoubleArray(snapshotWeights, destination, WeightCount);
            }

            EnsureAlive();
            return UfbxNative.CopyClusterWeights(Scene.Handle, OwnerMesh.Index, OwnerSkin.Index, Index, destination, destination?.Length ?? 0);
        }

        public int[] GetIndices()
        {
            var values = new int[WeightCount];
            return CopyIndices(values) != 0 ? values : Array.Empty<int>();
        }

        public int[] GetVertices()
        {
            return GetIndices();
        }

        public double[] GetWeights()
        {
            var values = new double[WeightCount];
            return CopyWeights(values) != 0 ? values : Array.Empty<double>();
        }

        private static int CopyIntArray(IReadOnlyList<int> source, int[] destination, int count)
        {
            if (source == null || destination == null || destination.Length < count || source.Count < count)
            {
                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                destination[i] = source[i];
            }

            return 1;
        }

        private static int CopyDoubleArray(IReadOnlyList<double> source, double[] destination, int count)
        {
            if (source == null || destination == null || destination.Length < count || source.Count < count)
            {
                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                destination[i] = source[i];
            }

            return 1;
        }
    }

    public sealed class UfbxBlendDeformer : UfbxDeformer
    {
        private IReadOnlyList<UfbxBlendChannel> snapshotChannels;
        private IReadOnlyList<UfbxBlendChannel> channels;

        internal UfbxBlendDeformer(UfbxScene scene, UfbxMesh ownerMesh, int deformerIndex, long id, string name, int channelCount)
            : base(scene, UfbxElementType.BlendDeformer, ownerMesh, deformerIndex, id, name)
        {
            ChannelCount = Math.Max(0, channelCount);
        }

        internal UfbxBlendDeformer(
            UfbxBlendDeformer source,
            UfbxMesh ownerMesh)
            : base(source.Scene, UfbxElementType.BlendDeformer, ownerMesh, source.Index, source.Id, source.Name)
        {
            ChannelCount = source.ChannelCount;
        }

        public int ChannelCount { get; }
        public IReadOnlyList<UfbxBlendChannel> Channels => snapshotChannels ?? (channels ??= BuildChannels());

        internal void SetSnapshotChannels(IEnumerable<UfbxBlendChannel> channels)
        {
            snapshotChannels = FbxCollection.ToReadOnly(channels);
        }

        private IReadOnlyList<UfbxBlendChannel> BuildChannels()
        {
            EnsureAlive();
            var result = new List<UfbxBlendChannel>(ChannelCount);
            for (int i = 0; i < ChannelCount; i++)
            {
                if (UfbxNative.GetBlendChannelInfo(Scene.Handle, OwnerMesh.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, buffer => UfbxNative.CopyBlendChannelName(Scene.Handle, OwnerMesh.Index, Index, i, buffer, buffer.Length));
                result.Add(new UfbxBlendChannel(Scene, OwnerMesh, this, i, (long)info.Id, name, info.FrameCount));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxBlendChannel : UfbxElement
    {
        private IReadOnlyList<UfbxBlendShape> snapshotBlendShapes;
        private IReadOnlyList<UfbxBlendShape> blendShapes;

        internal UfbxBlendChannel(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            int channelIndex,
            long id,
            string name,
            int blendShapeCount)
            : base(scene, UfbxElementType.BlendChannel, channelIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            BlendShapeCount = Math.Max(0, blendShapeCount);
        }

        internal UfbxBlendChannel(
            UfbxBlendChannel source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer)
            : base(source.Scene, UfbxElementType.BlendChannel, source.Index, source.Id, source.Name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            BlendShapeCount = source.BlendShapeCount;
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxBlendDeformer OwnerDeformer { get; }
        public int BlendShapeCount { get; }
        public IReadOnlyList<UfbxBlendShape> BlendShapes => snapshotBlendShapes ?? (blendShapes ??= BuildBlendShapes());

        internal void SetSnapshotBlendShapes(IEnumerable<UfbxBlendShape> blendShapes)
        {
            snapshotBlendShapes = FbxCollection.ToReadOnly(blendShapes);
        }

        private IReadOnlyList<UfbxBlendShape> BuildBlendShapes()
        {
            EnsureAlive();
            var result = new List<UfbxBlendShape>(BlendShapeCount);
            for (int i = 0; i < BlendShapeCount; i++)
            {
                if (UfbxNative.GetBlendFrameInfo(Scene.Handle, OwnerMesh.Index, OwnerDeformer.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, buffer => UfbxNative.CopyBlendFrameName(Scene.Handle, OwnerMesh.Index, OwnerDeformer.Index, Index, i, buffer, buffer.Length));
                result.Add(new UfbxBlendShape(Scene, OwnerMesh, OwnerDeformer, this, i, (long)info.Id, name, info.Weight, info.OffsetCount));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxBlendShape : UfbxElement
    {
        private readonly int[] snapshotIndices;
        private readonly Vector3d[] snapshotPositions;
        private readonly Vector3d[] snapshotNormals;

        internal UfbxBlendShape(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            int blendShapeIndex,
            long id,
            string name,
            double weight,
            int offsetCount)
            : base(scene, UfbxElementType.BlendShape, blendShapeIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            OwnerChannel = ownerChannel;
            Weight = weight;
            OffsetCount = Math.Max(0, offsetCount);
        }

        internal UfbxBlendShape(
            UfbxBlendShape source,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            int[] indices,
            Vector3d[] positions,
            Vector3d[] normals)
            : base(source.Scene, UfbxElementType.BlendShape, source.Index, source.Id, source.Name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            OwnerChannel = ownerChannel;
            Weight = source.Weight;
            snapshotIndices = indices?.ToArray() ?? Array.Empty<int>();
            snapshotPositions = UfbxMesh.Copy(positions);
            snapshotNormals = UfbxMesh.Copy(normals);
            OffsetCount = Math.Min(snapshotIndices.Length, snapshotPositions.Length);
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxBlendDeformer OwnerDeformer { get; }
        public UfbxBlendChannel OwnerChannel { get; }
        public double Weight { get; }
        public int OffsetCount { get; }

        public int CopyOffsets(int[] destinationIndices, double[] destinationPositions, double[] destinationNormals)
        {
            if (snapshotIndices != null)
            {
                return CopySnapshotOffsets(destinationIndices, destinationPositions, destinationNormals);
            }

            EnsureAlive();
            int destinationCount = Math.Min(
                destinationIndices?.Length ?? 0,
                destinationPositions?.Length / 3 ?? 0);
            if (destinationNormals != null)
            {
                destinationCount = Math.Min(destinationCount, destinationNormals.Length / 3);
            }

            return UfbxNative.CopyBlendFrameOffsets(
                Scene.Handle,
                OwnerMesh.Index,
                OwnerDeformer.Index,
                OwnerChannel.Index,
                Index,
                destinationIndices,
                destinationPositions,
                destinationNormals,
                destinationCount);
        }

        private int CopySnapshotOffsets(int[] destinationIndices, double[] destinationPositions, double[] destinationNormals)
        {
            int destinationCount = Math.Min(destinationIndices?.Length ?? 0, destinationPositions?.Length / 3 ?? 0);
            if (destinationNormals != null)
            {
                destinationCount = Math.Min(destinationCount, destinationNormals.Length / 3);
            }

            if (destinationIndices == null || destinationPositions == null || destinationCount < OffsetCount)
            {
                return 0;
            }

            for (int i = 0; i < OffsetCount; i++)
            {
                destinationIndices[i] = snapshotIndices[i];
                destinationPositions[i * 3] = snapshotPositions[i].x;
                destinationPositions[i * 3 + 1] = snapshotPositions[i].y;
                destinationPositions[i * 3 + 2] = snapshotPositions[i].z;
                if (destinationNormals != null)
                {
                    var normal = i < snapshotNormals.Length ? snapshotNormals[i] : Vector3d.zero;
                    destinationNormals[i * 3] = normal.x;
                    destinationNormals[i * 3 + 1] = normal.y;
                    destinationNormals[i * 3 + 2] = normal.z;
                }
            }

            return 1;
        }
    }

}
