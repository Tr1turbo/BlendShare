using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;

namespace Triturbo.BlendShare.EditorAssets
{
    /// <summary>
    /// Accelerator-safe Editor repository bridging BlendShareAsset (GUID/TOC) to Container.Editor streams and PayloadStore.
    /// - Tracks multi-owner (GUID, Revision) sets so byte-identical duplicate patches never clobber each other.
    /// - Caches physical filesystem paths (via PackageInfo.resolvedPath) on the Editor thread so worker-thread OpenRead
    ///   never calls AssetDatabase and never deadlocks across repository/store lock order.
    /// - Preserves warm PayloadStore revisions across same-content (.meta) reimports and moves.
    /// - Fail-Closed on SourceRevisionMismatch with GUID-deduplicated delayed reimport scheduling.
    /// </summary>
    public sealed class BlendShareSourceRepository : IContainerStreamProvider, IDisposable
    {
        public static readonly BlendShareSourceRepository Shared = new BlendShareSourceRepository();

        private sealed class RevisionGroup
        {
            public readonly HashSet<string> OwnerGuids = new HashSet<string>(StringComparer.Ordinal);
            public ContainerToc Toc;
        }

        private sealed class GuidRecord
        {
            public string Revision;
            public string PhysicalPath;
        }

        private readonly object m_Lock = new object();
        private readonly Dictionary<string, RevisionGroup> m_ByRevision =
            new Dictionary<string, RevisionGroup>(StringComparer.Ordinal);
        private readonly Dictionary<string, GuidRecord> m_ByGuid =
            new Dictionary<string, GuidRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> m_PendingReimportGuids =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly BlendShareContainerReader m_Reader = new BlendShareContainerReader();
        private readonly PayloadStore m_PayloadStore;

        public PayloadStore PayloadStore => m_PayloadStore;

        public BlendShareSourceRepository(long maxBudgetBytes = 64L << 20, long maxSinglePayloadBytes = 16L << 20)
        {
            m_PayloadStore = new PayloadStore(this, maxBudgetBytes: maxBudgetBytes, maxSinglePayloadBytes: maxSinglePayloadBytes);
        }

        public bool TryGetGuidAndToc(BlendShareAsset asset, out string guid, out ContainerToc toc)
        {
            return TryGetGuidPathAndToc(asset, out guid, out _, out toc);
        }

        private bool TryGetGuidPathAndToc(BlendShareAsset asset, out string guid, out string physicalPath, out ContainerToc toc)
        {
            guid = null;
            physicalPath = null;
            toc = null;
            if (asset == null) return false;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return false;

            guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return false;

            string revision = asset.SourceRevision;
            if (string.IsNullOrEmpty(revision)) return false;

            physicalPath = BlendSharePathUtility.ResolvePhysicalPath(assetPath);

            string revisionToInvalidate = null;
            bool allowRevision = false;

            lock (m_Lock)
            {
                if (m_ByGuid.TryGetValue(guid, out var existing) &&
                    string.Equals(existing.Revision, revision, StringComparison.Ordinal) &&
                    m_ByRevision.TryGetValue(revision, out var existingGroup))
                {
                    existing.PhysicalPath = physicalPath;
                    toc = existingGroup.Toc;
                    return true;
                }
            }

            toc = BlendShareAssetTocMapper.ToContainerToc(asset);
            lock (m_Lock)
            {
                if (m_ByGuid.TryGetValue(guid, out var prev) &&
                    !string.Equals(prev.Revision, revision, StringComparison.Ordinal))
                {
                    revisionToInvalidate = DetachGuidFromRevisionLocked(guid, prev.Revision);
                }

                m_ByGuid[guid] = new GuidRecord { Revision = revision, PhysicalPath = physicalPath };
                if (!m_ByRevision.TryGetValue(revision, out var group))
                {
                    group = new RevisionGroup { Toc = toc };
                    m_ByRevision.Add(revision, group);
                    allowRevision = true;
                }
                else
                {
                    group.Toc = toc;
                }
                group.OwnerGuids.Add(guid);
            }

            // NEVER call PayloadStore methods while holding m_Lock (prevents lock-order inversion deadlock).
            if (!string.IsNullOrEmpty(revisionToInvalidate))
            {
                m_PayloadStore.InvalidateRevision(revisionToInvalidate);
            }
            if (allowRevision)
            {
                m_PayloadStore.AllowRevision(revision);
            }

            return true;
        }

