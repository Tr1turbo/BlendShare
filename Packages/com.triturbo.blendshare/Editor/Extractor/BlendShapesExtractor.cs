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
        public static BlendShapeDataSO ExtractBlendShapes(GameObject blendShapeSource, GameObject originObject, List<MeshData> meshDataList, 
            bool sourceAsBaseMesh = true, bool fixWeldVertices = true)
        {
            var data = ScriptableObject.CreateInstance<BlendShapeDataSO>();

            data.m_Original = originObject;

            string defaultName = $"{originObject.name}-{blendShapeSource.name}";


#if ENABLE_FBX_SDK
            if (IsUnityVerticesEqual(blendShapeSource, originObject))
            {
                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, sourceAsBaseMesh: sourceAsBaseMesh))
                {
                    return null;
                }
                ExtractUnityBlendShapes(ref meshDataList, blendShapeSource, originObject);
            }
            else
            {
                string path = AssetDatabase.GetAssetPath(originObject);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), $"{originObject.name}-{blendShapeSource.name}-{System.Guid.NewGuid().ToString()}.fbx");

                AssetDatabase.CopyAsset(path, tmp);
                AssetDatabase.Refresh();

                bool match = ExtractUnityBlendShapesIfVerticesEqual(meshDataList, blendShapeSource, originObject, tmp, 0, sourceAsBaseMesh);

                if (fixWeldVertices && !match)
                {
                    double[] tolerances = new double[] { 0.000016, 0.000018, 0.00002, 0.00005};
                    foreach (double tolerance in tolerances)
                    {
                        match = ExtractUnityBlendShapesIfVerticesEqual(meshDataList, blendShapeSource, originObject, tmp, tolerance, sourceAsBaseMesh);
                        if (match)
                        {
                            Debug.Log($"Fix Weld Vertices Problems Success @ tolerance {tolerance}");
                            break;
                        }
                    }
                }
                if (!match)
                {
                    Debug.LogWarning("Unity vertices can not match.");
                    foreach (var meshData in meshDataList)
                    {
                        meshData.m_VertexCount = -1;
                        meshData.m_VerticesHash = -1;
                    }
                }
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


