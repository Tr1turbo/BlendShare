using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Applies FBX control-point welding to managed FBX mesh geometry.
    /// </summary>
    internal static class FbxMeshGeometryNormalizer
    {
        public static FbxMeshGeometry Normalize(FbxMeshGeometry mesh, FbxControlPointWelding welding)
        {
            if (mesh == null || welding == null || !welding.HasGroups)
            {
                return mesh;
            }

            var controlPoints = mesh.ControlPoints.ToArray();
            var normals = mesh.ControlPointNormals.ToArray();
            var tangents = mesh.ControlPointTangents.ToArray();

            welding.ApplyAverage(controlPoints);
            welding.ApplyAverage(normals);
            welding.ApplyAverage(tangents);

            var ownerNode = CloneOwnerNode(mesh.OwnerNode);
            var deformers = mesh.Deformers
                .Select(deformer => CloneDeformer(deformer, mesh.ControlPointCount, welding))
                .Where(deformer => deformer != null)
                .ToArray();

            var normalized = new FbxMeshGeometry(
                mesh.Id,
                mesh.Name,
                ownerNode,
                mesh.ControlPointCount,
                controlPoints,
                normals,
                tangents,
                deformers);

            foreach (var deformer in deformers)
            {
                deformer.OwnerMesh = normalized;
            }

            ownerNode?.SetMesh(normalized);
            return normalized;
        }

        private static FbxSceneNode CloneOwnerNode(FbxSceneNode source)
        {
            return source == null
                ? null
                : new FbxSceneNode(source.Id, source.Name, source.Path, source.NodeType, source.LocalTransform);
        }

        private static FbxDeformer CloneDeformer(
            FbxDeformer source,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            return source switch
            {
                FbxBlendShapeDeformer blendShape => CloneBlendShapeDeformer(blendShape, controlPointCount, welding),
                FbxSkinDeformer skin => new FbxSkinDeformer(skin.Id, skin.Name, skin.Bones, skin.Clusters, skin.ControlPointWeights),
                FbxVertexCacheDeformer vertexCache => new FbxVertexCacheDeformer(vertexCache.Id, vertexCache.Name),
                FbxUnknownDeformer unknown => new FbxUnknownDeformer(unknown.Id, unknown.Name, unknown.RawType),
                _ => null
            };
        }

        private static FbxBlendShapeDeformer CloneBlendShapeDeformer(
            FbxBlendShapeDeformer source,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            var channels = source.Channels
                .Select(channel => new FbxBlendShapeChannel(
                    channel.Id,
                    channel.Name,
                    channel.Frames.Select(frame => NormalizeFrame(frame, controlPointCount, welding))))
                .ToArray();

            return new FbxBlendShapeDeformer(source.Id, source.Name, channels);
        }

        private static FbxShapeFrame NormalizeFrame(
            FbxShapeFrame source,
            int controlPointCount,
            FbxControlPointWelding welding)
        {
            if (source == null || controlPointCount <= 0)
            {
                return source;
            }

            var deltas = BuildDense(source.ControlPointIndices, source.ControlPointDeltas, controlPointCount);
            var normalDeltas = BuildDense(source.ControlPointIndices, source.ControlPointNormalDeltas, controlPointCount);
            var tangentDeltas = BuildDense(source.ControlPointIndices, source.ControlPointTangentDeltas, controlPointCount);

            welding.ApplyAverage(deltas);
            welding.ApplyAverage(normalDeltas);
            welding.ApplyAverage(tangentDeltas);

            var indices = new List<int>();
            var sparseDeltas = new List<Vector3d>();
            var sparseNormalDeltas = new List<Vector3d>();
            var sparseTangentDeltas = new List<Vector3d>();

            for (int i = 0; i < controlPointCount; i++)
            {
                if (deltas[i].IsZero() && normalDeltas[i].IsZero() && tangentDeltas[i].IsZero())
                {
                    continue;
                }

                indices.Add(i);
                sparseDeltas.Add(deltas[i]);
                sparseNormalDeltas.Add(normalDeltas[i]);
                sparseTangentDeltas.Add(tangentDeltas[i]);
            }

            return new FbxShapeFrame(
                source.FrameWeight,
                indices,
                sparseDeltas,
                sparseNormalDeltas,
                sparseTangentDeltas,
                source.SourceValueMode);
        }

        private static Vector3d[] BuildDense(
            IReadOnlyList<int> indices,
            IReadOnlyList<Vector3d> values,
            int controlPointCount)
        {
            var dense = new Vector3d[controlPointCount];
            int count = System.Math.Min(indices?.Count ?? 0, values?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < controlPointCount)
                {
                    dense[index] = values[i];
                }
            }

            return dense;
        }
    }
}
