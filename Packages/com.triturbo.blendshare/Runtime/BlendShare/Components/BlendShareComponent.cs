using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Core")]
    public sealed class BlendShareComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private AvatarObjectReference<Transform> m_TargetRootReference = new();

        [SerializeField, NotKeyable]
        private List<BlendShareObject> m_BlendShares = new();

        public Transform TargetRoot
        {
            get => m_TargetRootReference != null && m_TargetRootReference.IsConfigured
                ? m_TargetRootReference.Get(this)
                : null;
            set => EnsureTargetRootReferenceInitialized().Set(value);
        }

        public IReadOnlyList<BlendShareObject> BlendShares => m_BlendShares;

        public void SetBlendShares(IEnumerable<BlendShareObject> blendShares)
        {
            m_BlendShares = blendShares != null
                ? new List<BlendShareObject>(blendShares)
                : new List<BlendShareObject>();
            Sanitize();
        }

        private void OnValidate()
        {
            EnsureTargetRootReferenceInitialized();
            Sanitize();
        }

        private AvatarObjectReference<Transform> EnsureTargetRootReferenceInitialized()
        {
            if (m_TargetRootReference == null)
            {
                m_TargetRootReference = new AvatarObjectReference<Transform>();
            }

            return m_TargetRootReference;
        }

        private void Sanitize()
        {
            m_BlendShares ??= new List<BlendShareObject>();
            m_BlendShares.RemoveAll(share => share == null);
        }
    }
}
