using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [PreferBinarySerialization]
    public class MeshBlendShapeSelectionSO : ScriptableObject
    {
        public string m_MeshName;
        public List<string> m_BlendShapeNames = new();

        public string DisplayName => name;
    }
}
