using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Assets;
using UnityEngine;

namespace Triturbo.BlendShare.Components
{
    /// <summary>
    /// Provides one avatar-wide effective proxy binding selection for a source <see cref="BlendShareAsset"/>.
    /// </summary>
    public sealed class BlendShareBoneProxyResolution
    {
        private readonly Dictionary<string, BlendShareResolvedBone> m_BindingsByPath;
        private readonly List<BlendShareBoneProxy> m_SelectedProxies;
        private readonly HashSet<BlendShareBoneProxy> m_SelectedProxySet;
        private readonly List<string> m_Diagnostics;

        public BlendShareBoneProxyResolution(
            BlendShareAsset patch,
            Dictionary<string, BlendShareResolvedBone> bindingsByPath,
            IEnumerable<BlendShareBoneProxy> selectedProxies,
            IEnumerable<string> diagnostics)
        {
            Patch = patch;
            m_BindingsByPath = bindingsByPath ??
                               new Dictionary<string, BlendShareResolvedBone>(StringComparer.Ordinal);
            m_SelectedProxies = (selectedProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null)
                .Distinct()
                .ToList();
            m_SelectedProxySet = new HashSet<BlendShareBoneProxy>(m_SelectedProxies);
            m_Diagnostics = (diagnostics ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public BlendShareAsset Patch { get; }
        public bool Success => m_Diagnostics.Count == 0;
        public IReadOnlyDictionary<string, BlendShareResolvedBone> BindingsByPath => m_BindingsByPath;
        public IReadOnlyList<BlendShareBoneProxy> SelectedProxies => m_SelectedProxies;
        public IReadOnlyList<string> Diagnostics => m_Diagnostics;

        public bool TryGetBinding(string sourceBonePath, out BlendShareResolvedBone resolved)
        {
            resolved = default;
            string normalizedPath = RuntimeNodePathUtility.Normalize(sourceBonePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   m_BindingsByPath.TryGetValue(normalizedPath, out resolved) &&
                   resolved.IsProxy;
        }

        public bool IsSelected(BlendShareBoneProxy proxy)
        {
            return proxy != null && m_SelectedProxySet.Contains(proxy);
        }

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
    /// Resolves one stable per-path proxy binding selection for a source <see cref="BlendShareAsset"/>.
    /// </summary>
    public static class BlendShareBoneProxyResolver
    {
        public static BlendShareBoneProxyResolution Resolve(
            BlendShareAsset patch,
            IEnumerable<BlendShareBoneProxy> availableProxies)
        {
            if (patch == null)
            {
                return new BlendShareBoneProxyResolution(
                    null,
                    null,
                    null,
                    new[] { "Cannot resolve BlendShare bone proxies without a valid source patch." });
            }

            var orderedProxies = (availableProxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null &&
                                proxy.isActiveAndEnabled &&
                                ReferenceEquals(proxy.Patch, patch))
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

            return new BlendShareBoneProxyResolution(
                patch,
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
