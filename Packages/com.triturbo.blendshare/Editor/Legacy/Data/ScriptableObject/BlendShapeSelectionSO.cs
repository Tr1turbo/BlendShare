using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [PreferBinarySerialization]
    public class BlendShapeSelectionSO : ScriptableObject
    {
        public string m_MeshName;
        public List<string> m_BlendShapeNames = new();

        public string DisplayName => name;
    }
}
