using System;
using System.Collections.Generic;

namespace Triturbo.BlendShapeShare.Extractor
{
    public abstract class MeshFeatureExtractionOptions
    {
        public abstract string FeatureId { get; }
        public bool Enabled = true;
    }

    public sealed class MeshFeatureExtractionOptionsSet
    {
        private readonly Dictionary<Type, MeshFeatureExtractionOptions> optionsByType = new();

        public IEnumerable<MeshFeatureExtractionOptions> All => optionsByType.Values;

        public void Set<TOptions>(TOptions options)
            where TOptions : MeshFeatureExtractionOptions
        {
            Set(typeof(TOptions), options);
        }

        public void Set(Type optionsType, MeshFeatureExtractionOptions options)
        {
            if (optionsType == null)
            {
                return;
            }

            if (options == null)
            {
                optionsByType.Remove(optionsType);
                return;
            }

            if (!optionsType.IsInstanceOfType(options))
            {
                throw new ArgumentException(
                    $"Options instance '{options.GetType().FullName}' is not assignable to '{optionsType.FullName}'.",
                    nameof(options));
            }

            optionsByType[optionsType] = options;
        }

        public bool TryGet<TOptions>(out TOptions options)
            where TOptions : MeshFeatureExtractionOptions
        {
            if (optionsByType.TryGetValue(typeof(TOptions), out var raw))
            {
                options = raw as TOptions;
                return options != null;
            }

            options = null;
            return false;
        }

        public bool TryGet(Type optionsType, out MeshFeatureExtractionOptions options)
        {
            if (optionsType == null)
            {
                options = null;
                return false;
            }

            return optionsByType.TryGetValue(optionsType, out options);
        }
    }
}
