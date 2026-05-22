using System;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public enum UfbxElementType
    {
        Unknown,
        Node,
        Mesh,
        SkinDeformer,
        SkinCluster,
        BlendDeformer,
        BlendChannel,
        BlendShape
    }

    public enum UfbxNodeType
    {
        Unknown,
        Null,
        Mesh,
        LimbNode,
        Root
    }

}
