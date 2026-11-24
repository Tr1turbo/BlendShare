using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEngine;
#if BS_HAS_VRCSDK3_BASE
using VRC.SDKBase;
#endif

namespace Triturbo.BlendShare.Runtime.Components
{
    /// <summary>
    /// Serializable blendshape weight override applied on a renderer after BlendShare patch generation.
    /// </summary>
    [Serializable]
    public sealed class BlendShareWeightOverride
    {
        [SerializeField]
        private string m_ShapeName = string.Empty;

        [SerializeField, Range(0f, 100f)]
        private float m_Weight;

        public string ShapeName
        {
            get => m_ShapeName ?? string.Empty;
            set => m_ShapeName = value ?? string.Empty;
        }

        public float Weight
        {
            get => m_Weight;
            set => m_Weight = Mathf.Clamp(value, 0f, 100f);
        }

        public BlendShareWeightOverride()
        {
        }

        public BlendShareWeightOverride(string shapeName, float weight)
        {
            ShapeName = shapeName;
            Weight = weight;
        }
    }

    /// <summary>
    /// Ultra-thin BlendShare 2.0 authoring component attaching a <see cref="BlendShareAsset"/> mesh patch
    /// to a target <see cref="SkinnedMeshRenderer"/>. Holds zero vertex mappings, zero feature data,
    /// and zero FBX native dependencies.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("BlendShare/BlendShare Patch Mesh")]
    public sealed class BlendSharePatchMesh : MonoBehaviour
#if BS_HAS_VRCSDK3_BASE
        , IEditorOnly
#endif
    {
        [SerializeField]
        private BlendShareAsset m_Patch;

        [SerializeField]
        private BlendShareMeshAsset m_MeshAsset;

        [SerializeField]
        private string m_MeshId = string.Empty;

        [SerializeField]
        private SkinnedMeshRenderer m_TargetRenderer;

        [SerializeField]
        private GameObject m_TargetFbxOverride;

        [SerializeField]
        private Mesh m_ReferenceMesh;

        [SerializeField]
        private bool m_CreateMissingBones = true;

        [SerializeField]
        private List<BlendShareWeightOverride> m_BlendShapeWeights = new List<BlendShareWeightOverride>();

        public BlendShareAsset Patch
        {
            get => m_Patch;
            set => m_Patch = value;
        }

        public BlendShareMeshAsset MeshAsset
        {
            get => m_MeshAsset;
            set => m_MeshAsset = value;
        }

        public string MeshId
        {
            get => m_MeshId ?? string.Empty;
            set => m_MeshId = value ?? string.Empty;
        }

        /// <summary>
        /// Resolves the effective <see cref="BlendShareAsset"/> from <see cref="Patch"/> or <see cref="MeshAsset"/>.
        /// </summary>
        public BlendShareAsset ResolvedPatch => m_Patch != null ? m_Patch : (m_MeshAsset != null ? m_MeshAsset.Root : null);

        /// <summary>
        /// Resolves the effective MeshId from <see cref="MeshAsset"/> or <see cref="MeshId"/>.
        /// </summary>
        public string ResolvedMeshId =>
            m_MeshAsset != null && !string.IsNullOrEmpty(m_MeshAsset.MeshId)
                ? m_MeshAsset.MeshId
                : (m_MeshId ?? string.Empty);

        public SkinnedMeshRenderer TargetRenderer
        {
            get => m_TargetRenderer;
            set => m_TargetRenderer = value;
        }

        public GameObject TargetFbxOverride
        {
            get => m_TargetFbxOverride;
            set => m_TargetFbxOverride = value;
        }

        public Mesh ReferenceMesh
        {
            get => m_ReferenceMesh;
            set => m_ReferenceMesh = value;
        }

        public bool CreateMissingBones
        {
            get => m_CreateMissingBones;
            set => m_CreateMissingBones = value;
        }

        public IReadOnlyList<BlendShareWeightOverride> BlendShapeWeights =>
            m_BlendShapeWeights ?? (m_BlendShapeWeights = new List<BlendShareWeightOverride>());

        public void SetBlendShapeWeight(string shapeName, float weight)
        {
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                return;
            }

            m_BlendShapeWeights ??= new List<BlendShareWeightOverride>();
            for (int i = 0; i < m_BlendShapeWeights.Count; i++)
            {
                if (m_BlendShapeWeights[i] != null &&
                    string.Equals(m_BlendShapeWeights[i].ShapeName, shapeName, StringComparison.Ordinal))
                {
                    m_BlendShapeWeights[i].Weight = weight;
                    return;
                }
            }

            m_BlendShapeWeights.Add(new BlendShareWeightOverride(shapeName, weight));
        }

        public void ClearBlendShapeWeights()
        {
            m_BlendShapeWeights?.Clear();
        }
    }
}
