using System;
using System.Collections.Generic;
using System.Linq;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Base options for enabling and selecting extraction work for a feature.
    /// </summary>
    public abstract class MeshFeatureExtractionOptions
    {
        public abstract string FeatureId { get; }
        public bool Enabled = true;

        /// <summary>
        /// Checks whether this feature has work selected for the requested meshes.
        /// </summary>
        /// <param name="meshes">Mesh requests available to extraction.</param>
        /// <returns><c>true</c> when this enabled feature should extract at least one mesh.</returns>
        public virtual bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled &&
                   (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                   .Any(request => ShouldExtractMesh(request));
        }

        /// <summary>
        /// Checks whether this feature should run for one mesh request.
        /// </summary>
        /// <param name="mesh">Mesh request to inspect.</param>
        /// <returns><c>true</c> when this enabled feature should run for the mesh.</returns>
        public virtual bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            return Enabled && mesh != null;
        }
    }

    /// <summary>
    /// Type-keyed collection of feature extraction options for one extraction run.
    /// </summary>
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
