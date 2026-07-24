using System.Collections.Generic;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Generates one feature family from all ordered contributions targeting a mesh.
    /// </summary>
    public interface IMeshFeatureGenerator
    {
        /// <summary>
        /// Gets the feature object type claimed by this generator.
        /// </summary>
        System.Type FeatureType { get; }

        /// <summary>
        /// Gets the deterministic ordering value used between independent feature families.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Checks whether the ordered feature contributions can be applied to a Unity mesh batch.
        /// </summary>
        /// <param name="context">Shared Unity mesh feature context.</param>
        /// <returns>A skipped result when the batch has no contribution for this generator.</returns>
        MeshFeatureGenerationResult CanApplyToUnityMesh(UnityMeshFeatureContext context);

        /// <summary>
        /// Applies the ordered feature contributions to a Unity mesh batch.
        /// </summary>
        /// <param name="context">Shared Unity mesh feature context.</param>
        /// <returns>The accumulated generation result for this feature family.</returns>
        MeshFeatureGenerationResult ApplyToUnityMesh(UnityMeshFeatureContext context);

        /// <summary>
        /// Finalizes session-wide Unity data after every mesh batch has run.
        /// </summary>
        /// <param name="session">Unity generation session.</param>
        /// <returns>The finalization result.</returns>
        MeshFeatureGenerationResult FinalizeUnityGeneration(UnityMeshGenerationSession session);

#if ENABLE_FBX_SDK
        /// <summary>
        /// Checks whether the ordered feature contributions can be applied to an FBX mesh batch.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <returns>A skipped result when the batch has no contribution for this generator.</returns>
        MeshFeatureGenerationResult CanApplyToFbx(FbxMeshFeatureContext context);

        /// <summary>
        /// Applies the ordered feature contributions to an FBX mesh batch.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <returns>The accumulated generation result for this feature family.</returns>
        MeshFeatureGenerationResult ApplyToFbx(FbxMeshFeatureContext context);

        /// <summary>
        /// Checks whether this FBX batch needs the original reader scene.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <returns><c>true</c> when the reader scene must be loaded.</returns>
        bool RequiresFbxReaderScene(FbxMeshFeatureContext context);

        /// <summary>
        /// Finalizes session-wide FBX data after every mesh batch has run.
        /// </summary>
        /// <param name="session">FBX generation session.</param>
        /// <returns>The finalization result.</returns>
        MeshFeatureGenerationResult FinalizeFbxGeneration(FbxGenerationSession session);
#endif
    }

    /// <summary>
    /// Typed base class for generators that process one ordered feature family per target mesh.
    /// </summary>
    /// <typeparam name="TFeature">Concrete feature object handled by the generator.</typeparam>
    public abstract class MeshFeatureGenerator<TFeature> : IMeshFeatureGenerator
        where TFeature : MeshFeatureObject
    {
        /// <inheritdoc />
        public System.Type FeatureType => typeof(TFeature);

        /// <inheritdoc />
        public virtual int Order => 0;

        /// <inheritdoc />
        public MeshFeatureGenerationResult CanApplyToUnityMesh(UnityMeshFeatureContext context)
        {
            var features = context?.GetFeatures<TFeature>() ?? System.Array.Empty<TFeature>();
            return features.Count == 0
                ? MeshFeatureGenerationResult.Skipped()
                : CanApplyToUnityMesh(context, features);
        }

        /// <inheritdoc />
        public MeshFeatureGenerationResult ApplyToUnityMesh(UnityMeshFeatureContext context)
        {
            var features = context?.GetFeatures<TFeature>() ?? System.Array.Empty<TFeature>();
            return features.Count == 0
                ? MeshFeatureGenerationResult.Skipped()
                : ApplyToUnityMesh(context, features);
        }

        /// <inheritdoc />
        public virtual MeshFeatureGenerationResult FinalizeUnityGeneration(UnityMeshGenerationSession session)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        /// <summary>
        /// Checks all ordered contributions for this feature family before Unity generation.
        /// </summary>
        /// <param name="context">Shared Unity mesh feature context.</param>
        /// <param name="features">Feature contributions in stable patch order.</param>
        /// <returns>The validation result for the feature family.</returns>
        protected virtual MeshFeatureGenerationResult CanApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<TFeature> features)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        /// <summary>
        /// Applies all ordered contributions for this feature family to the shared Unity mesh.
        /// </summary>
        /// <param name="context">Shared Unity mesh feature context.</param>
        /// <param name="features">Feature contributions in stable patch order.</param>
        /// <returns>The accumulated generation result for the feature family.</returns>
        protected abstract MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<TFeature> features);

#if ENABLE_FBX_SDK
        /// <inheritdoc />
        public MeshFeatureGenerationResult CanApplyToFbx(FbxMeshFeatureContext context)
        {
            var features = context?.GetFeatures<TFeature>() ?? System.Array.Empty<TFeature>();
            return features.Count == 0
                ? MeshFeatureGenerationResult.Skipped()
                : CanApplyToFbx(context, features);
        }

        /// <inheritdoc />
        public MeshFeatureGenerationResult ApplyToFbx(FbxMeshFeatureContext context)
        {
            var features = context?.GetFeatures<TFeature>() ?? System.Array.Empty<TFeature>();
            return features.Count == 0
                ? MeshFeatureGenerationResult.Skipped()
                : ApplyToFbx(context, features);
        }

        /// <inheritdoc />
        public bool RequiresFbxReaderScene(FbxMeshFeatureContext context)
        {
            var features = context?.GetFeatures<TFeature>() ?? System.Array.Empty<TFeature>();
            return features.Count > 0 && RequiresFbxReaderScene(context, features);
        }

        /// <inheritdoc />
        public virtual MeshFeatureGenerationResult FinalizeFbxGeneration(FbxGenerationSession session)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        /// <summary>
        /// Checks all ordered contributions for this feature family before FBX generation.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <param name="features">Feature contributions in stable patch order.</param>
        /// <returns>The validation result for the feature family.</returns>
        protected virtual MeshFeatureGenerationResult CanApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<TFeature> features)
        {
            return MeshFeatureGenerationResult.FailedResult(
                $"Feature '{typeof(TFeature).Name}' does not support FBX generation.");
        }

        /// <summary>
        /// Applies all ordered contributions for this feature family to the shared FBX mesh.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <param name="features">Feature contributions in stable patch order.</param>
        /// <returns>The accumulated generation result for the feature family.</returns>
        protected virtual MeshFeatureGenerationResult ApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<TFeature> features)
        {
            return MeshFeatureGenerationResult.FailedResult(
                $"Feature '{typeof(TFeature).Name}' does not support FBX generation.");
        }

        /// <summary>
        /// Checks whether the ordered contributions need the original FBX reader scene.
        /// </summary>
        /// <param name="context">Shared FBX mesh feature context.</param>
        /// <param name="features">Feature contributions in stable patch order.</param>
        /// <returns><c>true</c> when the reader scene must be loaded.</returns>
        protected virtual bool RequiresFbxReaderScene(
            FbxMeshFeatureContext context,
            IReadOnlyList<TFeature> features)
        {
            return false;
        }
#endif
    }
}
