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
        public static GeneratedMeshAssetSO CreateMeshAsset(Object targetMeshContainer, IEnumerable<BlendShareObject> blendShares, string path)
        {
            return BlendShareGenerationService.CreateMeshAsset(targetMeshContainer, blendShares, path);
        }

        // targetMeshContainer can be an FBX or GeneratedMeshAssetSO
        [Obsolete("Use BlendShareGenerationService.CreateMeshAsset(Object, IEnumerable<BlendShareObject>, string) instead.")]
        public static GeneratedMeshAssetSO CreateMeshAsset(Object targetMeshContainer, IEnumerable<BlendShapeDataSO> blendShapes, string path)
        {
            var upgraded = blendShapes?
                .Where(blendShape => blendShape != null)
                .Select(BlendShareUpgradeService.UpgradeSideBySide)
                .Where(blendShare => blendShare != null)
                .ToArray() ?? Array.Empty<BlendShareObject>();
            return BlendShareGenerationService.CreateMeshAsset(targetMeshContainer, upgraded, path);
        }
        
        public static GeneratedMeshAssetSO CreateMeshAsset(this BlendShareObject so, string path)
        {
            return CreateMeshAsset(so.m_Original, new[] { so }, path);
        }

        [Obsolete("Use BlendShareObject.CreateMeshAsset compatibility overload or BlendShareGenerationService instead.")]
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
        public static bool IsAllMeshesValid(IEnumerable<BlendShareObject> blendShares, IEnumerable<Mesh> meshes)
        {
            return BlendShareGenerationService.IsAllMeshesValid(blendShares, meshes);
        }

        [Obsolete("Use BlendShareGenerationService.IsAllMeshesValid(IEnumerable<BlendShareObject>, IEnumerable<Mesh>) instead.")]
        public static bool IsAllMeshesValid(IEnumerable<BlendShapeDataSO> blendShapes, IEnumerable<Mesh> meshes)
        {
            var upgraded = blendShapes?
                .Where(blendShape => blendShape != null)
                .Select(BlendShareUpgradeService.ConvertLegacy)
                .Where(blendShare => blendShare != null)
                .ToArray() ?? Array.Empty<BlendShareObject>();
            return BlendShareGenerationService.IsAllMeshesValid(upgraded, meshes);
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
        public static bool CreateFbx(GameObject source, IEnumerable<BlendShareObject> blendShares, string outputPath = null, bool onlyNecessary = false)
        {
            return BlendShareGenerationService.CreateFbx(source, blendShares, outputPath, onlyNecessary);
        }

        [Obsolete("Use BlendShareGenerationService.CreateFbx(GameObject, IEnumerable<BlendShareObject>, string, bool) instead.")]
        public static bool CreateFbx(GameObject source, IEnumerable<BlendShapeDataSO> blendShapes, string outputPath = null, bool onlyNecessary = false)
        {
            var upgraded = blendShapes?
                .Where(blendShape => blendShape != null)
                .Select(BlendShareUpgradeService.UpgradeSideBySide)
                .Where(blendShare => blendShare != null)
                .ToArray() ?? Array.Empty<BlendShareObject>();
            return BlendShareGenerationService.CreateFbx(source, upgraded, outputPath, onlyNecessary);
        }
        
        public static bool CreateFbx(this BlendShareObject so, GameObject source, string outputPath = null)
        {
            return CreateFbx(source, new[] { so }, outputPath, false);
        }

        [Obsolete("Use BlendShareObject CreateFbx overload or BlendShareGenerationService instead.")]
        public static bool CreateFbx(this BlendShapeDataSO so, GameObject source, string outputPath = null)
        {
            return CreateFbx(source, new[] { so }, outputPath, false);
        }
        
        public static bool RemoveBlendShapes(this BlendShareObject so, GameObject target, bool removeInAllDeformer = true)
        {
            return BlendShareGenerationService.RemoveBlendShapes(so, target, removeInAllDeformer);
        }

        [Obsolete("Use BlendShareGenerationService.RemoveBlendShapes(BlendShareObject, GameObject, bool) instead.")]
        public static bool RemoveBlendShapes(this BlendShapeDataSO so, GameObject target, bool removeInAllDeformer = true)
        {
            var upgraded = BlendShareUpgradeService.UpgradeSideBySide(so);
            return BlendShareGenerationService.RemoveBlendShapes(upgraded, target, removeInAllDeformer);
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
