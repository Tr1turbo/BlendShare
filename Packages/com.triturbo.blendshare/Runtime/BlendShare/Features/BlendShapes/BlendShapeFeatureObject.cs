using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    [System.Serializable]
    public sealed class BlendShapeSelectionState
    {
        public string m_Id;
        public string m_DisplayName;
        public List<int> m_OrderedBlendShapeIndices = new();
        public List<string> m_NameSnapshots = new();

        public string DisplayName => string.IsNullOrWhiteSpace(m_DisplayName) ? "Selection Set" : m_DisplayName;

        public void Set(string displayName, IEnumerable<int> indices, IReadOnlyList<FbxBlendShapeData> blendShapes)
        {
            if (string.IsNullOrWhiteSpace(m_Id))
            {
                m_Id = System.Guid.NewGuid().ToString("N");
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                m_DisplayName = displayName.Trim();
            }

            int count = blendShapes?.Count ?? 0;
            m_OrderedBlendShapeIndices = indices?
                .Where(index => index >= 0 && index < count)
                .Distinct()
                .ToList() ?? new List<int>();
            m_NameSnapshots = m_OrderedBlendShapeIndices
                .Select(index => blendShapes?[index]?.m_Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }
    }

    public sealed class BlendShapeFeatureObject : MeshFeatureObject
    {
        public const string Id = "blend-shapes";

        [SerializeField, NonReorderable]
        private List<FbxBlendShapeData> m_BlendShapes = new();

        [SerializeField]
        private List<int> m_ActiveBlendShapeIndices = new();

        [SerializeField]
        private string m_ActiveSelectionSetId = string.Empty;

        [SerializeField]
        private List<BlendShapeSelectionState> m_SelectionSets = new();

        public override string FeatureId => Id;
        public IReadOnlyList<FbxBlendShapeData> BlendShapes => m_BlendShapes;
        public IReadOnlyList<int> ActiveBlendShapeIndices => m_ActiveBlendShapeIndices;
        public IReadOnlyList<BlendShapeSelectionState> SelectionSets => m_SelectionSets;
        public string ActiveSelectionSetId => m_ActiveSelectionSetId ?? string.Empty;

        public override void Sanitize(MeshDataObject owner)
        {
            SanitizeShapeNames();
        }

        public IEnumerable<FbxBlendShapeData> GetActiveBlendShapes()
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

        public FbxBlendShapeData GetBlendShape(string name)
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

            m_BlendShapes ??= new List<FbxBlendShapeData>();
            var blendShape = GetBlendShape(name);
            if (blendShape == null)
            {
                data.m_Name = name;
                blendShape = data;
                m_BlendShapes.Add(blendShape);
                m_ActiveBlendShapeIndices ??= new List<int>();
                m_ActiveBlendShapeIndices.Add(m_BlendShapes.Count - 1);
                return;
            }

            data.m_Name = name;
            int index = m_BlendShapes.IndexOf(blendShape);
            if (index >= 0)
            {
                m_BlendShapes[index] = data;
            }
        }

        public void SetBlendShapes(IEnumerable<FbxBlendShapeData> blendShapes)
        {
            m_BlendShapes = blendShapes?
                .Where(blendShape => blendShape != null && !string.IsNullOrWhiteSpace(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .Select(group => group.First())
                .ToList() ?? new List<FbxBlendShapeData>();

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

        public void SetWorkingSelection(IEnumerable<int> indices, string selectionSetId = null)
        {
            SetActiveBlendShapeIndices(indices);
            m_ActiveSelectionSetId = selectionSetId ?? string.Empty;
        }

        public BlendShapeSelectionState GetSelectionSet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return (m_SelectionSets ?? new List<BlendShapeSelectionState>())
                .FirstOrDefault(selection => selection != null && selection.m_Id == id);
        }

        public BlendShapeSelectionState SaveSelectionSet(string displayName, string existingId = null)
        {
            m_SelectionSets ??= new List<BlendShapeSelectionState>();
            var selection = GetSelectionSet(existingId);
            if (selection == null)
            {
                selection = new BlendShapeSelectionState();
                m_SelectionSets.Add(selection);
            }

            selection.Set(displayName, m_ActiveBlendShapeIndices, m_BlendShapes);
            m_ActiveSelectionSetId = selection.m_Id;
            SanitizeShapeNames();
            return selection;
        }

        public bool DeleteSelectionSet(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || m_SelectionSets == null)
            {
                return false;
            }

            int removed = m_SelectionSets.RemoveAll(selection => selection != null && selection.m_Id == id);
            if (m_ActiveSelectionSetId == id)
            {
                m_ActiveSelectionSetId = string.Empty;
            }

            return removed > 0;
        }

        public bool ApplySelectionSet(string id)
        {
            var selection = GetSelectionSet(id);
            if (selection == null)
            {
                return false;
            }

            SetWorkingSelection(selection.m_OrderedBlendShapeIndices, selection.m_Id);
            return true;
        }

        public bool WorkingSelectionMatches(BlendShapeSelectionState selection)
        {
            return selection != null &&
                   m_ActiveBlendShapeIndices.SequenceEqual(selection.m_OrderedBlendShapeIndices ?? new List<int>());
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

        public int InferFbxControlPointCount()
        {
            int maxIndex = -1;
            foreach (var blendShape in m_BlendShapes ?? Enumerable.Empty<FbxBlendShapeData>())
            {
                foreach (var frame in blendShape?.m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
                {
                    if (frame == null || frame.MaxDeltaIndex < 0)
                    {
                        continue;
                    }

                    maxIndex = Mathf.Max(maxIndex, frame.MaxDeltaIndex);
                }
            }

            return maxIndex >= 0 ? maxIndex + 1 : -1;
        }

        public bool SanitizeShapeNames()
        {
            m_BlendShapes ??= new List<FbxBlendShapeData>();
            m_ActiveBlendShapeIndices ??= new List<int>();
            m_SelectionSets ??= new List<BlendShapeSelectionState>();

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

            bool changed = oldBlendShapeCount != m_BlendShapes.Count ||
                           validActive.Count != m_ActiveBlendShapeIndices.Count ||
                           !validActive.SequenceEqual(m_ActiveBlendShapeIndices);

            m_ActiveBlendShapeIndices = validActive;

            var validSelectionIds = new HashSet<string>();
            foreach (var selection in m_SelectionSets.Where(selection => selection != null))
            {
                if (string.IsNullOrWhiteSpace(selection.m_Id))
                {
                    selection.m_Id = System.Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(selection.m_DisplayName))
                {
                    selection.m_DisplayName = "Selection Set";
                }

                selection.Set(selection.m_DisplayName, selection.m_OrderedBlendShapeIndices, m_BlendShapes);
                validSelectionIds.Add(selection.m_Id);
            }

            m_SelectionSets = m_SelectionSets
                .Where(selection => selection != null)
                .GroupBy(selection => selection.m_Id)
                .Select(group => group.First())
                .ToList();

            if (!string.IsNullOrEmpty(m_ActiveSelectionSetId) && !validSelectionIds.Contains(m_ActiveSelectionSetId))
            {
                m_ActiveSelectionSetId = string.Empty;
            }

            return changed;
        }
    }
}
