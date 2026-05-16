using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightExtractionOptions : MeshFeatureExtractionOptions
    {
        public override string FeatureId => SkinWeightFeatureObject.Id;

        public SkinWeightExtractionOptions()
        {
            Enabled = false;
        }
    }

    public sealed class SkinWeightFeatureExtractor
        : MeshFeatureExtractor<SkinWeightFeatureObject, SkinWeightExtractionOptions>
    {
        public static readonly SkinWeightFeatureExtractor Instance = new();

        public override string FeatureId => SkinWeightFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            SkinWeightExtractionOptions options,
            out SkinWeightFeatureObject feature)
        {
            feature = null;
            return MeshFeatureExtractionResult.Skipped("Skin weight extraction is not implemented yet.");
        }
    }
}
