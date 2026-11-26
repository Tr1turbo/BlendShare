
namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Element categories exposed by the reader.</summary>
    public enum FbxElementType
    {
        /// <summary>Unknown.</summary>
        Unknown,
        /// <summary>Node.</summary>
        Node,
        /// <summary>Mesh.</summary>
        Mesh,
        /// <summary>Material.</summary>
        Material,
        /// <summary>Skin deformer.</summary>
        SkinDeformer,
        /// <summary>Skin cluster.</summary>
        SkinCluster,
        /// <summary>Blend deformer.</summary>
        BlendDeformer,
        /// <summary>Blend channel.</summary>
        BlendChannel,
        /// <summary>Blend shape.</summary>
        BlendShape
    }

    /// <summary>Identifies the native bridge attribute attached to a node.</summary>
    public enum FbxNodeAttributeKind
    {
        /// <summary>None.</summary>
        None = 0,
        /// <summary>Mesh.</summary>
        Mesh = 1,
        /// <summary>Bone.</summary>
        Bone = 2,
        /// <summary>Empty.</summary>
        Empty = 3,
        /// <summary>Camera.</summary>
        Camera = 4,
        /// <summary>Light.</summary>
        Light = 5,
        /// <summary>Other.</summary>
        Other = 6
    }

    /// <summary>FBX skin cluster weight accumulation/normalization mode (<c>FbxCluster::ELinkMode</c>).</summary>
    public enum FbxClusterLinkMode
    {
        /// <summary>Weights on each control point are normalized to sum to 1.0 (<c>eNormalize</c>).</summary>
        Normalize = 0,
        /// <summary>Additive cluster deformation (<c>eAdditive</c>).</summary>
        Additive = 1,
        /// <summary>Partial weights sum to at most 1.0; remaining <c>1 - sum</c> stays at bind pose (<c>eTotalOne</c> / <c>"Total1"</c>).</summary>
        TotalOne = 2,
        /// <summary>Unrecognized cluster link mode string in the source file.</summary>
        Unknown = 3
    }
}
