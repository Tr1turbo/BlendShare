using System;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using FbxReaderMatrix = Triturbo.BlendShare.Fbx.FbxMatrix4x4;
using Vector3d = Triturbo.BlendShare.Fbx.Vector3d;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public static class BlendShapeFbxExtractionUtility
    {
        /// <summary>Extracts dense blendshape position and normal deltas from an FBX blend channel.</summary>
        public static FbxBlendShapeData GetFbxBlendShapeData(
            IFbxBlendChannel source,
            IFbxMeshGeometry sourceMesh,
            FbxReaderMatrix transformMatrix,
            IFbxMeshGeometry baseMesh = null,
            int controlPointLimit = -1)
        {
            if (!FbxSourceSpaceTransform.TryCreate(transformMatrix, out var spaceTransform))
            {
                return null;
            }

            return GetFbxBlendShapeData(source, sourceMesh, spaceTransform, baseMesh, controlPointLimit);
        }

        /// <summary>Extracts dense blendshape position and normal deltas from an FBX blend channel.</summary>
        public static FbxBlendShapeData GetFbxBlendShapeData(
            IFbxBlendChannel source,
            IFbxMeshGeometry sourceMesh,
            FbxSourceSpaceTransform spaceTransform,
            IFbxMeshGeometry baseMesh = null,
            int controlPointLimit = -1)
        {
            if (source == null || sourceMesh == null || !spaceTransform.IsValid)
            {
                return null;
            }

            int controlPointCount = controlPointLimit >= 0
                ? Math.Min(controlPointLimit, sourceMesh.ControlPointCount)
                : sourceMesh.ControlPointCount;

            if (baseMesh == null)
            {
                baseMesh = sourceMesh;
            }
            controlPointCount = Math.Min(controlPointCount, baseMesh.ControlPointCount);

            var sourceControlPoints = sourceMesh.GetControlPoints();
            var baseControlPoints = ReferenceEquals(baseMesh, sourceMesh)
                ? sourceControlPoints
                : baseMesh.GetControlPoints();
            if (sourceControlPoints.Length == 0 || baseControlPoints.Length == 0)
            {
                return null;
            }

            int shapeCount = source.Keyframes.Count;
            var frames = new FbxBlendShapeFrame[shapeCount];
            bool preserveSourceRelative = ReferenceEquals(baseMesh, sourceMesh);
            Vector3d[] sourceBaseNormals = null;
            bool sourceBaseNormalsLoaded = false;

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var sourceKeyframe = source.Keyframes[shapeIndex];
                frames[shapeIndex] = new FbxBlendShapeFrame(sourceKeyframe?.TargetWeight ?? 100.0);
                var shape = sourceKeyframe?.Shape;
                var sourceDeltas = BuildDenseFbxDeltas(shape, controlPointCount, out var sourceNormalDeltas);
                var deltas = new Vector3d[controlPointCount];
                var normalDeltas = new Vector3d[controlPointCount];

                if (!spaceTransform.IsIdentity && shape?.HasNormalOffsets == true && !sourceBaseNormalsLoaded)
                {
                    sourceBaseNormals = sourceMesh.GetFirstCornerNormals();
                    sourceBaseNormalsLoaded = true;
                }

                int baseNormalLength = sourceBaseNormals?.Length ?? 0;

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var sourceDelta = sourceDeltas[pointIndex];
                    var sourceControlPoint = GetControlPoint(sourceControlPoints, pointIndex);
                    var baseControlPoint = GetControlPoint(baseControlPoints, pointIndex);

                    deltas[pointIndex] = preserveSourceRelative
                        ? spaceTransform.TransformVector(sourceDelta)
                        : spaceTransform.TransformPoint(sourceControlPoint + sourceDelta) - baseControlPoint;

                    var rawNormalDelta = sourceNormalDeltas[pointIndex];
                    if (spaceTransform.IsIdentity || rawNormalDelta.IsZero())
                    {
                        normalDeltas[pointIndex] = rawNormalDelta;
                    }
                    else
                    {
                        var baseNormal = pointIndex < baseNormalLength ? sourceBaseNormals[pointIndex] : Vector3d.zero;
                        normalDeltas[pointIndex] = spaceTransform.TransformNormalDelta(baseNormal, rawNormalDelta);
                    }
                }

                for (int index = 0; index < deltas.Length; index++)
                {
                    frames[shapeIndex].SetDeltaPositionAt(index, deltas[index]);
                    frames[shapeIndex].SetDeltaNormalAt(index, normalDeltas[index]);
                }
            }

            return new FbxBlendShapeData(frames);
        }

        private static Vector3d[] BuildDenseFbxDeltas(
            IFbxBlendShape sourceFrame,
            int controlPointCount,
            out Vector3d[] normalDeltas)
        {
            var deltas = new Vector3d[controlPointCount];
            normalDeltas = new Vector3d[controlPointCount];
            if (sourceFrame == null || sourceFrame.OffsetCount <= 0)
            {
                return deltas;
            }

            var indices = new int[sourceFrame.OffsetCount];
            var sourceDeltas = new Vector3d[sourceFrame.OffsetCount];
            var sourceNormalDeltas = sourceFrame.HasNormalOffsets
                ? new Vector3d[sourceFrame.OffsetCount]
                : Array.Empty<Vector3d>();
            if (!sourceFrame.TryCopyOffsets(indices, sourceDeltas, sourceNormalDeltas, Span<double>.Empty, out int count) ||
                count == 0)
            {
                return deltas;
            }

            int safeCount = Math.Min(count, Math.Min(indices.Length, sourceDeltas.Length));
            for (int i = 0; i < safeCount; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < controlPointCount)
                {
                    deltas[index] = sourceDeltas[i];
                    if (i < sourceNormalDeltas.Length)
                    {
                        normalDeltas[index] = sourceNormalDeltas[i];
                    }
                }
            }

            return deltas;
        }

        private static Vector3d GetControlPoint(
            System.Collections.Generic.IReadOnlyList<Vector3d> controlPoints,
            int index)
        {
            return index >= 0 && index < (controlPoints?.Count ?? 0)
                ? controlPoints[index]
                : Vector3d.zero;
        }
    }
}
