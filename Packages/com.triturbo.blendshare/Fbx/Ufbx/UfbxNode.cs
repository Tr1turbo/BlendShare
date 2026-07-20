using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public sealed class UfbxNode : UfbxElement
    {
        private IReadOnlyList<UfbxNode> children = Array.AsReadOnly(Array.Empty<UfbxNode>());

        internal UfbxNode(
            UfbxScene scene,
            int nodeIndex,
            long id,
            string name,
            string path,
            UfbxNodeType nodeType,
            UfbxTransform localTransform,
            Vector3d lclTranslation = default,
            Vector3d lclRotation = default,
            Vector3d lclScaling = default,
            FbxRotationOrder rotationOrder = FbxRotationOrder.XYZ,
            FbxTransformInheritMode inheritMode = FbxTransformInheritMode.RrSs,
            bool rotationActive = true,
            Vector3d preRotation = default,
            Vector3d postRotation = default,
            Vector3d rotationPivot = default,
            Vector3d scalingPivot = default,
            Vector3d rotationOffset = default,
            Vector3d scalingOffset = default,
            FbxMatrix4x4 nodeToParentMatrix = default)
            : base(scene, UfbxElementType.Node, nodeIndex, id, name)
        {
            Path = FbxNameUtility.NormalizePath(path);
            NodeType = nodeType;
            LocalTransform = localTransform;
            LclTranslation = lclTranslation;
            LclRotation = lclRotation;
            LclScaling = lclScaling;
            RotationOrder = rotationOrder;
            InheritMode = inheritMode;
            RotationActive = rotationActive;
            PreRotation = preRotation;
            PostRotation = postRotation;
            RotationPivot = rotationPivot;
            ScalingPivot = scalingPivot;
            RotationOffset = rotationOffset;
            ScalingOffset = scalingOffset;
            NodeToParentMatrix = nodeToParentMatrix;
        }

        public string Path { get; }
        public UfbxNodeType NodeType { get; }
        public UfbxTransform LocalTransform { get; }
        public Vector3d LclTranslation { get; }
        public Vector3d LclRotation { get; }
        public Vector3d LclScaling { get; }
        public FbxRotationOrder RotationOrder { get; }
        public FbxTransformInheritMode InheritMode { get; }
        public bool RotationActive { get; }
        public Vector3d PreRotation { get; }
        public Vector3d PostRotation { get; }
        public Vector3d RotationPivot { get; }
        public Vector3d ScalingPivot { get; }
        public Vector3d RotationOffset { get; }
        public Vector3d ScalingOffset { get; }
        public FbxMatrix4x4 NodeToParentMatrix { get; }
        public UfbxNode Parent { get; private set; }
        public IReadOnlyList<UfbxNode> Children => children;

        internal void SetParent(UfbxNode parent)
        {
            Parent = parent;
        }

        internal void SetChildren(IEnumerable<UfbxNode> nodes)
        {
            children = FbxCollection.ToReadOnly(nodes);
        }
    }

}
