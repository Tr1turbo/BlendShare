using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    /// <summary>
    /// Resolves avatar-global bone proxies by source identity.
    /// </summary>
    internal sealed class BlendShareBoneProxyLookup
    {
        private readonly Dictionary<SourceKey, ResolvedBinding> bindingsBySource = new();

        private BlendShareBoneProxyLookup(IEnumerable<BlendShareBoneProxy> proxies)
        {
            var ordered = (proxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null &&
                                proxy.isActiveAndEnabled &&
                                proxy.SourceArmature != null &&
                                proxy.Bindings.Any(binding =>
                                    binding != null && !string.IsNullOrWhiteSpace(binding.SourceBonePath)))
                .Distinct()
                .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
                .ToArray();

            // Bone proxies are avatar-global. Later hierarchy entries replace earlier
            // definitions, and later bindings within one component replace earlier bindings.
            foreach (var proxy in ordered)
            {
                foreach (var binding in proxy.Bindings)
                {
                    if (binding == null || string.IsNullOrWhiteSpace(binding.SourceBonePath))
                    {
                        continue;
                    }

                    bindingsBySource[new SourceKey(proxy.SourceArmature, binding.SourceBonePath)] =
                        new ResolvedBinding(proxy, binding);
                }
            }
        }

        internal static BlendShareBoneProxyLookup Create(IEnumerable<BlendShareBoneProxy> proxies)
        {
            return new BlendShareBoneProxyLookup(proxies);
        }

        internal bool TryGet(ArmatureObject armature, string sourceBonePath, out BlendShareBoneProxy proxy)
        {
            if (TryGetBinding(armature, sourceBonePath, out var resolved))
            {
                proxy = resolved.Component;
                return true;
            }

            proxy = null;
            return false;
        }

        internal bool TryGetBinding(ArmatureObject armature, string sourceBonePath, out ResolvedBinding binding)
        {
            if (armature == null)
            {
                binding = default;
                return false;
            }

            return bindingsBySource.TryGetValue(new SourceKey(armature, sourceBonePath), out binding);
        }

        private static string GetHierarchyOrder(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var indices = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
            }

            return string.Join("/", indices);
        }

        internal readonly struct SourceKey : IEquatable<SourceKey>
        {
            private readonly ArmatureObject armature;
            private readonly string sourceBonePath;

            internal SourceKey(ArmatureObject armature, string sourceBonePath)
            {
                this.armature = armature;
                this.sourceBonePath = MeshNodePath.Normalize(sourceBonePath);
            }

            /// <inheritdoc />
            public bool Equals(SourceKey other)
            {
                return ReferenceEquals(armature, other.armature) &&
                       sourceBonePath == other.sourceBonePath;
            }

            /// <inheritdoc />
            public override bool Equals(object obj)
            {
                return obj is SourceKey other && Equals(other);
            }

            /// <inheritdoc />
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((armature != null ? armature.GetInstanceID() : 0) * 397) ^
                           (sourceBonePath != null ? sourceBonePath.GetHashCode() : 0);
                }
            }
        }

        internal readonly struct ResolvedBinding
        {
            internal ResolvedBinding(BlendShareBoneProxy component, BlendShareBoneProxyBinding binding)
            {
                Component = component;
                Binding = binding;
            }

            internal BlendShareBoneProxy Component { get; }
            internal BlendShareBoneProxyBinding Binding { get; }
            internal Transform FinalTransform => Binding?.Transform;
            internal Transform EffectiveParent => Component?.GetEffectiveParent(Binding);
            internal string SourceBonePath => Binding?.SourceBonePath;
        }
    }
}
