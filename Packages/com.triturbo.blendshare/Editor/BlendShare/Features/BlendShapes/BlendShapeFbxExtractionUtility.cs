using Triturbo.BlendShapeShare.BlendShapeData;
using Debug = UnityEngine.Debug;
using FbxReaderBlendShapeChannel = Triturbo.Fbx.FbxBlendShapeChannel;
using FbxReaderMatrix = Triturbo.Fbx.FbxMatrix4x4;
using FbxReaderMesh = Triturbo.Fbx.FbxMeshGeometry;
using Vector3d = Triturbo.Fbx.Vector3d;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public static class BlendShapeFbxExtractionUtility
    {
        public static FbxBlendShapeData GetFbxBlendShapeData(
            FbxReaderBlendShapeChannel source,
            FbxReaderMesh sourceMesh,
            FbxReaderMatrix transformMatrix,
            FbxReaderMesh baseMesh = null)
        {
            if (source == null || sourceMesh == null)
            {
                return null;
            }

            int controlPointCount = sourceMesh.ControlPointCount;

            if (baseMesh == null)
            {
                baseMesh = sourceMesh;
            }
            else if (controlPointCount != baseMesh.ControlPointCount)
            {
                Debug.LogWarning("Base mesh control point count does not match the source mesh control point count. Use blendshape source as basis");
                baseMesh = sourceMesh;
            }

            var sourceControlPoints = sourceMesh.ControlPoints;
            var baseControlPoints = baseMesh.ControlPoints;
            if (sourceControlPoints.Count == 0 || baseControlPoints.Count == 0)
            {
                return null;
            }

            int shapeCount = source.Frames.Count;
            var frames = new FbxBlendShapeFrame[shapeCount];

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var sourceFrame = source.Frames[shapeIndex];
                frames[shapeIndex] = new FbxBlendShapeFrame();
                var sourceDeltas = BuildDenseReaderDeltas(sourceFrame, controlPointCount);
                var deltas = new Vector3d[controlPointCount];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var sourceDelta = sourceDeltas[pointIndex];
                    var sourceControlPoint = GetControlPoint(sourceControlPoints, pointIndex);
                    var baseControlPoint = GetControlPoint(baseControlPoints, pointIndex);

                    deltas[pointIndex] = baseMesh == sourceMesh
                        ? TransformPoint(transformMatrix, sourceDelta)
                        : TransformPoint(transformMatrix, sourceControlPoint + sourceDelta) - baseControlPoint;
                }

                for (int index = 0; index < deltas.Length; index++)
                {
                    if (!deltas[index].IsZero())
                    {
                        frames[shapeIndex].AddDeltaControlPointAt(deltas[index], index);
                    }
                }
            }

            return new FbxBlendShapeData(frames);
        }

        private static Vector3d[] BuildDenseReaderDeltas(
            Triturbo.Fbx.FbxShapeFrame sourceFrame,
            int controlPointCount)
        {
            var deltas = new Vector3d[controlPointCount];
            var indices = sourceFrame?.ControlPointIndices ?? System.Array.Empty<int>();
            var sourceDeltas = sourceFrame?.ControlPointDeltas ?? System.Array.Empty<Vector3d>();
            int count = System.Math.Min(indices.Count, sourceDeltas.Count);
            for (int i = 0; i < count; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < controlPointCount)
                {
                    deltas[index] = sourceDeltas[i];
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

        private static Vector3d TransformPoint(FbxReaderMatrix matrix, Vector3d point)
        {
            return new Vector3d(
                point.x * matrix[0, 0] + point.y * matrix[1, 0] + point.z * matrix[2, 0] + matrix[3, 0],
                point.x * matrix[0, 1] + point.y * matrix[1, 1] + point.z * matrix[2, 1] + matrix[3, 1],
                point.x * matrix[0, 2] + point.y * matrix[1, 2] + point.z * matrix[2, 2] + matrix[3, 2]);
        }
    }
}
