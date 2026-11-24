using System;
using UnityEngine;

namespace Triturbo.BlendShare.EditorAssets.Persistence
{
    /// <summary>
    /// Imported ScriptableObject descriptor for a <c>.blendsharebackup</c> FBX baseline backup file.
    /// </summary>
    public sealed class BlendShareFbxBackupInfo : ScriptableObject
    {
        [SerializeField] private int m_MetadataVersion;
        [SerializeField] private GameObject m_SourceFbx;
        [SerializeField] private GameObject[] m_DerivedFbxs = Array.Empty<GameObject>();
        [SerializeField] private string m_SourceFbxHash = string.Empty;
        [SerializeField] private string m_CreatedAtUtc = string.Empty;
        [SerializeField] private string m_OriginalFileName = string.Empty;
        [SerializeField] private string m_BackupPath = string.Empty;
        [SerializeField] private string m_BackupHash = string.Empty;
        [SerializeField] private long m_BackupSizeBytes;

        public int MetadataVersion
        {
            get => m_MetadataVersion;
            set => m_MetadataVersion = value;
        }

        public GameObject SourceFbx
        {
            get => m_SourceFbx;
            set => m_SourceFbx = value;
        }

        public GameObject[] DerivedFbxs
        {
            get => m_DerivedFbxs ?? Array.Empty<GameObject>();
            set => m_DerivedFbxs = value ?? Array.Empty<GameObject>();
        }

        public string SourceFbxHash
        {
            get => m_SourceFbxHash ?? string.Empty;
            set => m_SourceFbxHash = value ?? string.Empty;
        }

        public string CreatedAtUtc
        {
            get => m_CreatedAtUtc ?? string.Empty;
            set => m_CreatedAtUtc = value ?? string.Empty;
        }

        public string OriginalFileName
        {
            get => m_OriginalFileName ?? string.Empty;
            set => m_OriginalFileName = value ?? string.Empty;
        }

        public string BackupPath
        {
            get => m_BackupPath ?? string.Empty;
            set => m_BackupPath = value ?? string.Empty;
        }

        public string BackupHash
        {
            get => m_BackupHash ?? string.Empty;
            set => m_BackupHash = value ?? string.Empty;
        }

        public long BackupSizeBytes
        {
            get => m_BackupSizeBytes;
            set => m_BackupSizeBytes = value;
        }
    }

    [Serializable]
    public sealed class BlendShareFbxBackupMetadata
    {
        public int Version;
        public string SourceFbxHash = string.Empty;
        public string BackupPath = string.Empty;
        public string CreatedAtUtc = string.Empty;
        public string OriginalFileName = string.Empty;
        public GameObject SourceFbx;
        public GameObject[] DerivedFbxs = Array.Empty<GameObject>();
    }
}
