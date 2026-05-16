using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Builds extraction-owned FBX documents with extraction-specific mesh normalization applied.
    /// </summary>
    internal static class FbxExtractionDocumentNormalizer
    {
        public static FbxDocument NormalizeMeshes(
            FbxDocument document,
            Func<FbxMeshGeometry, FbxMeshGeometry> normalizeMesh)
        {
            if (document == null)
            {
                return null;
            }

            var nodeMap = document.Nodes.ToDictionary(
                node => node,
                node => new FbxSceneNode(
                    node.Id,
                    node.Name,
                    node.Path,
                    node.NodeType,
                    node.LocalTransform));

            foreach (var pair in nodeMap)
            {
                var sourceNode = pair.Key;
                var clonedNode = pair.Value;
                if (sourceNode.Parent != null && nodeMap.TryGetValue(sourceNode.Parent, out var clonedParent))
                {
                    clonedNode.SetParent(clonedParent);
                }

                clonedNode.SetChildren(sourceNode.Children
                    .Where(nodeMap.ContainsKey)
                    .Select(child => nodeMap[child]));

                if (sourceNode.IsBone)
                {
                    clonedNode.MarkBone();
                }
            }

            var meshes = new List<FbxMeshGeometry>();
            foreach (var pair in nodeMap)
            {
                var sourceMesh = pair.Key.Mesh;
                if (sourceMesh == null)
                {
                    continue;
                }

                var meshToClone = normalizeMesh != null ? normalizeMesh(sourceMesh) : sourceMesh;
                var clonedMesh = CloneMesh(meshToClone, pair.Value, nodeMap);
                pair.Value.SetMesh(clonedMesh);
                meshes.Add(clonedMesh);
            }

            var root = nodeMap.TryGetValue(document.RootNode, out var clonedRoot)
                ? clonedRoot
                : new FbxSceneNode(0, string.Empty, string.Empty, FbxSceneNodeType.Root, FbxTransform.Identity);
            return new FbxDocument(
                root,
                nodeMap.Values,
                meshes,
                null,
                document.AssetPath);
        }

        private static FbxMeshGeometry CloneMesh(
            FbxMeshGeometry source,
            FbxSceneNode ownerNode,
            IReadOnlyDictionary<FbxSceneNode, FbxSceneNode> nodeMap)
        {
            var deformers = source.Deformers
                .Select(deformer => CloneDeformer(deformer, nodeMap))
                .Where(deformer => deformer != null)
                .ToArray();
            var mesh = new FbxMeshGeometry(
                source.Id,
                source.Name,
                ownerNode,
                source.ControlPointCount,
                source.ControlPoints,
                source.ControlPointNormals,
                source.ControlPointTangents,
                deformers);

            foreach (var deformer in deformers)
            {
                deformer.OwnerMesh = mesh;
            }

            return mesh;
        }

        private static FbxDeformer CloneDeformer(
            FbxDeformer source,
            IReadOnlyDictionary<FbxSceneNode, FbxSceneNode> nodeMap)
        {
            return source switch
            {
                FbxBlendShapeDeformer blendShape => new FbxBlendShapeDeformer(
                    blendShape.Id,
                    blendShape.Name,
                    blendShape.Channels.Select(channel => new FbxBlendShapeChannel(
                        channel.Id,
                        channel.Name,
                        channel.Frames.Select(frame => new FbxShapeFrame(
                            frame.FrameWeight,
                            frame.ControlPointIndices,
                            frame.ControlPointDeltas,
                            frame.ControlPointNormalDeltas,
                            frame.ControlPointTangentDeltas,
                            frame.SourceValueMode))))),
                FbxSkinDeformer skin => CloneSkinDeformer(skin, nodeMap),
                FbxVertexCacheDeformer vertexCache => new FbxVertexCacheDeformer(vertexCache.Id, vertexCache.Name),
                FbxUnknownDeformer unknown => new FbxUnknownDeformer(unknown.Id, unknown.Name, unknown.RawType),
                _ => null
            };
        }

        private static FbxSkinDeformer CloneSkinDeformer(
            FbxSkinDeformer source,
            IReadOnlyDictionary<FbxSceneNode, FbxSceneNode> nodeMap)
        {
            var bones = source.Bones
                .Select(bone =>
                {
                    var clonedNode = bone.Node != null && nodeMap.TryGetValue(bone.Node, out var mappedNode)
                        ? mappedNode
                        : null;
                    return new FbxBoneBinding(
                        bone.Index,
                        bone.NodeId,
                        bone.Name,
                        bone.Path,
                        clonedNode);
                })
                .ToArray();
            var bonesByIndex = bones.ToDictionary(bone => bone.Index);
            var clusters = source.Clusters
                .Select(cluster => new FbxCluster(
                    cluster.Id,
                    cluster.Name,
                    cluster.BoneIndex,
                    bonesByIndex.TryGetValue(cluster.BoneIndex, out var bone) ? bone : null,
                    cluster.LinkMode,
                    cluster.TransformMatrix,
                    cluster.HasTransformMatrix,
                    cluster.TransformLinkMatrix,
                    cluster.HasTransformLinkMatrix,
                    cluster.TransformAssociateModelMatrix,
                    cluster.HasTransformAssociateModelMatrix,
                    cluster.ControlPointIndices,
                    cluster.Weights))
                .ToArray();

            return new FbxSkinDeformer(
                source.Id,
                source.Name,
                bones,
                clusters,
                source.ControlPointWeights);
        }
    }
}
