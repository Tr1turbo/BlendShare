using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareArtifact))]
    public sealed class BlendShareArtifactEditor : UnityEditor.Editor
    {
        private GameObject selectedGameObject;

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(Localization.G("mesh_asset.assign_meshes"), EditorStyles.boldLabel);

            selectedGameObject = (GameObject)EditorGUILayout.ObjectField(
                Localization.G("mesh_asset.assign_meshes.target_go"),
                selectedGameObject,
                typeof(GameObject),
                true);

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

            GUI.enabled = selectedGameObject != null;
            if (GUILayout.Button(isPrefab
                    ? Localization.G("mesh_asset.assign_meshes.to_prefab_btn")
                    : Localization.G("mesh_asset.assign_meshes.to_go_btn")))
            {
                BlendShareArtifactService.ApplyArtifact((BlendShareArtifact)target, selectedGameObject.transform);
                selectedGameObject = null;
            }

            GUI.enabled = true;
        }
    }
}
