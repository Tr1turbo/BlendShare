
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
}
