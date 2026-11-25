using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    /// <summary>
    /// Deserialization-only compatibility shell for BlendShare 1.x selection preset sub-assets.
    /// Migrated into <c>BlendShapeSelectionSet</c> entries during asset upgrade.
    /// </summary>
    [PreferBinarySerialization]
    [MovedFrom(true, null, null, "MeshBlendShapeSelectionSO")]
    public sealed class BlendShapeSelectionSO : ScriptableObject
    {
        internal const string MeshNameFieldName = nameof(m_MeshName);
        internal const string BlendShapeNamesFieldName = nameof(m_BlendShapeNames);

        [SerializeField] private string m_MeshName = string.Empty;
        [SerializeField] private List<string> m_BlendShapeNames = new List<string>();

        public string MeshName => m_MeshName ?? string.Empty;
        public string DisplayName => name ?? string.Empty;
        public IReadOnlyList<string> BlendShapeNames => m_BlendShapeNames ?? (IReadOnlyList<string>)Array.Empty<string>();

        internal void ConfigureSelection(string meshName, List<string> blendShapeNames)
        {
            m_MeshName = meshName ?? string.Empty;
            m_BlendShapeNames = blendShapeNames ?? new List<string>();
        }
    }
}
