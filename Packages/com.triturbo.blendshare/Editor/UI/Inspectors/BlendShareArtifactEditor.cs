using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.UI.Shared;
using Triturbo.BlendShare.Pipeline.Artifacts;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Inspectors
{
    [CustomEditor(typeof(BlendShareGeneratedArtifact))]
    public sealed class BlendShareArtifactEditor : UnityEditor.Editor
    {
        private GameObject m_SelectedGameObject;

        public override void OnInspectorGUI()
        {
            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(Localization.G("asset.assign_meshes"), EditorStyles.boldLabel);

            m_SelectedGameObject = (GameObject)EditorGUILayout.ObjectField(
                Localization.G("asset.assign_meshes.target_go"),
                m_SelectedGameObject,
                typeof(GameObject),
                true);

            bool isPrefab = m_SelectedGameObject != null && EditorUtility.IsPersistent(m_SelectedGameObject);
            if (isPrefab)
            {
                if (EditorWidgets.IsFBXGameObject(m_SelectedGameObject))
                {
                    m_SelectedGameObject = null;
                    Localization.DisplayDialog("asset.assign_meshes.dialog.is_fbx");
                }
                else
                {
                    EditorGUILayout.HelpBox(Localization.S("asset.assign_meshes.is_prefab"), MessageType.Warning);
                }
            }

            using (new EditorGUI.DisabledScope(m_SelectedGameObject == null))
            {
                if (GUILayout.Button(isPrefab
                        ? Localization.G("asset.assign_meshes.to_prefab_btn")
                        : Localization.G("asset.assign_meshes.to_go_btn")))
                {
                    BlendShareArtifactService.ApplyArtifact((BlendShareGeneratedArtifact)target, m_SelectedGameObject.transform);
                    m_SelectedGameObject = null;
                }
            }
        }
    }
}
