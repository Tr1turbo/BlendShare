using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Extraction;
using UnityEditor;

namespace Triturbo.BlendShare.UI.Extraction
{
    public sealed class FeatureExtractionOptionsContext
    {
        public IReadOnlyList<string> MeshPaths { get; set; } = Array.Empty<string>();
        public string ActiveMeshPath { get; set; }
        public IFbxScene SourceScene { get; set; }
        public IFbxScene OriginScene { get; set; }
        public PayloadEncodeOptions EncodeOptions { get; set; } = PayloadEncodeOptions.Default;
        public Action<PayloadEncodeOptions> SetEncodeOptions { get; set; }
        public Action NotifyChanged { get; set; }
    }

    public interface IFeatureExtractionOptionsDrawer
    {
        string FeatureTypeId { get; }
        int DisplayOrder { get; }
        string DisplayName { get; }
        void DrawOptions(IFeatureExtractionOptions options, FeatureExtractionOptionsContext context);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareExtractionOptionsDrawerAttribute : Attribute
    {
        public Type DrawerType { get; }
        public RegisterBlendShareExtractionOptionsDrawerAttribute(Type drawerType) => DrawerType = drawerType;
    }

    public sealed class DefaultExtractionOptionsDrawer : IFeatureExtractionOptionsDrawer
    {
        public string FeatureTypeId => "*";
        public int DisplayOrder => int.MaxValue;
        public string DisplayName => "Feature";
        public void DrawOptions(IFeatureExtractionOptions options, FeatureExtractionOptionsContext context)
        {
            if (options == null) return;
            EditorGUILayout.LabelField("Feature type", options.FeatureTypeId);
            bool enabled = EditorGUILayout.Toggle("Enabled", options.Enabled);
            if (enabled == options.Enabled) return;
            options.Enabled = enabled;
            context?.NotifyChanged?.Invoke();
        }
    }

    public static class FeatureExtractionUiRegistry
    {
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<string, IFeatureExtractionOptionsDrawer> s_Drawers =
            new Dictionary<string, IFeatureExtractionOptionsDrawer>(StringComparer.Ordinal);
        private static readonly DefaultExtractionOptionsDrawer s_Default = new DefaultExtractionOptionsDrawer();
        private static bool s_Discovered;

        private static void Discover()
        {
            if (s_Discovered) return;
            s_Discovered = true;
            Register(new BlendShapeExtractionOptionsDrawer());
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareExtractionOptionsDrawerAttribute[] attributes;
                try { attributes = assembly.GetCustomAttributes<RegisterBlendShareExtractionOptionsDrawerAttribute>().ToArray(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning(ex.Message); continue; }
                foreach (var attribute in attributes.OrderBy(a => a.DrawerType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.DrawerType;
                    if (type == null || type.IsAbstract || !typeof(IFeatureExtractionOptionsDrawer).IsAssignableFrom(type)) continue;
                    try { Register((IFeatureExtractionOptionsDrawer)Activator.CreateInstance(type)); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning(ex.Message); }
                }
            }
        }

        public static void Register(IFeatureExtractionOptionsDrawer drawer)
        {
            if (string.IsNullOrWhiteSpace(drawer?.FeatureTypeId)) throw new ArgumentException(nameof(drawer));
            lock (s_Lock)
            {
                Discover();
                if (s_Drawers.ContainsKey(drawer.FeatureTypeId))
                    throw new InvalidOperationException("Duplicate drawer for " + drawer.FeatureTypeId);
                s_Drawers.Add(drawer.FeatureTypeId, drawer);
            }
        }

        public static bool Unregister(string featureTypeId)
        {
            lock (s_Lock)
            {
                Discover();
                return s_Drawers.Remove(featureTypeId ?? "");
            }
        }

        public static bool TryGet(string featureTypeId, out IFeatureExtractionOptionsDrawer drawer)
        {
            lock (s_Lock)
            {
                Discover();
                return s_Drawers.TryGetValue(featureTypeId ?? "", out drawer);
            }
        }

        public static IFeatureExtractionOptionsDrawer Resolve(string featureTypeId) =>
            TryGet(featureTypeId, out var drawer) ? drawer : s_Default;

        public static IReadOnlyList<IFeatureExtractionOptionsDrawer> GetAllOrdered()
        {
            lock (s_Lock)
            {
                Discover();
                return s_Drawers.Values.OrderBy(d => d.DisplayOrder)
                    .ThenBy(d => d.FeatureTypeId, StringComparer.Ordinal).ToArray();
            }
        }
    }
}