        public ContainerReadResult<BlendShareFeatureData> ReadFeature(BlendShareAsset asset, string meshId, string featureId)
        {
            return ExecuteFailClosed(asset, (stream, toc) => m_Reader.ReadFeature(stream, toc, meshId, featureId));
        }

        public ContainerReadResult<MeshDocument> ReadMeshHeader(BlendShareAsset asset, string meshId)
        {
            return ExecuteFailClosed(asset, (stream, toc) => m_Reader.ReadMeshHeader(stream, toc, meshId));
        }

        public ContainerReadResult<List<ArmatureDocument>> ReadArmatures(BlendShareAsset asset)
        {
            return ExecuteFailClosed(asset, (stream, toc) => m_Reader.ReadArmatures(stream, toc));
        }

        public ContainerReadResult<PatchDocument> ReadPatchDocument(BlendShareAsset asset)
        {
            return ExecuteFailClosed(asset, (stream, toc) => m_Reader.ReadPatchDocument(stream, toc));
        }

        /// <summary>
        /// Thread-safe stream lookup using cached physical paths (never invokes AssetDatabase on worker threads).
        /// Verifies header revision on candidate live owners before returning so stale files trigger Fail-Closed reimport.
        /// </summary>
        public Stream OpenRead(string sourceRevision, out ContainerToc toc)
        {
            toc = null;
            if (string.IsNullOrEmpty(sourceRevision)) return null;

            List<(string Guid, string PhysicalPath)> candidates;
            lock (m_Lock)
            {
                if (!m_ByRevision.TryGetValue(sourceRevision, out var group) || group.OwnerGuids.Count == 0)
                {
                    return null;
                }

                toc = group.Toc;
                candidates = new List<(string, string)>(group.OwnerGuids.Count);
                foreach (string g in group.OwnerGuids)
                {
                    if (m_ByGuid.TryGetValue(g, out var rec) && !string.IsNullOrEmpty(rec.PhysicalPath))
                    {
                        candidates.Add((g, rec.PhysicalPath));
                    }
                }
            }

            string staleGuid = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i].PhysicalPath;
                if (!File.Exists(path)) continue;

                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length == toc.TotalFileLength)
                {
                    return fs;
                }

