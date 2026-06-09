using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Core")]
    public sealed class BlendShareCore : BlendShareComponent
    {
        [SerializeField, NotKeyable]
        private AvatarObjectReference<Transform> m_TargetRootReference = new();

        [SerializeField, NotKeyable]
        private List<BlendShareObject> m_Patches = new();

        public Transform TargetRoot
        {
            get => m_TargetRootReference != null && m_TargetRootReference.IsConfigured
                ? m_TargetRootReference.Get(this)
                : null;
            set => EnsureTargetRootReferenceInitialized().Set(value);
        }

        public IReadOnlyList<BlendShareObject> Patches => m_Patches;

        public void SetPatches(IEnumerable<BlendShareObject> patches)
        {
            m_Patches = patches != null
                ? new List<BlendShareObject>(patches)
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
            m_Patches ??= new List<BlendShareObject>();
            m_Patches.RemoveAll(patch => patch == null);
        }
    }
}
