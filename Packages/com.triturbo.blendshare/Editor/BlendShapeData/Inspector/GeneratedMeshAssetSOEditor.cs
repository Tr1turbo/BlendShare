
using UnityEngine;
using UnityEditor;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CustomEditor(typeof(GeneratedMeshAssetSO))]
    public class GeneratedMeshAssetsEditor : Editor
    {
        private GameObject selectedGameObject; // Stores the user-selected GameObject
        private string prefKey;
        private SerializedProperty originalFbxProp;
        private SerializedProperty originalHashProp;
        private SerializedProperty appliedBlendShapesProp;
        
        private string currentFbxHash = "";

        
        private void OnEnable()
        {
            originalFbxProp = serializedObject.FindProperty("m_OriginalFbxAsset");
            originalHashProp = serializedObject.FindProperty("m_OriginalFbxHash");
            appliedBlendShapesProp = serializedObject.FindProperty("m_AppliedBlendShapes");
            
            currentFbxHash =  GeneratedMeshAssetSO.CalculateHash(originalFbxProp.objectReferenceValue);
            // create a per-asset preference key so the edit-mode persists per asset
            var path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path)) path = "GeneratedMeshAssetSO_" + target.GetInstanceID();
            prefKey = $"GeneratedMeshAssetSO_EditMode_{path}";
        }
        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label("A mesh assets container");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            serializedObject.Update();
            EditorGUILayout.Space();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Metadata", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical(GUI.skin.box);
            bool isEditing = EditorPrefs.GetBool(prefKey, false);
            // Metadata fields — disabled unless Edit pressed
            EditorGUI.BeginDisabledGroup(!isEditing);
            // Original FBX asset (object field)
            if (EditorWidgets.FBXGameObjectField(Localization.G("data.original_fbx"), originalFbxProp))
            {
                currentFbxHash = GeneratedMeshAssetSO.CalculateHash(originalFbxProp.objectReferenceValue);
            }
            
            // Compare the hashes
            if (originalFbxProp.objectReferenceValue != null &&
                !string.IsNullOrEmpty(currentFbxHash) &&
                originalHashProp.stringValue != currentFbxHash)
            {
                bool prevEnabled = GUI.enabled;
                GUI.enabled = true;
                EditorGUILayout.HelpBox(
                    Localization.S("mesh_asset.fbx_edited"),
                    MessageType.Warning
                );
                GUI.enabled = prevEnabled;
                
                EditorGUILayout.TextField("Original", originalHashProp.stringValue);
                EditorGUILayout.TextField("Current",currentFbxHash);
            }
            
            if (isEditing)
            {
                EditorGUILayout.PropertyField(appliedBlendShapesProp, Localization.G("mesh_asset.applied_blendshapes"), true);
            }
            else if (appliedBlendShapesProp.arraySize > 0)
            {
                EditorGUILayout.LabelField(Localization.G("mesh_asset.applied_blendshapes"), EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                for (int i = 0; i < appliedBlendShapesProp.arraySize; i++)
                {
                    var element = appliedBlendShapesProp.GetArrayElementAtIndex(i);
                    var obj = element.objectReferenceValue;
                    if (obj != null)
                    {
                        var so = element.objectReferenceValue as BlendShapeDataSO;
                        string label = obj.name;
                        if (so != null)
                        {
                            label = so.m_DeformerID;
                        }
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
            
            // When not editing show a small hint
            if (!isEditing)
            {
                EditorGUILayout.HelpBox(Localization.S("mesh_asset.read_only"), MessageType.Info);
            }

            // Top buttons: Edit / Save / Cancel
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (!isEditing)
            { 
                if (GUILayout.Button(Localization.G("data.edit"), GUILayout.Width(80)))
                { 
                    EditorPrefs.SetBool(prefKey, true); 
                    isEditing = true;
                }
            }
            else
            {
                if (GUILayout.Button(Localization.G("data.lock"), GUILayout.Width(80)))
                {
                    // apply and exit edit mode
                    serializedObject.ApplyModifiedProperties();
                    EditorPrefs.SetBool(prefKey, false);
                    isEditing = false;
                    // ensure asset is marked dirty so changes persist
                    EditorUtility.SetDirty(target);
                    AssetDatabase.SaveAssets();
                }
            }
            
            EditorGUILayout.EndHorizontal();
            serializedObject.ApplyModifiedProperties();

            GUILayout.Space(10);
            
            EditorGUILayout.LabelField(Localization.G("mesh_asset.assign_meshes"), EditorStyles.boldLabel);

            selectedGameObject = (GameObject)EditorGUILayout.ObjectField(Localization.G("mesh_asset.assign_meshes.target_go"), selectedGameObject, typeof(GameObject), true);
            bool isPrefab = selectedGameObject != null && EditorUtility.IsPersistent(selectedGameObject);
            if (isPrefab)
            {
                if (EditorWidgets.IsFBXGameObject(selectedGameObject))
                {
                    selectedGameObject = null;
                    Localization.DisplayDialog("mesh_asset.assign_meshes.dialog.is_fbx");
                }
                EditorGUILayout.HelpBox(Localization.S("mesh_asset.assign_meshes.is_prefab"), MessageType.Warning);
            }
            GUI.enabled = selectedGameObject != null; // Disable button if no GameObject selected
            // Button to apply mesh
            if (GUILayout.Button(isPrefab ? 
                    Localization.G("mesh_asset.assign_meshes.to_prefab_btn"):
                    Localization.G("mesh_asset.assign_meshes.to_go_btn")))
            {
                GeneratedMeshAssetSO meshAssetSO = (GeneratedMeshAssetSO)target;

                if (selectedGameObject != null)
                {
                    meshAssetSO.ApplyMesh(selectedGameObject.transform);
                }
                else
                {
                    Debug.LogWarning("No GameObject selected.");
                }

                selectedGameObject = null;
            }
            GUI.enabled = true;
        }
        
    }
}


