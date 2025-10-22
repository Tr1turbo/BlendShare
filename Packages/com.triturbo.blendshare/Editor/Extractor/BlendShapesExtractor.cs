using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using UnityEditor;
using System.Linq;

using Triturbo.BlendShapeShare.BlendShapeData;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using System.Threading.Tasks;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using Triturbo.BlendShapeShare.Util;
# endif


namespace Triturbo.BlendShapeShare.Extractor
{
    public class BlendShapesExtractorOptions
    {
        public enum BaseMesh
        {
            Source,
            Original
        }

        public BaseMesh baseMesh = BaseMesh.Source;
        public bool weldVertices = true;
        public bool applyRotation = false;
        public bool applyScale = false;
        public bool applyTranslate = false;
        public float blendShapesScale = 1;

        public bool ApplyTransform => applyRotation || applyScale || applyTranslate;

        public FbxAMatrix GetTransform(FbxAMatrix originalMatrix, FbxAMatrix sourceMatrix)
        {
            if (originalMatrix == null)
            {
                originalMatrix = new FbxAMatrix();
                originalMatrix.SetIdentity();
            }
            FbxAMatrix relativeTransform;
            if (ApplyTransform)
            {
                relativeTransform = sourceMatrix * originalMatrix.Inverse();
                sourceMatrix.Dispose();
                originalMatrix.Dispose();

                if (!applyScale)
                {
                    relativeTransform.SetS(new FbxVector4(1, 1, 1, 1));
                }
                if (!applyRotation)
                {
                    relativeTransform.SetR(new FbxVector4(0, 0, 0, 0));
                }
                if (!applyTranslate)
                {
                    relativeTransform.SetT(new FbxVector4(0, 0, 0, 0));
                }
            }
            else
            {
                relativeTransform = new FbxAMatrix();
                relativeTransform.SetIdentity();
            }
            return relativeTransform;
        }
    }

