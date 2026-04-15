using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Triturbo.BlendShapeShare.FbxReader;
using Triturbo.BlendShapeShare.Util;
using Object = UnityEngine.Object;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class BlendShareGenerationService
    {
        public static GeneratedMeshAssetSO CreateMeshAsset(Object targetMeshContainer, IEnumerable<BlendShareObject> blendShares, string path)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToList() ?? new List<BlendShareObject>();
            if (targetMeshContainer == null || shares.Count == 0)
            {
                return null;
            }

            Dictionary<string, Mesh> targetMeshes = MeshUtil.GetMeshes(targetMeshContainer);
            if (targetMeshes == null)
            {
                return null;
            }

            var generatedMeshes = new Dictionary<string, Mesh>();
            var appliedBlendShares = new List<BlendShareObject>();
            GameObject targetFbx = targetMeshContainer as GameObject;

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                targetFbx = generatedAsset.m_OriginalFbxAsset;
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShares.Where(share => share != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(share => share != null));
                }
            }

            appliedBlendShares.AddRange(shares);
            appliedBlendShares = appliedBlendShares.Where(share => share != null).Distinct().ToList();

            if (targetFbx != null && !IsAllMeshesValid(shares, targetMeshes.Values))
            {
#if ENABLE_FBX_SDK
                string folder = System.IO.Path.GetDirectoryName(path) ?? Application.dataPath;
                string tempAssetPath = System.IO.Path.Combine(folder, $"{targetMeshContainer.name}-{System.Guid.NewGuid()}.fbx");
                if (!CreateFbx(targetFbx, appliedBlendShares, tempAssetPath, true))
                {
                    Debug.LogError("Failed to create blendshapes fbx.");
                    return null;
                }

                var result = GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(targetFbx, appliedBlendShares, tempAssetPath, path);
                AssetDatabase.MoveAssetToTrash(tempAssetPath);
                return result;
#else
                return null;
#endif
            }

            foreach (var share in shares)
            {
                foreach (var meshData in share.Meshes)
                {
                    if (meshData == null)
                    {
                        continue;
                    }

                    if (!generatedMeshes.TryGetValue(meshData.m_MeshName, out Mesh targetMesh) &&
                        !targetMeshes.TryGetValue(meshData.m_MeshName, out targetMesh))
                    {
                        Debug.LogError($"[BlendShare] Target mesh '{meshData.m_MeshName}' was not found in '{targetMeshContainer.name}'.");
                        continue;
                    }

                    var mesh = CreateBlendShapesMesh(meshData, targetMesh);
                    if (mesh == null)
                    {
                        Debug.LogError($"Failed to create blendshapes mesh for {meshData.m_MeshName} in {share.name}");
                        continue;
                    }

                    mesh.name = meshData.m_MeshName;
                    generatedMeshes[meshData.m_MeshName] = mesh;
                }
            }

            return generatedMeshes.Count == 0
                ? null
                : GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(targetFbx, appliedBlendShares, generatedMeshes.Values, path);
        }

        public static bool IsAllMeshesValid(IEnumerable<BlendShareObject> blendShares, IEnumerable<Mesh> meshes)
        {
            var meshList = meshes?.Where(mesh => mesh != null).ToList() ?? new List<Mesh>();
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                foreach (var meshData in share.Meshes)
                {
                    var targetMesh = meshList.FirstOrDefault(mesh => mesh.name == meshData.m_MeshName);
                    if (!meshData.IsValidTarget(targetMesh))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static Mesh CreateBlendShapesMesh(MeshDataObject meshData, Mesh target)
        {
            if (!meshData.IsValidTarget(target))
            {
                return null;
            }

            var blendShapeFeature = meshData.GetFeature<BlendShapeFeatureObject>();
            if (blendShapeFeature == null)
            {
                return null;
            }

            Mesh newMesh = Object.Instantiate(target);
            var activeBlendShapes = blendShapeFeature.GetActiveBlendShapes().ToArray();
            var activeNames = new HashSet<string>(activeBlendShapes.Select(blendShape => blendShape.m_Name));
            var blendShapesToApply = new List<(string name, UnityBlendShapeData data)>();

            for (int i = 0; i < newMesh.blendShapeCount; i++)
            {
                string name = newMesh.GetBlendShapeName(i);
                if (!activeNames.Contains(name))
                {
                    blendShapesToApply.Add((name, new UnityBlendShapeData(newMesh, i)));
                }
            }

            foreach (var blendShape in activeBlendShapes)
            {
                var unityData = CreateUnityBlendShapeData(meshData, blendShape, target);
                if (unityData == null)
                {
                    Debug.LogError($"[BlendShare] Cannot generate Unity blendshape '{blendShape.m_Name}' for mesh '{meshData.m_MeshName}'.");
                    continue;
                }

                blendShapesToApply.Add((blendShape.m_Name, unityData));
            }

            if (blendShapesToApply.Count == 0)
            {
                return null;
            }

            newMesh.ClearBlendShapes();
            foreach (var blendShape in blendShapesToApply)
            {
                foreach (var frame in blendShape.data.m_Frames ?? System.Array.Empty<UnityBlendShapeFrame>())
                {
                    frame?.AddBlendShapeFrame(ref newMesh, blendShape.name);
                }
            }

            return newMesh;
        }

        private static UnityBlendShapeData CreateUnityBlendShapeData(MeshDataObject meshData, BlendShapeRecord blendShape, Mesh targetMesh)
        {
            var mapping = (meshData.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.IsValidFor(targetMesh));

            if (mapping != null)
            {
                return CreateUnityBlendShapeDataFromMapping(mapping, blendShape, targetMesh.vertexCount);
            }

            var cacheMapping = (meshData.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null &&
                                             candidate.MatchesUnityMesh(targetMesh) &&
                                             candidate.GetCachedBlendShape(blendShape.m_Name) != null);
            return cacheMapping?.GetCachedBlendShape(blendShape.m_Name);
        }

        private static UnityBlendShapeData CreateUnityBlendShapeDataFromMapping(
            UnityVertexMappingObject mapping,
            BlendShapeRecord blendShape,
            int unityVertexCount)
        {
            var frames = blendShape.m_FbxBlendShapeData?.m_Frames;
            if (frames == null || frames.Length == 0)
            {
                return null;
            }

            var unityData = new UnityBlendShapeData(frames.Length);
            for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var deltaVertices = new Vector3[unityVertexCount];
                var deltaNormals = new Vector3[unityVertexCount];
                var deltaTangents = new Vector3[unityVertexCount];

                for (int unityIndex = 0; unityIndex < unityVertexCount; unityIndex++)
                {
                    if (!mapping.TryGetFbxGroup(unityIndex, out FbxIndexGroup group))
                    {
                        return null;
                    }

                    var delta = GetDeltaFromGroup(frames[frameIndex], group);
                    deltaVertices[unityIndex] = new Vector3((float)delta.x, (float)delta.y, (float)delta.z) *
                                                mapping.FbxToUnityScale;
                }

                float weight = 100f * (frameIndex + 1) / frames.Length;
                unityData.AddFrameAt(frameIndex, new UnityBlendShapeFrame(weight, unityVertexCount, deltaVertices, deltaNormals, deltaTangents));
            }

            return unityData;
        }

        private static Vector3d GetDeltaFromGroup(FbxBlendShapeFrame frame, FbxIndexGroup group)
        {
            if (group.m_Indices == null) return Vector3d.zero;
            // All welded members share the same delta; try each until a non-zero one is found.
            // (Sparse FBX storage may omit zero-delta entries for some group members.)
            for (int i = 0; i < group.m_Indices.Length; i++)
            {
                var delta = frame.GetDeltaControlPointAt(group.m_Indices[i]);
                if (!delta.IsZero()) return delta;
            }
            return Vector3d.zero;
        }

#if ENABLE_FBX_SDK
        public static bool CreateFbx(GameObject source, IEnumerable<BlendShareObject> blendShares, string outputPath = null, bool onlyNecessary = false)
        {
            var shares = blendShares?.Where(share => share != null).ToArray() ?? System.Array.Empty<BlendShareObject>();
            if (source == null || shares.Length == 0)
            {
                return false;
            }

            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, source.name);
            var fbxImporter = FbxImporter.Create(fbxManager, "");
            int fileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(source), fileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }

            fbxImporter.Import(scene);
            fbxImporter.Destroy();

            var sourceRootNode = scene.GetRootNode();
            var modifiedNodes = new HashSet<FbxNode>();
            foreach (var share in shares)
            {
                foreach (var meshData in share.Meshes)
                {
                    FbxNode node = sourceRootNode.FindMeshChild(meshData.m_MeshName);
                    if (AddBlendShapes(share, meshData, node))
                    {
                        modifiedNodes.Add(node);
                    }
                }
            }

            if (onlyNecessary)
            {
                DeleteFbxNodesWithMesh(sourceRootNode, modifiedNodes, false);
            }

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

            if (!exporter.Initialize(outputPath, fileFormat, fbxManager.GetIOSettings()))
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }

            exporter.Export(scene);
            exporter.Destroy();
            AssetDatabase.Refresh();
            return true;
        }

        public static bool RemoveBlendShapes(BlendShareObject share, GameObject target, bool removeInAllDeformer = true)
        {
            if (share == null || target == null)
            {
                return false;
            }

            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, target.name);
            var fbxImporter = FbxImporter.Create(fbxManager, "");
            int fileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(target), fileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }

            fbxImporter.Import(scene);
            fbxImporter.Destroy();
            var sourceRootNode = scene.GetRootNode();

            foreach (var meshData in share.Meshes)
            {
                FbxNode node = sourceRootNode.FindMeshChild(meshData.m_MeshName);
                FbxMesh sourceMesh = node?.GetMesh();
                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                share.GetDeformer(sourceMesh, false)?.Destroy();
                if (!removeInAllDeformer)
                {
                    continue;
                }

                var blendShapeFeature = meshData.GetFeature<BlendShapeFeatureObject>();
                var activeNames = new HashSet<string>(blendShapeFeature != null
                    ? blendShapeFeature.GetActiveBlendShapes().Select(record => record.m_Name)
                    : Enumerable.Empty<string>());
                for (int i = 0; i < sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
                {
                    var deformer = sourceMesh.GetBlendShapeDeformer(i);
                    for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                    {
                        var name = deformer.GetBlendShapeChannel(j).GetName();
                        if (activeNames.Contains(name))
                        {
                            deformer.RemoveBlendShapeChannel(deformer.GetBlendShapeChannel(j));
                        }
                    }
                }
            }

            var exporter = FbxExporter.Create(fbxManager, "");
            if (!exporter.Initialize(AssetDatabase.GetAssetPath(target), fileFormat, fbxManager.GetIOSettings()))
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }

            exporter.Export(scene);
            exporter.Destroy();
            AssetDatabase.Refresh();
            return true;
        }

        private static FbxBlendShape GetDeformer(this BlendShareObject share, FbxMesh targetMesh, bool create = true)
        {
            if (!string.IsNullOrEmpty(share.m_DeformerID))
            {
                for (int i = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) - 1; i >= 0; i--)
                {
                    var deformer = targetMesh.GetBlendShapeDeformer(i);
                    if (deformer.GetName() == share.m_DeformerID)
                    {
                        return deformer;
                    }
                }
            }

            return create ? FbxBlendShape.Create(targetMesh, share.m_DeformerID) : null;
        }

        private static bool AddBlendShapes(BlendShareObject share, MeshDataObject meshData, FbxNode node)
        {
            FbxMesh targetMesh = node?.GetMesh();
            if (targetMesh == null)
            {
                Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                return false;
            }

            share.GetDeformer(targetMesh, false)?.Destroy();
            var blendShapeFeature = meshData.GetFeature<BlendShapeFeatureObject>();
            if (blendShapeFeature == null)
            {
                return false;
            }

            var existingBlendShapes = new HashSet<string>();
            var activeNames = new HashSet<string>(blendShapeFeature.GetActiveBlendShapes().Select(record => record.m_Name));

            for (int i = 0; i < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
            {
                var deformer = targetMesh.GetBlendShapeDeformer(i);
                for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                {
                    var name = deformer.GetBlendShapeChannel(j).GetName();
                    if (!activeNames.Contains(name))
                    {
                        continue;
                    }

                    var channel = deformer.GetBlendShapeChannel(j);
                    for (int shape = channel.GetTargetShapeCount() - 1; shape >= 0; shape--)
                    {
                        channel.RemoveTargetShape(channel.GetTargetShape(shape));
                    }

                    CreateFbxBlendShapeChannel(channel, targetMesh, blendShapeFeature.GetBlendShape(name).m_FbxBlendShapeData);
                    existingBlendShapes.Add(name);
                }
            }

            var targetDeformer = share.GetDeformer(targetMesh);
            foreach (var blendShape in blendShapeFeature.GetActiveBlendShapes())
            {
                if (existingBlendShapes.Contains(blendShape.m_Name))
                {
                    continue;
                }

                targetDeformer.AddBlendShapeChannel(CreateFbxBlendShapeChannel(
                    blendShape.m_Name,
                    targetMesh,
                    blendShape.m_FbxBlendShapeData));
            }

            return true;
        }

        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(string name, FbxMesh mesh, FbxBlendShapeData fbxBlendShapeData)
        {
            return CreateFbxBlendShapeChannel(FbxBlendShapeChannel.Create(mesh, name), mesh, fbxBlendShapeData);
        }

        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(FbxBlendShapeChannel fbxBlendShapeChannel, FbxMesh mesh, FbxBlendShapeData fbxBlendShapeData)
        {
            int controlPointCount = mesh.GetControlPointsCount();
            int shapeCount = fbxBlendShapeData?.m_Frames?.Length ?? 0;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape newShape = FbxShape.Create(mesh, fbxBlendShapeChannel.GetName());
                newShape.InitControlPoints(controlPointCount);
                FbxBlendShapeFrame frame = fbxBlendShapeData.m_Frames[shapeIndex];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var delta = frame.GetDeltaControlPointAt(pointIndex);
                    var controlPoint = mesh.GetControlPointAt(pointIndex) + new FbxVector4(delta.x, delta.y, delta.z, 0.0);
                    newShape.SetControlPointAt(controlPoint, pointIndex);
                }

                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }

            return fbxBlendShapeChannel;
        }

        private static void DeleteFbxNodesWithMesh(FbxNode entry, IEnumerable<FbxNode> exceptions, bool recursive)
        {
            var exceptionSet = new HashSet<FbxNode>(exceptions.Where(node => node != null));
            for (int i = entry.GetChildCount() - 1; i >= 0; i--)
            {
                var node = entry.GetChild(i);
                if (!exceptionSet.Contains(node) && node.GetMesh() != null)
                {
                    node.Destroy();
                }
                else if (recursive)
                {
                    DeleteFbxNodesWithMesh(node, exceptionSet, true);
                }
            }
        }
#else
        public static bool CreateFbx(GameObject source, IEnumerable<BlendShareObject> blendShares, string outputPath = null, bool onlyNecessary = false)
        {
            return false;
        }

        public static bool RemoveBlendShapes(BlendShareObject share, GameObject target, bool removeInAllDeformer = true)
        {
            return false;
        }
#endif
    }
}
