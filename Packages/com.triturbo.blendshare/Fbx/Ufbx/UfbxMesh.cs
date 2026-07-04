using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public sealed class UfbxMesh : UfbxElement
    {
        private readonly Vector3d[] snapshotVertices;
        private readonly Vector3d[] snapshotNormals;
        private readonly Vector3d[] snapshotTangents;
        private IReadOnlyList<UfbxDeformer> snapshotDeformers;
        private IReadOnlyList<UfbxDeformer> deformers;
        private IReadOnlyList<UfbxSkinDeformer> skinDeformers;
        private IReadOnlyList<UfbxBlendDeformer> blendDeformers;

        internal UfbxMesh(
            UfbxScene scene,
            int meshIndex,
            long id,
            string name,
            UfbxNode ownerNode,
            int controlPointCount,
            int faceCount,
            int faceIndexCount,
            int skinCount,
            int blendDeformerCount)
            : base(scene, UfbxElementType.Mesh, meshIndex, id, name)
        {
            OwnerNode = ownerNode;
            ControlPointCount = Math.Max(0, controlPointCount);
            FaceCount = Math.Max(0, faceCount);
            FaceIndexCount = Math.Max(0, faceIndexCount);
            SkinCount = Math.Max(0, skinCount);
            BlendDeformerCount = Math.Max(0, blendDeformerCount);
        }

        internal UfbxMesh(
            UfbxMesh source,
            Vector3d[] vertices,
            Vector3d[] normals,
            Vector3d[] tangents)
            : this(
                source?.Scene ?? throw new ArgumentNullException(nameof(source)),
                source.Index,
                source.Id,
                source.Name,
                source.OwnerNode,
                source.ControlPointCount,
                source.FaceCount,
                source.FaceIndexCount,
                source.SkinCount,
                source.BlendDeformerCount)
        {
            snapshotVertices = Copy(vertices);
            snapshotNormals = Copy(normals);
            snapshotTangents = Copy(tangents);
        }

        public UfbxNode OwnerNode { get; }
        public int ControlPointCount { get; }
        public int FaceCount { get; }
        public int FaceIndexCount { get; }
        public int SkinCount { get; }
        public int BlendDeformerCount { get; }
        public IReadOnlyList<UfbxDeformer> Deformers => snapshotDeformers ?? (deformers ??= BuildDeformers());
        public IReadOnlyList<UfbxSkinDeformer> SkinDeformers => skinDeformers ??= FbxCollection.ToReadOnly(Deformers.OfType<UfbxSkinDeformer>());
        public IReadOnlyList<UfbxBlendDeformer> BlendDeformers => blendDeformers ??= FbxCollection.ToReadOnly(Deformers.OfType<UfbxBlendDeformer>());

        internal void SetSnapshotDeformers(IEnumerable<UfbxDeformer> deformers)
        {
            snapshotDeformers = FbxCollection.ToReadOnly(deformers);
            skinDeformers = null;
            blendDeformers = null;
        }

        public int CopyVertices(double[] destination)
        {
            if (snapshotVertices != null)
            {
                return CopyVector3dArray(snapshotVertices, destination, GetVectorCapacity(destination));
            }

            EnsureAlive();
            return UfbxNative.CopyControlPoints(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public int CopyNormals(double[] destination)
        {
            if (snapshotNormals != null)
            {
                return CopyVector3dArray(snapshotNormals, destination, GetVectorCapacity(destination));
            }

            EnsureAlive();
            return UfbxNative.CopyControlPointNormals(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public int CopyTangents(double[] destination)
        {
            if (snapshotTangents != null)
            {
                return CopyVector3dArray(snapshotTangents, destination, GetVectorCapacity(destination));
            }

            EnsureAlive();
            return UfbxNative.CopyControlPointTangents(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public Vector3d[] GetVertices()
        {
            if (snapshotVertices != null)
            {
                return Copy(snapshotVertices);
            }

            var values = new double[ControlPointCount * 3];
            return CopyVertices(values) != 0 ? FbxArrayUtility.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        public Vector3d[] GetNormals()
        {
            if (snapshotNormals != null)
            {
                return Copy(snapshotNormals);
            }

            var values = new double[ControlPointCount * 3];
            return CopyNormals(values) != 0 ? FbxArrayUtility.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        public Vector3d[] GetTangents()
        {
            if (snapshotTangents != null)
            {
                return Copy(snapshotTangents);
            }

            var values = new double[ControlPointCount * 3];
            return CopyTangents(values) != 0 ? FbxArrayUtility.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        public int CopyFaceSizes(int[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyFaceSizes(Scene.Handle, Index, destination, destination?.Length ?? 0);
        }

        public int CopyFaceControlPointIndices(int[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyFaceVertexIndices(Scene.Handle, Index, destination, destination?.Length ?? 0);
        }

        public int[] GetFaceSizes()
        {
            var values = new int[FaceCount];
            return CopyFaceSizes(values) != 0 ? values : Array.Empty<int>();
        }

        public int[] GetFaceControlPointIndices()
        {
            var values = new int[FaceIndexCount];
            return CopyFaceControlPointIndices(values) != 0 ? values : Array.Empty<int>();
        }

        private IReadOnlyList<UfbxDeformer> BuildDeformers()
        {
            EnsureAlive();
            var result = new List<UfbxDeformer>(SkinCount + BlendDeformerCount);
            for (int i = 0; i < SkinCount; i++)
            {
                if (UfbxNative.GetSkinInfo(Scene.Handle, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, buffer => UfbxNative.CopySkinName(Scene.Handle, Index, i, buffer, buffer.Length));
                result.Add(new UfbxSkinDeformer(Scene, this, i, (long)info.Id, name, info.ClusterCount));
            }

            for (int i = 0; i < BlendDeformerCount; i++)
            {
                if (UfbxNative.GetBlendDeformerInfo(Scene.Handle, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, buffer => UfbxNative.CopyBlendDeformerName(Scene.Handle, Index, i, buffer, buffer.Length));
                result.Add(new UfbxBlendDeformer(Scene, this, i, (long)info.Id, name, info.ChannelCount));
            }

            return FbxCollection.ToReadOnly(result);
        }

        private static int GetVectorCapacity(double[] destination)
        {
            return destination?.Length / 3 ?? 0;
        }

        internal static Vector3d[] Copy(IReadOnlyList<Vector3d> values)
        {
            return values?.ToArray() ?? Array.Empty<Vector3d>();
        }

        private static int CopyVector3dArray(IReadOnlyList<Vector3d> source, double[] destination, int destinationCount)
        {
            if (source == null || destination == null || destinationCount < source.Count ||
                (source.Count == 0 && destinationCount > 0))
            {
                return 0;
            }

            for (int i = 0; i < source.Count; i++)
            {
                destination[i * 3] = source[i].x;
                destination[i * 3 + 1] = source[i].y;
                destination[i * 3 + 2] = source[i].z;
            }

            return 1;
        }
    }

}
