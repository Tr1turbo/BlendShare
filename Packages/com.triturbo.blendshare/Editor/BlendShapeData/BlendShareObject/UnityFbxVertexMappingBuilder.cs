using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;


using Triturbo.BlendShapeShare.FbxReader;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-5f;
        private const string LogPrefix = "[BlendShare Vertex Mapping]";

        public static UnityVertexMappingObject BuildFromLegacy(
            string unityRendererPath,
            int unityVertexCount,
            int unityVerticesHash,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes)
        {
            return BuildFromLegacy(
                unityRendererPath,
                unityVertexCount,
                unityVerticesHash,
                fbxBlendShapes,
                unityBlendShapes,
                null,
                null);
        }

        internal static UnityVertexMappingObject BuildFromLegacy(
            string unityRendererPath,
            int unityVertexCount,
            int unityVerticesHash,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes,
            Mesh unityMesh = null,
            FbxMeshSnapshot fbxMesh = null)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMilliseconds = 0d;
            var records = fbxBlendShapes?.ToArray()
                          ?? System.Array.Empty<BlendShapeRecord>();
            var unityCache = unityBlendShapes?.ToArray()
                             ?? System.Array.Empty<MappingUnityBlendShapeCache>();
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Collect input arrays",
                stopwatch,
                ref lastLogMilliseconds,
                $"fbxBlendShapes={records.Length}, unityBlendShapes={unityCache.Length}");

            var unityBlendShapesByName = BuildUnityBlendShapeLookup(unityCache);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Build Unity blendshape lookup",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityBlendShapeNames={unityBlendShapesByName.Count}");

            BuildBlendShapeDataSequences(
                records,
                unityBlendShapesByName,
                out var blendShapeSequence,
                out var fbxBlendShapeSequence,
                out var unityBlendShapeSequence);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Build blendshape sequences",
                stopwatch,
                ref lastLogMilliseconds,
                $"usableBlendShapes={blendShapeSequence.Length}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityVertexCount = unityVertexCount;
            mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(unityMesh);
            mapping.m_FbxToUnityScale = fbxMesh?.ImportScale ?? 1f;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.LegacyUpgrade;
            mapping.m_SourceBlendShapeNames = blendShapeSequence;
            mapping.m_Indices = Enumerable.Repeat(-1, Mathf.Max(0, unityVertexCount)).ToArray();
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Create mapping object",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityVertices={mapping.m_Indices.Length}");

            if (blendShapeSequence.Length == 0 || unityBlendShapesByName.Count == 0 || unityVertexCount <= 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires both FBX and Unity blendshape data.";
                mapping.SetLegacyCache(unityCache);
                LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            int sampleCount = PositionFingerprintFactory.CountSamples(unityBlendShapeSequence);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Count fingerprint samples",
                stopwatch,
                ref lastLogMilliseconds,
                $"samples={sampleCount}");
            if (sampleCount == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires matching FBX and Unity blendshape frames.";
                mapping.SetLegacyCache(unityCache);
                LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (!HasUnityMesh(unityMesh, unityVertexCount) || !HasFbxMesh(fbxMesh))
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires inferable Unity and FBX base positions.";
                mapping.SetLegacyCache(unityCache);
                LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Validate source meshes",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityMeshVertices={unityMesh.vertexCount}, fbxControlPoints={fbxMesh.ControlPointCount}");

            var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(
                fbxMesh,
                fbxBlendShapeSequence);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Create FBX fingerprints",
                stopwatch,
                ref lastLogMilliseconds,
                $"fingerprints={fbxFingerprints.Length}");

            var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(
                unityMesh,
                unityBlendShapeSequence);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Create Unity fingerprints",
                stopwatch,
                ref lastLogMilliseconds,
                $"fingerprints={unityFingerprints.Length}");

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_Indices,
                unityFingerprints,
                fbxFingerprints);
            LogTiming(
                "Legacy",
                unityRendererPath,
                "Match Unity vertices to FBX vertices",
                stopwatch,
                ref lastLogMilliseconds,
                $"unresolved={unresolved}");

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"Legacy mapping has {unresolved} unresolved Unity vertices.";

            if (!mapping.m_IsValid)
            {
                mapping.SetLegacyCache(unityCache);
                LogTiming(
                    "Legacy",
                    unityRendererPath,
                    "Store legacy fallback cache",
                    stopwatch,
                    ref lastLogMilliseconds,
                    $"fallbackBlendShapes={unityCache.Length}");
            }

            LogCompletion("Legacy", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
            return mapping;
        }

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
            double lastLogMilliseconds = 0d;

            var readCandidates = BuildFbxReadCandidates(unityRendererPath, unityMesh);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Collect FBX read candidates",
                stopwatch,
                ref lastLogMilliseconds,
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
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Read FBX control point positions and blendshapes",
                stopwatch,
                ref lastLogMilliseconds,
                $"found={(fbxMesh != null ? 1 : 0)}, controlPoints={fbxMesh?.ControlPointCount ?? 0}, blendShapes={fbxMesh?.BlendShapes?.Length ?? 0}");

            var unityMeshBlendShapes = BuildUnityMeshBlendShapeSequence(unityMesh, out var unityMeshBlendShapeNames);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Build Unity mesh blendshape sequence",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityBlendShapes={unityMeshBlendShapes.Length}");

            var fbxMeshBlendShapes = BuildFbxBlendShapeSequence(
                fbxMesh?.BlendShapes,
                out var fbxMeshBlendShapeNames);
            PairBlendShapeDataSequences(
                fbxMeshBlendShapes,
                unityMeshBlendShapes,
                fbxMeshBlendShapeNames,
                unityMeshBlendShapeNames,
                out var blendShapeSequence,
                out var fbxBlendShapeSequence,
                out var unityBlendShapeSequence);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Build blendshape sequences",
                stopwatch,
                ref lastLogMilliseconds,
                $"fbxBlendShapes={fbxMeshBlendShapes.Length}, unityBlendShapes={unityMeshBlendShapes.Length}, usableBlendShapes={blendShapeSequence.Length}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(unityMesh);
            mapping.m_FbxToUnityScale = fbxMesh?.ImportScale ?? 1f;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.FbxAsset;
            mapping.m_SourceBlendShapeNames = blendShapeSequence;
            mapping.m_Indices = Enumerable.Repeat(-1, Mathf.Max(0, mapping.m_UnityVertexCount)).ToArray();
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Create mapping object",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityVertices={mapping.m_Indices.Length}");

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

            if (!HasFbxMesh(fbxMesh))
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping could not read matching FBX control point positions.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (blendShapeSequence.Length == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires matching FBX and Unity mesh blendshapes.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            int sampleCount = PositionFingerprintFactory.CountSamples(unityBlendShapeSequence);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Count fingerprint samples",
                stopwatch,
                ref lastLogMilliseconds,
                $"samples={sampleCount}");
            if (sampleCount == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires matching FBX and Unity mesh blendshape frames.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (mapping.m_UnityVertexCount <= 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires a Unity mesh with vertices.";
                LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(
                fbxMesh,
                fbxBlendShapeSequence);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Create FBX fingerprints",
                stopwatch,
                ref lastLogMilliseconds,
                $"fingerprints={fbxFingerprints.Length}");

            var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(
                unityMesh,
                unityBlendShapeSequence);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Create Unity fingerprints",
                stopwatch,
                ref lastLogMilliseconds,
                $"fingerprints={unityFingerprints.Length}");

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_Indices,
                unityFingerprints,
                fbxFingerprints);
            LogTiming(
                "FbxAsset",
                unityRendererPath,
                "Match Unity vertices to FBX control points",
                stopwatch,
                ref lastLogMilliseconds,
                $"unresolved={unresolved}");

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"FBX asset mapping has {unresolved} Unity vertices without matching FBX control point positions.";

            LogCompletion("FbxAsset", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
            return mapping;
        }

        private static Dictionary<string, UnityBlendShapeData> BuildUnityBlendShapeLookup(IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes)
        {
            return unityBlendShapes?
                .Where(blendShape => blendShape?.m_UnityBlendShapeData != null && !string.IsNullOrEmpty(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .ToDictionary(group => group.Key, group => group.First().m_UnityBlendShapeData)
                ?? new Dictionary<string, UnityBlendShapeData>();
        }

        private static UnityBlendShapeData[] BuildUnityMeshBlendShapeSequence(Mesh unityMesh, out string[] blendShapeNames)
        {
            var blendShapeNamesList = new List<string>();
            var blendShapes = new List<UnityBlendShapeData>();
            if (unityMesh == null)
            {
                blendShapeNames = System.Array.Empty<string>();
                return System.Array.Empty<UnityBlendShapeData>();
            }

            for (int blendShapeIndex = 0; blendShapeIndex < unityMesh.blendShapeCount; blendShapeIndex++)
            {
                var blendShapeData = new UnityBlendShapeData(unityMesh, blendShapeIndex);
                if (!PositionFingerprintFactory.HasSamples(blendShapeData) ||
                    !HasAnyDelta(blendShapeData, unityMesh.vertexCount))
                {
                    continue;
                }

                blendShapeNamesList.Add(unityMesh.GetBlendShapeName(blendShapeIndex));
                blendShapes.Add(blendShapeData);
            }

            blendShapeNames = blendShapeNamesList.ToArray();
            return blendShapes.ToArray();
        }

        private static void BuildBlendShapeDataSequences(
            IEnumerable<BlendShapeRecord> records,
            IReadOnlyDictionary<string, UnityBlendShapeData> unityBlendShapesByName,
            out string[] blendShapeSequence,
            out FbxBlendShapeData[] fbxBlendShapeSequence,
            out UnityBlendShapeData[] unityBlendShapeSequence)
        {
            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();
            var unityBlendShapes = new List<UnityBlendShapeData>();

            foreach (var record in records ?? Enumerable.Empty<BlendShapeRecord>())
            {
                if (record == null ||
                    string.IsNullOrEmpty(record.m_Name) ||
                    record.m_FbxBlendShapeData == null ||
                    unityBlendShapesByName == null ||
                    !unityBlendShapesByName.TryGetValue(record.m_Name, out var unityBlendShapeData) ||
                    unityBlendShapeData == null ||
                    !PositionFingerprintFactory.HasSamples(record.m_FbxBlendShapeData) ||
                    !PositionFingerprintFactory.HasSamples(unityBlendShapeData))
                {
                    continue;
                }

                names.Add(record.m_Name);
                fbxBlendShapes.Add(record.m_FbxBlendShapeData);
                unityBlendShapes.Add(unityBlendShapeData);
            }

            blendShapeSequence = names.ToArray();
            fbxBlendShapeSequence = fbxBlendShapes.ToArray();
            unityBlendShapeSequence = unityBlendShapes.ToArray();
        }

        private static FbxBlendShapeData[] BuildFbxBlendShapeSequence(
            IEnumerable<FbxBlendShapeSnapshot> snapshots,
            out string[] blendShapeNames)
        {
            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();

            foreach (var snapshot in snapshots ?? Enumerable.Empty<FbxBlendShapeSnapshot>())
            {
                if (snapshot == null ||
                    string.IsNullOrEmpty(snapshot.BlendShapeName))
                {
                    continue;
                }

                var fbxBlendShapeData = CreateFbxBlendShapeData(snapshot);
                if (!PositionFingerprintFactory.HasSamples(fbxBlendShapeData) ||
                    !HasAnyDelta(fbxBlendShapeData))
                {
                    continue;
                }

                names.Add(snapshot.BlendShapeName);
                fbxBlendShapes.Add(fbxBlendShapeData);
            }

            blendShapeNames = names.ToArray();
            return fbxBlendShapes.ToArray();
        }

        private static void PairBlendShapeDataSequences(
            IReadOnlyList<FbxBlendShapeData> fbxBlendShapes,
            IReadOnlyList<UnityBlendShapeData> unityBlendShapes,
            IReadOnlyList<string> fbxBlendShapeNames,
            IReadOnlyList<string> unityBlendShapeNames,
            out string[] blendShapeSequence,
            out FbxBlendShapeData[] fbxBlendShapeSequence,
            out UnityBlendShapeData[] unityBlendShapeSequence)
        {
            int count = Mathf.Min(fbxBlendShapes?.Count ?? 0, unityBlendShapes?.Count ?? 0);
            var names = new string[count];
            var fbxSequence = new FbxBlendShapeData[count];
            var unitySequence = new UnityBlendShapeData[count];

            for (int i = 0; i < count; i++)
            {
                names[i] = fbxBlendShapeNames != null && i < fbxBlendShapeNames.Count
                    ? fbxBlendShapeNames[i]
                    : unityBlendShapeNames != null && i < unityBlendShapeNames.Count
                        ? unityBlendShapeNames[i]
                        : $"BlendShape_{i}";
                fbxSequence[i] = fbxBlendShapes[i];
                unitySequence[i] = unityBlendShapes[i];
            }

            blendShapeSequence = names;
            fbxBlendShapeSequence = fbxSequence;
            unityBlendShapeSequence = unitySequence;
        }

        private static FbxBlendShapeData CreateFbxBlendShapeData(FbxBlendShapeSnapshot snapshot)
        {
            var snapshotFrames = snapshot?.Frames ?? System.Array.Empty<FbxBlendShapeFrameSnapshot>();
            var frames = new FbxBlendShapeFrame[snapshotFrames.Length];

            for (int frameIndex = 0; frameIndex < snapshotFrames.Length; frameIndex++)
            {
                frames[frameIndex] = CreateFbxBlendShapeFrame(snapshotFrames[frameIndex]);
            }

            return new FbxBlendShapeData(frames);
        }

        private static FbxBlendShapeFrame CreateFbxBlendShapeFrame(FbxBlendShapeFrameSnapshot snapshotFrame)
        {
            var frame = new FbxBlendShapeFrame();
            var indices = snapshotFrame?.ControlPointIndices ?? System.Array.Empty<int>();
            var deltas = snapshotFrame?.ControlPointDeltas ?? System.Array.Empty<Vector3d>();
            int count = Mathf.Min(indices.Length, deltas.Length);

            for (int i = 0; i < count; i++)
            {
                if (!deltas[i].IsZero())
                {
                    frame.AddDeltaControlPointAt(deltas[i], indices[i]);
                }
            }

            return frame;
        }

        private static bool HasAnyDelta(FbxBlendShapeData blendShapeData)
        {
            foreach (var frame in blendShapeData?.m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
            {
                if ((frame?.m_PointsIndices?.Count ?? 0) > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyDelta(UnityBlendShapeData blendShapeData, int vertexCount)
        {
            foreach (var frame in blendShapeData?.m_Frames ?? System.Array.Empty<UnityBlendShapeFrame>())
            {
                var deltaVertices = frame?.GetDeltaVertices(vertexCount);
                if (deltaVertices == null)
                {
                    continue;
                }

                for (int i = 0; i < deltaVertices.Length; i++)
                {
                    if (deltaVertices[i] != Vector3.zero)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasUnityMesh(Mesh mesh, int requiredVertexCount)
        {
            return mesh != null && requiredVertexCount > 0 && mesh.vertexCount == requiredVertexCount;
        }

        private static bool HasFbxMesh(FbxMeshSnapshot mesh)
        {
            return mesh != null && mesh.ControlPointCount > 0;
        }

        private static int MapUnityVerticesByFingerprintMatch(
            int[] mappingIndices,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            IReadOnlyList<PositionFingerprint> fbxFingerprints)
        {
            var fbxFingerprintIndex = new PositionFingerprint.CandidateIndex(
                fbxFingerprints,
                FingerprintEpsilon);
            int unresolved = 0;
            for (int unityIndex = 0; unityIndex < unityFingerprints.Count; unityIndex++)
            {
                if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(
                        fbxFingerprintIndex,
                        out int fbxIndex))
                {
                    unresolved++;
                    continue;
                }

                mappingIndices[unityIndex] = fbxIndex;
            }

            return unresolved;
        }

        private static string[] BuildFbxReadCandidates(string unityRendererPath, Mesh unityMesh)
        {
            var candidates = new List<string>();
            var seenCandidates = new HashSet<string>();
            AddFbxReadCandidate(candidates, seenCandidates, unityRendererPath);
            AddFbxReadCandidate(candidates, seenCandidates, unityMesh != null ? unityMesh.name : null);
            return candidates.ToArray();
        }

        private static void AddFbxReadCandidate(List<string> candidates, HashSet<string> seenCandidates, string candidate)
        {
            if (!string.IsNullOrEmpty(candidate) && seenCandidates.Add(candidate))
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
            if (fbxMeshes == null || candidates == null)
            {
                return false;
            }

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && fbxMeshes.TryGetValue(candidate, out fbxMesh))
                {
                    return true;
                }
            }

            return false;
        }

        private static void LogTiming(
            string buildMode,
            string unityRendererPath,
            string section,
            Stopwatch stopwatch,
            ref double previousMilliseconds,
            string details = null)
        {
            double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            double sectionMilliseconds = elapsedMilliseconds - previousMilliseconds;
            previousMilliseconds = elapsedMilliseconds;
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': {section} took {sectionMilliseconds:0.###} ms (total {elapsedMilliseconds:0.###} ms){FormatLogDetails(details)}");
        }

        private static void LogCompletion(
            string buildMode,
            string unityRendererPath,
            Stopwatch stopwatch,
            bool isValid,
            string invalidReason)
        {
            string status = isValid
                ? "valid"
                : $"invalid: {invalidReason}";
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': Finished in {stopwatch.Elapsed.TotalMilliseconds:0.###} ms ({status})");
        }

        private static string FormatLogTarget(string unityRendererPath)
        {
            return string.IsNullOrEmpty(unityRendererPath) ? "<unknown>" : unityRendererPath;
        }

        private static string FormatLogDetails(string details)
        {
            return string.IsNullOrEmpty(details) ? string.Empty : $" ({details})";
        }

    }
}
