using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    /// <summary>
    /// Deserialization-only compatibility shell for BlendShare 1.x generated mesh container assets.
    /// Preserves the script GUID so scenes and prefabs referencing <see cref="Mesh"/> sub-assets remain intact.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class GeneratedMeshAssetSO : ScriptableObject
    {
        [SerializeField] private GameObject m_OriginalFbxAsset;
        [SerializeField] private string m_OriginalFbxHash = string.Empty;
        [SerializeField] private BlendShapeDataSO[] m_AppliedBlendShapes = Array.Empty<BlendShapeDataSO>();

        public GameObject ReferenceFbxAsset => m_OriginalFbxAsset;
        public string ReferenceFbxHash => m_OriginalFbxHash ?? string.Empty;
        public IReadOnlyList<BlendShapeDataSO> AppliedPatches => m_AppliedBlendShapes ?? Array.Empty<BlendShapeDataSO>();
    }
}
