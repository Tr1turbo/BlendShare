using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [PreferBinarySerialization]
    public class GeneratedMeshAssetSO : ScriptableObject
    {
        // a container for mesh assets
        public void ApplyMesh(Transform target)
        {

            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(AssetDatabase.GetAssetPath(this));

            foreach (var asset in subAssets)
            {
                var mesh = asset as Mesh;
                if (mesh == null)
                {
                    continue;
                }

                var targetMeshRenderer = target.Find(mesh.name)?.GetComponent<SkinnedMeshRenderer>();


                if (targetMeshRenderer != null)
                {
                    targetMeshRenderer.sharedMesh = mesh;
                }

            }
        }
    }

    [CustomEditor(typeof(GeneratedMeshAssetSO))]
    public class GeneratedMeshAssetsEditor : Editor
    {
        private Texture bannerIcon;

        private void OnEnable()
        {
            bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;

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




            GUILayout.BeginHorizontal("box");
            GUILayout.FlexibleSpace();
            GUILayout.Label("A mesh assets container");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();



        }


        

    }
}


