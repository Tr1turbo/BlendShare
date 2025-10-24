using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using System.Linq;

using UnityEditor;
using Object = UnityEngine.Object;
#if ENABLE_FBX_SDK
using Triturbo.BlendShapeShare.Util;
using Autodesk.Fbx;
#endif



namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class BlendShapeAppender
    {
        // targetMeshContainer can be an FBX or GeneratedMeshAssetSO
        public static GeneratedMeshAssetSO CreateMeshAsset(Object targetMeshContainer, IEnumerable<BlendShapeDataSO> blendShapes, string path)
        {
            blendShapes = blendShapes.ToList();
            
            Dictionary<string, Mesh> targetMeshes =  MeshUtil.GetMeshes(targetMeshContainer);
            Dictionary<string, Mesh> generatedMeshes = new();

            List<BlendShapeDataSO> appliedBlendShapes;
            
            var targetFbx = targetMeshContainer as GameObject;
            if (targetMeshContainer is GeneratedMeshAssetSO so)
            {
                targetFbx = so.m_OriginalFbxAsset;
                appliedBlendShapes = so.m_AppliedBlendShapes
                    .Concat(blendShapes)
                    .Where(b => b != null)
                    .Distinct() // Prevent duplicates by reference
                    .ToList();            }
            else
            {
                appliedBlendShapes = blendShapes.ToList();
            }

            if (targetFbx != null && !IsAllMeshesValid(blendShapes, targetMeshes.Values))
            {
                // If invalid, rebuild the FBX immediately
                string folder = System.IO.Path.GetDirectoryName(path) ?? Application.dataPath;
                string tempAssetPath = System.IO.Path.Combine(folder, $"{targetMeshContainer.name}-{System.Guid.NewGuid().ToString()}.fbx");
                
                if (!CreateFbx(targetFbx, appliedBlendShapes, tempAssetPath, true))
                {
                    Debug.LogError("Failed to create blendshapes fbx.");
                    return null;
                }
                var result = GeneratedMeshAssetSO.SaveMeshesToAsset(targetFbx, appliedBlendShapes, tempAssetPath, path); 
                AssetDatabase.MoveAssetToTrash(tempAssetPath);
                return result;
            }
            
            foreach (BlendShapeDataSO data in blendShapes)
            {
                foreach (var meshData in data.m_MeshDataList)
                {
                    if (!generatedMeshes.TryGetValue(meshData.m_MeshName, out Mesh targetMesh))
                    {
                        targetMesh = targetMeshes.GetValueOrDefault(meshData.m_MeshName) ?? meshData.m_OriginMesh;
                    }
                    var mesh = CreateBlendShapesMesh(meshData, targetMesh);
                    if (mesh == null)
                    {
                        Debug.LogError($"Failed to create blendshapes mesh for {meshData.m_MeshName} in {data.name}");
                        continue;
                    }
                
                    mesh.name = meshData.m_MeshName;
                    
                    generatedMeshes[meshData.m_MeshName] = mesh;
                }
            }

            if (generatedMeshes.Count == 0)
            {
                return null;
            }
            return GeneratedMeshAssetSO.SaveMeshesToAsset(targetFbx, appliedBlendShapes, generatedMeshes.Values, path);
        }
        
        public static GeneratedMeshAssetSO CreateMeshAsset(this BlendShapeDataSO so, string path)
        {
            return CreateMeshAsset(so.m_Original, new[]{so}, path);
        }
        
        [Obsolete("CreateMeshAsset(List<Mesh>, string) is deprecated. Use GeneratedMeshAssetSO.SaveMeshesToAsset() instead.")]
        public static GeneratedMeshAssetSO CreateMeshAsset(List<Mesh> meshesList, string path)
        {
            return GeneratedMeshAssetSO.SaveMeshesToAsset(null, null, meshesList, path);
        }
        
        
        [Obsolete("CreateMeshAsset(this BlendShapeDataSO, string, GameObject) is deprecated. Use GeneratedMeshAssetSO.SaveMeshesToAsset() instead.")]
        public static GeneratedMeshAssetSO CreateMeshAsset(this BlendShapeDataSO so, string path, GameObject target)
        {
            return GeneratedMeshAssetSO.SaveMeshesToAsset(null, new[]{so}, target, path);
        }
        
        
        #region Private Methods
        private static Mesh CreateBlendShapesMesh(MeshData meshBlendShapesData, Mesh target)
        {
            if (!meshBlendShapesData.IsValidTarget(target)) return null;

            // Create a copy of the target mesh to modify
            Mesh newMesh = Object.Instantiate(target);

            // Check if any blend shapes already exist in the target mesh
            bool exist = false;
            foreach (var blendShapeData in meshBlendShapesData.BlendShapes)
            {
                var index = newMesh.GetBlendShapeIndex(blendShapeData.m_ShapeName);
                if (index != -1)
                {
                    exist = true;
                    break;
                }
            }

            // If blend shapes already exist, remove all existing shapes and add back those not defined in meshBlendShapesData
            if (exist)
            {
                List<(string, UnityBlendShapeData)> blendshapes = new List<(string, UnityBlendShapeData)>();

                // Collect existing blend shapes that are not defined in meshBlendShapesData

                for (int i = 0; i < newMesh.blendShapeCount; i++)
                {
                    string name = newMesh.GetBlendShapeName(i);
                    if (!meshBlendShapesData.ContainsBlendShape(name))
                    {
                        blendshapes.Add((name, new UnityBlendShapeData(newMesh, i)));
                    }
                    else
                    {
                        blendshapes.Add((name, meshBlendShapesData.GetBlendShape(name).m_UnityBlendShapeData));
                    }
                }

                // Clear all blend shapes from the target mesh
                // Add back the collected blend shapes to the target mesh

                newMesh.ClearBlendShapes();
                foreach (var blendShape in blendshapes)
                {
                    foreach (var frame in blendShape.Item2.m_Frames)
                    {
                        frame.AddBlendShapeFrame(ref newMesh, blendShape.Item1);
                    }
                }
            }

            // Apply the new blend shapes from meshBlendShapesData to the target mesh
            foreach (var blendShapeData in meshBlendShapesData.BlendShapes)
            {
                if (newMesh.GetBlendShapeIndex(blendShapeData.m_ShapeName) != -1) continue;

                foreach (var frame in blendShapeData.m_UnityBlendShapeData.m_Frames)
                {
                    frame.AddBlendShapeFrame(ref newMesh, blendShapeData.m_ShapeName);
                }
            }
            return newMesh;
        }
        public static bool IsAllMeshesValid(IEnumerable<BlendShapeDataSO> blendShapes, IEnumerable<Mesh> meshes)
        {
            List<Mesh> meshList = meshes.ToList();
            foreach (BlendShapeDataSO data in blendShapes) { 
                foreach (var meshData in data.m_MeshDataList)
                {
                    var targetMesh =  meshList.FirstOrDefault(m => m.name == meshData.m_MeshName) ?? meshData.m_OriginMesh;
                    if (!meshData.IsValidTarget(targetMesh)) return false;
                } 
            }
            return true;
        }

        #endregion
        
        #region Autodesk FBX SDK
        
        /// <summary>
        /// Creates a new FBX file from the specified source GameObject and applies blend shape data to it.
        /// </summary>
        /// <param name="source">The source FBX <see cref="GameObject"/> to extract mesh and FBX data from.</param>
        /// <param name="blendShapes">A collection of <see cref="BlendShapeDataSO"/> containing blend shape data to be added to the FBX.</param>
        /// <param name="outputPath">
        /// Optional. The file path where the generated FBX should be saved.  
        /// If null or empty, the original asset path of the source GameObject is used.
        /// </param>
        /// <param name="onlyNecessary">
        /// If true, removes unused mesh nodes from the FBX, keeping only the nodes modified by the provided blend shapes.
        /// </param>
        /// <returns>
        /// Returns <c>true</c> if the FBX was successfully created and exported;  
        /// otherwise, returns <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This method requires the FBX SDK to be enabled (compiled with <c>ENABLE_FBX_SDK</c>).
        /// It creates an FBX scene, imports the source FBX, applies blend shape data to matching mesh nodes,  
        /// optionally removes unmodified meshes, and then exports the updated scene to the specified path.
        /// </remarks>
        public static bool CreateFbx(GameObject source, IEnumerable<BlendShapeDataSO> blendShapes, string outputPath = null, bool onlyNecessary = false)
        {
#if !ENABLE_FBX_SDK
            return false;
#endif

            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, source.name);

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(source), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }
            fbxImporter.Import(scene);
            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = scene.GetRootNode();

            
            HashSet<FbxNode> modifiedNode = new();
            foreach (var so in blendShapes)
            {
                foreach (var meshData in so.m_MeshDataList)
                {
                    FbxNode node = sourceRootNode.FindMeshChild(meshData.m_MeshName);
                    AddBlendShapes(so, meshData, node);
                    modifiedNode.Add(node);
                }
            }
            
            if (onlyNecessary)
                DeleteFbxNodesWithMesh(sourceRootNode, modifiedNode, false);

            
            var exporter = FbxExporter.Create(fbxManager, "");

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = AssetDatabase.GetAssetPath(source);
            }
            else
            {
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), outputPath);
            }
            AssetDatabase.Refresh();

            if (exporter.Initialize(outputPath, pFileFormat, fbxManager.GetIOSettings()) == false)
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }
            exporter.Export(scene);
            exporter.Destroy();

            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            return true;
        }
        
        public static bool CreateFbx(this BlendShapeDataSO so, GameObject source, string outputPath = null)
        {
            return CreateFbx(source, new[] { so }, outputPath, false);
        }
        
        public static bool RemoveBlendShapes(this BlendShapeDataSO so, GameObject target, bool removeInAllDeformer = true)
        {
#if !ENABLE_FBX_SDK
            return false;
#endif
            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, target.name);

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(target), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }

            fbxImporter.Import(scene);
            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = scene.GetRootNode();



            foreach (var meshData in so.m_MeshDataList)
            {
                FbxNode node = sourceRootNode.FindMeshChild(meshData.m_MeshName);
                FbxMesh sourceMesh = node?.GetMesh();
                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                if (sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) < 1)
                {
                    continue;
                }

                so.GetDeformer(sourceMesh, false)?.Destroy();
                if (removeInAllDeformer)
                {
                    for (int i = 0; i < sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
                    {
                        var deformer = sourceMesh.GetBlendShapeDeformer(i);
                        for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                        {
                            var name = deformer.GetBlendShapeChannel(j).GetName();
                            if (meshData.ContainsBlendShape(name))
                            {
                                deformer.RemoveBlendShapeChannel(deformer.GetBlendShapeChannel(j));

                            }
                        }
                    }
                }
            }
            var exporter = FbxExporter.Create(fbxManager, "");
            if (exporter.Initialize(AssetDatabase.GetAssetPath(target), pFileFormat, fbxManager.GetIOSettings()) == false)
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }
            exporter.Export(scene);
            exporter.Destroy();

            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            return true;
        }

        #region Private Methods
