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
    }
}
