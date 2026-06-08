using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    public sealed class BlendShareFbxBackupInfo : ScriptableObject
    {
        public int m_MetadataVersion;
        public GameObject m_SourceFbx;
        public GameObject[] m_DerivedFbxs;
        public string m_SourceFbxHash;
        public string m_CreatedAtUtc;
        public string m_OriginalFileName;
        public string m_BackupPath;
        public string m_BackupHash;
        public long m_BackupSizeBytes;
    }
}
