using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;


namespace Triturbo.BlendShapeShare.Extractor
{
    public class BlendShapesExtractorEditor : EditorWindow
    {
        public GameObject originFBX;
        public GameObject sourceFBX;
        public GameObject lastSourceFBX = null;

        public string defaultName = "";
        public static Texture bannerIcon;


        
        public BlendShapesExtractor.CompareMethod compareMethod = BlendShapesExtractor.CompareMethod.Name;


        [MenuItem("Tools/BlendShare/BlendShapes Extractor")]
        public static void ShowWindow()
        {
            GetWindow<BlendShapesExtractorEditor>("BlendShare");
            bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;
        }


        private void OnGUI()
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
            else
            {
                bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;
            }


            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("BlendShapes Extract Tool by Triturbo", EditorStyles.boldLabel);
            GUILayout.EndHorizontal();

            EditorGUILayout.Separator();
            originFBX = (GameObject)EditorGUILayout.ObjectField("Origin FBX", originFBX, typeof(GameObject), false);

            sourceFBX = (GameObject)EditorGUILayout.ObjectField("Source FBX", sourceFBX, typeof(GameObject), false);


           if (sourceFBX != lastSourceFBX)
           {
                defaultName = sourceFBX?.name;
           }

            lastSourceFBX = sourceFBX;
            defaultName = EditorGUILayout.TextField("Default Genertated Asset Name", defaultName);



            compareMethod = (BlendShapesExtractor.CompareMethod)EditorGUILayout.EnumPopup("Compare Method", compareMethod);

#if ENABLE_FBX_SDK
            bool enableFbx = true;
#else
            bool enableFbx = false;
            EditorGUILayout.HelpBox("Autodesk FBX SDK is missing. FBX manipulate features will be disabled", MessageType.Warning);
#endif

            EditorGUILayout.Separator();
            EditorGUI.BeginDisabledGroup(!enableFbx);

            if (GUILayout.Button("Save BlendShapes as .asset"))
            {
                var so = BlendShapesExtractor.ExtractFbxBlendShapes(sourceFBX, originFBX, compareMethod);
                if (so == null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(defaultName))
                {
                    defaultName = sourceFBX.name;
                }

                
                string path = EditorUtility.SaveFilePanelInProject("Save asset", $"{defaultName}_BlendShare", 
                    "asset", "Please enter a file name to save the FBX");


                so.m_DefaultGeneratedAssetName = defaultName;
                so.m_DeformerID = "+BlendShare-" + defaultName;
                

                AssetDatabase.CreateAsset(so, path);
                AssetDatabase.SaveAssets();


                AssetDatabase.Refresh();
            }
            EditorGUI.EndDisabledGroup();

        }
    }
}


