using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Scene-backed ufbx mesh implementing the full <see cref="IFbxMesh"/> contract.</summary>
    public sealed class UfbxMesh : UfbxElement, IFbxMesh
    {
        private readonly object lazyLock = new object();
        private readonly int uvSetCount;
        private readonly int colorSetCount;
        private readonly int materialPartCount;
        private readonly int skinCount;
        private readonly int blendDeformerCount;

        private IReadOnlyList<IFbxNode> instances = Array.AsReadOnly(Array.Empty<IFbxNode>());
        private IReadOnlyList<IFbxMaterial> materials = Array.AsReadOnly(Array.Empty<IFbxMaterial>());
        private IFbxIndexedAttribute<Vector3d> normals;
        private IFbxIndexedAttribute<Vector3d> tangents;
        private IFbxIndexedAttribute<Vector3d> bitangents;
        private IFbxIndexedAttribute<double> vertexCreases;
        private IReadOnlyList<IFbxUvSet> uvSets;
        private IReadOnlyList<IFbxColorSet> colorSets;
        private IReadOnlyList<IFbxMeshPart> materialParts;
        private IReadOnlyList<IFbxDeformer> deformers;
        private IReadOnlyList<IFbxSkinDeformer> skinDeformers;
        private IReadOnlyList<IFbxBlendDeformer> blendDeformers;

        internal UfbxMesh(UfbxScene scene, int meshIndex, string name, UfbxNative.MeshInfo info)
            : base(scene, FbxElementType.Mesh, meshIndex, (long)info.Id, name)
        {
            ControlPointCount = Math.Max(0, info.ControlPointCount);
            FaceCount = Math.Max(0, info.FaceCount);
            FaceCornerCount = Math.Max(0, info.FaceCornerCount);
            TriangleCount = Math.Max(0, info.TriangleCount);
            MaxFaceTriangles = Math.Max(0, info.MaxFaceTriangles);
            EdgeCount = Math.Max(0, info.EdgeCount);
            MaterialPartUsageCount = Math.Max(0, info.MaterialPartUsageCount);
            uvSetCount = Math.Max(0, info.UvSetCount);
            colorSetCount = Math.Max(0, info.ColorSetCount);
            materialPartCount = Math.Max(0, info.MaterialPartCount);
            skinCount = Math.Max(0, info.SkinCount);
            blendDeformerCount = Math.Max(0, info.BlendDeformerCount);

            HasFaceSmoothing = info.HasFaceSmoothing != 0;
            HasFaceSmoothingGroups = info.HasFaceSmoothingGroup != 0;
            HasFaceMaterials = info.HasFaceMaterial != 0;
            HasFaceGroups = info.HasFaceGroup != 0;
            HasFaceHoles = info.HasFaceHole != 0;
            HasEdgeSmoothing = info.HasEdgeSmoothing != 0;
            HasEdgeCreases = info.HasEdgeCrease != 0;
            HasEdgeVisibility = info.HasEdgeVisibility != 0;
        }

        /// <inheritdoc />
        public FbxNodeAttributeKind AttributeKind => FbxNodeAttributeKind.Mesh;

        /// <inheritdoc />
        public IReadOnlyList<IFbxNode> Instances => instances;

        /// <inheritdoc />
        public IFbxNode PrimaryNode => instances.Count > 0 ? instances[0] : null;

        /// <inheritdoc />
        public int ControlPointCount { get; }

        /// <inheritdoc />
        public int FaceCount { get; }

        /// <inheritdoc />
        public int FaceCornerCount { get; }

        /// <inheritdoc />
        public int TriangleCount { get; }

        /// <inheritdoc />
        public int MaxFaceTriangles { get; }

        /// <inheritdoc />
        public int EdgeCount { get; }

        /// <inheritdoc />
        public int MaterialPartUsageCount { get; }

        /// <inheritdoc />
        public bool HasFaceSmoothing { get; }

        /// <inheritdoc />
        public bool HasFaceSmoothingGroups { get; }

        /// <inheritdoc />
        public bool HasFaceMaterials { get; }

        /// <inheritdoc />
        public bool HasFaceGroups { get; }

        /// <inheritdoc />
        public bool HasFaceHoles { get; }

        /// <inheritdoc />
        public bool HasEdgeSmoothing { get; }

        /// <inheritdoc />
        public bool HasEdgeCreases { get; }

        /// <inheritdoc />
        public bool HasEdgeVisibility { get; }

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector3d> Normals => GetOrInit(ref normals, () => BuildVec3Attrib(UfbxNative.AttribKindNormal, -1));

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector3d> Tangents => GetOrInit(ref tangents, () => BuildVec3Attrib(UfbxNative.AttribKindTangent, -1));

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector3d> Bitangents => GetOrInit(ref bitangents, () => BuildVec3Attrib(UfbxNative.AttribKindBitangent, -1));

        /// <inheritdoc />
        public IFbxIndexedAttribute<double> VertexCreases => GetOrInit(ref vertexCreases, () => BuildRealAttrib(UfbxNative.AttribKindVertexCrease, -1));

        /// <inheritdoc />
        public IReadOnlyList<IFbxUvSet> UvSets => GetOrInit(ref uvSets, BuildUvSets);

        /// <inheritdoc />
        public IReadOnlyList<IFbxColorSet> ColorSets => GetOrInit(ref colorSets, BuildColorSets);

        /// <inheritdoc />
        public IReadOnlyList<IFbxMaterial> Materials => materials;

        /// <inheritdoc />
        public IReadOnlyList<IFbxMeshPart> MaterialParts => GetOrInit(ref materialParts, BuildMaterialParts);

        /// <inheritdoc />
        public IReadOnlyList<IFbxDeformer> Deformers => GetOrInit(ref deformers, BuildDeformers);

        /// <inheritdoc />
        public IReadOnlyList<IFbxSkinDeformer> SkinDeformers => GetOrInit(ref skinDeformers, () => FbxReadOnlyList.ToReadOnly(Deformers.OfType<IFbxSkinDeformer>()));

        /// <inheritdoc />
        public IReadOnlyList<IFbxBlendDeformer> BlendDeformers => GetOrInit(ref blendDeformers, () => FbxReadOnlyList.ToReadOnly(Deformers.OfType<IFbxBlendDeformer>()));

        internal void SetInstances(IEnumerable<IFbxNode> meshInstances)
        {
            instances = FbxReadOnlyList.ToReadOnly(meshInstances);
        }

        internal void SetMaterials(IEnumerable<IFbxMaterial> meshMaterials)
        {
            materials = FbxReadOnlyList.ToReadOnly(meshMaterials);
        }

        /// <inheritdoc />
        public bool TryCopyControlPoints(Span<Vector3d> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < ControlPointCount)
            {
                written = 0;
                return false;
            }

            if (ControlPointCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyControlPoints(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFirstCornerNormals(Span<Vector3d> destination, out int written)
        {
            return TryCopyFirstCornerAttribVec3(UfbxNative.AttribKindNormal, destination, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFirstCornerTangents(Span<Vector3d> destination, out int written)
        {
            return TryCopyFirstCornerAttribVec3(UfbxNative.AttribKindTangent, destination, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceIndexBegins(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            if (FaceCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyFaceIndexBegins(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceSizes(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            if (FaceCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyFaceSizes(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceCornerControlPoints(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < FaceCornerCount)
            {
                written = 0;
                return false;
            }

            if (FaceCornerCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyFaceCornerControlPoints(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyVertexFirstCorners(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < ControlPointCount)
            {
                written = 0;
                return false;
            }

            if (ControlPointCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyVertexFirstCorners(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceSmoothing(Span<byte> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasFaceSmoothing || destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyFaceSmoothing(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceSmoothingGroups(Span<uint> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasFaceSmoothingGroups || destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyFaceSmoothingGroups(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceMaterials(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasFaceMaterials || destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyFaceMaterials(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceGroups(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasFaceGroups || destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyFaceGroups(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyFaceHoles(Span<byte> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasFaceHoles || destination.Length < FaceCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyFaceHoles(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyEdges(Span<FbxEdge> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < EdgeCount)
            {
                written = 0;
                return false;
            }

            if (EdgeCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyEdges(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyEdgeSmoothing(Span<byte> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasEdgeSmoothing || destination.Length < EdgeCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyEdgeSmoothing(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyEdgeCreases(Span<double> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasEdgeCreases || destination.Length < EdgeCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyEdgeCreases(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyEdgeVisibility(Span<byte> destination, out int written)
        {
            var handle = RequireHandle();
            if (!HasEdgeVisibility || destination.Length < EdgeCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyEdgeVisibility(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        /// <inheritdoc />
        public bool TryCopyMaterialPartUsageOrder(Span<int> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < MaterialPartUsageCount)
            {
                written = 0;
                return false;
            }

            if (MaterialPartUsageCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyMaterialPartUsageOrder(handle, Index, ref MemoryMarshal.GetReference(destination), destination.Length);
            return FinishCopy(result, out written);
        }

        private bool TryCopyFirstCornerAttribVec3(int attribKind, Span<Vector3d> destination, out int written)
        {
            var handle = RequireHandle();
            if (destination.Length < ControlPointCount)
            {
                written = 0;
                return false;
            }

            if (ControlPointCount == 0)
            {
                var infoStatus = UfbxNative.GetAttribInfo(handle, Index, attribKind, -1, out var info);
                written = 0;
                return infoStatus == UfbxNative.StatusOk && info.Exists != 0;
            }

            int result = UfbxNative.CopyControlPointFirstCornerAttribVec3(
                handle,
                Index,
                attribKind,
                -1,
                ref MemoryMarshal.GetReference(destination),
                destination.Length);
            return FinishCopy(result, out written);
        }

        private T GetOrInit<T>(ref T field, Func<T> factory) where T : class
        {
            var existing = field;
            if (existing != null)
            {
                return existing;
            }

            lock (lazyLock)
            {
                if (field == null)
                {
                    field = factory();
                }

                return field;
            }
        }

        private IFbxIndexedAttribute<Vector2d> BuildVec2Attrib(int attribKind, int setIndex)
        {
            var handle = RequireHandle();
            int status = UfbxNative.GetAttribInfo(handle, Index, attribKind, setIndex, out var info);
            if (status != UfbxNative.StatusOk)
            {
                info = default;
            }

            return new UfbxIndexedAttribute<Vector2d>(Scene, Index, attribKind, setIndex, info, UfbxNative.CopyAttribValuesVec2);
        }

        private IFbxIndexedAttribute<Vector3d> BuildVec3Attrib(int attribKind, int setIndex)
        {
            var handle = RequireHandle();
            int status = UfbxNative.GetAttribInfo(handle, Index, attribKind, setIndex, out var info);
            if (status != UfbxNative.StatusOk)
            {
                info = default;
            }

            return new UfbxIndexedAttribute<Vector3d>(Scene, Index, attribKind, setIndex, info, UfbxNative.CopyAttribValuesVec3);
        }

        private IFbxIndexedAttribute<Vector4d> BuildVec4Attrib(int attribKind, int setIndex)
        {
            var handle = RequireHandle();
            int status = UfbxNative.GetAttribInfo(handle, Index, attribKind, setIndex, out var info);
            if (status != UfbxNative.StatusOk)
            {
                info = default;
            }

            return new UfbxIndexedAttribute<Vector4d>(Scene, Index, attribKind, setIndex, info, UfbxNative.CopyAttribValuesVec4);
        }

        private IFbxIndexedAttribute<double> BuildRealAttrib(int attribKind, int setIndex)
        {
            var handle = RequireHandle();
            int status = UfbxNative.GetAttribInfo(handle, Index, attribKind, setIndex, out var info);
            if (status != UfbxNative.StatusOk)
            {
                info = default;
            }

            return new UfbxIndexedAttribute<double>(Scene, Index, attribKind, setIndex, info, UfbxNative.CopyAttribValuesReal);
        }

        private IReadOnlyList<IFbxUvSet> BuildUvSets()
        {
            var handle = RequireHandle();
            var result = new List<IFbxUvSet>(uvSetCount);
            for (int i = 0; i < uvSetCount; i++)
            {
                if (UfbxNative.GetUvSetInfo(handle, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyUvSetName(handle, Index, i, ref dst, size));
                result.Add(new UfbxUvSet(
                    info.SetIndex,
                    name,
                    BuildVec2Attrib(UfbxNative.AttribKindUv, i),
                    BuildVec3Attrib(UfbxNative.AttribKindTangent, i),
                    BuildVec3Attrib(UfbxNative.AttribKindBitangent, i)));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }

        private IReadOnlyList<IFbxColorSet> BuildColorSets()
        {
            var handle = RequireHandle();
            var result = new List<IFbxColorSet>(colorSetCount);
            for (int i = 0; i < colorSetCount; i++)
            {
                if (UfbxNative.GetColorSetInfo(handle, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyColorSetName(handle, Index, i, ref dst, size));
                result.Add(new UfbxColorSet(
                    info.SetIndex,
                    name,
                    BuildVec4Attrib(UfbxNative.AttribKindColor, i)));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }

        private IReadOnlyList<IFbxMeshPart> BuildMaterialParts()
        {
            var handle = RequireHandle();
            var result = new List<IFbxMeshPart>(materialPartCount);
            for (int i = 0; i < materialPartCount; i++)
            {
                if (UfbxNative.GetMaterialPartInfo(handle, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                result.Add(new UfbxMeshPart(Scene, Index, i, info));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }

        private IReadOnlyList<IFbxDeformer> BuildDeformers()
        {
            var handle = RequireHandle();
            var result = new List<IFbxDeformer>(skinCount + blendDeformerCount);
            for (int i = 0; i < skinCount; i++)
            {
                if (UfbxNative.GetSkinInfo(handle, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopySkinName(handle, Index, i, ref dst, size));
                result.Add(new UfbxSkinDeformer(Scene, this, i, (long)info.Id, name, info.ClusterCount));
            }

            for (int i = 0; i < blendDeformerCount; i++)
            {
                if (UfbxNative.GetBlendDeformerInfo(handle, Index, i, out var info) != UfbxNative.StatusOk)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyBlendDeformerName(handle, Index, i, ref dst, size));
                result.Add(new UfbxBlendDeformer(Scene, this, i, (long)info.Id, name, info.ChannelCount));
            }

            return FbxReadOnlyList.ToReadOnly(result);
        }

        internal static bool FinishCopy(int nativeResult, out int written)
        {
            if (nativeResult < 0)
            {
                written = 0;
                return false;
            }

            written = nativeResult;
            return true;
        }
    }
}
