using Triturbo.BlendShare.Core;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public sealed class SkinWeightFeatureObject : MeshFeatureObject
    {
        public const string Id = "skin-weights";

        public override string FeatureId => Id;
    }
}
