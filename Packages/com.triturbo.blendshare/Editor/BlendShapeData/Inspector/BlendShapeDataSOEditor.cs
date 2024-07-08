using System.Collections;
using System.Collections.Generic;
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

        private Texture bannerIcon;

        private List<List<SerializedProperty>> meshBlendShapeNames;

        private void ShowContextMenu(ReorderableList reorderableList, int index)
        {
            GenericMenu menu = new GenericMenu();
            
            menu.AddItem(new GUIContent("Mute"), false, () => {
                SerializedProperty listProperty = reorderableList.serializedProperty;
                listProperty.DeleteArrayElementAtIndex(index);
                serializedObject.ApplyModifiedProperties();
            });
            //menu.AddItem(new GUIContent("Option 2"), false, null);
            menu.ShowAsContext();
        }
        private void OnEnable()
        {
            
            meshDataListProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_MeshDataList));
            originalFbxProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_Original));
            appliedProperty = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_Applied));

            bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;
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
                            ShowContextMenu(reorderableList, index);
                            Event.current.Use();
                    }
#endif
                };

                // Set up the header callback
                //reorderableList.drawHeaderCallback = (Rect rect) =>
                //{
                    
                //    EditorGUI.LabelField(rect, "Name");
                //    //EditorGUI.LabelField(new Rect(rect.x + rect.width / 2, rect.y, rect.width / 2, rect.height), "vertex delta count");
                //};


                reorderableList.footerHeight = 0;
                meshBlendShapes.Add(reorderableList);
            }
        }


        public override void OnInspectorGUI()
        {
            if (bannerIcon != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace(); // Pushes the label to the center
                GUILayout.Label(bannerIcon, GUILayout.Height(42), GUILayout.Width(168));


                GUILayout.FlexibleSpace(); // Pushes the label to the center
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
            }

            EditorGUI.BeginDisabledGroup(readOnlyMode);
            EditorGUILayout.PropertyField(originalFbxProperty);



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
                    var node = (originalFbxProperty.objectReferenceValue as GameObject)?.transform.Find(meshName);

                    appliedProperty.boolValue = false;

                    if (node != null && node.TryGetComponent(out SkinnedMeshRenderer meshRenderer))
                    {
                        meshProperty.objectReferenceValue = meshRenderer.sharedMesh;
                    }

                }

                EditorGUILayout.ObjectField(meshProperty, new GUIContent(meshNameProperty.stringValue));
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel++;

                var property = meshBlendShapes[i].serializedProperty;
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, $"Blendshapes: {property.arraySize}");


                if (property.isExpanded)
                {

                    //if (readOnlyMode)
                    //{
                    //    var names = meshDataProperty.FindPropertyRelative(nameof(BlendShapeDataSO.MeshData.shapeNames))
                    //    for()
                    //    foreach (string name in dataAsset.meshDataList[i].shapeNames)
                    //    {
                    //        EditorGUILayout.LabelField(name);
                    //    }
                    //}
                    //else
                    //{
                    meshBlendShapes[i].DoLayoutList();

                    meshBlendShapes[i].draggable = !readOnlyMode;
                    meshBlendShapes[i].displayRemove = !readOnlyMode;

                    meshBlendShapes[i].footerHeight = readOnlyMode ? 0 : EditorGUIUtility.singleLineHeight;

                    // }


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
            string applyBlendShapeLockedMsg = applyBlendShapeLocked ? "\n(Currently this button is disable since you are already apply blendshapes to Fbx. Click Edit to force enable)" : "";

            if (GUILayout.Button(new GUIContent("Apply blendshapes", "Add blendshapes in the list to Original GameObject." + applyBlendShapeLockedMsg)))
            {
#if ENABLE_FBX_SDK
                var dataAsset = (BlendShapeDataSO)target;

                if (dataAsset.m_Original == null)
                {
                    EditorUtility.DisplayDialog("Original FBX is missing", "Original FBX is missing. Please import asset or assign a FBX to 'Origin'.", "OK");
                    return;
                }
                if (EditorUtility.DisplayDialog("Apply blendshapes", "It will add blendshapes in this list to Original GameObject. Are you sure?", "Yes", "Cancel"))
                {
                    


                    dataAsset.CreateFbx(dataAsset.m_Original);
                }
                appliedProperty.boolValue = true;
#endif
            }
            EditorGUI.EndDisabledGroup();


            bool removeBlendShapeLocked = readOnlyMode && !appliedProperty.boolValue;
            EditorGUI.BeginDisabledGroup(removeBlendShapeLocked);
            string removeBlendShapeLockedMsg = removeBlendShapeLocked ? "\n(Currently this button is disable since you are not apply blendshapes to Fbx yet. Click Edit to force enable)" : "";
            if (GUILayout.Button(new GUIContent("Remove blendshapes", "Remove blendshapes in the list from Original GameObject." + removeBlendShapeLockedMsg)))
            {
#if ENABLE_FBX_SDK
                var dataAsset = (BlendShapeDataSO)target;

                if (dataAsset.m_Original == null)
                {
                    EditorUtility.DisplayDialog("Original FBX is missing", "Original FBX is missing. Please import asset or assign a FBX to 'Origin'.", "OK");
                    return;
                }
                if (EditorUtility.DisplayDialog("Remove blendshapes", "It will remove blendshapes in this list from Original GameObject. Are you sure?", "Yes", "Cancel"))
                {

                    dataAsset.RemoveBlendShapes(dataAsset.m_Original);

                }
                appliedProperty.boolValue = false;
#endif
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();



            if (GUILayout.Button(new GUIContent("Apply blendshapes as new FBX", "Duplicate Original GameObject and apply blendshapes to the copy.")))
            {


#if ENABLE_FBX_SDK
                var dataAsset = (BlendShapeDataSO)target;
                if (dataAsset.m_Original == null)
                {
                    EditorUtility.DisplayDialog("Original FBX is missing", "Original FBX is missing. Please import asset or assign a FBX to 'Origin'.", "OK");
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
            //
            EditorGUI.EndDisabledGroup();

            //


            if (GUILayout.Button(new GUIContent("Create Meshes", "Create new mesh assets with blendshapes.")))
            {
                var dataAsset = (BlendShapeDataSO)target;

                if (dataAsset.m_Original == null)
                {
                    EditorUtility.DisplayDialog("Original FBX is missing", "Original FBX is missing. Please import asset or assign a FBX to 'Origin'.", "OK");
                    return;
                }

                var meshList = dataAsset.CreateMeshes();
                string folderPath = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(dataAsset));

                string path = "";
                if (meshList == null)
                {

#if ENABLE_FBX_SDK
                    if (EditorUtility.DisplayDialog("Unity mesh vertices not match", "Unable to create mesh since vertices not match. \nCreate FBX file instead?", "Create FBX", "Cancel"))
                    {


                        path = EditorUtility.SaveFilePanelInProject("Save FBX",
                            dataAsset.DefaultFbxName, "fbx",
                            "Please enter a file name", folderPath);
                        if (path.Length > 0)
                        {
                            dataAsset.CreateFbx(dataAsset.m_Original, path);
                        }
                    }
#else
                    if (EditorUtility.DisplayDialog("Unity mesh vertices not match", "Unable to create mesh since vertices not match. Please import FBX SDK and create FBX file", "OK"))
                    {
                        
                    }
#endif
                    return;
                }

                
                path = EditorUtility.SaveFilePanelInProject("Save Mesh",
                dataAsset.DefaultMeshAssetName, "asset",
                "Please enter a file name", folderPath);

                if (path.Length > 0)
                    BlendShapeAppender.CreateMeshAsset(meshList, path);

            }






            EditorGUILayout.Separator();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();


            EditorGUI.BeginDisabledGroup(readOnlyMode);
            if (GUILayout.Button(new GUIContent("Reset blendshapes", "Reset blendshapes list to original")))
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
            }
            EditorGUI.EndDisabledGroup();



            string btn = readOnlyMode ? " Edit " : "Lock";
            if (GUILayout.Button(btn, GUILayout.ExpandWidth(false)))
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



                EditorGUILayout.LabelField("Hidden Settings", EditorStyles.boldLabel);

                GUILayout.BeginVertical(GUI.skin.box);
                var defaultAssetName = serializedObject.FindProperty(nameof(BlendShapeDataSO.m_DefaultGeneratedAssetName));
                EditorGUILayout.PropertyField(defaultAssetName,  new GUIContent("Default Asset Name", "Default name for generated asset."));
                EditorGUILayout.PropertyField(appliedProperty, new GUIContent("Applied", "Indicate if user has applied blendshapes to FBX."));


                var m_DeformerID = serializedObject.FindProperty("m_DeformerID");


                EditorGUI.BeginDisabledGroup(true);

                EditorGUILayout.PropertyField(m_DeformerID, new GUIContent("Deformer ID", ""));

                EditorGUI.EndDisabledGroup();


                GUILayout.EndVertical();






            }


            serializedObject.ApplyModifiedProperties();
        }


    }

}


