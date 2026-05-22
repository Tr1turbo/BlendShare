using System;
using System.Collections.Generic;
using Triturbo.Fbx;

namespace Triturbo.Fbx.Ufbx
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
            FbxTransform? lclTransform = null,
            Vector3d eulerRotation = default,
            Vector3d preRotation = default,
            Vector3d postRotation = default)
            : base(scene, UfbxElementType.Node, nodeIndex, id, name)
        {
            Path = FbxNameUtility.NormalizePath(path);
            NodeType = nodeType;
            LocalTransform = localTransform;
            LclTransform = lclTransform ?? FbxTransform.Identity;
            EulerRotation = eulerRotation;
            PreRotation = preRotation;
            PostRotation = postRotation;
        }

        public string Path { get; }
        public UfbxNodeType NodeType { get; }
        public UfbxTransform LocalTransform { get; }
        public Vector3d LocalTranslation => LocalTransform.Translation;
        public Quaterniond LocalRotation => LocalTransform.Rotation;
        public Vector3d LocalScale => LocalTransform.Scale;
        public FbxTransform LclTransform { get; }
        public Vector3d LclTranslation => LclTransform.Translation;
        public Vector3d LclRotation => LclTransform.Rotation;
        public Vector3d LclScale => LclTransform.Scale;
        public Vector3d EulerRotation { get; }
        public Vector3d PreRotation { get; }
        public Vector3d PostRotation { get; }
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
