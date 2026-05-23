using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using UfbxScene = Triturbo.BlendShare.Fbx.Ufbx.UfbxScene;
#endif

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Feature generator execution for Unity mesh assets and FBX files.
    /// </summary>
    public sealed class MeshFeatureGenerationPipeline
    {
        // Keep generation passes explicit so feature generation stays predictable and object-based.
        // A pass claims its MeshFeatureObject through the context instead of being selected by feature id.
        private static readonly IReadOnlyList<IMeshFeatureGenerator> FeatureGenerators =
            BlendShareFeatureModules.All
                .Select(module => module?.Generator)
                .Where(generator => generator != null)
            .OrderBy(generator => generator.Order)
            .ThenBy(generator => generator.GetType().FullName, System.StringComparer.Ordinal)
            .ToArray();

        /// <summary>
        /// Generates Unity mesh copies by applying all supported features in the supplied BlendShare assets.
        /// </summary>
        /// <param name="targetMeshContainer">Unity object containing the target mesh assets.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="generatedMeshes">Generated mesh copies when generation succeeds.</param>
        /// <returns><c>true</c> when at least one mesh was generated.</returns>
        public bool TryGenerateUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            out List<Mesh> generatedMeshes)
        {
            return TryGenerateUnityMeshes(
                targetMeshContainer,
                blendShares,
                out generatedMeshes,
                out _);
        }

        /// <summary>
        /// Generates Unity mesh copies and returns generator-created output subassets.
        /// </summary>
        /// <param name="targetMeshContainer">Unity object containing the target mesh assets.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="generatedMeshes">Generated mesh copies when generation succeeds.</param>
        /// <param name="generatedObjects">Additional subassets created by generator passes.</param>
        /// <returns><c>true</c> when at least one mesh was generated.</returns>
        public bool TryGenerateUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            out List<Mesh> generatedMeshes,
            out List<Object> generatedObjects)
        {
            return TryGenerateUnityMeshes(
                targetMeshContainer,
                blendShares,
                out generatedMeshes,
                out generatedObjects,
                out _);
        }

        public bool TryGenerateUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            out List<Mesh> generatedMeshes,
            out List<Object> generatedObjects,
            out IReadOnlyDictionary<string, MeshFeatureSkinBindingOutput> skinBindingsByMeshKey,
            System.Func<BlendShareObject, MeshDataObject, bool> shouldGenerateMesh = null)
        {
            return TryGenerateUnityMeshes(
                new BlendShareObjectGenerationSource(targetMeshContainer, blendShares, shouldGenerateMesh),
                out generatedMeshes,
                out generatedObjects,
                out skinBindingsByMeshKey);
        }

        public bool TryGenerateUnityMeshes(
            IBlendShareGenerationSource source,
            out List<Mesh> generatedMeshes,
            out List<Object> generatedObjects,
            out IReadOnlyDictionary<string, MeshFeatureSkinBindingOutput> skinBindingsByMeshKey)
        {
            generatedMeshes = new List<Mesh>();
            generatedObjects = new List<Object>();
            skinBindingsByMeshKey = new Dictionary<string, MeshFeatureSkinBindingOutput>();

            if (source == null)
            {
                return false;
            }

            foreach (string diagnostic in source.Diagnostics ?? System.Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                {
                    Debug.LogError($"[BlendShare] {diagnostic}");
                }
            }

            var requests = source.Requests?.Where(request => request?.MeshData != null && request.Share != null)
                .OrderBy(request => request.Order)
                .ToArray() ?? System.Array.Empty<BlendShareGenerationRequest>();
            var targetLookup = MeshFeatureTargetMeshLookup.Create(source.TargetMeshContainer);
            if (source.TargetMeshContainer == null || requests.Length == 0 || targetLookup == null)
            {
                return false;
            }

            var session = new MeshFeatureGenerationSession(source, targetLookup);
            var generatedByMeshKey = new Dictionary<string, Mesh>();

            foreach (var request in requests)
            {
                var share = request.Share;
                var meshData = request.MeshData;
                string meshKey = MeshFeatureGenerationSession.BuildMeshKey(meshData);
                bool createdForThisMeshData = false;
                // Generated meshes are reused by mesh key so later BlendShare assets stack on prior feature output.
                if (!generatedByMeshKey.TryGetValue(meshKey, out var workingMesh))
                {
                    if (!targetLookup.TryGetMesh(meshData, out var targetMesh))
                    {
                        Debug.LogError($"[BlendShare] Target mesh '{FormatMesh(meshData)}' was not found in '{session.FormatTargetName()}': {targetLookup.GetResolutionError(meshData)}");
                        continue;
                    }

                    workingMesh = Object.Instantiate(targetMesh);
                    workingMesh.name = MeshNodePath.Normalize(meshData.m_Path);
                    generatedByMeshKey.Add(meshKey, workingMesh);
                    createdForThisMeshData = true;
                }

                bool failed = false;
                bool generatedFeature = false;
                targetLookup.TryGetRenderer(meshData, out var targetRenderer);
                targetRenderer = request.TargetRenderer != null ? request.TargetRenderer : targetRenderer;
                var context = new MeshFeatureUnityGenerationContext(
                    session,
                    share,
                    meshData,
                    workingMesh,
                    workingMesh,
                    targetRenderer,
                    source.TargetRoot ?? targetLookup.RootTransform,
                    request);

                foreach (var generator in FeatureGenerators)
                {
                    var canApply = generator.CanApplyToUnityMesh(context);
                    if (canApply.Failed)
                    {
                        LogFeatureFailure("validate", generator, meshData, canApply);
                        failed = true;
                        break;
                    }

                    if (canApply.Status == MeshFeatureGenerationStatus.Skipped)
                    {
                        continue;
                    }

                    var result = generator.ApplyToUnityMesh(context);
                    if (result.Failed)
                    {
                        LogFeatureFailure("apply", generator, meshData, result);
                        failed = true;
                        break;
                    }

                    if (result.Status == MeshFeatureGenerationStatus.Skipped)
                    {
                        continue;
                    }

                    generatedFeature = true;
                    workingMesh = context.WorkingMesh;
                    generatedByMeshKey[meshKey] = workingMesh;
                }

                if (!failed && context.HasUnhandledFeatures)
                {
                    LogUnhandledFeatures("apply", meshData, context.GetUnhandledFeatures());
                    failed = true;
                }

                if ((failed || !generatedFeature) && createdForThisMeshData)
                {
                    generatedByMeshKey.Remove(meshKey);
                }
            }

            generatedMeshes.AddRange(generatedByMeshKey.Values.Where(mesh => mesh != null));
            generatedObjects.AddRange(session.GeneratedObjects.Where(obj => obj != null));
            skinBindingsByMeshKey = session.SkinBindingsByMeshKey;
            return generatedMeshes.Count > 0;
        }

        /// <summary>
        /// Checks whether the supplied BlendShare assets can be applied directly to target Unity meshes.
        /// </summary>
        /// <param name="targetMeshContainer">Unity object containing the target mesh assets.</param>
        /// <param name="blendShares">BlendShare assets to validate.</param>
        /// <returns><c>true</c> when every stored feature can apply to the Unity mesh route.</returns>
        public bool CanApplyToUnityMeshes(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares)
        {
            return CanApplyToUnityMeshes(
                MeshFeatureTargetMeshLookup.Create(targetMeshContainer),
                targetMeshContainer,
                blendShares);
        }

        /// <summary>
        /// Checks whether the supplied BlendShare assets can be applied to a collection of target meshes.
        /// </summary>
        /// <param name="blendShares">BlendShare assets to validate.</param>
        /// <param name="meshes">Target meshes to validate against.</param>
        /// <returns><c>true</c> when every stored feature can apply to the Unity mesh route.</returns>
        public bool CanApplyToUnityMeshes(
            IEnumerable<BlendShareObject> blendShares,
            IEnumerable<Mesh> meshes)
        {
            return CanApplyToUnityMeshes(
                MeshFeatureTargetMeshLookup.Create(meshes),
                null,
                blendShares);
        }

        private static bool CanApplyToUnityMeshes(
            MeshFeatureTargetMeshLookup targetLookup,
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToArray() ??
                         System.Array.Empty<BlendShareObject>();
            if (targetLookup == null)
            {
                return false;
            }

            var session = new MeshFeatureGenerationSession(targetMeshContainer, shares, targetLookup);
            foreach (var share in shares)
            {
                foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
                {
                    if (meshData == null)
                    {
                        continue;
                    }

                    if (!targetLookup.TryGetMesh(meshData, out var targetMesh))
                    {
                        return false;
                    }

                    bool canGenerateFeature = false;
                    targetLookup.TryGetRenderer(meshData, out var targetRenderer);
                    var context = new MeshFeatureUnityGenerationContext(
                        session,
                        share,
                        meshData,
                        targetMesh,
                        targetMesh,
                        targetRenderer,
                        targetLookup.RootTransform);

                    foreach (var generator in FeatureGenerators)
                    {
                        var result = generator.CanApplyToUnityMesh(context);
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

#if ENABLE_FBX_SDK
        /// <summary>
        /// Checks whether all stored features are claimed by FBX-capable generation passes.
        /// </summary>
        /// <param name="blendShares">BlendShare assets to validate.</param>
        /// <returns><c>true</c> when all stored features can be handled by the FBX route.</returns>
        public bool CanApplyToFbx(IEnumerable<BlendShareObject> blendShares)
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
                    var context = new MeshFeatureFbxGenerationContext(share, meshData, null);

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

        /// <summary>
        /// Creates an FBX file by applying all supported features in the supplied BlendShare assets.
        /// </summary>
        /// <param name="source">Source FBX asset to import and modify.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="outputPath">Destination FBX asset path, or the source asset path when omitted.</param>
        /// <param name="onlyNecessary">Whether to remove unmodified mesh nodes before export.</param>
        /// <returns><c>true</c> when the FBX file was exported successfully.</returns>
        public bool CreateFbx(
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
                var generationSession = new MeshFeatureFbxGenerationSession(
                    sourceRootNode,
                    GetBlendShareMappingScale(shares),
                    readerScene);
                var modifiedNodes = new HashSet<FbxNode>();
                // modifiedNodes drives onlyNecessary export pruning and must include every node touched by a generator.
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
                        var context = new MeshFeatureFbxGenerationContext(
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
                            failed = true;
                        }

                        if (failed)
                        {
                            continue;
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

        /// <summary>
        /// Removes supported generated features from an FBX asset.
        /// </summary>
        /// <param name="share">BlendShare asset describing features to remove.</param>
        /// <param name="target">Target FBX asset to modify.</param>
        /// <param name="removeInAllDeformer">Whether removal should scan all blendshape deformers.</param>
        /// <returns><c>true</c> when the modified FBX file was exported successfully.</returns>
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
                var context = new MeshFeatureFbxGenerationContext(
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
#endif

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
            if (meshData == null)
            {
                return "<null>";
            }

            return MeshNodePath.Normalize(meshData.m_Path);
        }

#if ENABLE_FBX_SDK
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
#endif
    }
}
