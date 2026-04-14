using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public class BlendShapePresetObject : UpgradableScriptableObject
    {
        protected override int CurrentVersion => 1;

        public string m_MeshPath;
        public List<int> m_BlendShapeIndices = new();
        public List<string> m_NameSnapshots = new();

        public string DisplayName => name;

        public void Set(string meshPath, IEnumerable<int> blendShapeIndices, IEnumerable<string> nameSnapshots)
        {
            m_MeshPath = meshPath;
            m_BlendShapeIndices = blendShapeIndices?.Distinct().ToList() ?? new List<int>();
            m_NameSnapshots = nameSnapshots?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? new List<string>();
        }

        public void Sanitize(int blendShapeCount)
        {
            m_BlendShapeIndices = m_BlendShapeIndices?
                .Where(index => index >= 0 && index < blendShapeCount)
                .Distinct()
                .ToList() ?? new List<int>();
        }

        protected override void UpgradeStep(int fromVersion)
        {
            if (fromVersion == 0)
            {
                m_BlendShapeIndices ??= new List<int>();
                m_NameSnapshots ??= new List<string>();
                SetVersion(1);
                return;
            }

            SetVersion(CurrentVersion);
        }
    }
}
