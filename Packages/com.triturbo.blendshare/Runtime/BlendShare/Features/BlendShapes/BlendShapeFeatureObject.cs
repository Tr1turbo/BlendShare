using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    [System.Serializable]
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class BlendShapeRecord
    {
        public string m_Name;
        public FbxBlendShapeData m_FbxBlendShapeData;

        public BlendShapeRecord() { }

        public BlendShapeRecord(string name, FbxBlendShapeData fbxBlendShapeData)
        {
            m_Name = name;
            m_FbxBlendShapeData = fbxBlendShapeData;
        }
    }

    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public sealed class BlendShapeFeatureObject : MeshFeatureObject
    {
        public const string Id = "blend-shapes";

        [SerializeField, NonReorderable]
        private List<BlendShapeRecord> m_BlendShapes = new();

        [SerializeField]
        private List<int> m_ActiveBlendShapeIndices = new();

        public override string FeatureId => Id;
        public IReadOnlyList<BlendShapeRecord> BlendShapes => m_BlendShapes;
        public IReadOnlyList<int> ActiveBlendShapeIndices => m_ActiveBlendShapeIndices;

        public override void Sanitize(MeshDataObject owner)
        {
            SanitizeShapeNames();
        }

        public IEnumerable<BlendShapeRecord> GetActiveBlendShapes()
        {
            SanitizeShapeNames();
            foreach (int index in m_ActiveBlendShapeIndices)
            {
                if (index >= 0 && index < m_BlendShapes.Count)
                {
                    yield return m_BlendShapes[index];
                }
            }
        }

        public List<string> GetAllBlendShapeNames()
        {
            return m_BlendShapes?
                .Where(blendShape => blendShape != null)
                .Select(blendShape => blendShape.m_Name)
                .ToList() ?? new List<string>();
        }

        public bool ContainsBlendShape(string name)
        {
            return GetBlendShape(name) != null;
        }

        public BlendShapeRecord GetBlendShape(string name)
        {
            if (string.IsNullOrEmpty(name) || m_BlendShapes == null)
            {
                return null;
            }

            return m_BlendShapes.FirstOrDefault(blendShape => blendShape != null && blendShape.m_Name == name);
        }

        public void SetBlendShape(string name, FbxBlendShapeData data)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            m_BlendShapes ??= new List<BlendShapeRecord>();
            var blendShape = GetBlendShape(name);
            if (blendShape == null)
            {
                blendShape = new BlendShapeRecord(name, data);
                m_BlendShapes.Add(blendShape);
                m_ActiveBlendShapeIndices ??= new List<int>();
                m_ActiveBlendShapeIndices.Add(m_BlendShapes.Count - 1);
                return;
            }

            blendShape.m_FbxBlendShapeData = data;
        }

        public void SetBlendShapes(IEnumerable<BlendShapeRecord> blendShapes)
        {
            m_BlendShapes = blendShapes?
                .Where(blendShape => blendShape != null && !string.IsNullOrWhiteSpace(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .Select(group => group.First())
                .ToList() ?? new List<BlendShapeRecord>();

            m_ActiveBlendShapeIndices = Enumerable.Range(0, m_BlendShapes.Count).ToList();
            SanitizeShapeNames();
        }

        public void SetActiveBlendShapeIndices(IEnumerable<int> indices)
        {
            m_ActiveBlendShapeIndices = indices?
                .Where(index => index >= 0 && m_BlendShapes != null && index < m_BlendShapes.Count)
                .Distinct()
                .ToList() ?? new List<int>();
        }

        public void SetActiveBlendShapeNames(IEnumerable<string> shapeNames)
        {
            var lookup = m_BlendShapes
                .Select((blendShape, index) => new { blendShape, index })
                .Where(entry => entry.blendShape != null)
                .ToDictionary(entry => entry.blendShape.m_Name, entry => entry.index);

            SetActiveBlendShapeIndices((shapeNames ?? Enumerable.Empty<string>())
                .Where(lookup.ContainsKey)
                .Select(shapeName => lookup[shapeName]));
        }

        public void ApplyPreset(MeshDataObject owner, BlendShapePresetObject preset)
        {
            if (owner == null ||
                preset == null ||
                MeshNodePath.Normalize(preset.m_Path) != MeshNodePath.Normalize(owner.m_Path))
            {
                return;
            }

            SetActiveBlendShapeIndices(preset.m_BlendShapeIndices);
        }

        public BlendShapePresetObject CreatePreset(MeshDataObject owner, string presetName)
        {
            var preset = ScriptableObject.CreateInstance<BlendShapePresetObject>();
            string path = owner != null ? owner.m_Path : string.Empty;
            preset.name = string.IsNullOrWhiteSpace(presetName) ? $"{path} Preset" : presetName;
            preset.Set(path, m_ActiveBlendShapeIndices, m_BlendShapes.Select(blendShape => blendShape?.m_Name));
            return preset;
        }

        public int InferFbxControlPointCount()
        {
            int maxIndex = -1;
            foreach (var blendShape in m_BlendShapes ?? Enumerable.Empty<BlendShapeRecord>())
            {
                foreach (var frame in blendShape?.m_FbxBlendShapeData?.m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
                {
                    if (frame?.m_PointsIndices == null || frame.m_PointsIndices.Count == 0)
                    {
                        continue;
                    }

                    maxIndex = Mathf.Max(maxIndex, frame.m_PointsIndices.Max());
                }
            }

            return maxIndex >= 0 ? maxIndex + 1 : -1;
        }

        public bool SanitizeShapeNames()
        {
            m_BlendShapes ??= new List<BlendShapeRecord>();
            m_ActiveBlendShapeIndices ??= new List<int>();

            int oldBlendShapeCount = m_BlendShapes.Count;
            m_BlendShapes = m_BlendShapes
                .Where(blendShape => blendShape != null && !string.IsNullOrWhiteSpace(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .Select(group => group.First())
                .ToList();

            var validActive = new List<int>();
            var seen = new HashSet<int>();
            foreach (int index in m_ActiveBlendShapeIndices)
            {
                if (index < 0 || index >= m_BlendShapes.Count || !seen.Add(index))
                {
                    continue;
                }
                validActive.Add(index);
            }

            if (validActive.Count == 0 && m_BlendShapes.Count > 0)
            {
                validActive.AddRange(Enumerable.Range(0, m_BlendShapes.Count));
            }

            bool changed = oldBlendShapeCount != m_BlendShapes.Count ||
                           validActive.Count != m_ActiveBlendShapeIndices.Count ||
                           !validActive.SequenceEqual(m_ActiveBlendShapeIndices);

            m_ActiveBlendShapeIndices = validActive;
            return changed;
        }
    }
}
