namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Pass-style generator that applies one feature family to a Unity mesh or FBX mesh target.
    /// </summary>
    public interface IMeshFeatureGenerator
    {
        /// <summary>
        /// Ordering value used when multiple feature generators apply to the same mesh.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Checks whether this feature can be applied to the current Unity mesh target.
        /// </summary>
        /// <param name="context">Unity mesh generation context for the current mesh.</param>
        /// <returns>A skipped result when this generator has no feature data in the context.</returns>
        MeshFeatureGenerationResult CanApplyToUnityMesh(UnityMeshGenerationContext context);

        /// <summary>
        /// Applies this feature to the current Unity mesh target.
        /// </summary>
        /// <param name="context">Unity mesh generation context for the current mesh.</param>
        /// <returns>Generation result for this feature.</returns>
        MeshFeatureGenerationResult ApplyToUnityMesh(UnityMeshGenerationContext context);

#if ENABLE_FBX_SDK
        /// <summary>
        /// Checks whether this feature can be applied to the current FBX mesh target.
        /// </summary>
        /// <param name="context">FBX generation context for the current mesh.</param>
        /// <returns>A skipped result when this generator has no feature data in the context.</returns>
        MeshFeatureGenerationResult CanApplyToFbx(FbxGenerationContext context);

        /// <summary>
        /// Applies this feature to the current FBX mesh node.
        /// </summary>
        /// <param name="context">FBX generation context for the current mesh.</param>
        /// <returns>Generation result for this feature.</returns>
        MeshFeatureGenerationResult ApplyToFbx(FbxGenerationContext context);

        /// <summary>
        /// Removes this feature from the current FBX mesh node.
        /// </summary>
        /// <param name="context">FBX generation context for the current mesh.</param>
        /// <returns>Generation result for this feature.</returns>
        MeshFeatureGenerationResult RemoveFromFbx(FbxGenerationContext context);
#endif
    }

    /// <summary>
    /// Typed base class for feature generator passes that claim a specific feature object from the context.
    /// </summary>
    /// <typeparam name="TFeature">Concrete feature object handled by the generator.</typeparam>
    public abstract class MeshFeatureGenerator<TFeature> : IMeshFeatureGenerator
        where TFeature : MeshFeatureObject
    {
        /// <inheritdoc />
        public virtual int Order => 0;

        /// <inheritdoc />
        public MeshFeatureGenerationResult CanApplyToUnityMesh(UnityMeshGenerationContext context)
        {
            if (!TryGetFeature(context, out TFeature feature, out var result))
            {
                return result;
            }

            return CanApplyToUnityMesh(context, feature);
        }

        /// <inheritdoc />
        public MeshFeatureGenerationResult ApplyToUnityMesh(UnityMeshGenerationContext context)
        {
            if (!TryGetFeature(context, out TFeature feature, out var result))
            {
                return result;
            }

            return ApplyToUnityMesh(context, feature);
        }

        protected virtual MeshFeatureGenerationResult CanApplyToUnityMesh(
            UnityMeshGenerationContext context,
            TFeature feature)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        protected abstract MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshGenerationContext context,
            TFeature feature);

#if ENABLE_FBX_SDK
        /// <inheritdoc />
        public MeshFeatureGenerationResult CanApplyToFbx(FbxGenerationContext context)
        {
            if (!TryGetFeature(context, out TFeature feature, out var result))
            {
                return result;
            }

            return CanApplyToFbx(context, feature);
        }

        /// <inheritdoc />
        public MeshFeatureGenerationResult ApplyToFbx(FbxGenerationContext context)
        {
            if (!TryGetFeature(context, out TFeature feature, out var result))
            {
                return result;
            }

            return ApplyToFbx(context, feature);
        }

        /// <inheritdoc />
        public MeshFeatureGenerationResult RemoveFromFbx(FbxGenerationContext context)
        {
            if (!TryGetFeature(context, out TFeature feature, out var result))
            {
                return result;
            }

            return RemoveFromFbx(context, feature);
        }

        protected virtual MeshFeatureGenerationResult CanApplyToFbx(
            FbxGenerationContext context,
            TFeature feature)
        {
            return MeshFeatureGenerationResult.FailedResult($"Feature '{typeof(TFeature).Name}' does not support FBX generation.");
        }

        protected virtual MeshFeatureGenerationResult ApplyToFbx(
            FbxGenerationContext context,
            TFeature feature)
        {
            return MeshFeatureGenerationResult.FailedResult($"Feature '{typeof(TFeature).Name}' does not support FBX generation.");
        }

        protected virtual MeshFeatureGenerationResult RemoveFromFbx(
            FbxGenerationContext context,
            TFeature feature)
        {
            return MeshFeatureGenerationResult.FailedResult($"Feature '{typeof(TFeature).Name}' does not support FBX removal.");
        }
#endif

        private bool TryGetFeature(
            UnityMeshGenerationContext context,
            out TFeature feature,
            out MeshFeatureGenerationResult result)
        {
            feature = null;
            result = default;

            if (context == null)
            {
                result = MeshFeatureGenerationResult.FailedResult("Generation context is null.");
                return false;
            }

            feature = context.GetFeature<TFeature>();
            if (feature == null)
            {
                result = MeshFeatureGenerationResult.Skipped();
                return false;
            }

            return true;
        }

#if ENABLE_FBX_SDK
        private bool TryGetFeature(
            FbxGenerationContext context,
            out TFeature feature,
            out MeshFeatureGenerationResult result)
        {
            feature = null;
            result = default;

            if (context == null)
            {
                result = MeshFeatureGenerationResult.FailedResult("Generation context is null.");
                return false;
            }

            feature = context.GetFeature<TFeature>();
            if (feature == null)
            {
                result = MeshFeatureGenerationResult.Skipped();
                return false;
            }

            return true;
        }
#endif
    }
}
