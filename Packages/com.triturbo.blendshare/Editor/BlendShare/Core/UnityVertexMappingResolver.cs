using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Hashing;
using Triturbo.BlendShare.Mapping;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Indicates how a resolved <see cref="IUnityToFbxVertexMap"/> was obtained during a generation session.
    /// </summary>
    internal enum MappingResolutionProvenance
    {
        None = 0,
        StoredOrOverride = 1,
        MeshToMeshRemap = 2,
        FbxInverseMap = 3
    }

    /// <summary>
    /// Result returned by <see cref="UnityVertexMappingResolver"/>.
    /// </summary>
    internal readonly struct ResolvedVertexMapping
    {
        public IUnityToFbxVertexMap Map { get; }
        public MappingResolutionProvenance Provenance { get; }
        public string Diagnostic { get; }

        public bool Succeeded => Map != null;

        public ResolvedVertexMapping(
            IUnityToFbxVertexMap map,
            MappingResolutionProvenance provenance,
            string diagnostic = null)
        {
            Map = map;
            Provenance = provenance;
            Diagnostic = diagnostic ?? string.Empty;
        }
    }

    /// <summary>
    /// Pure in-memory <see cref="IUnityToFbxVertexMap"/> backed by a composed or inverse-mapped array,
    /// avoiding transient <see cref="ScriptableObject"/> creation and never mutating distributed patches.
    /// </summary>
    internal sealed class TransientUnityToFbxVertexMap : IUnityToFbxVertexMap
    {
        private readonly FbxIndexGroup[] groups;

        /// <inheritdoc />
        public int UnityVertexCount => groups.Length;

        /// <inheritdoc />
        public FbxUnitySpaceConversion SpaceConversion { get; }

        internal TransientUnityToFbxVertexMap(int[][] rawGroups, FbxUnitySpaceConversion spaceConversion)
        {
            groups = UnityFbxVertexMappingBuilder.ToSerializedIndexGroups(rawGroups);
            SpaceConversion = spaceConversion;
        }

        /// <inheritdoc />
        public bool TryGetFbxGroup(int unityVertexIndex, out FbxIndexGroup group)
        {
            if ((uint)unityVertexIndex < (uint)groups.Length)
            {
                group = groups[unityVertexIndex];
                return group.m_Indices != null && group.m_Indices.Length > 0 && group.m_Indices[0] >= 0;
            }

            group = default;
            return false;
        }
    }

    /// <summary>
    /// Session-scoped resolver that locates or derives an <see cref="IUnityToFbxVertexMap"/> for a
    /// <c>(MeshDataObject, Mesh, Overrides, FallbackFbx)</c> request without coupling contexts or feature
    /// generators to mapping algorithms. Memoizes results per session and disposes lazily opened FBX scenes.
    /// </summary>
    internal sealed class UnityVertexMappingResolver : IDisposable
    {
        private const float RoundTripPositionEpsilon = 1e-5f;

        private readonly Dictionary<CacheKey, ResolvedVertexMapping> memoized = new();
        private readonly Dictionary<Mesh, UnityMeshSnapshot> snapshotCache = new(ReferenceComparer<Mesh>.Instance);
        private readonly Dictionary<GameObject, CachedFbxSource> fbxSourceCache = new(ReferenceComparer<GameObject>.Instance);
        private bool disposed;

        /// <summary>
        /// Resolves a compatible vertex mapping for <paramref name="targetMesh"/>, trying in order:
        /// 1) exact stored/override match (editor-safe hash),
        /// 2) Unity mesh-to-mesh auto-remap from a verified reference mesh with a stored mapping,
        /// 3) direct FBX inverse mapping against a topology-verified compatible FBX.
        /// </summary>
        public ResolvedVertexMapping Resolve(
            MeshDataObject meshData,
            Mesh targetMesh,
            IReadOnlyList<UnityVertexMappingObject> overrides,
            GameObject fallbackSourceFbx = null)
        {
            if (meshData == null || targetMesh == null)
            {
                return default;
            }

            var key = new CacheKey(meshData, targetMesh, fallbackSourceFbx, overrides);
            if (memoized.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolved = ResolveUncached(meshData, targetMesh, overrides, fallbackSourceFbx);
            memoized[key] = resolved;
            return resolved;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in fbxSourceCache.Values)
            {
                entry.Scene?.Dispose();
            }

            fbxSourceCache.Clear();
            snapshotCache.Clear();
            memoized.Clear();
        }

        private ResolvedVertexMapping ResolveUncached(
            MeshDataObject meshData,
            Mesh targetMesh,
            IReadOnlyList<UnityVertexMappingObject> overrides,
            GameObject fallbackSourceFbx)
        {
            // Strategy 1: Editor-safe exact match on overrides and stored mappings (fast path).
            var direct = FindDirectCompatibleMapping(meshData, targetMesh, overrides);
            if (direct != null)
            {
                return new ResolvedVertexMapping(direct, MappingResolutionProvenance.StoredOrOverride);
            }

            if (!TryGetSnapshot(targetMesh, out var targetSnapshot))
            {
                return new ResolvedVertexMapping(
                    null,
                    MappingResolutionProvenance.None,
                    "Could not read target Unity mesh snapshot for auto-remap.");
            }

            // Strategy 2: Mesh-to-mesh auto-remap against a stored mapping whose verified reference Unity mesh is available.
            if (TryRemapViaMeshToMesh(meshData, overrides, targetSnapshot, fallbackSourceFbx, out var remapped))
            {
                return remapped;
            }

            // Strategy 3: Direct FBX inverse mapping against a topology-verified FBX asset.
            if (TryRemapViaFbxInverse(meshData, targetMesh, targetSnapshot, fallbackSourceFbx, out var inverseMapped))
            {
                return inverseMapped;
            }

            return new ResolvedVertexMapping(
                null,
                MappingResolutionProvenance.None,
                "No stored Unity vertex mapping matched, and automatic remap could not establish correspondence.");
        }

        private static UnityVertexMappingObject FindDirectCompatibleMapping(
            MeshDataObject meshData,
            Mesh targetMesh,
            IReadOnlyList<UnityVertexMappingObject> overrides)
        {
            if (overrides != null)
            {
                for (int i = 0; i < overrides.Count; i++)
                {
                    var mapping = overrides[i];
                    if (UnityMeshEditorDataUtility.IsMappingCompatible(mapping, meshData, targetMesh))
                    {
                        return mapping;
                    }
                }
            }

            var stored = meshData.m_Mappings;
            if (stored != null)
            {
                for (int i = 0; i < stored.Length; i++)
                {
                    var mapping = stored[i];
                    if (UnityMeshEditorDataUtility.IsMappingCompatible(mapping, meshData, targetMesh))
                    {
                        return mapping;
                    }
                }
            }

            return null;
        }

        private bool TryRemapViaMeshToMesh(
            MeshDataObject meshData,
            IReadOnlyList<UnityVertexMappingObject> overrides,
            UnityMeshSnapshot targetSnapshot,
            GameObject fallbackSourceFbx,
            out ResolvedVertexMapping resolved)
        {
            resolved = default;
            int expectedFbxCps = meshData.FbxControlPointCount;

            foreach (var candidate in EnumerateStoredCandidates(overrides, meshData.m_Mappings))
            {
                if (candidate == null || !candidate.m_IsValid ||
                    candidate.m_IndexGroups == null ||
                    candidate.m_IndexGroups.Length != candidate.m_UnityVertexCount ||
                    !candidate.MatchesFbxControlPointCount(expectedFbxCps))
                {
                    continue;
                }

                var referenceMesh = ResolveReferenceMesh(candidate, meshData.m_Path, fallbackSourceFbx);
                // Verify the reference mesh STILL matches candidate's stored vertex hash so an in-place
                // re-import with reordered vertices can never be composed against stale m_IndexGroups rows.
                if (referenceMesh == null ||
                    !UnityMeshEditorDataUtility.IsMappingCompatible(candidate, meshData, referenceMesh) ||
                    !TryGetSnapshot(referenceMesh, out var referenceSnapshot))
                {
                    continue;
                }

                // Try exact float32 first (NDMF/reordered meshes), then epsilon tolerance (Blender round-trip).
                var correspondence = UnityMeshToMeshMapper.Map(referenceSnapshot, targetSnapshot, MeshToMeshOptions.Default);
                if (!correspondence.Succeeded)
                {
                    correspondence = UnityMeshToMeshMapper.Map(
                        referenceSnapshot,
                        targetSnapshot,
                        new MeshToMeshOptions(positionEpsilon: RoundTripPositionEpsilon));
                }

                if (!correspondence.Succeeded)
                {
                    continue;
                }

                var rawSourceGroups = ExtractRawGroups(candidate.m_IndexGroups);
                var composed = VertexMapComposer.Compose(
                    rawSourceGroups,
                    correspondence,
                    VertexMapComposePolicy.AllowMergedUnion);
                if (!composed.Succeeded)
                {
                    continue;
                }

                var transient = new TransientUnityToFbxVertexMap(composed.TargetFbxGroups, candidate.SpaceConversion);
                resolved = new ResolvedVertexMapping(
                    transient,
                    MappingResolutionProvenance.MeshToMeshRemap,
                    $"Auto-remapped via mesh-to-mesh correspondence (anchors={correspondence.CommonTriangleAnchorCount}, " +
                    $"merged={correspondence.MergedTargetVertexCount}, split={correspondence.SplitSourceVertexCount}).");
                return true;
            }

            return false;
        }

        private bool TryRemapViaFbxInverse(
            MeshDataObject meshData,
            Mesh targetMesh,
            UnityMeshSnapshot targetSnapshot,
            GameObject fallbackSourceFbx,
            out ResolvedVertexMapping resolved)
        {
            resolved = default;
            var fbxCandidates = CollectFbxAssetCandidates(targetMesh, fallbackSourceFbx);
            string fbxNodePath = MeshNodePath.ToFbxPath(meshData.m_Path);
            var storedSignature = meshData.m_FbxTopologySignature;
            bool hasStoredTopologyHash = storedSignature != null &&
                                         storedSignature.IsValid &&
                                         !string.IsNullOrEmpty(storedSignature.Hash);
            int expectedFbxCps = meshData.FbxControlPointCount;

            for (int i = 0; i < fbxCandidates.Count; i++)
            {
                var fbxGo = fbxCandidates[i];
                // Without a stored topology hash (legacy patch), only trust the patch's own origin FBX.
                if (!hasStoredTopologyHash && !ReferenceEquals(fbxGo, fallbackSourceFbx))
                {
                    continue;
                }

                if (!TryGetFbxSource(fbxGo, out var fbxSource) || fbxSource.Scene == null)
                {
                    continue;
                }

                var fbxMesh = fbxSource.Scene.FindMeshByNodePath(fbxNodePath);
                if (fbxMesh == null || fbxMesh.ControlPointCount <= 0)
                {
                    continue;
                }

                if (expectedFbxCps > 0 && fbxMesh.ControlPointCount != expectedFbxCps)
                {
                    continue;
                }

                // Enforce exact FBX topology signature match so renumbered control points in a re-exported
                // FBX can never map patch deltas to the wrong control-point indices.
                if (hasStoredTopologyHash)
                {
                    var actualSignature = FbxTopologyHash.Calculate(fbxMesh);
                    if (actualSignature == null ||
                        !actualSignature.IsValid ||
                        !string.Equals(actualSignature.Hash, storedSignature.Hash, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                var result = FbxUnityInverseMapper.Map(
                    fbxMesh,
                    targetSnapshot,
                    FbxInverseMapHints.FromImportSettings(fbxSource.ImportSettings));
                if (!result.Succeeded)
                {
                    continue;
                }

                var conversion = new FbxUnitySpaceConversion(result.MatchedScale, result.MatchedBakeAxisConversion);
                var transient = new TransientUnityToFbxVertexMap(result.VertexControlPointGroups, conversion);
                resolved = new ResolvedVertexMapping(
                    transient,
                    MappingResolutionProvenance.FbxInverseMap,
                    result.Diagnostic);
                return true;
            }

            return false;
        }

        private bool TryGetSnapshot(Mesh mesh, out UnityMeshSnapshot snapshot)
        {
            if (snapshotCache.TryGetValue(mesh, out snapshot))
            {
                return snapshot != null;
            }

            if (!UnityMeshEditorDataUtility.TryReadMeshSnapshot(mesh, out snapshot))
            {
                snapshot = null;
            }

            snapshotCache[mesh] = snapshot;
            return snapshot != null;
        }

        private bool TryGetFbxSource(GameObject fbxGo, out CachedFbxSource cached)
        {
            if (fbxGo == null)
            {
                cached = default;
                return false;
            }

            if (fbxSourceCache.TryGetValue(fbxGo, out cached))
            {
                return cached.Scene != null;
            }

            var read = FbxUnityAssetReader.ReadScene(fbxGo);
            cached = new CachedFbxSource(
                read.Success ? read.Value : null,
                FbxUnityAssetReader.GetImportSettings(fbxGo));
            fbxSourceCache[fbxGo] = cached;
            return cached.Scene != null;
        }

        private static Mesh ResolveReferenceMesh(
            UnityVertexMappingObject mapping,
            string meshPath,
            GameObject fallbackSourceFbx)
        {
            if (mapping.m_UnityMesh != null)
            {
                return mapping.m_UnityMesh;
            }

            if (fallbackSourceFbx != null)
            {
                var lookup = UnityMeshTargetLookup.Create(fallbackSourceFbx);
                if (lookup != null && lookup.TryGetMesh(meshPath, out var fbxImportedMesh) && fbxImportedMesh != null)
                {
                    return fbxImportedMesh;
                }
            }

            return null;
        }

        private static List<GameObject> CollectFbxAssetCandidates(Mesh targetMesh, GameObject fallbackSourceFbx)
        {
            var list = new List<GameObject>(2);
            if (targetMesh != null)
            {
                string targetPath = AssetDatabase.GetAssetPath(targetMesh);
                if (!string.IsNullOrEmpty(targetPath) &&
                    targetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    var mainGo = AssetDatabase.LoadMainAssetAtPath(targetPath) as GameObject;
                    if (mainGo != null)
                    {
                        list.Add(mainGo);
                    }
                }
            }

            if (fallbackSourceFbx != null && !list.Contains(fallbackSourceFbx))
            {
                string fallbackPath = AssetDatabase.GetAssetPath(fallbackSourceFbx);
                if (!string.IsNullOrEmpty(fallbackPath) &&
                    fallbackPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(fallbackSourceFbx);
                }
            }

            return list;
        }

        private static IEnumerable<UnityVertexMappingObject> EnumerateStoredCandidates(
            IReadOnlyList<UnityVertexMappingObject> overrides,
            UnityVertexMappingObject[] stored)
        {
            if (overrides != null)
            {
                for (int i = 0; i < overrides.Count; i++)
                {
                    yield return overrides[i];
                }
            }

            if (stored != null)
            {
                for (int i = 0; i < stored.Length; i++)
                {
                    yield return stored[i];
                }
            }
        }

        private static int[][] ExtractRawGroups(FbxIndexGroup[] groups)
        {
            var raw = new int[groups.Length][];
            for (int i = 0; i < groups.Length; i++)
            {
                raw[i] = groups[i].m_Indices ?? Array.Empty<int>();
            }

            return raw;
        }

        private readonly struct CachedFbxSource
        {
            public readonly IFbxScene Scene;
            public readonly FbxUnityImportSettings ImportSettings;

            public CachedFbxSource(IFbxScene scene, FbxUnityImportSettings importSettings)
            {
                Scene = scene;
                ImportSettings = importSettings;
            }
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly MeshDataObject meshData;
            private readonly Mesh targetMesh;
            private readonly GameObject fallbackSourceFbx;
            private readonly UnityVertexMappingObject[] overrides;
            private readonly int cachedHash;

            public CacheKey(
                MeshDataObject meshData,
                Mesh targetMesh,
                GameObject fallbackSourceFbx,
                IReadOnlyList<UnityVertexMappingObject> overrides)
            {
                this.meshData = meshData;
                this.targetMesh = targetMesh;
                this.fallbackSourceFbx = fallbackSourceFbx;

                if (overrides == null || overrides.Count == 0)
                {
                    this.overrides = Array.Empty<UnityVertexMappingObject>();
                }
                else
                {
                    this.overrides = new UnityVertexMappingObject[overrides.Count];
                    for (int i = 0; i < overrides.Count; i++)
                    {
                        this.overrides[i] = overrides[i];
                    }
                }

                unchecked
                {
                    int h = RuntimeHelpers.GetHashCode(meshData);
                    h = (h * 397) ^ RuntimeHelpers.GetHashCode(targetMesh);
                    h = (h * 397) ^ RuntimeHelpers.GetHashCode(fallbackSourceFbx);
                    for (int i = 0; i < this.overrides.Length; i++)
                    {
                        h = (h * 397) ^ RuntimeHelpers.GetHashCode(this.overrides[i]);
                    }

                    cachedHash = h;
                }
            }

            public bool Equals(CacheKey other)
            {
                if (!ReferenceEquals(meshData, other.meshData) ||
                    !ReferenceEquals(targetMesh, other.targetMesh) ||
                    !ReferenceEquals(fallbackSourceFbx, other.fallbackSourceFbx) ||
                    overrides.Length != other.overrides.Length)
                {
                    return false;
                }

                for (int i = 0; i < overrides.Length; i++)
                {
                    if (!ReferenceEquals(overrides[i], other.overrides[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj) => obj is CacheKey other && Equals(other);
            public override int GetHashCode() => cachedHash;
        }
    }
}
