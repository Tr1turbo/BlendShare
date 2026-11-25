using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Pipeline.Export;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Persistence
{
    /// <summary>
    /// High-level Editor orchestration facade for applying, creating, reverting, and restoring FBX patches
    /// via <see cref="FbxPatchExportBackendRegistry"/> and <see cref="BlendShareFbxMetadataService"/>.
    /// Zero direct <c>Autodesk.Fbx</c> dependency.
    /// </summary>
    public static class BlendShareGenerationService
    {
        public static bool IsFbxExportAvailable => FbxPatchExportBackendRegistry.QueryAvailability().IsAvailable;

        public static bool ApplyPatch(
            GameObject targetFbx,
            BlendShareAsset patch,
            bool force,
            out string message)
        {
            message = null;
            if (targetFbx == null || patch == null)
            {
                message = "Target FBX and BlendShare patch asset are required.";
                return false;
            }

            var availability = FbxPatchExportBackendRegistry.QueryAvailability();
            if (!availability.IsAvailable)
            {
                message = availability.UnavailableDetails;
                return false;
            }

            var state = BlendShareFbxMetadataService.GetPatchState(targetFbx, patch);
            if (state.HasPatch && !force)
            {
                message = "This patch is already applied to the target FBX. Unlock force-apply to overwrite.";
                return false;
            }

            string targetAssetPath = AssetDatabase.GetAssetPath(targetFbx);
            string physicalTarget = BlendSharePathUtility.ResolvePhysicalPath(targetAssetPath);
            if (string.IsNullOrEmpty(physicalTarget) || !File.Exists(physicalTarget))
            {
                message = $"Target FBX file not found: '{targetAssetPath}'.";
                return false;
            }

            var metadata = state.Metadata;
            if (!BlendShareFbxMetadataService.EnsureBaselineBackup(targetFbx, metadata, out string backupError))
            {
                message = backupError;
                return false;
            }

            if (!TryBuildExportItem(patch, out var exportItem, out string itemError))
            {
                message = itemError;
                return false;
            }

            if (state.HasPatch && force)
            {
                string backupAssetPath = BlendShareFbxMetadataService.ResolveBackupPath(metadata);
                string physicalBackup = BlendSharePathUtility.ResolvePhysicalPath(backupAssetPath);
                if (string.IsNullOrEmpty(physicalBackup) || !File.Exists(physicalBackup))
                {
                    message = $"Cannot rebuild from baseline: backup '{backupAssetPath}' is missing.";
                    return false;
                }

                if (!string.IsNullOrEmpty(metadata.BaselineHash))
                {
                    string actualBackupHash = EditorAssets.Persistence.BlendShareFbxBackupImporter.ComputeSha256File(physicalBackup);
                    if (!string.Equals(actualBackupHash, metadata.BaselineHash, StringComparison.Ordinal))
                    {
                        message = $"Baseline backup '{backupAssetPath}' hash mismatch (expected {metadata.BaselineHash}, got {actualBackupHash}).";
                        return false;
                    }
                }

                var replayItems = new List<FbxPatchExportItem>();
                foreach (var existingRecord in metadata.ActiveRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                {
                    if (existingRecord == null || BlendShareFbxMetadataService.IsSamePatch(existingRecord, patch))
                    {
                        continue;
                    }

                    if (!BlendShareFbxMetadataService.TryResolveRecord(existingRecord, out var existingPatch))
                    {
                        message = $"Cannot rebuild from baseline: missing applied patch '{existingRecord.BlendShareName}'.";
                        return false;
                    }

                    if (!TryBuildExportItem(existingPatch, out var existingItem, out string existingErr))
                    {
                        message = existingErr;
                        return false;
                    }

                    replayItems.Add(existingItem);
                }

                replayItems.Add(exportItem);

                string tempRebuild = physicalTarget + "." + Guid.NewGuid().ToString("N") + ".rebuild.fbx";
                try
                {
                    File.Copy(physicalBackup, tempRebuild, overwrite: true);
                    var rebuildResult = FbxPatchExportBackendRegistry.Export(new FbxPatchExportRequest
                    {
                        TargetFbxPath = tempRebuild,
                        Patches = replayItems,
                        CreateBackup = false,
                        VerifyRoundTripNumerics = true,
                        PayloadStore = BlendShareSourceRepository.Shared.PayloadStore
                    });

                    if (!rebuildResult.Succeeded)
                    {
                        message = FormatExportDiagnostics(rebuildResult.Diagnostics, "FBX baseline rebuild failed.");
                        return false;
                    }

                    File.Replace(tempRebuild, physicalTarget, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    tempRebuild = null;
                }
                catch (Exception ex)
                {
                    message = $"FBX baseline rebuild failed: {ex.Message}";
                    return false;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempRebuild) && File.Exists(tempRebuild))
                    {
                        try { File.Delete(tempRebuild); } catch { }
                    }
                }
            }
            else
            {
                var exportResult = FbxPatchExportBackendRegistry.Export(new FbxPatchExportRequest
                {
                    TargetFbxPath = physicalTarget,
                    Patches = new[] { exportItem },
                    CreateBackup = false,
                    VerifyRoundTripNumerics = true,
                    PayloadStore = BlendShareSourceRepository.Shared.PayloadStore
                });

                if (!exportResult.Succeeded)
                {
                    message = FormatExportDiagnostics(exportResult.Diagnostics, "FBX patch export failed.");
                    return false;
                }
            }

            var record = BlendShareFbxMetadataService.CreateRecord(patch);
            BlendShareFbxMetadataService.CommitApplyRecord(metadata, patch, record);
            if (!BlendShareFbxMetadataService.Save(targetFbx, metadata, out string saveError))
            {
                message = saveError;
                return false;
            }

            AssetDatabase.ImportAsset(targetAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            message = $"Applied '{record.BlendShareName}' to '{targetFbx.name}'.";
            return true;
        }

        public static bool CreateFbx(
            GameObject sourceFbx,
            IReadOnlyList<BlendShareAsset> patches,
            string outputAssetPath,
            out string message)
        {
            message = null;
            if (sourceFbx == null || patches == null || patches.Count == 0 || string.IsNullOrWhiteSpace(outputAssetPath))
            {
                message = "Source FBX, at least one patch, and an output path are required.";
                return false;
            }

            var availability = FbxPatchExportBackendRegistry.QueryAvailability();
            if (!availability.IsAvailable)
            {
                message = availability.UnavailableDetails;
                return false;
            }

            string sourceAssetPath = AssetDatabase.GetAssetPath(sourceFbx);
            string physicalSource = BlendSharePathUtility.ResolvePhysicalPath(sourceAssetPath);
            string physicalOutput = BlendSharePathUtility.ResolvePhysicalPath(outputAssetPath);
            if (string.IsNullOrEmpty(physicalSource) || !File.Exists(physicalSource))
            {
                message = $"Source FBX file not found: '{sourceAssetPath}'.";
                return false;
            }

            var items = new List<FbxPatchExportItem>(patches.Count);
            for (int i = 0; i < patches.Count; i++)
            {
                if (patches[i] == null) continue;
                if (!TryBuildExportItem(patches[i], out var item, out string itemError))
                {
                    message = itemError;
                    return false;
                }

                items.Add(item);
            }

            if (items.Count == 0)
            {
                message = "No valid patches to apply.";
                return false;
            }

            string outputDir = Path.GetDirectoryName(physicalOutput);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            string tempCopy = physicalOutput + "." + Guid.NewGuid().ToString("N") + ".fbx";
            try
            {
                File.Copy(physicalSource, tempCopy, overwrite: true);
                var exportResult = FbxPatchExportBackendRegistry.Export(new FbxPatchExportRequest
                {
                    TargetFbxPath = tempCopy,
                    Patches = items,
                    CreateBackup = false,
                    VerifyRoundTripNumerics = true,
                    PayloadStore = BlendShareSourceRepository.Shared.PayloadStore
                });

                if (!exportResult.Succeeded)
                {
                    message = FormatExportDiagnostics(exportResult.Diagnostics, "Create FBX failed.");
                    return false;
                }

                if (File.Exists(physicalOutput))
                {
                    File.Replace(tempCopy, physicalOutput, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempCopy, physicalOutput);
                }

                tempCopy = null;
            }
            catch (Exception ex)
            {
                message = $"Failed to create FBX: {ex.Message}";
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempCopy) && File.Exists(tempCopy))
                {
                    try { File.Delete(tempCopy); } catch { }
                }
            }

            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            BlendShareFbxMetadataService.InitializeGeneratedOutput(sourceFbx, outputAssetPath, patches, out _);
            message = $"Created FBX at '{outputAssetPath}'.";
            return true;
        }

        public static bool RestoreToOriginal(GameObject targetFbx, out string message)
        {
            message = null;
            if (targetFbx == null)
            {
                message = "Target FBX is required.";
                return false;
            }

            var metadata = BlendShareFbxMetadataService.Load(targetFbx);
            if (!BlendShareFbxMetadataService.RestoreBaseline(targetFbx, metadata, out string error))
            {
                message = error;
                return false;
            }

            message = $"Restored '{targetFbx.name}' to original baseline.";
            return true;
        }

        public static bool RevertPatch(GameObject targetFbx, BlendShareAsset patch, out string message)
        {
            message = null;
            if (targetFbx == null || patch == null)
            {
                message = "Target FBX and patch asset are required.";
                return false;
            }

            var state = BlendShareFbxMetadataService.GetPatchState(targetFbx, patch);
            if (!state.HasExactPatch)
            {
                message = "Patch is not currently recorded on this FBX.";
                return false;
            }

            var remainingRecords = state.Metadata.ActiveRecords
                .Where((_, idx) => idx != state.PatchIndex)
                .Where(r => r != null)
                .ToArray();

            if (remainingRecords.Length == 0)
            {
                return RestoreToOriginal(targetFbx, out message);
            }

            var remainingPatches = new List<BlendShareAsset>(remainingRecords.Length);
            for (int i = 0; i < remainingRecords.Length; i++)
            {
                if (!BlendShareFbxMetadataService.TryResolveRecord(remainingRecords[i], out var resolved))
                {
                    message = $"Cannot revert: missing earlier/later patch '{remainingRecords[i].BlendShareName}'.";
                    return false;
                }

                remainingPatches.Add(resolved);
            }

            var availability = FbxPatchExportBackendRegistry.QueryAvailability();
            if (!availability.IsAvailable)
            {
                message = availability.UnavailableDetails;
                return false;
            }

            string targetAssetPath = AssetDatabase.GetAssetPath(targetFbx);
            string physicalTarget = BlendSharePathUtility.ResolvePhysicalPath(targetAssetPath);
            string backupAssetPath = BlendShareFbxMetadataService.ResolveBackupPath(state.Metadata);
            string physicalBackup = BlendSharePathUtility.ResolvePhysicalPath(backupAssetPath);
            if (string.IsNullOrEmpty(physicalBackup) || !File.Exists(physicalBackup))
            {
                message = $"Cannot revert: baseline backup '{backupAssetPath}' is missing.";
                return false;
            }

            if (!string.IsNullOrEmpty(state.Metadata.BaselineHash))
            {
                string actualBackupHash = EditorAssets.Persistence.BlendShareFbxBackupImporter.ComputeSha256File(physicalBackup);
                if (!string.Equals(actualBackupHash, state.Metadata.BaselineHash, StringComparison.Ordinal))
                {
                    message = $"Baseline backup '{backupAssetPath}' hash mismatch (expected {state.Metadata.BaselineHash}, got {actualBackupHash}).";
                    return false;
                }
            }

            var exportItems = new List<FbxPatchExportItem>(remainingPatches.Count);
            for (int i = 0; i < remainingPatches.Count; i++)
            {
                if (!TryBuildExportItem(remainingPatches[i], out var item, out string itemErr))
                {
                    message = itemErr;
                    return false;
                }

                exportItems.Add(item);
            }

            string tempPath = physicalTarget + "." + Guid.NewGuid().ToString("N") + ".revert.fbx";
            try
            {
                File.Copy(physicalBackup, tempPath, overwrite: true);
                var result = FbxPatchExportBackendRegistry.Export(new FbxPatchExportRequest
                {
                    TargetFbxPath = tempPath,
                    Patches = exportItems,
                    CreateBackup = false,
                    VerifyRoundTripNumerics = true,
                    PayloadStore = BlendShareSourceRepository.Shared.PayloadStore
                });

                if (!result.Succeeded)
                {
                    message = FormatExportDiagnostics(result.Diagnostics, "Revert patch failed during replay.");
                    return false;
                }

                File.Replace(tempPath, physicalTarget, destinationBackupFileName: null, ignoreMetadataErrors: true);
                tempPath = null;
            }
            catch (Exception ex)
            {
                message = $"Revert patch failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            state.Metadata.ActiveRecords = remainingRecords;
            if (!BlendShareFbxMetadataService.Save(targetFbx, state.Metadata, out string saveError))
            {
                message = saveError;
                return false;
            }

            AssetDatabase.ImportAsset(targetAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            message = $"Reverted '{patch.name}' from '{targetFbx.name}'.";
            return true;
        }

        private static bool TryBuildExportItem(BlendShareAsset patch, out FbxPatchExportItem item, out string error)
        {
            item = null;
            error = null;

            string assetPath = AssetDatabase.GetAssetPath(patch);
            string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(assetPath);
            if (string.IsNullOrEmpty(physicalPath) || !File.Exists(physicalPath))
            {
                error = $"Patch source file not found: '{assetPath}'.";
                return false;
            }

            if (!BlendShareSourceRepository.Shared.TryGetGuidAndToc(patch, out _, out var toc))
            {
                error = $"Failed reading TOC for patch '{patch.name}'.";
                return false;
            }

            var docResult = BlendShareSourceRepository.Shared.ReadPatchDocument(patch);
            if (!docResult.Success || docResult.Value == null)
            {
                error = $"Failed reading patch document '{patch.name}': {docResult.Message}";
                return false;
            }

            var settings = EffectiveSettingsResolver.Resolve(patch).Settings;
            item = new FbxPatchExportItem(
                string.IsNullOrWhiteSpace(patch.PatchId) ? patch.name : patch.PatchId,
                physicalPath,
                toc,
                docResult.Value,
                settings);
            return true;
        }

        private static string FormatExportDiagnostics(IReadOnlyList<FbxExportDiagnostic> diagnostics, string fallback)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return fallback;
            }

            var errors = diagnostics
                .Where(d => d.Severity == FbxExportDiagnosticSeverity.Error)
                .Select(d => $"[{d.Code}] {d.Message}")
                .ToArray();
            return errors.Length > 0 ? string.Join("\n", errors) : fallback;
        }
    }
}
