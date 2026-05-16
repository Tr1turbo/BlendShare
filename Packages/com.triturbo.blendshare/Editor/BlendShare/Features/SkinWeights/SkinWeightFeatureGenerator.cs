using Triturbo.BlendShare.Core;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightFeatureGenerator : MeshFeatureGenerator<SkinWeightFeatureObject>
    {
        public static readonly SkinWeightFeatureGenerator Instance = new();

        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            MeshFeatureUnityGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            return MeshFeatureGenerationResult.FailedResult("Skin weight generation is not implemented yet.");
        }
    }
}
