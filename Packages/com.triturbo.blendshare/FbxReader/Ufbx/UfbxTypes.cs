using System;
using Triturbo.Fbx;

namespace Triturbo.Fbx.Ufbx
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
