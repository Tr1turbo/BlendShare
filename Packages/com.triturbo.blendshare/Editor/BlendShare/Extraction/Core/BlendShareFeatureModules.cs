using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Feature module contract for extraction, generation, and optional editor options.
    /// </summary>
    public interface IBlendShareFeatureModule
    {
        string FeatureId { get; }
        IMeshFeatureExtractor Extractor { get; }
        IMeshFeatureGenerator Generator { get; }
        IMeshFeatureExtractionOptionsProvider OptionsProvider { get; }
        bool RequiresRawFbxSdk { get; }
    }

    /// <summary>
    /// Static catalog of BlendShare feature modules.
    /// </summary>
    public static class BlendShareFeatureModules
    {
        public static readonly IReadOnlyList<IBlendShareFeatureModule> All =
            new IBlendShareFeatureModule[]
            {
                BlendShapeFeatureModule.Instance,
                SkinWeightFeatureModule.Instance
            };
    }
}

namespace Triturbo.BlendShare.Features.BlendShapes
{
    /// <summary>
    /// Feature module wiring for blendshape extraction and generation.
    /// </summary>
    public sealed class BlendShapeFeatureModule : IBlendShareFeatureModule
    {
        public static readonly BlendShapeFeatureModule Instance = new();

        public string FeatureId => BlendShapeFeatureObject.Id;
        public IMeshFeatureExtractor Extractor => BlendShapeFeatureExtractor.Instance;
        public IMeshFeatureGenerator Generator => BlendShapeFeatureGenerator.Instance;
        public IMeshFeatureExtractionOptionsProvider OptionsProvider => new BlendShapeExtractionOptionsProvider();
        public bool RequiresRawFbxSdk => false;
    }
}

namespace Triturbo.BlendShare.Features.SkinWeights
{
    /// <summary>
    /// Feature module wiring for skin-weight extraction and generation.
    /// </summary>
    public sealed class SkinWeightFeatureModule : IBlendShareFeatureModule
    {
        public static readonly SkinWeightFeatureModule Instance = new();

        public string FeatureId => SkinWeightFeatureObject.Id;
        public IMeshFeatureExtractor Extractor => SkinWeightFeatureExtractor.Instance;
        public IMeshFeatureGenerator Generator => SkinWeightFeatureGenerator.Instance;
        public IMeshFeatureExtractionOptionsProvider OptionsProvider => new SkinWeightExtractionOptionsProvider();
        public bool RequiresRawFbxSdk => false;
    }
}
