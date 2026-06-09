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

        internal static bool HasBlendShareMetadata(string userData)
        {
            return !string.IsNullOrEmpty(userData) && userData.StartsWith(MetadataPrefix, StringComparison.Ordinal);
        }

        internal static bool UsesBaselineBackup(GameObject target, string backupPath)
        {
            string targetPath = GetAssetPath(target);
            var importer = GetImporter(targetPath);
            string userData = importer?.userData ?? string.Empty;
            if (!HasBlendShareMetadata(userData))
            {
                return false;
            }

            try
            {
                var metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(userData.Substring(MetadataPrefix.Length));
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
            string path = GetAssetPath(target);
            var importer = GetImporter(path);
            string userData = importer?.userData ?? string.Empty;

            BlendShareFbxMetadata metadata = null;
            if (HasBlendShareMetadata(userData))
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
                previousUserData = string.IsNullOrEmpty(userData) || HasBlendShareMetadata(userData)
                    ? string.Empty
                    : userData
            };

            InitializeTarget(metadata, path);
            return metadata;
        }

        public static BlendShareFbxPatchState GetPatchState(GameObject target, BlendShareObject patch)
        {
            var metadata = Load(target);
            int patchIndex = FindLatestPatchIndex(metadata, patch);
            int exactPatchIndex = FindLatestPatchAssetIndex(metadata, patch);
            int recordCount = metadata.activeRecords?.Length ?? 0;
            bool hasPatch = patchIndex >= 0;
            bool hasExactPatch = exactPatchIndex >= 0;
            bool hasLater = hasPatch && patchIndex < recordCount - 1;
            bool canRevertPatch = hasExactPatch &&
                                  TryResolveRecord(metadata.activeRecords[exactPatchIndex], out _) &&
                                  CanResolveRecords(
                                      metadata.activeRecords.Where((_, index) => index != exactPatchIndex),
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
                metadata.baselineHash = BlendShareHashUtility.Sha256File(existingBackupPath);
                RefreshBackupImporterMetadata(metadata);
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
            RefreshBackupImporterMetadata(metadata);
            return true;
        }

        public static bool InitializeGeneratedOutput(
            GameObject source,
            string outputPath,
            IEnumerable<BlendShareObject> patches,
            out string error)
        {
            error = null;
            string sourcePath = GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(outputPath))
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

            var importer = GetImporter(outputPath);
            if (importer == null)
            {
                AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                importer = GetImporter(outputPath);
            }

            if (importer == null)
            {
                error = $"Could not load importer for generated FBX '{outputPath}'.";
                return false;
            }

            string sourceHash = BlendShareHashUtility.Sha256File(sourcePath);
            string outputHash = BlendShareHashUtility.Sha256File(outputPath);
            if (string.IsNullOrEmpty(sourceHash) || string.IsNullOrEmpty(outputHash))
            {
                error = $"Could not hash source or generated FBX for '{outputPath}'.";
                return false;
            }

            var metadata = new BlendShareFbxMetadata
            {
                previousUserData = string.IsNullOrEmpty(importer.userData) || HasBlendShareMetadata(importer.userData)
                    ? string.Empty
                    : importer.userData,
                baselineBackupGuid = sourceMetadata.baselineBackupGuid,
                baselineBackupPath = ResolveBackupPath(sourceMetadata),
                baselineHash = string.IsNullOrEmpty(sourceMetadata.baselineHash)
                    ? BlendShareHashUtility.Sha256File(ResolveBackupPath(sourceMetadata))
                    : sourceMetadata.baselineHash,
                currentFbxHash = outputHash,
                activeRecords = (sourceMetadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                    .Where(record => record != null)
                    .ToArray()
            };
            InitializeTarget(metadata, outputPath);

            var records = metadata.activeRecords.ToList();
            foreach (var patch in patches ?? Enumerable.Empty<BlendShareObject>())
            {
                if (patch == null)
                {
                    continue;
                }

                records.Add(CreateRecord(source, patch, sourceHash, outputHash));
            }

            metadata.activeRecords = records.ToArray();
            importer.userData = MetadataPrefix + JsonUtility.ToJson(metadata, false);
            importer.SaveAndReimport();
            RefreshBackupImporterMetadata(metadata);
            return true;
        }

        public static BlendShareFbxPatchRecord CreateRecord(
            GameObject target,
            BlendShareObject patch,
            string hashBefore,
            string hashAfter)
        {
            GetPatchIdentity(patch, out string guid, out string localId);
            return new BlendShareFbxPatchRecord
            {
                recordId = Guid.NewGuid().ToString("N"),
                blendShareGuid = guid,
                blendShareLocalId = localId,
                blendSharePath = AssetDatabase.GetAssetPath(patch),
                blendShareName = patch != null ? patch.name : string.Empty,
                patchId = patch != null ? patch.m_PatchId : string.Empty,
                featureIds = GetFeatureIds(patch),
                appliedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                hashBefore = hashBefore ?? string.Empty,
                hashAfter = hashAfter ?? string.Empty
            };
        }

        public static BlendShareFbxPatchRecord CopyRecord(
            BlendShareFbxPatchRecord record,
            string hashBefore = null,
            string hashAfter = null)
        {
            if (record == null)
            {
                return null;
            }

            return new BlendShareFbxPatchRecord
            {
                recordId = record.recordId,
                blendShareGuid = record.blendShareGuid,
                blendShareLocalId = record.blendShareLocalId,
                blendSharePath = record.blendSharePath,
                blendShareName = record.blendShareName,
                patchId = record.patchId,
                featureIds = (record.featureIds ?? Array.Empty<string>()).ToArray(),
                appliedAtUtc = record.appliedAtUtc,
                hashBefore = hashBefore ?? record.hashBefore,
                hashAfter = hashAfter ?? record.hashAfter
            };
        }

        public static void CommitApplyRecord(BlendShareFbxMetadata metadata, BlendShareObject patch, BlendShareFbxPatchRecord record)
        {
            var records = (metadata.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>())
                .Where(existing => existing != null)
                .ToList();

            if (record != null)
            {
                records.Add(record);
                metadata.currentFbxHash = record.hashAfter;
            }

            metadata.activeRecords = records.ToArray();
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

            BlendShareFbxMetadata clearMetadata = metadata ?? Load(target);
            string backupPath = ResolveBackupPath(clearMetadata);

            importer.userData = clearMetadata?.previousUserData ?? string.Empty;
            importer.SaveAndReimport();

            if (!string.IsNullOrEmpty(backupPath))
            {
                UnregisterDerivedFbx(backupPath, path);
            }

            PruneBackupIfUnused(backupPath);
            return true;
        }

        public static bool ClearBlendShareMetadataAtPath(string path)
        {
            var importer = GetImporter(path);
            if (importer == null || string.IsNullOrEmpty(importer.userData) ||
                !HasBlendShareMetadata(importer.userData))
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

            string backupPath = ResolveBackupPath(metadata);
            importer.userData = metadata?.previousUserData ?? string.Empty;
            importer.SaveAndReimport();
            if (!string.IsNullOrEmpty(backupPath))
            {
                UnregisterDerivedFbx(backupPath, path);
            }

            PruneBackupIfUnused(backupPath);
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
            return PruneBackupIfUnused(ResolveBackupPath(metadata));
        }

        private static bool PruneBackupIfUnused(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath))
            {
                return true;
            }

            string folder = Path.GetDirectoryName(backupPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith(BackupRoot, StringComparison.Ordinal))
            {
                return false;
            }

            if (HasRegisteredDerivedFbxs(backupPath))
            {
                return true;
            }

            bool deleted = !File.Exists(backupPath) || AssetDatabase.DeleteAsset(backupPath);
            if (!deleted)
            {
                return false;
            }

            if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
            {
                AssetDatabase.DeleteAsset(folder);
            }

            return true;
        }

        public static int FindLatestPatchIndex(BlendShareFbxMetadata metadata, BlendShareObject patch)
        {
            var records = metadata?.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>();
            for (int i = records.Length - 1; i >= 0; i--)
            {
                if (IsSamePatch(records[i], patch))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int FindLatestPatchAssetIndex(BlendShareFbxMetadata metadata, BlendShareObject patch)
        {
            var records = metadata?.activeRecords ?? Array.Empty<BlendShareFbxPatchRecord>();
            for (int i = records.Length - 1; i >= 0; i--)
            {
                if (IsSamePatchAsset(records[i], patch))
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool TryResolveRecord(BlendShareFbxPatchRecord record, out BlendShareObject patch)
        {
            patch = null;
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
                GetPatchIdentity(asset, out _, out string localId);
                if (localId == record.blendShareLocalId)
                {
                    patch = asset;
                    return true;
                }
            }

            patch = AssetDatabase.LoadAssetAtPath<BlendShareObject>(path);
            return patch != null;
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

        public static bool IsSamePatch(BlendShareFbxPatchRecord record, BlendShareObject patch)
        {
            if (record == null || patch == null)
            {
                return false;
            }

            return string.Equals(record.patchId ?? string.Empty, patch.m_PatchId ?? string.Empty, StringComparison.Ordinal);
        }

        public static bool IsSamePatchAsset(BlendShareFbxPatchRecord record, BlendShareObject patch)
        {
            if (record == null || patch == null)
            {
                return false;
            }

            GetPatchIdentity(patch, out string guid, out string localId);
            if (!string.IsNullOrEmpty(record.blendShareGuid) && !string.IsNullOrEmpty(guid))
            {
                if (!string.Equals(record.blendShareGuid, guid, StringComparison.Ordinal))
                {
                    return false;
                }

                return string.IsNullOrEmpty(record.blendShareLocalId) ||
                       string.IsNullOrEmpty(localId) ||
                       string.Equals(record.blendShareLocalId, localId, StringComparison.Ordinal);
            }

            string path = AssetDatabase.GetAssetPath(patch);
            return !string.IsNullOrEmpty(record.blendSharePath) &&
                   !string.IsNullOrEmpty(path) &&
                   string.Equals(record.blendSharePath, path, StringComparison.Ordinal);
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
            var importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            if (importer == null)
            {
                AssetDatabase.ImportAsset(backupPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            }

            if (importer == null)
            {
                return;
            }

            string createdAtUtc = string.IsNullOrEmpty(importer.CreatedAtUtc)
                ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : importer.CreatedAtUtc;
            string importerSourcePath = GetAssetPath(importer.SourceFbx);
            string metadataSourcePath = string.IsNullOrEmpty(importerSourcePath) ? sourcePath ?? string.Empty : importerSourcePath;
            string metadataSourceHash = string.IsNullOrEmpty(importer.SourceFbxHash) ? sourceHash ?? string.Empty : importer.SourceFbxHash;
            var derivedReferences = GetDerivedFbxReferences(importer)
                .Where(reference => IsLiveDerivedFbx(reference.Path, backupPath))
                .ToList();
            if (metadata.activeRecords != null && metadata.activeRecords.Length > 0)
            {
                AddDerivedFbxReference(derivedReferences, metadata.targetGuid, metadata.targetPath);
            }

            var backupMetadata = new BlendShareFbxBackupMetadata
            {
                version = BackupMetadataVersion,
                sourceFbx = LoadFbx(metadataSourcePath),
                sourceFbxHash = metadataSourceHash,
                backupPath = backupPath ?? string.Empty,
                createdAtUtc = createdAtUtc,
                originalFileName = string.IsNullOrEmpty(importer.OriginalFileName)
                    ? string.IsNullOrEmpty(metadataSourcePath) ? string.Empty : Path.GetFileName(metadataSourcePath)
                    : importer.OriginalFileName,
                derivedFbxs = derivedReferences
                    .Select(reference => LoadFbx(reference.Path))
                    .Where(fbx => fbx != null)
                    .ToArray()
            };
            importer.SetMetadata(backupMetadata);
            importer.userData = string.Empty;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static bool HasRegisteredDerivedFbxs(string backupPath)
        {
            var importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            if (importer == null)
            {
                return false;
            }

            var allReferences = GetDerivedFbxReferences(importer);
            var liveReferences = allReferences
                .Where(reference => IsLiveDerivedFbx(reference.Path, backupPath))
                .ToList();
            if (liveReferences.Count != allReferences.Count)
            {
                RewriteBackupDerivedReferences(backupPath, liveReferences);
            }

            return liveReferences.Count > 0;
        }

        private static void UnregisterDerivedFbx(string backupPath, string targetPath)
        {
            var importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            if (importer == null)
            {
                return;
            }

            string normalizedTargetPath = NormalizeAssetPath(targetPath);
            var references = GetDerivedFbxReferences(importer)
                .Where(reference => !string.Equals(reference.Path, normalizedTargetPath, StringComparison.Ordinal))
                .Where(reference => IsLiveDerivedFbx(reference.Path, backupPath))
                .ToList();
            RewriteBackupDerivedReferences(backupPath, references);
        }

        private static void RewriteBackupDerivedReferences(
            string backupPath,
            IReadOnlyList<DerivedFbxReference> derivedReferences)
        {
            var importer = AssetImporter.GetAtPath(backupPath) as BlendShareFbxBackupImporter;
            if (importer == null)
            {
                return;
            }

            string sourcePath = GetAssetPath(importer.SourceFbx);
            var backupMetadata = new BlendShareFbxBackupMetadata
            {
                version = BackupMetadataVersion,
                sourceFbx = LoadFbx(sourcePath),
                sourceFbxHash = importer.SourceFbxHash,
                backupPath = string.IsNullOrEmpty(importer.BackupPath) ? backupPath : importer.BackupPath,
                createdAtUtc = importer.CreatedAtUtc,
                originalFileName = importer.OriginalFileName,
                derivedFbxs = (derivedReferences ?? Array.Empty<DerivedFbxReference>())
                    .Select(reference => LoadFbx(reference.Path))
                    .Where(fbx => fbx != null)
                    .ToArray()
            };
            importer.SetMetadata(backupMetadata);
            importer.userData = string.Empty;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static List<DerivedFbxReference> GetDerivedFbxReferences(BlendShareFbxBackupImporter importer)
        {
            var references = new List<DerivedFbxReference>();
            foreach (var fbx in importer?.DerivedFbxs ?? Array.Empty<GameObject>())
            {
                string path = GetAssetPath(fbx);
                AddDerivedFbxReference(references, string.Empty, path);
            }

            return references;
        }

        private static void AddDerivedFbxReference(
            List<DerivedFbxReference> references,
            string guid,
            string path)
        {
            string guidPath = string.IsNullOrEmpty(guid) ? string.Empty : AssetDatabase.GUIDToAssetPath(guid);
            path = NormalizeAssetPath(string.IsNullOrEmpty(guidPath) ? path : guidPath);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            guid = AssetDatabase.AssetPathToGUID(path);
            if (references.Any(reference =>
                    string.Equals(reference.Guid, guid, StringComparison.Ordinal) ||
                    string.Equals(reference.Path, path, StringComparison.Ordinal)))
            {
                return;
            }

            references.Add(new DerivedFbxReference(guid, path));
        }

        private static bool IsLiveDerivedFbx(string path, string backupPath)
        {
            path = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            var importer = GetImporter(path);
            string userData = importer?.userData ?? string.Empty;
            if (!HasBlendShareMetadata(userData))
            {
                return false;
            }

            try
            {
                var metadata = JsonUtility.FromJson<BlendShareFbxMetadata>(userData.Substring(MetadataPrefix.Length));
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

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        private static GameObject LoadFbx(string path)
        {
            path = NormalizeAssetPath(path);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
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

        private static void GetPatchIdentity(BlendShareObject patch, out string guid, out string localId)
        {
            guid = string.Empty;
            localId = string.Empty;
            if (patch == null)
            {
                return;
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(patch, out string assetGuid, out long assetLocalId))
            {
                guid = assetGuid;
                localId = assetLocalId.ToString(CultureInfo.InvariantCulture);
                return;
            }

            string path = AssetDatabase.GetAssetPath(patch);
            guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }

        private static string[] GetFeatureIds(BlendShareObject patch)
        {
            return (patch?.Meshes ?? Array.Empty<MeshDataObject>())
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
        public string patchId;
        public string[] featureIds = Array.Empty<string>();
        public string appliedAtUtc;
        public string hashBefore;
        public string hashAfter;
    }

    [Serializable]
    public sealed class BlendShareFbxBackupMetadata
    {
        public int version;
        public GameObject sourceFbx;
        public string sourceFbxHash;
        public string backupPath;
        public string createdAtUtc;
        public string originalFileName;
        public GameObject[] derivedFbxs = Array.Empty<GameObject>();
    }

    internal readonly struct DerivedFbxReference
    {
        public DerivedFbxReference(string guid, string path)
        {
            Guid = guid ?? string.Empty;
            Path = path ?? string.Empty;
        }

        public string Guid { get; }
        public string Path { get; }
    }

    public readonly struct BlendShareFbxPatchState
    {
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

        public BlendShareFbxMetadata Metadata { get; }
        public int PatchIndex { get; }
        public bool HasPatch { get; }
        public bool IsLatestPatch { get; }
        public bool HasLaterPatches { get; }
        public bool HasExactPatch { get; }
        public bool CanRevertPatch { get; }
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