#if ENABLE_FBX_SDK
        private static FbxBlendShape GetDeformer(this BlendShapeDataSO so, FbxMesh targetMesh, bool create = true)
        {
            if(!string.IsNullOrEmpty(so.m_DeformerID))
            {
                int deformerCount = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
                for (int i = deformerCount - 1; i >= 0; i--)
                {
                    var deformer = targetMesh.GetBlendShapeDeformer(i);
                    if (deformer.GetName() == so.m_DeformerID) return deformer;
                }
            }
            if (create) return FbxBlendShape.Create(targetMesh, so.m_DeformerID);
            return null;
        }
        
        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(string name, FbxMesh mesh, FbxBlendShapeData fbxBlendShapeData)
        {
            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(mesh, name);
            int controlPointCount = mesh.GetControlPointsCount();
            
            int shapeCount = fbxBlendShapeData.m_Frames.Length;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape newShape = FbxShape.Create(mesh, name);
                newShape.InitControlPoints(controlPointCount);

                FbxBlendShapeFrame frame = fbxBlendShapeData.m_Frames[shapeIndex];
                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var d = frame.GetDeltaControlPointAt(pointIndex);
                    var controlPoint = mesh.GetControlPointAt(pointIndex) + new FbxVector4(d.m_X, d.m_Y, d.m_Z, d.m_W);
                    newShape.SetControlPointAt(controlPoint, pointIndex);
                }
                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }
        
        //Add TargetShape to FbxBlendShapeChannel from FbxBlendShapeData
        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(FbxBlendShapeChannel fbxBlendShapeChannel, FbxMesh mesh, FbxBlendShapeData fbxBlendShapeData)
        {
            int controlPointCount = mesh.GetControlPointsCount();
            int shapeCount = fbxBlendShapeData.m_Frames.Length;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {

                FbxShape newShape = FbxShape.Create(mesh, fbxBlendShapeChannel.GetName());
                newShape.InitControlPoints(controlPointCount);

                FbxBlendShapeFrame frame = fbxBlendShapeData.m_Frames[shapeIndex];
                
                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var d = frame.GetDeltaControlPointAt(pointIndex);
                    var controlPoint = mesh.GetControlPointAt(pointIndex) + new FbxVector4(d.m_X, d.m_Y, d.m_Z, d.m_W);
                    newShape.SetControlPointAt(controlPoint, pointIndex);
                }
                
                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }

        
        private static bool AddBlendShapes(BlendShapeDataSO so, MeshData meshData, FbxNode node)
        {
            FbxMesh targetMesh = node?.GetMesh();
            if (targetMesh == null)
            {
                Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                return false;
            }
            so.GetDeformer(targetMesh, false)?.Destroy();
            HashSet<string> existingBlendshapes = new HashSet<string>();

            // overwrite blend shape define in list
            for (int i = 0; i < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
            {
                var d = targetMesh.GetBlendShapeDeformer(i);
                for (int j = d.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                {
                    var name = d.GetBlendShapeChannel(j).GetName();
                    if (meshData.ContainsBlendShape(name))
                    {
                        Debug.LogWarning($"Warning: The blendshape with the name '{name}' already exists in the node '{node.GetName()}'. The existing blendshape was overwritten.");
                        var channel = d.GetBlendShapeChannel(j);
                        int shapeCount = channel.GetTargetShapeCount();

                        //Clear all existing shapes
                        for (int shape = 0; shape < shapeCount; shape++)
                        {
                            channel.RemoveTargetShape(channel.GetTargetShape(shape));
                        }
                        CreateFbxBlendShapeChannel(channel, targetMesh, meshData.GetBlendShape(name).m_FbxBlendShapeData);
                        existingBlendshapes.Add(name);
                    }
                }
            }

            var deformer = so.GetDeformer(targetMesh);
            foreach (var blend in meshData.BlendShapes)
            {
                if (existingBlendshapes.Contains(blend.m_ShapeName)) continue;
                deformer.AddBlendShapeChannel(CreateFbxBlendShapeChannel(blend.m_ShapeName, targetMesh, blend.m_FbxBlendShapeData));
            }
            return true;
        }
        
        private static void DeleteFbxNodesWithMesh(FbxNode entry, IEnumerable<FbxNode> exceptions, bool recursive)
        {
            exceptions = exceptions.ToArray();
            for (int i = entry.GetChildCount() - 1; i >= 0; i--)
            {
                var node = entry.GetChild(i);
                if (!exceptions.Contains(node) && node.GetMesh() != null)
                {
                    node.Destroy();
                }
                else if(recursive)
                {
                    DeleteFbxNodesWithMesh(node, exceptions, true);
                }
            }
        }
#endif
        
        #endregion

        #endregion
    }
}

