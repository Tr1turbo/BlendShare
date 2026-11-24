using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Persistence;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Persistence
{
    [Serializable]
    public sealed class BlendShareFbxMetadata
    {
        public int Version = 1;
        public string PreviousUserData = string.Empty;
        public string TargetGuid = string.Empty;
        public string TargetPath = string.Empty;
        public string BaselineBackupGuid = string.Empty;
        public string BaselineBackupPath = string.Empty;
        public string BaselineHash = string.Empty;
        public string CurrentFbxHash = string.Empty;
        public BlendShareFbxPatchRecord[] ActiveRecords = Array.Empty<BlendShareFbxPatchRecord>();
    }

    [Serializable]
    public sealed class BlendShareFbxPatchRecord
    {
        public string PatchId = string.Empty;
        public string BlendShareGuid = string.Empty;
        public string BlendSharePath = string.Empty;
        public string BlendShareName = string.Empty;
        public string AppliedAtUtc = string.Empty;
        public string SourceRevision = string.Empty;
    }

    public readonly struct BlendShareFbxPatchState
    {
        public BlendShareFbxMetadata Metadata { get; }
        public int PatchIndex { get; }
        public bool HasPatch { get; }
        public bool IsLatestPatch { get; }
        public bool HasLaterPatches { get; }
        public bool HasExactPatch { get; }
        public bool CanRevertPatch { get; }
        public int ActiveRecordCount { get; }

        public BlendShareFbxPatchState(
            BlendShareFbxMetadata metadata,
            int patchIndex,
            bool hasPatch,
            bool isLatestPatch,
            bool hasLaterPatches,
            bool hasExactPatch,
            bool canRevertPatch,
            int activeRecordCount)
        {
            Metadata = metadata;
            PatchIndex = patchIndex;
            HasPatch = hasPatch;
            IsLatestPatch = isLatestPatch;
            HasLaterPatches = hasLaterPatches;
            HasExactPatch = hasExactPatch;
            CanRevertPatch = canRevertPatch;
            ActiveRecordCount = activeRecordCount;
        }
    }

    /// <summary>
    /// Tracks applied BlendShare 2.0 patches and verified <c>.blendsharebackup</c> baselines in FBX
    /// <see cref="AssetImporter.userData"/> without losing prior userData.
    /// </summary>
    public static class BlendShareFbxMetadataService
    {
        private const int k_CurrentVersion = 1;
        private const int k_BackupMetadataVersion = 1;
        private const string k_MetadataPrefix = "BlendShareMetadata:";
        private const string k_BackupRoot = "Assets/BlendShareBackups";

        public static bool HasBlendShareMetadata(string userData)
        {
            return !string.IsNullOrEmpty(userData) && userData.StartsWith(k_MetadataPrefix, StringComparison.Ordinal);
        }

        public static bool UsesBaselineBackup(GameObject target, string backupPath)
        {
            if (target == null || string.IsNullOrWhiteSpace(backupPath))
            {
                return false;
            }

            string targetPath = AssetDatabase.GetAssetPath(target);
            var importer = GetImporter(targetPath);
            string userData = importer?.userData ?? string.Empty;
            if (!HasBlendShareMetadata(userData))
            {
                return false;
            }

            try
            {
                var metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(userData.Substring(k_MetadataPrefix.Length));
                return string.Equals(
                    NormalizeAssetPath(ResolveBackupPath(metadata)),
                    NormalizeAssetPath(backupPath),
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static BlendShareFbxMetadata Load(GameObject target)
        {
            string path = target != null ? AssetDatabase.GetAssetPath(target) : string.Empty;
            var importer = GetImporter(path);
            string userData = importer?.userData ?? string.Empty;

            BlendShareFbxMetadata metadata = null;
            if (HasBlendShareMetadata(userData))
            {
                try
                {
                    metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(userData.Substring(k_MetadataPrefix.Length));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BlendShare] Failed to parse FBX metadata for '{path}': {ex.Message}");
                }
            }

            metadata ??= new BlendShareFbxMetadata
            {
                PreviousUserData = string.IsNullOrEmpty(userData) || HasBlendShareMetadata(userData)
                    ? string.Empty
                    : userData
            };

            InitializeTarget(metadata, path);
            return metadata;
        }

        public static BlendShareFbxPatchState GetPatchState(GameObject target, BlendShareAsset patch)
        {
            var metadata = Load(target);
            int patchIndex = FindLatestPatchIndex(metadata, patch);
            int exactPatchIndex = FindLatestPatchAssetIndex(metadata, patch);
            int recordCount = metadata.ActiveRecords?.Length ?? 0;
            bool hasPatch = patchIndex >= 0;
            bool hasExactPatch = exactPatchIndex >= 0;
            bool hasLater = hasPatch && patchIndex < recordCount - 1;
            bool canRevertPatch = hasExactPatch &&
                                  TryResolveRecord(metadata.ActiveRecords[exactPatchIndex], out _) &&
                                  CanResolveRecords(
                                      metadata.ActiveRecords.Where((_, index) => index != exactPatchIndex),
                                      out _);
            return new BlendShareFbxPatchState(
                metadata,
                patchIndex,
                hasPatch,
                hasPatch && patchIndex == recordCount - 1,
                hasLater,
                hasExactPatch,
                canRevertPatch,
                recordCount);
        }

        public static bool EnsureBaselineBackup(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string targetPath = target != null ? AssetDatabase.GetAssetPath(target) : string.Empty;
            if (string.IsNullOrEmpty(targetPath))
            {
                error = "Target FBX asset path is empty.";
                return false;
            }

            string physicalTarget = BlendSharePathUtility.ResolvePhysicalPath(targetPath);
            if (string.IsNullOrEmpty(physicalTarget) || !File.Exists(physicalTarget))
            {
                error = $"Target FBX file not found: '{targetPath}'.";
                return false;
            }

            InitializeTarget(metadata, targetPath);
            string existingBackupPath = ResolveBackupPath(metadata);
            string existingPhysical = !string.IsNullOrEmpty(existingBackupPath)
                ? BlendSharePathUtility.ResolvePhysicalPath(existingBackupPath)
                : string.Empty;
            if (!string.IsNullOrEmpty(existingPhysical) && File.Exists(existingPhysical))
            {
                string actualHash = BlendShareFbxBackupImporter.ComputeSha256File(existingPhysical);
                if (string.IsNullOrEmpty(actualHash))
                {
                    error = $"Could not hash baseline backup '{existingBackupPath}'.";
                    return false;
                }

                if (!string.IsNullOrEmpty(metadata.BaselineHash) &&
                    !string.Equals(actualHash, metadata.BaselineHash, StringComparison.Ordinal))
                {
                    error = $"Baseline backup '{existingBackupPath}' hash mismatch (expected {metadata.BaselineHash}, got {actualHash}).";
                    return false;
                }

                metadata.BaselineBackupPath = NormalizeAssetPath(existingBackupPath);
                metadata.BaselineBackupGuid = AssetDatabase.AssetPathToGUID(metadata.BaselineBackupPath);
                if (string.IsNullOrEmpty(metadata.BaselineHash))
                {
                    metadata.BaselineHash = actualHash;
                }

                RefreshBackupImporterMetadata(metadata, target);
                return true;
            }

            string currentHash = BlendShareFbxBackupImporter.ComputeSha256File(physicalTarget);
            if (string.IsNullOrEmpty(currentHash))
            {
                error = $"Could not hash target FBX '{targetPath}'.";
                return false;
            }

            string backupFolder = GetBackupFolder(metadata.TargetGuid);
            string physicalFolder = BlendSharePathUtility.ResolvePhysicalPath(backupFolder);
            Directory.CreateDirectory(physicalFolder);

            string fileName = $"{Path.GetFileName(targetPath)}.blendsharebackup";
            string backupAssetPath = NormalizeAssetPath(Path.Combine(backupFolder, fileName));
            string backupPhysical = BlendSharePathUtility.ResolvePhysicalPath(backupAssetPath);

            if (File.Exists(backupPhysical))
            {
                if (string.Equals(BlendShareFbxBackupImporter.ComputeSha256File(backupPhysical), currentHash, StringComparison.Ordinal))
                {
                    metadata.BaselineBackupPath = backupAssetPath;
                    metadata.BaselineBackupGuid = AssetDatabase.AssetPathToGUID(backupAssetPath);
                    metadata.BaselineHash = currentHash;
                    metadata.CurrentFbxHash = currentHash;
                    WriteBackupImporterMetadata(backupAssetPath, targetPath, target, metadata, currentHash);
                    return true;
                }

                backupAssetPath = AssetDatabase.GenerateUniqueAssetPath(backupAssetPath);
                backupPhysical = BlendSharePathUtility.ResolvePhysicalPath(backupAssetPath);
            }

            File.Copy(physicalTarget, backupPhysical, overwrite: false);
            AssetDatabase.ImportAsset(backupAssetPath, ImportAssetOptions.ForceSynchronousImport);
            WriteBackupImporterMetadata(backupAssetPath, targetPath, target, metadata, currentHash);

            metadata.BaselineBackupPath = backupAssetPath;
            metadata.BaselineBackupGuid = AssetDatabase.AssetPathToGUID(backupAssetPath);
            metadata.BaselineHash = BlendShareFbxBackupImporter.ComputeSha256File(backupPhysical);
            metadata.CurrentFbxHash = currentHash;
            RefreshBackupImporterMetadata(metadata, target);
            return true;
        }

        public static bool InitializeGeneratedOutput(
            GameObject source,
            string outputAssetPath,
            IReadOnlyList<BlendShareAsset> patches,
            out string error)
        {
            error = null;
            string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(outputAssetPath))
            {
                error = "Source or generated FBX path is empty.";
                return false;
            }

            var sourceMetadata = Load(source);
            if (!EnsureBaselineBackup(source, sourceMetadata, out error))
            {
                return false;
            }

            if (!Save(source, sourceMetadata, out error))
            {
                return false;
            }

            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var importer = GetImporter(outputAssetPath);
            if (importer == null)
            {
                error = $"Could not load importer for generated FBX '{outputAssetPath}'.";
                return false;
            }

            string outputPhysical = BlendSharePathUtility.ResolvePhysicalPath(outputAssetPath);
            string outputHash = BlendShareFbxBackupImporter.ComputeSha256File(outputPhysical);
            string backupPath = ResolveBackupPath(sourceMetadata);
            string backupPhysical = !string.IsNullOrEmpty(backupPath) ? BlendSharePathUtility.ResolvePhysicalPath(backupPath) : string.Empty;

            var records = new List<BlendShareFbxPatchRecord>();
            if (sourceMetadata.ActiveRecords != null)
            {
                for (int i = 0; i < sourceMetadata.ActiveRecords.Length; i++)
                {
                    if (sourceMetadata.ActiveRecords[i] != null)
                    {
                        records.Add(CopyRecord(sourceMetadata.ActiveRecords[i]));
                    }
                }
            }

            if (patches != null)
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    if (patches[i] != null)
                    {
                        records.Add(CreateRecord(patches[i]));
                    }
                }
            }

            var metadata = new BlendShareFbxMetadata
            {
                Version = k_CurrentVersion,
                PreviousUserData = string.IsNullOrEmpty(importer.userData) || HasBlendShareMetadata(importer.userData)
                    ? string.Empty
                    : importer.userData,
                BaselineBackupGuid = sourceMetadata.BaselineBackupGuid,
                BaselineBackupPath = backupPath,
                BaselineHash = string.IsNullOrEmpty(sourceMetadata.BaselineHash)
                    ? BlendShareFbxBackupImporter.ComputeSha256File(backupPhysical)
                    : sourceMetadata.BaselineHash,
                CurrentFbxHash = outputHash,
                ActiveRecords = records.ToArray()
            };
            InitializeTarget(metadata, outputAssetPath);

            var outputAsset = AssetDatabase.LoadAssetAtPath<GameObject>(outputAssetPath);
            if (!Save(outputAsset, metadata, out error))
            {
                return false;
            }

            RefreshBackupImporterMetadata(metadata, outputAsset);
            return true;
        }

        public static BlendShareFbxPatchRecord CreateRecord(BlendShareAsset patch)
        {
            string assetPath = patch != null ? AssetDatabase.GetAssetPath(patch) : string.Empty;
            string guid = !string.IsNullOrEmpty(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : string.Empty;
            return new BlendShareFbxPatchRecord
            {
                PatchId = patch?.PatchId ?? string.Empty,
                BlendShareGuid = guid ?? string.Empty,
                BlendSharePath = NormalizeAssetPath(assetPath),
                BlendShareName = patch != null ? patch.name : string.Empty,
                AppliedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SourceRevision = patch?.SourceRevision ?? string.Empty
            };
        }

        public static BlendShareFbxPatchRecord CopyRecord(BlendShareFbxPatchRecord source)
        {
            if (source == null) return null;
            return new BlendShareFbxPatchRecord
            {
                PatchId = source.PatchId ?? string.Empty,
                BlendShareGuid = source.BlendShareGuid ?? string.Empty,
                BlendSharePath = source.BlendSharePath ?? string.Empty,
                BlendShareName = source.BlendShareName ?? string.Empty,
                AppliedAtUtc = source.AppliedAtUtc ?? string.Empty,
                SourceRevision = source.SourceRevision ?? string.Empty
            };
        }

        public static void CommitApplyRecord(BlendShareFbxMetadata metadata, BlendShareAsset patch, BlendShareFbxPatchRecord record)
        {
            if (metadata == null || record == null) return;

            var list = (metadata.ActiveRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                .Where(r => r != null && !IsSamePatch(r, patch))
                .ToList();
            list.Add(record);
            metadata.ActiveRecords = list.ToArray();
        }

        public static bool Save(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string path = target != null ? AssetDatabase.GetAssetPath(target) : metadata?.TargetPath;
            var importer = GetImporter(path);
            if (importer == null)
            {
                error = $"Could not load AssetImporter for '{path}'.";
                return false;
            }

            InitializeTarget(metadata, path);
            string physical = BlendSharePathUtility.ResolvePhysicalPath(path);
            if (!string.IsNullOrEmpty(physical) && File.Exists(physical))
            {
                metadata.CurrentFbxHash = BlendShareFbxBackupImporter.ComputeSha256File(physical);
            }

            importer.userData = k_MetadataPrefix + JsonUtility.ToJson(metadata);
            importer.SaveAndReimport();
            return true;
        }

        public static bool Clear(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string path = target != null ? AssetDatabase.GetAssetPath(target) : metadata?.TargetPath;
            var importer = GetImporter(path);
            if (importer == null)
            {
                error = $"Could not load AssetImporter for '{path}'.";
                return false;
            }

            importer.userData = metadata?.PreviousUserData ?? string.Empty;
            importer.SaveAndReimport();
            return true;
        }

        public static bool RestoreBaseline(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string targetPath = target != null ? AssetDatabase.GetAssetPath(target) : string.Empty;
            if (string.IsNullOrEmpty(targetPath))
            {
                error = "Target FBX asset path is empty.";
                return false;
            }

            string backupPath = ResolveBackupPath(metadata);
            if (string.IsNullOrEmpty(backupPath))
            {
                error = "No .blendsharebackup baseline is recorded for this FBX.";
                return false;
            }

            string physicalTarget = BlendSharePathUtility.ResolvePhysicalPath(targetPath);
            string physicalBackup = BlendSharePathUtility.ResolvePhysicalPath(backupPath);
            if (string.IsNullOrEmpty(physicalBackup) || !File.Exists(physicalBackup))
            {
                error = $"Baseline backup file not found: '{backupPath}'.";
                return false;
            }

            if (!string.IsNullOrEmpty(metadata?.BaselineHash))
            {
                string actualHash = BlendShareFbxBackupImporter.ComputeSha256File(physicalBackup);
                if (!string.Equals(actualHash, metadata.BaselineHash, StringComparison.Ordinal))
                {
                    error = $"Baseline backup '{backupPath}' hash mismatch (expected {metadata.BaselineHash}, got {actualHash}).";
                    return false;
                }
            }

            string tempPath = physicalTarget + "." + Guid.NewGuid().ToString("N") + ".restore.tmp";
            try
            {
                File.Copy(physicalBackup, tempPath, overwrite: true);
                if (File.Exists(physicalTarget))
                {
                    File.Replace(tempPath, physicalTarget, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, physicalTarget);
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                error = $"Failed restoring baseline FBX: {ex.Message}";
                return false;
            }

            if (!Clear(target, metadata, out string clearError))
            {
                error = clearError;
                return false;
            }

            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return true;
        }

        public static int FindLatestPatchIndex(BlendShareFbxMetadata metadata, BlendShareAsset patch)
        {
            var records = metadata?.ActiveRecords;
            if (records == null || patch == null) return -1;
            for (int i = records.Length - 1; i >= 0; i--)
            {
                if (IsSamePatch(records[i], patch))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int FindLatestPatchAssetIndex(BlendShareFbxMetadata metadata, BlendShareAsset patch)
        {
            var records = metadata?.ActiveRecords;
            if (records == null || patch == null) return -1;
            for (int i = records.Length - 1; i >= 0; i--)
            {
                if (IsSamePatchAsset(records[i], patch))
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool TryResolveRecord(BlendShareFbxPatchRecord record, out BlendShareAsset patch)
        {
            patch = null;
            if (record == null) return false;

            if (!string.IsNullOrEmpty(record.BlendShareGuid))
            {
                string pathByGuid = AssetDatabase.GUIDToAssetPath(record.BlendShareGuid);
                if (!string.IsNullOrEmpty(pathByGuid))
                {
                    patch = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(pathByGuid);
                    if (patch != null) return true;
                }
            }

            if (!string.IsNullOrEmpty(record.BlendSharePath))
            {
                patch = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(record.BlendSharePath);
                if (patch != null) return true;
            }

            return false;
        }

        public static bool CanResolveRecords(IEnumerable<BlendShareFbxPatchRecord> records, out string error)
        {
            error = null;
            foreach (var record in records ?? Array.Empty<BlendShareFbxPatchRecord>())
            {
                if (record == null) continue;
                if (!TryResolveRecord(record, out _))
                {
                    error = $"Missing patch asset '{record.BlendShareName}' ({record.PatchId}).";
                    return false;
                }
            }

            return true;
        }

        public static bool IsSamePatch(BlendShareFbxPatchRecord record, BlendShareAsset patch)
        {
            if (record == null || patch == null) return false;
            if (!string.IsNullOrEmpty(record.PatchId) &&
                !string.IsNullOrEmpty(patch.PatchId) &&
                string.Equals(record.PatchId, patch.PatchId, StringComparison.Ordinal))
            {
                return true;
            }

            return IsSamePatchAsset(record, patch);
        }

        public static bool IsSamePatchAsset(BlendShareFbxPatchRecord record, BlendShareAsset patch)
        {
            if (record == null || patch == null) return false;
            string path = NormalizeAssetPath(AssetDatabase.GetAssetPath(patch));
            string guid = !string.IsNullOrEmpty(path) ? AssetDatabase.AssetPathToGUID(path) : string.Empty;
            if (!string.IsNullOrEmpty(guid) && string.Equals(record.BlendShareGuid, guid, StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrEmpty(path) && string.Equals(NormalizeAssetPath(record.BlendSharePath), path, StringComparison.Ordinal);
        }

        public static string ResolveBackupPath(BlendShareFbxMetadata metadata)
        {
            if (metadata == null) return string.Empty;
            if (!string.IsNullOrEmpty(metadata.BaselineBackupGuid))
            {
                string byGuid = AssetDatabase.GUIDToAssetPath(metadata.BaselineBackupGuid);
                if (!string.IsNullOrEmpty(byGuid))
                {
                    return NormalizeAssetPath(byGuid);
                }
            }

            return NormalizeAssetPath(metadata.BaselineBackupPath);
        }

        private static void InitializeTarget(BlendShareFbxMetadata metadata, string assetPath)
        {
            metadata.Version = k_CurrentVersion;
            metadata.TargetPath = NormalizeAssetPath(assetPath);
            metadata.TargetGuid = !string.IsNullOrEmpty(metadata.TargetPath)
                ? AssetDatabase.AssetPathToGUID(metadata.TargetPath)
                : string.Empty;
            metadata.ActiveRecords ??= Array.Empty<BlendShareFbxPatchRecord>();
        }

        private static string GetBackupFolder(string targetGuid)
        {
            string suffix = string.IsNullOrEmpty(targetGuid) ? "untracked" : targetGuid;
            return $"{k_BackupRoot}/{suffix}";
        }

        private static void WriteBackupImporterMetadata(
            string backupAssetPath,
            string sourceFbxPath,
            GameObject sourceFbx,
            BlendShareFbxMetadata metadata,
            string sourceHash)
        {
            if (AssetImporter.GetAtPath(backupAssetPath) is not BlendShareFbxBackupImporter backupImporter)
            {
                return;
            }

            var derived = new List<GameObject>(backupImporter.DerivedFbxs ?? Array.Empty<GameObject>());
            if (sourceFbx != null && !derived.Contains(sourceFbx))
            {
                derived.Add(sourceFbx);
            }

            backupImporter.SetMetadata(new BlendShareFbxBackupMetadata
            {
                Version = k_BackupMetadataVersion,
                SourceFbx = backupImporter.SourceFbx != null ? backupImporter.SourceFbx : sourceFbx,
                DerivedFbxs = derived.Where(go => go != null).Distinct().ToArray(),
                SourceFbxHash = string.IsNullOrEmpty(backupImporter.SourceFbxHash) ? sourceHash : backupImporter.SourceFbxHash,
                BackupPath = NormalizeAssetPath(backupAssetPath),
                CreatedAtUtc = string.IsNullOrEmpty(backupImporter.CreatedAtUtc)
                    ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : backupImporter.CreatedAtUtc,
                OriginalFileName = string.IsNullOrEmpty(backupImporter.OriginalFileName)
                    ? Path.GetFileName(sourceFbxPath)
                    : backupImporter.OriginalFileName
            });
            backupImporter.SaveAndReimport();
        }

        private static void RefreshBackupImporterMetadata(BlendShareFbxMetadata metadata, GameObject derivedFbx)
        {
            string backupPath = ResolveBackupPath(metadata);
            if (string.IsNullOrEmpty(backupPath)) return;
            if (AssetImporter.GetAtPath(backupPath) is not BlendShareFbxBackupImporter backupImporter) return;

            var derived = new List<GameObject>(backupImporter.DerivedFbxs ?? Array.Empty<GameObject>());
            bool changed = false;
            if (derivedFbx != null && !derived.Contains(derivedFbx))
            {
                derived.Add(derivedFbx);
                changed = true;
            }

            int beforeCount = derived.Count;
            derived = derived.Where(go => go != null).Distinct().ToList();
            if (derived.Count != beforeCount) changed = true;

            if (!changed) return;

            backupImporter.SetMetadata(new BlendShareFbxBackupMetadata
            {
                Version = k_BackupMetadataVersion,
                SourceFbx = backupImporter.SourceFbx,
                DerivedFbxs = derived.ToArray(),
                SourceFbxHash = backupImporter.SourceFbxHash,
                BackupPath = string.IsNullOrEmpty(backupImporter.BackupPath) ? backupPath : backupImporter.BackupPath,
                CreatedAtUtc = backupImporter.CreatedAtUtc,
                OriginalFileName = backupImporter.OriginalFileName
            });
            backupImporter.SaveAndReimport();
        }

        private static AssetImporter GetImporter(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? null : AssetImporter.GetAtPath(assetPath);
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }
    }
}
