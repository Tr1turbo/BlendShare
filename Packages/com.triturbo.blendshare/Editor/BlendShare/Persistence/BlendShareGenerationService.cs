using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Hashing;
using Triturbo.BlendShare.Migration;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Persistence
{
    public static class BlendShareGenerationService
    {
        /// <summary>
        /// Creates a generated mesh asset by applying BlendShare features to a target mesh container.
        /// </summary>
        /// <param name="targetMeshContainer">FBX asset, generated mesh asset, or other asset containing target meshes.</param>
        /// <param name="patches">BlendShare assets to apply.</param>
        /// <param name="path">Asset path where the generated mesh asset should be saved.</param>
        /// <returns>The saved generated mesh asset, or <c>null</c> when generation fails.</returns>
        [System.Obsolete("CreateMeshAsset creates the legacy GeneratedMeshAssetSO format. Use BlendShareArtifactService.CreateArtifact for new generated assets.")]
        public static GeneratedMeshAssetSO CreateMeshAsset(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            string path)
        {
            var deduplicatedPatches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToList();
            if (targetMeshContainer == null || deduplicatedPatches.Count == 0)
            {
                return null;
            }

            var unityPipeline = new UnityMeshGenerationPipeline();
            var appliedPatches = GetAppliedPatches(targetMeshContainer, deduplicatedPatches);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);

            if (targetFbx != null && !unityPipeline.CanApplyToUnityMeshes(targetMeshContainer, deduplicatedPatches))
            {
#if ENABLE_FBX_SDK
                var fbxPipeline = new FbxGenerationPipeline();
                if (fbxPipeline.CanApply(appliedPatches))
                {
                    string folder = System.IO.Path.GetDirectoryName(path) ?? Application.dataPath;
                    string tempAssetPath = System.IO.Path.Combine(folder, $"{targetMeshContainer.name}-{System.Guid.NewGuid()}.fbx");
                    if (!CreateFbx(targetFbx, appliedPatches, tempAssetPath, true, false))
                    {
                        Debug.LogError("Failed to create blendshapes fbx.");
                        return null;
                    }

                    var result = GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(
                        targetFbx,
                        appliedPatches,
                        tempAssetPath,
                        path);
                    AssetDatabase.MoveAssetToTrash(tempAssetPath);
                    return result;
                }
#endif
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var artifact = unityPipeline.CreateArtifact(
                targetMeshContainer,
                deduplicatedPatches,
                null,
                appliedPatches);
            if (artifact == null)
            {
                return null;
            }

            var meshes = (artifact.m_Meshes ?? System.Array.Empty<BlendShareMeshDescriptor>())
                .Select(descriptor => descriptor?.m_Mesh)
                .Where(mesh => mesh != null)
                .ToArray();
            try
            {
                return GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(
                    targetFbx,
                    appliedPatches,
                    meshes,
                    artifact.m_Armature != null ? new Object[] { artifact.m_Armature } : System.Array.Empty<Object>(),
                    path);
            }
            finally
            {
                RemoveGeneratedObjects(meshes);
                RemoveGeneratedObjects(new Object[] { artifact.m_Armature, artifact });
            }
        }

        /// <summary>
        /// Checks whether every mesh feature in the supplied BlendShare assets can apply to the target meshes.
        /// </summary>
        /// <param name="patches">BlendShare assets to validate.</param>
        /// <param name="meshes">Target meshes to validate against.</param>
        /// <returns><c>true</c> when every feature can be applied directly to the Unity meshes.</returns>
        public static bool IsAllMeshesValid(IEnumerable<BlendShareObject> patches, IEnumerable<Mesh> meshes)
        {
            return new UnityMeshGenerationPipeline().CanApplyToUnityMeshes(patches, meshes);
        }

        public static bool ApplyPatch(
            GameObject target,
            BlendShareObject patch,
            bool force,
            IBlendShareProgress progress,
            out string message)
        {
            message = null;
            if (target == null || patch == null)
            {
                message = "Target FBX or BlendShare patch is missing.";
                return false;
            }

            var state = BlendShareFbxMetadataService.GetPatchState(target, patch);
            if (state.HasPatch && !force)
            {
                message = state.HasExactPatch
                    ? "This BlendShare patch is already recorded on the FBX."
                    : "Another BlendShare patch with the same patch id is already recorded on the FBX.";
                return false;
            }

#if ENABLE_FBX_SDK
            try
            {
                progress = BlendShareProgressUtility.Resolve(progress);
                BlendShareProgressUtility.Report(progress, null, "Preparing baseline backup...", 0.03f, true);
                var metadata = state.Metadata;
                if (!BlendShareFbxMetadataService.EnsureBaselineBackup(target, metadata, out message))
                {
                    return false;
                }

                BlendShareProgressUtility.Report(progress, null, "Calculating source hash...", 0.05f, true);
                string targetPath = AssetDatabase.GetAssetPath(target);
                string hashBefore = BlendShareHashUtility.Sha256File(targetPath);
                if (!CreateFbx(target, new[] { patch }, progress: progress))
                {
                    message = "Failed to apply BlendShare patch to FBX.";
                    return false;
                }

                BlendShareProgressUtility.Report(progress, null, "Saving BlendShare metadata...", 0.98f, false);
                string hashAfter = BlendShareHashUtility.Sha256File(targetPath);
                var record = BlendShareFbxMetadataService.CreateRecord(target, patch, hashBefore, hashAfter);
                BlendShareFbxMetadataService.CommitApplyRecord(metadata, patch, record);
                if (!BlendShareFbxMetadataService.Save(target, metadata, out message))
                {
                    return false;
                }

                message = state.HasPatch
                    ? "BlendShare patch applied again. This may accumulate on the current FBX."
                    : "BlendShare patch applied.";
                return true;
            }
            catch (BlendShareOperationCanceledException)
            {
                message = BlendShareProgressUtility.CanceledMessage;
                return false;
            }
#else
            message = "FBX SDK support is not available.";
            return false;
#endif
        }

        public static bool ApplyPatch(
            GameObject target,
            BlendShareObject patch,
            bool force,
            out string message)
        {
            return ApplyPatch(target, patch, force, null, out message);
        }

        public static bool RevertPatch(
            GameObject target,
            BlendShareObject patch,
            IBlendShareProgress progress,
            out string message)
        {
            message = null;
            if (target == null || patch == null)
            {
                message = "Target FBX or BlendShare patch is missing.";
                return false;
            }

            var metadata = BlendShareFbxMetadataService.Load(target);
            int restoreIndex = BlendShareFbxMetadataService.FindLatestPatchAssetIndex(metadata, patch);
            if (restoreIndex < 0)
            {
                message = "This BlendShare patch is not recorded on the FBX.";
                return false;
            }

            if ((metadata.activeRecords?.Length ?? 0) <= 1)
            {
                return RestoreToOriginal(target, progress, out message);
            }

#if ENABLE_FBX_SDK
            return RevertByReplay(target, metadata, restoreIndex, progress, out message);
#else
            message = "FBX SDK support is not available.";
            return false;
#endif
        }

        public static bool RevertPatch(
            GameObject target,
            BlendShareObject patch,
            out string message)
        {
            return RevertPatch(target, patch, null, out message);
        }

        public static bool RestoreToOriginal(GameObject target, IBlendShareProgress progress, out string message)
        {
            message = null;
            if (target == null)
            {
                message = "Target FBX is missing.";
                return false;
            }

            var metadata = BlendShareFbxMetadataService.Load(target);
            string targetPath = AssetDatabase.GetAssetPath(target);
            string tempPath = CreateTemporaryFbxCopy(targetPath);
            try
            {
                BlendShareProgressUtility.Report(progress, null, "Restoring FBX baseline...", 0.25f, false);
                if (!BlendShareFbxMetadataService.RestoreBaseline(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                BlendShareProgressUtility.Report(progress, null, "Clearing BlendShare metadata...", 0.8f, false);
                if (!BlendShareFbxMetadataService.Clear(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                message = "FBX restored to original.";
                return true;
            }
            finally
            {
                DeleteTemporaryFbxCopy(tempPath);
            }
        }

        public static bool RestoreToOriginal(GameObject target, out string message)
        {
            return RestoreToOriginal(target, null, out message);
        }

#if ENABLE_FBX_SDK
        /// <summary>
        /// Creates an FBX file by applying BlendShare features to a source FBX asset.
        /// </summary>
        /// <param name="source">Source FBX asset to import and modify.</param>
        /// <param name="patches">BlendShare assets to apply.</param>
        /// <param name="outputPath">Destination FBX asset path, or the source asset path when omitted.</param>
        /// <param name="onlyNecessary">Whether to remove unmodified mesh nodes before export.</param>
        /// <returns><c>true</c> when the FBX file was exported successfully.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> patches,
            string outputPath = null,
            bool onlyNecessary = false,
            bool initializeMetadata = true,
            bool deduplicatePatchIds = true,
            IBlendShareProgress progress = null)
        {
            var deduplicatedPatches = deduplicatePatchIds
                ? BlendSharePatchIdUtility.DeduplicateByPatchId(patches)
                : (patches ?? Enumerable.Empty<BlendShareObject>()).Where(patch => patch != null).ToArray();
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            string resolvedOutputPath = string.IsNullOrWhiteSpace(outputPath) ? sourcePath : outputPath;
            bool writesNewOutput = !string.Equals(resolvedOutputPath, sourcePath, StringComparison.Ordinal);

            bool result;
            try
            {
                result = new FbxGenerationPipeline().Create(sourcePath, deduplicatedPatches, resolvedOutputPath, onlyNecessary, deduplicatePatchIds, progress);
            }
            catch
            {
                if (writesNewOutput)
                {
                    DeleteGeneratedOutput(resolvedOutputPath);
                }

                throw;
            }

            if (!result && writesNewOutput)
            {
                DeleteGeneratedOutput(resolvedOutputPath);
            }

            if (result)
            {
                BlendShareProgressUtility.Report(progress, null, "Importing FBX in Unity...", 0.96f, false);
                AssetDatabase.ImportAsset(resolvedOutputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }

            if (result &&
                !string.IsNullOrWhiteSpace(resolvedOutputPath) &&
                !string.Equals(resolvedOutputPath, sourcePath, StringComparison.Ordinal))
            {
                if (initializeMetadata)
                {
                    BlendShareProgressUtility.Report(progress, null, "Saving BlendShare metadata...", 0.98f, false);
                    if (!BlendShareFbxMetadataService.InitializeGeneratedOutput(source, resolvedOutputPath, deduplicatedPatches, out string error))
                    {
                        Debug.LogError($"[BlendShare] Failed to initialize generated FBX metadata: {error}");
                        AssetDatabase.DeleteAsset(resolvedOutputPath);
                        return false;
                    }
                }
                else
                {
                    BlendShareProgressUtility.Report(progress, null, "Clearing BlendShare metadata...", 0.98f, false);
                    BlendShareFbxMetadataService.ClearBlendShareMetadataAtPath(resolvedOutputPath);
                }
            }

            return result;
        }

        // Feature-level inverse is intentionally disabled. Revert uses baseline replay instead.
        // public static bool RemoveBlendShapes(
        //     BlendShareObject patch,
        //     GameObject target,
        //     bool removeInAllDeformer = true)
        // {
        //     return new FbxGenerationPipeline().RemoveBlendShapes(patch, target, removeInAllDeformer);
        // }
#else
        /// <summary>
        /// Stub used when the Autodesk FBX SDK package is not available.
        /// </summary>
        /// <param name="source">Unused source FBX asset.</param>
        /// <param name="patches">Unused BlendShare assets.</param>
        /// <param name="outputPath">Unused output path.</param>
        /// <param name="onlyNecessary">Unused pruning flag.</param>
        /// <returns>Always <c>false</c> without FBX SDK support.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> patches,
            string outputPath = null,
            bool onlyNecessary = false,
            bool initializeMetadata = true,
            bool deduplicatePatchIds = true,
            IBlendShareProgress progress = null)
        {
            return false;
        }

        // Feature-level inverse is intentionally disabled. Revert uses baseline replay instead.
        // public static bool RemoveBlendShapes(
        //     BlendShareObject patch,
        //     GameObject target,
        //     bool removeInAllDeformer = true)
        // {
        //     return false;
        // }
#endif

#if ENABLE_FBX_SDK
        private static bool RevertByReplay(
            GameObject target,
            BlendShareFbxMetadata metadata,
            int restoreIndex,
            IBlendShareProgress progress,
            out string message)
        {
            message = null;
            var records = metadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>();
            if (restoreIndex < 0 || restoreIndex >= records.Length)
            {
                message = "BlendShare patch history record is missing.";
                return false;
            }

            var replayRecords = records
                .Where((_, index) => index != restoreIndex)
                .ToArray();
            if (!BlendShareFbxMetadataService.CanResolveRecords(replayRecords, out message))
            {
                return false;
            }

            string targetPath = AssetDatabase.GetAssetPath(target);
            string tempPath = CreateTemporaryFbxCopy(targetPath);
            try
            {
                BlendShareProgressUtility.Report(progress, null, "Restoring FBX baseline...", 0.1f, false);
                if (!BlendShareFbxMetadataService.RestoreBaseline(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                var replayPatches = new List<BlendShareObject>();
                foreach (var record in replayRecords)
                {
                    if (!BlendShareFbxMetadataService.TryResolveRecord(record, out var replayPatch))
                    {
                        message = $"Cannot find BlendShare patch asset '{record?.blendShareName}'.";
                        RestoreTemporaryFbxCopy(tempPath, targetPath);
                        return false;
                    }

                    replayPatches.Add(replayPatch);
                }

                string hashBefore = BlendShareHashUtility.Sha256File(targetPath);
                if (!CreateFbx(target, replayPatches, deduplicatePatchIds: false, progress: progress))
                {
                    message = "Failed to reapply BlendShare patch history.";
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                BlendShareProgressUtility.Report(progress, null, "Saving BlendShare metadata...", 0.98f, false);
                string hashAfter = BlendShareHashUtility.Sha256File(targetPath);
                var replayedRecords = replayRecords
                    .Select(record => BlendShareFbxMetadataService.CopyRecord(record, hashBefore, hashAfter))
                    .ToArray();
                metadata.activeRecords = replayedRecords.ToArray();
                if (!BlendShareFbxMetadataService.Save(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                message = "BlendShare patch reverted.";
                return true;
            }
            catch (BlendShareOperationCanceledException)
            {
                RestoreTemporaryFbxCopy(tempPath, targetPath);
                message = BlendShareProgressUtility.CanceledMessage;
                return false;
            }
            finally
            {
                DeleteTemporaryFbxCopy(tempPath);
            }
        }
#endif

        private static string CreateTemporaryFbxCopy(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            {
                return string.Empty;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"BlendShareRestore-{Guid.NewGuid():N}.fbx");
            File.Copy(targetPath, tempPath, true);
            return tempPath;
        }

        private static void RestoreTemporaryFbxCopy(string tempPath, string targetPath)
        {
            if (string.IsNullOrEmpty(tempPath) || string.IsNullOrEmpty(targetPath) || !File.Exists(tempPath))
            {
                return;
            }

            File.Copy(tempPath, targetPath, true);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static void DeleteTemporaryFbxCopy(string tempPath)
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        private static void DeleteGeneratedOutput(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!AssetDatabase.DeleteAsset(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static List<BlendShareObject> GetAppliedPatches(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches)
        {
            var appliedPatches = new List<BlendShareObject>();

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    appliedPatches.AddRange(generatedAsset.m_AppliedBlendShares.Where(patch => patch != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    appliedPatches.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(patch => patch != null));
                }
            }

            appliedPatches.AddRange(patches ?? Enumerable.Empty<BlendShareObject>());
            return BlendSharePatchIdUtility.DeduplicateByPatchId(appliedPatches).ToList();
        }

        private static GameObject GetTargetFbx(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxAsset;
            }

            return targetMeshContainer as GameObject;
        }

        private static void RemoveGeneratedObjects(IEnumerable<Object> generatedObjects)
        {
            foreach (var generatedObject in generatedObjects ?? Enumerable.Empty<Object>())
            {
                if (generatedObject == null || AssetDatabase.Contains(generatedObject))
                {
                    continue;
                }

                Object.DestroyImmediate(generatedObject);
            }
        }
    }
}
