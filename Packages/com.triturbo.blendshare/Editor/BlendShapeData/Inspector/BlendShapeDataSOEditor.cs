using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using UnityEditor;
using UnityEditorInternal;
using System.Drawing;

//
namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CustomEditor(typeof(BlendShapeDataSO))]
    public class BlendShapeDataSOEditor : Editor
    {
        private enum BlendShapeEditMode
        {
            Reorder = 0,
            Toggle = 1
        }

        private const string BlendShapesPropertyName = "m_BlendShapes";
        private List<ReorderableList> meshBlendShapes;
        private SerializedProperty meshDataListProperty;
        private SerializedProperty originalFbxProperty;
        private SerializedProperty appliedProperty;
        private bool readOnlyMode = true;
        private BlendShapeEditMode editMode = BlendShapeEditMode.Reorder;
        private List<string> meshBlendShapeSearchTerms;
        private BlendShapeMeshGeneratorWindow advancedGeneratorWindow = null;
        private Dictionary<string, List<BlendShapeSelectionSO>> meshSelectionsByMeshName;
        private Dictionary<string, BlendShapeSelectionSO> workingSelectionSourcesByMeshName;
        private Dictionary<string, string[]> cachedAllBlendShapeNamesByMeshName;
        private bool bulkToggleDragActive;
        private bool bulkToggleDragValue;
        private int bulkToggleDragMeshIndex = -1;
        private Vector2 bulkToggleDragStartPosition;

        private static class EditorStatePrefs
        {
            private const bool DefaultEditingEnabled = false;
            private const BlendShapeEditMode DefaultBlendShapeEditMode = BlendShapeEditMode.Reorder;
            private const string KeyPrefix = "Triturbo.BlendShapeShare.BlendShapeDataSO.";
            private const string EditingEnabledKeyPrefix = KeyPrefix + "EditingEnabled.";
            private const string BlendShapeEditModeKeyPrefix = KeyPrefix + "BlendShapeEditMode.";

            public static bool GetEditingEnabled(UnityEngine.Object editorTarget)
            {
                return EditorPrefs.GetBool(GetEditingEnabledKey(editorTarget), DefaultEditingEnabled);
            }

            public static void SetEditingEnabled(UnityEngine.Object editorTarget, bool editingEnabled)
            {
                var key = GetEditingEnabledKey(editorTarget);
                if (editingEnabled == DefaultEditingEnabled)
                {
                    EditorPrefs.DeleteKey(key);
                    return;
                }

                EditorPrefs.SetBool(key, editingEnabled);
            }

            public static BlendShapeEditMode GetBlendShapeEditMode(UnityEngine.Object editorTarget)
            {
                var key = GetBlendShapeEditModeKey(editorTarget);
                int savedValue = EditorPrefs.GetInt(key, (int)DefaultBlendShapeEditMode);
                return System.Enum.IsDefined(typeof(BlendShapeEditMode), savedValue)
                    ? (BlendShapeEditMode)savedValue
                    : DefaultBlendShapeEditMode;
            }

            public static void SetBlendShapeEditMode(UnityEngine.Object editorTarget, BlendShapeEditMode editMode)
            {
                var key = GetBlendShapeEditModeKey(editorTarget);
                if (editMode == DefaultBlendShapeEditMode)
                {
                    EditorPrefs.DeleteKey(key);
                    return;
                }

                EditorPrefs.SetInt(key, (int)editMode);
            }

            private static string GetEditingEnabledKey(UnityEngine.Object editorTarget)
            {
                return EditingEnabledKeyPrefix + GetAssetKey(editorTarget);
            }

            private static string GetBlendShapeEditModeKey(UnityEngine.Object editorTarget)
            {
                return BlendShapeEditModeKeyPrefix + GetAssetKey(editorTarget);
            }

            private static string GetAssetKey(UnityEngine.Object editorTarget)
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

                var path = AssetDatabase.GetAssetPath(editorTarget);
                if (!string.IsNullOrEmpty(path))
                {
                    var pathGuid = AssetDatabase.AssetPathToGUID(path);
                    return string.IsNullOrEmpty(pathGuid) ? path : pathGuid;
                }

                return editorTarget.GetInstanceID().ToString();
            }
        }

        private void ShowContextMenu(ReorderableList reorderableList, int index)
        {
            GenericMenu menu = new GenericMenu();

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
            meshDataListProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_MeshDataList));
            originalFbxProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_Original));
            appliedProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_Applied));
            meshSelectionsByMeshName = new Dictionary<string, List<BlendShapeSelectionSO>>();
            workingSelectionSourcesByMeshName = new Dictionary<string, BlendShapeSelectionSO>();
            
            meshBlendShapeSearchTerms = new List<string>(meshDataListProperty.arraySize);
            // Get the target object
            BlendShapeDataSO dataAsset = (BlendShapeDataSO)target;
            RefreshAllBlendShapeNameCache();
            readOnlyMode = !EditorStatePrefs.GetEditingEnabled(target);
            editMode = EditorStatePrefs.GetBlendShapeEditMode(target);
            RefreshMeshSelections(dataAsset);

            meshBlendShapes = new List<ReorderableList>(dataAsset.m_MeshDataList.Count);
            for (int i = 0; i < meshDataListProperty.arraySize; i++)
            {
                var meshDataProperty = meshDataListProperty.GetArrayElementAtIndex(i);
                var blendShapeNamesProperty = meshDataProperty.FindPropertyRelative(nameof(MeshData.m_ShapeNames));

                var reorderableList = new ReorderableList(blendShapeNamesProperty.serializedObject,
                    blendShapeNamesProperty, false, false, false, false);

                reorderableList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
                {
                    var blendShapeProperty = blendShapeNamesProperty.GetArrayElementAtIndex(index);
                    EditorGUI.LabelField(rect, blendShapeProperty.stringValue);
#if UNITY_2022
                    if (!readOnlyMode && editMode == BlendShapeEditMode.Reorder &&
                        Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
                    {
                        if (reorderableList.IsSelected(index))
                        {
                            ShowContextMenu(reorderableList, index);
                            Event.current.Use();
                        }
                    }
#endif
                };
                reorderableList.footerHeight = 0;
                meshBlendShapes.Add(reorderableList);
                meshBlendShapeSearchTerms.Add(string.Empty);
            }
        }

        private void RefreshAllBlendShapeNameCache()
        {
            cachedAllBlendShapeNamesByMeshName = new Dictionary<string, string[]>();

            if (meshDataListProperty == null)
            {
                return;
            }

            for (int i = 0; i < meshDataListProperty.arraySize; i++)
            {
                CacheAllBlendShapeNames(meshDataListProperty.GetArrayElementAtIndex(i));
            }
        }

        private static string GetBlendShapeCacheKey(string meshName)
        {
            return meshName ?? string.Empty;
        }

        private string[] CacheAllBlendShapeNames(SerializedProperty meshDataProperty)
        {
            if (meshDataProperty == null)
            {
                return System.Array.Empty<string>();
            }

            var meshName = meshDataProperty.FindPropertyRelative(nameof(MeshData.m_MeshName)).stringValue;
            var blendShapesProperty = meshDataProperty.FindPropertyRelative(BlendShapesPropertyName);
            var allBlendShapeNames = new string[blendShapesProperty.arraySize];

            for (int i = 0; i < blendShapesProperty.arraySize; i++)
            {
                allBlendShapeNames[i] = GetBlendShapeName(blendShapesProperty.GetArrayElementAtIndex(i));
            }

            cachedAllBlendShapeNamesByMeshName[GetBlendShapeCacheKey(meshName)] = allBlendShapeNames;
            return allBlendShapeNames;
        }

        private string[] GetCachedAllBlendShapeNames(string meshName)
        {
            cachedAllBlendShapeNamesByMeshName ??= new Dictionary<string, string[]>();
            return cachedAllBlendShapeNamesByMeshName.TryGetValue(GetBlendShapeCacheKey(meshName), out var allBlendShapeNames)
                ? allBlendShapeNames
                : System.Array.Empty<string>();
        }

        private string[] GetCachedAllBlendShapeNames(MeshData meshData)
        {
            return GetCachedAllBlendShapeNames(meshData?.m_MeshName);
        }

        private List<string> ResolveSelectionShapeNamesUsingCache(MeshData meshData, BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return meshData?.m_ShapeNames != null
                    ? new List<string>(meshData.m_ShapeNames)
                    : new List<string>();
            }

            if (!string.Equals(selection.m_MeshName, meshData.m_MeshName))
            {
                return new List<string>(meshData.m_ShapeNames);
            }

            var allBlendShapeNames = GetCachedAllBlendShapeNames(meshData);
            return (selection.m_BlendShapeNames ?? new List<string>())
                .Where(shapeName => allBlendShapeNames.Contains(shapeName))
                .Distinct()
                .ToList();
        }

        private void RefreshMeshSelections(BlendShapeDataSO dataAsset)
        {
            meshSelectionsByMeshName = BlendShapeSelectionUtility.GroupSelectionsByMesh(dataAsset);

            if (workingSelectionSourcesByMeshName == null)
            {
                workingSelectionSourcesByMeshName = new Dictionary<string, BlendShapeSelectionSO>();
            }

            var validSelections = new HashSet<BlendShapeSelectionSO>(meshSelectionsByMeshName.Values.SelectMany(list => list));
            var invalidKeys = workingSelectionSourcesByMeshName
                .Where(entry => entry.Value == null || !validSelections.Contains(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var key in invalidKeys)
            {
                workingSelectionSourcesByMeshName.Remove(key);
            }
        }

        private List<BlendShapeSelectionSO> GetMeshSelections(string meshName)
        {
            return meshSelectionsByMeshName != null &&
                   meshSelectionsByMeshName.TryGetValue(meshName ?? string.Empty, out var selections)
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
                if (!string.Equals(currentShapeNames[i], selectionShapeNames[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private int GetCurrentPopupIndex(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            IReadOnlyList<BlendShapeSelectionSO> selections)
        {
            if (HasSameShapeNames(meshData.m_ShapeNames, GetCachedAllBlendShapeNames(meshData)))
            {
                return 0;
            }

            for (int i = 0; i < selections.Count; i++)
            {
                var resolvedNames = ResolveSelectionShapeNamesUsingCache(meshData, selections[i]);

                if (HasSameShapeNames(meshData.m_ShapeNames, resolvedNames))
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
            var selections = GetMeshSelections(meshData.m_MeshName);
            string selectionName = existingSelection != null
                ? existingSelection.DisplayName
                : selectionNameOverride;

            if (string.IsNullOrWhiteSpace(selectionName))
            {
                selectionName = BlendShapeSelectionUtility.GetNextSelectionName(selections, meshData.m_MeshName);
            }

            selectionName = selectionName.Trim();
            var sanitizedNames = BlendShapeSelectionUtility.SanitizeSelectionShapeNames(
                meshData,
                meshData.m_ShapeNames,
                dataAsset,
                selectionName);

            BlendShapeSelectionSO selection = existingSelection;
            if (selection == null)
            {
                selection = CreateInstance<BlendShapeSelectionSO>();
                selection.name = selectionName;
                AssetDatabase.AddObjectToAsset(selection, dataAsset);
            }

            selection.m_MeshName = meshData.m_MeshName;
            selection.m_BlendShapeNames = sanitizedNames;
            selection.name = selectionName;

            EditorUtility.SetDirty(selection);
            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();

            RefreshMeshSelections(dataAsset);
            SetWorkingSelectionSource(meshData.m_MeshName, selection);
            return selection;
        }

        private void LoadShapeNamesIntoWorkingSet(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            IReadOnlyList<string> shapeNames)
        {
            meshData.m_ShapeNames = new List<string>(shapeNames ?? System.Array.Empty<string>());
            serializedObject.Update();

            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();
            Repaint();
        }

        private BlendShapeSelectionSO GetWorkingSelectionSource(string meshName)
        {
            if (workingSelectionSourcesByMeshName != null &&
                workingSelectionSourcesByMeshName.TryGetValue(meshName, out var selection))
            {
                return selection;
            }

            return null;
        }

        private void SetWorkingSelectionSource(string meshName, BlendShapeSelectionSO selection)
        {
            if (workingSelectionSourcesByMeshName == null)
            {
                workingSelectionSourcesByMeshName = new Dictionary<string, BlendShapeSelectionSO>();
            }

            if (selection == null)
            {
                workingSelectionSourcesByMeshName.Remove(meshName);
                return;
            }

            workingSelectionSourcesByMeshName[meshName] = selection;
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

            var resolvedNames = BlendShapeSelectionUtility.ResolveSelectionShapeNames(dataAsset, meshData, selection, true);
            LoadShapeNamesIntoWorkingSet(dataAsset, meshData, resolvedNames);
            SetWorkingSelectionSource(meshData.m_MeshName, selection);
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
            SetWorkingSelectionSource(meshData.m_MeshName, null);
        }

        private void PromptSaveMeshSelection(BlendShapeDataSO dataAsset, MeshData meshData, System.Action onSaved = null)
        {
            string defaultName = BlendShapeSelectionUtility.GetNextSelectionName(
                GetMeshSelections(meshData.m_MeshName),
                meshData.m_MeshName);

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
                SetWorkingSelectionSource(meshData.m_MeshName, null);
                return;
            }

            int selectionIndex = popupIndex - 1;
            if (selectionIndex >= 0 && selectionIndex < selections.Count)
            {
                LoadMeshSelectionIntoWorkingSet(dataAsset, meshData, selections[selectionIndex]);
            }
        }

        private bool HasSelectionIssues(BlendShapeDataSO dataAsset, MeshData meshData, BlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return false;
            }

            if (!string.Equals(selection.m_MeshName, meshData.m_MeshName))
            {
                return true;
            }

            var allShapeNames = GetCachedAllBlendShapeNames(meshData);
            return selection.m_BlendShapeNames
                .GroupBy(shapeName => shapeName)
                .Any(group => group.Count() > 1 || !allShapeNames.Contains(group.Key));
        }

        private void DrawMeshSelectionSection(
            BlendShapeDataSO dataAsset,
            MeshData meshData)
        {
            string meshName = meshData.m_MeshName;
            var selections = GetMeshSelections(meshName);
            if (readOnlyMode && selections.Count == 0)
            {
                return;
            }

            var workingSelectionSource = GetWorkingSelectionSource(meshName);
            int currentPopupIndex = GetCurrentPopupIndex(dataAsset, meshData, selections);
            bool showCurrentWorkingSet = currentPopupIndex == selections.Count + 1;
            int workingSelectionSourceIndex = workingSelectionSource != null
                ? selections.FindIndex(selection => selection == workingSelectionSource) + 1
                : -1;
            int displayedPopupIndex =
                showCurrentWorkingSet && workingSelectionSourceIndex > 0
                    ? workingSelectionSourceIndex
                    : currentPopupIndex;

            var optionNames = new[] { Localization.S("data.blendshape_selection.all") }
                .Concat(selections.Select(selection =>
                    showCurrentWorkingSet && selection == workingSelectionSource
                        ? $"{selection.DisplayName}*"
                        : selection.DisplayName))
                .Concat(showCurrentWorkingSet && workingSelectionSourceIndex <= 0
                    ? new[] { Localization.S("data.blendshape_selection.unsaved_working_set") }
                    : System.Array.Empty<string>())
                .ToArray();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField(Localization.G("data.blendshape_selection.title"), EditorStyles.boldLabel);

            Rect selectionRowRect = EditorGUILayout.GetControlRect();
            Rect popupRect = EditorGUI.PrefixLabel(
                selectionRowRect,
                GUIUtility.GetControlID(FocusType.Passive),
                Localization.G("data.blendshape_selection.active"));

            int updatedSelectionIndex = EditorGUI.Popup(
                popupRect,
                Mathf.Clamp(displayedPopupIndex, 0, Mathf.Max(0, optionNames.Length - 1)),
                optionNames);

            if (updatedSelectionIndex != displayedPopupIndex)
            {
                bool isUnsavedWorkingState = currentPopupIndex == selections.Count + 1 &&
                                            meshData.m_ShapeNames != null &&
                                            meshData.m_ShapeNames.Count > 0;
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

            BlendShapeSelectionSO selectedSelection =
                displayedPopupIndex > 0 && displayedPopupIndex <= selections.Count
                    ? selections[displayedPopupIndex - 1]
                    : null;

            if (selectedSelection != null)
            {
                SetWorkingSelectionSource(meshName, selectedSelection);

                if (HasSelectionIssues(dataAsset, meshData, selectedSelection))
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



            EditorGUI.BeginDisabledGroup(readOnlyMode);
            if (!readOnlyMode)
            {
                if (showCurrentWorkingSet)
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
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private static string GetBlendShapeName(SerializedProperty blendShapeWrapperProperty)
        {
            return blendShapeWrapperProperty.FindPropertyRelative(nameof(BlendShapeWrapper.m_ShapeName)).stringValue;
        }

        private static HashSet<string> GetEnabledBlendShapeNames(SerializedProperty shapeNamesProperty)
        {
            var enabledNames = new HashSet<string>();
            for (int i = 0; i < shapeNamesProperty.arraySize; i++)
            {
                enabledNames.Add(shapeNamesProperty.GetArrayElementAtIndex(i).stringValue);
            }

            return enabledNames;
        }

        private static List<string> GetShapeNamesInOrder(SerializedProperty shapeNamesProperty)
        {
            var shapeNames = new List<string>(shapeNamesProperty.arraySize);
            for (int i = 0; i < shapeNamesProperty.arraySize; i++)
            {
                shapeNames.Add(shapeNamesProperty.GetArrayElementAtIndex(i).stringValue);
            }

            return shapeNames;
        }

        private static void RewriteShapeNames(SerializedProperty shapeNamesProperty, IEnumerable<string> orderedShapeNames)
        {
            shapeNamesProperty.ClearArray();

            int index = 0;
            foreach (var shapeName in orderedShapeNames)
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

            return shapeName.IndexOf(searchTerm, System.StringComparison.OrdinalIgnoreCase) >= 0;
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

            foreach (var shapeName in allShapeNames)
            {
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
            bulkToggleDragActive = false;
            bulkToggleDragMeshIndex = -1;
        }

        private void HandleBulkToggleDragLifecycle()
        {
            var currentEvent = Event.current;
            if (!bulkToggleDragActive)
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

            if (currentEvent.type == EventType.MouseLeaveWindow ||
                currentEvent.type == EventType.Ignore)
            {
                ResetBulkToggleDrag();
            }
        }

        private bool IsToggleInsideBulkDragRange(int meshIndex, Rect toggleRect, Vector2 currentMousePosition)
        {
            if (!bulkToggleDragActive || bulkToggleDragMeshIndex != meshIndex)
            {
                return false;
            }

            float minY = Mathf.Min(bulkToggleDragStartPosition.y, currentMousePosition.y);
            float maxY = Mathf.Max(bulkToggleDragStartPosition.y, currentMousePosition.y);
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
                bulkToggleDragActive = true;
                bulkToggleDragValue = !isEnabled;
                bulkToggleDragMeshIndex = meshIndex;
                bulkToggleDragStartPosition = currentEvent.mousePosition;

                SetBlendShapeEnabledPreserveSourceOrder(
                    shapeNamesProperty,
                    allShapeNames,
                    shapeName,
                    bulkToggleDragValue);

                changed = true;
                currentEvent.Use();
                GUI.changed = true;
                EditorGUI.ToggleLeft(toggleRect, shapeName, bulkToggleDragValue);
                return;
            }

            if (currentEvent.type == EventType.MouseDrag &&
                currentEvent.button == 0 &&
                IsToggleInsideBulkDragRange(meshIndex, toggleRect, currentEvent.mousePosition))
            {
                if (isEnabled != bulkToggleDragValue)
                {
                    SetBlendShapeEnabledPreserveSourceOrder(
                        shapeNamesProperty,
                        allShapeNames,
                        shapeName,
                        bulkToggleDragValue);

                    changed = true;
                    GUI.changed = true;
                }

                EditorGUI.ToggleLeft(toggleRect, shapeName, bulkToggleDragValue);
                return;
            }

            bool updated = EditorGUI.ToggleLeft(toggleRect, shapeName, isEnabled);
            if (updated != isEnabled)
            {
                SetBlendShapeEnabledPreserveSourceOrder(
                    shapeNamesProperty,
                    allShapeNames,
                    shapeName,
                    updated);

                changed = true;
            }
        }

        private void DrawEditableBlendShapeList(int meshIndex, SerializedProperty meshDataProperty, SerializedProperty shapeNamesProperty)
        {
            var meshName = meshDataProperty.FindPropertyRelative(nameof(MeshData.m_MeshName)).stringValue;
            var allShapeNames = GetCachedAllBlendShapeNames(meshName);
            var enabledNames = GetEnabledBlendShapeNames(shapeNamesProperty);
            var visibleShapeNames = allShapeNames
                .Where(shapeName => MatchesSearch(meshBlendShapeSearchTerms[meshIndex], shapeName))
                .ToArray();

            var style = new GUIStyle(GUI.skin.window);
            style.padding.top = (int)EditorGUIUtility.standardVerticalSpacing;
            EditorGUILayout.BeginVertical(style);

            EditorGUILayout.LabelField(
                Localization.SF("data.blendshape_status", shapeNamesProperty.arraySize, allShapeNames.Length, visibleShapeNames.Length),
                EditorStyles.miniLabel);

            meshBlendShapeSearchTerms[meshIndex] = EditorGUILayout.TextField(
                Localization.G("data.blendshape_filter"),
                meshBlendShapeSearchTerms[meshIndex]);



            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.G("data.enable_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, meshBlendShapeSearchTerms[meshIndex], true);
            }

            if (GUILayout.Button(Localization.G("data.mute_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, allShapeNames, meshBlendShapeSearchTerms[meshIndex], false);
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
                foreach (var shapeName in visibleShapeNames)
                {
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

            EditorGUI.BeginDisabledGroup(readOnlyMode);
            EditorWidgets.FBXGameObjectField(Localization.G("data.original_fbx"), originalFbxProperty);
            EditorGUI.EndDisabledGroup();
            if (!readOnlyMode)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField(Localization.G("data.edit_mode"), EditorStyles.boldLabel);
                var updatedEditMode = (BlendShapeEditMode)GUILayout.Toolbar(
                    (int)editMode,
                    new[]
                    {
                        Localization.S("data.edit_mode.order_adjust"),
                        Localization.S("data.edit_mode.bulk_toggle")
                    });
                if (updatedEditMode != editMode)
                {
                    editMode = updatedEditMode;
                    EditorStatePrefs.SetBlendShapeEditMode(target, editMode);
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
            for (int i = 0; i < meshDataListProperty.arraySize; i++)
            {
                SerializedProperty meshDataProperty =
                    meshDataListProperty.GetArrayElementAtIndex(i);
                SerializedProperty meshNameProperty =
                    meshDataProperty.FindPropertyRelative(nameof(MeshData.m_MeshName));
                SerializedProperty meshProperty =
                    meshDataProperty.FindPropertyRelative(nameof(MeshData.m_OriginMesh));
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUI.BeginDisabledGroup(readOnlyMode);
                
                if (originalFbxProperty.serializedObject.hasModifiedProperties)
                {
                    var meshName = meshNameProperty.stringValue;
                    Mesh found = Util.MeshUtil.FindMeshAsset(originalFbxProperty.objectReferenceValue, meshName);
                    if (found != null)
                    {
                        meshProperty.objectReferenceValue = found;
                    }
                }
                EditorGUILayout.ObjectField(meshProperty, new GUIContent(meshNameProperty.stringValue));
                EditorGUI.EndDisabledGroup();
                var shapeNamesProperty = meshBlendShapes[i].serializedProperty;
                var blendShapesProperty = meshDataProperty.FindPropertyRelative(BlendShapesPropertyName);
                var activeBlendShapeCount = shapeNamesProperty.arraySize;
                var totalBlendShapeCount = blendShapesProperty.arraySize;

                EditorGUI.indentLevel++;

                Rect rect = EditorGUILayout.GetControlRect();
                var foldoutLabel = readOnlyMode
                    ? new GUIContent(Localization.SF("data.blendshape_readonly_count_summary", activeBlendShapeCount))
                    : new GUIContent(Localization.SF("data.blendshape_count_summary", activeBlendShapeCount, totalBlendShapeCount));

                if(!readOnlyMode) foldoutLabel = EditorGUI.BeginProperty(rect, foldoutLabel, shapeNamesProperty);
                shapeNamesProperty.isExpanded = EditorGUI.Foldout(rect, shapeNamesProperty.isExpanded, foldoutLabel, true);
                if(!readOnlyMode) EditorGUI.EndProperty();

                if (shapeNamesProperty.isExpanded)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.box);

                    if (readOnlyMode)
                    {
                        meshBlendShapes[i].draggable = false;
                        meshBlendShapes[i].displayRemove = false;
                        meshBlendShapes[i].footerHeight = 0;
                        meshBlendShapes[i].DoLayoutList();

                    }
                    else if (editMode == BlendShapeEditMode.Reorder)
                    {
                        meshBlendShapes[i].draggable = true;
                        meshBlendShapes[i].displayRemove = true;
                        meshBlendShapes[i].footerHeight = EditorGUIUtility.singleLineHeight;
                        meshBlendShapes[i].DoLayoutList();
                    }
                    else
                    {
                        DrawEditableBlendShapeList(i, meshDataProperty, shapeNamesProperty);
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUI.indentLevel--;


                DrawMeshSelectionSection(dataAsset, dataAsset.m_MeshDataList[i]);
                

                EditorGUILayout.EndVertical();
                EditorGUILayout.Separator();
            }
            
      

#if ENABLE_FBX_SDK
            bool useFbxSdk = true;
#else
            bool useFbxSdk = false;
            EditorGUILayout.HelpBox(Localization.S("data.fbx_sdk_missing"), MessageType.Warning);
#endif
            EditorGUI.BeginDisabledGroup(!useFbxSdk || originalFbxProperty.objectReferenceValue == null);
            GUILayout.BeginHorizontal();
            bool applyBlendShapeLocked = readOnlyMode && appliedProperty.boolValue;
            EditorGUI.BeginDisabledGroup(applyBlendShapeLocked);
            
            var label = Localization.G("data.apply_blendshapes");
            if (applyBlendShapeLocked)
            {
                label.tooltip +=  Localization.S("data.apply_blendshapes.tooltip_locked_message");
            }
            if (GUILayout.Button(label))
            {
#if ENABLE_FBX_SDK
                if (dataAsset.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }
                if (Localization.DisplayDialog("data.dialog.apply_blendshapes", Localization.S("data.dialog.ok"), Localization.S("data.dialog.cancel")))
                {
                    appliedProperty.boolValue = true;
                    dataAsset.CreateFbx(dataAsset.m_Original);
                }
#endif
            }
            EditorGUI.EndDisabledGroup();


            bool removeBlendShapeLocked = readOnlyMode && !appliedProperty.boolValue;
            var removeBlendShapeLabel = Localization.G("data.remove_blendshapes");
            if (removeBlendShapeLocked)
            {
                removeBlendShapeLabel.tooltip +=  Localization.S("data.remove_blendshapes.tooltip_locked_message");
            }
            EditorGUI.BeginDisabledGroup(removeBlendShapeLocked);
            if (GUILayout.Button(removeBlendShapeLabel))
            {
#if ENABLE_FBX_SDK
                if (dataAsset.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }
                if (Localization.DisplayDialog("data.dialog.remove_blendshapes", Localization.S("data.dialog.ok"), Localization.S("data.dialog.cancel")))
                {
                    appliedProperty.boolValue = false;
                    dataAsset.RemoveBlendShapes(dataAsset.m_Original);
                }
#endif
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
            
            if (GUILayout.Button(Localization.G("data.apply_blendshapes_as_new_fbx")))
            {
#if ENABLE_FBX_SDK
                if (dataAsset.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }
                string folderPath = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataAsset));
                string path = EditorUtility.SaveFilePanelInProject(Localization.S("data.save_fbx.title"),
                    dataAsset.DefaultFbxName, "fbx",
                    Localization.S("data.save_file.message"), folderPath);
                if (path.Length > 0)
                {
                    dataAsset.CreateFbx(dataAsset.m_Original, path);
                }
#endif
            }
            
            EditorGUI.EndDisabledGroup();
            
            if (GUILayout.Button(Localization.G("data.create_meshes")))
            {
                if (dataAsset.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }

                string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataAsset));
                var path = EditorUtility.SaveFilePanelInProject(Localization.S("data.save_mesh.title"),
                    dataAsset.DefaultMeshAssetName, "asset",
                    Localization.S("data.save_file.message"), folderPath);

                if (path.Length > 0)
                {
                    var generated = BlendShapeAppender.CreateMeshAsset(dataAsset.m_Original, new[]{dataAsset}, path);

#if !ENABLE_FBX_SDK
                    if (generated == null)
                    {
                        Localization.DisplayDialog("data.dialog.mesh_vertices_not_match", Localization.S("data.dialog.ok"));
                    }
#endif
                }
            }
            
            EditorGUILayout.Separator();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.G("data.open_advanced_generator")))
            {
                var data = (BlendShapeDataSO)target;
                if (advancedGeneratorWindow == null)
                {
                    advancedGeneratorWindow =  EditorWindow.GetWindow<BlendShapeMeshGeneratorWindow>("BlendShare");
                    foreach (var obj in targets)
                    {               
                        advancedGeneratorWindow.blendShapeList.Add((BlendShapeDataSO)obj);
                    }
                    advancedGeneratorWindow.TargetMeshContainer = data.m_Original;
                }
                else
                {
                    advancedGeneratorWindow.Focus();
                }

            }
            EditorGUI.BeginDisabledGroup(readOnlyMode);
            if (GUILayout.Button(Localization.G("data.reset_blendshapes")))
            {
                for (int i = 0; i < meshDataListProperty.arraySize; i++)
                {
                    SerializedProperty meshDataProperty = meshDataListProperty.GetArrayElementAtIndex(i);
                    var blendShapesProp = meshDataProperty.FindPropertyRelative("m_BlendShapes");
                    dataAsset.m_MeshDataList[i].m_ShapeNames.Clear();
                    for (int j = 0; j < blendShapesProp.arraySize; j++)
                    {
                        var prop = blendShapesProp.GetArrayElementAtIndex(j).FindPropertyRelative(nameof(BlendShapeWrapper.m_ShapeName));
                        dataAsset.m_MeshDataList[i].m_ShapeNames.Add(prop.stringValue);
                    }  
                }
                serializedObject.Update();

                EditorUtility.SetDirty(dataAsset);
                AssetDatabase.SaveAssets();

                OnEnable();
            }
            EditorGUI.EndDisabledGroup();
            
            if (GUILayout.Button(readOnlyMode
                        ? Localization.G("data.edit") :
                        Localization.G("data.lock")
                    , GUILayout.ExpandWidth(false)))
            {
                readOnlyMode = !readOnlyMode;
                EditorStatePrefs.SetEditingEnabled(target, !readOnlyMode);

            }

            GUILayout.EndHorizontal();


            if (!readOnlyMode)
            {
                EditorGUILayout.Separator();
                GUILayout.BeginHorizontal(GUI.skin.box);
                EditorGUILayout.LabelField(Localization.G("data.created_by"), EditorStyles.centeredGreyMiniLabel);
                GUILayout.EndHorizontal();

                EditorGUILayout.Separator();
                
                EditorGUILayout.LabelField(Localization.G( "data.hidden_settings"), EditorStyles.boldLabel);

                GUILayout.BeginVertical(GUI.skin.box);
                var defaultAssetName = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_DefaultGeneratedAssetName));
                EditorGUILayout.PropertyField(defaultAssetName,  Localization.G( "data.hidden_settings.default_asset_name"));
                EditorGUILayout.PropertyField(appliedProperty, Localization.G( "data.hidden_settings.applied"));

                var m_DeformerID = serializedObject.FindProperty("m_DeformerID");

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(m_DeformerID, Localization.G( "data.hidden_settings.deformer_id"));
                EditorGUI.EndDisabledGroup();
                
                GUILayout.EndVertical();
            }


            serializedObject.ApplyModifiedProperties();
            
            
            
            
            
        }


    }

}
