using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace Triturbo.BlendShare.Components
{
    /// <summary>
    /// Records the pre-application baseline on a <see cref="SkinnedMeshRenderer"/> when a
    /// <see cref="Runtime.Assets.BlendShareGeneratedArtifact"/> is applied directly in the scene/prefab,
    /// enabling clean one-click reversion.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [AddComponentMenu("")]
    public sealed class BlendShareAppliedRenderer : BlendShareComponent
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
        private bool m_HasBaseline;

        public Mesh OriginalMesh => m_OriginalMesh;
        public Transform OriginalRootBone => m_OriginalRootBone;
        public Transform[] OriginalBones => m_OriginalBones ?? Array.Empty<Transform>();
        public Transform[] GeneratedBones => m_GeneratedBones ?? Array.Empty<Transform>();
        public bool HasBaseline => m_HasBaseline || m_OriginalMesh != null || m_OriginalRootBone != null || (m_OriginalBones?.Length ?? 0) > 0;

        public void CaptureBaseline(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || HasBaseline)
            {
                return;
            }

            CaptureBaseline(renderer.sharedMesh, renderer.rootBone, renderer.bones);
        }

        public void CaptureBaseline(
            Mesh originalMesh,
            Transform originalRootBone,
            Transform[] originalBones)
        {
            if (HasBaseline)
            {
                return;
            }

            m_OriginalMesh = originalMesh;
            m_OriginalRootBone = originalRootBone;
            m_OriginalBones = originalBones ?? Array.Empty<Transform>();
            m_HasBaseline = true;
        }

        public void SetGeneratedBones(Transform[] generatedBones)
        {
            m_GeneratedBones = generatedBones ?? Array.Empty<Transform>();
        }

        public void AddGeneratedBones(Transform[] generatedBones)
        {
            if (generatedBones == null || generatedBones.Length == 0)
            {
                return;
            }

            var merged = new List<Transform>(GeneratedBones);
            for (int i = 0; i < generatedBones.Length; i++)
            {
                var bone = generatedBones[i];
                if (bone != null && !merged.Contains(bone))
                {
                    merged.Add(bone);
                }
            }

            m_GeneratedBones = merged.ToArray();
        }
    }
}
