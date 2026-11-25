using UnityEngine;

namespace Triturbo.BlendShare.Assets
{
    /// <summary>
    /// Ultra-thin selectable sub-asset handle (Root, MeshId) allowing per-mesh drag-and-drop and component references
    /// without duplicating any descriptor data.
    /// </summary>
    public sealed class BlendShareMeshAsset : ScriptableObject
    {
        [SerializeField] private BlendShareAsset m_Root;
        [SerializeField] private string m_MeshId = string.Empty;

        public BlendShareAsset Root => m_Root;
        public string MeshId => m_MeshId ?? string.Empty;

        public void InitializeHandle(BlendShareAsset root, string meshId)
        {
            m_Root = root;
            m_MeshId = meshId ?? string.Empty;
        }
    }
}
