using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using UnityEditor;
using System.Linq;

using Triturbo.BlendShapeShare.BlendShapeData;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
# endif
namespace Triturbo.BlendShapeShare.Extractor
{
    public static class BlendShapesExtractor
    {


        public static BlendShapeDataSO ExtractFbxBlendShapes(GameObject blendShapeSource, GameObject compareTarget, List<MeshData> meshDataList)
        {
            var data = ScriptableObject.CreateInstance<BlendShapeDataSO>();

            data.m_Original = compareTarget;

            string defaultName = $"{compareTarget.name}-{blendShapeSource.name}";


#if ENABLE_FBX_SDK
            if (IsUnityVerticesEqual(blendShapeSource, compareTarget))
            {
                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource))
                {
                    return null;
                }
                ExtractUnityBlendShapes(ref meshDataList, blendShapeSource);
            }
            else
            {
                string path = AssetDatabase.GetAssetPath(blendShapeSource);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), $"{compareTarget.name}-{blendShapeSource.name}-{System.Guid.NewGuid().ToString()}.fbx");

                AssetDatabase.CopyAsset(path, tmp);
                AssetDatabase.Refresh();

                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, compareTarget, tmp))
                {
                    AssetDatabase.DeleteAsset(tmp);
                    return null;
                }

                var genertated = AssetDatabase.LoadAssetAtPath<GameObject>(tmp);

                ExtractUnityBlendShapes(ref meshDataList, genertated);

                AssetDatabase.DeleteAsset(tmp);
            }
#else
            if(!isEqualVertices)
            {
                EditorUtility.DisplayDialog("Vertices not match", "Vertices not match and Autodesk FBX SDK is missing", "OK");
            }
#endif

            List<MeshData> finalList = new List<MeshData>();
            foreach (var meshData in meshDataList)
            {
                if (meshData.BlendShapes.Count > 0)
                {
                    finalList.Add(meshData);
                }
            }

            data.m_MeshDataList = finalList;



            return data;
        }



        public static BlendShapeDataSO ExtractFbxBlendShapes(GameObject blendShapeSource, GameObject compareTarget, bool compareByName)
        {
            var meshDataList = CompareBlendShape(blendShapeSource, compareTarget, compareByName);
            return ExtractFbxBlendShapes(blendShapeSource, compareTarget, meshDataList);
        }



