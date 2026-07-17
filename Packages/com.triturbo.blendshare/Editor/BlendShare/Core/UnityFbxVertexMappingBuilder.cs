using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Builds vertex mappings between imported Unity meshes and FBX control points.
    /// </summary>
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-6f;
        private const double MaxAverageMappedBaseOffset = 0.02d;
        private const double MaxAverageMappedBaseSqrOffset = MaxAverageMappedBaseOffset * MaxAverageMappedBaseOffset;
        private const float MaxMappedBaseOffset = 0.05f;
        private const int ParallelMatchThreshold = 1024;
        private const string LogPrefix = "[BlendShare Vertex Mapping]";

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxGo)
        {
            return BuildFromFbx(unityRendererPath, unityMesh, fbxGo, out UfbxMesh _);
        }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxGo,
            out UfbxMesh fbxMesh)
        {
            fbxMesh = null;
            UfbxScene scene = null;
            try
            {
                if (fbxGo != null)
                {
                    var sceneResult = FbxUnityAssetReader.ReadScene(fbxGo);
                    if (sceneResult.Success)
                    {
                        scene = sceneResult.Value;
                    }
                }

                return BuildFromFbx(
                    unityRendererPath,
                    unityMesh,
                    scene,
                    FbxUnityAssetReader.GetImportScale(fbxGo),
                    FbxUnityAssetReader.GetBakeAxisConversion(fbxGo),
                    FbxUnityAssetReader.GetImporterSpaceTransform(fbxGo),
                    out fbxMesh);
            }
            finally
            {
                scene?.Dispose();
            }
        }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            UfbxScene fbxScene,
            float importScale,
            out UfbxMesh fbxMesh)
        {
            return BuildFromFbx(
                unityRendererPath,
                unityMesh,
                fbxScene,
                importScale,
                false,
                Matrix4x4.Scale(Vector3.one * (importScale == 0f ? 1f : importScale)),
                out fbxMesh);
        }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            UfbxScene fbxScene,
            float importScale,
            bool bakeAxisConversion,
            Matrix4x4 fbxToUnity,
            out UfbxMesh fbxMesh)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMs = 0d;

            string nodePath = MeshNodePath.Normalize(unityRendererPath);
            fbxMesh = fbxScene?.FindMeshByNodePath(MeshNodePath.ToFbxPath(nodePath));
            LogTiming("FbxAsset", nodePath,
                "Resolve FBX vertex positions and blendshapes by node path", stopwatch, ref lastLogMs,
                $"found={(fbxMesh != null ? 1 : 0)}, fbxVertices={fbxMesh?.ControlPointCount ?? 0}, blendShapes={CountBlendShapeChannels(fbxMesh)}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVertexHash = UnityMeshEditorDataUtility.TryCalculatePositionHash(unityMesh, out string unityVertexHash)
                ? unityVertexHash
                : string.Empty;
            mapping.m_FbxToUnityScale = importScale == 0f ? 1f : importScale;
            mapping.m_BakeAxisConversion = bakeAxisConversion;
            mapping.m_IndexGroups = CreateEmptyGroups(Mathf.Max(0, mapping.m_UnityVertexCount));
            InitializeReport(mapping);
            LogTiming("FbxAsset", nodePath, "Create mapping object", stopwatch, ref lastLogMs,
                $"unityVertices={mapping.m_IndexGroups.Length}");

            if (unityMesh == null)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires a Unity mesh asset.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (fbxScene == null)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires an open ufbx scene.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (fbxMesh == null || fbxMesh.ControlPointCount == 0)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping could not read matching FBX vertex positions.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (mapping.m_UnityVertexCount <= 0)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires a Unity mesh with vertices.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            var pair = PositionFingerprintFactory.CreatePair(fbxMesh, unityMesh, fbxToUnity);
            LogTiming("FbxAsset", nodePath, "CreatePair", stopwatch, ref lastLogMs,
                $"usableBlendShapes={pair.BlendShapeNames?.Length ?? 0}");

            if (!pair.IsValid)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires matching FBX and Unity mesh blendshapes, or no usable blendshapes on either mesh for position-only matching.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            LogTiming("FbxAsset", nodePath, "Create FBX fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.FbxFingerprints.Length}");
            LogTiming("FbxAsset", nodePath, "Create Unity fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.UnityFingerprints.Length}");

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_IndexGroups,
                pair.UnityFingerprints,
                pair.FbxFingerprints);
            LogTiming("FbxGo", nodePath, "Match Unity vertices to FBX vertices", stopwatch, ref lastLogMs,
                $"unresolved={unresolved}");

            if (unresolved != 0)
            {
                SetMappingStatus(mapping, false, $"FBX asset mapping has {unresolved} Unity vertices without matching FBX vertex positions.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            double averageBaseSqrOffset = CalculateAverageMappedBaseSqrOffset(
                mapping.m_IndexGroups,
                pair.UnityFingerprints,
                pair.FbxFingerprints);
            float maxBaseOffset = CalculateMaxMappedBaseOffset(
                mapping.m_IndexGroups,
                pair.UnityFingerprints,
                pair.FbxFingerprints,
                MaxMappedBaseOffset,
                out int maxOffsetViolationCount);
            LogTiming("FbxAsset", nodePath, "Validate mapped base position offsets", stopwatch, ref lastLogMs,
                $"averageSqrOffset={averageBaseSqrOffset:0.##########}, rmsOffset={System.Math.Sqrt(averageBaseSqrOffset):0.##########}, maxOffset={maxBaseOffset:0.##########}, maxOffsetViolations={maxOffsetViolationCount}");

            bool averageOffsetValid = averageBaseSqrOffset <= MaxAverageMappedBaseSqrOffset;
            bool maxOffsetValid = maxBaseOffset <= MaxMappedBaseOffset;
            bool isValid = averageOffsetValid && maxOffsetValid;
            string summary = isValid
                ? string.Empty
                : FormatOffsetValidationFailure(averageBaseSqrOffset, maxBaseOffset, maxOffsetViolationCount);
            SetMappingStatus(mapping, isValid, summary);

            LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
            return mapping;
        }

        // ─── Core matching ─────────────────────────────────────────────────────────

        private static int MapUnityVerticesByFingerprintMatch(
            FbxIndexGroup[] mappingGroups,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            PositionFingerprint[] fbxFingerprints)
        {
            var sw = Stopwatch.StartNew();

            // Build welding groups: K unique fingerprint groups (K ≤ M control points).
            var weldingIndex = new PositionFingerprint.WeldingGroupIndex(fbxFingerprints, FingerprintEpsilon);
            double weldMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: WeldingGroupIndex built in {weldMs:0.###} ms" +
                      $" (fbxControlPoints={fbxFingerprints?.Length ?? 0}, groups={weldingIndex.GroupCount})");

            // Build spatial candidate index over K representatives only (faster than M).
            var candidateIndex = new PositionFingerprint.CandidateIndex(
                weldingIndex.GetAllRepresentatives(),
                FingerprintEpsilon);
            double indexMs = sw.Elapsed.TotalMilliseconds - weldMs;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: CandidateIndex built in {indexMs:0.###} ms");

            double matchStart = sw.Elapsed.TotalMilliseconds;
            int unresolved = 0;
            int unityVertexCount = unityFingerprints.Count;
            if (unityVertexCount >= ParallelMatchThreshold)
            {
                Parallel.For(
                    0,
                    unityVertexCount,
                    () => 0,
                    (unityIndex, _, localUnresolved) =>
                    {
                        if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(candidateIndex, out int groupId))
                        {
                            localUnresolved++;
                            return localUnresolved;
                        }

                        mappingGroups[unityIndex] = new FbxIndexGroup
                        {
                            m_Indices = weldingIndex.GetGroupMembers(groupId)
                        };

                        return localUnresolved;
                    },
                    localUnresolved => Interlocked.Add(ref unresolved, localUnresolved));
            }
            else
            {
                for (int unityIndex = 0; unityIndex < unityVertexCount; unityIndex++)
                {
                    if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(candidateIndex, out int groupId))
                    {
                        // Leave group as empty (initialized above)
                        unresolved++;
                        continue;
                    }

                    mappingGroups[unityIndex] = new FbxIndexGroup
                    {
                        m_Indices = weldingIndex.GetGroupMembers(groupId)
                    };
                }
            }
            double matchMs = sw.Elapsed.TotalMilliseconds - matchStart;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: Matching loop done in {matchMs:0.###} ms" +
                      $" (unityVertices={unityVertexCount}, unresolved={unresolved}, " +
                      $"mode={(unityVertexCount >= ParallelMatchThreshold ? "parallel" : "sequential")})");
            candidateIndex.LogStats(LogPrefix);

            return unresolved;
        }

        private static double CalculateAverageMappedBaseSqrOffset(
            IReadOnlyList<FbxIndexGroup> mappingGroups,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            IReadOnlyList<PositionFingerprint> fbxFingerprints)
        {
            if (mappingGroups == null || unityFingerprints == null || fbxFingerprints == null)
            {
                return double.PositiveInfinity;
            }

            if (mappingGroups.Count != unityFingerprints.Count)
            {
                return double.PositiveInfinity;
            }

            double total = 0d;
            int count = unityFingerprints.Count;
            for (int unityIndex = 0; unityIndex < count; unityIndex++)
            {
                var indices = mappingGroups[unityIndex].m_Indices;
                if (indices == null || indices.Length == 0)
                {
                    return double.PositiveInfinity;
                }

                total += CalculateGroupBaseSqrOffset(
                    unityFingerprints[unityIndex],
                    indices,
                    fbxFingerprints);
            }

            return count > 0 ? total / count : double.PositiveInfinity;
        }

        private static double CalculateGroupBaseSqrOffset(
            PositionFingerprint unityFingerprint,
            IReadOnlyList<int> fbxIndices,
            IReadOnlyList<PositionFingerprint> fbxFingerprints)
        {
            if (unityFingerprint == null || fbxIndices == null || fbxIndices.Count == 0 || fbxFingerprints == null)
            {
                return double.PositiveInfinity;
            }

            double total = 0d;
            int count = 0;
            for (int i = 0; i < fbxIndices.Count; i++)
            {
                int fbxIndex = fbxIndices[i];
                if (fbxIndex < 0 || fbxIndex >= fbxFingerprints.Count || fbxFingerprints[fbxIndex] == null)
                {
                    return double.PositiveInfinity;
                }

                Vector3 delta = unityFingerprint.BasePosition - fbxFingerprints[fbxIndex].BasePosition;
                total += delta.sqrMagnitude;
                count++;
            }

            return count > 0 ? total / count : double.PositiveInfinity;
        }

        private static float CalculateMaxMappedBaseOffset(
            IReadOnlyList<FbxIndexGroup> mappingGroups,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            IReadOnlyList<PositionFingerprint> fbxFingerprints,
            float threshold,
            out int violationCount)
        {
            violationCount = 0;
            if (mappingGroups == null || unityFingerprints == null || fbxFingerprints == null ||
                mappingGroups.Count != unityFingerprints.Count)
            {
                violationCount = int.MaxValue;
                return float.PositiveInfinity;
            }

            float max = 0f;
            for (int unityIndex = 0; unityIndex < unityFingerprints.Count; unityIndex++)
            {
                var indices = mappingGroups[unityIndex].m_Indices;
                if (indices == null || indices.Length == 0)
                {
                    violationCount = int.MaxValue;
                    return float.PositiveInfinity;
                }

                for (int i = 0; i < indices.Length; i++)
                {
                    int fbxIndex = indices[i];
                    if (fbxIndex < 0 || fbxIndex >= fbxFingerprints.Count || fbxFingerprints[fbxIndex] == null)
                    {
                        violationCount = int.MaxValue;
                        return float.PositiveInfinity;
                    }

                    float offset = (unityFingerprints[unityIndex].BasePosition - fbxFingerprints[fbxIndex].BasePosition).magnitude;
                    if (offset > threshold)
                    {
                        violationCount++;
                    }

                    max = Mathf.Max(max, offset);
                }
            }

            return max;
        }

        private static string FormatOffsetValidationFailure(
            double averageBaseSqrOffset,
            float maxBaseOffset,
            int maxOffsetViolationCount)
        {
            return $"FBX asset mapping vertex offset validation failed. " +
                   $"Average squared offset: {averageBaseSqrOffset:0.##########}, " +
                   $"RMS offset: {System.Math.Sqrt(averageBaseSqrOffset):0.##########} " +
                   $"(limit {MaxAverageMappedBaseOffset:0.##########}); " +
                   $"max offset: {maxBaseOffset:0.##########} " +
                   $"(limit {MaxMappedBaseOffset:0.##########}, violations {maxOffsetViolationCount}).";
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static FbxIndexGroup[] CreateEmptyGroups(int count)
        {
            var groups = new FbxIndexGroup[count];
            for (int i = 0; i < count; i++)
            {
                groups[i] = new FbxIndexGroup { m_Indices = System.Array.Empty<int>() };
            }
            return groups;
        }

        private static int CountBlendShapeChannels(UfbxMesh mesh)
        {
            return mesh?.BlendDeformers.Sum(deformer => deformer.Channels.Count) ?? 0;
        }

        private static void InitializeReport(UnityVertexMappingObject mapping)
        {
            mapping.m_Report = string.Empty;
        }

        private static void SetMappingStatus(UnityVertexMappingObject mapping, bool isValid, string summary)
        {
            mapping.m_IsValid = isValid;
            mapping.m_Report = summary ?? string.Empty;
        }

        // ─── Logging ───────────────────────────────────────────────────────────────

        private static void LogTiming(
            string buildMode,
            string unityRendererPath,
            string section,
            Stopwatch stopwatch,
            ref double previousMs,
            string details = null)
        {
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            double sectionMs = elapsed - previousMs;
            previousMs = elapsed;
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': {section} took {sectionMs:0.###} ms (total {elapsed:0.###} ms){FormatLogDetails(details)}");
        }

        private static void LogCompletion(
            string buildMode,
            string unityRendererPath,
            Stopwatch stopwatch,
            UnityVertexMappingObject mapping)
        {
            string status = mapping != null && mapping.m_IsValid
                ? "valid"
                : $"invalid: {mapping?.m_Report}";
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': Finished in {stopwatch.Elapsed.TotalMilliseconds:0.###} ms ({status})");
        }

        private static string FormatLogTarget(string path)
        {
            return string.IsNullOrEmpty(path) ? "<unknown>" : path;
        }

        private static string FormatLogDetails(string details)
        {
            return string.IsNullOrEmpty(details) ? string.Empty : $" ({details})";
        }
    }
}
