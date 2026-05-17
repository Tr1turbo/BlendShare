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

    public enum FbxNodeAttributeType
    {
        Unknown,
        Null,
        Skeleton,
        Mesh
    }

    public sealed class FbxDocument
    {
        public FbxSceneNode RootNode { get; }
        public IReadOnlyList<FbxSceneNode> Nodes { get; }
        public IReadOnlyList<FbxMeshGeometry> Meshes =>
            FbxCollection.ToReadOnly(Nodes.Select(node => node.Mesh).Where(mesh => mesh != null));
        public string AssetPath { get; }

        internal FbxDocument(
            FbxSceneNode rootNode,
            IEnumerable<FbxSceneNode> nodes,
            string assetPath = null)
        {
            RootNode = rootNode;
            Nodes = FbxCollection.ToReadOnly(nodes);
            AssetPath = assetPath;
        }

        public IReadOnlyList<FbxMeshGeometry> ListMeshes()
        {
            return Meshes;
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
                    ListMeshes());
            }

            var mesh = nodeResult.Value.Mesh;
            return mesh != null
                ? FbxReadResult<FbxMeshGeometry>.Succeeded(mesh)
                : FbxReadResult<FbxMeshGeometry>.Failed(
                    FbxReadStatus.MeshNotFound,
                    $"FBX node '{nodePath}' does not contain a mesh.",
                    candidateMeshes: ListMeshes());
        }
    }

    public sealed class FbxSceneNode
    {
        private IReadOnlyList<FbxSceneNode> children = Array.AsReadOnly(Array.Empty<FbxSceneNode>());
        private FbxNodeAttribute attribute;
        private readonly FbxSceneNodeType fallbackNodeType;
        private bool isClusterBone;

        public long Id { get; }
        public string Name { get; }
        public string Path { get; }
        public FbxSceneNodeType NodeType => Attribute switch
        {
            FbxMeshGeometry => FbxSceneNodeType.Mesh,
            FbxSkeleton skeleton => skeleton.SkeletonType == "LimbNode"
                ? FbxSceneNodeType.LimbNode
                : FbxSceneNodeType.Skeleton,
            _ => fallbackNodeType
        };
        public Vector3d LocalTranslation => LocalTransform.Translation;
        public Vector3d LocalRotation => LocalTransform.Rotation;
        public Vector3d LocalScale => LocalTransform.Scale;
        public FbxTransform LocalTransform { get; }
        public FbxSceneNode Parent { get; private set; }
        public IReadOnlyList<FbxSceneNode> Children => children;
        public FbxNodeAttribute Attribute => attribute;
        public FbxMeshGeometry Mesh => Attribute as FbxMeshGeometry;
        public FbxSkeleton Skeleton => Attribute as FbxSkeleton;
        public bool HasMesh => Mesh != null;
        public bool HasAttribute => Attribute != null;
        public bool IsBone => isClusterBone || Attribute is FbxSkeleton;

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
            fallbackNodeType = nodeType;
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

        internal void SetAttribute(FbxNodeAttribute attribute)
        {
            if (this.attribute == null)
            {
                this.attribute = attribute;
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

    /// <summary>
    /// Simplified equivalent of the FBX SDK's FbxNodeAttribute.
    /// A scene node owns one attribute that describes what kind of object the node represents.
    /// </summary>
    public abstract class FbxNodeAttribute
    {
        public long Id { get; }
        public string Name { get; }
        public FbxSceneNode OwnerNode { get; }
        public FbxNodeAttributeType AttributeType { get; }

        protected FbxNodeAttribute(
            long id,
            string name,
            FbxSceneNode ownerNode,
            FbxNodeAttributeType attributeType)
        {
            Id = id;
            Name = name ?? string.Empty;
            OwnerNode = ownerNode;
            AttributeType = attributeType;
        }
    }

    /// <summary>
    /// Simplified skeleton node attribute, equivalent to the FBX SDK's FbxSkeleton concept.
    /// Used to identify nodes that participate in skeleton hierarchies.
    /// </summary>
    public sealed class FbxSkeleton : FbxNodeAttribute
    {
        public string SkeletonType { get; }

        internal FbxSkeleton(
            long id,
            string name,
            FbxSceneNode ownerNode,
            string skeletonType)
            : base(id, name, ownerNode, FbxNodeAttributeType.Skeleton)
        {
            SkeletonType = string.IsNullOrEmpty(skeletonType) ? "Skeleton" : skeletonType;
        }
    }

    /// <summary>
    /// Simplified mesh node attribute.
    /// This intentionally folds the FBX SDK mesh-related hierarchy
    /// (FbxLayerContainer, FbxGeometryBase, FbxGeometry, and FbxMesh) into one reader type.
    /// </summary>
    public sealed class FbxMeshGeometry : FbxNodeAttribute
    {
        private readonly Vector3d[] controlPoints;
        private readonly Vector3d[] controlPointNormals;
        private readonly Vector3d[] controlPointTangents;

        public IReadOnlyList<Vector3d> ControlPoints { get; }
        public IReadOnlyList<Vector3d> ControlPointNormals { get; }
        public IReadOnlyList<Vector3d> ControlPointTangents { get; }
        public IReadOnlyList<FbxDeformer> Deformers { get; }
        public IReadOnlyList<FbxSkinDeformer> SkinDeformers { get; }
        public IReadOnlyList<FbxBlendShapeDeformer> BlendShapeDeformers { get; }
        public int ControlPointCount { get; }
        public bool HasControlPoints => ControlPoints.Count > 0;
        public bool HasNormals => ControlPointNormals.Count > 0;
        public bool HasTangents => ControlPointTangents.Count > 0;
        public bool HasBlendShapes => BlendShapeDeformers.Count > 0;
        public bool HasBoneWeights => SkinDeformers.Count > 0;

        internal Vector3d[] MutableControlPoints => controlPoints;
        internal Vector3d[] MutableControlPointNormals => controlPointNormals;
        internal Vector3d[] MutableControlPointTangents => controlPointTangents;

        internal FbxMeshGeometry(
            long id,
            string name,
            FbxSceneNode ownerNode,
            int controlPointCount,
            IEnumerable<Vector3d> controlPoints,
            IEnumerable<Vector3d> controlPointNormals,
            IEnumerable<Vector3d> controlPointTangents,
            IEnumerable<FbxDeformer> deformers)
            : base(id, name, ownerNode, FbxNodeAttributeType.Mesh)
        {
            this.controlPoints = ToArray(controlPoints);
            this.controlPointNormals = ToArray(controlPointNormals);
            this.controlPointTangents = ToArray(controlPointTangents);
            ControlPoints = Array.AsReadOnly(this.controlPoints);
            ControlPointNormals = Array.AsReadOnly(this.controlPointNormals);
            ControlPointTangents = Array.AsReadOnly(this.controlPointTangents);
            Deformers = FbxCollection.ToReadOnly(deformers);
            SkinDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxSkinDeformer>());
            BlendShapeDeformers = FbxCollection.ToReadOnly(Deformers.OfType<FbxBlendShapeDeformer>());
            ControlPointCount = Math.Max(controlPointCount, this.controlPoints.Length);
        }

        public bool TryGetSkin(out FbxSkinDeformer skin)
        {
            skin = SkinDeformers.Count > 0 ? SkinDeformers[0] : null;
            return skin != null;
        }

        private static Vector3d[] ToArray(IEnumerable<Vector3d> values)
        {
            return values switch
            {
                null => Array.Empty<Vector3d>(),
                Vector3d[] array => (Vector3d[])array.Clone(),
                _ => values.ToArray()
            };
        }
    }
}
