using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

using Triturbo.BlendShapeShare.FbxReader;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-6f;
        private const int ParallelMatchThreshold = 1024;
        private const string LogPrefix = "[BlendShare Vertex Mapping]";


        // internal static UnityVertexMappingObject BuildFromLegacy(
        //     string unityRendererPath,
        //     int unityVertexCount,
        //     int unityVerticesHash,
        //     IEnumerable<BlendShapeRecord> fbxBlendShapes,
        //     IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes,
        //     Mesh unityMesh = null,
        //     FbxMeshSnapshot fbxMesh = null)
        // {
        //     var stopwatch = Stopwatch.StartNew();
        //     double lastLogMs = 0d;

        //     var records = ToArray(fbxBlendShapes);
        //     var unityCache = ToArray(unityBlendShapes);
        //     LogTiming("Legacy", unityRendererPath, "Collect input arrays", stopwatch, ref lastLogMs,
        //         $"fbxBlendShapes={records.Length}, unityBlendShapes={unityCache.Length}");

        //     var unityBlendShapesByName = BuildUnityBlendShapeLookup(unityCache);
        //     LogTiming("Legacy", unityRendererPath, "Build Unity blendshape lookup", stopwatch, ref lastLogMs,
        //         $"unityBlendShapeNames={unityBlendShapesByName.Count}");

        //     PositionFingerprintFactory.BuildBlendShapeSequencesByName(
        //         records,
        //         unityBlendShapesByName,
        //         out var blendShapeSequence,
        //         out var fbxBlendShapeSequence,
        //         out var unityBlendShapeSequence);
        //     LogTiming("Legacy", unityRendererPath, "Build blendshape sequences", stopwatch, ref lastLogMs,
        //         $"usableBlendShapes={blendShapeSequence.Length}");

        //     var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
        //     mapping.m_UnityRendererPath = unityRendererPath;
        //     mapping.m_UnityVertexCount = unityVertexCount;
        //     mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(unityMesh);
        //     mapping.m_FbxToUnityScale = fbxMesh?.ImportScale ?? 1f;
        //     mapping.m_BuildMode = UnityFbxMappingBuildMode.LegacyUpgrade;
        //     mapping.m_SourceBlendShapeNames = blendShapeSequence;
        //     mapping.m_IndexGroups = CreateEmptyGroups(Mathf.Max(0, unityVertexCount));
        //     LogTiming("Legacy", unityRendererPath, "Create mapping object", stopwatch, ref lastLogMs,
        //         $"unityVertices={mapping.m_IndexGroups.Length}");

        //     if (blendShapeSequence.Length == 0 || unityBlendShapesByName.Count == 0 || unityVertexCount <= 0)
        //     {
        //         mapping.m_IsValid = false;
        //         mapping.m_InvalidReason = "Legacy mapping requires both FBX and Unity blendshape data.";
        //         mapping.SetLegacyCache(unityCache);
        //         LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
        //         return mapping;
        //     }

        //     int sampleCount = PositionFingerprintFactory.CountSamples(unityBlendShapeSequence);
        //     LogTiming("Legacy", unityRendererPath, "Count fingerprint samples", stopwatch, ref lastLogMs,
        //         $"samples={sampleCount}");
        //     if (sampleCount == 0)
        //     {
        //         mapping.m_IsValid = false;
        //         mapping.m_InvalidReason = "Legacy mapping requires matching FBX and Unity blendshape frames.";
        //         mapping.SetLegacyCache(unityCache);
        //         LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
        //         return mapping;
        //     }

        //     if (!HasUnityMesh(unityMesh, unityVertexCount) || !HasFbxMesh(fbxMesh))
        //     {
        //         mapping.m_IsValid = false;
        //         mapping.m_InvalidReason = "Legacy mapping requires inferable Unity and FBX base positions.";
        //         mapping.SetLegacyCache(unityCache);
        //         LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
        //         return mapping;
        //     }
        //     LogTiming("Legacy", unityRendererPath, "Validate source meshes", stopwatch, ref lastLogMs,
        //         $"unityMeshVertices={unityMesh.vertexCount}, fbxControlPoints={fbxMesh.ControlPointCount}");

        //     var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(fbxMesh, fbxBlendShapeSequence);
        //     LogTiming("Legacy", unityRendererPath, "Create FBX fingerprints", stopwatch, ref lastLogMs,
        //         $"fingerprints={fbxFingerprints.Length}");

        //     var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(unityMesh, unityBlendShapeSequence);
        //     LogTiming("Legacy", unityRendererPath, "Create Unity fingerprints", stopwatch, ref lastLogMs,
        //         $"fingerprints={unityFingerprints.Length}");

        //     int unresolved = MapUnityVerticesByFingerprintMatch(
        //         mapping.m_IndexGroups,
        //         unityFingerprints,
        //         fbxFingerprints);
        //     LogTiming("Legacy", unityRendererPath, "Match Unity vertices to FBX vertices", stopwatch, ref lastLogMs,
        //         $"unresolved={unresolved}");

        //     mapping.m_IsValid = unresolved == 0;
        //     mapping.m_InvalidReason = mapping.m_IsValid
        //         ? string.Empty
        //         : $"Legacy mapping has {unresolved} unresolved Unity vertices.";

        //     if (!mapping.m_IsValid)
        //     {
        //         mapping.SetLegacyCache(unityCache);
        //         LogTiming("Legacy", unityRendererPath, "Store legacy fallback cache", stopwatch, ref lastLogMs,
        //             $"fallbackBlendShapes={unityCache.Length}");
        //     }

        //     LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
        //     return mapping;
        // }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxAsset)
        {
            return BuildFromFbx(unityRendererPath, unityMesh, fbxAsset, out FbxMeshSnapshot _);
        }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxAsset,
            out FbxMeshSnapshot fbxMesh)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMs = 0d;

            var readCandidates = BuildFbxReadCandidates(unityRendererPath, unityMesh);
            LogTiming("FbxAsset", unityRendererPath, "Collect FBX read candidates", stopwatch, ref lastLogMs,
                $"candidates={readCandidates.Length}");

            fbxMesh = null;
            if (fbxAsset != null && readCandidates.Length > 0)
            {
                var fbxMeshes = BinaryFbxMeshReader.TryReadMeshes(
                    fbxAsset,
                    readCandidates,
                    FbxMeshReadOptions.ControlPointPositions | FbxMeshReadOptions.BlendShapes);
                TryGetFbxMesh(fbxMeshes, readCandidates, out fbxMesh);
            }
            LogTiming("FbxAsset", unityRendererPath,
                "Read FBX control point positions and blendshapes", stopwatch, ref lastLogMs,
                $"found={(fbxMesh != null ? 1 : 0)}, controlPoints={fbxMesh?.ControlPointCount ?? 0}, blendShapes={fbxMesh?.BlendShapes?.Length ?? 0}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(unityMesh);
            mapping.m_FbxToUnityScale = fbxMesh?.ImportScale ?? 1f;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.FbxAsset;
            mapping.m_IndexGroups = CreateEmptyGroups(Mathf.Max(0, mapping.m_UnityVertexCount));
            LogTiming("FbxAsset", unityRendererPath, "Create mapping object", stopwatch, ref lastLogMs,
                $"unityVertices={mapping.m_IndexGroups.Length}");

            if (unityMesh == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires a Unity mesh asset.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (fbxAsset == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires an FBX asset.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (fbxMesh == null || fbxMesh.ControlPointCount == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping could not read matching FBX control point positions.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            var pair = PositionFingerprintFactory.CreatePair(fbxMesh, unityMesh);
            LogTiming("FbxAsset", unityRendererPath, "CreatePair", stopwatch, ref lastLogMs,
                $"usableBlendShapes={pair.BlendShapeNames?.Length ?? 0}");

            if (!pair.IsValid)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires matching FBX and Unity mesh blendshapes with frames.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            mapping.m_SourceBlendShapeNames = pair.BlendShapeNames;

            if (mapping.m_UnityVertexCount <= 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires a Unity mesh with vertices.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            LogTiming("FbxAsset", unityRendererPath, "Create FBX fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.FbxFingerprints.Length}");
            LogTiming("FbxAsset", unityRendererPath, "Create Unity fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.UnityFingerprints.Length}");

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_IndexGroups,
                pair.UnityFingerprints,
                pair.FbxFingerprints);
            LogTiming("FbxAsset", unityRendererPath, "Match Unity vertices to FBX control points", stopwatch, ref lastLogMs,
                $"unresolved={unresolved}");

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"FBX asset mapping has {unresolved} Unity vertices without matching FBX control point positions.";

            LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
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

        private static Dictionary<string, UnityBlendShapeData> BuildUnityBlendShapeLookup(
            MappingUnityBlendShapeCache[] unityBlendShapes)
        {
            var lookup = new Dictionary<string, UnityBlendShapeData>();
            if (unityBlendShapes == null) return lookup;

            for (int i = 0; i < unityBlendShapes.Length; i++)
            {
                var entry = unityBlendShapes[i];
                if (entry?.m_UnityBlendShapeData == null || string.IsNullOrEmpty(entry.m_Name)) continue;
                if (!lookup.ContainsKey(entry.m_Name))
                {
                    lookup[entry.m_Name] = entry.m_UnityBlendShapeData;
                }
            }

            return lookup;
        }

        private static bool HasUnityMesh(Mesh mesh, int requiredVertexCount)
        {
            return mesh != null && requiredVertexCount > 0 && mesh.vertexCount == requiredVertexCount;
        }

        private static string[] BuildFbxReadCandidates(string unityRendererPath, Mesh unityMesh)
        {
            var candidates = new List<string>(2);
            var seen = new HashSet<string>();
            AddFbxReadCandidate(candidates, seen, unityRendererPath);
            AddFbxReadCandidate(candidates, seen, unityMesh?.name);
            return candidates.ToArray();
        }

        private static void AddFbxReadCandidate(List<string> candidates, HashSet<string> seen, string candidate)
        {
            if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        private static bool TryGetFbxMesh(
            IReadOnlyDictionary<string, FbxMeshSnapshot> fbxMeshes,
            IReadOnlyList<string> candidates,
            out FbxMeshSnapshot fbxMesh)
        {
            fbxMesh = null;
            if (fbxMeshes == null || candidates == null) return false;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i]) && fbxMeshes.TryGetValue(candidates[i], out fbxMesh))
                {
                    return true;
                }
            }

            return false;
        }

        private static BlendShapeRecord[] ToArray(IEnumerable<BlendShapeRecord> source)
        {
            if (source == null) return System.Array.Empty<BlendShapeRecord>();
            var list = new List<BlendShapeRecord>(source);
            return list.ToArray();
        }

        private static MappingUnityBlendShapeCache[] ToArray(IEnumerable<MappingUnityBlendShapeCache> source)
        {
            if (source == null) return System.Array.Empty<MappingUnityBlendShapeCache>();
            var list = new List<MappingUnityBlendShapeCache>(source);
            return list.ToArray();
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
            bool isValid,
            string invalidReason)
        {
            string status = isValid ? "valid" : $"invalid: {invalidReason}";
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
