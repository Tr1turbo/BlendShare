using Triturbo.BlendShare.Core;
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
        private string m_BonePath;

        [SerializeField, NotKeyable]
        private Transform m_TargetParent;

        [SerializeField, NotKeyable]
        private string m_TargetParentPath;

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

        public string BonePath
        {
            get => MeshNodePath.Normalize(m_BonePath);
            set => m_BonePath = MeshNodePath.Normalize(value);
        }

        public Transform TargetParent
        {
            get => m_TargetParent;
            set => m_TargetParent = value;
        }

        public string TargetParentPath
        {
            get => MeshNodePath.Normalize(m_TargetParentPath);
            set => m_TargetParentPath = MeshNodePath.Normalize(value);
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

            m_BonePath = MeshNodePath.Normalize(m_BonePath);
            m_TargetParentPath = MeshNodePath.Normalize(m_TargetParentPath);
            if (m_LocalScale == Vector3.zero)
            {
                m_LocalScale = Vector3.one;
            }
        }
    }
}
