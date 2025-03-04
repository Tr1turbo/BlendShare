
using UnityEngine;
using UnityEditor;
using System.Linq;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [PreferBinarySerialization]
    public class GeneratedMeshAssetSO : ScriptableObject
    {
        // a container for mesh assets
        public void ApplyMesh(Transform target)
        {
            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(AssetDatabase.GetAssetPath(this));
            var meshRenderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null) // Skip null sharedMesh
                .ToDictionary(renderer => renderer.sharedMesh.name, renderer => renderer);
            
            foreach (var asset in subAssets)
            {
                if (asset is Mesh mesh)
                {
                    if (meshRenderers.TryGetValue(mesh.name, out var targetMeshRenderer))
                    {
                        targetMeshRenderer.sharedMesh = mesh;
                    }
                    else
                    {
                        Debug.LogWarning($"Mesh '{mesh.name}' not found in target GameObject: {target.name}", target);
                    }
                }
            }
        }
    }

    [CustomEditor(typeof(GeneratedMeshAssetSO))]
    public class GeneratedMeshAssetsEditor : Editor
    {
        private Texture bannerIcon;
        private GameObject selectedGameObject; // Stores the user-selected GameObject

        private void OnEnable()
        {
            bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;

        }
        public override void OnInspectorGUI()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace(); // Pushes the label to the center
            GUILayout.Label(bannerIcon, GUILayout.Height(42), GUILayout.Width(168));
            GUILayout.FlexibleSpace(); // Pushes the label to the center
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            
            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label("A mesh assets container");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            
            GUILayout.Space(10);
            selectedGameObject = (GameObject)EditorGUILayout.ObjectField("Target GameObject", selectedGameObject, typeof(GameObject), true);
            if (selectedGameObject != null && EditorUtility.IsPersistent(selectedGameObject))
            {
                selectedGameObject = null; // Reset if it's an asset
                Debug.LogWarning("Please select a GameObject from the scene, not a prefab or asset.");
            }
            GUI.enabled = selectedGameObject != null; // Disable button if no GameObject selected
            // Button to apply mesh
            if (GUILayout.Button("Apply Mesh to Target GameObject"))
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
            }
            GUI.enabled = true;
        }
    }
}


