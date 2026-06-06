using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Migration;
using Triturbo.BlendShare.Persistence;
using UnityEditorInternal;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [System.Obsolete("BlendShapeMeshGeneratorWindow is a legacy editor. Use the new BlendShareObject workflow for new assets.")]
    public class BlendShapeMeshGeneratorWindow : EditorWindow 
    { 
        private Object targetMeshContainer;
        public List<Object> blendShapeList = new();
        
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

        private Object[] GetValidBlendShapeAssets()
        {
            return blendShapeList
                .Where(IsSupportedBlendShareData)
                .Distinct()
                .ToArray();
        } 
        
        private void OnEnable()
        {
            reorderableList = new ReorderableList(
                blendShapeList,
                typeof(Object),
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
                var updated = EditorGUI.ObjectField(
                    new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight),
                    blendShapeList[index],
                    typeof(Object),
                    false
                );
                blendShapeList[index] = IsSupportedBlendShareData(updated) ? updated : blendShapeList[index];
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
                    bool valid = DragAndDrop.objectReferences.Any(IsSupportedBlendShareData);
                    DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        blendShapeList.AddRange(DragAndDrop.objectReferences
                            .Where(IsSupportedBlendShareData)
                            .Where(bsd => !blendShapeList.Contains(bsd)));
                        evt.Use();
                    }
                }
            }
        }

        private void OnDataListUpdated()
        {
            // Remove nulls first
            var validList = GetValidBlendShapeAssets();

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
            
            isAbleToGenerateMesh = targetMeshContainer != null && validList.Length > 0;
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
            
            bool isValidInput = targetMeshContainer != null && blendShapeList.Any(IsSupportedBlendShareData);
            
            if (isValidInput && !isAbleToGenerateFbx && !isAbleToGenerateMesh)
            {
                EditorGUILayout.HelpBox(
                    Localization.S("mesh_generator.mesh_generation_disable"), 
                    MessageType.Error);
            }
            
            GUI.enabled = isValidInput && (isAbleToGenerateFbx || isAbleToGenerateMesh);
            if (GUILayout.Button(Localization.G("mesh_generator.generate_mesh"), GUILayout.Height(32)))
            {
                var validBlendShapes =  GetValidBlendShapeAssets();
                var savePath = GetFileSavePath(validBlendShapes, "asset");
                if (!string.IsNullOrEmpty(savePath))
                {
                    GenerateMesh(validBlendShapes, savePath);
                }
            }
            
            GUI.enabled = isValidInput && isAbleToGenerateFbx;
            if (GUILayout.Button(Localization.G("mesh_generator.generate_fbx"), GUILayout.Height(32)))
            {
                var validBlendShapes =  GetValidBlendShapeAssets();
                var savePath = GetFileSavePath(validBlendShapes,"fbx");
                if (!string.IsNullOrEmpty(savePath))
                {
                    GenerateFbx(validBlendShapes, savePath);
                }
            }
            GUI.enabled = true;


            
        }

        private string GetFileSavePath(Object[] validBlendShapes, string extension)
        {
            string defaultName = targetMeshContainer.name;
            foreach (var blendShape in validBlendShapes)
            {
                defaultName += GetDefaultAssetName(blendShape);
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

        private void GenerateMesh(Object[] validBlendShapes, string filePath)
        {
            if (targetMeshContainer == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Target Mesh Container.", "OK");
                return;
            }
            var blendShares = GetBlendSharesForGeneration(validBlendShapes, false);
            if (blendShares.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "Please assign at least one BlendShapeDataSO or BlendShareObject.", "OK");
                return;
            }
            try
            {
                var result = BlendShareArtifactService.CreateArtifact(
                    targetMeshContainer,
                    blendShares,
                    filePath
                );
                if (result == null)
                {
                    EditorUtility.DisplayDialog("Error", "Mesh generation failed", "OK");
                    return;
                }
            
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            
                EditorUtility.DisplayDialog("Success", $"Generated artifact saved at:\n{filePath}", "OK");
                Selection.activeObject = result;
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex);
                EditorUtility.DisplayDialog("Error", $"Failed to generate mesh asset:\n{ex.Message}", "OK");
            }
        }
        
        private void GenerateFbx(Object[] validBlendShapes, string filePath)
        {
            if (targetMeshContainer == null)
            {
                EditorUtility.DisplayDialog("Error", "Please assign a Target Mesh Container.", "OK");
                return;
            }
            
            var blendShares = GetBlendSharesForGeneration(validBlendShapes, true);
            if (blendShares.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "Please assign at least one BlendShapeDataSO or BlendShareObject.", "OK");
                return;
            }
            try
            {
                GameObject source = null;
                if (EditorWidgets.IsFBXGameObject(targetMeshContainer, out var fbx))
                {
                    source = fbx;
                }
                else if(targetMeshContainer is GeneratedMeshAssetSO maso)
                {
                    source = maso.m_OriginalFbxAsset;
                }
                
                if (!BlendShareGenerationService.CreateFbx(source, blendShares, filePath))
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

        private static bool IsSupportedBlendShareData(Object obj)
        {
            return obj is BlendShapeDataSO || obj is BlendShareObject;
        }

        private static string GetDefaultAssetName(Object asset)
        {
            if (asset is BlendShareObject blendShare)
            {
                return blendShare.DefaultMeshAssetName;
            }

            if (asset is BlendShapeDataSO legacy)
            {
                return legacy.DefaultMeshAssetName;
            }

            return string.Empty;
        }

        private BlendShareObject[] GetBlendSharesForGeneration(IEnumerable<Object> assets, bool includeTargetGeneratedAssetApplied)
        {
            var blendShares = new List<BlendShareObject>();
            if (includeTargetGeneratedAssetApplied && targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    blendShares.AddRange(generatedAsset.m_AppliedBlendShares.Where(share => share != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    blendShares.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(share => share != null));
                }
            }

            foreach (var asset in assets ?? Enumerable.Empty<Object>())
            {
                if (asset is BlendShareObject blendShare)
                {
                    blendShares.Add(blendShare);
                    continue;
                }

                if (asset is BlendShapeDataSO legacy)
                {
                    var upgraded = BlendShareUpgradeService.UpgradeSideBySide(legacy);
                    if (upgraded != null)
                    {
                        blendShares.Add(upgraded);
                    }
                }
            }

            return blendShares
                .Where(share => share != null)
                .Distinct()
                .ToArray();
        }
    
        
    }

}
