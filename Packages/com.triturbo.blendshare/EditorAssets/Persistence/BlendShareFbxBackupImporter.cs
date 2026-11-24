using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Triturbo.BlendShare.EditorAssets.Persistence
{
    /// <summary>
    /// Importer dedicated exclusively to <c>.blendsharebackup</c> files (never claiming generic <c>.bak</c>).
    /// </summary>
    [ScriptedImporter(1, "blendsharebackup")]
    public sealed class BlendShareFbxBackupImporter : ScriptedImporter
    {
        [HideInInspector, SerializeField] private int m_Version;
        [HideInInspector, SerializeField] private GameObject m_SourceFbx;
        [HideInInspector, SerializeField] private GameObject[] m_DerivedFbxs = Array.Empty<GameObject>();
        [HideInInspector, SerializeField] private string m_SourceFbxHash = string.Empty;
        [HideInInspector, SerializeField] private string m_BackupPath = string.Empty;
        [HideInInspector, SerializeField] private string m_CreatedAtUtc = string.Empty;
        [HideInInspector, SerializeField] private string m_OriginalFileName = string.Empty;

        public string CreatedAtUtc => m_CreatedAtUtc ?? string.Empty;
        public GameObject SourceFbx => m_SourceFbx;
        public string SourceFbxHash => m_SourceFbxHash ?? string.Empty;
        public string OriginalFileName => m_OriginalFileName ?? string.Empty;
        public string BackupPath => m_BackupPath ?? string.Empty;
        public GameObject[] DerivedFbxs => m_DerivedFbxs ?? Array.Empty<GameObject>();

        public void SetMetadata(BlendShareFbxBackupMetadata metadata)
        {
            m_Version = metadata?.Version ?? 0;
            m_SourceFbxHash = metadata?.SourceFbxHash ?? string.Empty;
            m_BackupPath = metadata?.BackupPath ?? string.Empty;
            m_CreatedAtUtc = metadata?.CreatedAtUtc ?? string.Empty;
            m_OriginalFileName = metadata?.OriginalFileName ?? string.Empty;
            m_SourceFbx = metadata?.SourceFbx;
            m_DerivedFbxs = metadata?.DerivedFbxs ?? Array.Empty<GameObject>();
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var info = ScriptableObject.CreateInstance<BlendShareFbxBackupInfo>();
            info.name = Path.GetFileName(ctx.assetPath);
            info.MetadataVersion = m_Version;
            info.SourceFbx = m_SourceFbx;
            info.DerivedFbxs = m_DerivedFbxs ?? Array.Empty<GameObject>();
            info.SourceFbxHash = m_SourceFbxHash;
            info.CreatedAtUtc = m_CreatedAtUtc;
            info.OriginalFileName = m_OriginalFileName;
            info.BackupPath = string.IsNullOrEmpty(m_BackupPath) ? ctx.assetPath : m_BackupPath;

            string physical = BlendSharePathUtility.ResolvePhysicalPath(ctx.assetPath);
            if (!string.IsNullOrEmpty(physical) && File.Exists(physical))
            {
                info.BackupHash = ComputeSha256File(physical);
                info.BackupSizeBytes = new FileInfo(physical).Length;
            }

            ctx.AddObjectToAsset("Backup Info", info);
            ctx.SetMainObject(info);
        }

        public static string ComputeSha256File(string physicalPath)
        {
            if (string.IsNullOrEmpty(physicalPath) || !File.Exists(physicalPath))
            {
                return string.Empty;
            }

            try
            {
                using var sha = SHA256.Create();
                using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
