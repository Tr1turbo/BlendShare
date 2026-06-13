using System;
using Triturbo.BlendShare.Core;

namespace Triturbo.BlendShare.Inspector
{
    public sealed class MeshFeatureEditorContext
    {
        public MeshFeatureEditorContext(
            MeshFeatureObject feature,
            MeshDataObject ownerMesh,
            BlendShareObject ownerPatch,
            Action refresh)
        {
            Feature = feature;
            OwnerMesh = ownerMesh;
            OwnerPatch = ownerPatch;
            Refresh = refresh;
        }

        public MeshFeatureObject Feature { get; }
        public MeshDataObject OwnerMesh { get; }
        public BlendShareObject OwnerPatch { get; }
        public Action Refresh { get; }
    }
}
