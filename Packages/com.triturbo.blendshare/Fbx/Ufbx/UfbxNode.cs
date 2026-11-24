using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Scene-backed non-mesh node attribute (bone, camera, light, empty, or other) that may be instanced across multiple nodes.</summary>
    public sealed class UfbxNodeAttribute : UfbxElement, IFbxNodeAttribute
    {
        private IReadOnlyList<IFbxNode> instances = Array.AsReadOnly(Array.Empty<IFbxNode>());

        internal UfbxNodeAttribute(UfbxScene scene, long id, string name, FbxNodeAttributeKind attributeKind)
            : base(scene, FbxElementType.Unknown, -1, id, name)
        {
            AttributeKind = attributeKind;
        }

        /// <inheritdoc />
        public FbxNodeAttributeKind AttributeKind { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxNode> Instances => instances;

        internal void SetInstances(IEnumerable<IFbxNode> nodes)
        {
            instances = FbxReadOnlyList.ToReadOnly(nodes);
        }
    }

    /// <summary>Scene-backed ufbx node in the evaluated scene hierarchy.</summary>
    public sealed class UfbxNode : UfbxElement, IFbxNode
    {
        private IReadOnlyList<IFbxNode> children = Array.AsReadOnly(Array.Empty<IFbxNode>());
        private IReadOnlyList<IFbxMaterial> materials = Array.AsReadOnly(Array.Empty<IFbxMaterial>());

        internal UfbxNode(
            UfbxScene scene,
            int nodeIndex,
            long id,
            string name,
            string path,
            bool isRoot,
            bool isBone,
            FbxNodeAttributeKind attributeKind,
            int meshIndex,
            FbxTransform localTransform,
            FbxTransform geometryTransform,
            FbxMatrix4x4 nodeToParentMatrix,
            FbxMatrix4x4 nodeToWorldMatrix,
            FbxMatrix4x4 geometryToNodeMatrix,
            FbxMatrix4x4 geometryToWorldMatrix,
            FbxNodeRawTransform rawTransform)
            : base(scene, FbxElementType.Node, nodeIndex, id, name)
        {
            Path = FbxNameUtility.NormalizePath(path);
            IsRoot = isRoot;
            IsBone = isBone;
            AttributeKind = attributeKind;
            MeshIndex = meshIndex;
            LocalTransform = localTransform;
            GeometryTransform = geometryTransform;
            NodeToParentMatrix = nodeToParentMatrix;
            NodeToWorldMatrix = nodeToWorldMatrix;
            GeometryToNodeMatrix = geometryToNodeMatrix;
            GeometryToWorldMatrix = geometryToWorldMatrix;
            RawTransform = rawTransform;
        }

        internal int MeshIndex { get; }

        /// <inheritdoc />
        public string Path { get; }

        /// <inheritdoc />
        public bool IsRoot { get; }

        /// <inheritdoc />
        public bool IsBone { get; }

        /// <inheritdoc />
        public FbxNodeAttributeKind AttributeKind { get; }

        /// <inheritdoc />
        public IFbxNode Parent { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxNode> Children => children;

        /// <inheritdoc />
        public IFbxNodeAttribute Attribute { get; private set; }

        /// <inheritdoc />
        public IFbxMesh Mesh { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxMaterial> Materials => materials;

        /// <inheritdoc />
        public FbxTransform LocalTransform { get; }

        /// <inheritdoc />
        public FbxTransform GeometryTransform { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 NodeToParentMatrix { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 NodeToWorldMatrix { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 GeometryToNodeMatrix { get; }

        /// <inheritdoc />
        public FbxMatrix4x4 GeometryToWorldMatrix { get; }

        /// <inheritdoc />
        public FbxNodeRawTransform RawTransform { get; }

        internal void SetParent(IFbxNode parent)
        {
            Parent = parent;
        }

        internal void SetChildren(IEnumerable<IFbxNode> nodes)
        {
            children = FbxReadOnlyList.ToReadOnly(nodes);
        }

        internal void SetAttribute(IFbxNodeAttribute attribute)
        {
            Attribute = attribute;
        }

        internal void SetMesh(UfbxMesh mesh)
        {
            Mesh = mesh;
            if (mesh != null)
            {
                Attribute = mesh;
            }
        }

        internal void SetMaterials(IEnumerable<IFbxMaterial> nodeMaterials)
        {
            materials = FbxReadOnlyList.ToReadOnly(nodeMaterials);
        }
    }
}
