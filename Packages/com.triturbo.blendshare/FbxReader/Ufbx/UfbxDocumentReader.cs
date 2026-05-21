using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Triturbo.Fbx
{
    public static class UfbxDocumentReader
    {
        public static FbxReadResult<FbxDocument> Read(string assetPath, IEnumerable<string> nodePaths = null)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.InvalidArgument, "FBX asset path is empty.");
            }

            if (!File.Exists(assetPath))
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.FileNotFound, $"FBX file '{assetPath}' was not found.");
            }

            IntPtr scene = IntPtr.Zero;
            try
            {
                var error = new StringBuilder(2048);
                if (UfbxNative.Load(assetPath, out scene, error, error.Capacity) == 0 || scene == IntPtr.Zero)
                {
                    return FbxReadResult<FbxDocument>.Failed(
                        FbxReadStatus.ParseError,
                        string.IsNullOrWhiteSpace(error.ToString()) ? "ufbx failed to load the FBX file." : error.ToString());
                }

                return BuildDocument(scene, assetPath, nodePaths);
            }
            catch (DllNotFoundException exception)
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.SectionUnavailable, exception.Message);
            }
            catch (EntryPointNotFoundException exception)
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.SectionUnavailable, exception.Message);
            }
            catch (Exception exception) when (exception is SEHException || exception is InvalidOperationException || exception is ArgumentException)
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.ParseError, $"ufbx reader failed: {exception.Message}");
            }
            finally
            {
                if (scene != IntPtr.Zero)
                {
                    UfbxNative.Free(scene);
                }
            }
        }

        private static FbxReadResult<FbxDocument> BuildDocument(IntPtr scene, string assetPath, IEnumerable<string> nodePaths)
        {
            var requestedPaths = NormalizeRequestedNodePaths(nodePaths);
            var nodes = BuildNodes(scene, out var nodesByIndex);
            var nodesByPath = nodes.Where(node => node.Path != string.Empty).ToDictionary(node => node.Path, StringComparer.Ordinal);
            if (requestedPaths.Length > 0)
            {
                var missingPaths = requestedPaths.Where(path => !nodesByPath.ContainsKey(path)).ToArray();
                if (missingPaths.Length > 0)
                {
                    return FbxReadResult<FbxDocument>.Failed(
                        FbxReadStatus.NodeNotFound,
                        $"FBX node path was not found: {string.Join(", ", missingPaths)}.");
                }
            }

            var includedPaths = requestedPaths.Length > 0
                ? new HashSet<string>(requestedPaths, StringComparer.Ordinal)
                : null;
            BuildMeshes(scene, nodesByIndex, includedPaths);

            var rootNode = nodes.Count > 0 ? nodes[0] : new FbxSceneNode(0, string.Empty, string.Empty, FbxSceneNodeType.Root, FbxTransform.Identity);
            if (includedPaths != null)
            {
                nodes = BuildPartialNodeList(rootNode, nodes, includedPaths);
            }

            return FbxReadResult<FbxDocument>.Succeeded(new FbxDocument(rootNode, nodes, assetPath));
        }

        private static List<FbxSceneNode> BuildNodes(IntPtr scene, out Dictionary<int, FbxSceneNode> nodesByIndex)
        {
            int count = Math.Max(0, UfbxNative.GetNodeCount(scene));
            var nodes = new List<FbxSceneNode>(count);
            nodesByIndex = new Dictionary<int, FbxSceneNode>();
            var parentIndices = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetNodeInfo(scene, i, out var info) == 0)
                {
                    parentIndices[i] = -1;
                    continue;
                }

                parentIndices[i] = info.ParentIndex;
                string name = CopyString(info.NameLength, builder => UfbxNative.CopyNodeName(scene, i, builder, builder.Capacity));
                string path = CopyString(info.PathLength, builder => UfbxNative.CopyNodePath(scene, i, builder, builder.Capacity));
                var node = new FbxSceneNode(
                    (long)info.Id,
                    name,
                    path,
                    ToNodeType(info.Type),
                    new FbxTransform(
                        new Vector3d(info.LocalTranslationX, info.LocalTranslationY, info.LocalTranslationZ),
                        new Vector3d(info.LocalRotationX, info.LocalRotationY, info.LocalRotationZ),
                        new Vector3d(info.LocalScaleX, info.LocalScaleY, info.LocalScaleZ)));
                nodes.Add(node);
                nodesByIndex[i] = node;
            }

            var children = nodes.ToDictionary(node => node, _ => new List<FbxSceneNode>());
            for (int i = 0; i < count; i++)
            {
                if (!nodesByIndex.TryGetValue(i, out var node))
                {
                    continue;
                }

                if (parentIndices[i] >= 0 && nodesByIndex.TryGetValue(parentIndices[i], out var parent))
                {
                    node.SetParent(parent);
                    children[parent].Add(node);
                }

                if (node.NodeType == FbxSceneNodeType.Skeleton || node.NodeType == FbxSceneNodeType.LimbNode)
                {
                    node.SetAttribute(new FbxSkeleton(node.Id, node.Name, node, node.NodeType == FbxSceneNodeType.LimbNode ? "LimbNode" : "Skeleton"));
                }
            }

            foreach (var pair in children)
            {
                pair.Key.SetChildren(pair.Value);
            }

            return nodes;
        }

        private static List<FbxSceneNode> BuildPartialNodeList(
            FbxSceneNode rootNode,
            IEnumerable<FbxSceneNode> allNodes,
            ISet<string> selectedMeshPaths)
        {
            var included = new HashSet<FbxSceneNode>();
            foreach (var node in allNodes)
            {
                if (node.Path == string.Empty || !selectedMeshPaths.Contains(node.Path))
                {
                    continue;
                }

                included.Add(node);
                foreach (var skin in node.Mesh?.SkinDeformers ?? Array.Empty<FbxSkinDeformer>())
                {
                    foreach (var bone in skin.Bones)
                    {
                        if (bone.Node != null)
                        {
                            included.Add(bone.Node);
                        }
                    }
                }
            }

            var orderedNodes = allNodes
                .Where(node => node.Path == string.Empty || included.Contains(node))
                .ToList();
            var children = orderedNodes.ToDictionary(node => node, _ => new List<FbxSceneNode>());
            foreach (var node in orderedNodes)
            {
                if (node == rootNode)
                {
                    continue;
                }

                var parent = node.Parent != null && included.Contains(node.Parent)
                    ? node.Parent
                    : rootNode;
                node.SetParent(parent);
                children[parent].Add(node);
            }

            foreach (var pair in children)
            {
                pair.Key.SetChildren(pair.Value);
            }

            return orderedNodes;
        }

        private static void BuildMeshes(IntPtr scene, IReadOnlyDictionary<int, FbxSceneNode> nodesByIndex, ISet<string> includedPaths)
        {
            int meshCount = Math.Max(0, UfbxNative.GetMeshCount(scene));
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
            {
                if (UfbxNative.GetMeshInfo(scene, meshIndex, out var meshInfo) == 0)
                {
                    continue;
                }

                nodesByIndex.TryGetValue(meshInfo.NodeIndex, out var ownerNode);
                if (ownerNode == null)
                {
                    continue;
                }

                if (includedPaths != null && !includedPaths.Contains(ownerNode.Path))
                {
                    continue;
                }

                string meshName = CopyString(meshInfo.NameLength, builder => UfbxNative.CopyMeshName(scene, meshIndex, builder, builder.Capacity));
                var controlPoints = CopyVector3dArray(meshInfo.ControlPointCount, buffer => UfbxNative.CopyControlPoints(scene, meshIndex, buffer, meshInfo.ControlPointCount));
                var normals = CopyVector3dArray(meshInfo.ControlPointCount, buffer => UfbxNative.CopyControlPointNormals(scene, meshIndex, buffer, meshInfo.ControlPointCount));
                var tangents = CopyVector3dArray(meshInfo.ControlPointCount, buffer => UfbxNative.CopyControlPointTangents(scene, meshIndex, buffer, meshInfo.ControlPointCount));
                var deformers = BuildDeformers(scene, meshIndex, meshInfo, nodesByIndex, meshInfo.ControlPointCount);
                var mesh = new FbxMeshGeometry(
                    (long)meshInfo.Id,
                    meshName,
                    ownerNode,
                    meshInfo.ControlPointCount,
                    controlPoints,
                    normals,
                    tangents,
                    deformers);
                foreach (var deformer in deformers)
                {
                    deformer.OwnerMesh = mesh;
                }

                ownerNode.SetAttribute(mesh);
            }
        }

        private static List<FbxDeformer> BuildDeformers(
            IntPtr scene,
            int meshIndex,
            UfbxNative.MeshInfo meshInfo,
            IReadOnlyDictionary<int, FbxSceneNode> nodesByIndex,
            int controlPointCount)
        {
            var deformers = new List<FbxDeformer>();
            for (int skinIndex = 0; skinIndex < meshInfo.SkinCount; skinIndex++)
            {
                if (UfbxNative.GetSkinInfo(scene, meshIndex, skinIndex, out var skinInfo) == 0)
                {
                    continue;
                }

                deformers.Add(BuildSkin(scene, meshIndex, skinIndex, skinInfo, nodesByIndex, controlPointCount));
            }

            for (int deformerIndex = 0; deformerIndex < meshInfo.BlendDeformerCount; deformerIndex++)
            {
                if (UfbxNative.GetBlendDeformerInfo(scene, meshIndex, deformerIndex, out var blendInfo) == 0)
                {
                    continue;
                }

                deformers.Add(BuildBlendDeformer(scene, meshIndex, deformerIndex, blendInfo));
            }

            return deformers;
        }

        private static FbxSkinDeformer BuildSkin(
            IntPtr scene,
            int meshIndex,
            int skinIndex,
            UfbxNative.SkinInfo skinInfo,
            IReadOnlyDictionary<int, FbxSceneNode> nodesByIndex,
            int controlPointCount)
        {
            string skinName = CopyString(skinInfo.NameLength, builder => UfbxNative.CopySkinName(scene, meshIndex, skinIndex, builder, builder.Capacity));
            var bones = new List<FbxBoneBinding>();
            var clusters = new List<FbxCluster>();
            var weightsByControlPoint = new List<FbxControlPointBoneWeight>[Math.Max(0, controlPointCount)];

            for (int clusterIndex = 0; clusterIndex < skinInfo.ClusterCount; clusterIndex++)
            {
                if (UfbxNative.GetSkinClusterInfo(scene, meshIndex, skinIndex, clusterIndex, out var clusterInfo) == 0)
                {
                    continue;
                }

                nodesByIndex.TryGetValue(clusterInfo.BoneNodeIndex, out var boneNode);
                boneNode?.MarkBone();
                int boneIndex = bones.Count;
                var bone = new FbxBoneBinding(boneIndex, boneNode?.Id ?? 0, boneNode?.Name ?? string.Empty, boneNode?.Path ?? string.Empty, boneNode);
                bones.Add(bone);
                string clusterName = CopyString(clusterInfo.NameLength, builder => UfbxNative.CopyClusterName(scene, meshIndex, skinIndex, clusterIndex, builder, builder.Capacity));
                int[] indices = new int[Math.Max(0, clusterInfo.WeightCount)];
                double[] weights = new double[indices.Length];
                if (indices.Length > 0)
                {
                    UfbxNative.CopyClusterIndices(scene, meshIndex, skinIndex, clusterIndex, indices, indices.Length);
                    UfbxNative.CopyClusterWeights(scene, meshIndex, skinIndex, clusterIndex, weights, weights.Length);
                }

                clusters.Add(new FbxCluster(
                    (long)clusterInfo.Id,
                    clusterName,
                    boneIndex,
                    bone,
                    FbxClusterLinkMode.Normalize,
                    ToMatrix(clusterInfo.MeshBindWorld),
                    true,
                    ToMatrix(clusterInfo.BoneBindWorld),
                    true,
                    FbxMatrix4x4.Identity,
                    false,
                    ToMatrix(clusterInfo.MeshNodeToBone),
                    ToMatrix(clusterInfo.GeometryToBone),
                    indices,
                    weights));

                for (int i = 0; i < indices.Length && i < weights.Length; i++)
                {
                    int controlPointIndex = indices[i];
                    if (controlPointIndex < 0 || controlPointIndex >= weightsByControlPoint.Length || weights[i] == 0d)
                    {
                        continue;
                    }

                    weightsByControlPoint[controlPointIndex] ??= new List<FbxControlPointBoneWeight>();
                    weightsByControlPoint[controlPointIndex].Add(new FbxControlPointBoneWeight(boneIndex, (float)weights[i]));
                }
            }

            var packedWeights = new IReadOnlyList<FbxControlPointBoneWeight>[weightsByControlPoint.Length];
            for (int i = 0; i < packedWeights.Length; i++)
            {
                packedWeights[i] = FbxCollection.ToReadOnly(
                    weightsByControlPoint[i]?.OrderByDescending(weight => weight.Weight).ToArray() ?? Array.Empty<FbxControlPointBoneWeight>());
            }

            return new FbxSkinDeformer((long)skinInfo.Id, skinName, bones, clusters, packedWeights);
        }

        private static FbxBlendShapeDeformer BuildBlendDeformer(IntPtr scene, int meshIndex, int deformerIndex, UfbxNative.BlendDeformerInfo info)
        {
            string name = CopyString(info.NameLength, builder => UfbxNative.CopyBlendDeformerName(scene, meshIndex, deformerIndex, builder, builder.Capacity));
            var channels = new List<FbxBlendShapeChannel>();
            for (int channelIndex = 0; channelIndex < info.ChannelCount; channelIndex++)
            {
                if (UfbxNative.GetBlendChannelInfo(scene, meshIndex, deformerIndex, channelIndex, out var channelInfo) == 0)
                {
                    continue;
                }

                channels.Add(BuildBlendChannel(scene, meshIndex, deformerIndex, channelIndex, channelInfo));
            }

            return new FbxBlendShapeDeformer((long)info.Id, name, channels);
        }

        private static FbxBlendShapeChannel BuildBlendChannel(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, UfbxNative.BlendChannelInfo info)
        {
            string name = CopyString(info.NameLength, builder => UfbxNative.CopyBlendChannelName(scene, meshIndex, deformerIndex, channelIndex, builder, builder.Capacity));
            var frames = new List<FbxShapeFrame>();
            for (int frameIndex = 0; frameIndex < info.FrameCount; frameIndex++)
            {
                if (UfbxNative.GetBlendFrameInfo(scene, meshIndex, deformerIndex, channelIndex, frameIndex, out var frameInfo) == 0)
                {
                    continue;
                }

                int[] indices = new int[Math.Max(0, frameInfo.OffsetCount)];
                double[] positions = new double[indices.Length * 3];
                double[] normals = new double[indices.Length * 3];
                if (indices.Length > 0)
                {
                    UfbxNative.CopyBlendFrameOffsets(scene, meshIndex, deformerIndex, channelIndex, frameIndex, indices, positions, normals, indices.Length);
                }

                frames.Add(new FbxShapeFrame(
                    frameInfo.Weight,
                    indices,
                    ToVector3dArray(positions),
                    ToVector3dArray(normals),
                    null,
                    FbxShapeValueMode.Relative));
            }

            return new FbxBlendShapeChannel((long)info.Id, name, frames);
        }

        private static string[] NormalizeRequestedNodePaths(IEnumerable<string> nodePaths)
        {
            return nodePaths?
                .Select(FbxNameUtility.NormalizePath)
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }

        private static string CopyString(int length, Func<StringBuilder, int> copy)
        {
            var builder = new StringBuilder(Math.Max(1, length + 1));
            copy(builder);
            return builder.ToString();
        }

        private static Vector3d[] CopyVector3dArray(int count, Func<double[], int> copy)
        {
            if (count <= 0)
            {
                return Array.Empty<Vector3d>();
            }

            var values = new double[count * 3];
            return copy(values) != 0 ? ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        private static Vector3d[] ToVector3dArray(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<Vector3d>();
            }

            var result = new Vector3d[values.Length / 3];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new Vector3d(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            }

            return result;
        }

        private static FbxMatrix4x4 ToMatrix(UfbxNative.Matrix matrix)
        {
            return FbxMatrix4x4.FromRowMajor(matrix.ToRowMajorArray());
        }

        private static FbxSceneNodeType ToNodeType(int type)
        {
            return type switch
            {
                1 => FbxSceneNodeType.Mesh,
                3 => FbxSceneNodeType.LimbNode,
                4 => FbxSceneNodeType.Root,
                _ => FbxSceneNodeType.Null
            };
        }
    }
}
