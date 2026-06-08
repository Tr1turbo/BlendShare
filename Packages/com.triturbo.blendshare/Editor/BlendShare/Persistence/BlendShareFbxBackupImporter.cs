using System.IO;
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
        private GameObject[] derivedFbxs = System.Array.Empty<GameObject>();

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

        internal void SetMetadata(BlendShareFbxBackupMetadata metadata)
        {
            version = metadata?.version ?? 0;
            sourceFbxHash = metadata?.sourceFbxHash ?? string.Empty;
            backupPath = metadata?.backupPath ?? string.Empty;
            createdAtUtc = metadata?.createdAtUtc ?? string.Empty;
            originalFileName = metadata?.originalFileName ?? string.Empty;
            sourceFbx = metadata?.sourceFbx;
            derivedFbxs = metadata?.derivedFbxs ?? System.Array.Empty<GameObject>();
        }

        internal string CreatedAtUtc => createdAtUtc;
        internal GameObject SourceFbx => sourceFbx;
        internal string SourceFbxHash => sourceFbxHash;
        internal string OriginalFileName => originalFileName;
        internal string BackupPath => backupPath;
        internal GameObject[] DerivedFbxs => derivedFbxs ?? System.Array.Empty<GameObject>();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var info = ScriptableObject.CreateInstance<BlendShareFbxBackupInfo>();
            info.name = Path.GetFileName(ctx.assetPath);
            info.m_MetadataVersion = version;
            info.m_SourceFbx = sourceFbx;
            info.m_DerivedFbxs = derivedFbxs ?? System.Array.Empty<GameObject>();
            info.m_SourceFbxHash = sourceFbxHash;
            info.m_CreatedAtUtc = createdAtUtc;
            info.m_OriginalFileName = originalFileName;
            info.m_BackupPath = string.IsNullOrEmpty(backupPath) ? ctx.assetPath : backupPath;
            info.m_BackupHash = BlendShareHashUtility.Sha256File(ctx.assetPath);
            info.m_BackupSizeBytes = new FileInfo(ctx.assetPath).Length;

            ctx.AddObjectToAsset("Backup Info", info);
            ctx.SetMainObject(info);
        }

    }
}
