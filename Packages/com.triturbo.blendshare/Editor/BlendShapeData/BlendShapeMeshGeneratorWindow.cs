using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Triturbo.BlendShapeShare.Util;
using UnityEditorInternal;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public class BlendShapeMeshGeneratorWindow : EditorWindow 
    { 
        private Object targetMeshContainer;
        public List<BlendShapeDataSO> blendShapeList = new();
        
        private ReorderableList reorderableList;
        private Vector2 scroll;
        
        
        private bool isAbleToGenerateFbx = false;
        private bool isAbleToGenerateMesh = false;

        public Object TargetMeshContainer
        {
            get => targetMeshContainer;
            set
            {
                targetMeshContainer = value;
                isAbleToGenerateFbx = EditorWidgets.IsFBXGameObject(targetMeshContainer);
            }
        }

        [MenuItem("Tools/BlendShare/Advanced Mesh Generator")]
        public static void ShowWindow()
        {
           GetWindow<BlendShapeMeshGeneratorWindow>("BlendShare");
        }

        private BlendShapeDataSO[] GetValidBlendShapes()
        {
            return blendShapeList
                .Where(b => b != null)   // remove nulls
                .Distinct()              // remove duplicates
                .ToArray();
        } 
        
        private void OnEnable()
        {
            reorderableList = new ReorderableList(
                blendShapeList,
                typeof(BlendShapeDataSO),
                true,  // draggable
                true,  // display header
                true,  // display add button
                true   // display remove button
            );

            reorderableList.drawHeaderCallback = rect =>
            {
                EditorGUI.LabelField(rect, Localization.G("mesh_generator.blendshapes_data_list"));
            };

            reorderableList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                rect.y += 2;
                blendShapeList[index] = (BlendShapeDataSO)EditorGUI.ObjectField(
                    new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight),
                    blendShapeList[index],
                    typeof(BlendShapeDataSO),
                    false
                );
            };

            reorderableList.onAddCallback = list =>
            {
                blendShapeList.Add(null);
            };

            reorderableList.onRemoveCallback = list =>
            {
                if (list.index >= 0 && list.index < blendShapeList.Count)
                    blendShapeList.RemoveAt(list.index);
            };
        }
        
        private void HandleDragAndDrop()
        {
            Event evt = Event.current;
            Rect dropArea = GUILayoutUtility.GetLastRect();

            if (dropArea.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
                {
                    bool valid = DragAndDrop.objectReferences.Any(o => o is BlendShapeDataSO);
                    DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        blendShapeList.AddRange(DragAndDrop.objectReferences
                            .OfType<BlendShapeDataSO>()
                            .Where(bsd => !blendShapeList.Contains(bsd)));

                        evt.Use();
                    }
                }
            }
        }

        private void OnDataListUpdated()
        {
            // Remove nulls first
            var validList = GetValidBlendShapes();

            // bool allSame = validList.Length > 0 && 
            //                validList.All(b => b.m_Original == validList[0].m_Original);
            //
            // if (allSame)
            // {
            //     Debug.Log("All elements have the same origin FBX");
            // }
            // else
            // {
            //     Debug.Log("Elements have different origin FBX objects");
            // }
            
            isAbleToGenerateMesh = targetMeshContainer != null && BlendShapeAppender.IsAllMeshesValid(validList, MeshUtil.GetMeshes(targetMeshContainer).Values);
        }
        
        
        private void OnGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            EditorGUILayout.LabelField("Advanced BlendShape Mesh Generator", EditorStyles.boldLabel);
            
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space(8);
            
            EditorGUI.BeginChangeCheck();
            targetMeshContainer = EditorWidgets.MeshAssetObjectField(
                Localization.G("mesh_generator.target_mesh_container"), targetMeshContainer);
            
            if (EditorGUI.EndChangeCheck())
            {
                isAbleToGenerateFbx = EditorWidgets.IsFBXGameObject(targetMeshContainer);
                if (!isAbleToGenerateFbx && targetMeshContainer is GeneratedMeshAssetSO maso)
                {
                    isAbleToGenerateFbx = maso.m_OriginalFbxAsset != null;
                }
                OnDataListUpdated();

            }
            EditorGUILayout.Space(6);

            // Reorderable List
            using (var scrollView = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scrollView.scrollPosition;
                
                EditorGUI.BeginChangeCheck();
                reorderableList.DoLayoutList();
                if (EditorGUI.EndChangeCheck())
                {
                    OnDataListUpdated();
                }
                
            }

            HandleDragAndDrop();
            EditorGUILayout.Space(16);
            
            bool isValidInput = targetMeshContainer != null && blendShapeList.Any(b => b != null);
            
            if (isValidInput &&　!isAbleToGenerateFbx && !isAbleToGenerateMesh)
            {
                EditorGUILayout.HelpBox(
                    Localization.S("mesh_generator.mesh_generation_disable"), 
                    MessageType.Error);
            }
            
            GUI.enabled = isValidInput && (isAbleToGenerateFbx || isAbleToGenerateMesh);
            if (GUILayout.Button(Localization.G("mesh_generator.generate_mesh"), GUILayout.Height(32)))
            {
                var validBlendShapes =  GetValidBlendShapes();
                var savePath = GetFileSavePath(validBlendShapes, "asset");
                if (!string.IsNullOrEmpty(savePath))
                {
                    GenerateMesh(validBlendShapes, savePath);
                }
            }
            
            GUI.enabled = isValidInput && isAbleToGenerateFbx;
            if (GUILayout.Button(Localization.G("mesh_generator.generate_fbx"), GUILayout.Height(32)))
            {
                var validBlendShapes =  GetValidBlendShapes();
                var savePath = GetFileSavePath(validBlendShapes,"fbx");
                if (!string.IsNullOrEmpty(savePath))
                {
                    GenerateFbx(validBlendShapes, savePath);
                }
            }
            GUI.enabled = true;


            
        }

        private string GetFileSavePath(BlendShapeDataSO[] validBlendShapes, string extension)
        {
            string defaultName = targetMeshContainer.name;
            foreach (var blendShape in validBlendShapes)
            {
                defaultName += blendShape.DefaultMeshAssetName;
            }
            string savePath = EditorUtility.SaveFilePanel(
                "Save Mesh Asset",
                Application.dataPath,
                defaultName,
                extension
            );
            if (savePath.StartsWith(Application.dataPath))
            {
                return "Assets" + savePath.Substring(Application.dataPath.Length);
            }

            return "";
        }

        private void GenerateMesh(BlendShapeDataSO[] validBlendShapes, string filePath)
        {
            if (targetMeshContainer == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Target Mesh Container.", "OK");
                return;
            }
            if (validBlendShapes.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "Please assign at least one BlendShapeDataSO.", "OK");
                return;
            }
            try
            {
                var result = BlendShapeAppender.CreateMeshAsset(
                    targetMeshContainer,
                    validBlendShapes,
                    filePath
                );
                if (result == null)
                {
                    EditorUtility.DisplayDialog("Error", "Mesh generation failed", "OK");
                    return;
                }
            
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            
                EditorUtility.DisplayDialog("Success", $"Generated mesh asset saved at:\n{filePath}", "OK");
                Selection.activeObject = result;
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex);
                EditorUtility.DisplayDialog("Error", $"Failed to generate mesh asset:\n{ex.Message}", "OK");
            }
        }
        
        private void GenerateFbx(BlendShapeDataSO[] validBlendShapes, string filePath)
        {
            if (targetMeshContainer == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Target Mesh Container.", "OK");
                return;
            }
            
            if (validBlendShapes.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "Please assign at least one BlendShapeDataSO.", "OK");
                return;
            }
            try
            {
                GameObject source = null;
                IEnumerable<BlendShapeDataSO> blendShapes = validBlendShapes;
                if (EditorWidgets.IsFBXGameObject(targetMeshContainer, out var fbx))
                {
                    source = fbx;
                }
                else if(targetMeshContainer is GeneratedMeshAssetSO maso)
                {
                    source = maso.m_OriginalFbxAsset;
                    blendShapes = maso.m_AppliedBlendShapes.Concat(validBlendShapes);
                }
                
                if (!BlendShapeAppender.CreateFbx(source, blendShapes, filePath))
                {
                    EditorUtility.DisplayDialog("Error", "Fbx generation failed", "OK");
                    return;
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Success", $"Generated mesh asset saved at:\n{filePath}", "OK");
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex);
                EditorUtility.DisplayDialog("Error", $"Failed to generate mesh asset:\n{ex.Message}", "OK");
            }
        }
    
        
    }

}
