using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
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
            var instances = new List<IMeshFeatureExtractor>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IMeshFeatureExtractor>()
                         .Where(IsConcrete)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogError($"[BlendShare] Mesh feature extractor '{type.FullName}' must have a public parameterless constructor.");
                    continue;
                }

                try
                {
                    var extractor = (IMeshFeatureExtractor)Activator.CreateInstance(type);
                    if (ValidateExtractor(extractor, type))
                    {
                        instances.Add(extractor);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[BlendShare] Failed to create mesh feature extractor '{type.FullName}': {exception.Message}");
                }
            }

            ReportDuplicates(instances, extractor => extractor.FeatureId, "FeatureId", extractor => extractor.GetType().FullName);
            ReportDuplicates(instances, extractor => extractor.OptionsType, "OptionsType", extractor => extractor.GetType().FullName);
            return instances;
        }

        private static bool ValidateExtractor(IMeshFeatureExtractor extractor, Type sourceType)
        {
            if (extractor == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(extractor.FeatureId))
            {
                Debug.LogError($"[BlendShare] Mesh feature extractor '{sourceType.FullName}' has an empty FeatureId.");
                return false;
            }

            if (extractor.FeatureType == null || !typeof(MeshFeatureObject).IsAssignableFrom(extractor.FeatureType))
            {
                Debug.LogError($"[BlendShare] Mesh feature extractor '{sourceType.FullName}' has an invalid FeatureType.");
                return false;
            }

            if (extractor.OptionsType == null || !typeof(MeshFeatureExtractionOptions).IsAssignableFrom(extractor.OptionsType))
            {
                Debug.LogError($"[BlendShare] Mesh feature extractor '{sourceType.FullName}' has an invalid OptionsType.");
                return false;
            }

            return true;
        }

        private static bool IsConcrete(Type type)
        {
            return type != null && !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition;
        }

        private static void ReportDuplicates<TItem, TKey>(
            IEnumerable<TItem> items,
            Func<TItem, TKey> keySelector,
            string label,
            Func<TItem, string> nameSelector)
        {
            foreach (var group in items.GroupBy(keySelector).Where(group => group.Key != null && group.Count() > 1))
            {
                string names = string.Join(", ", group.Select(nameSelector));
                Debug.LogError($"[BlendShare] Duplicate mesh feature extractor {label} '{group.Key}': {names}");
            }
        }
    }

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
            var instances = new List<IMeshFeatureExtractionOptionsProvider>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IMeshFeatureExtractionOptionsProvider>()
                         .Where(type => type != null && !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogError($"[BlendShare] Mesh feature options provider '{type.FullName}' must have a public parameterless constructor.");
                    continue;
                }

                try
                {
                    var provider = (IMeshFeatureExtractionOptionsProvider)Activator.CreateInstance(type);
                    if (ValidateProvider(provider, type))
                    {
                        instances.Add(provider);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[BlendShare] Failed to create mesh feature options provider '{type.FullName}': {exception.Message}");
                }
            }

            ReportDuplicates(instances, provider => provider.FeatureId, "FeatureId", provider => provider.GetType().FullName);
            ReportDuplicates(instances, provider => provider.OptionsType, "OptionsType", provider => provider.GetType().FullName);
            return instances.OrderBy(provider => provider.DisplayOrder).ThenBy(provider => provider.GetType().FullName, StringComparer.Ordinal).ToArray();
        }

        private static bool ValidateProvider(IMeshFeatureExtractionOptionsProvider provider, Type sourceType)
        {
            if (provider == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(provider.FeatureId))
            {
                Debug.LogError($"[BlendShare] Mesh feature options provider '{sourceType.FullName}' has an empty FeatureId.");
                return false;
            }

            if (provider.OptionsType == null || !typeof(MeshFeatureExtractionOptions).IsAssignableFrom(provider.OptionsType))
            {
                Debug.LogError($"[BlendShare] Mesh feature options provider '{sourceType.FullName}' has an invalid OptionsType.");
                return false;
            }

            return true;
        }

        private static void ReportDuplicates<TItem, TKey>(
            IEnumerable<TItem> items,
            Func<TItem, TKey> keySelector,
            string label,
            Func<TItem, string> nameSelector)
        {
            foreach (var group in items.GroupBy(keySelector).Where(group => group.Key != null && group.Count() > 1))
            {
                string names = string.Join(", ", group.Select(nameSelector));
                Debug.LogError($"[BlendShare] Duplicate mesh feature options provider {label} '{group.Key}': {names}");
            }
        }
    }
}
