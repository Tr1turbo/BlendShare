using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Bone Proxy")]
    public sealed class BlendShareBoneProxyComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private BlendShareApplierComponent m_Owner;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<Transform> m_TargetParentReference = new();

        [SerializeField, NotKeyable]
        private Vector3 m_LocalPosition;

        [SerializeField, NotKeyable]
        private Vector3 m_LocalEulerRotation;

        [SerializeField, NotKeyable]
        private Vector3 m_LocalScale = Vector3.one;

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

            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = CalculateLocalScaleForWorldScale(transform.parent, scale);
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
    }
}
