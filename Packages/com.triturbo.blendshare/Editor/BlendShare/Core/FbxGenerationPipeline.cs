using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            foreach (var share in BlendSharePatchIdUtility.DeduplicateByPatchId(blendShares))
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
            string sourcePath,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false,
            bool deduplicatePatchIds = true,
            IBlendShareProgress progress = null)
        {
            progress = BlendShareProgressUtility.Resolve(progress);
            var shares = deduplicatePatchIds
                ? BlendSharePatchIdUtility.DeduplicateByPatchId(blendShares).ToArray()
                : (blendShares ?? Enumerable.Empty<BlendShareObject>()).Where(share => share != null).ToArray();
            if (string.IsNullOrWhiteSpace(sourcePath) || shares.Length == 0)
            {
                return false;
            }

            const string title = null;
            BlendShareProgressUtility.Report(progress, title, "Initializing FBX SDK...", 0.02f, false);

            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, Path.GetFileNameWithoutExtension(sourcePath));
            var fbxImporter = FbxImporter.Create(fbxManager, "");
            int fileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
            bool requiresReaderScene = RequiresFbxReaderScene(shares);
            UfbxScene readerScene = null;
            if (requiresReaderScene)
            {
                BlendShareProgressUtility.Report(progress, title, "Reading source FBX data...", 0.06f, true);
                var readerResult = UfbxScene.TryLoad(sourcePath);
                if (readerResult.Success)
                {
                    readerScene = readerResult.Value;
                }
                else
                {
                    LogWarning($"[BlendShare] Could not read original FBX data: {readerResult.Message}");
                }
            }

            try
            {
                BlendShareProgressUtility.Report(progress, title, "Loading FBX scene...", 0.12f, true);
                if (!fbxImporter.Initialize(sourcePath, fileFormat, fbxManager.GetIOSettings()))
                {
                    return false;
                }

                fbxImporter.Import(scene);
                fbxImporter.Destroy();
                BlendShareProgressUtility.Report(progress, title, "Applying BlendShare data...", 0.2f, true);

                var sourceRootNode = scene.GetRootNode();
                var generationSession = new FbxGenerationSession(
                    sourceRootNode,
                    GetBlendShareMappingScale(shares),
                    readerScene,
                    progress);
                var modifiedNodes = new HashSet<FbxNode>();
                int meshStep = 0;
                int meshStepCount = CountMeshes(shares);
                foreach (var share in shares)
                {
                    foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
                    {
                        if (meshData == null)
                        {
                            continue;
                        }

                        meshStep++;
                        float meshProgress = GetGenerationProgress(meshStep, meshStepCount, 0f);
                        BlendShareProgressUtility.Report(progress, title, $"Applying {FormatMesh(meshData)}...", meshProgress, true);

                        FbxNode node = FindFbxMeshNode(sourceRootNode, meshData);
                        if (node?.GetMesh() == null)
                        {
                            LogError($"Can not find mesh: {FormatMesh(meshData)} in FBX file");
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
                            BlendShareProgressUtility.Report(progress, title, $"Applying {generator.GetType().Name} to {FormatMesh(meshData)}...", meshProgress, true);
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
                    BlendShareProgressUtility.Report(progress, title, "Preparing generated FBX contents...", 0.8f, true);
                    DeleteFbxNodesWithMesh(sourceRootNode, modifiedNodes, false);
                }

                var exporter = FbxExporter.Create(fbxManager, "");
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = sourcePath;
                }

                BlendShareProgressUtility.Report(progress, title, "Writing FBX file...", 0.86f, false);
                if (!exporter.Initialize(outputPath, fileFormat, fbxManager.GetIOSettings()))
                {
                    LogError("Exporter Initialize failed.");
                    return false;
                }

                exporter.Export(scene);
                exporter.Destroy();
                return true;
            }
            finally
            {
                readerScene?.Dispose();
            }
        }

        private static int CountMeshes(IEnumerable<BlendShareObject> shares)
        {
            int count = 0;
            foreach (var share in shares ?? Enumerable.Empty<BlendShareObject>())
            {
                count += (share?.Meshes ?? System.Array.Empty<MeshDataObject>()).Count(mesh => mesh != null);
            }

            return count;
        }

        private static float GetGenerationProgress(int currentMesh, int meshCount, float featureProgress)
        {
            if (meshCount <= 0)
            {
                return 0.2f;
            }

            float meshBase = Clamp01((currentMesh - 1f) / meshCount);
            float meshSpan = 1f / meshCount;
            return Lerp(0.2f, 0.78f, meshBase + meshSpan * Clamp01(featureProgress));
        }

        private static bool RequiresFbxReaderScene(IEnumerable<BlendShareObject> shares)
        {
            foreach (var share in shares ?? Enumerable.Empty<BlendShareObject>())
            {
                foreach (var meshData in share.Meshes ?? System.Array.Empty<MeshDataObject>())
                {
                    if (meshData == null)
                    {
                        continue;
                    }

                    var context = new FbxGenerationContext(share, meshData, null);
                    if (FeatureGenerators.Any(generator => generator.RequiresFbxReaderScene(context)))
                    {
                        return true;
                    }
                }
            }

            return false;
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
            LogError(
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

            LogError(
                $"[BlendShare] Failed to {action} mesh '{FormatMesh(meshData)}': no generation pass handled feature object(s): {featureNames}.");
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }

        private static float Lerp(float from, float to, float value)
        {
            return from + (to - from) * Clamp01(value);
        }

        private static void LogWarning(string message)
        {
            System.Console.Error.WriteLine(message);
        }

        private static void LogError(string message)
        {
            System.Console.Error.WriteLine(message);
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
