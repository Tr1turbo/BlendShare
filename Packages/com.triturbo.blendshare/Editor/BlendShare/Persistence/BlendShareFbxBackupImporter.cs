using System;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Hashing;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    [ScriptedImporter(1, "blendsharebackup")]
    public sealed class BlendShareFbxBackupImporter : ScriptedImporter
    {
        [HideInInspector]
        [SerializeField]
        private int version;

        [HideInInspector]
        [SerializeField]
        private GameObject sourceFbx;

        [HideInInspector]
        [SerializeField]
        private BlendShareObject[] blendSharePatches = Array.Empty<BlendShareObject>();

        [HideInInspector]
        [SerializeField]
        private string sourceFbxGuid;

        [HideInInspector]
        [SerializeField]
        private string sourceFbxPath;

        [HideInInspector]
        [SerializeField]
        private string sourceFbxHash;

        [HideInInspector]
        [SerializeField]
        private string backupPath;

        [HideInInspector]
        [SerializeField]
        private string createdAtUtc;

        [HideInInspector]
        [SerializeField]
        private string originalFileName;

        [HideInInspector]
        [SerializeField]
        private string[] blendShareGuids = Array.Empty<string>();

        internal void SetMetadata(BlendShareFbxBackupMetadata metadata, BlendShareObject[] patches)
        {
            version = metadata?.version ?? 0;
            sourceFbxGuid = metadata?.sourceFbxGuid ?? string.Empty;
            sourceFbxPath = metadata?.sourceFbxPath ?? string.Empty;
            sourceFbxHash = metadata?.sourceFbxHash ?? string.Empty;
            backupPath = metadata?.backupPath ?? string.Empty;
            createdAtUtc = metadata?.createdAtUtc ?? string.Empty;
            originalFileName = metadata?.originalFileName ?? string.Empty;
            blendShareGuids = metadata?.blendShareGuids ?? Array.Empty<string>();
            sourceFbx = ResolveAsset<GameObject>(sourceFbxGuid);
            blendSharePatches = patches ?? Array.Empty<BlendShareObject>();
        }

        internal string CreatedAtUtc => createdAtUtc;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var info = ScriptableObject.CreateInstance<BlendShareFbxBackupInfo>();
            info.name = Path.GetFileName(ctx.assetPath);
            info.m_MetadataVersion = version;
            info.m_SourceFbx = sourceFbx;
            info.m_BlendSharePatches = blendSharePatches ?? Array.Empty<BlendShareObject>();
            info.m_SourceFbxGuid = sourceFbxGuid;
            info.m_SourceFbxPath = sourceFbxPath;
            info.m_SourceFbxHash = sourceFbxHash;
            info.m_BlendShareGuids = blendShareGuids ?? Array.Empty<string>();
            info.m_CreatedAtUtc = createdAtUtc;
            info.m_OriginalFileName = originalFileName;
            info.m_BackupPath = string.IsNullOrEmpty(backupPath) ? ctx.assetPath : backupPath;
            info.m_BackupHash = BlendShareHashUtility.Sha256File(ctx.assetPath);
            info.m_BackupSizeBytes = new FileInfo(ctx.assetPath).Length;

            if (version <= 0)
            {
                ImportLegacyUserDataMetadata(info);
            }

            ctx.AddObjectToAsset("Backup Info", info);
            ctx.SetMainObject(info);
            
        }

        private void ImportLegacyUserDataMetadata(BlendShareFbxBackupInfo info)
        {
            string metadataText = userData ?? string.Empty;
            if (!metadataText.StartsWith(BlendShareFbxMetadataService.BackupMetadataPrefix, StringComparison.Ordinal))
            {
                info.m_ImportMessage = "BlendShare backup metadata is missing.";
                return;
            }

            try
            {
                var metadata = JsonUtility.FromJson<BlendShareFbxBackupMetadata>(
                    metadataText.Substring(BlendShareFbxMetadataService.BackupMetadataPrefix.Length));
                info.m_MetadataVersion = metadata.version;
                info.m_SourceFbxGuid = metadata.sourceFbxGuid;
                info.m_SourceFbxPath = metadata.sourceFbxPath;
                info.m_SourceFbxHash = metadata.sourceFbxHash;
                info.m_BlendShareGuids = metadata.blendShareGuids ?? Array.Empty<string>();
                info.m_CreatedAtUtc = metadata.createdAtUtc;
                info.m_OriginalFileName = metadata.originalFileName;
                info.m_SourceFbx = ResolveAsset<GameObject>(metadata.sourceFbxGuid);
                info.m_BlendSharePatches = (metadata.blendShareGuids ?? Array.Empty<string>())
                    .Select(ResolveAsset<BlendShareObject>)
                    .Where(asset => asset != null)
                    .Distinct()
                    .ToArray();
            }
            catch (Exception exception)
            {
                info.m_ImportMessage = $"Could not read BlendShare backup metadata: {exception.Message}";
            }
        }

        private static T ResolveAsset<T>(string guid)
            where T : UnityEngine.Object
        {
            string path = string.IsNullOrEmpty(guid) ? string.Empty : AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }
    }
}