#if ENABLE_FBX_SDK
        public static bool ExtractFbxBlendshapes(GameObject source, ref List<MeshData> meshDataList)
        {
            if(meshDataList == null)
            {
                meshDataList = new List<MeshData>();
            }


            var fbxManager = FbxManager.Create();

            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);

            var sourceScene = FbxScene.Create(fbxManager, "Source");


            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }


            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            Debug.Log($"pFileFormat {pFileFormat}");


            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(source), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }
            fbxImporter.Import(sourceScene);

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Destroy Importer...", 0.3f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = sourceScene.GetRootNode();



            foreach (var meshData in meshDataList)
            {
                FbxNode node = sourceRootNode.FindChild(meshData.m_MeshName, false);
                FbxMesh sourceMesh = node?.GetMesh();
                if (sourceMesh == null)
                {
                    continue;
                }

                if (sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) < 1)
                {
                    continue;
                }

                var deformer = sourceMesh.GetBlendShapeDeformer(0);



                for (int i = 0; i < deformer.GetBlendShapeChannelCount(); i++)
                {
                    var channel = deformer.GetBlendShapeChannel(i);

                    string name = channel.GetName();
                    if (meshData.ContainsBlendShape(name))
                    {
                        meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh));
                    }
                }

            }



            return true;

        }


        public static bool ExtractFbxBlendshapes(
            ref List<MeshData> meshDataList, GameObject source, GameObject origin = null, string fbxPath = "")
        {
            if (meshDataList == null)
            {
                meshDataList = new List<MeshData>();
            }


            var fbxManager = FbxManager.Create();

            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);

            var sourceScene = FbxScene.Create(fbxManager, "Source");



            if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }


            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");



            if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(source), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }
            fbxImporter.Import(sourceScene);
            fbxImporter.Destroy();


            FbxScene originScene = null;
            FbxManager originFbxManager = null;

            if (origin != null)
            {
                originFbxManager = FbxManager.Create();
                originFbxManager.SetIOSettings(FbxIOSettings.Create(originFbxManager, Globals.IOSROOT));
                FbxImporter originFbxImporter = FbxImporter.Create(originFbxManager, "");


                originScene = FbxScene.Create(originFbxManager, "Origin");
                if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", "Import origin FBX...", 0.2f))
                {
                    EditorUtility.ClearProgressBar();
                    return false;
                }
                if (!originFbxImporter.Initialize(AssetDatabase.GetAssetPath(origin), pFileFormat, originFbxManager.GetIOSettings()))
                {
                    return false;
                }

                originFbxImporter.Import(originScene);
                originFbxImporter.Destroy();
            }



            if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", "Get Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = sourceScene.GetRootNode();
            var originRootNode = originScene?.GetRootNode();


            int count = 0;
            foreach (var meshData in meshDataList)
            {
                FbxNode node = sourceRootNode.FindChild(meshData.m_MeshName, false);
                FbxMesh sourceMesh = node?.GetMesh();


                if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", $"Check node: {meshData.m_MeshName}", 0.4f + 0.4f * count++ / meshDataList.Count))
                {
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                if (sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) < 1)
                {
                    continue;
                }


                FbxNode nodeOrigin = originRootNode?.FindChild(meshData.m_MeshName, false);
                FbxMesh originMesh = nodeOrigin?.GetMesh();

                if(originMesh != null)
                {
                    for (int dIndex = 0; dIndex < originMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); dIndex++)
                    {
                        originMesh.GetBlendShapeDeformer(dIndex).Destroy();
                    }
                }

                FbxBlendShape originDeformer = originMesh != null ? FbxBlendShape.Create(originMesh, "BlendShapeShareTemp") : null;


                for (int dIndex = 0; dIndex < sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); dIndex++)
                {
                    var deformer = sourceMesh.GetBlendShapeDeformer(dIndex);

                    for (int i = 0; i < deformer.GetBlendShapeChannelCount(); i++)
                    {
                        var channel = deformer.GetBlendShapeChannel(i);
                        string name = channel.GetName();
                        if (!meshData.ContainsBlendShape(name))
                        {
                            continue;
                        }
                        if (originDeformer != null)
                            originDeformer.AddBlendShapeChannel(CopyFbxBlendShapeChannel(channel, originMesh));

                        meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh));
                    }
                }

            }

            if (originScene != null && originFbxManager != null)
            {
                if (EditorUtility.DisplayCancelableProgressBar("ExtractFbxBlendshapes", $"Export temporary FBX...", 0.9f))
                {
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                var exporter = FbxExporter.Create(originFbxManager, "");
                if (exporter.Initialize(fbxPath, pFileFormat, originFbxManager.GetIOSettings()) == false)
                {
                    Debug.LogError("Exporter Initialize failed.");
                    return false;
                }
                exporter.Export(originScene);
                exporter.Destroy();
            }


            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();

        

            return true;
        }

        public static FbxBlendShapeChannel CopyFbxBlendShapeChannel(FbxBlendShapeChannel source, FbxObject container)
        {
            // Create a new FbxBlendShapeChannel with the same name as the source
            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(container, source.GetName());

            // Iterate over each target shape in the source blend shape channel
            int shapeCount = source.GetTargetShapeCount();
            for (int shapeIndex = 0; shapeIndex < source.GetTargetShapeCount(); shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);
                
                int controlPointCount = sourceShape.GetControlPointsCount();


                FbxShape newShape = FbxShape.Create(container, sourceShape.GetName());
                newShape.InitControlPoints(controlPointCount);

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    newShape.SetControlPointAt(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex), pointIndex);
                }


                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 *  (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }


        public static FbxBlendShapeData GetFbxBlendShapeData(FbxBlendShapeChannel source, FbxMesh sourceMesh)
        {
            int controlPointCount = sourceMesh.GetControlPointsCount();
            int shapeCount = source.GetTargetShapeCount();
            

            var frames = new FbxBlendShapeFrame[shapeCount];
            // Loop through each target shape in the blend shape channel
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);

                if (sourceShape.GetControlPointsCount() != controlPointCount)
                {
                    Debug.LogError("Control point count mismatch between the source mesh and target shape.");
                    return null;
                }

                frames[shapeIndex] = new FbxBlendShapeFrame();

                //var deltaControlPoints = new FbxBlendShapeData.Vector4d[controlPointCount];
                // Loop through each control point
                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var shapeControlPoint = sourceShape.GetControlPointAt(pointIndex);
                    var meshControlPoint = sourceMesh.GetControlPointAt(pointIndex);

                    var d = shapeControlPoint - meshControlPoint;


                    var delta = new Vector4d(d.X, d.Y, d.Z, d.W);

                    if (!delta.IsZero())
                        frames[shapeIndex].AddDeltaControlPointAt(delta, pointIndex);


                }

               
            }


            return new FbxBlendShapeData(frames);
        }
       

