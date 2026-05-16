using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class BlendShapePresetObject : ScriptableObject
    {
        [FormerlySerializedAs("m_MeshPath")]
        public string m_Path;
        public List<int> m_BlendShapeIndices = new();
        public List<string> m_NameSnapshots = new();

        public string DisplayName => name;

        /// <summary>
        /// Stores the preset target path and selected blendshape indices.
        /// </summary>
        /// <param name="meshPath">FBX node path or matching Unity renderer path.</param>
        /// <param name="blendShapeIndices">Selected blendshape indices.</param>
        /// <param name="nameSnapshots">Blendshape names captured for diagnostics and display.</param>
        public void Set(string meshPath, IEnumerable<int> blendShapeIndices, IEnumerable<string> nameSnapshots)
        {
            m_Path = MeshNodePath.Normalize(meshPath);
            m_BlendShapeIndices = blendShapeIndices?.Distinct().ToList() ?? new List<int>();
            m_NameSnapshots = nameSnapshots?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? new List<string>();
        }

        /// <summary>
        /// Removes selected indices that are outside the current blendshape range.
        /// </summary>
        /// <param name="blendShapeCount">Current number of stored blendshapes.</param>
        public void Sanitize(int blendShapeCount)
        {
            m_Path = MeshNodePath.Normalize(m_Path);
            m_BlendShapeIndices = m_BlendShapeIndices?
                .Where(index => index >= 0 && index < blendShapeCount)
                .Distinct()
                .ToList() ?? new List<int>();
        }
    }
}
