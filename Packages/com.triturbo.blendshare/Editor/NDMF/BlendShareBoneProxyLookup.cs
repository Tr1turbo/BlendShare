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
        private readonly Dictionary<SourceKey, BlendShareBoneProxy> proxiesBySource = new();

        private BlendShareBoneProxyLookup(IEnumerable<BlendShareBoneProxy> proxies)
        {
            var ordered = (proxies ?? Enumerable.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null &&
                                proxy.isActiveAndEnabled &&
                                proxy.SourceArmature != null &&
                                !string.IsNullOrWhiteSpace(proxy.SourceBonePath))
                .Distinct()
                .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
                .ToArray();

            // Bone proxies are avatar-global. Later hierarchy entries replace earlier
            // definitions for the same source armature and bone path.
            foreach (var proxy in ordered)
            {
                proxiesBySource[new SourceKey(proxy.SourceArmature, proxy.SourceBonePath)] = proxy;
            }
        }

        internal static BlendShareBoneProxyLookup Create(IEnumerable<BlendShareBoneProxy> proxies)
        {
            return new BlendShareBoneProxyLookup(proxies);
        }

        internal bool TryGet(ArmatureObject armature, string sourceBonePath, out BlendShareBoneProxy proxy)
        {
            if (armature == null)
            {
                proxy = null;
                return false;
            }

            return proxiesBySource.TryGetValue(new SourceKey(armature, sourceBonePath), out proxy);
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
    }
}
