using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Triturbo.BlendShapeShare.Data.Editor")]

namespace Triturbo.Fbx
{
    public enum FbxSceneNodeType
    {
        Unknown,
        Null,
        Mesh,
        Skeleton,
        LimbNode,
        Root
    }

    public sealed class FbxDocument
    {
        public FbxSceneNode RootNode { get; }
        public IReadOnlyList<FbxSceneNode> Nodes { get; }
        public IReadOnlyList<FbxMeshDescriptor> MeshDescriptors { get; }
        public IReadOnlyList<FbxMeshGeometry> Meshes =>
            FbxCollection.ToReadOnly(Nodes.Select(node => node.Mesh).Where(mesh => mesh != null));
        public string AssetPath { get; }

        internal FbxDocument(
            FbxSceneNode rootNode,
            IEnumerable<FbxSceneNode> nodes,
            IEnumerable<FbxMeshGeometry> meshes,
            IEnumerable<FbxMeshDescriptor> meshDescriptors = null,
            string assetPath = null)
        {
            RootNode = rootNode;
            Nodes = FbxCollection.ToReadOnly(nodes);
            MeshDescriptors = FbxCollection.ToReadOnly(meshDescriptors ?? (meshes ?? Enumerable.Empty<FbxMeshGeometry>()).Select(mesh => mesh.Descriptor));
            AssetPath = assetPath;
        }

        public IReadOnlyList<FbxMeshDescriptor> ListMeshes()
        {
            return MeshDescriptors;
        }

        public FbxReadResult<FbxSceneNode> TryFindNode(string path)
        {
            string normalizedPath = FbxNameUtility.NormalizePath(path);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return FbxReadResult<FbxSceneNode>.Succeeded(RootNode);
            }

            var node = Nodes.FirstOrDefault(candidate => candidate.Path == normalizedPath);
            return node != null
                ? FbxReadResult<FbxSceneNode>.Succeeded(node)
                : FbxReadResult<FbxSceneNode>.Failed(FbxReadStatus.NodeNotFound, $"FBX node '{path}' was not found.");
        }

