using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Hashing;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    public static class BlendShareFbxMetadataService
    {
        private const int CurrentVersion = 1;
        private const string MetadataPrefix = "BlendShareMetadata:";
        private const string BackupRoot = "Assets/BlendShareBackups";
        internal const int BackupMetadataVersion = 1;
        internal const string BackupMetadataPrefix = "BlendShareBackupMetadata:";

        public static BlendShareFbxMetadata Load(GameObject target)
        {
            string path = GetAssetPath(target);
            var importer = GetImporter(path);
            string userData = importer?.userData ?? string.Empty;

            BlendShareFbxMetadata metadata = null;
            if (userData.StartsWith(MetadataPrefix, StringComparison.Ordinal))
            {
                try
                {
                    metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(userData.Substring(MetadataPrefix.Length));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[BlendShare] Failed to parse FBX metadata for '{path}': {exception.Message}");
                }
            }

            metadata ??= new BlendShareFbxMetadata
            {
                previousUserData = string.IsNullOrEmpty(userData) || userData.StartsWith(MetadataPrefix, StringComparison.Ordinal)
                    ? string.Empty
                    : userData
            };

            InitializeTarget(metadata, path);
            return metadata;
        }

        public static BlendShareFbxPatchState GetPatchState(GameObject target, BlendShareObject share)
        {
            var metadata = Load(target);
            int patchIndex = FindLatestPatchIndex(metadata, share);
            int recordCount = metadata.activeRecords?.Length ?? 0;
            bool hasPatch = patchIndex >= 0;
            bool hasLater = hasPatch && patchIndex < recordCount - 1;
            bool canReplayRemaining = !hasPatch || CanResolveRecords(
                metadata.activeRecords.Where((_, index) => index != patchIndex),
                out _);
            return new BlendShareFbxPatchState(
                metadata,
                patchIndex,
                hasPatch,
                hasPatch && patchIndex == recordCount - 1,
                hasLater,
                canReplayRemaining,
                recordCount);
        }

        public static bool EnsureBaselineBackup(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string targetPath = GetAssetPath(target);
            if (string.IsNullOrEmpty(targetPath))
            {
                error = "Target FBX asset path is empty.";
                return false;
            }

            InitializeTarget(metadata, targetPath);
            string existingBackupPath = ResolveBackupPath(metadata);
            if (!string.IsNullOrEmpty(existingBackupPath) && File.Exists(existingBackupPath))
            {
                metadata.baselineBackupPath = existingBackupPath;
                metadata.baselineBackupGuid = AssetDatabase.AssetPathToGUID(existingBackupPath);
                return true;
            }

            string currentHash = BlendShareHashUtility.Sha256File(targetPath);
            if (string.IsNullOrEmpty(currentHash))
            {
                error = $"Could not hash target FBX '{targetPath}'.";
                return false;
            }

            string backupFolder = GetBackupFolder(metadata.targetGuid);
            Directory.CreateDirectory(backupFolder);
            string fileName = $"{Path.GetFileName(targetPath)}.blendsharebackup";
            string backupPath = Path.Combine(backupFolder, fileName).Replace('\\', '/');
            if (File.Exists(backupPath))
            {
                if (BlendShareHashUtility.Sha256File(backupPath) == currentHash)
                {
                    metadata.baselineBackupPath = backupPath;
                    metadata.baselineBackupGuid = AssetDatabase.AssetPathToGUID(backupPath);
                    metadata.baselineHash = currentHash;
                    metadata.currentFbxHash = currentHash;
                    WriteBackupImporterMetadata(backupPath, targetPath, metadata, currentHash);
                    return true;
                }

                backupPath = AssetDatabase.GenerateUniqueAssetPath(backupPath);
            }

            File.Copy(targetPath, backupPath, false);
            WriteBackupImporterMetadata(backupPath, targetPath, metadata, currentHash);

            metadata.baselineBackupPath = backupPath;
            metadata.baselineBackupGuid = AssetDatabase.AssetPathToGUID(backupPath);
            metadata.baselineHash = BlendShareHashUtility.Sha256File(backupPath);
            metadata.currentFbxHash = currentHash;
            return true;
        }

        public static BlendShareFbxPatchRecord CreateRecord(
            GameObject target,
            BlendShareObject share,
            string hashBefore,
            string hashAfter)
        {
            GetBlendShareIdentity(share, out string guid, out string localId);
            return new BlendShareFbxPatchRecord
            {
                recordId = Guid.NewGuid().ToString("N"),
                blendShareGuid = guid,
                blendShareLocalId = localId,
                blendSharePath = AssetDatabase.GetAssetPath(share),
                blendShareName = share != null ? share.name : string.Empty,
                deformerId = share != null ? share.m_DeformerID : string.Empty,
                featureIds = GetFeatureIds(share),
                appliedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                hashBefore = hashBefore ?? string.Empty,
                hashAfter = hashAfter ?? string.Empty
            };
        }

        public static void CommitApplyRecord(BlendShareFbxMetadata metadata, BlendShareObject share, BlendShareFbxPatchRecord record)
        {
            var records = (metadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                .Where(existing => existing != null)
                .ToList();

            if (records.Count > 0 && IsSamePatch(records[records.Count - 1], share))
            {
                records[records.Count - 1] = record;
            }
            else
            {
                records.Add(record);
            }

            metadata.activeRecords = records.ToArray();
            metadata.currentFbxHash = record.hashAfter;
        }

        public static bool Save(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string path = GetAssetPath(target);
            var importer = GetImporter(path);
            if (importer == null)
            {
                error = $"Could not load importer for '{path}'.";
                return false;
            }

            InitializeTarget(metadata, path);
            metadata.currentFbxHash = BlendShareHashUtility.Sha256File(path);
            importer.userData = MetadataPrefix + JsonUtility.ToJson(metadata, false);
            importer.SaveAndReimport();
            RefreshBackupImporterMetadata(metadata);
            return true;
        }

        public static bool Clear(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string path = GetAssetPath(target);
            var importer = GetImporter(path);
            if (importer == null)
            {
                error = $"Could not load importer for '{path}'.";
                return false;
            }

            importer.userData = metadata?.previousUserData ?? string.Empty;
            importer.SaveAndReimport();
            return true;
        }

        public static bool ClearBlendShareMetadataAtPath(string path)
        {
            var importer = GetImporter(path);
            if (importer == null || string.IsNullOrEmpty(importer.userData) ||
                !importer.userData.StartsWith(MetadataPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            BlendShareFbxMetadata metadata = null;
            try
            {
                metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(importer.userData.Substring(MetadataPrefix.Length));
            }
            catch
            {
                // Clear malformed BlendShare metadata on generated copies.
            }

            importer.userData = metadata?.previousUserData ?? string.Empty;
            importer.SaveAndReimport();
            return true;
        }

        public static bool RestoreBaseline(GameObject target, BlendShareFbxMetadata metadata, out string error)
        {
            error = null;
            string targetPath = GetAssetPath(target);
            if (string.IsNullOrEmpty(targetPath))
            {
                error = "Target FBX asset path is empty.";
                return false;
            }

            string backupPath = ResolveBackupPath(metadata);
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            {
                error = "BlendShare baseline backup is missing.";
                return false;
            }

            metadata.baselineBackupPath = backupPath;
            File.Copy(backupPath, targetPath, true);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return true;
        }

        public static bool PruneBackup(BlendShareFbxMetadata metadata)
        {
            string backupPath = ResolveBackupPath(metadata);
            if (string.IsNullOrEmpty(backupPath))
            {
                return true;
            }

            string folder = Path.GetDirectoryName(backupPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith(BackupRoot, StringComparison.Ordinal))
            {
                return false;
            }

            return AssetDatabase.DeleteAsset(folder);
        }

        public static int FindLatestPatchIndex(BlendShareFbxMetadata metadata, BlendShareObject share)
        {
            var records = metadata?.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>();
            for (int i = records.Length - 1; i >= 0; i--)
            {
                if (IsSamePatch(records[i], share))
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool TryResolveRecord(BlendShareFbxPatchRecord record, out BlendShareObject share)
        {
            share = null;
            if (record == null || string.IsNullOrEmpty(record.blendShareGuid))
            {
                return false;
            }

            string path = AssetDatabase.GUIDToAssetPath(record.blendShareGuid);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path).OfType<BlendShareObject>())
            {
                GetBlendShareIdentity(asset, out _, out string localId);
                if (localId == record.blendShareLocalId)
                {
                    share = asset;
                    return true;
                }
            }

            share = AssetDatabase.LoadAssetAtPath<BlendShareObject>(path);
            return share != null;
        }

        public static bool CanResolveRecords(IEnumerable<BlendShareFbxPatchRecord> records, out string error)
        {
            error = null;
            foreach (var record in records ?? Enumerable.Empty<BlendShareFbxPatchRecord>())
            {
                if (TryResolveRecord(record, out _))
                {
                    continue;
                }

                error = $"Cannot find BlendShare patch asset '{record?.blendShareName}'.";
                return false;
            }

            return true;
        }

        public static bool IsSamePatch(BlendShareFbxPatchRecord record, BlendShareObject share)
        {
            if (record == null || share == null)
            {
                return false;
            }

            GetBlendShareIdentity(share, out string guid, out string localId);
            return record.blendShareGuid == guid && record.blendShareLocalId == localId;
        }

        private static void InitializeTarget(BlendShareFbxMetadata metadata, string path)
        {
            metadata.version = CurrentVersion;
            metadata.targetPath = path ?? string.Empty;
            metadata.targetGuid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            metadata.activeRecords ??= Array.Empty<BlendShareFbxPatchRecord>();
        }

        private static string GetAssetPath(GameObject target)
        {
            return target == null ? string.Empty : AssetDatabase.GetAssetPath(target);
        }

        private static AssetImporter GetImporter(string path)
        {
            return string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path);
        }

        private static string GetBackupFolder(string targetGuid)
        {
            return $"{BackupRoot}/{targetGuid}";
        }

        private static void WriteBackupImporterMetadata(
            string backupPath,
            string sourcePath,
            BlendShareFbxMetadata metadata,
            string sourceHash)
        {
            AssetDatabase.ImportAsset(backupPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            if (importer == null)
            {
                return;
            }

            string createdAtUtc = string.IsNullOrEmpty(importer.CreatedAtUtc)
                ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : importer.CreatedAtUtc;
            var backupMetadata = new BlendShareFbxBackupMetadata
            {
                version = BackupMetadataVersion,
                sourceFbxGuid = metadata.targetGuid,
                sourceFbxPath = sourcePath ?? string.Empty,
                sourceFbxHash = sourceHash ?? string.Empty,
                backupPath = backupPath ?? string.Empty,
                createdAtUtc = createdAtUtc,
                originalFileName = string.IsNullOrEmpty(sourcePath) ? string.Empty : Path.GetFileName(sourcePath),
                blendShareGuids = (metadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                    .Where(record => record != null && !string.IsNullOrEmpty(record.blendShareGuid))
                    .Select(record => record.blendShareGuid)
                    .Distinct()
                    .ToArray()
            };
            importer.SetMetadata(backupMetadata, ResolvePatchObjects(metadata));
            importer.userData = string.Empty;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static BlendShareObject[] ResolvePatchObjects(BlendShareFbxMetadata metadata)
        {
            return (metadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                .Where(record => record != null)
                .Select(record => TryResolveRecord(record, out var share) ? share : null)
                .Where(share => share != null)
                .Distinct()
                .ToArray();
        }

        private static void RefreshBackupImporterMetadata(BlendShareFbxMetadata metadata)
        {
            string backupPath = ResolveBackupPath(metadata);
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            {
                return;
            }

            WriteBackupImporterMetadata(
                backupPath,
                metadata.targetPath,
                metadata,
                string.IsNullOrEmpty(metadata.baselineHash)
                    ? BlendShareHashUtility.Sha256File(backupPath)
                    : metadata.baselineHash);
        }

        private static string ResolveBackupPath(BlendShareFbxMetadata metadata)
        {
            if (!string.IsNullOrEmpty(metadata?.baselineBackupGuid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(metadata.baselineBackupGuid);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    return guidPath;
                }
            }

            return metadata?.baselineBackupPath ?? string.Empty;
        }

        private static void GetBlendShareIdentity(BlendShareObject share, out string guid, out string localId)
        {
            guid = string.Empty;
            localId = string.Empty;
            if (share == null)
            {
                return;
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(share, out string assetGuid, out long assetLocalId))
            {
                guid = assetGuid;
                localId = assetLocalId.ToString(CultureInfo.InvariantCulture);
                return;
            }

            string path = AssetDatabase.GetAssetPath(share);
            guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }

        private static string[] GetFeatureIds(BlendShareObject share)
        {
            return (share?.Meshes ?? Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .SelectMany(mesh => mesh.Features ?? Array.Empty<MeshFeatureObject>())
                .Where(feature => feature != null && !string.IsNullOrEmpty(feature.FeatureId))
                .Select(feature => feature.FeatureId)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    [Serializable]
    public sealed class BlendShareFbxMetadata
    {
        public int version;
        public string targetGuid;
        public string targetPath;
        public string currentFbxHash;
        public string baselineBackupGuid;
        public string baselineBackupPath;
        public string baselineHash;
        public string previousUserData;
        public BlendShareFbxPatchRecord[] activeRecords = Array.Empty<BlendShareFbxPatchRecord>();
    }

    [Serializable]
    public sealed class BlendShareFbxPatchRecord
    {
        public string recordId;
        public string blendShareGuid;
        public string blendShareLocalId;
        public string blendSharePath;
        public string blendShareName;
        public string deformerId;
        public string[] featureIds = Array.Empty<string>();
        public string appliedAtUtc;
        public string hashBefore;
        public string hashAfter;
    }

    [Serializable]
    public sealed class BlendShareFbxBackupMetadata
    {
        public int version;
        public string sourceFbxGuid;
        public string sourceFbxPath;
        public string sourceFbxHash;
        public string backupPath;
        public string createdAtUtc;
        public string originalFileName;
        public string[] blendShareGuids = Array.Empty<string>();
    }

    public readonly struct BlendShareFbxPatchState
    {
        public BlendShareFbxPatchState(
            BlendShareFbxMetadata metadata,
            int patchIndex,
            bool hasPatch,
            bool isLatestPatch,
            bool hasLaterPatches,
            bool canReplayRemainingPatches,
            int activeRecordCount)
        {
            Metadata = metadata;
            PatchIndex = patchIndex;
            HasPatch = hasPatch;
            IsLatestPatch = isLatestPatch;
            HasLaterPatches = hasLaterPatches;
            CanReplayRemainingPatches = canReplayRemainingPatches;
            ActiveRecordCount = activeRecordCount;
        }

        public BlendShareFbxMetadata Metadata { get; }
        public int PatchIndex { get; }
        public bool HasPatch { get; }
        public bool IsLatestPatch { get; }
        public bool HasLaterPatches { get; }
        public bool CanReplayRemainingPatches { get; }
        public int ActiveRecordCount { get; }
        public bool HasBaseline
        {
            get
            {
                if (!string.IsNullOrEmpty(Metadata?.baselineBackupGuid))
                {
                    string path = AssetDatabase.GUIDToAssetPath(Metadata.baselineBackupGuid);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return true;
                    }
                }

                return !string.IsNullOrEmpty(Metadata?.baselineBackupPath) && File.Exists(Metadata.baselineBackupPath);
            }
        }
        public string LatestPatchName => Metadata?.activeRecords != null && Metadata.activeRecords.Length > 0
            ? Metadata.activeRecords[Metadata.activeRecords.Length - 1]?.blendShareName
            : string.Empty;
    }
}
