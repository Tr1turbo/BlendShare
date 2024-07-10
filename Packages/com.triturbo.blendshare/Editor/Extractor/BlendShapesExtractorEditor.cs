using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Triturbo.BlendShapeShare.BlendShapeData;

namespace Triturbo.BlendShapeShare.Extractor
{
    public class BlendShapesExtractorEditor : EditorWindow
    {
        public GameObject originFBX;
        public GameObject sourceFBX;
        //public GameObject lastSourceFBX = null;

        public string defaultName = "";
        public static Texture bannerIcon;

        public enum CompareMethod
        {
            Name,
            Index,
            Custom
        }

        public CompareMethod compareMethod = CompareMethod.Name;

        // blendshapes togles
        public Vector2 mainScrollPos;
        private List<SkinnedMeshRenderer> skinnedMeshRenderers;

        public Dictionary<SkinnedMeshRenderer, bool[]> blendShapeToggles =  new Dictionary<SkinnedMeshRenderer, bool[]>();
        public Dictionary<SkinnedMeshRenderer, Vector2> scrollPositions = new Dictionary<SkinnedMeshRenderer, Vector2>();
       
        public SkinnedMeshRenderer currentFocus;

        public int firstIndexWithCtrl = -1;

        public bool sourceIsFbx = false;
        public bool originIsFbx = false;


