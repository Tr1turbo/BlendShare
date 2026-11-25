using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Pipeline.Extraction;
using Triturbo.BlendShare.Pipeline.Inspection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Extraction
{
    /// <summary>
    /// Provides default options and editor UI for a BlendShare 2.0 feature extractor.
    /// </summary>
    public interface IMeshFeatureExtractionOptionsProvider
    {
        string FeatureTypeId { get; }
        string TabLabel { get; }
        Type OptionsType { get; }
        int DisplayOrder { get; }

        IFeatureExtractionOptions CreateDefaultOptions();

        bool HasSelectedWork(IFeatureExtractionOptions options, IReadOnlyList<string> meshPaths);

        bool ShouldExtractMesh(IFeatureExtractionOptions options, string meshPath);

        void ResetUiState();

        void DrawOptionsGUI(
            IFeatureExtractionOptions options,
            MeshFeatureOptionsEditorContext context);
    }

    /// <summary>
    /// Optional editor hook for building copied feature inspection data from a short-lived FBX session.
    /// </summary>
    public interface IMeshFeatureInspectionProvider
    {
        object BuildInspectionData(
            FbxInspectionSession session,
            ExtractionOptionsSet options,
            SourceOffsetTable sourceOffsets,
            IReadOnlyList<string> meshPaths);
    }

    /// <summary>
    /// Optional UI Toolkit renderer for feature options. Providers without this render through IMGUI fallback.
    /// </summary>
    public interface IUIToolkitMeshFeatureOptionsProvider
    {
        VisualElement CreateOptionsElement(
            IFeatureExtractionOptions options,
            MeshFeatureOptionsEditorContext context);
    }

    /// <summary>
    /// Assembly-level attribute for third-party packages registering custom Patch Creator options providers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareExtractionOptionsProviderAttribute : Attribute
    {
        public Type ProviderType { get; }

        public RegisterBlendShareExtractionOptionsProviderAttribute(Type providerType)
        {
            ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));
        }
    }

    /// <summary>
    /// Typed base options provider for feature-specific extraction settings.
    /// </summary>
    public abstract class MeshFeatureExtractionOptionsProvider<TOptions> : IMeshFeatureExtractionOptionsProvider
        where TOptions : class, IFeatureExtractionOptions
    {
        public abstract string FeatureTypeId { get; }
        public virtual string TabLabel => FeatureTypeId;
        public Type OptionsType => typeof(TOptions);
        public virtual int DisplayOrder => 0;

        public IFeatureExtractionOptions CreateDefaultOptions()
        {
            return CreateDefault();
        }

        public virtual bool HasSelectedWork(IFeatureExtractionOptions options, IReadOnlyList<string> meshPaths)
        {
            if (options == null || !options.Enabled || meshPaths == null || meshPaths.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < meshPaths.Count; i++)
            {
                if (ShouldExtractMesh(options, meshPaths[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public virtual bool ShouldExtractMesh(IFeatureExtractionOptions options, string meshPath)
        {
            if (options == null || !options.Enabled || string.IsNullOrWhiteSpace(meshPath))
            {
                return false;
            }

            return options.ShouldExtractMesh(FbxNameUtility.NormalizePath(meshPath));
        }

        public virtual void ResetUiState()
        {
        }

        public void DrawOptionsGUI(
            IFeatureExtractionOptions options,
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
        public FbxInspectionSession InspectionSession { get; }
        public IReadOnlyList<string> MeshPaths { get; }
        public IReadOnlyDictionary<string, object> CachedData { get; }
        public float AvailableHeight { get; }
        public Action RequestInspectionRefresh { get; }

        public MeshFeatureOptionsEditorContext(
            GameObject sourceFbxGo,
            GameObject originFbxGo,
            FbxInspectionSession inspectionSession,
            IReadOnlyList<string> meshPaths,
            IReadOnlyDictionary<string, object> cachedData = null,
            float availableHeight = 0f,
            Action requestInspectionRefresh = null)
        {
            SourceFbxGo = sourceFbxGo;
            OriginFbxGo = originFbxGo;
            InspectionSession = inspectionSession;
            MeshPaths = meshPaths ?? Array.Empty<string>();
            CachedData = cachedData ?? new Dictionary<string, object>(StringComparer.Ordinal);
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

    /// <summary>
    /// Deterministic registry of Patch Creator feature options providers.
    /// Creates fresh per-window provider instances so foldout/search UI state stays isolated per window.
    /// </summary>
    public static class FeatureExtractionOptionsProviderRegistry
    {
        private static readonly object s_Lock = new object();
        private static readonly List<Type> s_ProviderTypes = new List<Type>();
        private static bool s_Discovered;

        public static void Register(Type providerType)
        {
            if (providerType == null ||
                providerType.IsAbstract ||
                !typeof(IMeshFeatureExtractionOptionsProvider).IsAssignableFrom(providerType))
            {
                throw new ArgumentException("Provider type must be a concrete IMeshFeatureExtractionOptionsProvider.", nameof(providerType));
            }

            lock (s_Lock)
            {
                EnsureDiscoveredLocked();
                if (!s_ProviderTypes.Contains(providerType))
                {
                    s_ProviderTypes.Add(providerType);
                }
            }
        }

        public static bool Unregister(Type providerType)
        {
            if (providerType == null)
            {
                return false;
            }

            lock (s_Lock)
            {
                EnsureDiscoveredLocked();
                return s_ProviderTypes.Remove(providerType);
            }
        }

        public static IMeshFeatureExtractionOptionsProvider[] CreateProviders()
        {
            Type[] snapshot;
            lock (s_Lock)
            {
                EnsureDiscoveredLocked();
                snapshot = s_ProviderTypes.ToArray();
            }

            var providers = new List<IMeshFeatureExtractionOptionsProvider>(snapshot.Length);
            var seenFeatureIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    if (Activator.CreateInstance(snapshot[i]) is IMeshFeatureExtractionOptionsProvider instance &&
                        !string.IsNullOrWhiteSpace(instance.FeatureTypeId) &&
                        seenFeatureIds.Add(instance.FeatureTypeId))
                    {
                        providers.Add(instance);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to create extraction options provider '{snapshot[i].FullName}': {ex.Message}");
                }
            }

            return providers
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.GetType().FullName, StringComparer.Ordinal)
                .ToArray();
        }

        private static void EnsureDiscoveredLocked()
        {
            if (s_Discovered)
            {
                return;
            }

            s_Discovered = true;
            s_ProviderTypes.Add(typeof(BlendShapeExtractionOptionsProvider));
            s_ProviderTypes.Add(typeof(SkinWeightExtractionOptionsProvider));

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareExtractionOptionsProviderAttribute[] attributes;
                try
                {
                    attributes = assembly.GetCustomAttributes<RegisterBlendShareExtractionOptionsProviderAttribute>().ToArray();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(ex.Message);
                    continue;
                }

                foreach (var attribute in attributes.OrderBy(a => a.ProviderType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.ProviderType;
                    if (type == null ||
                        type.IsAbstract ||
                        !typeof(IMeshFeatureExtractionOptionsProvider).IsAssignableFrom(type) ||
                        s_ProviderTypes.Contains(type))
                    {
                        continue;
                    }

                    s_ProviderTypes.Add(type);
                }
            }
        }
    }
}
