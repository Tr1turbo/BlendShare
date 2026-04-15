using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;


using Triturbo.BlendShapeShare.FbxReader;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-6f;
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
            mapping.m_UnityVerticesHash = unityVerticesHash;
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

#if ENABLE_FBX_SDK
        public static UnityVertexMappingObject BuildFromExtraction(
            string unityRendererPath,
            Mesh unityMesh,
            FbxMesh fbxMesh,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes,
            float fbxImportScale = 1f)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMilliseconds = 0d;
            var records = fbxBlendShapes?.ToArray()
                          ?? System.Array.Empty<BlendShapeRecord>();
            var unityCache = unityBlendShapes?.ToArray()
                             ?? System.Array.Empty<MappingUnityBlendShapeCache>();
            LogTiming(
                "Extraction",
                unityRendererPath,
                "Collect input arrays",
                stopwatch,
                ref lastLogMilliseconds,
                $"fbxBlendShapes={records.Length}, unityBlendShapes={unityCache.Length}");

            var unityBlendShapesByName = BuildUnityBlendShapeLookup(unityCache);
            LogTiming(
                "Extraction",
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
                "Extraction",
                unityRendererPath,
                "Build blendshape sequences",
                stopwatch,
                ref lastLogMilliseconds,
                $"usableBlendShapes={blendShapeSequence.Length}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVerticesHash = unityMesh != null ? MeshData.GetVerticesHash(unityMesh) : 0;
            mapping.m_FbxToUnityScale = fbxImportScale;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.Extraction;
            mapping.m_SourceBlendShapeNames = blendShapeSequence;
            mapping.m_Indices = Enumerable.Repeat(-1, Mathf.Max(0, mapping.m_UnityVertexCount)).ToArray();
            LogTiming(
                "Extraction",
                unityRendererPath,
                "Create mapping object",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityVertices={mapping.m_Indices.Length}");

            if (unityMesh == null || fbxMesh == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Extraction mapping requires Unity mesh and FBX mesh.";
                mapping.SetLegacyCache(unityCache);
                LogCompletion("Extraction", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }
            LogTiming(
                "Extraction",
                unityRendererPath,
                "Validate source meshes",
                stopwatch,
                ref lastLogMilliseconds,
                $"unityMeshVertices={unityMesh.vertexCount}, fbxControlPoints={fbxMesh.GetControlPointsCount()}");

            var fbxMeshSnapshot = CreateFbxMeshSnapshot(fbxMesh, fbxImportScale);
            LogTiming(
                "Extraction",
                unityRendererPath,
                "Create FBX mesh snapshot",
                stopwatch,
                ref lastLogMilliseconds,
                $"fbxControlPoints={fbxMeshSnapshot.ControlPointCount}");

            var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(
                fbxMeshSnapshot,
                fbxBlendShapeSequence);
            LogTiming(
                "Extraction",
                unityRendererPath,
                "Create FBX fingerprints",
                stopwatch,
                ref lastLogMilliseconds,
                $"fingerprints={fbxFingerprints.Length}");

            var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(
                unityMesh,
                unityBlendShapeSequence);
            LogTiming(
                "Extraction",
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
                "Extraction",
                unityRendererPath,
                "Match Unity vertices to FBX vertices",
                stopwatch,
                ref lastLogMilliseconds,
                $"unresolved={unresolved}");

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"Extraction mapping has {unresolved} unresolved Unity vertices.";

            if (!mapping.m_IsValid)
            {
                mapping.SetLegacyCache(unityCache);
                LogTiming(
                    "Extraction",
                    unityRendererPath,
                    "Store legacy fallback cache",
                    stopwatch,
                    ref lastLogMilliseconds,
                    $"fallbackBlendShapes={unityCache.Length}");
            }

            LogCompletion("Extraction", unityRendererPath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
            return mapping;
        }
#endif

        private static Dictionary<string, UnityBlendShapeData> BuildUnityBlendShapeLookup(IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes)
        {
            return unityBlendShapes?
                .Where(blendShape => blendShape?.m_UnityBlendShapeData != null && !string.IsNullOrEmpty(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .ToDictionary(group => group.Key, group => group.First().m_UnityBlendShapeData)
                ?? new Dictionary<string, UnityBlendShapeData>();
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

#if ENABLE_FBX_SDK
        private static FbxMeshSnapshot CreateFbxMeshSnapshot(FbxMesh fbxMesh, float importScale)
        {
            int controlPointCount = fbxMesh.GetControlPointsCount();
            var positions = new Vector3d[controlPointCount];
            for (int i = 0; i < controlPointCount; i++)
            {
                positions[i] = ToVector3d(fbxMesh.GetControlPointAt(i));
            }

            return new FbxMeshSnapshot(
                fbxMesh.GetName(),
                positions,
                CreateFbxBlendShapeSnapshots(fbxMesh, positions),
                importScale);
        }

        private static FbxBlendShapeSnapshot[] CreateFbxBlendShapeSnapshots(
            FbxMesh fbxMesh,
            Vector3d[] basePositions)
        {
            int controlPointCount = basePositions?.Length ?? 0;
            var snapshots = new List<FbxBlendShapeSnapshot>();
            for (int deformerIndex = 0;
                 deformerIndex < fbxMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
                 deformerIndex++)
            {
                var deformer = fbxMesh.GetBlendShapeDeformer(deformerIndex);
                if (deformer == null)
                {
                    continue;
                }

                for (int channelIndex = 0; channelIndex < deformer.GetBlendShapeChannelCount(); channelIndex++)
                {
                    var channel = deformer.GetBlendShapeChannel(channelIndex);
                    if (channel == null)
                    {
                        continue;
                    }

                    int frameCount = channel.GetTargetShapeCount();
                    var frames = new FbxBlendShapeFrameSnapshot[frameCount];
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        frames[frameIndex] = new FbxBlendShapeFrameSnapshot(
                            frameCount > 0 ? 100.0 * (frameIndex + 1) / frameCount : 0.0,
                            CreateControlPointIndices(channel.GetTargetShape(frameIndex), basePositions, controlPointCount),
                            CreateControlPointDeltas(channel.GetTargetShape(frameIndex), basePositions, controlPointCount));
                    }

                    snapshots.Add(new FbxBlendShapeSnapshot(
                        deformer.GetName(),
                        channel.GetName(),
                        frames));
                }
            }

            return snapshots.ToArray();
        }

        private static Vector3d[] CreateControlPointDeltas(
            FbxShape targetShape,
            Vector3d[] basePositions,
            int controlPointCount)
        {
            var deltas = new List<Vector3d>(controlPointCount);
            if (targetShape == null || basePositions == null)
            {
                return deltas.ToArray();
            }

            int count = Mathf.Min(controlPointCount, targetShape.GetControlPointsCount());
            for (int i = 0; i < count; i++)
            {
                var delta = ToVector3d(targetShape.GetControlPointAt(i)) - basePositions[i];
                if (!delta.IsZero())
                {
                    deltas.Add(delta);
                }
            }

            return deltas.ToArray();
        }

        private static int[] CreateControlPointIndices(
            FbxShape targetShape,
            Vector3d[] basePositions,
            int controlPointCount)
        {
            var indices = new List<int>(controlPointCount);
            if (targetShape == null || basePositions == null)
            {
                return indices.ToArray();
            }

            int count = Mathf.Min(controlPointCount, targetShape.GetControlPointsCount());
            for (int i = 0; i < count; i++)
            {
                if (!ToVector3d(targetShape.GetControlPointAt(i)).Equals(basePositions[i]))
                {
                    indices.Add(i);
                }
            }

            return indices.ToArray();
        }

        private static Vector3d ToVector3d(FbxVector4 value)
        {
            return new Vector3d(value.X, value.Y, value.Z);
        }
#endif

    }
}