#if ENABLE_FBX_SDK
        public static bool ExtractUnityBlendShapesIfVerticesEqual(List<MeshData> meshDataList, GameObject blendShapeSource, GameObject compareTarget,  string tmp, double tolerence, bool sourceAsBaseMesh)
        {
            if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, compareTarget, tmp, tolerence, sourceAsBaseMesh))
            {
                AssetDatabase.DeleteAsset(tmp);
                return false;
            }

            var genertated = AssetDatabase.LoadAssetAtPath<GameObject>(tmp);

            if (IsUnityVerticesEqual(genertated, compareTarget))
            {
                ExtractUnityBlendShapes(ref meshDataList, genertated, null);
                return true;
            }
            return false;
        }


        //Extracts blendshapes from a source FBX file and optionally transfers them to an origin FBX file. 

        public static bool ExtractFbxBlendshapes(
            ref List<MeshData> meshDataList, GameObject source, GameObject origin = null, string exportPath = "", double tolerance = 0, bool sourceAsBaseMesh = true)
        {
            if (meshDataList == null)
            {
                meshDataList = new List<MeshData>();
            }


            var fbxManager = FbxManager.Create();

            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);

            var sourceScene = FbxScene.Create(fbxManager, "Source");



            if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }


            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");



            if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", "Import source FBX...", 0.1f))
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
                if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", "Import origin FBX...", 0.2f))
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



            if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", "Get Root Node...", 0.4f))
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


                if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", $"Check node: {meshData.m_MeshName}", 0.4f + 0.4f * count++ / meshDataList.Count))
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

                if(originMesh != null && tolerance == 0)
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
                        {
                            originDeformer.AddBlendShapeChannel(CopyFbxBlendShapeChannel(channel, originMesh, tolerance, sourceAsBaseMesh ? sourceMesh : originMesh));
                        }

                        meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh, tolerance, sourceAsBaseMesh ? sourceMesh : originMesh));
                        
                    }
                }

            }

            if (originScene != null && originFbxManager != null)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Extract blendshapes", $"Export temporary FBX...", 0.9f))
                {
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                var exporter = FbxExporter.Create(originFbxManager, "");
                if (exporter.Initialize(exportPath, pFileFormat, originFbxManager.GetIOSettings()) == false)
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


        public static FbxBlendShapeChannel CopyFbxBlendShapeChannel(FbxBlendShapeChannel source, FbxMesh target, double tolerence, FbxMesh baseMesh)
        {

            if (baseMesh == null)
            {
                baseMesh = target;
            }
            else if (target.GetControlPointsCount() != baseMesh.GetControlPointsCount())
            {
                Debug.LogWarning("Base mesh control point count does not match the target mesh control point count. Use target as basis");
                baseMesh = target;
            }

            // Create a new FbxBlendShapeChannel with the same name as the source
            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(target, source.GetName());

            // Iterate over each target shape in the source blend shape channel
            int shapeCount = source.GetTargetShapeCount();

            for (int shapeIndex = 0; shapeIndex < source.GetTargetShapeCount(); shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);

                int controlPointCount = sourceShape.GetControlPointsCount();


                FbxShape newShape = FbxShape.Create(target, sourceShape.GetName());
                newShape.InitControlPoints(controlPointCount);

                Dictionary<FbxVector4, FbxVector4> deltaDict = new Dictionary<FbxVector4, FbxVector4>();

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var cp = baseMesh.GetControlPointAt(pointIndex);

                    var delta = GetMergedDeltaVector(deltaDict, source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex), cp, tolerence);
                    newShape.SetControlPointAt(target.GetControlPointAt(pointIndex) + delta, pointIndex);
                }

                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }

        /// <summary>
        /// Creates FbxBlendShapeData from a given FbxBlendShapeChannel and FbxMesh.
        /// </summary>
        /// <param name="source">The source FbxBlendShapeChannel to extract blend shape data from.</param>
        /// <param name="sourceMesh">The source FbxMesh containing the blend shape channel.</param>
        /// <param name="tolerance">The maximum allowable distance for merging delta vectors. A tolerance of 0 means no merging.</param>
        /// <param name="baseMesh">
        /// The base FbxMesh to use as a reference for computing blendshape offsets. If null, sourceMesh is used as the base.
        /// </param>
        /// <returns>
        /// An FbxBlendShapeData object containing the blend shape frames, or null if there's a control point count mismatch.
        /// </returns>
        public static FbxBlendShapeData GetFbxBlendShapeData(FbxBlendShapeChannel source, FbxMesh sourceMesh, double tolerance = 0, FbxMesh baseMesh = null)
        {
            int controlPointCount = sourceMesh.GetControlPointsCount();

            if(baseMesh == null)
            {
                baseMesh = sourceMesh;
            }
            else if (controlPointCount != baseMesh.GetControlPointsCount())
            {
                Debug.LogWarning("Base mesh control point count does not match the source mesh control point count. Use blendshape source as basis");
                baseMesh = sourceMesh;
            }

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


                Dictionary<FbxVector4, FbxVector4> deltaDict = new Dictionary<FbxVector4, FbxVector4>();

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var shapeControlPoint = sourceShape.GetControlPointAt(pointIndex);
                    var basicControlPoint = baseMesh.GetControlPointAt(pointIndex);

                    var deltaFbxVec = GetMergedDeltaVector(deltaDict, shapeControlPoint, basicControlPoint, tolerance);
                    var delta = new Vector4d(deltaFbxVec.X, deltaFbxVec.Y, deltaFbxVec.Z, deltaFbxVec.W);

                    if (!delta.IsZero())
                        frames[shapeIndex].AddDeltaControlPointAt(delta, pointIndex);
                }
            }

            return new FbxBlendShapeData(frames);
        }

        /// <summary>
        /// Computes the delta vector between a shape control point and a mesh control point,
        /// and attempts to merge it with a previously recorded delta vector if the difference is within a specified tolerance.
        /// This method addresses an issue where Unity's Weld Vertices option can lead to different vertex counts and order 
        /// when morph targets (blendshapes) separate vertices that would otherwise be welded.
        /// By merging the deltas of vertices that would be welded ( sharing the same position), the method ensures consistent vertex count and order.
        /// </summary>
        /// <param name="deltaDict">A dictionary storing previously computed delta vectors, with mesh control points as keys.</param>
        /// <param name="shapeControlPoint">The control point of the shape to compare.</param>
        /// <param name="meshControlPoint">The control point of the mesh to compare.</param>
        /// <param name="tolerance">The maximum allowable distance for merging delta vectors. A tolerance of 0 means no merging.</param>
        /// <returns>
        /// The delta vector between the shape control point and the mesh control point, 
        /// either as a new delta or a merged delta if within the specified tolerance.
        /// </returns>
        public static FbxVector4 GetMergedDeltaVector(Dictionary<FbxVector4, FbxVector4> deltaDict, FbxVector4 shapeControlPoint, FbxVector4 meshControlPoint, double tolerance)
        {

            var delta = shapeControlPoint - meshControlPoint;

            if (tolerance == 0)
            {
                return delta;
            }

            if(deltaDict == null)
            {
                deltaDict = new Dictionary<FbxVector4, FbxVector4>();
            }

            if (!deltaDict.TryGetValue(meshControlPoint, out FbxVector4 record))
            {
                deltaDict.Add(meshControlPoint, delta);
                return delta;
            }

            if (record.Distance(delta) < tolerance)
            {
                return record;
            }

            return delta;
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


        public static void ExtractUnityBlendShapes(ref List<MeshData> meshDataList, GameObject source, GameObject origin)
        {
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

                Mesh baseMesh = null;
                if (origin != null)
                {
                    Transform baseMeshTransform = origin.transform.Find(tansform.name);
                    if(baseMeshTransform != null)
                    {
                        baseMesh = baseMeshTransform.TryGetComponent(out SkinnedMeshRenderer baseMeshRenderer) ? baseMeshRenderer.sharedMesh : null;
                    }
                }
                meshData.ExtractUnityBlendShapes(sourceMesh, baseMesh);
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


        public static void ExtractUnityBlendShapes(this MeshData meshData, Mesh sourceMesh, Mesh baseMesh)
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


                    if(baseMesh != null && baseMesh != sourceMesh && baseMesh.vertexCount == sourceMesh.vertexCount)
                    {
                       for(int k = 0; k < sourceMesh.vertexCount; k++)
                        {
                            deltaVertices[k] += sourceMesh.vertices[k] - baseMesh.vertices[k];
                            deltaNormals[k] += sourceMesh.normals[k] - baseMesh.normals[k];
                            deltaTangents[k] += (Vector3) (sourceMesh.tangents[k] - baseMesh.tangents[k]);
                        }
                    }


                    UnityBlendShapeFrame frame = new UnityBlendShapeFrame(frameWeight, sourceMesh.vertexCount,
                        deltaVertices, deltaNormals, deltaTangents);

                    unityBlendShapeData.AddFrameAt(j, frame);
                }
                
                meshData.SetBlendShape(shapeName, unityBlendShapeData);
            }

        }


    }

}


