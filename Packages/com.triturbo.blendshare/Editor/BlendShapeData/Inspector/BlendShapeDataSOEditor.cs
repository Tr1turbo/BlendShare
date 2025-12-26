using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using UnityEditor;
using UnityEditorInternal;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CustomEditor(typeof(BlendShapeDataSO))]
    public class BlendShapeDataSOEditor : Editor
    {
        private List<ReorderableList> meshBlendShapes;
        private SerializedProperty meshDataListProperty;
        private SerializedProperty originalFbxProperty;
        private SerializedProperty appliedProperty;
        private bool readOnlyMode = true;
        private List<List<SerializedProperty>> meshBlendShapeNames;
        private BlendShapeMeshGeneratorWindow advancedGeneratorWindow = null;
        private void ShowContextMenu(ReorderableList reorderableList, int index)
        {
            GenericMenu menu = new GenericMenu();
            
            menu.AddItem(new GUIContent("Mute"), false, () => {
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
            
            meshBlendShapeNames = new List<List<SerializedProperty>>();
            // Get the target object
            BlendShapeDataSO dataAsset = (BlendShapeDataSO)target;

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
                    if (!readOnlyMode && Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
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
            }
        }


        public override void OnInspectorGUI()
        {

            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Separator();

            EditorGUI.BeginDisabledGroup(readOnlyMode);
            EditorWidgets.FBXGameObjectField(Localization.G("data.original_fbx"), originalFbxProperty);
            EditorGUI.EndDisabledGroup();
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
                var property = meshBlendShapes[i].serializedProperty;
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, $"{Localization.S("blendshapes")}: {property.arraySize}");
                
                if (property.isExpanded)
                {
                    meshBlendShapes[i].DoLayoutList();
                    meshBlendShapes[i].draggable = !readOnlyMode;
                    meshBlendShapes[i].displayRemove = !readOnlyMode;
                    meshBlendShapes[i].footerHeight = readOnlyMode ? 0 : EditorGUIUtility.singleLineHeight;
                }
                
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
                var dataAsset = (BlendShapeDataSO)target;

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
                var dataAsset = (BlendShapeDataSO)target;

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
                var dataAsset = (BlendShapeDataSO)target;
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
                var dataAsset = (BlendShapeDataSO)target;

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
                BlendShapeDataSO dataAsset = (BlendShapeDataSO)target;
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


