using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Fbx;
using Debug = UnityEngine.Debug;
using FbxReaderMatrix = Triturbo.BlendShare.Fbx.FbxMatrix4x4;
using UfbxBlendChannel = Triturbo.BlendShare.Fbx.Ufbx.UfbxBlendChannel;
using UfbxBlendShape = Triturbo.BlendShare.Fbx.Ufbx.UfbxBlendShape;
using UfbxMesh = Triturbo.BlendShare.Fbx.Ufbx.UfbxMesh;
using Vector3d = Triturbo.BlendShare.Fbx.Vector3d;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public static class BlendShapeFbxExtractionUtility
    {
        public static FbxBlendShapeData GetFbxBlendShapeData(
            UfbxBlendChannel source,
            UfbxMesh sourceMesh,
            FbxReaderMatrix transformMatrix,
            UfbxMesh baseMesh = null)
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
                Debug.LogWarning("Base mesh FBX vertex count does not match the source mesh FBX vertex count. Use blendshape source as basis");
                baseMesh = sourceMesh;
            }

            var sourceControlPoints = sourceMesh.GetVertices();
            var baseControlPoints = baseMesh.GetVertices();
            if (sourceControlPoints.Length == 0 || baseControlPoints.Length == 0)
            {
                return null;
            }

            int shapeCount = source.BlendShapes.Count;
            var frames = new FbxBlendShapeFrame[shapeCount];

            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var sourceFrame = source.BlendShapes[shapeIndex];
                frames[shapeIndex] = new FbxBlendShapeFrame();
                var sourceDeltas = BuildDenseUfbxDeltas(sourceFrame, controlPointCount);
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

        private static Vector3d[] BuildDenseUfbxDeltas(
            UfbxBlendShape sourceFrame,
            int controlPointCount)
        {
            var deltas = new Vector3d[controlPointCount];
            if (sourceFrame == null || sourceFrame.OffsetCount <= 0)
            {
                return deltas;
            }

            var indices = new int[sourceFrame.OffsetCount];
            var values = new double[sourceFrame.OffsetCount * 3];
            if (sourceFrame.CopyOffsets(indices, values, null) == 0)
            {
                return deltas;
            }

            var sourceDeltas = FbxArrayUtility.ToVector3dArray(values);
            int count = System.Math.Min(indices.Length, sourceDeltas.Length);
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
