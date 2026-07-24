using System.Collections.Generic;
using System.IO;
using System.Linq;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using Debug = UnityEngine.Debug;
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

        public bool CanApply(IEnumerable<BlendShareObject> patches)
        {
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            return CanApplyBatched(deduplicatedPatches);
        }

        public bool Create(
            string sourcePath,
            IEnumerable<BlendShareObject> patches,
            string outputPath = null,
            bool onlyNecessary = false,
            IBlendShareProgress progress = null)
        {
            progress = BlendShareProgressUtility.Resolve(progress);
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            if (string.IsNullOrWhiteSpace(sourcePath) || deduplicatedPatches.Length == 0)
            {
                return false;
            }

            const string title = null;
            BlendShareProgressUtility.Report(progress, title, "Initializing FBX SDK...", 0.02f, false);

            FbxManager fbxManager = null;
            FbxImporter fbxImporter = null;
            FbxExporter exporter = null;
            UfbxScene readerScene = null;
            try
            {
                fbxManager = FbxManager.Create();
                var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
                fbxManager.SetIOSettings(ios);
                var scene = FbxScene.Create(fbxManager, Path.GetFileNameWithoutExtension(sourcePath));
                fbxImporter = FbxImporter.Create(fbxManager, "");
                int fileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
                bool requiresReaderScene = RequiresFbxReaderScene(deduplicatedPatches);
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

                BlendShareProgressUtility.Report(progress, title, "Loading FBX scene...", 0.12f, true);
                if (!fbxImporter.Initialize(sourcePath, fileFormat, fbxManager.GetIOSettings()))
                {
                    return false;
                }

                fbxImporter.Import(scene);
                fbxImporter.Destroy();
                fbxImporter = null;
                BlendShareProgressUtility.Report(progress, title, "Applying BlendShare data...", 0.2f, true);

                var sourceRootNode = scene.GetRootNode();
                var generationSession = new FbxGenerationSession(
                    sourceRootNode,
                    GetBlendShareMappingScale(deduplicatedPatches),
                    readerScene,
                    progress);
                var modifiedNodes = new HashSet<FbxNode>();
                if (!ApplyBatched(
                        sourceRootNode,
                        deduplicatedPatches,
                        generationSession,
                        modifiedNodes,
                        progress,
                        title))
                {
                    return false;
                }

                if (onlyNecessary)
                {
                    BlendShareProgressUtility.Report(progress, title, "Preparing generated FBX contents...", 0.8f, true);
                    DeleteFbxNodesWithMesh(sourceRootNode, modifiedNodes, false);
                }

                exporter = FbxExporter.Create(fbxManager, "");
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
                exporter = null;
                return true;
            }
            finally
            {
                exporter?.Destroy();
                fbxImporter?.Destroy();
                readerScene?.Dispose();
                fbxManager?.Destroy();
            }
        }

        private bool CanApplyBatched(IReadOnlyList<BlendShareObject> patches)
        {
            foreach (var definition in BuildBatchDefinitions(patches))
            {
                var context = new FbxMeshFeatureContext(null, definition.MeshKey, null, definition.Handles);
                bool canGenerateFeature = false;
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

                if (!canGenerateFeature || context.GetUnhandledHandles().Count > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ApplyBatched(
            FbxNode sourceRootNode,
            IReadOnlyList<BlendShareObject> patches,
            FbxGenerationSession session,
            ISet<FbxNode> modifiedNodes,
            IBlendShareProgress progress,
            string title)
        {
            var definitions = BuildBatchDefinitions(patches);
            for (int index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                float meshProgress = GetGenerationProgress(index + 1, definitions.Count, 0f);
                BlendShareProgressUtility.Report(progress, title, $"Applying {definition.MeshKey}...", meshProgress, true);
                var node = FindFbxNodeByPath(sourceRootNode, definition.MeshKey);
                if (node?.GetMesh() == null)
                {
                    LogError($"Can not find mesh: {definition.MeshKey} in FBX file");
                    return false;
                }

                var context = new FbxMeshFeatureContext(session, definition.MeshKey, node, definition.Handles);
                foreach (var generator in FeatureGenerators)
                {
                    BlendShareProgressUtility.Report(
                        progress,
                        title,
                        $"Applying {generator.GetType().Name} to {definition.MeshKey}...",
                        meshProgress,
                        true);
                    var canApply = generator.CanApplyToFbx(context);
                    if (canApply.Failed)
                    {
                        LogError(
                            $"[BlendShare] Failed to validate FBX generator '{generator.GetType().Name}' for mesh '{definition.MeshKey}': {canApply.Message}");
                        return false;
                    }

                    if (canApply.Status == MeshFeatureGenerationStatus.Skipped)
                    {
                        continue;
                    }

                    var result = generator.ApplyToFbx(context);
                    if (result.Failed)
                    {
                        LogError(
                            $"[BlendShare] Failed to apply FBX generator '{generator.GetType().Name}' for mesh '{definition.MeshKey}': {result.Message}");
                        return false;
                    }

                    if (result.Modified)
                    {
                        modifiedNodes.Add(node);
                    }
                }

                var unhandled = context.GetUnhandledHandles();
                if (unhandled.Count > 0)
                {
                    LogUnhandledFeatures(
                        "apply to FBX",
                        unhandled[0].MeshData,
                        unhandled.Select(handle => handle.Feature));
                    return false;
                }
            }

            foreach (var generator in FeatureGenerators)
            {
                var result = generator.FinalizeFbxGeneration(session);
                if (result.Failed)
                {
                    LogError($"[BlendShare] Failed to finalize FBX generator '{generator.GetType().Name}': {result.Message}");
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<FbxBatchDefinition> BuildBatchDefinitions(
            IEnumerable<BlendShareObject> patches)
        {
            var ordered = new List<FbxBatchDefinition>();
            var byKey = new Dictionary<string, FbxBatchDefinition>(System.StringComparer.Ordinal);
            foreach (var patch in patches ?? System.Array.Empty<BlendShareObject>())
            {
                foreach (var meshData in patch?.Meshes ?? System.Array.Empty<MeshDataObject>())
                {
                    if (meshData == null)
                    {
                        continue;
                    }

                    string meshKey = MeshNodePath.Normalize(meshData.m_Path);
                    if (!byKey.TryGetValue(meshKey, out var definition))
                    {
                        definition = new FbxBatchDefinition(meshKey);
                        byKey.Add(meshKey, definition);
                        ordered.Add(definition);
                    }

                    definition.AddFeatures(patch, meshData);
                }
            }

            return ordered;
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

        private static bool RequiresFbxReaderScene(IEnumerable<BlendShareObject> patches)
        {
            var patchArray = (patches ?? Enumerable.Empty<BlendShareObject>()).ToArray();
            return BuildBatchDefinitions(patchArray)
                .Any(definition => FeatureGenerators.Any(generator => generator.RequiresFbxReaderScene(
                    new FbxMeshFeatureContext(null, definition.MeshKey, null, definition.Handles))));
        }

        private sealed class FbxBatchDefinition
        {
            internal FbxBatchDefinition(string meshKey)
            {
                MeshKey = MeshNodePath.Normalize(meshKey);
            }

            internal string MeshKey { get; }
            internal List<MeshFeatureHandle> Handles { get; } = new();

            internal void AddFeatures(BlendShareObject patch, MeshDataObject meshData)
            {
                foreach (var feature in meshData?.Features ?? System.Array.Empty<MeshFeatureObject>())
                {
                    if (feature == null)
                    {
                        continue;
                    }

                    Handles.Add(new MeshFeatureHandle(
                        feature,
                        patch,
                        meshData,
                        MeshKey,
                        Handles.Count));
                }
            }
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
            Debug.LogWarning(message);
        }

        private static void LogError(string message)
        {
            Debug.LogError(message);
        }

        private static string FormatMesh(MeshDataObject meshData)
        {
            return meshData == null ? "<null>" : MeshNodePath.Normalize(meshData.m_Path);
        }

        private static float GetBlendShareMappingScale(IEnumerable<BlendShareObject> patches)
        {
            var mapping = (patches ?? Enumerable.Empty<BlendShareObject>())
                .Where(patch => patch != null)
                .SelectMany(patch => patch.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(meshData => meshData != null)
                .SelectMany(meshData => meshData.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.m_IsValid);
            return mapping != null ? mapping.SpaceConversion.ImportScale : 1f;
        }
    }

}
#endif