                fs.Dispose();
                staleGuid = candidates[i].Guid;
            }

            if (!string.IsNullOrEmpty(staleGuid))
            {
                ScheduleReimportByGuid(staleGuid);
            }

            return null;
        }

        public void HandleAssetReimported(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return;

            var asset = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(assetPath);
            string currentRev = asset != null ? asset.SourceRevision : null;
            string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(assetPath);

            string revisionToInvalidate = null;
            lock (m_Lock)
            {
                if (m_ByGuid.TryGetValue(guid, out var existing))
                {
                    if (!string.IsNullOrEmpty(currentRev) && string.Equals(existing.Revision, currentRev, StringComparison.Ordinal))
                    {
                        existing.PhysicalPath = physicalPath;
                        return;
                    }

                    m_ByGuid.Remove(guid);
                    revisionToInvalidate = DetachGuidFromRevisionLocked(guid, existing.Revision);
                }
            }

            if (!string.IsNullOrEmpty(revisionToInvalidate))
            {
                m_PayloadStore.InvalidateRevision(revisionToInvalidate);
            }
        }

        public void HandleAssetMoved(string newAssetPath)
        {
            if (string.IsNullOrEmpty(newAssetPath)) return;
            string guid = AssetDatabase.AssetPathToGUID(newAssetPath);
            if (string.IsNullOrEmpty(guid)) return;
            string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(newAssetPath);

            lock (m_Lock)
            {
                if (m_ByGuid.TryGetValue(guid, out var existing))
                {
                    // Same content, updated physical path: keep warm PayloadStore cache intact!
                    existing.PhysicalPath = physicalPath;
                }
            }
        }

        public void HandleAssetDeleted()
        {
            var orphanedRevisions = new List<string>();
            lock (m_Lock)
            {
                var toRemove = new List<string>();
                foreach (var kv in m_ByGuid)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(kv.Key);
                    if (string.IsNullOrEmpty(assetPath) || !File.Exists(BlendSharePathUtility.ResolvePhysicalPath(assetPath)))
                    {
                        toRemove.Add(kv.Key);
                    }
                }

                for (int i = 0; i < toRemove.Count; i++)
                {
                    string g = toRemove[i];
                    var rec = m_ByGuid[g];
                    m_ByGuid.Remove(g);
                    string orphaned = DetachGuidFromRevisionLocked(g, rec.Revision);
                    if (!string.IsNullOrEmpty(orphaned))
                    {
                        orphanedRevisions.Add(orphaned);
                    }
                }
            }

            for (int i = 0; i < orphanedRevisions.Count; i++)
            {
                m_PayloadStore.InvalidateRevision(orphanedRevisions[i]);
            }
        }

        public void InvalidateByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            string orphaned = null;
            lock (m_Lock)
            {
                if (m_ByGuid.TryGetValue(guid, out var rec))
                {
                    m_ByGuid.Remove(guid);
                    orphaned = DetachGuidFromRevisionLocked(guid, rec.Revision);
                }
            }

            if (!string.IsNullOrEmpty(orphaned))
            {
                m_PayloadStore.InvalidateRevision(orphaned);
            }
        }

        private string DetachGuidFromRevisionLocked(string guid, string revision)
        {
            if (string.IsNullOrEmpty(revision) || !m_ByRevision.TryGetValue(revision, out var group))
            {
                return null;
            }

            group.OwnerGuids.Remove(guid);
            if (group.OwnerGuids.Count == 0)
            {
                m_ByRevision.Remove(revision);
                return revision;
            }

            return null;
        }

        private ContainerReadResult<T> ExecuteFailClosed<T>(
            BlendShareAsset asset,
            Func<Stream, ContainerToc, ContainerReadResult<T>> operation)
        {
            if (!TryGetGuidPathAndToc(asset, out string guid, out string physicalPath, out var toc))
            {
                return ContainerReadResult<T>.Fail(ContainerReadStatus.NotFound, "BlendShareAsset is not backed by a valid project asset path.");
            }

            if (string.IsNullOrEmpty(physicalPath) || !File.Exists(physicalPath))
            {
                return ContainerReadResult<T>.Fail(ContainerReadStatus.NotFound, $"Source file missing for GUID '{guid}'.");
            }

            ContainerReadResult<T> result;
            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = operation(stream, toc);
            }

            if (result.Status == ContainerReadStatus.SourceRevisionMismatch)
            {
                InvalidateByGuid(guid);
                ScheduleReimportByGuid(guid);
            }

            return result;
        }

        private void ScheduleReimportByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            lock (m_Lock)
            {
                if (!m_PendingReimportGuids.Add(guid))
                {
                    return;
                }
            }

            string capturedGuid = guid;
            EditorApplication.delayCall += () =>
            {
                lock (m_Lock)
                {
                    m_PendingReimportGuids.Remove(capturedGuid);
                }

                // Re-resolve path from GUID at callback time so moves never reimport an unrelated replacement.
                string currentPath = AssetDatabase.GUIDToAssetPath(capturedGuid);
                if (!string.IsNullOrEmpty(currentPath) && File.Exists(BlendSharePathUtility.ResolvePhysicalPath(currentPath)))
                {
                    AssetDatabase.ImportAsset(currentPath, ImportAssetOptions.ForceUpdate);
                }
            };
        }

        public void Dispose()
        {
            m_PayloadStore.Dispose();
        }
    }

    internal sealed class BlendShareSourcePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets != null)
            {
                for (int i = 0; i < importedAssets.Length; i++)
                {
                    string path = importedAssets[i];
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
                    {
                        BlendShareSourceRepository.Shared.HandleAssetReimported(path);
                    }
                }
            }

            if (movedAssets != null)
            {
                for (int i = 0; i < movedAssets.Length; i++)
                {
                    string path = movedAssets[i];
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
                    {
                        BlendShareSourceRepository.Shared.HandleAssetMoved(path);
                    }
                }
            }

            if (deletedAssets != null)
            {
                for (int i = 0; i < deletedAssets.Length; i++)
                {
                    if (!string.IsNullOrEmpty(deletedAssets[i]) && deletedAssets[i].EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
                    {
                        BlendShareSourceRepository.Shared.HandleAssetDeleted();
                        break;
                    }
                }
            }
        }
    }
}