#endif

        //Compare GameOjects and find out extra blendshape and store the names in each MeshData
        public static List<MeshData> CompareBlendShape(GameObject source, GameObject origin, bool compareByName = true)
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

                if (compareByName)
                {
                    MeshData meshData = new MeshData(originMesh, GetExtraBlendShapeNames(sourceMesh, originMesh));
                    meshDataList.Add(meshData);
                }
                else
                {
                    List<string> blendShapes = new List<string>();
                    for (int i = originMesh.blendShapeCount; i < sourceMesh.blendShapeCount; i++)
                    {
                        blendShapes.Add(sourceMesh.GetBlendShapeName(i));

                   
                    }
                    MeshData meshData = new MeshData(originMesh, blendShapes);

                    meshDataList.Add(meshData);
                }

               
            }
            return meshDataList;
        }




        public static bool IsUnityVerticesEqual(GameObject source, GameObject origin)
        {
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
                    return false;
                }


                if (originMesh.vertexCount != sourceMesh.vertexCount)
                {
                    return false;
                }
                if (!sourceMesh.vertices.SequenceEqual(originMesh.vertices))
                {
                    return false;
                }

            }

            return true;
        }
        //unity
        public static void ExtractUnityBlendShapes(ref List<MeshData> meshDataList, GameObject source)
        {

            //var skinnedMeshRenderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (Transform tansform in source.transform)
            {
                Mesh sourceMesh = tansform.TryGetComponent(out SkinnedMeshRenderer meshRenderer) ? meshRenderer.sharedMesh : null;
                if (sourceMesh == null)
                {
                    continue;
                }

                MeshData meshData = meshDataList.SingleOrDefault(m => m.m_MeshName == tansform.name);
                
                if (meshData == null)
                {
                    continue;
                }

                meshData.ExtractUnityBlendShapes(sourceMesh);
            }
        }



        public static List<string> GetExtraBlendShapeNames(Mesh sourceMesh, Mesh origin)
        {
            List<string> blendShapes = new List<string>();
            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(i);

                //Find out if blendshape exist in oringin mesh
                if (origin.GetBlendShapeIndex(shapeName) == -1 && !blendShapes.Contains(shapeName))
                {
                    blendShapes.Add(shapeName);
                }
            }
            return blendShapes;
        }


        public static void ExtractUnityBlendShapes(this MeshData meshData, Mesh sourceMesh)
        {
            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(i);

                int frameCount = sourceMesh.GetBlendShapeFrameCount(i);

                if(!meshData.ContainsBlendShape(shapeName))
                {
                    continue;
                }


                UnityBlendShapeData unityBlendShapeData = new UnityBlendShapeData(frameCount);

                for (int j = 0; j < frameCount; j++)
                {
                    float frameWeight = sourceMesh.GetBlendShapeFrameWeight(i, j);
                    Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
                    Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
                    Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];

                    sourceMesh.GetBlendShapeFrameVertices(i, j, deltaVertices, deltaNormals, deltaTangents);
                    UnityBlendShapeFrame frame = new UnityBlendShapeFrame(frameWeight, sourceMesh.vertexCount,
                        deltaVertices, deltaNormals, deltaTangents);

                    unityBlendShapeData.AddFrameAt(j, frame);
                }
                
                meshData.SetBlendShape(shapeName, unityBlendShapeData);
            }

        }




        //class BlendShapesExtractor
    }
    //namespace
}