        [MenuItem("Tools/BlendShare/BlendShapes Extractor")]
        public static void ShowWindow()
        {
            GetWindow<BlendShapesExtractorEditor>("BlendShare");
            bannerIcon = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath("6ee731e02404e154694005c2442f2bf4"), typeof(Texture)) as Texture;
        }
        bool IsFBXFile(GameObject obj)
        {
            // Check if the asset path ends with ".fbx"
            string assetPath = AssetDatabase.GetAssetPath(obj);
            return assetPath.ToLower().EndsWith(".fbx");
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
            GUILayout.Label("BlendShapes Extracting Tool by Triturbo", EditorStyles.boldLabel);
            GUILayout.EndHorizontal();

            EditorGUILayout.Separator();


            EditorGUI.BeginChangeCheck();


            EditorGUI.BeginChangeCheck();
            originFBX = (GameObject)EditorGUILayout.ObjectField("Origin FBX", originFBX, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck() && originFBX != null)
            {
                originIsFbx = IsFBXFile(originFBX);
            }

            if (!originIsFbx && originFBX != null)
            {
                EditorGUILayout.HelpBox("This is not an fbx file", MessageType.Error);
                return;
            }


            sourceFBX = (GameObject)EditorGUILayout.ObjectField("Source FBX", sourceFBX, typeof(GameObject), false);
            
            if (EditorGUI.EndChangeCheck())
            {
                sourceIsFbx = IsFBXFile(sourceFBX);

                if(sourceFBX != null)
                    defaultName = sourceFBX.name;
                else
                    blendShapeToggles.Clear();

                if (sourceFBX != null && originFBX != null)
                {
                    InitBlendShapesToggles();
                }

              
            }

            if(!sourceIsFbx && sourceFBX != null)
            {
                EditorGUILayout.HelpBox("This is not an fbx file", MessageType.Error);
                return;
            }

            defaultName = EditorGUILayout.TextField("Default Asset Name", defaultName);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Deformer ID", "+BlendShare-" + defaultName);
            EditorGUI.EndDisabledGroup();

            compareMethod = (CompareMethod)EditorGUILayout.EnumPopup("Compare Method", compareMethod);


            if(compareMethod == CompareMethod.Custom)
            {
                EditorGUI.indentLevel++;
                ShowBlendShapesToggles();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Separator();



            bool enable = sourceFBX != null && originFBX != null;

#if ENABLE_FBX_SDK
            bool enableFbx = true;
#else
            bool enableFbx = false;
            EditorGUILayout.HelpBox("Autodesk FBX SDK is missing. FBX manipulate features will be disabled", MessageType.Warning);
#endif


            EditorGUI.BeginDisabledGroup(!(enableFbx && enable));

            if (GUILayout.Button("Save BlendShapes as .asset"))
            {
                BlendShapeDataSO so = null;
                if (compareMethod == CompareMethod.Custom)
                {
                    so = BlendShapesExtractor.ExtractFbxBlendShapes(sourceFBX, originFBX, GetMeshDataList(sourceFBX, originFBX, blendShapeToggles));

                }
                else
                {
                    so = BlendShapesExtractor.ExtractFbxBlendShapes(sourceFBX, originFBX, compareMethod == CompareMethod.Name);
                }

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

        public static List<MeshData> GetMeshDataList(GameObject source, GameObject origin, Dictionary<SkinnedMeshRenderer, bool[]> blendShapeToggles)
        {
            List<MeshData> meshDataList = new List<MeshData>();
            var skinnedMeshRenderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (Transform tansform in source.transform)
            {
                Mesh sourceMesh = tansform.TryGetComponent(out SkinnedMeshRenderer meshRenderer) ? meshRenderer.sharedMesh : null;
                if (sourceMesh == null)
                {
                    continue;
                }
                Mesh originMesh = origin.transform.Find(tansform.name)?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;

                if (originMesh == null)
                {
                    Debug.LogError($"Can not find {tansform.name} in origin: {origin.name}");
                    continue;
                }


                List<string> blendShapes = new List<string>();
                for (int i = 0; i < blendShapeToggles[meshRenderer].Length; i++)
                {
                    if (blendShapeToggles[meshRenderer][i])
                    {
                        blendShapes.Add(sourceMesh.GetBlendShapeName(i));
                    }
                }
                MeshData meshData = new MeshData(originMesh, blendShapes);

                meshDataList.Add(meshData);
            }
            return meshDataList;
        }

        public void InitBlendShapesToggles()
        {
            if (sourceFBX == null) return;
            skinnedMeshRenderers = new List<SkinnedMeshRenderer>(sourceFBX.GetComponentsInChildren<SkinnedMeshRenderer>());

            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                Mesh mesh = skinnedMeshRenderer.sharedMesh;
                if (mesh == null) continue;


                int blendShapeCount = mesh.blendShapeCount;
                if (blendShapeCount == 0) continue;

                if (!blendShapeToggles.ContainsKey(skinnedMeshRenderer))
                {
                    blendShapeToggles[skinnedMeshRenderer] = new bool[blendShapeCount];

                    if (originFBX == null)
                    {
                        continue;

                    }
                    Mesh originMesh = originFBX.transform.Find(skinnedMeshRenderer.name)?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;

                    if (originMesh == null)
                    {
                        Debug.LogError($"Can not find {skinnedMeshRenderer.name} in origin: {originFBX.name}");
                        continue;
                    }



                    for (int i = 0; i < blendShapeCount; i++)
                    {
                        string shapeName = mesh.GetBlendShapeName(i);
                        if (originMesh.GetBlendShapeIndex(shapeName) == -1)
                        {
                            blendShapeToggles[skinnedMeshRenderer][i] = true;
                        }
                    }

                }




            }
        }
        public void ShowBlendShapesToggles()
        {
            if (sourceFBX == null) return;

            Event e = Event.current;

            float maximumMainHeight = position.height - 220;

            float blendshapesMaxHeight = maximumMainHeight * 0.6f;
            if(blendshapesMaxHeight > 600)
            {
                blendshapesMaxHeight = 600;
            }

            float blendshapesHeight = 0;
            if (currentFocus != null && currentFocus.sharedMesh != null)
            {
                blendshapesHeight = currentFocus.sharedMesh.blendShapeCount * 15f ;
            }
            if(blendshapesHeight > blendshapesMaxHeight + 30f)
            {
                blendshapesHeight = blendshapesMaxHeight + 30f;
            }
            float mainHeight = blendshapesHeight + skinnedMeshRenderers.Count * 15f;


            if (mainHeight > maximumMainHeight)
            {
               // mainHeight = position.height - 220;

                mainScrollPos = EditorGUILayout.BeginScrollView(mainScrollPos, GUILayout.Height(maximumMainHeight));
            }
           
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                Mesh mesh = skinnedMeshRenderer.sharedMesh;
                if (mesh == null) continue;
                int blendShapeCount = mesh.blendShapeCount;
                if (blendShapeCount == 0) continue;

                float totalHeight = blendShapeCount * 15f;


                bool isFocus = currentFocus == skinnedMeshRenderer;


                bool isFocusNew = EditorGUILayout.Foldout(isFocus, skinnedMeshRenderer.name, true);

                if (isFocusNew != isFocus)
                {
                    currentFocus = isFocusNew ? skinnedMeshRenderer : null;
                    firstIndexWithCtrl = -1;
                }

                if (!isFocusNew) continue;

                EditorGUI.indentLevel++;
                EditorGUILayout.BeginVertical(GUI.skin.box);

                if (!blendShapeToggles.ContainsKey(skinnedMeshRenderer))
                {
                    blendShapeToggles[skinnedMeshRenderer] = new bool[blendShapeCount];
                }

                if (GUILayout.Button("Deselect all"))
                {
                    for (int i = 0; i < blendShapeToggles[skinnedMeshRenderer].Length; i++)
                    {
                        blendShapeToggles[skinnedMeshRenderer][i] = false;
                    }
                }

                if (totalHeight > blendshapesMaxHeight)
                {
                    if (!scrollPositions.ContainsKey(skinnedMeshRenderer))
                    {
                        scrollPositions[skinnedMeshRenderer] = Vector2.zero;
                    }
                    scrollPositions[skinnedMeshRenderer] = EditorGUILayout.BeginScrollView(scrollPositions[skinnedMeshRenderer], GUILayout.Height(blendshapesMaxHeight));

                }

                for (int i = 0; i < blendShapeCount; i++)
                {
                    string blendShapeName = mesh.GetBlendShapeName(i);
                    EditorGUILayout.BeginHorizontal();
                    // DrawSelectableLabel(blendShapeName);

                    EditorGUI.BeginDisabledGroup(!blendShapeToggles[skinnedMeshRenderer][i]);
                    if (firstIndexWithCtrl == i)
                    {
                        GUI.contentColor = Color.green;
                    }
                    EditorGUILayout.LabelField(blendShapeName);

                    GUI.contentColor = Color.white;

                    EditorGUI.EndDisabledGroup();

                    EditorGUI.BeginChangeCheck();



                    blendShapeToggles[skinnedMeshRenderer][i] = EditorGUILayout.Toggle(blendShapeToggles[skinnedMeshRenderer][i]);


                    Rect lastRect = GUILayoutUtility.GetLastRect();
                    if (e.type == EventType.ContextClick && lastRect.Contains(e.mousePosition))
                    {
                        int index = i;
                        GenericMenu menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Select to end"), false, () => ToggleAll(index, blendShapeToggles[skinnedMeshRenderer], true));
                        menu.AddItem(new GUIContent("Deselect to end"), false, () => ToggleAll(index, blendShapeToggles[skinnedMeshRenderer], false));

                        menu.AddItem(new GUIContent("Select from here"), false, () => {
                            firstIndexWithCtrl = index;
                        });
                        if (firstIndexWithCtrl != -1)
                        {
                            menu.AddItem(new GUIContent("Select to here"), false, () => {

                                ToggleAll(firstIndexWithCtrl, index, blendShapeToggles[skinnedMeshRenderer], true);

                                firstIndexWithCtrl = -1;
                            });
                           
                        }

                        menu.ShowAsContext();
                        e.Use();
                    }



                    EditorGUILayout.EndHorizontal();
                }

                if (totalHeight > blendshapesMaxHeight)
                {
                    EditorGUILayout.EndScrollView();
                }
                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel--;

            }

            if (mainHeight > maximumMainHeight)
            {
                EditorGUILayout.EndScrollView();
            }
                
        }


        void ToggleAll(int index, bool [] toggles, bool value)
        {
            for (int i = index; i < toggles.Length; i ++)
            {
                toggles[i] = value;
            }
        }

        void ToggleAll(int from, int to, bool[] toggles, bool value)
        {
            if (from == to) return;
            if(from > to)
            {
                int temp = from;
                from = to;
                to = temp;
            }
            for (int i = from; i <= to; i++)
            {
                toggles[i] = value;
            }

           
        }

    }
}