        public FbxReadResult<FbxMeshGeometry> TryFindMesh(string nodePath)
        {
            var nodeResult = TryFindNode(nodePath);
            if (!nodeResult.Success)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    nodeResult.Status,
                    nodeResult.Message,
                    nodeResult.Diagnostics,
                    MeshDescriptors);
            }

            var mesh = nodeResult.Value.Mesh;
            return mesh != null
                ? FbxReadResult<FbxMeshGeometry>.Succeeded(mesh)
                : FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.MeshNotFound,
                    $"FBX node '{nodePath}' does not contain a mesh.",
                    candidateMeshes: MeshDescriptors);
        }
    }

    public sealed class FbxSceneNode
    {
        private IReadOnlyList<FbxSceneNode> children = Array.AsReadOnly(Array.Empty<FbxSceneNode>());
        private FbxMeshGeometry mesh;
        private bool isClusterBone;

        public long Id { get; }
        public string Name { get; }
        public string Path { get; }
        public FbxSceneNodeType NodeType { get; }
        public Vector3d LocalTranslation => LocalTransform.Translation;
        public Vector3d LocalRotation => LocalTransform.Rotation;
        public Vector3d LocalScale => LocalTransform.Scale;
        public FbxTransform LocalTransform { get; }
        public FbxSceneNode Parent { get; private set; }
        public IReadOnlyList<FbxSceneNode> Children => children;
        public FbxMeshGeometry Mesh => mesh;
        public bool HasMesh => Mesh != null;
        public bool IsBone => isClusterBone || NodeType == FbxSceneNodeType.Skeleton || NodeType == FbxSceneNodeType.LimbNode;

        internal FbxSceneNode(
            long id,
            string name,
            string path,
            FbxSceneNodeType nodeType = FbxSceneNodeType.Unknown,
            FbxTransform? localTransform = null)
        {
            Id = id;
            Name = name ?? string.Empty;
            Path = FbxNameUtility.NormalizePath(path);
            NodeType = nodeType;
            LocalTransform = localTransform ?? FbxTransform.Identity;
        }

        internal void SetParent(FbxSceneNode parent)
        {
            Parent = parent;
        }

        internal void SetChildren(IEnumerable<FbxSceneNode> nodes)
        {
            children = FbxCollection.ToReadOnly(nodes);
        }

        internal void SetMesh(FbxMeshGeometry mesh)
        {
            if (this.mesh == null)
            {
                this.mesh = mesh;
            }
        }

        internal void MarkBone()
        {
            isClusterBone = true;
        }

        public FbxSceneNode FindChild(string name, bool recursive = false)
        {
            foreach (var child in Children)
            {
                if (child.Name == name)
                {
                    return child;
                }

                if (recursive)
                {
                    var descendant = child.FindChild(name, true);
                    if (descendant != null)
                    {
                        return descendant;
                    }
                }
            }

            return null;
        }
    }

    public sealed class FbxMeshDescriptor
    {
        public long GeometryId { get; }
        public long NodeId { get; }
        public string GeometryName { get; }
        public string NodeName { get; }
        public string NodePath { get; }
        public int ControlPointCount { get; }
        public bool HasBlendShapes { get; }
        public bool HasBoneWeights { get; }
        public bool HasNormals { get; }
        public bool HasTangents { get; }

        internal FbxMeshDescriptor(
            long geometryId,
            long nodeId,
            string geometryName,
            string nodeName,
            string nodePath,
            int controlPointCount,
            bool hasBlendShapes,
            bool hasBoneWeights,
            bool hasNormals,
            bool hasTangents)
        {
            GeometryId = geometryId;
            NodeId = nodeId;
            GeometryName = geometryName ?? string.Empty;
            NodeName = nodeName ?? string.Empty;
            NodePath = nodePath ?? string.Empty;
            ControlPointCount = Math.Max(0, controlPointCount);
            HasBlendShapes = hasBlendShapes;
            HasBoneWeights = hasBoneWeights;
            HasNormals = hasNormals;
            HasTangents = hasTangents;
        }
    }

    public sealed class FbxMeshGeometry
    {
        public long Id { get; }
        public string Name { get; }
        public FbxSceneNode OwnerNode { get; }
        public IReadOnlyList<Vector3d> ControlPoints { get; }
        public IReadOnlyList<Vector3d> ControlPointNormals { get; }
        public IReadOnlyList<Vector3d> ControlPointTangents { get; }
        public IReadOnlyList<FbxDeformer> Deformers { get; }
        public IReadOnlyList<FbxSkinDeformer> SkinDeformers { get; }
        public IReadOnlyList<FbxBlendShapeDeformer> BlendShapeDeformers { get; }
        public FbxMeshDescriptor Descriptor { get; }
        public int ControlPointCount { get; }
        public bool HasControlPoints => ControlPoints.Count > 0;
        public bool HasNormals => ControlPointNormals.Count > 0;
        public bool HasTangents => ControlPointTangents.Count > 0;
        public bool HasBlendShapes => BlendShapeDeformers.Count > 0;
        public bool HasBoneWeights => SkinDeformers.Count > 0;

        internal FbxMeshGeometry(
            long id,
            string name,
            FbxSceneNode ownerNode,
            int controlPointCount,
            IEnumerable<Vector3d> controlPoints,
            IEnumerable<Vector3d> controlPointNormals,
            IEnumerable<Vector3d> controlPointTangents,
            IEnumerable<FbxDeformer> deformers)
        {
            Id = id;
            Name = name ?? string.Empty;
            OwnerNode = ownerNode;
            ControlPoints = FbxCollection.ToReadOnly(controlPoints);
            ControlPointNormals = FbxCollection.ToReadOnly(controlPointNormals);
            ControlPointTangents = FbxCollection.ToReadOnly(controlPointTangents);
            Deformers = FbxCollection.ToReadOnly(deformers);
            SkinDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxSkinDeformer>());
            BlendShapeDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxBlendShapeDeformer>());
            ControlPointCount = Math.Max(controlPointCount, ControlPoints.Count);
            Descriptor = new FbxMeshDescriptor(
                id,
                ownerNode?.Id ?? 0,
                Name,
                ownerNode?.Name,
                ownerNode?.Path,
                ControlPointCount,
                BlendShapeDeformers.Count > 0,
                SkinDeformers.Count > 0,
                ControlPointNormals.Count > 0,
                ControlPointTangents.Count > 0);
        }

        public bool TryGetSkin(out FbxSkinDeformer skin)
        {
            skin = SkinDeformers.Count > 0 ? SkinDeformers[0] : null;
            return skin != null;
        }
    }
}
