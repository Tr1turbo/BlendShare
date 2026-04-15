using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Triturbo.BlendShapeShare.FbxReader;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    // Threshold above which fingerprint arrays are built in parallel.
    // Parallel.For overhead is not worth it for small meshes.
    internal readonly struct FingerprintPair
    {
        public readonly PositionFingerprint[] FbxFingerprints;
        public readonly PositionFingerprint[] UnityFingerprints;
        public readonly string[] BlendShapeNames;

        public FingerprintPair(
            PositionFingerprint[] fbx,
            PositionFingerprint[] unity,
            string[] names)
        {
            FbxFingerprints = fbx;
            UnityFingerprints = unity;
            BlendShapeNames = names;
        }

        public bool IsValid =>
            FbxFingerprints != null && FbxFingerprints.Length > 0 &&
            UnityFingerprints != null && UnityFingerprints.Length > 0 &&
            BlendShapeNames != null && BlendShapeNames.Length > 0;
    }

    internal static class PositionFingerprintFactory
    {
        private const int ParallelThreshold = 1024;

        // ─── Primary entry point ───────────────────────────────────────────────────

        /// <summary>
        /// Builds paired FBX and Unity fingerprints from a matched mesh pair.
        /// Blendshape sequences are paired by index order (FBX asset path).
        /// Returns an empty/invalid pair when either mesh is null or no shared blendshapes exist.
        /// </summary>
        public static FingerprintPair CreatePair(FbxMeshSnapshot fbxMesh, Mesh unityMesh)
        {
            var fbxBlendShapes = BuildFbxBlendShapeSequence(
                fbxMesh?.BlendShapes,
                out var fbxNames);

            var unityBlendShapes = BuildUnityBlendShapeSequence(
                unityMesh,
                out var unityNames);

            PairBlendShapeSequences(
                fbxBlendShapes, unityBlendShapes,
                fbxNames, unityNames,
                out var pairedNames,
                out var fbxPaired,
                out var unityPaired);

            if (pairedNames.Length == 0)
            {
                return default;
            }

            return new FingerprintPair(
                CreateFromFbx(fbxMesh, fbxPaired),
                CreateFromUnity(unityMesh, unityPaired),
                pairedNames);
        }

        // ─── FBX / Unity fingerprint creation ─────────────────────────────────────

        public static PositionFingerprint[] CreateFromFbx(
            FbxMeshSnapshot mesh,
            FbxBlendShapeData[] blendShapes)
        {
            if (mesh == null)
            {
                return System.Array.Empty<PositionFingerprint>();
            }

            return CreateFromFbxPositions(
                mesh.ControlPointPositions,
                blendShapes,
                mesh.ImportScale);
        }

        public static PositionFingerprint[] CreateFromUnity(
            Mesh mesh,
            UnityBlendShapeData[] blendShapes)
        {
            if (mesh == null)
            {
                return System.Array.Empty<PositionFingerprint>();
            }

            int vertexCount = mesh.vertexCount;
            var vertices = mesh.vertices;
            var fingerprints = CreateFingerprints(vertexCount, CountSamples(blendShapes), vertices, 1f);

            int sampleIndex = 0;
            foreach (var blendShape in blendShapes ?? System.Array.Empty<UnityBlendShapeData>())
            {
                TryGetLastFrame(blendShape, out var frame);
                int currentSampleIndex = sampleIndex++;
                var deltas = frame?.GetDeltaVertices(vertexCount);

                if (vertexCount >= ParallelThreshold)
                {
                    Parallel.For(0, vertexCount, i =>
                    {
                        fingerprints[i].SetRelativeSample(
                            currentSampleIndex,
                            deltas != null && i < deltas.Length ? deltas[i] : Vector3.zero);
                    });
                }
                else
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        fingerprints[i].SetRelativeSample(
                            currentSampleIndex,
                            deltas != null && i < deltas.Length ? deltas[i] : Vector3.zero);
                    }
                }
            }

            return fingerprints;
        }

        // ─── Blendshape sequence builders (used by both FbxAsset and Legacy paths) ─

        /// <summary>
        /// Extracts blendshapes from a Unity mesh that have at least one non-zero delta.
        /// </summary>
        internal static UnityBlendShapeData[] BuildUnityBlendShapeSequence(
            Mesh unityMesh,
            out string[] blendShapeNames)
        {
            if (unityMesh == null)
            {
                blendShapeNames = System.Array.Empty<string>();
                return System.Array.Empty<UnityBlendShapeData>();
            }

            var names = new List<string>(unityMesh.blendShapeCount);
            var blendShapes = new List<UnityBlendShapeData>(unityMesh.blendShapeCount);

            for (int i = 0; i < unityMesh.blendShapeCount; i++)
            {
                var blendShapeData = new UnityBlendShapeData(unityMesh, i);
                if (!HasSamples(blendShapeData) || !HasAnyDelta(blendShapeData, unityMesh.vertexCount))
                {
                    continue;
                }

                names.Add(unityMesh.GetBlendShapeName(i));
                blendShapes.Add(blendShapeData);
            }

            blendShapeNames = names.ToArray();
            return blendShapes.ToArray();
        }

        /// <summary>
        /// Converts FBX blend shape snapshots to FbxBlendShapeData, filtering out entries
        /// without frames or deltas.
        /// </summary>
        internal static FbxBlendShapeData[] BuildFbxBlendShapeSequence(
            IEnumerable<FbxBlendShapeSnapshot> snapshots,
            out string[] blendShapeNames)
        {
            if (snapshots == null)
            {
                blendShapeNames = System.Array.Empty<string>();
                return System.Array.Empty<FbxBlendShapeData>();
            }

            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();

            foreach (var snapshot in snapshots)
            {
                if (snapshot == null || string.IsNullOrEmpty(snapshot.BlendShapeName)) continue;

                var data = CreateFbxBlendShapeData(snapshot);
                if (!HasSamples(data) || !HasAnyDelta(data)) continue;

                names.Add(snapshot.BlendShapeName);
                fbxBlendShapes.Add(data);
            }

            blendShapeNames = names.ToArray();
            return fbxBlendShapes.ToArray();
        }

        /// <summary>
        /// Zips FBX and Unity blendshape sequences by index (FBX asset path).
        /// Truncates to the shorter list.
        /// </summary>
        internal static void PairBlendShapeSequences(
            IReadOnlyList<FbxBlendShapeData> fbxBlendShapes,
            IReadOnlyList<UnityBlendShapeData> unityBlendShapes,
            IReadOnlyList<string> fbxNames,
            IReadOnlyList<string> unityNames,
            out string[] blendShapeSequence,
            out FbxBlendShapeData[] fbxSequence,
            out UnityBlendShapeData[] unitySequence)
        {
            int count = System.Math.Min(
                fbxBlendShapes?.Count ?? 0,
                unityBlendShapes?.Count ?? 0);

            blendShapeSequence = new string[count];
            fbxSequence = new FbxBlendShapeData[count];
            unitySequence = new UnityBlendShapeData[count];

            for (int i = 0; i < count; i++)
            {
                blendShapeSequence[i] = fbxNames != null && i < fbxNames.Count
                    ? fbxNames[i]
                    : unityNames != null && i < unityNames.Count
                        ? unityNames[i]
                        : $"BlendShape_{i}";
                fbxSequence[i] = fbxBlendShapes[i];
                unitySequence[i] = unityBlendShapes[i];
            }
        }

        /// <summary>
        /// Pairs FBX and Unity blendshapes by name (Legacy upgrade path).
        /// Only pairs where both sides have frames are included.
        /// </summary>
        internal static void BuildBlendShapeSequencesByName(
            IReadOnlyList<BlendShapeRecord> records,
            IReadOnlyDictionary<string, UnityBlendShapeData> unityBlendShapesByName,
            out string[] blendShapeSequence,
            out FbxBlendShapeData[] fbxSequence,
            out UnityBlendShapeData[] unitySequence)
        {
            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();
            var unityBlendShapes = new List<UnityBlendShapeData>();

            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (record == null ||
                        string.IsNullOrEmpty(record.m_Name) ||
                        record.m_FbxBlendShapeData == null ||
                        unityBlendShapesByName == null ||
                        !unityBlendShapesByName.TryGetValue(record.m_Name, out var unityData) ||
                        unityData == null ||
                        !HasSamples(record.m_FbxBlendShapeData) ||
                        !HasSamples(unityData))
                    {
                        continue;
                    }

                    names.Add(record.m_Name);
                    fbxBlendShapes.Add(record.m_FbxBlendShapeData);
                    unityBlendShapes.Add(unityData);
                }
            }

            blendShapeSequence = names.ToArray();
            fbxSequence = fbxBlendShapes.ToArray();
            unitySequence = unityBlendShapes.ToArray();
        }

        // ─── Sample counting and validation helpers ────────────────────────────────

        public static int CountSamples(UnityBlendShapeData[] blendShapes)
        {
            return blendShapes?.Length ?? 0;
        }

        public static bool HasSamples(FbxBlendShapeData blendShapeData)
        {
            return TryGetLastFrame(blendShapeData, out FbxBlendShapeFrame _);
        }

        public static bool HasSamples(UnityBlendShapeData blendShapeData)
        {
            return TryGetLastFrame(blendShapeData, out UnityBlendShapeFrame _);
        }

        internal static bool HasAnyDelta(FbxBlendShapeData blendShapeData)
        {
            var frames = blendShapeData?.m_Frames;
            if (frames == null) return false;
            for (int i = 0; i < frames.Length; i++)
            {
                if ((frames[i]?.m_PointsIndices?.Count ?? 0) > 0) return true;
            }
            return false;
        }

        internal static bool HasAnyDelta(UnityBlendShapeData blendShapeData, int vertexCount)
        {
            var frames = blendShapeData?.m_Frames;
            if (frames == null) return false;
            for (int fi = 0; fi < frames.Length; fi++)
            {
                var deltaVertices = frames[fi]?.GetDeltaVertices(vertexCount);
                if (deltaVertices == null) continue;
                for (int i = 0; i < deltaVertices.Length; i++)
                {
                    if (deltaVertices[i] != Vector3.zero) return true;
                }
            }
            return false;
        }

        // ─── FBX blendshape data construction ─────────────────────────────────────

        internal static FbxBlendShapeData CreateFbxBlendShapeData(FbxBlendShapeSnapshot snapshot)
        {
            var snapshotFrames = snapshot?.Frames ?? System.Array.Empty<FbxBlendShapeFrameSnapshot>();
            var frames = new FbxBlendShapeFrame[snapshotFrames.Length];
            for (int i = 0; i < snapshotFrames.Length; i++)
            {
                frames[i] = CreateFbxBlendShapeFrame(snapshotFrames[i]);
            }
            return new FbxBlendShapeData(frames);
        }

        internal static FbxBlendShapeFrame CreateFbxBlendShapeFrame(FbxBlendShapeFrameSnapshot snapshotFrame)
        {
            var frame = new FbxBlendShapeFrame();
            var indices = snapshotFrame?.ControlPointIndices ?? System.Array.Empty<int>();
            var deltas = snapshotFrame?.ControlPointDeltas ?? System.Array.Empty<Vector3d>();
            int count = System.Math.Min(indices.Length, deltas.Length);
            for (int i = 0; i < count; i++)
            {
                if (!deltas[i].IsZero())
                {
                    frame.AddDeltaControlPointAt(deltas[i], indices[i]);
                }
            }
            return frame;
        }

        // ─── Private helpers ───────────────────────────────────────────────────────

        private static PositionFingerprint[] CreateFromFbxPositions(
            Vector3d[] basePositions,
            FbxBlendShapeData[] blendShapes,
            float importScale)
        {
            int controlPointCount = basePositions?.Length ?? 0;
            var fingerprints = CreateFingerprints(
                controlPointCount,
                CountSamplesFromFbx(blendShapes),
                basePositions,
                importScale);

            int sampleIndex = 0;
            foreach (var blendShape in blendShapes ?? System.Array.Empty<FbxBlendShapeData>())
            {
                TryGetLastFrame(blendShape, out var frame);
                int currentSampleIndex = sampleIndex++;

                if (controlPointCount >= ParallelThreshold)
                {
                    // Pre-warm the frame's lazy dictionary on this thread before parallel access.
                    // GetDeltaControlPointAt has a non-thread-safe lazy init; once built, reads are safe.
                    frame?.GetDeltaControlPointAt(0);

                    Parallel.For(0, controlPointCount, fbxIndex =>
                    {
                        fingerprints[fbxIndex].SetRelativeSample(
                            currentSampleIndex,
                            ToVector3(frame?.GetDeltaControlPointAt(fbxIndex) ?? Vector3d.zero) * importScale);
                    });
                }
                else
                {
                    for (int fbxIndex = 0; fbxIndex < controlPointCount; fbxIndex++)
                    {
                        fingerprints[fbxIndex].SetRelativeSample(
                            currentSampleIndex,
                            ToVector3(frame?.GetDeltaControlPointAt(fbxIndex) ?? Vector3d.zero) * importScale);
                    }
                }
            }

            return fingerprints;
        }

        private static PositionFingerprint[] CreateFingerprints(
            int vertexCount,
            int sampleCount,
            Vector3d[] basePositions,
            float basePositionScale)
        {
            int count = System.Math.Max(0, vertexCount);
            var fingerprints = new PositionFingerprint[count];

            if (count >= ParallelThreshold)
            {
                Parallel.For(0, count, i =>
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Length
                        ? ToVector3(basePositions[i]) * basePositionScale
                        : Vector3.zero;
                    fingerprints[i] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Length
                        ? ToVector3(basePositions[i]) * basePositionScale
                        : Vector3.zero;
                    fingerprints[i] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
                }
            }

            return fingerprints;
        }

        private static PositionFingerprint[] CreateFingerprints(
            int vertexCount,
            int sampleCount,
            Vector3[] basePositions,
            float basePositionScale)
        {
            int count = System.Math.Max(0, vertexCount);
            var fingerprints = new PositionFingerprint[count];

            if (count >= ParallelThreshold)
            {
                Parallel.For(0, count, i =>
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Length
                        ? basePositions[i] * basePositionScale
                        : Vector3.zero;
                    fingerprints[i] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Length
                        ? basePositions[i] * basePositionScale
                        : Vector3.zero;
                    fingerprints[i] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
                }
            }

            return fingerprints;
        }

        private static bool TryGetLastFrame(FbxBlendShapeData blendShapeData, out FbxBlendShapeFrame frame)
        {
            var frames = blendShapeData?.m_Frames;
            frame = frames != null && frames.Length > 0 ? frames[frames.Length - 1] : null;
            return frame != null;
        }

        private static bool TryGetLastFrame(UnityBlendShapeData blendShapeData, out UnityBlendShapeFrame frame)
        {
            var frames = blendShapeData?.m_Frames;
            frame = frames != null && frames.Length > 0 ? frames[frames.Length - 1] : null;
            return frame != null;
        }

        private static int CountSamplesFromFbx(FbxBlendShapeData[] blendShapes)
        {
            return blendShapes?.Length ?? 0;
        }

        private static Vector3 ToVector3(Vector3d value)
        {
            return new Vector3((float)value.x, (float)value.y, (float)value.z);
        }
    }
}
