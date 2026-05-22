using System;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Applied Renderer")]
    public sealed class BlendShareDestructiveApplyMarker : MonoBehaviour
    {
        [SerializeField, NotKeyable]
        private Mesh m_OriginalMesh;

        [SerializeField, NotKeyable]
        private Transform m_OriginalRootBone;

        [SerializeField, NotKeyable]
        private Transform[] m_OriginalBones = Array.Empty<Transform>();

        [SerializeField, NotKeyable]
        private Transform[] m_GeneratedBones = Array.Empty<Transform>();

        [SerializeField, NotKeyable]
        private BlendShareObject[] m_AppliedBlendShares = Array.Empty<BlendShareObject>();

        [SerializeField, NotKeyable]
        private string m_RendererNodePath;

        public Mesh OriginalMesh => m_OriginalMesh;
        public Transform OriginalRootBone => m_OriginalRootBone;
        public Transform[] OriginalBones => m_OriginalBones ?? Array.Empty<Transform>();
        public Transform[] GeneratedBones => m_GeneratedBones ?? Array.Empty<Transform>();
        public BlendShareObject[] AppliedBlendShares => m_AppliedBlendShares ?? Array.Empty<BlendShareObject>();
        public string RendererNodePath => MeshNodePath.Normalize(m_RendererNodePath);
        public bool HasBaseline => m_OriginalMesh != null || m_OriginalRootBone != null || (m_OriginalBones?.Length ?? 0) > 0;

        public void CaptureBaseline(SkinnedMeshRenderer renderer, string rendererNodePath)
        {
            if (renderer == null || HasBaseline)
            {
                return;
            }

            m_OriginalMesh = renderer.sharedMesh;
            m_OriginalRootBone = renderer.rootBone;
            m_OriginalBones = renderer.bones ?? Array.Empty<Transform>();
            m_RendererNodePath = MeshNodePath.Normalize(rendererNodePath);
        }

        public void SetGeneratedBones(Transform[] generatedBones)
        {
            m_GeneratedBones = generatedBones ?? Array.Empty<Transform>();
        }

        public void SetAppliedBlendShares(BlendShareObject[] blendShares)
        {
            m_AppliedBlendShares = blendShares ?? Array.Empty<BlendShareObject>();
        }
    }
}
