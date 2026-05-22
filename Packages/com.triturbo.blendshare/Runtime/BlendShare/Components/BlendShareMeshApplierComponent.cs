using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Mesh Applier")]
    public sealed class BlendShareMeshApplierComponent : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private BlendShareApplierComponent m_Owner;

        [SerializeField, NotKeyable]
        private AvatarObjectReference<SkinnedMeshRenderer> m_TargetRendererReference = new();

        [SerializeField, NotKeyable]
        private MeshDataObject m_MeshData;

        [SerializeField, NotKeyable]
        private string m_RendererNodePath;

        [SerializeField, NotKeyable]
        private bool m_EnabledForBuild = true;

        public BlendShareApplierComponent Owner
        {
            get => m_Owner;
            set => m_Owner = value;
        }

        public SkinnedMeshRenderer TargetRenderer
        {
            get => m_TargetRendererReference != null && m_TargetRendererReference.IsConfigured
                ? m_TargetRendererReference.Get(this)
                : null;
            set => EnsureTargetRendererReferenceInitialized().Set(value);
        }

        public MeshDataObject MeshData
        {
            get => m_MeshData;
            set => m_MeshData = value;
        }

        public string RendererNodePath
        {
            get => MeshNodePath.Normalize(m_RendererNodePath);
            set => m_RendererNodePath = MeshNodePath.Normalize(value);
        }

        public bool EnabledForBuild
        {
            get => m_EnabledForBuild;
            set => m_EnabledForBuild = value;
        }

        private void OnValidate()
        {
            if (m_Owner == null)
            {
                m_Owner = GetComponentInParent<BlendShareApplierComponent>(true);
            }

            EnsureTargetRendererReferenceInitialized();
            if (!m_TargetRendererReference.IsConfigured)
            {
                var renderer = GetComponent<SkinnedMeshRenderer>();
                if (renderer != null)
                {
                    m_TargetRendererReference.Set(renderer);
                }
            }

            m_RendererNodePath = MeshNodePath.Normalize(m_RendererNodePath);
        }

        private AvatarObjectReference<SkinnedMeshRenderer> EnsureTargetRendererReferenceInitialized()
        {
            if (m_TargetRendererReference == null)
            {
                m_TargetRendererReference = new AvatarObjectReference<SkinnedMeshRenderer>();
            }

            return m_TargetRendererReference;
        }
    }
}
