using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal static class PositionFingerprintFactory
    {
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

        private static PositionFingerprint[] CreateFromFbxPositions(
            Vector3[] basePositions,
            FbxBlendShapeData[] blendShapes,
            float importScale)
        {
            int controlPointCount = basePositions?.Length ?? 0;
            var fingerprints = CreateFingerprints(
                controlPointCount,
                CountSamples(blendShapes),
                basePositions,
                importScale);
            int sampleIndex = 0;

            foreach (var blendShape in blendShapes ?? System.Array.Empty<FbxBlendShapeData>())
            {
                TryGetLastFrame(blendShape, out var frame);
                int currentSampleIndex = sampleIndex++;
                for (int fbxIndex = 0; fbxIndex < controlPointCount; fbxIndex++)
                {
                    fingerprints[fbxIndex].SetRelativeSample(
                        currentSampleIndex,
                       ToVector3(frame?.GetDeltaControlPointAt(fbxIndex) ?? Vector4d.zero) * importScale);
                }
            }

            return fingerprints;
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
            var fingerprints = CreateFingerprints(vertexCount, CountSamples(blendShapes), mesh.vertices, 1f);
            int sampleIndex = 0;
            List<Vector3> vertices = new List<Vector3>();
            mesh.GetVertices(vertices);

            foreach (var blendShape in blendShapes ?? System.Array.Empty<UnityBlendShapeData>())
            {
                TryGetLastFrame(blendShape, out var frame);
                int currentSampleIndex = sampleIndex++;
                var deltas = frame?.GetDeltaVertices(vertexCount);
                for (int unityIndex = 0; unityIndex < vertexCount; unityIndex++)
                {
                    fingerprints[unityIndex].SetRelativeSample(
                        currentSampleIndex,
                        deltas != null && unityIndex < deltas.Length ? deltas[unityIndex] : Vector3.zero);
                }
            }

            return fingerprints;
        }

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

        private static int CountSamples(FbxBlendShapeData[] blendShapes)
        {
            return blendShapes?.Length ?? 0;
        }

        private static PositionFingerprint[] CreateFingerprints(
            int vertexCount,
            int sampleCount,
            Vector3[] basePositions,
            float basePositionScale)
        {
            var fingerprints = new PositionFingerprint[Mathf.Max(0, vertexCount)];
            for (int vertexIndex = 0; vertexIndex < fingerprints.Length; vertexIndex++)
            {
                Vector3 basePosition = basePositions != null && vertexIndex < basePositions.Length
                    ? basePositions[vertexIndex] * basePositionScale
                    : Vector3.zero;
                fingerprints[vertexIndex] = new PositionFingerprint(basePosition, new Vector3[sampleCount]);
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

        private static Vector3 ToVector3(Vector4d value)
        {
            return new Vector3((float)value.m_X, (float)value.m_Y, (float)value.m_Z);
        }
    }
}