    public static class BlendShapesExtractor
    {
        public static BlendShapeDataSO ExtractBlendShapes(GameObject blendShapeSource, GameObject originObject, List<MeshData> meshDataList,
           BlendShapesExtractorOptions blendShapesExtractorOptions)
        {
            var data = ScriptableObject.CreateInstance<BlendShapeDataSO>();
            data.m_Original = originObject;
            string defaultName = $"{originObject.name}-{blendShapeSource.name}";
            bool sourceAsBaseMesh = blendShapesExtractorOptions.baseMesh == BlendShapesExtractorOptions.BaseMesh.Source;

#if ENABLE_FBX_SDK
            if (IsUnityVerticesEqual(blendShapeSource, originObject) && !blendShapesExtractorOptions.ApplyTransform)
            {
                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, blendShapesExtractorOptions))
                {
                    Debug.Log("ExtractFbxBlendshapes failed");
                    return null;
                }
                ExtractUnityBlendShapes(ref meshDataList, blendShapeSource, sourceAsBaseMesh ? blendShapeSource : originObject);
            }
            else
            {
                
                string path = AssetDatabase.GetAssetPath(originObject);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), $"{originObject.name}-{blendShapeSource.name}-{System.Guid.NewGuid().ToString()}.fbx");

                AssetDatabase.CopyAsset(path, tmp);
                AssetDatabase.Refresh();

                
                if (!ExtractFbxBlendshapes(ref meshDataList, blendShapeSource, blendShapesExtractorOptions, originObject, tmp))
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

        #region Path 

        /// <summary>
        /// Recursively builds a dictionary mapping mesh names to their relative paths.
        /// </summary>
        private static void BuildMeshPathLookup(Dictionary<string, string> lookup, Transform current, string parentPath = null)
        {
            string currentPath = string.IsNullOrEmpty(parentPath) ? current.name : $"{parentPath}/{current.name}";
            if (current.TryGetComponent(out SkinnedMeshRenderer meshRenderer))
            {
                if (meshRenderer.sharedMesh == null)
                {
                    Debug.LogError($"SharedMesh is null in {meshRenderer.name}");
                }
                if (!lookup.ContainsKey(meshRenderer.sharedMesh.name))
                {
                    lookup[meshRenderer.sharedMesh.name] = currentPath;
                    Debug.Log($"LookUP:{meshRenderer.sharedMesh.name} - {currentPath}");
                }
            }
            foreach (Transform child in current)
            {
                BuildMeshPathLookup(lookup, child, parentPath == null ? "" : currentPath);
            }
        }
        
        private static string GetRelativePath(Transform target, Transform root)
        {
            if (target == root) return "";
            return GetRelativePath(target.parent, root) + (target.parent == root ? "" : "/") + target.name;
        }

        #endregion
       

        #region FBX SDK

        #if ENABLE_FBX_SDK
        
        internal static bool ExtractFbxBlendshapes(ref List<MeshData> meshDataList, GameObject source, 
            BlendShapesExtractorOptions blendShapesExtractorOptions, GameObject origin = null, string exportPath = "")
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
                if (EditorUtility.DisplayCancelableProgressBar("Extract BlendShapes", "Import origin FBX...", 0.2f))
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

            
            if (EditorUtility.DisplayCancelableProgressBar("Extract BlendShapes", "Get Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = sourceScene.GetRootNode();
            var originRootNode = originScene?.GetRootNode();

            int count = 0;
            foreach (var meshData in meshDataList)
            {
                FbxNode node = sourceRootNode.FindMeshChild(meshData.m_MeshName);
                FbxMesh sourceMesh = node?.GetMesh();
                if (EditorUtility.DisplayCancelableProgressBar("Extract BlendShapes", $"Check node: {meshData.m_MeshName}", 0.4f + 0.4f * count++ / meshDataList.Count))
                {
                    EditorUtility.ClearProgressBar();
                    return false;
                }
                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                int sourceMeshBlendShapeDeformerCount = sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
                
                if (sourceMeshBlendShapeDeformerCount < 1)
                {
                    continue;
                }
                FbxNode nodeOrigin = originRootNode?.FindMeshChild(meshData.m_MeshName);
                FbxMesh originMesh = nodeOrigin?.GetMesh();

                if (originMesh != null)
                {
                    int sourceVertexCount = sourceMesh.GetControlPointsCount();
                    int originVertexCount = originMesh.GetControlPointsCount();

                    if (sourceVertexCount != originVertexCount)
                    {
                        Debug.LogError($"Vertex count mismatch: Source ({sourceVertexCount}) vs Origin ({originVertexCount})");
                    }
                }
                
                //FbxAMatrix relativeTransform;
                FbxAMatrix relativeTransform = blendShapesExtractorOptions.GetTransform(
                    nodeOrigin?.EvaluateLocalTransform(), 
                    node.EvaluateLocalTransform());
                
                //Remove all blendshapes in original mesh and make it a container as new blendshapes.
                if (originMesh != null && !blendShapesExtractorOptions.weldVertices)
                {
                    for (int dIndex = 0; dIndex < originMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); dIndex++)
                    {
                        originMesh.GetBlendShapeDeformer(dIndex).Destroy();
                    }
                }

                FbxBlendShape originDeformer = originMesh != null ? FbxBlendShape.Create(originMesh, "BlendShare") : null;
                bool sourceAsBaseMesh = blendShapesExtractorOptions.baseMesh == BlendShapesExtractorOptions.BaseMesh.Source;
                var baseMesh = sourceAsBaseMesh ? sourceMesh : originMesh;
                
                var weldingGroups = blendShapesExtractorOptions.weldVertices && originMesh != null ? GetWeldingGroups(originMesh) : null;

                for (int dIndex = 0; dIndex < sourceMeshBlendShapeDeformerCount; dIndex++)
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
                            originDeformer.AddBlendShapeChannel(CopyFbxBlendShapeChannel(channel, originMesh, baseMesh, weldingGroups, relativeTransform));
                        }
                        meshData.SetBlendShape(name, GetFbxBlendShapeData(channel, sourceMesh, weldingGroups, relativeTransform, baseMesh));
                    }
                }
                relativeTransform.Dispose();
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
        
        
        
        //base mesh: use for calculate vertices diff
        private static FbxBlendShapeChannel CopyFbxBlendShapeChannel(FbxBlendShapeChannel source, FbxMesh target, FbxMesh baseMesh, 
            List<List<int>> weldingList, FbxAMatrix transformMatrix)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            if (baseMesh == null)
            {
                baseMesh = target;
            }
            else if (target.GetControlPointsCount() != baseMesh.GetControlPointsCount())
            {
                Debug.LogWarning($"Base mesh vertices count does not match the target mesh vertices count. Using target mesh as basis.");
                baseMesh = target;
            }

            // Create a new FbxBlendShapeChannel with the same name as the source
            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(target, source.GetName());

            // Iterate over each target shape in the source blend shape channel
            int shapeCount = source.GetTargetShapeCount();

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape sourceShape = source.GetTargetShape(shapeIndex);
                int controlPointCount = sourceShape.GetControlPointsCount();
                
                FbxShape newShape = FbxShape.Create(target, sourceShape.GetName());
                newShape.InitControlPoints(controlPointCount);

                FbxVector4[] deltas = new FbxVector4[controlPointCount];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var baseControlPoint = baseMesh.GetControlPointAt(pointIndex);
                    
                    if (weldingList == null)
                    {
                        FbxVector4 delta;
                        if (baseMesh != target)
                            delta = transformMatrix.MultT(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex) - baseControlPoint);
                        else
                            delta = transformMatrix.MultT(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex)) - baseControlPoint;
                        
                        FbxVector4 transformedPoint = target.GetControlPointAt(pointIndex) + delta;
                        newShape.SetControlPointAt(transformedPoint, pointIndex);
                    }
                    else
                    { 
                        if (baseMesh != target)
                            deltas[pointIndex] = transformMatrix.MultT(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex) - baseControlPoint);
                        else
                            deltas[pointIndex] = transformMatrix.MultT(source.GetTargetShape(shapeIndex).GetControlPointAt(pointIndex)) - baseControlPoint;
                    }
                }
                
                if (ApplyWeldingDeltas(deltas, weldingList))
                {
                    for (int i = 0; i < deltas.Length; i++)
                    {
                        FbxVector4 transformedPoint = target.GetControlPointAt(i) + deltas[i];
                        newShape.SetControlPointAt(transformedPoint, i);
                    }  
                }
                
                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }

            stopwatch.Stop();
            Debug.Log($"CopyFbxBlendShapeChannel: {source.GetName()} Time taken: " + stopwatch.ElapsedMilliseconds + " ms");
            return fbxBlendShapeChannel;
        }
        
        private static bool ApplyWeldingDeltas(FbxVector4[] deltas, List<List<int>> weldingList)
        {
            if (weldingList == null || deltas == null)
            {
                return false;
            }

            foreach (var welding in weldingList)
            {
                if (welding == null || welding.Count < 2)
                {
                    continue;
                }

                FbxVector4 average = new FbxVector4(0, 0, 0, 0);
                int mergedCount = 0;

                foreach (int index in welding)
                {
                    if (index >= 0 && index < deltas.Length)
                    {
                        average += deltas[index];
                        mergedCount++;
                    }
                }
                
                average /= mergedCount;
                foreach (int index in welding)
                {
                    if (index >= 0 && index < deltas.Length)
                    {
                        deltas[index] = average;
                    }
                }
            }

            return true;
        }


        internal static List<FbxVector4>[] GetNormals(FbxMesh mesh)
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
        internal static List<List<int>> GetWeldingGroups(FbxMesh mesh)
        {
            //
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            //
            
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


            stopwatch.Stop();
            Debug.Log("GetWeldingGroups Time taken: " + stopwatch.ElapsedMilliseconds + " ms");

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
        internal static FbxBlendShapeData GetFbxBlendShapeData(FbxBlendShapeChannel source, FbxMesh sourceMesh, List<List<int>> weldingGroup,
           FbxAMatrix transformMatrix, FbxMesh baseMesh = null)
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


                    if (baseMesh == sourceMesh)
                    {
                        deltas[pointIndex] = transformMatrix.MultT(shapeControlPoint - basicControlPoint);
                    }
                    else
                    {
                        deltas[pointIndex] = transformMatrix.MultT(shapeControlPoint) - basicControlPoint;
                    }
                    

                    if (weldingGroup == null)
                    {
                        var delta = new Vector4d(deltas[pointIndex].X, deltas[pointIndex].Y, deltas[pointIndex].Z, deltas[pointIndex].W);

                        if (!delta.IsZero())
                            frames[shapeIndex].AddDeltaControlPointAt(delta, pointIndex);
                    }
                }
                
                if (ApplyWeldingDeltas(deltas, weldingGroup))
                {
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

        #endregion


        internal static List<MeshData> CompareBlendShape(GameObject source, GameObject origin, bool compareByName = true)
        {
            List<MeshData> meshDataList = new List<MeshData>();
            Dictionary<string, SkinnedMeshRenderer> originMeshLookup = new Dictionary<string, SkinnedMeshRenderer>();

            // Create a lookup dictionary for origin mesh renderers
            foreach (var skinnedMesh in origin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                originMeshLookup[skinnedMesh.name] = skinnedMesh;
            }

            foreach (var meshRenderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh sourceMesh = meshRenderer.sharedMesh;
                if (sourceMesh == null)
                {
                    continue;
                }

                // Use lookup dictionary for faster search
                if (!originMeshLookup.TryGetValue(meshRenderer.name, out SkinnedMeshRenderer originRenderer) || originRenderer.sharedMesh == null)
                {
                    Debug.LogError($"Cannot find matching SkinnedMeshRenderer for {meshRenderer.name} in origin: {origin.name}");
                    continue;
                }

                Mesh originMesh = originRenderer.sharedMesh;
                List<string> extraBlendShapes;

                if (compareByName)
                {
                    extraBlendShapes = GetExtraBlendShapeNames(sourceMesh, originMesh);
                }
                else
                {
                    extraBlendShapes = new List<string>();
                    for (int i = originMesh.blendShapeCount; i < sourceMesh.blendShapeCount; i++)
                    {
                        extraBlendShapes.Add(sourceMesh.GetBlendShapeName(i));
                    }
                }

                MeshData meshData = new MeshData(originMesh, extraBlendShapes);
                meshDataList.Add(meshData);
            }
            return meshDataList;
        }


        
        #region Unity Mesh
        
        private static bool IsUnityVerticesEqual(GameObject source, GameObject origin)
        {
            foreach (Transform transform in source.transform)
            {
                Mesh sourceMesh = transform.TryGetComponent(out SkinnedMeshRenderer meshRenderer) ? meshRenderer.sharedMesh : null;
                if (sourceMesh == null)
                {
                    continue;
                }
                
                string relativePath = GetRelativePath(transform, source.transform);

                Mesh originMesh = origin.transform.Find(relativePath)?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
                if (originMesh == null)
                {
                    Debug.LogError($"Can not find {transform.name} in origin: {origin.name}");
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
        private static void ExtractUnityBlendShapes(ref List<MeshData> meshDataList, GameObject source, GameObject baseObject)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            // Convert meshDataList to a dictionary for faster lookup
            Dictionary<string, MeshData> meshDataDict = meshDataList.ToDictionary(m => m.m_MeshName);

            // Get all skinned mesh renderers in source
            var skinnedMeshRenderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (var meshRenderer in skinnedMeshRenderers)
            {
                Mesh sourceMesh = meshRenderer.sharedMesh;
                if (sourceMesh == null)
                {
                    continue;
                }

                // Look up MeshData by name
                if (!meshDataDict.TryGetValue(sourceMesh.name, out MeshData meshData))
                {
                    continue;
                }

                Mesh baseMesh = null;
                if (source == baseObject)
                {
                    baseMesh = sourceMesh;
                }
                else if (baseObject != null)
                {
                    // Get relative path and find the corresponding mesh in baseObject
                    string relativePath = GetRelativePath(meshRenderer.transform, source.transform);
                    Transform baseMeshTransform = baseObject.transform.Find(relativePath);
                
                    if (baseMeshTransform != null && baseMeshTransform.TryGetComponent(out SkinnedMeshRenderer baseMeshRenderer))
                    {
                        baseMesh = baseMeshRenderer.sharedMesh;
                    }
                }

                meshData.ExtractUnityBlendShapes(sourceMesh, baseMesh);
            }

            stopwatch.Stop();
            Debug.Log("Extract Unity BlendShapes Time taken: " + stopwatch.ElapsedMilliseconds + " ms");
        }

        private static List<string> GetExtraBlendShapeNames(Mesh sourceMesh, Mesh origin)
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


        private static void ExtractUnityBlendShapes(this MeshData meshData, Mesh sourceMesh, Mesh baseMesh)
        {
            bool calculateDiffs = baseMesh != null && baseMesh != sourceMesh && baseMesh.vertexCount == sourceMesh.vertexCount;
            
            Vector3[] vertexDiffs = new Vector3[sourceMesh.vertexCount];
            Vector3[] normalDiffs = new Vector3[sourceMesh.vertexCount];
            Vector3[] tangentDiffs = new Vector3[sourceMesh.vertexCount];

            if (calculateDiffs)
            {
                Vector3[] vertices, normals;
                Vector4[] tangents;
                vertices = sourceMesh.vertices;
                normals = sourceMesh.normals;
                tangents = sourceMesh.tangents;

                Vector3[] baseVertices, baseNormals;
                Vector4[] baseTangents;
                baseVertices = baseMesh.vertices;
                baseNormals = baseMesh.normals;
                baseTangents = baseMesh.tangents;

                Parallel.For(0, sourceMesh.vertexCount, k =>
                {
                    vertexDiffs[k] = vertices[k] - baseVertices[k];
                    normalDiffs[k] = normals[k] - baseNormals[k];
                    tangentDiffs[k] = (Vector3)(tangents[k] - baseTangents[k]);
                });
            }


            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(i);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(i);
                if (!meshData.ContainsBlendShape(shapeName))
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

                    if (calculateDiffs)
                    {
                        for (int k = 0; k < sourceMesh.vertexCount; k++)
                        {
                            deltaVertices[k] += vertexDiffs[k];
                            deltaNormals[k] += normalDiffs[k];
                            deltaTangents[k] += tangentDiffs[k];
                        }
                    }
                    
                    UnityBlendShapeFrame frame = new UnityBlendShapeFrame(frameWeight, sourceMesh.vertexCount,
                        deltaVertices, deltaNormals, deltaTangents);

                    unityBlendShapeData.AddFrameAt(j, frame);
                }

                meshData.SetBlendShape(shapeName, unityBlendShapeData);
            }

        }

        #endregion
        

    }

}


