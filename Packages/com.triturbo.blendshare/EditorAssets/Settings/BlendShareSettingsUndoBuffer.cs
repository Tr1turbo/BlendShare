using System;
using System.Security.Cryptography;
using System.Text;
using Triturbo.BlendShare.Model.Settings;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.EditorAssets.Settings
{
    public sealed class BlendShareSettingsUndoBuffer : ScriptableObject
    {
        [SerializeField] private string m_AssetGuid;
        [SerializeField] private string m_BaselineMetaHash;
        [SerializeField] private string m_LastObservedHash;
        [SerializeField] private long m_DirtyGeneration;
        [SerializeField] private long m_CommittedGeneration;
        [SerializeField] private bool m_HasExternalConflict;
        [SerializeField] private string m_LastCommitError;
        [SerializeField] private BlendShareImportSettings m_Settings;

        // Complete-object Undo rewinds serialized bookkeeping along with the user's settings.
        // Keep the latest observed state outside Undo, reseeding it on domain reload.
        [NonSerialized] private (string baseline, string observed, long dirty, long committed,
            bool conflict, string error) m_Checkpoint;

        public string AssetGuid => m_AssetGuid;
        public string BaselineMetaHash => m_BaselineMetaHash;
        public long DirtyGeneration => m_DirtyGeneration;
        public long CommittedGeneration => m_CommittedGeneration;
        public bool IsDirty => m_DirtyGeneration != m_CommittedGeneration;
        public bool HasExternalConflict => m_HasExternalConflict;
        public string LastCommitError => m_LastCommitError;
        public BlendShareImportSettings Settings => m_Settings;
        internal string LastObservedHash => m_LastObservedHash;

        public string ComputeCurrentHash() => Hash(EditorJsonUtility.ToJson(m_Settings));

        internal static string Hash(string json)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant();
        }

        internal void Initialize(string guid, BlendShareImporter importer)
        {
            hideFlags = HideFlags.HideAndDontSave;
            m_AssetGuid = guid;
            Reload(importer);
        }

        internal void Reload(BlendShareImporter importer)
        {
            m_Settings = importer.GetSettingsCopy();
            MarkCommitted(Hash(importer.ComputeSettingsJson()));
        }

        internal void MarkCommitted(string baseline)
        {
            m_BaselineMetaHash = baseline;
            m_LastObservedHash = ComputeCurrentHash();
            m_CommittedGeneration = m_DirtyGeneration;
            SetError(string.Empty, false);
        }

        internal void RecordChange(string hash)
        {
            m_LastObservedHash = hash;
            ++m_DirtyGeneration;
            if (!m_HasExternalConflict && hash == m_BaselineMetaHash)
                m_CommittedGeneration = m_DirtyGeneration;
            SaveCheckpoint();
        }

        internal void AcceptBaseline(string hash)
        {
            m_BaselineMetaHash = hash;
            SetError(string.Empty, false);
        }

        internal void SetError(string message, bool conflict)
        {
            m_LastCommitError = message;
            m_HasExternalConflict = conflict;
            SaveCheckpoint();
        }

        internal void SaveCheckpoint()
        {
            m_Checkpoint = (m_BaselineMetaHash, m_LastObservedHash, m_DirtyGeneration, m_CommittedGeneration,
                m_HasExternalConflict, m_LastCommitError);
        }

        internal void RestoreCheckpoint()
        {
            if (m_Checkpoint == (m_BaselineMetaHash, m_LastObservedHash, m_DirtyGeneration, m_CommittedGeneration,
                m_HasExternalConflict, m_LastCommitError)) return;
            (m_BaselineMetaHash, m_LastObservedHash, m_DirtyGeneration, m_CommittedGeneration,
                m_HasExternalConflict, m_LastCommitError) = m_Checkpoint;
        }
    }
}
