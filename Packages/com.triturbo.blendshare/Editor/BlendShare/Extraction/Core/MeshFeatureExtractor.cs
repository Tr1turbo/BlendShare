using System;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Extracts one feature object from a mesh extraction context.
    /// </summary>
    public interface IMeshFeatureExtractor
    {
        string FeatureId { get; }
        Type FeatureType { get; }
        Type OptionsType { get; }

        MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            out MeshFeatureObject feature);
    }

    /// <summary>
    /// Typed base extractor that resolves feature options before extraction.
    /// </summary>
    public abstract class MeshFeatureExtractor<TFeature, TOptions> : IMeshFeatureExtractor
        where TFeature : MeshFeatureObject
        where TOptions : MeshFeatureExtractionOptions
    {
        public abstract string FeatureId { get; }
        public Type FeatureType => typeof(TFeature);
        public Type OptionsType => typeof(TOptions);

        public MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            out MeshFeatureObject feature)
        {
            feature = null;

            if (context == null)
            {
                return MeshFeatureExtractionResult.Failed("Extraction context is null.");
            }

            if (!context.Options.TryGet<TOptions>(out var options))
            {
                return MeshFeatureExtractionResult.Skipped("Options not present.");
            }

            if (options == null || !options.Enabled)
            {
                return MeshFeatureExtractionResult.Skipped("Options disabled.");
            }

            var result = TryExtract(context, options, out TFeature typedFeature);
            feature = typedFeature;
            return result;
        }

        protected abstract MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            TOptions options,
            out TFeature feature);
    }
}
