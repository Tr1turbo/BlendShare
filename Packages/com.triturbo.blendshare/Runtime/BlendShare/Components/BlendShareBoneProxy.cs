using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    /// <summary>
    /// Maps one source-armature bone path to its final avatar Transform.
    /// </summary>
    [Serializable]
    public sealed class BlendShareBoneProxyBinding
    {
        [SerializeField, NotKeyable]
        private string m_SourceBonePath;

        [SerializeField, NotKeyable]
        private Transform m_Transform;

        /// <summary>
        /// Gets or sets the normalized source-armature bone path.
        /// </summary>
        public string SourceBonePath
        {
            get => MeshNodePath.NormalizeOptional(m_SourceBonePath);
            set => m_SourceBonePath = MeshNodePath.NormalizeOptional(value);
        }

        /// <summary>Gets whether this binding has a source path and a final transform.</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(SourceBonePath) && Transform != null;

        /// <summary>
        /// Gets or sets the final avatar Transform for this source bone.
        /// </summary>
        public Transform Transform
        {
            get => m_Transform;
            set => m_Transform = value;
        }

        /// <summary>
        /// Creates an empty binding for Unity serialization.
        /// </summary>
        public BlendShareBoneProxyBinding()
        {
        }

        /// <summary>
        /// Creates a source-path-to-Transform binding.
        /// </summary>
        public BlendShareBoneProxyBinding(string sourceBonePath, Transform transform)
        {
            SourceBonePath = sourceBonePath;
            Transform = transform;
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Bone Proxy")]
    public sealed class BlendShareBoneProxy : BlendShareComponent
    {
        [SerializeField, NotKeyable]
        private FbxArmatureObject m_SourceArmature;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<Transform> m_TargetParentReference = new();

        [SerializeField, NotKeyable]
        private bool m_UseHierarchyParent;

        [SerializeField, NotKeyable]
        private List<BlendShareBoneProxyBinding> m_Bindings = new();

        [NonSerialized]
        private Dictionary<Transform, ParentingTransformCache> m_BindingParentingCaches = new();

        [System.NonSerialized]
        private bool m_IsApplyingParentingTransform;

        /// <summary>
        /// Gets or sets the source armature shared by all bindings on this component.
        /// </summary>
        public FbxArmatureObject SourceArmature
        {
            get => m_SourceArmature;
            set => m_SourceArmature = value;
        }

        /// <summary>
        /// Gets or sets the external target parent used by the root binding when hierarchy parenting is disabled.
        /// </summary>
        public Transform TargetParent
        {
            get => m_TargetParentReference != null && m_TargetParentReference.IsConfigured
                ? m_TargetParentReference.Get(this)
                : null;
            set => EnsureTargetParentReferenceInitialized().Set(value);
        }

        /// <summary>
        /// Gets or sets whether this proxy uses its existing Transform parent without build-time retargeting or editor following.
        /// </summary>
        public bool UseHierarchyParent
        {
            get => m_UseHierarchyParent;
            set
            {
                m_UseHierarchyParent = value;
                EnsureBindingParentingCaches().Clear();
            }
        }

        /// <summary>Gets the serialized source-to-transform bindings.</summary>
        public IReadOnlyList<BlendShareBoneProxyBinding> Bindings =>
            m_Bindings ??= new List<BlendShareBoneProxyBinding>();

        /// <summary>
        /// Replaces the serialized binding list.
        /// </summary>
        public void SetBindings(IEnumerable<BlendShareBoneProxyBinding> bindings)
        {
            m_Bindings = bindings != null
                ? new List<BlendShareBoneProxyBinding>(bindings)
                : new List<BlendShareBoneProxyBinding>();
        }

        /// <summary>
        /// Adds an explicit source-path-to-Transform binding.
        /// </summary>
        public BlendShareBoneProxyBinding AddBinding(string sourceBonePath, Transform finalTransform)
        {
            m_Bindings ??= new List<BlendShareBoneProxyBinding>();
            var binding = new BlendShareBoneProxyBinding(sourceBonePath, finalTransform);
            m_Bindings.Add(binding);
            return binding;
        }

        /// <summary>Updates one proxy binding.</summary>
        public void UpdateBinding(
            BlendShareBoneProxyBinding binding,
            string sourceBonePath,
            Transform finalTransform)
        {
            if (binding == null)
            {
                return;
            }

            binding.SourceBonePath = sourceBonePath;
            binding.Transform = finalTransform;
        }

        /// <summary>
        /// Finds the last binding for a source path, matching inspector precedence.
        /// </summary>
        public bool TryGetBinding(string sourceBonePath, out BlendShareBoneProxyBinding binding)
        {
            string normalizedPath = MeshNodePath.NormalizeOptional(sourceBonePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                binding = null;
                return false;
            }

            var bindings = Bindings;
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                var candidate = bindings[i];
                if (candidate?.IsConfigured == true && candidate.SourceBonePath == normalizedPath)
                {
                    binding = candidate;
                    return true;
                }
            }

            binding = null;
            return false;
        }

        /// <summary>
        /// Gets the final Transform assigned to a binding.
        /// </summary>
        public Transform GetFinalTransform(BlendShareBoneProxyBinding binding)
        {
            return binding?.Transform;
        }

        /// <summary>
        /// Gets the effective parent for a binding. The component root may retarget; descendants use their real hierarchy.
        /// </summary>
        public Transform GetEffectiveParent(BlendShareBoneProxyBinding binding)
        {
            var finalTransform = GetFinalTransform(binding);
            if (finalTransform == null)
            {
                return null;
            }

            if (!IsRootBinding(binding))
            {
                return finalTransform.parent;
            }

            return UseHierarchyParent ? finalTransform.parent : TargetParent;
        }

        /// <summary>Gets the build-time path for a binding under the supplied avatar root.</summary>
        public string GetFinalPath(
            BlendShareBoneProxyBinding binding,
            Transform avatarRoot,
            string finalBoneName = null)
        {
            return GetFinalPath(
                binding,
                avatarRoot,
                finalBoneName,
                new HashSet<BlendShareBoneProxyBinding>());
        }

        private string GetFinalPath(
            BlendShareBoneProxyBinding binding,
            Transform avatarRoot,
            string finalBoneName,
            ISet<BlendShareBoneProxyBinding> visitedBindings)
        {
            var finalTransform = GetFinalTransform(binding);
            string boneName = string.IsNullOrEmpty(finalBoneName)
                ? finalTransform != null ? finalTransform.name : string.Empty
                : finalBoneName;
            if (binding == null || !visitedBindings.Add(binding))
            {
                return MeshNodePath.Normalize(boneName);
            }

            BlendShareBoneProxyBinding parentBinding = null;
            var bindings = Bindings;
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                var candidate = bindings[i];
                if (candidate != null &&
                    candidate != binding &&
                    candidate.Transform == finalTransform?.parent)
                {
                    parentBinding = candidate;
                    break;
                }
            }
            if (parentBinding != null)
            {
                string parentBindingPath = GetFinalPath(
                    parentBinding,
                    avatarRoot,
                    null,
                    visitedBindings);
                return parentBindingPath == MeshNodePath.Root
                    ? MeshNodePath.Normalize(boneName)
                    : MeshNodePath.Normalize($"{parentBindingPath}/{boneName}");
            }

            var parent = GetEffectiveParent(binding);
            if (avatarRoot != null &&
                (parent == null || (parent != avatarRoot && !parent.IsChildOf(avatarRoot))))
            {
                parent = avatarRoot;
            }

            string parentPath = parent != null && avatarRoot != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(parent, avatarRoot))
                : MeshNodePath.Root;
            return parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(boneName)
                : MeshNodePath.Normalize($"{parentPath}/{boneName}");
        }

        /// <summary>
        /// Gets whether a binding is a root of this component's mapped Transform hierarchy.
        /// </summary>
        public bool IsRootBinding(BlendShareBoneProxyBinding binding)
        {
            var finalTransform = GetFinalTransform(binding);
            if (finalTransform == null)
            {
                return false;
            }

            var parent = finalTransform.parent;
            foreach (var candidate in Bindings)
            {
                if (candidate != null && candidate != binding && candidate.Transform == parent)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Calculates a binding's current local transform relative to its effective parent.
        /// </summary>
        public bool TryGetCurrentLocalTransform(
            BlendShareBoneProxyBinding binding,
            out Vector3 position,
            out Vector3 eulerRotation,
            out Vector3 scale)
        {
            var finalTransform = GetFinalTransform(binding);
            var parent = GetEffectiveParent(binding);
            if (finalTransform == null || parent == null)
            {
                position = default;
                eulerRotation = default;
                scale = default;
                return false;
            }

            position = parent.InverseTransformPoint(finalTransform.position);
            eulerRotation = (Quaternion.Inverse(parent.rotation) * finalTransform.rotation).eulerAngles;
            scale = CalculateLocalScaleForWorldScale(parent, finalTransform.lossyScale);
            return true;
        }

        /// <summary>
        /// Calculates a binding's current local-to-world matrix using its effective parent.
        /// </summary>
        public bool TryGetLocalToWorldMatrix(BlendShareBoneProxyBinding binding, out Matrix4x4 localToWorld)
        {
            if (!TryGetCurrentLocalTransform(binding, out var position, out var rotation, out var scale))
            {
                localToWorld = Matrix4x4.identity;
                return false;
            }

            localToWorld = GetEffectiveParent(binding).localToWorldMatrix * Matrix4x4.TRS(
                position,
                Quaternion.Euler(rotation),
                scale);
            return true;
        }

        /// <summary>
        /// Returns whether this proxy represents the specified armature bone.
        /// </summary>
        public bool MatchesSource(FbxArmatureObject armature, string sourceBonePath)
        {
            return SourceArmature == armature && TryGetBinding(sourceBonePath, out _);
        }

        private void OnValidate()
        {
            if (m_Bindings != null)
            {
                foreach (var binding in m_Bindings)
                {
                    if (binding != null)
                    {
                        binding.SourceBonePath = binding.SourceBonePath;
                    }
                }
            }
            EnsureTargetParentReferenceInitialized();
        }

        /// <summary>
        /// Synchronizes the organizer-side proxy transform with its configured target parent.
        /// </summary>
        public void SynchronizeParentingTransform()
        {
            if (Application.isPlaying || m_IsApplyingParentingTransform)
            {
                return;
            }

            if (UseHierarchyParent)
            {
                EnsureBindingParentingCaches().Clear();
                return;
            }

            var activeRoots = new HashSet<Transform>();
            foreach (var binding in Bindings)
            {
                var finalTransform = GetFinalTransform(binding);
                if (finalTransform == null || !IsRootBinding(binding))
                {
                    continue;
                }

                activeRoots.Add(finalTransform);
                var caches = EnsureBindingParentingCaches();
                if (!caches.TryGetValue(finalTransform, out var cache))
                {
                    cache = new ParentingTransformCache();
                    caches[finalTransform] = cache;
                }

                SynchronizeParentingTransform(finalTransform, GetEffectiveParent(binding), cache);
            }

            var staleRoots = new List<Transform>();
            foreach (var root in EnsureBindingParentingCaches().Keys)
            {
                if (root == null || !activeRoots.Contains(root))
                {
                    staleRoots.Add(root);
                }
            }

            foreach (var staleRoot in staleRoots)
            {
                EnsureBindingParentingCaches().Remove(staleRoot);
            }
        }

        private void SynchronizeParentingTransform(
            Transform target,
            Transform parent,
            ParentingTransformCache cache)
        {
            if (target == null)
            {
                return;
            }

            if (parent == null)
            {
                cache.HasSnapshot = false;
                CacheSnapshot(target, null, cache);
                return;
            }

            if (!cache.HasSnapshot || parent != cache.Parent)
            {
                RefreshParentingLocalFromCurrentTransform(target, parent, cache);
                CacheSnapshot(target, parent, cache);
                return;
            }

            bool proxyChanged = !IsSameTransformSnapshot(target.position, target.rotation, target.lossyScale,
                cache.WorldPosition, cache.WorldRotation, cache.WorldScale);
            bool parentChanged = !IsSameMatrix(parent.localToWorldMatrix, cache.ParentMatrix);

            if (proxyChanged)
            {
                RefreshParentingLocalFromCurrentTransform(target, parent, cache);
                CacheSnapshot(target, parent, cache);
                return;
            }

            if (parentChanged)
            {
                ApplyParentingLocalToWorld(target, parent, cache);
                CacheSnapshot(target, parent, cache);
            }
        }

        private AvatarObjectReference<Transform> EnsureTargetParentReferenceInitialized()
        {
            if (m_TargetParentReference == null)
            {
                m_TargetParentReference = new AvatarObjectReference<Transform>();
            }

            return m_TargetParentReference;
        }

        private static Vector3 CalculateLocalScaleForWorldScale(Transform parent, Vector3 worldScale)
        {
            if (parent == null)
            {
                return worldScale;
            }

            Vector3 parentScale = parent.lossyScale;
            return new Vector3(
                SafeDivide(worldScale.x, parentScale.x),
                SafeDivide(worldScale.y, parentScale.y),
                SafeDivide(worldScale.z, parentScale.z));
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) > 0.000001f ? value / divisor : value;
        }

        private static void RefreshParentingLocalFromCurrentTransform(
            Transform target,
            Transform parent,
            ParentingTransformCache cache)
        {
            if (parent == null)
            {
                cache.LocalPosition = target.localPosition;
                cache.LocalEulerRotation = target.localEulerAngles;
                cache.LocalScale = target.localScale == Vector3.zero ? Vector3.one : target.localScale;
                cache.HasSnapshot = false;
                return;
            }

            cache.LocalPosition = parent.InverseTransformPoint(target.position);
            cache.LocalEulerRotation = (Quaternion.Inverse(parent.rotation) * target.rotation).eulerAngles;
            cache.LocalScale = CalculateLocalScaleForWorldScale(parent, target.lossyScale);
            if (cache.LocalScale == Vector3.zero)
            {
                cache.LocalScale = Vector3.one;
            }
            cache.HasSnapshot = true;
        }

        private void ApplyParentingLocalToWorld(
            Transform target,
            Transform parent,
            ParentingTransformCache cache)
        {
            if (parent == null)
            {
                return;
            }

            var worldPosition = parent.TransformPoint(cache.LocalPosition);
            var worldRotation = parent.rotation * Quaternion.Euler(cache.LocalEulerRotation);
            var localScale = cache.LocalScale == Vector3.zero ? Vector3.one : cache.LocalScale;
            var worldScale = Vector3.Scale(parent.lossyScale, localScale);
            ApplyWorldTransform(target, worldPosition, worldRotation, worldScale);
        }

        private void ApplyWorldTransform(
            Transform target,
            Vector3 worldPosition,
            Quaternion worldRotation,
            Vector3 worldScale)
        {
            m_IsApplyingParentingTransform = true;
            try
            {
                target.SetPositionAndRotation(worldPosition, worldRotation);
                target.localScale = CalculateLocalScaleForWorldScale(target.parent, worldScale);
            }
            finally
            {
                m_IsApplyingParentingTransform = false;
            }
        }

        private static void CacheSnapshot(
            Transform target,
            Transform parent,
            ParentingTransformCache cache)
        {
            cache.Parent = parent;
            cache.WorldPosition = target.position;
            cache.WorldRotation = target.rotation;
            cache.WorldScale = target.lossyScale;
            cache.ParentMatrix = parent != null ? parent.localToWorldMatrix : Matrix4x4.identity;
        }

        private Dictionary<Transform, ParentingTransformCache> EnsureBindingParentingCaches()
        {
            return m_BindingParentingCaches ??= new Dictionary<Transform, ParentingTransformCache>();
        }

        private static bool IsSameTransformSnapshot(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Vector3 cachedPosition,
            Quaternion cachedRotation,
            Vector3 cachedScale)
        {
            return Vector3.Distance(position, cachedPosition) <= 0.0001f &&
                   Quaternion.Angle(rotation, cachedRotation) <= 0.01f &&
                   Vector3.Distance(scale, cachedScale) <= 0.0001f;
        }

        private static bool IsSameMatrix(Matrix4x4 lhs, Matrix4x4 rhs)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(lhs[row, column] - rhs[row, column]) > 0.0001f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private sealed class ParentingTransformCache
        {
            public bool HasSnapshot;
            public Transform Parent;
            public Vector3 WorldPosition;
            public Quaternion WorldRotation;
            public Vector3 WorldScale;
            public Matrix4x4 ParentMatrix;
            public Vector3 LocalPosition;
            public Vector3 LocalEulerRotation;
            public Vector3 LocalScale = Vector3.one;
        }
    }
}
