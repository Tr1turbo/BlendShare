using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using UfbxScene = Triturbo.BlendShare.Fbx.Ufbx.UfbxScene;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Feature generator execution for FBX asset mutation.
    /// </summary>
    public sealed class FbxGenerationPipeline
    {
        private static readonly IReadOnlyList<IMeshFeatureGenerator> FeatureGenerators =
            BlendShareFeatureModules.All
                .Select(module => module?.Generator)
                .Where(generator => generator != null)
                .OrderBy(generator => generator.Order)
                .ThenBy(generator => generator.GetType().FullName, System.StringComparer.Ordinal)
                .ToArray();

        public bool CanApply(IEnumerable<BlendShareObject> blendShares)
        {
            foreach (var share in blendShares ?? Enumerable.Empty<BlendShareObject>())
            {
                foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
                {
                    if (meshData == null)
                    {
                        continue;
                    }

                    bool canGenerateFeature = false;
                    var context = new FbxGenerationContext(share, meshData, null);

                    foreach (var generator in FeatureGenerators)
                    {
                        var result = generator.CanApplyToFbx(context);
                        if (result.Failed)
                        {
                            return false;
                        }

                        if (result.Status != MeshFeatureGenerationStatus.Skipped)
                        {
                            canGenerateFeature = true;
                        }
                    }

                    if (!canGenerateFeature || context.HasUnhandledFeatures)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public bool Create(
            GameObject source,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToArray() ??
                         System.Array.Empty<BlendShareObject>();
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
            string sourceAssetPath = AssetDatabase.GetAssetPath(source);
            bool requiresSkinReader = shares
                .SelectMany(share => share.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(meshData => meshData != null)
                .SelectMany(meshData => meshData.Features ?? System.Array.Empty<MeshFeatureObject>())
                .Any(feature => feature != null && feature.FeatureId == "skin-weights");
            UfbxScene readerScene = null;
            if (requiresSkinReader)
            {
                var readerResult = UfbxScene.TryLoad(sourceAssetPath);
                if (readerResult.Success)
                {
                    readerScene = readerResult.Value;
                }
                else
                {
                    Debug.LogWarning($"[BlendShare] Could not read original FBX skin data: {readerResult.Message}");
                }
            }

            try
            {
                if (!fbxImporter.Initialize(sourceAssetPath, fileFormat, fbxManager.GetIOSettings()))
                {
                    return false;
                }

                fbxImporter.Import(scene);
                fbxImporter.Destroy();

                var sourceRootNode = scene.GetRootNode();
                var generationSession = new FbxGenerationSession(
                    sourceRootNode,
                    GetBlendShareMappingScale(shares),
                    readerScene);
                var modifiedNodes = new HashSet<FbxNode>();
                foreach (var share in shares)
                {
                    foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
                    {
                        if (meshData == null)
                        {
                            continue;
                        }

                        FbxNode node = FindFbxMeshNode(sourceRootNode, meshData);
                        if (node?.GetMesh() == null)
                        {
                            Debug.LogError($"Can not find mesh: {FormatMesh(meshData)} in FBX file");
                            continue;
                        }

                        bool failed = false;
                        var context = new FbxGenerationContext(
                            share,
                            meshData,
                            node,
                            session: generationSession);

                        foreach (var generator in FeatureGenerators)
                        {
                            var result = generator.ApplyToFbx(context);
                            if (result.Failed)
                            {
                                LogFeatureFailure("apply to FBX", generator, meshData, result);
                                failed = true;
                                break;
                            }

                            if (result.Status == MeshFeatureGenerationStatus.Skipped)
                            {
                                continue;
                            }

                            if (result.Modified)
                            {
                                modifiedNodes.Add(node);
                            }
                        }

                        if (!failed && context.HasUnhandledFeatures)
                        {
                            LogUnhandledFeatures("apply to FBX", meshData, context.GetUnhandledFeatures());
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
                    outputPath = sourceAssetPath;
                }
                else
                {
                    AssetDatabase.CopyAsset(sourceAssetPath, outputPath);
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
            finally
            {
                readerScene?.Dispose();
            }
        }

        public bool RemoveBlendShapes(BlendShareObject share, GameObject target, bool removeInAllDeformer = true)
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

            foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
            {
                if (meshData == null)
                {
                    continue;
                }

                FbxNode node = FindFbxMeshNode(sourceRootNode, meshData);
                if (node?.GetMesh() == null)
                {
                    Debug.LogError($"Can not find mesh: {FormatMesh(meshData)} in FBX file");
                    continue;
                }

                bool failed = false;
                var context = new FbxGenerationContext(
                    share,
                    meshData,
                    node,
                    removeInAllDeformer);

                foreach (var generator in FeatureGenerators)
                {
                    var result = generator.RemoveFromFbx(context);
                    if (result.Failed)
                    {
                        LogFeatureFailure("remove from FBX", generator, meshData, result);
                        failed = true;
                        break;
                    }
                }

                if (!failed && context.HasUnhandledFeatures)
                {
                    LogUnhandledFeatures("remove from FBX", meshData, context.GetUnhandledFeatures());
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

        private static FbxNode FindFbxMeshNode(FbxNode rootNode, MeshDataObject meshData)
        {
            if (rootNode == null || meshData == null)
            {
                return null;
            }

            var pathNode = FindFbxNodeByPath(rootNode, MeshNodePath.Normalize(meshData.m_Path));
            return pathNode?.GetMesh() != null ? pathNode : null;
        }

        private static FbxNode FindFbxNodeByPath(FbxNode rootNode, string path)
        {
            if (rootNode == null)
            {
                return null;
            }

            string normalizedPath = MeshNodePath.Normalize(path);
            if (normalizedPath == MeshNodePath.Root)
            {
                return rootNode;
            }

            var currentNode = rootNode;
            foreach (string part in normalizedPath.Split('/'))
            {
                bool found = false;
                for (int i = 0; i < currentNode.GetChildCount(); i++)
                {
                    var child = currentNode.GetChild(i);
                    if (child.GetName() != part)
                    {
                        continue;
                    }

                    currentNode = child;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return null;
                }
            }

            return currentNode;
        }

        private static void DeleteFbxNodesWithMesh(FbxNode entry, IEnumerable<FbxNode> exceptions, bool recursive)
        {
            var exceptionSet = new HashSet<FbxNode>((exceptions ?? Enumerable.Empty<FbxNode>()).Where(node => node != null));
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

        private static void LogFeatureFailure(
            string action,
            IMeshFeatureGenerator generator,
            MeshDataObject meshData,
            MeshFeatureGenerationResult result)
        {
            Debug.LogError(
                $"[BlendShare] Failed to {action} generator '{generator.GetType().Name}' for mesh '{FormatMesh(meshData)}': {result.Message}");
        }

        private static void LogUnhandledFeatures(
            string action,
            MeshDataObject meshData,
            IEnumerable<MeshFeatureObject> features)
        {
            string featureNames = string.Join(", ", (features ?? Enumerable.Empty<MeshFeatureObject>())
                .Where(feature => feature != null)
                .Select(feature => feature.GetType().Name)
                .Distinct());

            if (string.IsNullOrEmpty(featureNames))
            {
                return;
            }

            Debug.LogError(
                $"[BlendShare] Failed to {action} mesh '{FormatMesh(meshData)}': no generation pass handled feature object(s): {featureNames}.");
        }

        private static string FormatMesh(MeshDataObject meshData)
        {
            return meshData == null ? "<null>" : MeshNodePath.Normalize(meshData.m_Path);
        }

        private static float GetBlendShareMappingScale(IEnumerable<BlendShareObject> shares)
        {
            var mapping = (shares ?? Enumerable.Empty<BlendShareObject>())
                .Where(share => share != null)
                .SelectMany(share => share.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(meshData => meshData != null)
                .SelectMany(meshData => meshData.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            return mapping != null ? mapping.FbxToUnityScale : 1f;
        }
    }

}
#endif