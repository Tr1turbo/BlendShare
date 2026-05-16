using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Provides default options and editor UI for a feature extractor.
    /// </summary>
    public interface IMeshFeatureExtractionOptionsProvider
    {
        string FeatureId { get; }
        Type OptionsType { get; }
        int DisplayOrder { get; }

        MeshFeatureExtractionOptions CreateDefaultOptions();

        void DrawOptionsGUI(
            MeshFeatureExtractionOptions options,
            MeshFeatureOptionsEditorContext context);
    }

    /// <summary>
    /// Typed base options provider for feature-specific extraction settings.
    /// </summary>
    public abstract class MeshFeatureExtractionOptionsProvider<TOptions> : IMeshFeatureExtractionOptionsProvider
        where TOptions : MeshFeatureExtractionOptions
    {
        public abstract string FeatureId { get; }
        public Type OptionsType => typeof(TOptions);
        public virtual int DisplayOrder => 0;

        public MeshFeatureExtractionOptions CreateDefaultOptions()
        {
            return CreateDefault();
        }

        public void DrawOptionsGUI(
            MeshFeatureExtractionOptions options,
            MeshFeatureOptionsEditorContext context)
        {
            if (options is TOptions typedOptions)
            {
                DrawOptionsGUI(typedOptions, context);
            }
        }

        protected abstract TOptions CreateDefault();

        protected abstract void DrawOptionsGUI(
            TOptions options,
            MeshFeatureOptionsEditorContext context);
    }

    /// <summary>
    /// Editor-only context passed to feature options UI providers.
    /// </summary>
    public sealed class MeshFeatureOptionsEditorContext
    {
        public GameObject SourceFbxGo { get; }
        public GameObject OriginFbxGo { get; }
        public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }

        public MeshFeatureOptionsEditorContext(
            GameObject sourceFbxGo,
            GameObject originFbxGo,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            SourceFbxGo = sourceFbxGo;
            OriginFbxGo = originFbxGo;
            Meshes = meshes ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
        }
    }
}
