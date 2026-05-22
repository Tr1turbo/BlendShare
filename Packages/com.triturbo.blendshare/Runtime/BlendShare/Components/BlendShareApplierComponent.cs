using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Applier")]
    public sealed class BlendShareApplierComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private Transform m_TargetRoot;

        [SerializeField, NotKeyable]
        private List<BlendShareObject> m_BlendShares = new();

        public Transform TargetRoot
        {
            get => m_TargetRoot;
            set => m_TargetRoot = value;
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
            Sanitize();
        }

        private void Sanitize()
        {
            m_BlendShares ??= new List<BlendShareObject>();
            m_BlendShares.RemoveAll(share => share == null);
        }
    }
}
