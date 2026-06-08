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
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="path">Asset path where the generated mesh asset should be saved.</param>
        /// <returns>The saved generated mesh asset, or <c>null</c> when generation fails.</returns>
        [System.Obsolete("CreateMeshAsset creates the legacy GeneratedMeshAssetSO format. Use BlendShareArtifactService.CreateArtifact for new generated assets.")]
        public static GeneratedMeshAssetSO CreateMeshAsset(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            string path)
        {
            var shares = BlendSharePatchIdUtility.DeduplicateByPatchId(blendShares).ToList();
            if (targetMeshContainer == null || shares.Count == 0)
            {
                return null;
            }

            var unityPipeline = new UnityMeshGenerationPipeline();
            var appliedBlendShares = GetAppliedBlendShares(targetMeshContainer, shares);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);

            if (targetFbx != null && !unityPipeline.CanApplyToUnityMeshes(targetMeshContainer, shares))
            {
#if ENABLE_FBX_SDK
                var fbxPipeline = new FbxGenerationPipeline();
                if (fbxPipeline.CanApply(appliedBlendShares))
                {
                    string folder = System.IO.Path.GetDirectoryName(path) ?? Application.dataPath;
                    string tempAssetPath = System.IO.Path.Combine(folder, $"{targetMeshContainer.name}-{System.Guid.NewGuid()}.fbx");
                    if (!CreateFbx(targetFbx, appliedBlendShares, tempAssetPath, true, false))
                    {
                        Debug.LogError("Failed to create blendshapes fbx.");
                        return null;
                    }

                    var result = GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(
                        targetFbx,
                        appliedBlendShares,
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
                shares,
                null,
                appliedBlendShares);
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
                    appliedBlendShares,
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
        /// <param name="blendShares">BlendShare assets to validate.</param>
        /// <param name="meshes">Target meshes to validate against.</param>
        /// <returns><c>true</c> when every feature can be applied directly to the Unity meshes.</returns>
        public static bool IsAllMeshesValid(IEnumerable<BlendShareObject> blendShares, IEnumerable<Mesh> meshes)
        {
            return new UnityMeshGenerationPipeline().CanApplyToUnityMeshes(blendShares, meshes);
        }

        public static bool ApplyPatch(
            GameObject target,
            BlendShareObject share,
            bool force,
            out string message)
        {
            message = null;
            if (target == null || share == null)
            {
                message = "Target FBX or BlendShare patch is missing.";
                return false;
            }

            var state = BlendShareFbxMetadataService.GetPatchState(target, share);
            if (state.HasPatch && !force)
            {
                message = state.HasExactPatch
                    ? "This BlendShare patch is already recorded on the FBX."
                    : "Another BlendShare patch with the same patch id is already recorded on the FBX.";
                return false;
            }

#if ENABLE_FBX_SDK
            var metadata = state.Metadata;
            if (!BlendShareFbxMetadataService.EnsureBaselineBackup(target, metadata, out message))
            {
                return false;
            }

            string targetPath = AssetDatabase.GetAssetPath(target);
            string hashBefore = BlendShareHashUtility.Sha256File(targetPath);
            if (!CreateFbx(target, new[] { share }))
            {
                message = "Failed to apply BlendShare patch to FBX.";
                return false;
            }

            string hashAfter = BlendShareHashUtility.Sha256File(targetPath);
            var record = BlendShareFbxMetadataService.CreateRecord(target, share, hashBefore, hashAfter);
            BlendShareFbxMetadataService.CommitApplyRecord(metadata, share, record);
            if (!BlendShareFbxMetadataService.Save(target, metadata, out message))
            {
                return false;
            }

            message = state.HasPatch
                ? "BlendShare patch applied again. This may accumulate on the current FBX."
                : "BlendShare patch applied.";
            return true;
#else
            message = "FBX SDK support is not available.";
            return false;
#endif
        }

        public static bool RestorePatch(
            GameObject target,
            BlendShareObject share,
            out string message)
        {
            message = null;
            if (target == null || share == null)
            {
                message = "Target FBX or BlendShare patch is missing.";
                return false;
            }

            var metadata = BlendShareFbxMetadataService.Load(target);
            int restoreIndex = BlendShareFbxMetadataService.FindLatestPatchAssetIndex(metadata, share);
            if (restoreIndex < 0)
            {
                message = "This BlendShare patch is not recorded on the FBX.";
                return false;
            }

            if ((metadata.activeRecords?.Length ?? 0) <= 1)
            {
                return RestoreToOriginal(target, out message);
            }

#if ENABLE_FBX_SDK
            return RevertByReplay(target, metadata, restoreIndex, out message);
#else
            message = "FBX SDK support is not available.";
            return false;
#endif
        }

        public static bool RestoreToOriginal(GameObject target, out string message)
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
                if (!BlendShareFbxMetadataService.RestoreBaseline(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

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

#if ENABLE_FBX_SDK
        /// <summary>
        /// Creates an FBX file by applying BlendShare features to a source FBX asset.
        /// </summary>
        /// <param name="source">Source FBX asset to import and modify.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="outputPath">Destination FBX asset path, or the source asset path when omitted.</param>
        /// <param name="onlyNecessary">Whether to remove unmodified mesh nodes before export.</param>
        /// <returns><c>true</c> when the FBX file was exported successfully.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false,
            bool initializeMetadata = true,
            bool deduplicatePatchIds = true)
        {
            var shares = deduplicatePatchIds
                ? BlendSharePatchIdUtility.DeduplicateByPatchId(blendShares)
                : (blendShares ?? Enumerable.Empty<BlendShareObject>()).Where(share => share != null).ToArray();
            bool result = new FbxGenerationPipeline().Create(source, shares, outputPath, onlyNecessary, deduplicatePatchIds);
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (result &&
                !string.IsNullOrWhiteSpace(outputPath) &&
                !string.Equals(outputPath, sourcePath, StringComparison.Ordinal))
            {
                if (initializeMetadata)
                {
                    if (!BlendShareFbxMetadataService.InitializeGeneratedOutput(source, outputPath, shares, out string error))
                    {
                        Debug.LogError($"[BlendShare] Failed to initialize generated FBX metadata: {error}");
                        AssetDatabase.DeleteAsset(outputPath);
                        return false;
                    }
                }
                else
                {
                    BlendShareFbxMetadataService.ClearBlendShareMetadataAtPath(outputPath);
                }
            }

            return result;
        }

        // Feature-level inverse is intentionally disabled. Revert uses baseline replay instead.
        // public static bool RemoveBlendShapes(
        //     BlendShareObject share,
        //     GameObject target,
        //     bool removeInAllDeformer = true)
        // {
        //     return new FbxGenerationPipeline().RemoveBlendShapes(share, target, removeInAllDeformer);
        // }
#else
        /// <summary>
        /// Stub used when the Autodesk FBX SDK package is not available.
        /// </summary>
        /// <param name="source">Unused source FBX asset.</param>
        /// <param name="blendShares">Unused BlendShare assets.</param>
        /// <param name="outputPath">Unused output path.</param>
        /// <param name="onlyNecessary">Unused pruning flag.</param>
        /// <returns>Always <c>false</c> without FBX SDK support.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false,
            bool initializeMetadata = true,
            bool deduplicatePatchIds = true)
        {
            return false;
        }

        // Feature-level inverse is intentionally disabled. Revert uses baseline replay instead.
        // public static bool RemoveBlendShapes(
        //     BlendShareObject share,
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
                if (!BlendShareFbxMetadataService.RestoreBaseline(target, metadata, out message))
                {
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

                var replayShares = new List<BlendShareObject>();
                foreach (var record in replayRecords)
                {
                    if (!BlendShareFbxMetadataService.TryResolveRecord(record, out var replayShare))
                    {
                        message = $"Cannot find BlendShare patch asset '{record?.blendShareName}'.";
                        RestoreTemporaryFbxCopy(tempPath, targetPath);
                        return false;
                    }

                    replayShares.Add(replayShare);
                }

                string hashBefore = BlendShareHashUtility.Sha256File(targetPath);
                if (!CreateFbx(target, replayShares, deduplicatePatchIds: false))
                {
                    message = "Failed to reapply BlendShare patch history.";
                    RestoreTemporaryFbxCopy(tempPath, targetPath);
                    return false;
                }

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

        private static List<BlendShareObject> GetAppliedBlendShares(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> shares)
        {
            var appliedBlendShares = new List<BlendShareObject>();

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
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

            appliedBlendShares.AddRange(shares ?? Enumerable.Empty<BlendShareObject>());
            return BlendSharePatchIdUtility.DeduplicateByPatchId(appliedBlendShares).ToList();
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
