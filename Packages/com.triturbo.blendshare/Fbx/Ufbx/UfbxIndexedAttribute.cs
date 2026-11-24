using System;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Scene-backed indexed per-face-corner attribute exposed by ufbx.</summary>
    public sealed class UfbxIndexedAttribute<T> : IFbxIndexedAttribute<T> where T : unmanaged
    {
        private readonly UfbxScene scene;
        private readonly int meshIndex;
        private readonly int attribKind;
        private readonly int setIndex;
        private readonly CopyValuesDelegate copyValues;

        internal delegate int CopyValuesDelegate(
            NativeSceneHandle handle,
            int meshIndex,
            int attribKind,
            int setIndex,
            ref T destination,
            int destinationCount,
            int expandPerCorner);

        internal UfbxIndexedAttribute(
            UfbxScene scene,
            int meshIndex,
            int attribKind,
            int setIndex,
            UfbxNative.AttribInfo info,
            CopyValuesDelegate copyValues)
        {
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
            this.meshIndex = meshIndex;
            this.attribKind = attribKind;
            this.setIndex = setIndex;
            this.copyValues = copyValues ?? throw new ArgumentNullException(nameof(copyValues));
            Exists = info.Exists != 0;
            UniquePerVertex = info.UniquePerVertex != 0;
            ValueCount = Math.Max(0, info.ValueCount);
            CornerCount = Math.Max(0, info.CornerCount);
            ComponentWCount = Math.Max(0, info.ValuesWCount);
        }

        /// <inheritdoc />
        public bool Exists { get; }

        /// <inheritdoc />
        public bool UniquePerVertex { get; }

        /// <inheritdoc />
        public int ValueCount { get; }

        /// <inheritdoc />
        public int CornerCount { get; }

        /// <inheritdoc />
        public int ComponentWCount { get; }

        /// <inheritdoc />
        public bool TryCopyValues(Span<T> destination, out int written)
        {
            var handle = scene.RequireHandle();
            if (!Exists || destination.Length < ValueCount)
            {
                written = 0;
                return false;
            }

            if (ValueCount == 0)
            {
                written = 0;
                return true;
            }

            int result = copyValues(handle, meshIndex, attribKind, setIndex, ref MemoryMarshal.GetReference(destination), destination.Length, 0);
            if (result < 0)
            {
                written = 0;
                return false;
            }

            written = result;
            return true;
        }

        /// <inheritdoc />
        public bool TryCopyCornerIndices(Span<int> destination, out int written)
        {
            var handle = scene.RequireHandle();
            if (!Exists || destination.Length < CornerCount)
            {
                written = 0;
                return false;
            }

            if (CornerCount == 0)
            {
                written = 0;
                return true;
            }

            int result = UfbxNative.CopyAttribIndices(handle, meshIndex, attribKind, setIndex, ref MemoryMarshal.GetReference(destination), destination.Length);
            if (result < 0)
            {
                written = 0;
                return false;
            }

            written = result;
            return true;
        }

        /// <inheritdoc />
        public bool TryCopyPerCornerValues(Span<T> destination, out int written)
        {
            var handle = scene.RequireHandle();
            if (!Exists || destination.Length < CornerCount)
            {
                written = 0;
                return false;
            }

            if (CornerCount == 0)
            {
                written = 0;
                return true;
            }

            int result = copyValues(handle, meshIndex, attribKind, setIndex, ref MemoryMarshal.GetReference(destination), destination.Length, 1);
            if (result < 0)
            {
                written = 0;
                return false;
            }

            written = result;
            return true;
        }

        /// <inheritdoc />
        public bool TryCopyValuesW(Span<double> destination, out int written)
        {
            var handle = scene.RequireHandle();
            if (!Exists || ComponentWCount == 0 || destination.Length < ComponentWCount)
            {
                written = 0;
                return false;
            }

            int result = UfbxNative.CopyAttribValuesW(handle, meshIndex, attribKind, setIndex, ref MemoryMarshal.GetReference(destination), destination.Length);
            if (result < 0)
            {
                written = 0;
                return false;
            }

            written = result;
            return true;
        }
    }

    /// <summary>Scene-backed UV layer and optional tangent basis.</summary>
    public sealed class UfbxUvSet : IFbxUvSet
    {
        internal UfbxUvSet(
            int setIndex,
            string name,
            IFbxIndexedAttribute<Vector2d> uvs,
            IFbxIndexedAttribute<Vector3d> tangents,
            IFbxIndexedAttribute<Vector3d> bitangents)
        {
            SetIndex = setIndex;
            Name = name ?? string.Empty;
            Uvs = uvs;
            Tangents = tangents;
            Bitangents = bitangents;
        }

        /// <inheritdoc />
        public int SetIndex { get; }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector2d> Uvs { get; }

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector3d> Tangents { get; }

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector3d> Bitangents { get; }
    }

    /// <summary>Scene-backed vertex color layer.</summary>
    public sealed class UfbxColorSet : IFbxColorSet
    {
        internal UfbxColorSet(int setIndex, string name, IFbxIndexedAttribute<Vector4d> colors)
        {
            SetIndex = setIndex;
            Name = name ?? string.Empty;
            Colors = colors;
        }

        /// <inheritdoc />
        public int SetIndex { get; }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public IFbxIndexedAttribute<Vector4d> Colors { get; }
    }

    /// <summary>Scene-backed material partition of mesh faces.</summary>
    public sealed class UfbxMeshPart : IFbxMeshPart
    {
        private readonly UfbxScene scene;
        private readonly int meshIndex;
        private readonly int partSlot;

        internal UfbxMeshPart(UfbxScene scene, int meshIndex, int partSlot, UfbxNative.MeshPartInfo info)
        {
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
            this.meshIndex = meshIndex;
            this.partSlot = partSlot;
            PartIndex = info.PartIndex;
            FaceCount = Math.Max(0, info.FaceCount);
            TriangleCount = Math.Max(0, info.TriangleCount);
        }

        /// <inheritdoc />
        public int PartIndex { get; }

        /// <inheritdoc />
        public int FaceCount { get; }

        /// <inheritdoc />
        public int TriangleCount { get; }

        /// <inheritdoc />
        public bool TryCopyFaceIndices(Span<int> destination, out int written)
        {
            var handle = scene.RequireHandle();
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

            int result = UfbxNative.CopyMaterialPartFaces(handle, meshIndex, partSlot, ref MemoryMarshal.GetReference(destination), destination.Length);
            if (result < 0)
            {
                written = 0;
                return false;
            }

            written = result;
            return true;
        }
    }
}
