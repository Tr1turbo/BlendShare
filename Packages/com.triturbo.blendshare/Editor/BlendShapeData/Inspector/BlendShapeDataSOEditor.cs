using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using UnityEditor;
using UnityEditorInternal;
using System.Drawing;


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
        private Dictionary<string, List<MeshBlendShapeSelectionSO>> meshSelectionsByMeshName;
        private Dictionary<string, MeshBlendShapeSelectionSO> workingSelectionSourcesByMeshName;
        private string editModePrefKey;
        private bool bulkToggleDragActive;
        private bool bulkToggleDragValue;
        private int bulkToggleDragMeshIndex = -1;
        private Vector2 bulkToggleDragStartPosition;

        private void ShowContextMenu(ReorderableList reorderableList, int index)
        {
            GenericMenu menu = new GenericMenu();

            menu.AddItem(new GUIContent("Mute"), false, () =>
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
            meshSelectionsByMeshName = new Dictionary<string, List<MeshBlendShapeSelectionSO>>();
            workingSelectionSourcesByMeshName = new Dictionary<string, MeshBlendShapeSelectionSO>();
            
            meshBlendShapeSearchTerms = new List<string>(meshDataListProperty.arraySize);
            // Get the target object
            BlendShapeDataSO dataAsset = (BlendShapeDataSO)target;
            var path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path)) path = "BlendShapeDataSO_" + target.GetInstanceID();
            editModePrefKey = $"BlendShapeDataSO_EditMode_{path}";
            readOnlyMode = !EditorPrefs.GetBool(editModePrefKey, false);
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

        private void RefreshMeshSelections(BlendShapeDataSO dataAsset)
        {
            meshSelectionsByMeshName = MeshBlendShapeSelectionUtility.GroupSelectionsByMesh(dataAsset);

            if (workingSelectionSourcesByMeshName == null)
            {
                workingSelectionSourcesByMeshName = new Dictionary<string, MeshBlendShapeSelectionSO>();
            }

            var validSelections = new HashSet<MeshBlendShapeSelectionSO>(meshSelectionsByMeshName.Values.SelectMany(list => list));
            var invalidKeys = workingSelectionSourcesByMeshName
                .Where(entry => entry.Value == null || !validSelections.Contains(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var key in invalidKeys)
            {
                workingSelectionSourcesByMeshName.Remove(key);
            }
        }

        private List<MeshBlendShapeSelectionSO> GetMeshSelections(string meshName)
        {
            return meshSelectionsByMeshName != null &&
                   meshSelectionsByMeshName.TryGetValue(meshName ?? string.Empty, out var selections)
                ? selections
                : new List<MeshBlendShapeSelectionSO>();
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
            IReadOnlyList<MeshBlendShapeSelectionSO> selections)
        {
            if (HasSameShapeNames(meshData.m_ShapeNames, meshData.GetAllBlendShapeNames()))
            {
                return 0;
            }

            for (int i = 0; i < selections.Count; i++)
            {
                var resolvedNames = MeshBlendShapeSelectionUtility.ResolveSelectionShapeNames(
                    dataAsset,
                    meshData,
                    selections[i],
                    false);

                if (HasSameShapeNames(meshData.m_ShapeNames, resolvedNames))
                {
                    return i + 1;
                }
            }

            return selections.Count + 1;
        }

        private MeshBlendShapeSelectionSO SaveMeshSelection(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            MeshBlendShapeSelectionSO existingSelection = null,
            string selectionNameOverride = null)
        {
            var selections = GetMeshSelections(meshData.m_MeshName);
            string selectionName = existingSelection != null
                ? existingSelection.DisplayName
                : selectionNameOverride;

            if (string.IsNullOrWhiteSpace(selectionName))
            {
                selectionName = MeshBlendShapeSelectionUtility.GetNextSelectionName(selections, meshData.m_MeshName);
            }

            selectionName = selectionName.Trim();
            var sanitizedNames = MeshBlendShapeSelectionUtility.SanitizeSelectionShapeNames(
                meshData,
                meshData.m_ShapeNames,
                dataAsset,
                selectionName);

            MeshBlendShapeSelectionSO selection = existingSelection;
            if (selection == null)
            {
                selection = CreateInstance<MeshBlendShapeSelectionSO>();
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

        private MeshBlendShapeSelectionSO GetWorkingSelectionSource(string meshName)
        {
            if (workingSelectionSourcesByMeshName != null &&
                workingSelectionSourcesByMeshName.TryGetValue(meshName, out var selection))
            {
                return selection;
            }

            return null;
        }

        private void SetWorkingSelectionSource(string meshName, MeshBlendShapeSelectionSO selection)
        {
            if (workingSelectionSourcesByMeshName == null)
            {
                workingSelectionSourcesByMeshName = new Dictionary<string, MeshBlendShapeSelectionSO>();
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
            MeshBlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return;
            }

            var resolvedNames = MeshBlendShapeSelectionUtility.ResolveSelectionShapeNames(dataAsset, meshData, selection, true);
            LoadShapeNamesIntoWorkingSet(dataAsset, meshData, resolvedNames);
            SetWorkingSelectionSource(meshData.m_MeshName, selection);
        }

        private void PromptSaveMeshSelection(BlendShapeDataSO dataAsset, MeshData meshData, System.Action onSaved = null)
        {
            string defaultName = MeshBlendShapeSelectionUtility.GetNextSelectionName(
                GetMeshSelections(meshData.m_MeshName),
                meshData.m_MeshName);

            MeshBlendShapeSelectionNamePrompt.Show(
                "Save BlendShape Selection",
                defaultName,
                selectionName =>
                {
                    SaveMeshSelection(dataAsset, meshData, null, selectionName);
                    onSaved?.Invoke();
                    serializedObject.Update();
                    Repaint();
                });
        }

        private void DeleteMeshSelection(BlendShapeDataSO dataAsset, MeshData meshData, MeshBlendShapeSelectionSO selection)
        {
            if (selection == null || meshData == null)
            {
                return;
            }

            LoadShapeNamesIntoWorkingSet(dataAsset, meshData, meshData.GetAllBlendShapeNames());
            SetWorkingSelectionSource(selection.m_MeshName, null);

            Undo.DestroyObjectImmediate(selection);
            EditorUtility.SetDirty(dataAsset);
            AssetDatabase.SaveAssets();
            RefreshMeshSelections(dataAsset);
            serializedObject.Update();
            Repaint();
        }

        private void LoadPopupSelectionChoice(
            BlendShapeDataSO dataAsset,
            MeshData meshData,
            IReadOnlyList<MeshBlendShapeSelectionSO> selections,
            int popupIndex)
        {
            if (popupIndex == 0)
            {
                LoadShapeNamesIntoWorkingSet(dataAsset, meshData, meshData.GetAllBlendShapeNames());
                SetWorkingSelectionSource(meshData.m_MeshName, null);
                return;
            }

            int selectionIndex = popupIndex - 1;
            if (selectionIndex >= 0 && selectionIndex < selections.Count)
            {
                LoadMeshSelectionIntoWorkingSet(dataAsset, meshData, selections[selectionIndex]);
            }
        }

        private bool HasSelectionIssues(BlendShapeDataSO dataAsset, MeshData meshData, MeshBlendShapeSelectionSO selection)
        {
            if (selection == null)
            {
                return false;
            }

            if (!string.Equals(selection.m_MeshName, meshData.m_MeshName))
            {
                return true;
            }

            var allShapeNames = new HashSet<string>(meshData.GetAllBlendShapeNames());
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

            var optionNames = new[] { "All BlendShapes" }
                .Concat(selections.Select(selection =>
                    showCurrentWorkingSet && selection == workingSelectionSource
                        ? $"{selection.DisplayName}*"
                        : selection.DisplayName))
                .Concat(showCurrentWorkingSet && workingSelectionSourceIndex <= 0
                    ? new[] { "Current Working Set" }
                    : System.Array.Empty<string>())
                .ToArray();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("BlendShape Selections", EditorStyles.boldLabel);

            int updatedSelectionIndex = EditorGUILayout.Popup(
                "Active Selection",
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
                        "Unsaved BlendShape Selection",
                        $"The current working state for mesh '{meshName}' has unsaved changes. Save it before loading another selection?",
                        "Save",
                        "Cancel",
                        "Discard");

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

            MeshBlendShapeSelectionSO selectedSelection =
                displayedPopupIndex > 0 && displayedPopupIndex <= selections.Count
                    ? selections[displayedPopupIndex - 1]
                    : null;

            if (selectedSelection != null)
            {
                SetWorkingSelectionSource(meshName, selectedSelection);

                if (HasSelectionIssues(dataAsset, meshData, selectedSelection))
                {
                    EditorGUILayout.HelpBox(
                        "The selected mesh selection contains missing or duplicate blendshape names. Invalid names will be skipped when used.",
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
                    if (workingSelectionSource != null && GUILayout.Button("Save"))
                    {
                        SaveMeshSelection(dataAsset, meshData, workingSelectionSource);
                        GUIUtility.ExitGUI();
                    }

                    if (GUILayout.Button("Create New Selection"))
                    {
                        PromptSaveMeshSelection(dataAsset, meshData);
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.EndHorizontal();
                }

                if (selectedSelection != null)
                {
                    if (GUILayout.Button("Delete"))
                    {
                        bool confirmed = EditorUtility.DisplayDialog(
                            "Delete BlendShape Selection",
                            $"Delete mesh selection '{selectedSelection.DisplayName}' for mesh '{meshName}'?",
                            "Delete",
                            "Cancel");

                        if (confirmed)
                        {
                            DeleteMeshSelection(dataAsset, meshData, selectedSelection);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndVertical();
        }

        private static string GetBlendShapeName(SerializedProperty blendShapeWrapperProperty)
        {
            return blendShapeWrapperProperty.FindPropertyRelative(nameof(BlendShapeWrapper.m_ShapeName)).stringValue;
        }

        private static string[] GetAllBlendShapeNames(SerializedProperty blendShapesProperty)
        {
            var names = new string[blendShapesProperty.arraySize];
            for (int i = 0; i < blendShapesProperty.arraySize; i++)
            {
                names[i] = GetBlendShapeName(blendShapesProperty.GetArrayElementAtIndex(i));
            }

            return names;
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
            SerializedProperty blendShapesProperty,
            string shapeName,
            bool enabled)
        {
            var allShapeNames = GetAllBlendShapeNames(blendShapesProperty);
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
            SerializedProperty blendShapesProperty,
            string searchTerm,
            bool enabled)
        {
            var allShapeNames = GetAllBlendShapeNames(blendShapesProperty);
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
            SerializedProperty blendShapesProperty,
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
                    blendShapesProperty,
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
                        blendShapesProperty,
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
                    blendShapesProperty,
                    shapeName,
                    updated);

                changed = true;
            }
        }

        private void DrawEditableBlendShapeList(int meshIndex, SerializedProperty meshDataProperty, SerializedProperty shapeNamesProperty)
        {
            var blendShapesProperty = meshDataProperty.FindPropertyRelative(BlendShapesPropertyName);
            var allShapeNames = GetAllBlendShapeNames(blendShapesProperty);
            var enabledNames = GetEnabledBlendShapeNames(shapeNamesProperty);
            var visibleShapeNames = allShapeNames
                .Where(shapeName => MatchesSearch(meshBlendShapeSearchTerms[meshIndex], shapeName))
                .ToArray();

            EditorGUILayout.BeginVertical(GUI.skin.box);

            meshBlendShapeSearchTerms[meshIndex] = EditorGUILayout.TextField(
                Localization.G("data.blendshape_filter"),
                meshBlendShapeSearchTerms[meshIndex]);

            EditorGUILayout.LabelField(
                Localization.SF("data.blendshape_status", shapeNamesProperty.arraySize, allShapeNames.Length, visibleShapeNames.Length),
                EditorStyles.miniLabel);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.G("data.enable_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, blendShapesProperty, meshBlendShapeSearchTerms[meshIndex], true);
            }

            if (GUILayout.Button(Localization.G("data.mute_visible_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, blendShapesProperty, meshBlendShapeSearchTerms[meshIndex], false);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.G("data.enable_all_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, blendShapesProperty, string.Empty, true);
            }

            if (GUILayout.Button(Localization.G("data.mute_all_blendshapes")))
            {
                SetFilteredBlendShapesEnabled(shapeNamesProperty, blendShapesProperty, string.Empty, false);
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
                        blendShapesProperty,
                        out bool changed);
                    if (changed)
                    {
                        enabledNames = GetEnabledBlendShapeNames(shapeNamesProperty);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawReorderList(int meshIndex)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            //EditorGUILayout.HelpBox(Localization.S("data.order_adjust_mode_hint"), MessageType.None);

            meshBlendShapes[meshIndex].draggable = true;
            meshBlendShapes[meshIndex].displayRemove = true;
            meshBlendShapes[meshIndex].footerHeight = EditorGUIUtility.singleLineHeight;
            meshBlendShapes[meshIndex].DoLayoutList();

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
                editMode = (BlendShapeEditMode)GUILayout.Toolbar(
                    (int)editMode,
                    new[]
                    {
                        Localization.S("data.edit_mode.order_adjust"),
                        Localization.S("data.edit_mode.bulk_toggle")
                    });
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
                EditorGUI.indentLevel++;
                var shapeNamesProperty = meshBlendShapes[i].serializedProperty;
                var blendShapesProperty = meshDataProperty.FindPropertyRelative(BlendShapesPropertyName);
                var activeBlendShapeCount = shapeNamesProperty.arraySize;
                var totalBlendShapeCount = blendShapesProperty.arraySize;


                Rect rect = EditorGUILayout.GetControlRect();
                var foldoutLabel = readOnlyMode
                    ? new GUIContent($"{Localization.S("blendshapes")}: {activeBlendShapeCount}")
                    : new GUIContent(Localization.SF("data.blendshape_count_summary", activeBlendShapeCount, totalBlendShapeCount));

                if(!readOnlyMode) foldoutLabel = EditorGUI.BeginProperty(rect, foldoutLabel, shapeNamesProperty);
                shapeNamesProperty.isExpanded = EditorGUI.Foldout(rect, shapeNamesProperty.isExpanded, foldoutLabel, true);
                if(!readOnlyMode) EditorGUI.EndProperty();
                if (shapeNamesProperty.isExpanded)
                {
                    if (readOnlyMode)
                    {
                        meshBlendShapes[i].DoLayoutList();
                        meshBlendShapes[i].draggable = false;
                        meshBlendShapes[i].displayRemove = false;
                        meshBlendShapes[i].footerHeight = 0;
                    }
                    else if (editMode == BlendShapeEditMode.Reorder)
                    {
                        DrawReorderList(i);
                    }
                    else
                    {
                        DrawEditableBlendShapeList(i, meshDataProperty, shapeNamesProperty);
                    }
                }

                DrawMeshSelectionSection(dataAsset, dataAsset.m_MeshDataList[i]);
                
                EditorGUI.indentLevel--;

                EditorGUILayout.EndVertical();
                EditorGUILayout.Separator();
            }
            
      

#if ENABLE_FBX_SDK
            bool useFbxSdk = true;
#else
            bool useFbxSdk = false;
            EditorGUILayout.HelpBox("Autodesk FBX SDK is missing. FBX manipulate features will be disabled", MessageType.Warning);
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
                    Localization.DisplayDialog("data.dialog.fbx_missing");
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
                    Localization.DisplayDialog("data.dialog.fbx_missing");
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
                    Localization.DisplayDialog("data.dialog.fbx_missing");
                    return;
                }
                string folderPath = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataAsset));
                string path = EditorUtility.SaveFilePanelInProject("Save FBX",
                    dataAsset.DefaultFbxName, "fbx",
                    "Please enter a file name", folderPath);
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
                    Localization.DisplayDialog("data.dialog.fbx_missing");
                    return;
                }

                string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataAsset));
                var path = EditorUtility.SaveFilePanelInProject("Save Mesh",
                    dataAsset.DefaultMeshAssetName, "asset",
                    "Please enter a file name", folderPath);

                if (path.Length > 0)
                {
                    var generated = BlendShapeAppender.CreateMeshAsset(dataAsset.m_Original, new[]{dataAsset}, path);

#if !ENABLE_FBX_SDK
                    if (generated == null)
                    {
                        EditorUtility.DisplayDialog("Unity mesh vertices not match", "Unable to create mesh since vertices not match. Please import FBX SDK and create FBX file", "OK");
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
                EditorPrefs.SetBool(editModePrefKey, !readOnlyMode);

            }

            GUILayout.EndHorizontal();


            if (!readOnlyMode)
            {
                EditorGUILayout.Separator();
                GUILayout.BeginHorizontal(GUI.skin.box);
                EditorGUILayout.LabelField("Blendshapes sharing tool created by Triturbo", EditorStyles.centeredGreyMiniLabel);
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
