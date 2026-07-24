using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;

namespace Triturbo.BlendShare.Components
{
    /// <summary>
    /// Provides one avatar-wide effective proxy binding selection for an armature object.
    /// </summary>
    public sealed class BlendShareArmatureBoneProxyResolution
    {
        private readonly Dictionary<string, BlendShareResolvedBone> bindingsByPath;
        private readonly List<BlendShareBoneProxy> selectedProxies;
        private readonly HashSet<BlendShareBoneProxy> selectedProxySet;
        private readonly List<string> diagnostics;

        internal BlendShareArmatureBoneProxyResolution(
            FbxArmatureObject armature,
            Dictionary<string, BlendShareResolvedBone> bindingsByPath,
            IEnumerable<BlendShareBoneProxy> selectedProxies,
            IEnumerable<string> diagnostics)
        {
            Armature = armature;
            this.bindingsByPath = bindingsByPath ??
                                  new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
            this.selectedProxies = (selectedProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null)
                .Distinct()
                .ToList();
            selectedProxySet = new HashSet<BlendShareBoneProxy>(this.selectedProxies);
            this.diagnostics = (diagnostics ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        /// <summary>Gets the armature object whose proxy bindings were resolved.</summary>
        public FbxArmatureObject Armature { get; }

        /// <summary>Gets whether the proxy bindings could be resolved.</summary>
        public bool Success => diagnostics.Count == 0;

        /// <summary>Gets the effective proxy binding selected for each normalized source bone path.</summary>
        public IReadOnlyDictionary<string, BlendShareResolvedBone> BindingsByPath => bindingsByPath;

        /// <summary>Gets proxy components that own at least one effective binding.</summary>
        public IReadOnlyList<BlendShareBoneProxy> SelectedProxies => selectedProxies;

        /// <summary>Gets diagnostics that prevented resolution.</summary>
        public IReadOnlyList<string> Diagnostics => diagnostics;

        /// <summary>Gets the effective proxy binding for a source bone path.</summary>
        public bool TryGetBinding(string sourceBonePath, out BlendShareResolvedBone resolved)
        {
            resolved = default;
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   bindingsByPath.TryGetValue(normalizedPath, out resolved) &&
                   resolved.IsProxy;
        }

        /// <summary>Gets whether the supplied proxy component participates in this resolution.</summary>
        public bool IsSelected(BlendShareBoneProxy proxy)
        {
            return proxy != null && selectedProxySet.Contains(proxy);
        }

        /// <summary>Gets whether the supplied binding is the effective binding for its source path.</summary>
        public bool IsEffective(BlendShareBoneProxy proxy, BlendShareBoneProxyBinding binding)
        {
            return proxy != null &&
                   binding?.IsConfigured == true &&
                   TryGetBinding(binding.SourceBonePath, out var resolved) &&
                   resolved.Proxy == proxy &&
                   ReferenceEquals(resolved.Binding, binding);
        }
    }

    /// <summary>
    /// Resolves one stable per-path proxy binding selection for a source armature.
    /// </summary>
    public static class BlendShareBoneProxyResolver
    {
        /// <summary>
        /// Resolves active proxy components by exact armature identity and normalized source bone path.
        /// </summary>
        public static BlendShareArmatureBoneProxyResolution Resolve(
            FbxArmatureObject armature,
            IEnumerable<BlendShareBoneProxy> availableProxies)
        {
            if (armature == null)
            {
                return new BlendShareArmatureBoneProxyResolution(
                    null,
                    null,
                    null,
                    new[] { "Cannot resolve BlendShare bone proxies without an armature object." });
            }

            var orderedProxies = (availableProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null &&
                                proxy.isActiveAndEnabled &&
                                ReferenceEquals(proxy.SourceArmature, armature))
                .Distinct()
                .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
                .ToArray();

            var bindings = new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
            foreach (var proxy in orderedProxies)
            {
                foreach (var binding in proxy.Bindings ?? Array.Empty<BlendShareBoneProxyBinding>())
                {
                    if (binding?.IsConfigured != true)
                    {
                        continue;
                    }

                    // Later proxies and later bindings within one proxy replace the earlier entry for this path.
                    bindings[binding.SourceBonePath] = new BlendShareResolvedBone(proxy, binding);
                }
            }

            var effectiveProxies = new HashSet<BlendShareBoneProxy>(
                bindings.Values.Select(binding => binding.Proxy));

            return new BlendShareArmatureBoneProxyResolution(
                armature,
                bindings,
                orderedProxies.Where(effectiveProxies.Contains),
                Array.Empty<string>());
        }

        private static string GetHierarchyOrder(Transform transform)
        {
            var indices = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
            }

            return string.Join("/", indices);
        }
    }
}
