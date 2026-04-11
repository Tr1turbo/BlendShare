using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal static class BlendShapeSelectionUtility
    {
        public static List<BlendShapeSelectionSO> LoadSelections(BlendShapeDataSO dataAsset)
        {
            if (dataAsset == null)
            {
                return new List<BlendShapeSelectionSO>();
            }

            string path = AssetDatabase.GetAssetPath(dataAsset);
            if (string.IsNullOrEmpty(path))
            {
                return new List<BlendShapeSelectionSO>();
            }

            return AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<BlendShapeSelectionSO>()
                .ToList();
        }

        public static Dictionary<string, List<BlendShapeSelectionSO>> GroupSelectionsByMesh(BlendShapeDataSO dataAsset)
        {
            return LoadSelections(dataAsset)
                .GroupBy(selection => selection.m_MeshName ?? string.Empty)
                .ToDictionary(group => group.Key, group => group.OrderBy(selection => selection.DisplayName).ToList());
        }

        public static List<string> SanitizeSelectionShapeNames(
            MeshData meshData,
            IEnumerable<string> shapeNames,
            Object logContext,
            string selectionLabel)
        {
            var allShapeNames = meshData.GetAllBlendShapeNames();
            var validNames = new List<string>();
            var seen = new HashSet<string>();

            foreach (var shapeName in shapeNames ?? Enumerable.Empty<string>())
            {
                if (!allShapeNames.Contains(shapeName))
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selectionLabel}' references missing blendshape '{shapeName}' on mesh '{meshData.m_MeshName}'.",
                        logContext);
                    continue;
                }

                if (!seen.Add(shapeName))
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selectionLabel}' contains duplicate blendshape '{shapeName}' on mesh '{meshData.m_MeshName}'.",
                        logContext);
                    continue;
                }

                validNames.Add(shapeName);
            }

            return validNames;
        }

        public static List<string> ResolveSelectionShapeNames(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            BlendShapeSelectionSO selection,
            bool logWarnings)
        {
            if (selection == null)
            {
                return new List<string>(meshData.m_ShapeNames);
            }

            if (!string.Equals(selection.m_MeshName, meshData.m_MeshName))
            {
                if (logWarnings)
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selection.DisplayName}' targets mesh '{selection.m_MeshName}', but was used with '{meshData.m_MeshName}'.",
                        dataAsset);
                }
                return new List<string>(meshData.m_ShapeNames);
            }

            return logWarnings
                ? SanitizeSelectionShapeNames(meshData, selection.m_BlendShapeNames, dataAsset, selection.DisplayName)
                : selection.m_BlendShapeNames
                    .Where(shapeName => meshData.GetAllBlendShapeNames().Contains(shapeName))
                    .Distinct()
                    .ToList();
        }

        public static string GetNextSelectionName(IEnumerable<BlendShapeSelectionSO> existingSelections, string meshName)
        {
            string baseName = string.IsNullOrWhiteSpace(meshName) ? "Selection" : $"{meshName} Selection";
            var usedNames = new HashSet<string>(existingSelections.Select(selection => selection.DisplayName));
            if (!usedNames.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; ; i++)
            {
                string candidate = $"{baseName} {i}";
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
