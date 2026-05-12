using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
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

    public sealed class MeshFeatureOptionsEditorContext
    {
        public GameObject SourceFbxAsset { get; }
        public GameObject OriginFbxAsset { get; }
        public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }

        public MeshFeatureOptionsEditorContext(
            GameObject sourceFbxAsset,
            GameObject originFbxAsset,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes)
        {
            SourceFbxAsset = sourceFbxAsset;
            OriginFbxAsset = originFbxAsset;
            Meshes = meshes ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
        }
    }
}
