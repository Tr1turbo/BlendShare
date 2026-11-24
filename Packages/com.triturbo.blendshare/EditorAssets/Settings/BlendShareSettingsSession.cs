using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Triturbo.BlendShare.EditorAssets.Settings
{
    public enum SettingsCommitResultStatus
    {
        NoChanges, Committed, QueuedForEditMode, ReadOnlySessionOnly, ExternalConflict, Failed
    }

    public readonly struct SettingsCommitResult
    {
        public SettingsCommitResultStatus Status { get; }
        public bool Success => Status == SettingsCommitResultStatus.NoChanges || Status == SettingsCommitResultStatus.Committed;
        public string Message { get; }

        public SettingsCommitResult(SettingsCommitResultStatus status, string message = "")
        {
            Status = status;
            Message = message;
        }
    }

    public readonly struct EffectiveSettingsSnapshot
    {
        public BlendShareImportSettings Settings { get; }
        public long SettingsRevision { get; }
        public bool IsDirty { get; }
        public bool IsReadOnly { get; }
        public bool HasExternalConflict { get; }

        public EffectiveSettingsSnapshot(BlendShareImportSettings settings, long settingsRevision, bool isDirty,
            bool isReadOnly, bool hasExternalConflict)
        {
            Settings = settings;
            SettingsRevision = settingsRevision;
            IsDirty = isDirty;
            IsReadOnly = isReadOnly;
            HasExternalConflict = hasExternalConflict;
        }
    }

    [InitializeOnLoad]
    public static class BlendShareSettingsSession
    {
        private static readonly Dictionary<string, BlendShareSettingsUndoBuffer> s_Buffers = new Dictionary<string, BlendShareSettingsUndoBuffer>();
        private static readonly Dictionary<string, long> s_Revisions = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> s_PendingUndoFlushes = new Dictionary<string, long>();
        private static readonly HashSet<string> s_WritingGuids = new HashSet<string>();
        private const string k_QueuedPrefix = "BlendShare.Settings.Queued.";
        private static bool s_ExitingEditMode;
        public static event Action<string, long> SettingsChanged;

        static BlendShareSettingsSession()
        {
            RebuildBufferMapFromLoadedObjects();
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += FlushPendingUndoFlushes;
            AssemblyReloadEvents.beforeAssemblyReload += () => FlushAllDirty();
            EditorApplication.quitting += () => FlushAllDirty();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        internal static void RebuildBufferMapFromLoadedObjects()
        {
            s_Buffers.Clear();
            foreach (var buffer in Resources.FindObjectsOfTypeAll<BlendShareSettingsUndoBuffer>()
                .Where(b => !string.IsNullOrEmpty(b.AssetGuid))
                .OrderByDescending(b => b.IsDirty).ThenByDescending(b => b.DirtyGeneration).ThenBy(b => b.GetInstanceID()))
            {
                if (s_Buffers.ContainsKey(buffer.AssetGuid)) continue;
                buffer.SaveCheckpoint();
                s_Buffers.Add(buffer.AssetGuid, buffer);
            }
        }

        internal static BlendShareSettingsUndoBuffer FindBuffer(string guid) =>
            guid != null && s_Buffers.TryGetValue(guid, out var buffer) && buffer ? buffer : null;

        public static BlendShareSettingsUndoBuffer GetOrCreateBuffer(BlendShareAsset patch) =>
            patch ? GetOrCreateBufferByGuid(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(patch))) : null;

        public static BlendShareSettingsUndoBuffer GetOrCreateBufferByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var buffer = FindBuffer(guid);
            if (buffer) return buffer;
            var importer = GetImporter(guid);
            if (!importer) return null;
            buffer = ScriptableObject.CreateInstance<BlendShareSettingsUndoBuffer>();
            buffer.Initialize(guid, importer);
            s_Buffers[guid] = buffer;
            return buffer;
        }

        private static BlendShareImporter GetImporter(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as BlendShareImporter;
        }

        public static long GetSettingsRevision(string guid) =>
            guid != null && s_Revisions.TryGetValue(guid, out long revision) ? revision : 0;

        private static void Changed(string guid)
        {
            long revision = s_Revisions[guid] = GetSettingsRevision(guid) + 1;
            SettingsChanged?.Invoke(guid, revision);
        }

        private static bool IsImmutable(PackageInfo package) => package != null &&
            (package.source == PackageSource.Registry || package.source == PackageSource.Git ||
             package.source == PackageSource.BuiltIn || package.source == PackageSource.LocalTarball);

        public static bool IsAssetReadOnly(string assetPath, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(assetPath))
            {
                reason = "Missing asset path.";
                return true;
            }
            var package = PackageInfo.FindForAssetPath(assetPath);
            if (IsImmutable(package))
            {
                reason = $"Package {package.name} is read-only ({package.source}).";
                return true;
            }
            string file = BlendSharePathUtility.ResolvePhysicalPath(assetPath);
            foreach (var suffix in new[] { ".meta", string.Empty })
            {
                if (File.Exists(file + suffix) && (File.GetAttributes(file + suffix) & FileAttributes.ReadOnly) != 0 ||
                    !AssetDatabase.IsOpenForEdit(assetPath + suffix))
                {
                    reason = $"{assetPath}{suffix} is read-only.";
                    return true;
                }
            }
            return false;
        }

        public static void NotifyBufferMutated(BlendShareSettingsUndoBuffer buffer)
        {
            if (!buffer) return;
            string hash = buffer.ComputeCurrentHash();
            if (hash == buffer.LastObservedHash) return;
            RecordChange(buffer, hash);
        }

        private static void RecordChange(BlendShareSettingsUndoBuffer buffer, string hash)
        {
            buffer.RecordChange(hash);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                SessionState.SetBool(k_QueuedPrefix + buffer.AssetGuid, true);
            Changed(buffer.AssetGuid);
        }

        private static void OnUndoRedo()
        {
            foreach (var buffer in s_Buffers.Values.ToArray())
            {
                if (!buffer) continue;
                buffer.RestoreCheckpoint();
                string hash = buffer.ComputeCurrentHash();
                if (hash == buffer.LastObservedHash) continue;
                RecordChange(buffer, hash);
                string guid = buffer.AssetGuid;
                s_PendingUndoFlushes[guid] = GetSettingsRevision(guid);
            }
        }

        private static void FlushPendingUndoFlushes()
        {
            if (s_PendingUndoFlushes.Count == 0) return;
            var pending = s_PendingUndoFlushes.ToArray();
            s_PendingUndoFlushes.Clear();
            foreach (var entry in pending)
            {
                if (entry.Value == GetSettingsRevision(entry.Key)) Flush(entry.Key);
            }
        }

        public static SettingsCommitResult Flush(string guid)
        {
            if (!s_ExitingEditMode && (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying))
            {
                SessionState.SetBool(k_QueuedPrefix + guid, true);
                return new SettingsCommitResult(SettingsCommitResultStatus.QueuedForEditMode);
            }
            var buffer = FindBuffer(guid);
            if (!buffer || !buffer.IsDirty || s_WritingGuids.Contains(guid))
                return new SettingsCommitResult(SettingsCommitResultStatus.NoChanges);

            try
            {
                var importer = GetImporter(guid);
                if (!importer) return Fail(buffer, SettingsCommitResultStatus.Failed, $"Missing importer for {guid}.");
                string path = importer.assetPath;
                if (IsAssetReadOnly(path, out string reason))
                {
                    if (!IsImmutable(PackageInfo.FindForAssetPath(path)))
                    {
                        AssetDatabase.MakeEditable(path + ".meta");
                        if (!AssetDatabase.IsOpenForEdit(path)) AssetDatabase.MakeEditable(path);
                    }
                    if (IsAssetReadOnly(path, out reason))
                        return Fail(buffer, SettingsCommitResultStatus.ReadOnlySessionOnly, reason);
                }

                string diskHash = BlendShareSettingsUndoBuffer.Hash(importer.ComputeSettingsJson());
                if (diskHash != buffer.BaselineMetaHash || buffer.HasExternalConflict)
                    return Fail(buffer, SettingsCommitResultStatus.ExternalConflict, $"Settings changed externally for {path}.");

                var sorted = new BlendShareImportSettings();
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(buffer.Settings), sorted);
                Canonicalize(sorted);
                string intended = BlendShareSettingsUndoBuffer.Hash(EditorJsonUtility.ToJson(sorted));
                s_WritingGuids.Add(guid);
                try
                {
                    importer.SetSettingsCopy(sorted);
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                }
                finally
                {
                    s_WritingGuids.Remove(guid);
                }

                importer = GetImporter(guid);
                if (!importer || BlendShareSettingsUndoBuffer.Hash(importer.ComputeSettingsJson()) != intended)
                    return Fail(buffer, SettingsCommitResultStatus.Failed, $"Could not verify saved settings for {path}.");
                Canonicalize(buffer.Settings);
                buffer.MarkCommitted(intended);
            }
            catch (Exception ex)
            {
                return Fail(buffer, SettingsCommitResultStatus.Failed, ex.Message);
            }
            Changed(guid);
            return new SettingsCommitResult(SettingsCommitResultStatus.Committed);
        }

        private static SettingsCommitResult Fail(BlendShareSettingsUndoBuffer buffer, SettingsCommitResultStatus status, string error)
        {
            bool conflict = buffer.HasExternalConflict || status == SettingsCommitResultStatus.ExternalConflict;
            if (buffer.LastCommitError != error || buffer.HasExternalConflict != conflict)
            {
                buffer.SetError(error, conflict);
                Changed(buffer.AssetGuid);
            }
            return new SettingsCommitResult(status, error);
        }

        private static void Canonicalize(BlendShareImportSettings settings)
        {
            settings.Meshes.Sort((a, b) => string.CompareOrdinal(a?.MeshId, b?.MeshId));
            foreach (var mesh in settings.Meshes)
            {
                if (mesh == null) continue;
                mesh.Features.Sort((a, b) => string.CompareOrdinal(a?.FeatureId, b?.FeatureId));
                foreach (var feature in mesh.Features.OfType<BlendShapeFeatureSettings>())
                {
                    feature.DisabledShapeIds.Sort(StringComparer.Ordinal);
                    foreach (var set in feature.SelectionSets) set?.DisabledShapeIds.Sort(StringComparer.Ordinal);
                }
            }
        }

        public static IReadOnlyList<SettingsCommitResult> FlushAllDirty()
        {
            var results = new List<SettingsCommitResult>();
            foreach (var buffer in s_Buffers.Values.ToArray())
                if (buffer && buffer.IsDirty) results.Add(Flush(buffer.AssetGuid));
            return results;
        }

        public static void ReloadFromDiskAndDiscardConflict(string guid)
        {
            var buffer = FindBuffer(guid);
            if (!buffer) return;
            var importer = GetImporter(guid);
            if (!importer) return;
            Undo.ClearUndo(buffer);
            buffer.Reload(importer);
            Changed(guid);
        }

        public static void ForceOverwriteDiskFromBuffer(string guid)
        {
            var buffer = FindBuffer(guid);
            if (!buffer) return;
            var importer = GetImporter(guid);
            if (importer) buffer.AcceptBaseline(BlendShareSettingsUndoBuffer.Hash(importer.ComputeSettingsJson()));
            Flush(guid);
        }

        internal static void OnImported(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            var buffer = FindBuffer(guid);
            if (!buffer || s_WritingGuids.Contains(guid)) return;
            var importer = GetImporter(guid);
            if (!importer || BlendShareSettingsUndoBuffer.Hash(importer.ComputeSettingsJson()) == buffer.BaselineMetaHash) return;
            if (buffer.IsDirty)
                Fail(buffer, SettingsCommitResultStatus.ExternalConflict, $"Settings changed externally for {path}.");
            else
                ReloadFromDiskAndDiscardConflict(guid);
        }

        internal static void OnDeleted(string path)
        {
            // Prune any buffers whose backing asset path no longer exists so deleted patches never block builds.
            foreach (var kv in s_Buffers.ToArray())
            {
                string currentPath = AssetDatabase.GUIDToAssetPath(kv.Key);
                if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                {
                    s_Buffers.Remove(kv.Key);
                    s_Revisions.Remove(kv.Key);
                    SessionState.EraseBool(k_QueuedPrefix + kv.Key);
                    if (kv.Value)
                    {
                        Undo.ClearUndo(kv.Value);
                        UnityEngine.Object.DestroyImmediate(kv.Value);
                    }
                }
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                s_ExitingEditMode = true;
                try { FlushAllDirty(); }
                finally { s_ExitingEditMode = false; }
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                foreach (string guid in s_Buffers.Keys.ToArray())
                {
                    if (!SessionState.GetBool(k_QueuedPrefix + guid, false)) continue;
                    SessionState.EraseBool(k_QueuedPrefix + guid);
                    Flush(guid);
                }
            }
        }
    }

    internal sealed class BlendShareSettingsPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (string path in importedAssets)
                if (path.EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
                    BlendShareSettingsSession.OnImported(path);
            foreach (string path in deletedAssets)
                if (path.EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
                    BlendShareSettingsSession.OnDeleted(path);
        }
    }

    internal sealed class BlendShareSettingsBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;
        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (var result in BlendShareSettingsSession.FlushAllDirty())
                if (!result.Success) throw new BuildFailedException($"BlendShare settings ({result.Status}) {result.Message}");
        }
    }
}
