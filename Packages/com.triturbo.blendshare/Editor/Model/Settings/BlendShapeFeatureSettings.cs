using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Model.Features;
using UnityEngine;

namespace Triturbo.BlendShare.Model.Settings
{
    [Serializable]
    public sealed class BlendShapeSelectionSet
    {
        [SerializeField] private string m_SetId = string.Empty;
        [SerializeField] private string m_DisplayName = string.Empty;
        [SerializeField] private List<string> m_DisabledShapeIds = new List<string>();
        [SerializeField] private List<string> m_OrderOverrides = new List<string>();

        public string SetId
        {
            get => m_SetId ?? string.Empty;
            set => m_SetId = value ?? string.Empty;
        }

        public string DisplayName
        {
            get => m_DisplayName ?? string.Empty;
            set => m_DisplayName = value ?? string.Empty;
        }

        public List<string> DisabledShapeIds => m_DisabledShapeIds ?? (m_DisabledShapeIds = new List<string>());
        public List<string> OrderOverrides => m_OrderOverrides ?? (m_OrderOverrides = new List<string>());
    }

    /// <summary>
    /// Editable BlendShape selection and ordering overrides stored in .blendshare.meta.
    /// Uses DisabledShapeIds + OrderOverrides so newly added shapes in upgraded patches default to enabled,
    /// while unknown IDs are preserved across temporary downgrades.
    /// </summary>
    [Serializable]
    public sealed class BlendShapeFeatureSettings : BlendShareFeatureSettings
    {
        [SerializeField] private List<string> m_DisabledShapeIds = new List<string>();
        [SerializeField] private List<string> m_OrderOverrides = new List<string>();
        [SerializeField] private string m_ActiveSelectionSetId = string.Empty;
        [SerializeField] private bool m_HasWorkingOverride;
        [SerializeField] private List<BlendShapeSelectionSet> m_SelectionSets = new List<BlendShapeSelectionSet>();

        public BlendShapeFeatureSettings()
        {
            FeatureTypeId = BlendShapeFeature.FeatureTypeId;
        }

        public List<string> DisabledShapeIds => m_DisabledShapeIds ?? (m_DisabledShapeIds = new List<string>());
        public List<string> OrderOverrides => m_OrderOverrides ?? (m_OrderOverrides = new List<string>());

        public string ActiveSelectionSetId
        {
            get => m_ActiveSelectionSetId ?? string.Empty;
            set => m_ActiveSelectionSetId = value ?? string.Empty;
        }

        public bool HasWorkingOverride
        {
            get => m_HasWorkingOverride;
            set => m_HasWorkingOverride = value;
        }

        public List<BlendShapeSelectionSet> SelectionSets => m_SelectionSets ?? (m_SelectionSets = new List<BlendShapeSelectionSet>());

        public BlendShapeSelectionSet FindSelectionSet(string setId)
        {
            if (string.IsNullOrEmpty(setId) || m_SelectionSets == null)
            {
                return null;
            }

            for (int i = 0; i < m_SelectionSets.Count; i++)
            {
                var set = m_SelectionSets[i];
                if (set != null && string.Equals(set.SetId, setId, StringComparison.Ordinal))
                {
                    return set;
                }
            }

            return null;
        }

        public bool WorkingSelectionMatches(BlendShapeSelectionSet selectionSet, IReadOnlyList<string> availableShapeIdsInPatchOrder)
        {
            if (selectionSet == null)
            {
                return false;
            }

            if (!m_HasWorkingOverride && string.Equals(m_ActiveSelectionSetId, selectionSet.SetId, StringComparison.Ordinal))
            {
                return true;
            }

            var working = ResolveEffectiveShapeIds(availableShapeIdsInPatchOrder);
            var setEffective = ResolveFromLists(availableShapeIdsInPatchOrder, selectionSet.DisabledShapeIds, selectionSet.OrderOverrides);
            if (working.Count != setEffective.Count)
            {
                return false;
            }

            for (int i = 0; i < working.Count; i++)
            {
                if (!string.Equals(working[i], setEffective[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes the effective ordered list of enabled ShapeIds against a patch's available shape list.
        /// Unknown IDs in DisabledShapeIds / OrderOverrides are ignored here (preserved on disk).
        /// Shapes present in the patch and not in DisabledShapeIds are enabled by default.
        /// </summary>
        public List<string> ResolveEffectiveShapeIds(IReadOnlyList<string> availableShapeIdsInPatchOrder)
        {
            List<string> disabledList = m_DisabledShapeIds;
            List<string> orderList = m_OrderOverrides;

            if (!m_HasWorkingOverride && !string.IsNullOrEmpty(m_ActiveSelectionSetId) && m_SelectionSets != null)
            {
                var set = FindSelectionSet(m_ActiveSelectionSetId);
                if (set != null)
                {
                    disabledList = set.DisabledShapeIds;
                    orderList = set.OrderOverrides;
                }
            }

            return ResolveFromLists(availableShapeIdsInPatchOrder, disabledList, orderList);
        }

        public static List<string> ResolveFromLists(
            IReadOnlyList<string> availableShapeIdsInPatchOrder,
            IReadOnlyList<string> disabledList,
            IReadOnlyList<string> orderList)
        {
            var result = new List<string>();
            if (availableShapeIdsInPatchOrder == null || availableShapeIdsInPatchOrder.Count == 0)
            {
                return result;
            }

            var availableSet = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < availableShapeIdsInPatchOrder.Count; i++)
            {
                string id = availableShapeIdsInPatchOrder[i];
                if (!string.IsNullOrEmpty(id))
                {
                    availableSet.Add(id);
                }
            }

            var disabledSet = new HashSet<string>(StringComparer.Ordinal);
            if (disabledList != null)
            {
                for (int i = 0; i < disabledList.Count; i++)
                {
                    if (!string.IsNullOrEmpty(disabledList[i]))
                    {
                        disabledSet.Add(disabledList[i]);
                    }
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            if (orderList != null)
            {
                for (int i = 0; i < orderList.Count; i++)
                {
                    string id = orderList[i];
                    if (availableSet.Contains(id) && !disabledSet.Contains(id) && emitted.Add(id))
                    {
                        result.Add(id);
                    }
                }
            }

            for (int i = 0; i < availableShapeIdsInPatchOrder.Count; i++)
            {
                string id = availableShapeIdsInPatchOrder[i];
                if (!string.IsNullOrEmpty(id) && !disabledSet.Contains(id) && emitted.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }
    }
}
