using System;
using System.Collections.Generic;
using System.Linq;

namespace Triturbo.Fbx
{
    public static class UfbxDocumentReader
    {
        public static FbxReadResult<FbxDocument> Read(string assetPath, IEnumerable<string> nodePaths = null)
        {
            var sceneResult = UfbxScene.TryLoad(assetPath);
            if (!sceneResult.Success)
            {
                return FbxReadResult<FbxDocument>.Failed(sceneResult.Status, sceneResult.Message, sceneResult.Diagnostics);
            }

            using var scene = sceneResult.Value;
            return BuildDocument(scene, nodePaths);
        }

        private static FbxReadResult<FbxDocument> BuildDocument(UfbxScene scene, IEnumerable<string> nodePaths)
        {
            var requestedPaths = NormalizeRequestedNodePaths(nodePaths);
            if (requestedPaths.Length > 0)
            {
                var missingPaths = requestedPaths
                    .Where(path => scene.FindNodeByPath(path) == null)
                    .ToArray();
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
            var document = BuildDocumentFromScene(scene, includedPaths);
            return FbxReadResult<FbxDocument>.Succeeded(document);
        }

        internal static FbxDocument BuildDocumentFromScene(UfbxScene scene, ISet<string> includedMeshPaths = null)
        {
            var nodesByUfbx = new Dictionary<UfbxNode, FbxSceneNode>();
            var nodes = new List<FbxSceneNode>();
            foreach (var ufbxNode in scene.Nodes)
            {
                var node = new FbxSceneNode(
                    ufbxNode.Id,
                    ufbxNode.Name,
                    ufbxNode.Path,
                    ToSceneNodeType(ufbxNode.NodeType),
                    ufbxNode.LclTransform);
                nodesByUfbx[ufbxNode] = node;
                nodes.Add(node);
            }

            var children = nodes.ToDictionary(node => node, _ => new List<FbxSceneNode>());
            foreach (var ufbxNode in scene.Nodes)
            {
                var node = nodesByUfbx[ufbxNode];
                if (ufbxNode.Parent != null && nodesByUfbx.TryGetValue(ufbxNode.Parent, out var parent))
                {
                    node.SetParent(parent);
                    children[parent].Add(node);
                }

                if (ufbxNode.NodeType == UfbxNodeType.LimbNode)
                {
                    node.SetAttribute(new FbxSkeleton(node.Id, node.Name, node, "LimbNode"));
                }
            }

            foreach (var pair in children)
            {
                pair.Key.SetChildren(pair.Value);
            }

            foreach (var ufbxMesh in scene.Meshes)
            {
                if (ufbxMesh.OwnerNode == null || !nodesByUfbx.TryGetValue(ufbxMesh.OwnerNode, out var ownerNode))
                {
                    continue;
                }

                if (includedMeshPaths != null && !includedMeshPaths.Contains(ownerNode.Path))
                {
                    continue;
                }

                var deformers = BuildDeformers(ufbxMesh, nodesByUfbx, ufbxMesh.ControlPointCount);
                var mesh = new FbxMeshGeometry(
                    ufbxMesh.Id,
                    ufbxMesh.Name,
                    ownerNode,
                    ufbxMesh.ControlPointCount,
                    ufbxMesh.GetVertices(),
                    ufbxMesh.GetNormals(),
                    ufbxMesh.GetTangents(),
                    deformers);
                foreach (var deformer in deformers)
                {
                    deformer.OwnerMesh = mesh;
                }

                ownerNode.SetAttribute(mesh);
            }

            var rootNode = nodes.FirstOrDefault(node => node.Path == string.Empty) ?? new FbxSceneNode(0, string.Empty, string.Empty, FbxSceneNodeType.Root, FbxTransform.Identity);
            if (includedMeshPaths != null)
            {
                nodes = BuildPartialNodeList(rootNode, nodes, includedMeshPaths);
            }

            return new FbxDocument(rootNode, nodes, scene.AssetPath);
        }

        private static List<FbxDeformer> BuildDeformers(
            UfbxMesh mesh,
            IReadOnlyDictionary<UfbxNode, FbxSceneNode> nodesByUfbx,
            int controlPointCount)
        {
            var deformers = new List<FbxDeformer>();
            foreach (var skin in mesh.SkinDeformers)
            {
                deformers.Add(BuildSkin(skin, nodesByUfbx, controlPointCount));
            }

            foreach (var blend in mesh.BlendDeformers)
            {
                deformers.Add(BuildBlendDeformer(blend));
            }

            return deformers;
        }

        private static FbxSkinDeformer BuildSkin(
            UfbxSkinDeformer skin,
            IReadOnlyDictionary<UfbxNode, FbxSceneNode> nodesByUfbx,
            int controlPointCount)
        {
            var bones = new List<FbxBoneBinding>();
            var clusters = new List<FbxCluster>();
            var weightsByControlPoint = new List<FbxControlPointBoneWeight>[Math.Max(0, controlPointCount)];

            foreach (var ufbxCluster in skin.Clusters)
            {
                FbxSceneNode boneNode = null;
                if (ufbxCluster.BoneNode != null)
                {
                    nodesByUfbx.TryGetValue(ufbxCluster.BoneNode, out boneNode);
                }

                boneNode?.MarkBone();
                int boneIndex = bones.Count;
                var bone = new FbxBoneBinding(boneIndex, boneNode?.Id ?? 0, boneNode?.Name ?? string.Empty, boneNode?.Path ?? string.Empty, boneNode);
                bones.Add(bone);

                int[] indices = ufbxCluster.GetIndices();
                double[] weights = ufbxCluster.GetWeights();
                clusters.Add(new FbxCluster(
                    ufbxCluster.Id,
                    ufbxCluster.Name,
                    boneIndex,
                    bone,
                    FbxClusterLinkMode.Normalize,
                    ufbxCluster.MeshBindWorld,
                    true,
                    ufbxCluster.BindToWorld,
                    true,
                    FbxMatrix4x4.Identity,
                    false,
                    ufbxCluster.MeshNodeToBone,
                    ufbxCluster.GeometryToBone,
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

            return new FbxSkinDeformer(skin.Id, skin.Name, bones, clusters, packedWeights);
        }

        private static FbxBlendShapeDeformer BuildBlendDeformer(UfbxBlendDeformer blend)
        {
            var channels = new List<FbxBlendShapeChannel>();
            foreach (var channel in blend.Channels)
            {
                var frames = new List<FbxShapeFrame>();
                foreach (var shape in channel.BlendShapes)
                {
                    int[] indices = new int[Math.Max(0, shape.OffsetCount)];
                    double[] positions = new double[indices.Length * 3];
                    double[] normals = new double[indices.Length * 3];
                    if (indices.Length > 0)
                    {
                        shape.CopyOffsets(indices, positions, normals);
                    }

                    frames.Add(new FbxShapeFrame(
                        shape.Weight,
                        indices,
                        UfbxScene.ToVector3dArray(positions),
                        UfbxScene.ToVector3dArray(normals),
                        null,
                        FbxShapeValueMode.Relative));
                }

                channels.Add(new FbxBlendShapeChannel(channel.Id, channel.Name, frames));
            }

            return new FbxBlendShapeDeformer(blend.Id, blend.Name, channels);
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

        private static string[] NormalizeRequestedNodePaths(IEnumerable<string> nodePaths)
        {
            return nodePaths?
                .Select(FbxNameUtility.NormalizePath)
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }

        private static FbxSceneNodeType ToSceneNodeType(UfbxNodeType type)
        {
            return type switch
            {
                UfbxNodeType.Mesh => FbxSceneNodeType.Mesh,
                UfbxNodeType.LimbNode => FbxSceneNodeType.LimbNode,
                UfbxNodeType.Root => FbxSceneNodeType.Root,
                UfbxNodeType.Null => FbxSceneNodeType.Null,
                _ => FbxSceneNodeType.Unknown
            };
        }
    }
}
