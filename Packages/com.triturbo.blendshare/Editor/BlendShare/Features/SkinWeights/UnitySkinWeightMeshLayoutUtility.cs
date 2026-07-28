using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    internal static class UnitySkinWeightMeshLayoutUtility
    {
        private static readonly MeshUpdateFlags CopyFlags =
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontResetBoneBounds;

        internal static bool RequiresUnlimitedLayoutRebuild(Mesh mesh)
        {
            if (mesh == null)
            {
                return false;
            }

            bool incompatibleWeights = mesh.HasVertexAttribute(VertexAttribute.BlendWeight) &&
                                       (mesh.GetVertexAttributeFormat(VertexAttribute.BlendWeight) != VertexAttributeFormat.UNorm16 ||
                                        mesh.GetVertexAttributeDimension(VertexAttribute.BlendWeight) != 4);
            bool incompatibleIndices = mesh.HasVertexAttribute(VertexAttribute.BlendIndices) &&
                                       (mesh.GetVertexAttributeFormat(VertexAttribute.BlendIndices) != VertexAttributeFormat.UInt16 ||
                                        mesh.GetVertexAttributeDimension(VertexAttribute.BlendIndices) != 4);
            return incompatibleWeights || incompatibleIndices;
        }

        internal static void RemoveSkinWeightVertexAttributes(Mesh mesh)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            var blendShapeSource = mesh.blendShapeCount > 0 ? Object.Instantiate(mesh) : null;
            try
            {
                var bindPoses = mesh.bindposes;
                var bounds = mesh.bounds;
                var descriptors = mesh.GetVertexAttributes()
                    .Where(descriptor =>
                        descriptor.attribute != VertexAttribute.BlendWeight &&
                        descriptor.attribute != VertexAttribute.BlendIndices)
                    .ToArray();

                using var sourceDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
                var sourceData = sourceDataArray[0];
                var writableDataArray = Mesh.AllocateWritableMeshData(1);
                bool applied = false;
                try
                {
                    var destinationData = writableDataArray[0];
                    destinationData.SetVertexBufferParams(sourceData.vertexCount, descriptors);
                    CopyVertexAttributes(sourceData, destinationData, descriptors);
                    CopyIndexBuffer(sourceData, destinationData);
                    CopySubMeshes(sourceData, destinationData);

                    Mesh.ApplyAndDisposeWritableMeshData(writableDataArray, mesh, CopyFlags);
                    applied = true;
                }
                finally
                {
                    if (!applied)
                    {
                        writableDataArray.Dispose();
                    }
                }

                mesh.bindposes = bindPoses;
                mesh.bounds = bounds;
                RestoreBlendShapes(mesh, blendShapeSource);
            }
            finally
            {
                if (blendShapeSource != null)
                {
                    Object.DestroyImmediate(blendShapeSource);
                }
            }
        }

        private static void CopyVertexAttributes(
            Mesh.MeshData sourceData,
            Mesh.MeshData destinationData,
            IReadOnlyList<VertexAttributeDescriptor> descriptors)
        {
            foreach (var descriptor in descriptors)
            {
                int attributeSize = GetFormatSize(descriptor.format) * descriptor.dimension;
                int sourceStride = sourceData.GetVertexBufferStride(descriptor.stream);
                int destinationStride = destinationData.GetVertexBufferStride(descriptor.stream);
                int sourceOffset = sourceData.GetVertexAttributeOffset(descriptor.attribute);
                int destinationOffset = destinationData.GetVertexAttributeOffset(descriptor.attribute);
                var sourceBytes = sourceData.GetVertexData<byte>(descriptor.stream);
                var destinationBytes = destinationData.GetVertexData<byte>(descriptor.stream);

                for (int vertexIndex = 0; vertexIndex < sourceData.vertexCount; vertexIndex++)
                {
                    int sourceStart = vertexIndex * sourceStride + sourceOffset;
                    int destinationStart = vertexIndex * destinationStride + destinationOffset;
                    for (int byteIndex = 0; byteIndex < attributeSize; byteIndex++)
                    {
                        destinationBytes[destinationStart + byteIndex] = sourceBytes[sourceStart + byteIndex];
                    }
                }
            }
        }

        private static void CopyIndexBuffer(Mesh.MeshData sourceData, Mesh.MeshData destinationData)
        {
            if (sourceData.indexFormat == IndexFormat.UInt16)
            {
                var sourceIndices = sourceData.GetIndexData<ushort>();
                destinationData.SetIndexBufferParams(sourceIndices.Length, IndexFormat.UInt16);
                NativeArray<ushort>.Copy(sourceIndices, destinationData.GetIndexData<ushort>());
                return;
            }

            var sourceIndices32 = sourceData.GetIndexData<uint>();
            destinationData.SetIndexBufferParams(sourceIndices32.Length, IndexFormat.UInt32);
            NativeArray<uint>.Copy(sourceIndices32, destinationData.GetIndexData<uint>());
        }

        private static void CopySubMeshes(Mesh.MeshData sourceData, Mesh.MeshData destinationData)
        {
            destinationData.subMeshCount = sourceData.subMeshCount;
            for (int subMeshIndex = 0; subMeshIndex < sourceData.subMeshCount; subMeshIndex++)
            {
                destinationData.SetSubMesh(subMeshIndex, sourceData.GetSubMesh(subMeshIndex), CopyFlags);
            }
        }

        private static int GetFormatSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.SInt32:
                case VertexAttributeFormat.UInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SInt16:
                case VertexAttributeFormat.UInt16:
                    return 2;
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SInt8:
                case VertexAttributeFormat.UInt8:
                    return 1;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported vertex attribute format.");
            }
        }

        private static void RestoreBlendShapes(Mesh mesh, Mesh source)
        {
            if (mesh == null || source == null || source.blendShapeCount == 0)
            {
                return;
            }

            mesh.ClearBlendShapes();
            var deltaVertices = new Vector3[source.vertexCount];
            var deltaNormals = new Vector3[source.vertexCount];
            var deltaTangents = new Vector3[source.vertexCount];
            for (int shapeIndex = 0; shapeIndex < source.blendShapeCount; shapeIndex++)
            {
                string shapeName = source.GetBlendShapeName(shapeIndex);
                int frameCount = source.GetBlendShapeFrameCount(shapeIndex);
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    source.GetBlendShapeFrameVertices(
                        shapeIndex,
                        frameIndex,
                        deltaVertices,
                        deltaNormals,
                        deltaTangents);
                    mesh.AddBlendShapeFrame(
                        shapeName,
                        source.GetBlendShapeFrameWeight(shapeIndex, frameIndex),
                        deltaVertices,
                        deltaNormals,
                        deltaTangents);
                }
            }
        }
    }
}
