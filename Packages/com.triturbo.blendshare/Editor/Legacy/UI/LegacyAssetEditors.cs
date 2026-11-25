using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.Legacy.Migration;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.UI.Shared;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Legacy.UI
{
    /// <summary>
    /// Preserves the original BlendShare 1.x <see cref="BlendShapeDataSO"/> IMGUI Inspector layout and editing controls,
    /// replacing legacy FBX/mesh generation buttons with a direct one-click upgrade to <c>.blendshare</c>.
    /// </summary>
    [CustomEditor(typeof(BlendShapeDataSO))]
    public sealed class BlendShapeDataSOEditor : UnityEditor.Editor
    {
        private enum BlendShapeEditMode
        {
            Reorder = 0,
            Toggle = 1
        }

        private List<ReorderableList> m_MeshBlendShapes;
        private SerializedProperty m_MeshDataListProperty;
        private SerializedProperty m_OriginalFbxProperty;
        private SerializedProperty m_AppliedProperty;
        private bool m_ReadOnlyMode = true;
        private BlendShapeEditMode m_EditMode = BlendShapeEditMode.Reorder;
        private List<string> m_MeshBlendShapeSearchTerms;
        private Dictionary<string, List<BlendShapeSelectionSO>> m_MeshSelectionsByMeshName;
        private Dictionary<string, BlendShapeSelectionSO> m_WorkingSelectionSourcesByMeshName;
        private Dictionary<string, string[]> m_CachedAllBlendShapeNamesByMeshName;
        private bool m_BulkToggleDragActive;
        private bool m_BulkToggleDragValue;
        private int m_BulkToggleDragMeshIndex = -1;
        private Vector2 m_BulkToggleDragStartPosition;

        private static class EditorStatePrefs
        {
            private const bool k_DefaultEditingEnabled = false;
            private const BlendShapeEditMode k_DefaultBlendShapeEditMode = BlendShapeEditMode.Reorder;
            private const string k_KeyPrefix = "Triturbo.BlendShapeShare.BlendShapeDataSO.";
            private const string k_EditingEnabledKeyPrefix = k_KeyPrefix + "EditingEnabled.";
            private const string k_BlendShapeEditModeKeyPrefix = k_KeyPrefix + "BlendShapeEditMode.";

            public static bool GetEditingEnabled(Object editorTarget)
            {
                return EditorPrefs.GetBool(GetEditingEnabledKey(editorTarget), k_DefaultEditingEnabled);
            }

            public static void SetEditingEnabled(Object editorTarget, bool editingEnabled)
            {
                string key = GetEditingEnabledKey(editorTarget);
                if (editingEnabled == k_DefaultEditingEnabled)
                {
                    EditorPrefs.DeleteKey(key);
                    return;
                }

                EditorPrefs.SetBool(key, editingEnabled);
            }

            public static BlendShapeEditMode GetBlendShapeEditMode(Object editorTarget)
            {
                string key = GetBlendShapeEditModeKey(editorTarget);
                int savedValue = EditorPrefs.GetInt(key, (int)k_DefaultBlendShapeEditMode);
                return Enum.IsDefined(typeof(BlendShapeEditMode), savedValue)
                    ? (BlendShapeEditMode)savedValue
                    : k_DefaultBlendShapeEditMode;
            }

            public static void SetBlendShapeEditMode(Object editorTarget, BlendShapeEditMode editMode)
            {
                string key = GetBlendShapeEditModeKey(editorTarget);
                if (editMode == k_DefaultBlendShapeEditMode)
                {
                    EditorPrefs.DeleteKey(key);
                    return;
                }

                EditorPrefs.SetInt(key, (int)editMode);
            }

            private static string GetEditingEnabledKey(Object editorTarget) => k_EditingEnabledKeyPrefix + GetAssetKey(editorTarget);
            private static string GetBlendShapeEditModeKey(Object editorTarget) => k_BlendShapeEditModeKeyPrefix + GetAssetKey(editorTarget);

            private static string GetAssetKey(Object editorTarget)
            {
                if (editorTarget == null)
                {
                    return "None";
                }

                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(editorTarget, out string guid, out long localId) &&
                    !string.IsNullOrEmpty(guid))
                {
                    return $"{guid}_{localId}";
                }

                string path = AssetDatabase.GetAssetPath(editorTarget);
                if (!string.IsNullOrEmpty(path))
                {
                    string pathGuid = AssetDatabase.AssetPathToGUID(path);
                    return string.IsNullOrEmpty(pathGuid) ? path : pathGuid;
                }

                return editorTarget.GetInstanceID().ToString();
            }
        }

        private void ShowContextMenu(ReorderableList reorderableList, int index)
        {
            var menu = new GenericMenu();
            menu.AddItem(Localization.G("data.mute_blendshape"), false, () =>
            {
                SerializedProperty listProperty = reorderableList.serializedProperty;
                listProperty.DeleteArrayElementAtIndex(index);
                serializedObject.ApplyModifiedProperties();
            });
            menu.ShowAsContext();
        }

        private void OnEnable()
        {
            m_MeshDataListProperty = serializedObject.FindProperty(BlendShapeDataSO.MeshDataListFieldName);
            m_OriginalFbxProperty = serializedObject.FindProperty(BlendShapeDataSO.OriginalFieldName);
            m_AppliedProperty = serializedObject.FindProperty(BlendShapeDataSO.AppliedFieldName);
            m_MeshSelectionsByMeshName = new Dictionary<string, List<BlendShapeSelectionSO>>(StringComparer.Ordinal);
            m_WorkingSelectionSourcesByMeshName = new Dictionary<string, BlendShapeSelectionSO>(StringComparer.Ordinal);

            int meshCount = m_MeshDataListProperty != null ? m_MeshDataListProperty.arraySize : 0;
            m_MeshBlendShapeSearchTerms = new List<string>(meshCount);
            var dataAsset = (BlendShapeDataSO)target;
            RefreshAllBlendShapeNameCache();
            m_ReadOnlyMode = !EditorStatePrefs.GetEditingEnabled(target);
            m_EditMode = EditorStatePrefs.GetBlendShapeEditMode(target);
            RefreshMeshSelections(dataAsset);

            m_MeshBlendShapes = new List<ReorderableList>(meshCount);
            for (int i = 0; i < meshCount; i++)
            {
                var meshDataProperty = m_MeshDataListProperty.GetArrayElementAtIndex(i);
                var blendShapeNamesProperty = meshDataProperty.FindPropertyRelative(MeshData.ShapeNamesFieldName);

                var reorderableList = new ReorderableList(
                    blendShapeNamesProperty.serializedObject,
                    blendShapeNamesProperty,
                    false,
                    false,
                    false,
                    false);

                reorderableList.drawElementCallback = (rect, index, _, _) =>
                {
                    var blendShapeProperty = blendShapeNamesProperty.GetArrayElementAtIndex(index);
                    EditorGUI.LabelField(rect, blendShapeProperty.stringValue);
                    if (!m_ReadOnlyMode && m_EditMode == BlendShapeEditMode.Reorder &&
                        Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
                    {
                        if (reorderableList.IsSelected(index))
                        {
                            ShowContextMenu(reorderableList, index);
                            Event.current.Use();
                        }
                    }
                };
                reorderableList.footerHeight = 0;
                m_MeshBlendShapes.Add(reorderableList);
                m_MeshBlendShapeSearchTerms.Add(string.Empty);
            }
        }

        private void RefreshAllBlendShapeNameCache()
        {
            m_CachedAllBlendShapeNamesByMeshName = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (m_MeshDataListProperty == null)
            {
                return;
            }

            for (int i = 0; i < m_MeshDataListProperty.arraySize; i++)
            {
                CacheAllBlendShapeNames(m_MeshDataListProperty.GetArrayElementAtIndex(i));
            }
        }

        private static string GetBlendShapeCacheKey(string meshName) => meshName ?? string.Empty;

        private string[] CacheAllBlendShapeNames(SerializedProperty meshDataProperty)
        {
            if (meshDataProperty == null)
            {
                return Array.Empty<string>();
            }

            string meshName = meshDataProperty.FindPropertyRelative(MeshData.MeshNameFieldName).stringValue;
            var blendShapesProperty = meshDataProperty.FindPropertyRelative(MeshData.BlendShapesFieldName);
            string[] allBlendShapeNames = new string[blendShapesProperty.arraySize];
            for (int i = 0; i < blendShapesProperty.arraySize; i++)
            {
                allBlendShapeNames[i] = GetBlendShapeName(blendShapesProperty.GetArrayElementAtIndex(i));
            }

            m_CachedAllBlendShapeNamesByMeshName[GetBlendShapeCacheKey(meshName)] = allBlendShapeNames;
            return allBlendShapeNames;
        }

        private string[] GetCachedAllBlendShapeNames(string meshName)
        {
            m_CachedAllBlendShapeNamesByMeshName ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
            return m_CachedAllBlendShapeNamesByMeshName.TryGetValue(GetBlendShapeCacheKey(meshName), out var all)
                ? all
                : Array.Empty<string>();
        }

        private string[] GetCachedAllBlendShapeNames(MeshData meshData) => GetCachedAllBlendShapeNames(meshData?.MeshName);

        private List<string> ResolveSelectionShapeNamesUsingCache(MeshData meshData, BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return meshData != null ? new List<string>(meshData.ShapeNames) : new List<string>();
            }

            if (!string.Equals(selection.MeshName, meshData.MeshName, StringComparison.Ordinal))
            {
                return new List<string>(meshData.ShapeNames);
            }

            string[] allBlendShapeNames = GetCachedAllBlendShapeNames(meshData);
            return selection.BlendShapeNames
                .Where(shapeName => allBlendShapeNames.Contains(shapeName))
                .Distinct()
                .ToList();
        }

        private void RefreshMeshSelections(BlendShapeDataSO dataAsset)
        {
            m_MeshSelectionsByMeshName = LegacySelectionUtility.GroupSelectionsByMesh(dataAsset);
            m_WorkingSelectionSourcesByMeshName ??= new Dictionary<string, BlendShapeSelectionSO>(StringComparer.Ordinal);

            var validSelections = new HashSet<BlendShapeSelectionSO>(m_MeshSelectionsByMeshName.Values.SelectMany(list => list));
            string[] invalidKeys = m_WorkingSelectionSourcesByMeshName
                .Where(entry => entry.Value == null || !validSelections.Contains(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();

            for (int i = 0; i < invalidKeys.Length; i++)
            {
                m_WorkingSelectionSourcesByMeshName.Remove(invalidKeys[i]);
            }
        }

        private List<BlendShapeSelectionSO> GetMeshSelections(string meshName)
        {
            return m_MeshSelectionsByMeshName != null &&
                   m_MeshSelectionsByMeshName.TryGetValue(meshName ?? string.Empty, out var selections)
                ? selections
                : new List<BlendShapeSelectionSO>();
        }

        private static bool HasSameShapeNames(IReadOnlyList<string> currentShapeNames, IReadOnlyList<string> selectionShapeNames)
        {
            if (currentShapeNames == null || selectionShapeNames == null || currentShapeNames.Count != selectionShapeNames.Count)
            {
                return false;
            }

            for (int i = 0; i < currentShapeNames.Count; i++)
            {
                if (!string.Equals(currentShapeNames[i], selectionShapeNames[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private int GetCurrentPopupIndex(MeshData meshData, IReadOnlyList<BlendShapeSelectionSO> selections)
        {
            if (HasSameShapeNames(meshData.ShapeNames, GetCachedAllBlendShapeNames(meshData)))
            {
                return 0;
            }

            for (int i = 0; i < selections.Count; i++)
            {
                var resolvedNames = ResolveSelectionShapeNamesUsingCache(meshData, selections[i]);
                if (HasSameShapeNames(meshData.ShapeNames, resolvedNames))
                {
                    return i + 1;
                }
            }

            return selections.Count + 1;
        }

        private BlendShapeSelectionSO SaveMeshSelection(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            BlendShapeSelectionSO existingSelection = null,
            string selectionNameOverride = null)
        {
            var selections = GetMeshSelections(meshData.MeshName);
            string selectionName = existingSelection != null
                ? existingSelection.DisplayName
                : selectionNameOverride;

            if (string.IsNullOrWhiteSpace(selectionName))
            {
                selectionName = LegacySelectionUtility.GetNextSelectionName(selections, meshData.MeshName);
            }

            selectionName = selectionName.Trim();
            var sanitizedNames = LegacySelectionUtility.SanitizeSelectionShapeNames(
                meshData,
                meshData.ShapeNames,
                dataAsset,
                selectionName);

            BlendShapeSelectionSO selection = existingSelection;
            if (selection == null)
            {
                selection = CreateInstance<BlendShapeSelectionSO>();
                selection.name = selectionName;
                AssetDatabase.AddObjectToAsset(selection, dataAsset);
            }

            selection.ConfigureSelection(meshData.MeshName, sanitizedNames);
            selection.name = selectionName;

            EditorUtility.SetDirty(selection);
            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();

            RefreshMeshSelections(dataAsset);
            SetWorkingSelectionSource(meshData.MeshName, selection);
            return selection;
        }

        private void DeleteMeshSelection(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                Localization.S("data.blendshape_selection.delete_title"),
                Localization.SF("data.blendshape_selection.delete_message", selection.DisplayName),
                Localization.S("data.dialog.delete"),
                Localization.S("data.dialog.cancel"));
            if (!confirmed)
            {
                return;
            }

            if (m_WorkingSelectionSourcesByMeshName != null)
            {
                string[] keys = m_WorkingSelectionSourcesByMeshName
                    .Where(entry => entry.Value == selection)
                    .Select(entry => entry.Key)
                    .ToArray();

                for (int i = 0; i < keys.Length; i++)
                {
                    m_WorkingSelectionSourcesByMeshName.Remove(keys[i]);
                }
            }

            if (Selection.activeObject == selection)
            {
                Selection.activeObject = dataAsset;
            }

            DestroyImmediate(selection, true);
            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            RefreshMeshSelections(dataAsset);
            SetWorkingSelectionSource(meshData.MeshName, null);
            serializedObject.Update();
            Repaint();
        }

        private void LoadShapeNamesIntoWorkingSet(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            IReadOnlyList<string> shapeNames)
        {
            meshData.SetWorkingShapeNames(shapeNames);
            serializedObject.Update();

            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();
            Repaint();
        }

        private BlendShapeSelectionSO GetWorkingSelectionSource(string meshName)
        {
            return m_WorkingSelectionSourcesByMeshName != null &&
                   m_WorkingSelectionSourcesByMeshName.TryGetValue(meshName ?? string.Empty, out var selection)
                ? selection
                : null;
        }

        private void SetWorkingSelectionSource(string meshName, BlendShapeSelectionSO selection)
        {
            m_WorkingSelectionSourcesByMeshName ??= new Dictionary<string, BlendShapeSelectionSO>(StringComparer.Ordinal);
            string key = meshName ?? string.Empty;
            if (selection == null)
            {
                m_WorkingSelectionSourcesByMeshName.Remove(key);
                return;
            }

            m_WorkingSelectionSourcesByMeshName[key] = selection;
        }

        private void LoadMeshSelectionIntoWorkingSet(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return;
            }

            var resolvedNames = LegacySelectionUtility.ResolveSelectionShapeNames(dataAsset, meshData, selection, true);
            LoadShapeNamesIntoWorkingSet(dataAsset, meshData, resolvedNames);
            SetWorkingSelectionSource(meshData.MeshName, selection);
        }

        private void RevertWorkingSelection(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            BlendShapeSelectionSO selection)
        {
            if (selection != null)
            {
                LoadMeshSelectionIntoWorkingSet(dataAsset, meshData, selection);
                return;
            }

            LoadShapeNamesIntoWorkingSet(dataAsset, meshData, GetCachedAllBlendShapeNames(meshData));
            SetWorkingSelectionSource(meshData.MeshName, null);
        }

        private void PromptSaveMeshSelection(BlendShapeDataSO dataAsset, MeshData meshData, Action onSaved = null)
        {
            string defaultName = LegacySelectionUtility.GetNextSelectionName(
                GetMeshSelections(meshData.MeshName),
                meshData.MeshName);

            BlendShapeSelectionNamePrompt.Show(
                Localization.S("data.blendshape_selection.save_title"),
                defaultName,
                selectionName =>
                {
                    SaveMeshSelection(dataAsset, meshData, null, selectionName);
                    onSaved?.Invoke();
                    serializedObject.Update();
                    Repaint();
                });
        }

        private void LoadPopupSelectionChoice(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            IReadOnlyList<BlendShapeSelectionSO> selections,
            int popupIndex)
        {
            if (popupIndex == 0)
            {
                LoadShapeNamesIntoWorkingSet(dataAsset, meshData, GetCachedAllBlendShapeNames(meshData));
                SetWorkingSelectionSource(meshData.MeshName, null);
                return;
            }

            int selectionIndex = popupIndex - 1;
            if (selectionIndex >= 0 && selectionIndex < selections.Count)
            {
                LoadMeshSelectionIntoWorkingSet(dataAsset, meshData, selections[selectionIndex]);
            }
        }

        private bool HasSelectionIssues(MeshData meshData, BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return false;
            }

            if (!string.Equals(selection.MeshName, meshData.MeshName, StringComparison.Ordinal))
            {
                return true;
            }

            string[] allShapeNames = GetCachedAllBlendShapeNames(meshData);
            return selection.BlendShapeNames
                .GroupBy(shapeName => shapeName, StringComparer.Ordinal)
                .Any(group => group.Count() > 1 || !allShapeNames.Contains(group.Key));
        }

        private void DrawMeshSelectionSection(BlendShapeDataSO dataAsset, MeshData meshData)
        {
            if (meshData == null)
            {
                return;
            }

            string meshName = meshData.MeshName;
            var selections = GetMeshSelections(meshName);
            if (m_ReadOnlyMode && selections.Count == 0)
            {
                return;
            }

            var workingSelectionSource = GetWorkingSelectionSource(meshName);
            int currentPopupIndex = GetCurrentPopupIndex(meshData, selections);
            bool showCurrentWorkingSet = currentPopupIndex == selections.Count + 1;
            int workingSelectionSourceIndex = workingSelectionSource != null
                ? selections.FindIndex(selection => selection == workingSelectionSource) + 1
                : -1;
            int displayedPopupIndex =
                showCurrentWorkingSet && workingSelectionSourceIndex > 0
                    ? workingSelectionSourceIndex
                    : currentPopupIndex;

            string[] optionNames = new[] { Localization.S("data.blendshape_selection.all") }
                .Concat(selections.Select(selection =>
                    showCurrentWorkingSet && selection == workingSelectionSource
                        ? $"{selection.DisplayName}*"
                        : selection.DisplayName))
                .Concat(showCurrentWorkingSet && workingSelectionSourceIndex <= 0
                    ? new[] { Localization.S("data.blendshape_selection.unsaved_working_set") }
                    : Array.Empty<string>())
                .ToArray();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField(Localization.G("data.blendshape_selection.title"), EditorStyles.boldLabel);

            BlendShapeSelectionSO selectedSelection =
                displayedPopupIndex > 0 && displayedPopupIndex <= selections.Count
                    ? selections[displayedPopupIndex - 1]
                    : null;

            Rect selectionRowRect = EditorGUILayout.GetControlRect();
            Rect popupRect = EditorGUI.PrefixLabel(
                selectionRowRect,
                GUIUtility.GetControlID(FocusType.Passive),
                Localization.G("data.blendshape_selection.active"));

            Rect deleteButtonRect = Rect.zero;
            if (!m_ReadOnlyMode)
            {
                GUIContent deleteSelectionContent = Localization.G("data.blendshape_selection.delete");
                float deleteButtonWidth = Mathf.Min(
                    Mathf.Max(64f, GUI.skin.button.CalcSize(deleteSelectionContent).x + 12f),
                    popupRect.width * 0.4f);
                deleteButtonRect = new Rect(
                    popupRect.xMax - deleteButtonWidth,
                    popupRect.y,
                    deleteButtonWidth,
                    popupRect.height);
                popupRect.xMax = deleteButtonRect.x - 4f;
            }

            int updatedSelectionIndex = EditorGUI.Popup(
                popupRect,
                Mathf.Clamp(displayedPopupIndex, 0, Mathf.Max(0, optionNames.Length - 1)),
                optionNames);

            if (!m_ReadOnlyMode)
            {
                EditorGUI.BeginDisabledGroup(selectedSelection == null);
                if (GUI.Button(deleteButtonRect, Localization.G("data.blendshape_selection.delete")))
                {
                    DeleteMeshSelection(dataAsset, meshData, selectedSelection);
                    GUIUtility.ExitGUI();
                }
                EditorGUI.EndDisabledGroup();
            }

            if (updatedSelectionIndex != displayedPopupIndex)
            {
                bool isUnsavedWorkingState = currentPopupIndex == selections.Count + 1 &&
                                             meshData.ShapeNames.Count > 0;
                if (isUnsavedWorkingState)
                {
                    int choice = EditorUtility.DisplayDialogComplex(
                        Localization.S("data.blendshape_selection.unsaved_working_set"),
                        Localization.SF("data.blendshape_selection.unsaved_working_set.message", meshName),
                        Localization.S("data.dialog.save"),
                        Localization.S("data.dialog.cancel"),
                        Localization.S("data.dialog.discard"));

                    if (choice == 0)
                    {
                        if (workingSelectionSource != null)
                        {
                            SaveMeshSelection(dataAsset, meshData, workingSelectionSource);
                            LoadPopupSelectionChoice(dataAsset, meshData, selections, updatedSelectionIndex);
                        }
                        else
                        {
                            PromptSaveMeshSelection(dataAsset, meshData, () =>
                            {
                                LoadPopupSelectionChoice(dataAsset, meshData, selections, updatedSelectionIndex);
                            });
                        }
                        GUIUtility.ExitGUI();
                    }
                    else if (choice == 2)
                    {
                        LoadPopupSelectionChoice(dataAsset, meshData, selections, updatedSelectionIndex);
                        GUIUtility.ExitGUI();
                    }

                    return;
                }

                LoadPopupSelectionChoice(dataAsset, meshData, selections, updatedSelectionIndex);
                GUIUtility.ExitGUI();
            }

            if (selectedSelection != null)
            {
                SetWorkingSelectionSource(meshName, selectedSelection);
                if (HasSelectionIssues(meshData, selectedSelection))
                {
                    EditorGUILayout.HelpBox(
                        Localization.S("data.blendshape_selection.issue_warning"),
                        MessageType.Warning);
                }
            }
            else if (currentPopupIndex == 0)
            {
                SetWorkingSelectionSource(meshName, null);
            }

            EditorGUI.BeginDisabledGroup(m_ReadOnlyMode);
            if (!m_ReadOnlyMode && showCurrentWorkingSet)
            {
                GUILayout.BeginHorizontal();
                if (workingSelectionSource != null && GUILayout.Button(Localization.G("data.blendshape_selection.revert_changes")))
                {
                    RevertWorkingSelection(dataAsset, meshData, workingSelectionSource);
                    GUIUtility.ExitGUI();
                }

                if (workingSelectionSource != null && GUILayout.Button(Localization.G("data.blendshape_selection.save_changes")))
                {
                    SaveMeshSelection(dataAsset, meshData, workingSelectionSource);
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button(Localization.G("data.blendshape_selection.save_as_new")))
                {
                    PromptSaveMeshSelection(dataAsset, meshData);
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private static string GetBlendShapeName(SerializedProperty blendShapeWrapperProperty)
        {
            return blendShapeWrapperProperty.FindPropertyRelative(BlendShapeWrapper.ShapeNameFieldName).stringValue;
        }

        private static HashSet<string> GetEnabledBlendShapeNames(SerializedProperty shapeNamesProperty)
        {
            var enabledNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < shapeNamesProperty.arraySize; i++)
            {
                enabledNames.Add(shapeNamesProperty.GetArrayElementAtIndex(i).stringValue);
            }

            return enabledNames;
        }

        private static void RewriteShapeNames(SerializedProperty shapeNamesProperty, IEnumerable<string> orderedShapeNames)
        {
            shapeNamesProperty.ClearArray();
            int index = 0;
            foreach (string shapeName in orderedShapeNames)
            {
                shapeNamesProperty.InsertArrayElementAtIndex(index);
                shapeNamesProperty.GetArrayElementAtIndex(index).stringValue = shapeName;
                index++;
            }
        }

        private static bool MatchesSearch(string searchTerm, string shapeName)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return true;
            }

            return shapeName.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void SetBlendShapeEnabledPreserveSourceOrder(
            SerializedProperty shapeNamesProperty,
            IReadOnlyList<string> allShapeNames,
            string shapeName,
            bool enabled)
        {
            var enabledShapeNames = GetEnabledBlendShapeNames(shapeNamesProperty);
            if (enabled)
            {
                enabledShapeNames.Add(shapeName);
            }
            else
            {
                enabledShapeNames.Remove(shapeName);
            }

            RewriteShapeNames(shapeNamesProperty, allShapeNames.Where(enabledShapeNames.Contains));
        }

        private static void SetFilteredBlendShapesEnabled(
            SerializedProperty shapeNamesProperty,
            IReadOnlyList<string> allShapeNames,
            string searchTerm,
            bool enabled)
        {
            var enabledShapeNames = GetEnabledBlendShapeNames(shapeNamesProperty);
            for (int i = 0; i < allShapeNames.Count; i++)
            {
                string shapeName = allShapeNames[i];
                if (!MatchesSearch(searchTerm, shapeName))
                {
                    continue;
                }

                if (enabled)
                {
                    enabledShapeNames.Add(shapeName);
                }
                else
                {
                    enabledShapeNames.Remove(shapeName);
                }
            }

            RewriteShapeNames(shapeNamesProperty, allShapeNames.Where(enabledShapeNames.Contains));
        }

        private void ResetBulkToggleDrag()
        {
            m_BulkToggleDragActive = false;
            m_BulkToggleDragMeshIndex = -1;
        }

        private void HandleBulkToggleDragLifecycle()
        {
            Event currentEvent = Event.current;
            if (!m_BulkToggleDragActive)
            {
                return;
            }

            if (currentEvent.type == EventType.MouseUp && currentEvent.button == 0)
            {
                ResetBulkToggleDrag();
                currentEvent.Use();
                Repaint();
                return;
            }

            if (currentEvent.type == EventType.MouseLeaveWindow || currentEvent.type == EventType.Ignore)
            {
                ResetBulkToggleDrag();
            }
        }

        private bool IsToggleInsideBulkDragRange(int meshIndex, Rect toggleRect, Vector2 currentMousePosition)
        {
            if (!m_BulkToggleDragActive || m_BulkToggleDragMeshIndex != meshIndex)
            {
                return false;
            }

            float minY = Mathf.Min(m_BulkToggleDragStartPosition.y, currentMousePosition.y);
            float maxY = Mathf.Max(m_BulkToggleDragStartPosition.y, currentMousePosition.y);
            return toggleRect.yMax >= minY && toggleRect.yMin <= maxY;
        }

        private void DrawBlendShapeToggle(
            int meshIndex,
            string shapeName,
            bool isEnabled,
            SerializedProperty shapeNamesProperty,
            IReadOnlyList<string> allShapeNames,
            out bool changed)
        {
            changed = false;
            Rect toggleRect = EditorGUILayout.GetControlRect();
            Event currentEvent = Event.current;

            if (currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                toggleRect.Contains(currentEvent.mousePosition))
            {
                m_BulkToggleDragActive = true;
                m_BulkToggleDragValue = !isEnabled;
                m_BulkToggleDragMeshIndex = meshIndex;
                m_BulkToggleDragStartPosition = currentEvent.mousePosition;

                SetBlendShapeEnabledPreserveSourceOrder(shapeNamesProperty, allShapeNames, shapeName, m_BulkToggleDragValue);
                changed = true;
                currentEvent.Use();
                GUI.changed = true;
                EditorGUI.ToggleLeft(toggleRect, shapeName, m_BulkToggleDragValue);
                return;
            }

            if (currentEvent.type == EventType.MouseDrag &&
                currentEvent.button == 0 &&
                IsToggleInsideBulkDragRange(meshIndex, toggleRect, currentEvent.mousePosition))
            {
                if (isEnabled != m_BulkToggleDragValue)
                {
                    SetBlendShapeEnabledPreserveSourceOrder(shapeNamesProperty, allShapeNames, shapeName, m_BulkToggleDragValue);
                    changed = true;
                    GUI.changed = true;
                }

                EditorGUI.ToggleLeft(toggleRect, shapeName, m_BulkToggleDragValue);
                return;
            }

            bool updated = EditorGUI.ToggleLeft(toggleRect, shapeName, isEnabled);
            if (updated != isEnabled)
            {
                SetBlendShapeEnabledPreserveSourceOrder(shapeNamesProperty, allShapeNames, shapeName, updated);
                changed = true;
            }
        }

        private void DrawEditableBlendShapeList(int meshIndex, SerializedProperty meshDataProperty, SerializedProperty shapeNamesProperty)
        {
            string meshName = meshDataProperty.FindPropertyRelative(MeshData.MeshNameFieldName).stringValue;
            string[] allShapeNames = GetCachedAllBlendShapeNames(meshName);
            var enabledNames = GetEnabledBlendShapeNames(shapeNamesProperty);
            string[] visibleShapeNames = allShapeNames
                .Where(shapeName => MatchesSearch(m_MeshBlendShapeSearchTerms[meshIndex], shapeName))
                .ToArray();

            var style = new GUIStyle(GUI.skin.window);
            style.padding.top = (int)EditorGUIUtility.standardVerticalSpacing;
            EditorGUILayout.BeginVertical(style);

            EditorGUILayout.LabelField(
                Localization.SF("data.blendshape_status", shapeNamesProperty.arraySize, allShapeNames.Length, visibleShapeNames.Length),
                EditorStyles.miniLabel);

            m_MeshBlendShapeSearchTerms[meshIndex] = EditorGUILayout.TextField(
                Localization.G("data.blendshape_filter"),
                m_MeshBlendShapeSearchTerms[meshIndex]);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.G("data.enable_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, m_MeshBlendShapeSearchTerms[meshIndex], true);
            }

            if (GUILayout.Button(Localization.G("data.mute_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, m_MeshBlendShapeSearchTerms[meshIndex], false);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.G("data.enable_all_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, string.Empty, true);
            }

            if (GUILayout.Button(Localization.G("data.mute_all_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, string.Empty, false);
            }
            GUILayout.EndHorizontal();

            if (visibleShapeNames.Length == 0)
            {
                EditorGUILayout.HelpBox(Localization.S("data.blendshape_filter_no_results"), MessageType.Info);
            }
            else
            {
                for (int i = 0; i < visibleShapeNames.Length; i++)
                {
                    string shapeName = visibleShapeNames[i];
                    bool isEnabled = enabledNames.Contains(shapeName);
                    DrawBlendShapeToggle(
                        meshIndex,
                        shapeName,
                        isEnabled,
                        shapeNamesProperty,
                        allShapeNames,
                        out bool changed);
                    if (changed)
                    {
                        enabledNames = GetEnabledBlendShapeNames(shapeNamesProperty);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            HandleBulkToggleDragLifecycle();
            var dataAsset = (BlendShapeDataSO)target;

            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Separator();

            EditorGUILayout.HelpBox(Localization.S("legacy.blendshape_data.legacy_notice"), MessageType.Info);
            EditorGUILayout.Separator();

            // Keep Original FBX editable when missing so users can immediately assign a base FBX and upgrade.
            EditorGUI.BeginDisabledGroup(m_ReadOnlyMode && m_OriginalFbxProperty.objectReferenceValue != null);
            EditorWidgets.FBXGameObjectField(Localization.G("data.original_fbx"), m_OriginalFbxProperty);
            EditorGUI.EndDisabledGroup();

            if (!m_ReadOnlyMode)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField(Localization.G("data.edit_mode"), EditorStyles.boldLabel);
                var updatedEditMode = (BlendShapeEditMode)GUILayout.Toolbar(
                    (int)m_EditMode,
                    new[]
                    {
                        Localization.S("data.edit_mode.order_adjust"),
                        Localization.S("data.edit_mode.bulk_toggle")
                    });
                if (updatedEditMode != m_EditMode)
                {
                    m_EditMode = updatedEditMode;
                    EditorStatePrefs.SetBlendShapeEditMode(target, m_EditMode);
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            if (m_MeshDataListProperty != null)
            {
                for (int i = 0; i < m_MeshDataListProperty.arraySize; i++)
                {
                    SerializedProperty meshDataProperty = m_MeshDataListProperty.GetArrayElementAtIndex(i);
                    SerializedProperty meshNameProperty = meshDataProperty.FindPropertyRelative(MeshData.MeshNameFieldName);
                    SerializedProperty meshProperty = meshDataProperty.FindPropertyRelative(MeshData.OriginMeshFieldName);

                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    EditorGUI.BeginDisabledGroup(m_ReadOnlyMode);
                    if (m_OriginalFbxProperty.serializedObject.hasModifiedProperties)
                    {
                        Mesh found = FindMeshAsset(m_OriginalFbxProperty.objectReferenceValue, meshNameProperty.stringValue);
                        if (found != null)
                        {
                            meshProperty.objectReferenceValue = found;
                        }
                    }

                    EditorGUILayout.ObjectField(meshProperty, new GUIContent(meshNameProperty.stringValue));
                    EditorGUI.EndDisabledGroup();

                    var shapeNamesProperty = m_MeshBlendShapes[i].serializedProperty;
                    var blendShapesProperty = meshDataProperty.FindPropertyRelative(MeshData.BlendShapesFieldName);
                    int activeBlendShapeCount = shapeNamesProperty.arraySize;
                    int totalBlendShapeCount = blendShapesProperty.arraySize;

                    EditorGUI.indentLevel++;
                    Rect rect = EditorGUILayout.GetControlRect();
                    var foldoutLabel = m_ReadOnlyMode
                        ? new GUIContent(Localization.SF("data.blendshape_readonly_count_summary", activeBlendShapeCount))
                        : new GUIContent(Localization.SF("data.blendshape_count_summary", activeBlendShapeCount, totalBlendShapeCount));

                    if (!m_ReadOnlyMode)
                    {
                        foldoutLabel = EditorGUI.BeginProperty(rect, foldoutLabel, shapeNamesProperty);
                    }

                    shapeNamesProperty.isExpanded = EditorGUI.Foldout(rect, shapeNamesProperty.isExpanded, foldoutLabel, true);
                    if (!m_ReadOnlyMode)
                    {
                        EditorGUI.EndProperty();
                    }

                    if (shapeNamesProperty.isExpanded)
                    {
                        EditorGUILayout.BeginVertical(GUI.skin.box);
                        if (m_ReadOnlyMode)
                        {
                            m_MeshBlendShapes[i].draggable = false;
                            m_MeshBlendShapes[i].displayRemove = false;
                            m_MeshBlendShapes[i].footerHeight = 0;
                            m_MeshBlendShapes[i].DoLayoutList();
                        }
                        else if (m_EditMode == BlendShapeEditMode.Reorder)
                        {
                            m_MeshBlendShapes[i].draggable = true;
                            m_MeshBlendShapes[i].displayRemove = true;
                            m_MeshBlendShapes[i].footerHeight = EditorGUIUtility.singleLineHeight;
                            m_MeshBlendShapes[i].DoLayoutList();
                        }
                        else
                        {
                            DrawEditableBlendShapeList(i, meshDataProperty, shapeNamesProperty);
                        }
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUI.indentLevel--;

                    if (i < dataAsset.MeshDataList.Count)
                    {
                        DrawMeshSelectionSection(dataAsset, dataAsset.MeshDataList[i]);
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Separator();
                }
            }

            // Replaced legacy action buttons with one-click upgrade to .blendshare
            if (GUILayout.Button(Localization.G("legacy.blendshape_data.upgrade"), GUILayout.Height(26f)))
            {
                serializedObject.ApplyModifiedProperties();
                var fbx = m_OriginalFbxProperty.objectReferenceValue as GameObject;
                if (fbx == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }

                string sourcePath = AssetDatabase.GetAssetPath(dataAsset);
                string outputPath;
                if (LegacyPatchMigrator.IsWritableAssetLocation(sourcePath))
                {
                    outputPath = LegacyPatchMigrator.GetDefaultOutputAssetPath(dataAsset, uniqueIfExists: true);
                }
                else
                {
                    outputPath = EditorUtility.SaveFilePanelInProject(
                        Localization.S("legacy.blendshape_data.upgrade"),
                        dataAsset.name,
                        "blendshare",
                        Localization.S("data.save_file.message"),
                        "Assets");
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        return;
                    }
                }

                var result = LegacyPatchMigrator.MigrateToBlendShare(dataAsset, fbx, outputPath);
                for (int d = 0; d < result.Diagnostics.Count; d++)
                {
                    var diag = result.Diagnostics[d];
                    if (diag.Severity == LegacyMigrationSeverity.Error)
                    {
                        Debug.LogError($"[BlendShare] {diag.Message}", dataAsset);
                    }
                    else
                    {
                        Debug.LogWarning($"[BlendShare] {diag.Message}", dataAsset);
                    }
                }

                if (result.Succeeded && result.ImportedAsset != null)
                {
                    bool hasWarnings = result.Diagnostics.Any(d => d.Severity == LegacyMigrationSeverity.Warning);
                    if (hasWarnings)
                    {
                        EditorUtility.DisplayDialog(
                            Localization.S("legacy.blendshape_data.upgrade"),
                            Localization.S("legacy.blendshape_data.upgrade_completed_with_warnings"),
                            Localization.S("data.dialog.ok"));
                    }

                    EditorGUIUtility.PingObject(result.ImportedAsset);
                    Selection.activeObject = result.ImportedAsset;
                    GUIUtility.ExitGUI();
                }
                else if (result.ImportedAsset != null)
                {
                    EditorGUIUtility.PingObject(result.ImportedAsset);
                    EditorUtility.DisplayDialog(
                        Localization.S("legacy.blendshape_data.upgrade"),
                        Localization.S("legacy.blendshape_data.upgrade_partial_settings_failed"),
                        Localization.S("data.dialog.ok"));
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        Localization.S("legacy.blendshape_data.upgrade"),
                        Localization.S("legacy.blendshape_data.upgrade_failed"),
                        Localization.S("data.dialog.ok"));
                }
            }

            EditorGUILayout.Separator();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(m_ReadOnlyMode);
            if (GUILayout.Button(Localization.G("data.reset_blendshapes")))
            {
                for (int i = 0; i < m_MeshDataListProperty.arraySize; i++)
                {
                    SerializedProperty meshDataProperty = m_MeshDataListProperty.GetArrayElementAtIndex(i);
                    var blendShapesProp = meshDataProperty.FindPropertyRelative(MeshData.BlendShapesFieldName);
                    var names = new List<string>(blendShapesProp.arraySize);
                    for (int j = 0; j < blendShapesProp.arraySize; j++)
                    {
                        var prop = blendShapesProp.GetArrayElementAtIndex(j).FindPropertyRelative(BlendShapeWrapper.ShapeNameFieldName);
                        names.Add(prop.stringValue);
                    }

                    dataAsset.MeshDataList[i].SetWorkingShapeNames(names);
                }

                serializedObject.Update();
                EditorUtility.SetDirty(dataAsset);
                AssetDatabase.SaveAssets();
                OnEnable();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button(
                    m_ReadOnlyMode ? Localization.G("data.edit") : Localization.G("data.lock"),
                    GUILayout.ExpandWidth(false)))
            {
                m_ReadOnlyMode = !m_ReadOnlyMode;
                EditorStatePrefs.SetEditingEnabled(target, !m_ReadOnlyMode);
            }

            GUILayout.EndHorizontal();

            if (!m_ReadOnlyMode)
            {
                EditorGUILayout.Separator();
                GUILayout.BeginHorizontal(GUI.skin.box);
                EditorGUILayout.LabelField(Localization.G("data.created_by"), EditorStyles.centeredGreyMiniLabel);
                GUILayout.EndHorizontal();

                EditorGUILayout.Separator();
                EditorGUILayout.LabelField(Localization.G("data.hidden_settings"), EditorStyles.boldLabel);

                GUILayout.BeginVertical(GUI.skin.box);
                var defaultAssetName = serializedObject.FindProperty(BlendShapeDataSO.DefaultGeneratedAssetNameFieldName);
                EditorGUILayout.PropertyField(defaultAssetName, Localization.G("data.hidden_settings.default_asset_name"));
                EditorGUILayout.PropertyField(m_AppliedProperty, Localization.G("data.hidden_settings.applied"));

                var deformerId = serializedObject.FindProperty(BlendShapeDataSO.DeformerIdFieldName);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(deformerId, Localization.G("data.hidden_settings.deformer_id"));
                EditorGUI.EndDisabledGroup();
                GUILayout.EndVertical();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static Mesh FindMeshAsset(Object root, string meshName)
        {
            string path = AssetDatabase.GetAssetPath(root);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            for (int i = 0; i < subAssets.Length; i++)
            {
                if (subAssets[i] is Mesh mesh && string.Equals(mesh.name, meshName, StringComparison.Ordinal))
                {
                    return mesh;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Original BlendShare 1.x IMGUI Inspector for <see cref="BlendShapeSelectionSO"/> sub-assets.
    /// </summary>
    [CustomEditor(typeof(BlendShapeSelectionSO))]
    public sealed class BlendShapeSelectionSOEditor : UnityEditor.Editor
    {
        private SerializedProperty m_MeshNameProperty;
        private SerializedProperty m_BlendShapeNamesProperty;

        private void OnEnable()
        {
            m_MeshNameProperty = serializedObject.FindProperty(BlendShapeSelectionSO.MeshNameFieldName);
            m_BlendShapeNamesProperty = serializedObject.FindProperty(BlendShapeSelectionSO.BlendShapeNamesFieldName);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var selection = (BlendShapeSelectionSO)target;

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.G("blendshape_selection.title"), EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            EditorGUI.BeginChangeCheck();
            string updatedName = EditorGUILayout.TextField(Localization.G("blendshape_selection.name"), selection.name);
            if (EditorGUI.EndChangeCheck())
            {
                updatedName = updatedName?.Trim();
                if (!string.IsNullOrWhiteSpace(updatedName) && !string.Equals(updatedName, selection.name, StringComparison.Ordinal))
                {
                    selection.name = updatedName;
                    EditorUtility.SetDirty(selection);
                    AssetDatabase.SaveAssets();
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(m_MeshNameProperty, Localization.G("blendshape_selection.mesh_name"));
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.PropertyField(m_BlendShapeNamesProperty, Localization.G("blendshape_selection.blendshape_names"), true);

            if (serializedObject.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(selection);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }
    }

    /// <summary>
    /// Original BlendShare 1.x IMGUI Inspector for <see cref="GeneratedMeshAssetSO"/> containers.
    /// </summary>
    [CustomEditor(typeof(GeneratedMeshAssetSO))]
    public sealed class GeneratedMeshAssetSOEditor : UnityEditor.Editor
    {
        private GameObject m_SelectedGameObject;
        private string m_PrefKey;
        private SerializedProperty m_OriginalFbxProp;
        private SerializedProperty m_OriginalHashProp;
        private SerializedProperty m_AppliedBlendShapesProp;
        private string m_CurrentFbxHash = string.Empty;

        private void OnEnable()
        {
            m_OriginalFbxProp = serializedObject.FindProperty("m_OriginalFbxAsset");
            m_OriginalHashProp = serializedObject.FindProperty("m_OriginalFbxHash");
            m_AppliedBlendShapesProp = serializedObject.FindProperty("m_AppliedBlendShapes");

            m_CurrentFbxHash = CalculateHash(m_OriginalFbxProp.objectReferenceValue);
            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path))
            {
                path = "GeneratedMeshAssetSO_" + target.GetInstanceID();
            }

            m_PrefKey = $"GeneratedMeshAssetSO_EditMode_{path}";
        }

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label(Localization.S("legacy.generated_mesh_asset.description", "A mesh assets container"));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            serializedObject.Update();
            EditorGUILayout.Space();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.S("legacy.generated_mesh_asset.metadata", "Metadata"), EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(GUI.skin.box);
            bool isEditing = EditorPrefs.GetBool(m_PrefKey, false);
            EditorGUI.BeginDisabledGroup(!isEditing);

            if (EditorWidgets.FBXGameObjectField(Localization.G("data.original_fbx"), m_OriginalFbxProp))
            {
                m_CurrentFbxHash = CalculateHash(m_OriginalFbxProp.objectReferenceValue);
            }

            if (m_OriginalFbxProp.objectReferenceValue != null &&
                !string.IsNullOrEmpty(m_CurrentFbxHash) &&
                m_OriginalHashProp.stringValue != m_CurrentFbxHash)
            {
                bool prevEnabled = GUI.enabled;
                GUI.enabled = true;
                EditorGUILayout.HelpBox(Localization.S("mesh_asset.fbx_edited"), MessageType.Warning);
                GUI.enabled = prevEnabled;

                EditorGUILayout.TextField(Localization.S("legacy.generated_mesh_asset.original", "Original"), m_OriginalHashProp.stringValue);
                EditorGUILayout.TextField(Localization.S("legacy.generated_mesh_asset.current", "Current"), m_CurrentFbxHash);
            }

            if (isEditing)
            {
                EditorGUILayout.PropertyField(m_AppliedBlendShapesProp, Localization.G("mesh_asset.applied_blendshapes"), true);
            }
            else if (m_AppliedBlendShapesProp.arraySize > 0)
            {
                EditorGUILayout.LabelField(Localization.G("mesh_asset.applied_blendshapes"), EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                for (int i = 0; i < m_AppliedBlendShapesProp.arraySize; i++)
                {
                    var element = m_AppliedBlendShapesProp.GetArrayElementAtIndex(i);
                    var obj = element.objectReferenceValue;
                    if (obj != null)
                    {
                        var so = obj as BlendShapeDataSO;
                        string label = so != null ? so.DeformerId : obj.name;
                        EditorGUILayout.ObjectField(label, obj, typeof(BlendShapeDataSO), false);
                    }
                    else
                    {
                        EditorGUILayout.LabelField($"[{i}] (null)");
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();

            if (!isEditing)
            {
                EditorGUILayout.HelpBox(Localization.S("mesh_asset.read_only"), MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (!isEditing)
            {
                if (GUILayout.Button(Localization.G("data.edit"), GUILayout.Width(80)))
                {
                    EditorPrefs.SetBool(m_PrefKey, true);
                }
            }
            else
            {
                if (GUILayout.Button(Localization.G("data.lock"), GUILayout.Width(80)))
                {
                    serializedObject.ApplyModifiedProperties();
                    EditorPrefs.SetBool(m_PrefKey, false);
                    EditorUtility.SetDirty(target);
                    AssetDatabase.SaveAssets();
                }
            }
            EditorGUILayout.EndHorizontal();
            serializedObject.ApplyModifiedProperties();

            GUILayout.Space(10);
            EditorGUILayout.LabelField(Localization.G("mesh_asset.assign_meshes"), EditorStyles.boldLabel);

            m_SelectedGameObject = (GameObject)EditorGUILayout.ObjectField(
                Localization.G("mesh_asset.assign_meshes.target_go"),
                m_SelectedGameObject,
                typeof(GameObject),
                true);

            bool isPrefab = m_SelectedGameObject != null && EditorUtility.IsPersistent(m_SelectedGameObject);
            if (isPrefab)
            {
                if (EditorWidgets.IsFBXGameObject(m_SelectedGameObject))
                {
                    m_SelectedGameObject = null;
                    Localization.DisplayDialog("mesh_asset.assign_meshes.dialog.is_fbx", Localization.S("data.dialog.ok"));
                }
                else
                {
                    EditorGUILayout.HelpBox(Localization.S("mesh_asset.assign_meshes.is_prefab"), MessageType.Warning);
                }
            }

            GUI.enabled = m_SelectedGameObject != null;
            if (GUILayout.Button(isPrefab
                    ? Localization.G("mesh_asset.assign_meshes.to_prefab_btn")
                    : Localization.G("mesh_asset.assign_meshes.to_go_btn")))
            {
                if (m_SelectedGameObject != null)
                {
                    ApplySubAssetMeshesToTarget((GeneratedMeshAssetSO)target, m_SelectedGameObject.transform);
                }

                m_SelectedGameObject = null;
            }
            GUI.enabled = true;
        }

        private static void ApplySubAssetMeshesToTarget(GeneratedMeshAssetSO containerAsset, Transform targetRoot)
        {
            string assetPath = AssetDatabase.GetAssetPath(containerAsset);
            if (string.IsNullOrEmpty(assetPath) || targetRoot == null)
            {
                return;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
            var meshRenderers = new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
            foreach (var renderer in targetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string key = renderer.sharedMesh != null ? renderer.sharedMesh.name : renderer.gameObject.name;
                if (!meshRenderers.TryGetValue(key, out var list))
                {
                    list = new List<SkinnedMeshRenderer>();
                    meshRenderers[key] = list;
                }
                list.Add(renderer);
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"[BlendShare] Apply Meshes to {targetRoot.name}");
            for (int i = 0; i < subAssets.Length; i++)
            {
                if (subAssets[i] is Mesh mesh)
                {
                    if (meshRenderers.TryGetValue(mesh.name, out var renderers))
                    {
                        if (renderers.Count > 1)
                        {
                            Debug.LogWarning(
                                $"[BlendShare Apply Mesh] Multiple renderers share the same mesh name '{mesh.name}' under '{targetRoot.name}'.",
                                targetRoot);
                        }

                        foreach (var targetMeshRenderer in renderers)
                        {
                            Undo.RecordObject(targetMeshRenderer, "Apply Mesh");
                            targetMeshRenderer.sharedMesh = mesh;
                            EditorUtility.SetDirty(targetMeshRenderer);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[BlendShare Apply Mesh] Mesh '{mesh.name}' not found in target '{targetRoot.name}'.", targetRoot);
                    }
                }
            }
            Undo.CollapseUndoOperations(undoGroup);
        }

        private static string CalculateHash(Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(assetPath);
            if (string.IsNullOrEmpty(physicalPath) || !File.Exists(physicalPath))
            {
                return string.Empty;
            }

            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).ToLowerInvariant().Replace("-", string.Empty);
            }
        }
    }

    internal sealed class BlendShapeSelectionNamePrompt : EditorWindow
    {
        private string m_Value;
        private Action<string> m_OnSubmit;

        public static void Show(string title, string initialValue, Action<string> onSubmit)
        {
            var window = CreateInstance<BlendShapeSelectionNamePrompt>();
            window.titleContent = new GUIContent(title);
            window.m_Value = initialValue ?? string.Empty;
            window.m_OnSubmit = onSubmit;
            window.minSize = new Vector2(320f, 86f);
            window.maxSize = window.minSize;
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(Localization.G("data.blendshape_selection.name_prompt"), EditorStyles.boldLabel);
            GUI.SetNextControlName("SelectionNameField");
            m_Value = EditorGUILayout.TextField(m_Value);

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.S("data.dialog.cancel")))
            {
                Close();
                GUIUtility.ExitGUI();
            }

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(m_Value));
            if (GUILayout.Button(Localization.S("data.dialog.save")))
            {
                string trimmedValue = m_Value.Trim();
                Close();
                m_OnSubmit?.Invoke(trimmedValue);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl("SelectionNameField");
            }
        }
    }

    internal static class LegacySelectionUtility
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
                .GroupBy(selection => selection.MeshName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(selection => selection.DisplayName, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
        }

        public static List<string> SanitizeSelectionShapeNames(
            MeshData meshData,
            IEnumerable<string> shapeNames,
            Object logContext,
            string selectionLabel)
        {
            var allShapeNames = new HashSet<string>(meshData.GetAllBlendShapeNames(), StringComparer.Ordinal);
            var validNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string shapeName in shapeNames ?? Enumerable.Empty<string>())
            {
                if (!allShapeNames.Contains(shapeName))
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selectionLabel}' references missing blendshape '{shapeName}' on mesh '{meshData.MeshName}'.",
                        logContext);
                    continue;
                }

                if (!seen.Add(shapeName))
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selectionLabel}' contains duplicate blendshape '{shapeName}' on mesh '{meshData.MeshName}'.",
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
                return new List<string>(meshData.ShapeNames);
            }

            if (!string.Equals(selection.MeshName, meshData.MeshName, StringComparison.Ordinal))
            {
                if (logWarnings)
                {
                    Debug.LogWarning(
                        $"[BlendShare] Selection '{selection.DisplayName}' targets mesh '{selection.MeshName}', but was used with '{meshData.MeshName}'.",
                        dataAsset);
                }

                return new List<string>(meshData.ShapeNames);
            }

            if (logWarnings)
            {
                return SanitizeSelectionShapeNames(meshData, selection.BlendShapeNames, dataAsset, selection.DisplayName);
            }

            var allSet = new HashSet<string>(meshData.GetAllBlendShapeNames(), StringComparer.Ordinal);
            return selection.BlendShapeNames
                .Where(allSet.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public static string GetNextSelectionName(IEnumerable<BlendShapeSelectionSO> existingSelections, string meshName)
        {
            string baseName = string.IsNullOrWhiteSpace(meshName) ? "Selection" : $"{meshName} Selection";
            var usedNames = new HashSet<string>(existingSelections.Select(selection => selection.DisplayName), StringComparer.Ordinal);
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
