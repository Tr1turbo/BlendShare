using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Provides default options and editor UI for a feature extractor.
    /// </summary>
    public interface IMeshFeatureExtractionOptionsProvider
    {
        string FeatureId { get; }
        string TabLabel { get; }
        Type OptionsType { get; }
        int DisplayOrder { get; }

        MeshFeatureExtractionOptions CreateDefaultOptions();

        void DrawOptionsGUI(
            MeshFeatureExtractionOptions options,
            MeshFeatureOptionsEditorContext context);
    }

    /// <summary>
    /// Optional editor hook for building copied feature inspection data from a short-lived FBX session.
    /// </summary>
    public interface IMeshFeatureInspectionProvider
    {
        object BuildInspectionData(
            FbxInspectionSession session,
            MeshFeatureExtractionOptionsSet options,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes);
    }

    /// <summary>
    /// Optional UI Toolkit renderer for feature options. Providers without this render through IMGUI fallback.
    /// </summary>
    public interface IUIToolkitMeshFeatureOptionsProvider
    {
        VisualElement CreateOptionsElement(
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
        public virtual string TabLabel => FeatureId;
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
        public IReadOnlyDictionary<string, object> CachedData { get; }
        public float AvailableHeight { get; }
        public Action RequestInspectionRefresh { get; }

        public MeshFeatureOptionsEditorContext(
            GameObject sourceFbxGo,
            GameObject originFbxGo,
            IReadOnlyList<MeshFeatureExtractionMeshRequest> meshes,
            IReadOnlyDictionary<string, object> cachedData = null,
            float availableHeight = 0f,
            Action requestInspectionRefresh = null)
        {
            SourceFbxGo = sourceFbxGo;
            OriginFbxGo = originFbxGo;
            Meshes = meshes ?? Array.Empty<MeshFeatureExtractionMeshRequest>();
            CachedData = cachedData ?? new Dictionary<string, object>();
            AvailableHeight = availableHeight;
            RequestInspectionRefresh = requestInspectionRefresh;
        }

        public bool TryGetCachedData<TValue>(string key, out TValue value)
        {
            if (!string.IsNullOrEmpty(key) &&
                CachedData.TryGetValue(key, out var raw) &&
                raw is TValue typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }
    }
}
