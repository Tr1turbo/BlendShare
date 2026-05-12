using System;
using System.Collections.Generic;
using System.Linq;

namespace Triturbo.FBX
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
        public IReadOnlyList<FbxMeshGeometry> Meshes { get; }
        public IReadOnlyList<FbxMeshDescriptor> MeshDescriptors { get; }
        public FbxMeshReadOptions RequestedOptions { get; }
        public FbxMeshReadOptions AvailableOptions { get; }

        internal FbxDocument(
            FbxSceneNode rootNode,
            IEnumerable<FbxSceneNode> nodes,
            IEnumerable<FbxMeshGeometry> meshes,
            FbxMeshReadOptions requestedOptions)
        {
            RootNode = rootNode;
            Nodes = FbxCollection.ToReadOnly(nodes);
            Meshes = FbxCollection.ToReadOnly(meshes);
            MeshDescriptors = FbxCollection.ToReadOnly(Meshes.Select(mesh => mesh.Descriptor));
            RequestedOptions = requestedOptions;
            AvailableOptions = Meshes.Aggregate(FbxMeshReadOptions.None, (current, mesh) => current | mesh.AvailableOptions);
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
                : FbxReadResult<FbxSceneNode>.Failed(FbxReadStatus.MeshNotFound, $"FBX node '{path}' was not found.");
        }

        public FbxReadResult<FbxMeshGeometry> TryFindMesh(FbxMeshSelector selector)
        {
            List<FbxMeshGeometry> matches;
            switch (selector.Kind)
            {
                case FbxMeshSelectorKind.GeometryId:
                    matches = Meshes.Where(mesh => mesh.Id == selector.Id).ToList();
                    break;
                case FbxMeshSelectorKind.NodePath:
                    string path = FbxNameUtility.NormalizePath(selector.Value);
                    matches = Meshes.Where(mesh => mesh.OwnerNode != null && mesh.OwnerNode.Path == path).ToList();
                    break;
                case FbxMeshSelectorKind.NodeName:
                    matches = Meshes.Where(mesh => mesh.OwnerNode != null && mesh.OwnerNode.Name == selector.Value).ToList();
                    break;
                case FbxMeshSelectorKind.GeometryName:
                    matches = Meshes.Where(mesh => mesh.Name == selector.Value).ToList();
                    break;
                default:
                    return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.InvalidArgument, "Unsupported FBX mesh selector.");
            }

            if (matches.Count == 0)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(FbxReadStatus.MeshNotFound, "No FBX mesh matched the selector.");
            }

            if (matches.Count > 1)
            {
                return FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.AmbiguousMesh,
                    "More than one FBX mesh matched the selector.",
                    candidateMeshes: matches.Select(mesh => mesh.Descriptor));
            }

            return FbxReadResult<FbxMeshGeometry>.Succeeded(matches[0]);
        }
    }

    public sealed class FbxSceneNode
    {
        private IReadOnlyList<FbxSceneNode> children = Array.AsReadOnly(Array.Empty<FbxSceneNode>());
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
        public FbxMeshGeometry Mesh { get; private set; }
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
            if (Mesh == null)
            {
                Mesh = mesh;
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
        public FbxMeshReadOptions AvailableOptions { get; }

        internal FbxMeshDescriptor(
            long geometryId,
            long nodeId,
            string geometryName,
            string nodeName,
            string nodePath,
            int controlPointCount,
            bool hasBlendShapes,
            bool hasBoneWeights,
            FbxMeshReadOptions availableOptions)
        {
            GeometryId = geometryId;
            NodeId = nodeId;
            GeometryName = geometryName ?? string.Empty;
            NodeName = nodeName ?? string.Empty;
            NodePath = nodePath ?? string.Empty;
            ControlPointCount = Math.Max(0, controlPointCount);
            HasBlendShapes = hasBlendShapes;
            HasBoneWeights = hasBoneWeights;
            AvailableOptions = availableOptions;
        }
    }

    public sealed class FbxMeshGeometry
    {
        public long Id { get; }
        public string Name { get; }
        public FbxSceneNode OwnerNode { get; }
        public IReadOnlyList<Vector3d> ControlPoints { get; }
        public IReadOnlyList<FbxDeformer> Deformers { get; }
        public IReadOnlyList<FbxSkinDeformer> SkinDeformers { get; }
        public IReadOnlyList<FbxBlendShapeDeformer> BlendShapeDeformers { get; }
        public FbxMeshReadOptions RequestedOptions { get; }
        public FbxMeshReadOptions AvailableOptions { get; }
        public FbxMeshDescriptor Descriptor { get; }
        public int ControlPointCount { get; }
        public bool HasControlPoints => ControlPoints.Count > 0;
        public bool HasBlendShapes => BlendShapeDeformers.Count > 0;
        public bool HasBoneWeights => SkinDeformers.Count > 0;

        internal FbxMeshGeometry(
            long id,
            string name,
            FbxSceneNode ownerNode,
            int controlPointCount,
            IEnumerable<Vector3d> controlPoints,
            IEnumerable<FbxDeformer> deformers,
            FbxMeshReadOptions requestedOptions,
            FbxMeshReadOptions availableOptions)
        {
            Id = id;
            Name = name ?? string.Empty;
            OwnerNode = ownerNode;
            ControlPoints = FbxCollection.ToReadOnly(controlPoints);
            Deformers = FbxCollection.ToReadOnly(deformers);
            SkinDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxSkinDeformer>());
            BlendShapeDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxBlendShapeDeformer>());
            RequestedOptions = requestedOptions;
            AvailableOptions = availableOptions;
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
                availableOptions);
        }

        public bool TryGetSkin(out FbxSkinDeformer skin)
        {
            skin = SkinDeformers.Count > 0 ? SkinDeformers[0] : null;
            return skin != null;
        }
    }
}
