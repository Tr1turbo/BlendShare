using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Bone Proxy")]
    public sealed class BlendShareBoneProxyComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private BlendShareApplierComponent m_Owner;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<Transform> m_TargetParentReference = new();

        [SerializeField, HideInInspector, NotKeyable]
        private Vector3 m_LocalPosition;

        [SerializeField, HideInInspector, NotKeyable]
        private Vector3 m_LocalEulerRotation;

        [SerializeField, HideInInspector, NotKeyable]
        private Vector3 m_LocalScale = Vector3.one;

        [SerializeField, NotKeyable]
        private bool m_RecalculateBindpose;

        [System.NonSerialized]
        private bool m_HasParentingCache;

        [System.NonSerialized]
        private Transform m_CachedTargetParent;

        [System.NonSerialized]
        private Vector3 m_CachedProxyPosition;

        [System.NonSerialized]
        private Quaternion m_CachedProxyRotation;

        [System.NonSerialized]
        private Vector3 m_CachedProxyScale;

        [System.NonSerialized]
        private Matrix4x4 m_CachedParentMatrix;

        [System.NonSerialized]
        private Vector3 m_ParentingLocalPosition;

        [System.NonSerialized]
        private Vector3 m_ParentingLocalEulerRotation;

        [System.NonSerialized]
        private Vector3 m_ParentingLocalScale = Vector3.one;

        [System.NonSerialized]
        private bool m_IsApplyingParentingTransform;

        public BlendShareApplierComponent Owner
        {
            get => m_Owner;
            set => m_Owner = value;
        }

        public Transform TargetParent
        {
            get => m_TargetParentReference != null && m_TargetParentReference.IsConfigured
                ? m_TargetParentReference.Get(this)
                : null;
            set => EnsureTargetParentReferenceInitialized().Set(value);
        }

        public Vector3 LocalPosition
        {
            get => m_LocalPosition;
            set => m_LocalPosition = value;
        }

        public Vector3 LocalEulerRotation
        {
            get => m_LocalEulerRotation;
            set => m_LocalEulerRotation = value;
        }

        public Vector3 LocalScale
        {
            get => m_LocalScale == Vector3.zero ? Vector3.one : m_LocalScale;
            set => m_LocalScale = value == Vector3.zero ? Vector3.one : value;
        }

        public bool RecalculateBindpose
        {
            get => m_RecalculateBindpose;
            set => m_RecalculateBindpose = value;
        }

        public Vector3 ParentingLocalPosition => m_ParentingLocalPosition;
        public Vector3 ParentingLocalEulerRotation => m_ParentingLocalEulerRotation;
        public Vector3 ParentingLocalScale => m_ParentingLocalScale == Vector3.zero ? Vector3.one : m_ParentingLocalScale;

        public bool TryGetBindPoseWorldTransform(
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 scale)
        {
            var parent = TargetParent;
            if (parent == null)
            {
                position = default;
                rotation = default;
                scale = default;
                return false;
            }

            position = parent.TransformPoint(LocalPosition);
            rotation = parent.rotation * Quaternion.Euler(LocalEulerRotation);
            scale = Vector3.Scale(parent.lossyScale, LocalScale);
            return true;
        }

        public bool TryGetCurrentLocalTransform(
            out Vector3 position,
            out Vector3 eulerRotation,
            out Vector3 scale)
        {
            var parent = TargetParent;
            if (parent == null)
            {
                position = default;
                eulerRotation = default;
                scale = default;
                return false;
            }

            position = parent.InverseTransformPoint(transform.position);
            eulerRotation = (Quaternion.Inverse(parent.rotation) * transform.rotation).eulerAngles;
            scale = CalculateLocalScaleForWorldScale(parent, transform.lossyScale);
            return true;
        }

        public void ResetTransformToBindPose()
        {
            if (!TryGetBindPoseWorldTransform(out Vector3 position, out Quaternion rotation, out Vector3 scale))
            {
                return;
            }

            ApplyWorldTransform(position, rotation, scale);
            RefreshParentingLocalFromCurrentTransform(TargetParent);
            CacheSnapshot(TargetParent);
        }

        public bool CaptureBindingTransformFromCurrentTransform()
        {
            if (!TryGetCurrentLocalTransform(out var position, out var eulerRotation, out var scale))
            {
                return false;
            }

            LocalPosition = position;
            LocalEulerRotation = eulerRotation;
            LocalScale = scale;
            RecalculateBindpose = true;
            return true;
        }

        public bool IsTransformAtBindPosition(float tolerance = 0.0001f)
        {
            return TryGetBindPoseWorldTransform(out Vector3 position, out _, out _) &&
                   Vector3.Distance(transform.position, position) <= tolerance;
        }

        private void OnValidate()
        {
            if (m_Owner == null)
            {
                m_Owner = GetComponentInParent<BlendShareApplierComponent>(true);
            }

            EnsureTargetParentReferenceInitialized();
            if (m_LocalScale == Vector3.zero)
            {
                m_LocalScale = Vector3.one;
            }

            if (m_ParentingLocalScale == Vector3.zero)
            {
                m_ParentingLocalScale = Vector3.one;
            }
        }

        private void Update()
        {
            if (Application.isPlaying || m_IsApplyingParentingTransform)
            {
                return;
            }

            var parent = TargetParent;
            if (parent == null)
            {
                m_HasParentingCache = false;
                CacheSnapshot(null);
                return;
            }

            if (!m_HasParentingCache || parent != m_CachedTargetParent)
            {
                RefreshParentingLocalFromCurrentTransform(parent);
                CacheSnapshot(parent);
                return;
            }

            bool proxyChanged = !IsSameTransformSnapshot(transform.position, transform.rotation, transform.lossyScale,
                m_CachedProxyPosition, m_CachedProxyRotation, m_CachedProxyScale);
            bool parentChanged = !IsSameMatrix(parent.localToWorldMatrix, m_CachedParentMatrix);

            if (proxyChanged)
            {
                RefreshParentingLocalFromCurrentTransform(parent);
                CacheSnapshot(parent);
                return;
            }

            if (parentChanged)
            {
                ApplyParentingLocalToWorld(parent);
                CacheSnapshot(parent);
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

        private void RefreshParentingLocalFromCurrentTransform(Transform parent)
        {
            if (parent == null)
            {
                m_ParentingLocalPosition = transform.localPosition;
                m_ParentingLocalEulerRotation = transform.localEulerAngles;
                m_ParentingLocalScale = transform.localScale == Vector3.zero ? Vector3.one : transform.localScale;
                m_HasParentingCache = false;
                return;
            }

            m_ParentingLocalPosition = parent.InverseTransformPoint(transform.position);
            m_ParentingLocalEulerRotation = (Quaternion.Inverse(parent.rotation) * transform.rotation).eulerAngles;
            m_ParentingLocalScale = CalculateLocalScaleForWorldScale(parent, transform.lossyScale);
            if (m_ParentingLocalScale == Vector3.zero)
            {
                m_ParentingLocalScale = Vector3.one;
            }
            m_HasParentingCache = true;
        }

        private void ApplyParentingLocalToWorld(Transform parent)
        {
            if (parent == null)
            {
                return;
            }

            var worldPosition = parent.TransformPoint(m_ParentingLocalPosition);
            var worldRotation = parent.rotation * Quaternion.Euler(m_ParentingLocalEulerRotation);
            var worldScale = Vector3.Scale(parent.lossyScale, ParentingLocalScale);
            ApplyWorldTransform(worldPosition, worldRotation, worldScale);
        }

        private void ApplyWorldTransform(Vector3 worldPosition, Quaternion worldRotation, Vector3 worldScale)
        {
            m_IsApplyingParentingTransform = true;
            try
            {
                transform.SetPositionAndRotation(worldPosition, worldRotation);
                transform.localScale = CalculateLocalScaleForWorldScale(transform.parent, worldScale);
            }
            finally
            {
                m_IsApplyingParentingTransform = false;
            }
        }

        private void CacheSnapshot(Transform parent)
        {
            m_CachedTargetParent = parent;
            m_CachedProxyPosition = transform.position;
            m_CachedProxyRotation = transform.rotation;
            m_CachedProxyScale = transform.lossyScale;
            m_CachedParentMatrix = parent != null ? parent.localToWorldMatrix : Matrix4x4.identity;
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
    }
}
