using Triturbo.BlendShare.Core;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    public sealed class BlendShareFbxBackupInfo : ScriptableObject
    {
        public int m_MetadataVersion;
        public GameObject m_SourceFbx;
        public BlendShareObject[] m_BlendSharePatches;
        public string m_SourceFbxGuid;
        public string m_SourceFbxPath;
        public string m_SourceFbxHash;
        public string[] m_BlendShareGuids;
        public string m_CreatedAtUtc;
        public string m_OriginalFileName;
        public string m_BackupPath;
        public string m_BackupHash;
        public long m_BackupSizeBytes;
        public string m_ImportMessage;
    }
}
