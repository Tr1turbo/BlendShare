using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Model.Settings
{
    /// <summary>
    /// Project-local Unity settings serialized into .blendshare.meta by BlendShareImporter.
    /// Assembly placement (Triturbo.BlendShare.Model.Editor) is locked from Commit 1 to keep [SerializeReference] stable.
    /// </summary>
    [Serializable]
    public sealed class BlendShareImportSettings
    {
        [SerializeField] private GameObject m_DefaultTargetFbx;
        [SerializeField] private string m_DefaultGeneratedAssetName = string.Empty;
        [SerializeField] private List<BlendShareMeshImportSettings> m_Meshes = new List<BlendShareMeshImportSettings>();

        public GameObject DefaultTargetFbx
        {
            get => m_DefaultTargetFbx;
            set => m_DefaultTargetFbx = value;
        }

        public string DefaultGeneratedAssetName
        {
            get => m_DefaultGeneratedAssetName ?? string.Empty;
            set => m_DefaultGeneratedAssetName = value ?? string.Empty;
        }

        public List<BlendShareMeshImportSettings> Meshes => m_Meshes ?? (m_Meshes = new List<BlendShareMeshImportSettings>());
    }

    [Serializable]
    public sealed class BlendShareMeshImportSettings
    {
        [SerializeField] private string m_MeshId = string.Empty;
        [SerializeReference] private List<BlendShareFeatureSettings> m_Features = new List<BlendShareFeatureSettings>();

        public string MeshId
        {
            get => m_MeshId ?? string.Empty;
            set => m_MeshId = value ?? string.Empty;
        }

        public List<BlendShareFeatureSettings> Features => m_Features ?? (m_Features = new List<BlendShareFeatureSettings>());
    }

    /// <summary>
    /// Optional Unity-serialized per-feature settings keyed by (MeshId, FeatureId) inside .blendshare.meta.
    /// </summary>
    [Serializable]
    public abstract class BlendShareFeatureSettings
    {
        [SerializeField] private string m_FeatureId = string.Empty;
        [SerializeField] private string m_FeatureTypeId = string.Empty;

        public string FeatureId
        {
            get => m_FeatureId ?? string.Empty;
            set => m_FeatureId = value ?? string.Empty;
        }

        public string FeatureTypeId
        {
            get => m_FeatureTypeId ?? string.Empty;
            set => m_FeatureTypeId = value ?? string.Empty;
        }
    }
}
