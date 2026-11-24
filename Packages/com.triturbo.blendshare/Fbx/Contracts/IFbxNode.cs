using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>An attribute shared by one or more scene nodes.</summary>
    public interface IFbxNodeAttribute : IFbxElement
    {
        /// <summary>Kind of node attribute reported by the reader.</summary>
        FbxNodeAttributeKind AttributeKind { get; }
        /// <summary>All scene nodes instancing this attribute.</summary>
        IReadOnlyList<IFbxNode> Instances { get; }
    }

    /// <summary>A node in the evaluated scene hierarchy.</summary>
    public interface IFbxNode : IFbxElement
    {
        /// <summary>Hierarchy path of this node.</summary>
        string Path { get; }
        /// <summary>Whether this is the scene root.</summary>
        bool IsRoot { get; }
        /// <summary>Whether the node carries a bone attribute.</summary>
        bool IsBone { get; }
        /// <summary>Kind of node attribute reported by the reader.</summary>
        FbxNodeAttributeKind AttributeKind { get; }
        /// <summary>Parent node, or null for the root.</summary>
        IFbxNode Parent { get; }
        /// <summary>Immediate child nodes in hierarchy order.</summary>
        IReadOnlyList<IFbxNode> Children { get; }
        /// <summary>The attached node attribute, or null if absent.</summary>
        IFbxNodeAttribute Attribute { get; }
        /// <summary>Mesh attached to the node, or null if absent.</summary>
        IFbxMesh Mesh { get; }
        /// <summary>Material slots for this node instance, including overrides.</summary>
        IReadOnlyList<IFbxMaterial> Materials { get; }

        // Evaluated transforms
        /// <summary>Local transform.</summary>
        FbxTransform LocalTransform { get; }
        /// <summary>Geometry transform.</summary>
        FbxTransform GeometryTransform { get; }
        /// <summary>Node to parent matrix.</summary>
        FbxMatrix4x4 NodeToParentMatrix { get; }
        /// <summary>Node to world matrix.</summary>
        FbxMatrix4x4 NodeToWorldMatrix { get; }
        /// <summary>Geometry to node matrix.</summary>
        FbxMatrix4x4 GeometryToNodeMatrix { get; }
        /// <summary>Geometry to world matrix.</summary>
        FbxMatrix4x4 GeometryToWorldMatrix { get; }

        // Raw FBX transform properties (for lossless FBX SDK write-back)
        /// <summary>Raw transform.</summary>
        FbxNodeRawTransform RawTransform { get; }
    }
}
