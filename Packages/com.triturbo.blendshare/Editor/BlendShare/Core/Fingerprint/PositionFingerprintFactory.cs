using System.Collections.Generic;
using System.Threading.Tasks;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEngine;
using UnityBlendShapeData = Triturbo.BlendShapeShare.BlendShapeData.UnityBlendShapeData;
using UnityBlendShapeFrame = Triturbo.BlendShapeShare.BlendShapeData.UnityBlendShapeFrame;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Paired FBX and Unity fingerprints plus the blendshape names used to build them.
    /// </summary>
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
            BlendShapeNames != null;
    }

    /// <summary>
    /// Creates FBX and Unity position fingerprints used by vertex mapping.
    /// </summary>
    internal static class PositionFingerprintFactory
    {
        private const int ParallelThreshold = 1024;

        // ─── Primary entry point ───────────────────────────────────────────────────

        /// <summary>
        /// Builds paired FBX and Unity fingerprints from a matched mesh pair.
        /// Blendshape sequences are paired by index order (FBX asset path).
        /// Returns an empty/invalid pair when either mesh is null or no shared blendshapes exist.
        /// </summary>
        public static FingerprintPair CreatePair(UfbxMesh fbxMesh, Mesh unityMesh, Matrix4x4 fbxToUnity)
        {
            var fbxBlendShapes = BuildFbxBlendShapeSequence(
                fbxMesh,
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

            if (pairedNames.Length == 0 &&
                ((fbxBlendShapes?.Length ?? 0) > 0 || (unityBlendShapes?.Length ?? 0) > 0))
            {
                return default;
            }

            return new FingerprintPair(
                CreateFromFbx(fbxMesh, fbxPaired, fbxToUnity),
                CreateFromUnity(unityMesh, unityPaired),
                pairedNames);
        }

        // ─── FBX / Unity fingerprint creation ─────────────────────────────────────
        public static PositionFingerprint[] CreateFromFbx(
            UfbxMesh mesh,
            FbxBlendShapeData[] blendShapes,
            Matrix4x4 fbxToUnity)
        {
            if (mesh == null)
            {
                return System.Array.Empty<PositionFingerprint>();
            }

            return CreateFromFbxPositions(
                mesh.GetVertices(),
                blendShapes,
                fbxToUnity);
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

        // ─── Blendshape sequence builders ─────────────────────────────────────────

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
        /// Converts FBX blend shape channels to FbxBlendShapeData, filtering out entries
        /// without frames or deltas.
        /// </summary>
        internal static FbxBlendShapeData[] BuildFbxBlendShapeSequence(
            UfbxMesh mesh,
            out string[] blendShapeNames)
        {
            if (mesh == null)
            {
                blendShapeNames = System.Array.Empty<string>();
                return System.Array.Empty<FbxBlendShapeData>();
            }

            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();

            foreach (var deformer in mesh.BlendDeformers)
            {
                foreach (var channel in deformer.Channels)
                {
                    if (channel == null || string.IsNullOrEmpty(channel.Name)) continue;

                    var data = CreateFbxBlendShapeData(channel);
                    if (!HasSamples(data) || !HasAnyDelta(data)) continue;

                    names.Add(channel.Name);
                    fbxBlendShapes.Add(data);
                }
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
                if ((frames[i]?.StoredDeltaCount ?? 0) > 0) return true;
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
        internal static FbxBlendShapeData CreateFbxBlendShapeData(UfbxBlendChannel channel)
        {
            var shapes = channel?.BlendShapes ?? System.Array.Empty<UfbxBlendShape>();
            var frames = new FbxBlendShapeFrame[shapes.Count];
            for (int i = 0; i < shapes.Count; i++)
            {
                frames[i] = CreateFbxBlendShapeFrame(shapes[i]);
            }
            return new FbxBlendShapeData(frames);
        }

        internal static FbxBlendShapeFrame CreateFbxBlendShapeFrame(UfbxBlendShape shape)
        {
            var frame = new FbxBlendShapeFrame((float)(shape?.Weight ?? 100.0));
            if (shape == null || shape.OffsetCount <= 0)
            {
                return frame;
            }

            var indices = new int[shape.OffsetCount];
            var values = new double[shape.OffsetCount * 3];
            var normals = new double[shape.OffsetCount * 3];
            if (shape.CopyOffsets(indices, values, normals) == 0)
            {
                return frame;
            }

            var deltas = FbxArrayUtility.ToVector3dArray(values);
            var normalDeltas = FbxArrayUtility.ToVector3dArray(normals);
            int count = System.Math.Min(indices.Length, System.Math.Min(deltas.Length, normalDeltas.Length));
            for (int i = 0; i < count; i++)
            {
                if (!deltas[i].IsZero())
                {
                    frame.SetDeltaPositionAt(indices[i], deltas[i]);
                }

                if (!normalDeltas[i].IsZero())
                {
                    frame.SetDeltaNormalAt(indices[i], normalDeltas[i]);
                }
            }
            return frame;
        }

        // ─── Private helpers ───────────────────────────────────────────────────────

        private static PositionFingerprint[] CreateFromFbxPositions(
            IReadOnlyList<Vector3d> basePositions,
            FbxBlendShapeData[] blendShapes,
            Matrix4x4 fbxToUnity)
        {
            int controlPointCount = basePositions?.Count ?? 0;
            var fingerprints = CreateFingerprints(
                controlPointCount,
                CountSamplesFromFbx(blendShapes),
                basePositions,
                fbxToUnity);

            int sampleIndex = 0;
            foreach (var blendShape in blendShapes ?? System.Array.Empty<FbxBlendShapeData>())
            {
                TryGetLastFrame(blendShape, out var frame);
                int currentSampleIndex = sampleIndex++;

                if (controlPointCount >= ParallelThreshold)
                {
                    // Pre-warm sparse lookup before parallel reads.
                    frame?.GetDeltaPositionAt(0);

                    Parallel.For(0, controlPointCount, fbxIndex =>
                    {
                        fingerprints[fbxIndex].SetRelativeSample(
                            currentSampleIndex,
                            fbxToUnity.MultiplyVector(ToVector3(frame?.GetDeltaPositionAt(fbxIndex) ?? Vector3d.zero)));
                    });
                }
                else
                {
                    for (int fbxIndex = 0; fbxIndex < controlPointCount; fbxIndex++)
                    {
                        fingerprints[fbxIndex].SetRelativeSample(
                            currentSampleIndex,
                            fbxToUnity.MultiplyVector(ToVector3(frame?.GetDeltaPositionAt(fbxIndex) ?? Vector3d.zero)));
                    }
                }
            }

            return fingerprints;
        }

        private static PositionFingerprint[] CreateFingerprints(
            int vertexCount,
            int sampleCount,
            IReadOnlyList<Vector3d> basePositions,
            Matrix4x4 fbxToUnity)
        {
            int count = System.Math.Max(0, vertexCount);
            var fingerprints = new PositionFingerprint[count];

            if (count >= ParallelThreshold)
            {
                Parallel.For(0, count, i =>
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Count
                        ? fbxToUnity.MultiplyPoint3x4(ToVector3(basePositions[i]))
                        : Vector3.zero;
                    fingerprints[i] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
                });
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 basePosition = basePositions != null && i < basePositions.Count
                        ? fbxToUnity.MultiplyPoint3x4(ToVector3(basePositions[i]))
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
