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
                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, weldVertices: false, sourceAsBaseMesh: sourceAsBaseMesh))
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

                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, originObject, tmp, fixWeldVertices, sourceAsBaseMesh))
                {
                    AssetDatabase.DeleteAsset(tmp);
                    return null;
                }
                var genertated = AssetDatabase.LoadAssetAtPath<GameObject>(tmp);

                if (IsUnityVerticesEqual(genertated, originObject))
                {
                    ExtractUnityBlendShapes(ref meshDataList, genertated, null);
                }
                else
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

        public static bool ExtractFbxBlendshapes(
            ref List<MeshData> meshDataList, GameObject source, GameObject origin = null, string exportPath = "", bool weldVertices = true, bool sourceAsBaseMesh = true)
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

                if (originMesh != null && !weldVertices)
                {
                    for (int dIndex = 0; dIndex < originMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); dIndex++)
                    {
                        originMesh.GetBlendShapeDeformer(dIndex).Destroy();
                    }
                }

                FbxBlendShape originDeformer = originMesh != null ? FbxBlendShape.Create(originMesh, "BlendShapeShareTemp") : null;

                var baseMesh = sourceAsBaseMesh ? sourceMesh : originMesh;



                var weldingGroups = weldVertices ? GetWeldingGroups(originMesh) : null;

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
                            originDeformer.AddBlendShapeChannel(CopyFbxBlendShapeChannel(channel, originMesh, baseMesh, weldingGroups));
                        }

                        meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh, weldingGroups, baseMesh));

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


        public static FbxBlendShapeChannel CopyFbxBlendShapeChannel(FbxBlendShapeChannel source, FbxMesh target, FbxMesh baseMesh, List<List<int>> weldingList)
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

                FbxVector4[] deltas = new FbxVector4[controlPointCount];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    if(weldingList == null)
                    {
                        newShape.SetControlPointAt(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex), pointIndex);

                    }
                    else
                    {
                        var cp = baseMesh.GetControlPointAt(pointIndex);
                        deltas[pointIndex] = source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex) - cp;
                    }
                }

                if(weldingList != null)
                {
                    foreach (var welding in weldingList)
                    {
                        if (welding.Count < 2)
                        {
                            continue;
                        }
                        FbxVector4 average = new FbxVector4(0, 0, 0, 0);
                        foreach (int index in welding)
                        {
                            average += deltas[index];
                        }
                        average /= welding.Count;
                        foreach (int index in welding)
                        {
                            deltas[index] = average;
                        }
                    }

                    for (int i = 0; i < deltas.Length; i++)
                    {
                        newShape.SetControlPointAt(target.GetControlPointAt(i) + deltas[i], i);
                    }
                }

                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);


            }
            return fbxBlendShapeChannel;
        }



        public static List<FbxVector4>[] GetNormals(FbxMesh mesh)
        {
            int cpCount = mesh.GetControlPointsCount();
            List<FbxVector4>[] normals = new List<FbxVector4>[cpCount];

            for (int i = 0; i < mesh.GetPolygonCount(); i++)
            {
                for (int j = 0; j < mesh.GetPolygonSize(i); j++)
                {
                    int cpIndex = mesh.GetPolygonVertex(i, j);
                    if (!mesh.GetPolygonVertexNormal(i, j, out FbxVector4 normal))
                    {
                        continue;
                    }

                    if (normals[cpIndex] == null)
                    {
                        normals[cpIndex] = new List<FbxVector4>();
                    }

                    normals[cpIndex].Add(normal);
                }
            }
            return normals;
        }



        /// <summary>
        /// Gets a list of vertex groups that should be welded together by checking if vertices share the same position
        /// and ensuring all blendshape vertices maintain the same position relative to each other.
        /// 
        /// </summary>
        /// <param name="mesh">The FbxMesh object containing the vertices and blendshapes to be analyzed.</param>
        /// <returns>A list of vertex groups, where each group is a list of indices that should be welded together.</returns>
        public static List<List<int>> GetWeldingGroups(FbxMesh mesh)
        {
            int count = mesh.GetControlPointsCount();
            Dictionary<FbxVector4, List<int>> controlPointPosition = new Dictionary<FbxVector4, List<int>>();

            for (int i = 0; i < count; i++)
            {
                FbxVector4 position = mesh.GetControlPointAt(i);
                if (!controlPointPosition.TryGetValue(position, out List<int> indices))
                {
                    indices = new List<int>();
                    controlPointPosition[position] = indices;
                }

                indices.Add(i);
            }
            var weldingGroup = controlPointPosition.Where(kv => kv.Value.Count > 1).Select(kv => kv.Value).ToList();


            for (int dIndex = 0; dIndex < mesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); dIndex++)
            {
                var deformer = mesh.GetBlendShapeDeformer(dIndex);

                for (int i = 0; i < deformer.GetBlendShapeChannelCount(); i++)
                {
                    var channel = deformer.GetBlendShapeChannel(i);

                    for (int j = 0; j < channel.GetTargetShapeCount(); j++)
                    {
                        FbxShape targetShape = channel.GetTargetShape(j);
                        weldingGroup = GroupWithShape(weldingGroup, targetShape);
                    }
                }
            }

            return weldingGroup;
        }

        private static List<List<int>> GroupWithShape(List<List<int>> groups, FbxShape targetShape)
        {
            List<List<int>> newGroups = new List<List<int>>();
            foreach (var group in groups)
            {
                Dictionary<FbxVector4, List<int>> newGroup = new Dictionary<FbxVector4, List<int>>();
                foreach (int index in group)
                {
                    var vertex = targetShape.GetControlPointAt(index);

                    if (!newGroup.TryGetValue(vertex, out List<int> indices))
                    {
                        indices = new List<int>();
                        newGroup[vertex] = indices;
                    }

                    indices.Add(index);
                }
                newGroups.AddRange(newGroup.Where(kv => kv.Value.Count > 1).Select(kv => kv.Value).ToList());
            }

            return newGroups;
        }


        // Creates FbxBlendShapeData from a given FbxBlendShapeChannel and FbxMesh.
        // The base FbxMesh to use as a reference for computing blendshape offsets. If null, sourceMesh is used as the base.
        public static FbxBlendShapeData GetFbxBlendShapeData(FbxBlendShapeChannel source, FbxMesh sourceMesh, List<List<int>> weldingGroup, FbxMesh baseMesh = null)
        {
            int controlPointCount = sourceMesh.GetControlPointsCount();

            if (baseMesh == null)
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


                FbxVector4[] deltas = new FbxVector4[controlPointCount];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var shapeControlPoint = sourceShape.GetControlPointAt(pointIndex);
                    var basicControlPoint = baseMesh.GetControlPointAt(pointIndex);
                    deltas[pointIndex] = shapeControlPoint - basicControlPoint;

                    if (weldingGroup == null)
                    {
                        var delta = new Vector4d(deltas[pointIndex].X, deltas[pointIndex].Y, deltas[pointIndex].Z, deltas[pointIndex].W);

                        if (!delta.IsZero())
                            frames[shapeIndex].AddDeltaControlPointAt(delta, pointIndex);
                    }
                }

                if(weldingGroup != null)
                {
                    foreach (var welding in weldingGroup)
                    {
                        if (welding.Count < 2)
                        {
                            continue;
                        }
                        FbxVector4 average = new FbxVector4(0, 0, 0, 0);
                        foreach (int index in welding)
                        {
                            average += deltas[index];
                        }
                        average /= welding.Count;
                        foreach (int index in welding)
                        {
                            deltas[index] = average;
                        }
                    }

                    for (int i = 0; i < deltas.Length; i++)
                    {
                        var delta = new Vector4d(deltas[i].X, deltas[i].Y, deltas[i].Z, deltas[i].W);

                        if (!delta.IsZero())
                            frames[shapeIndex].AddDeltaControlPointAt(delta, i);
                    }
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


