using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Runtime registry of hardcoded feature extractors.
    /// </summary>
    [InitializeOnLoad]
    public static class MeshFeatureExtractorRegistry
    {
        private static IReadOnlyList<IMeshFeatureExtractor> extractors;

        public static IReadOnlyList<IMeshFeatureExtractor> Extractors => extractors ??= BuildExtractors();

        static MeshFeatureExtractorRegistry()
        {
            Reload();
        }

        public static void Reload()
        {
            extractors = BuildExtractors();
        }

        public static IMeshFeatureExtractor GetByFeatureId(string featureId)
        {
            return Extractors.FirstOrDefault(extractor => extractor.FeatureId == featureId);
        }

        public static IMeshFeatureExtractor GetByOptionsType(Type optionsType)
        {
            return Extractors.FirstOrDefault(extractor => extractor.OptionsType == optionsType);
        }

        private static IReadOnlyList<IMeshFeatureExtractor> BuildExtractors()
        {
            return BlendShareFeatureModules.All
                .Select(module => module?.Extractor)
                .Where(extractor => extractor != null)
                .ToArray();
        }
    }

    /// <summary>
    /// Runtime registry of feature options UI providers.
    /// </summary>
    [InitializeOnLoad]
    public static class MeshFeatureExtractionOptionsProviderRegistry
    {
        private static IReadOnlyList<IMeshFeatureExtractionOptionsProvider> providers;

        public static IReadOnlyList<IMeshFeatureExtractionOptionsProvider> Providers => providers ??= BuildProviders();

        static MeshFeatureExtractionOptionsProviderRegistry()
        {
            Reload();
        }

        public static void Reload()
        {
            providers = BuildProviders();
        }

        private static IReadOnlyList<IMeshFeatureExtractionOptionsProvider> BuildProviders()
        {
            return BlendShareFeatureModules.All
                .Select(module => module?.OptionsProvider)
                .Where(provider => provider != null)
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.GetType().FullName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
