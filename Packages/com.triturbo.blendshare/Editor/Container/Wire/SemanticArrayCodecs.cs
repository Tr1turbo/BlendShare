using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Wire
{
    /// <summary>
    /// Semantic-driven binary codecs for dense arrays and domain-tagged IndexedBuffers.
    /// Enforces symmetric writer/reader limits, pre-allocation DecodeBudget charges, measured reconstruction error
    /// outlier fallback, Float32 finite representability checks, strict [0,1] NormalizedWeight validation,
    /// and deterministic first-occurrence adaptive vector palette deduplication.
    /// </summary>
    public static class SemanticArrayCodecs
    {
        private enum VectorEncodingKind : byte
        {
            Float64 = 0,
            Float32 = 1,
            SymmetricQuant16 = 2,
            SymmetricQuant12 = 3,
            NormalSnorm16 = 4
        }

        private enum ScalarEncodingKind : byte
        {
            Float64 = 0,
            Float32 = 1,
            Unorm16 = 2,
            Snorm16 = 3
        }

        private enum StorageLayoutFlags : byte
        {
            Dense = 0,
            Sparse = 1,
            PaletteFlag = 1 << 7
        }

        // ============================================================
        // Public entry points for IndexedBuffer<Vector3d> & Vector3d[]
        // ============================================================

        public static void WriteIndexedVector3d(
            WireWriter writer,
            IndexedBuffer<Vector3d> buffer,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (buffer == null)
            {
                writer.WriteByte(0);
                return;
            }

            ValidateDomainAndCountForWrite(buffer.Domain, buffer.DomainCount);
            writer.WriteByte(1);
            writer.WriteByte((byte)buffer.Domain);
            writer.WriteVarUInt32(checked((uint)buffer.DomainCount));

            ExtractExactNonZero(buffer.Indices, buffer.Values, out int[] nzIndices, out Vector3d[] nzValues);
            WriteSparseVector3dPayload(writer, nzIndices, nzValues, semantic, options);
        }

        public static IndexedBuffer<Vector3d> ReadIndexedVector3d(ref WireReader reader, ref DecodeBudget budget)
        {
            byte present = reader.ReadByte();
            if (present == 0)
            {
                return null;
            }

            if (present != 1)
            {
                throw new InvalidDataException($"Invalid IndexedBuffer presence byte {present}.");
            }

            var domain = ReadCheckedDomain(ref reader);
            int domainCount = checked((int)reader.ReadVarUInt32());
            if (domainCount > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"IndexedBuffer DomainCount {domainCount} exceeds limit.");
            }

            ReadSparseVector3dPayload(ref reader, domainCount, ref budget, out int[] indices, out Vector3d[] values);
            return new IndexedBuffer<Vector3d>(domain, domainCount, indices, values, takeOwnership: true);
        }

        public static void WriteDenseOrAdaptiveVector3dArray(
            WireWriter writer,
            Vector3d[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (values == null)
            {
                writer.WriteByte(0);
                return;
            }

            if (values.Length > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"Vector3d[] length {values.Length} exceeds limit ({WireLimits.MaxCollectionCount}).");
            }

            writer.WriteByte(1);
            writer.WriteVarUInt32(checked((uint)values.Length));

            int nonZeroCount = 0;
            for (int i = 0; i < values.Length; i++)
            {
                ValidateFinite(values[i], i);
                if (!values[i].IsZero())
                {
                    nonZeroCount++;
                }
            }

            bool useSparse = values.Length > 0 && nonZeroCount * 2 <= values.Length;
            if (useSparse)
            {
                var indices = new int[nonZeroCount];
                var nz = new Vector3d[nonZeroCount];
                int dst = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    if (!values[i].IsZero())
                    {
                        indices[dst] = i;
                        nz[dst] = values[i];
                        dst++;
                    }
                }

                writer.WriteByte((byte)StorageLayoutFlags.Sparse);
                WriteSparseVector3dPayload(writer, indices, nz, semantic, options);
            }
            else
            {
                writer.WriteByte((byte)StorageLayoutFlags.Dense);
                WriteVector3dValuesWithOptionalPalette(writer, values, semantic, options);
            }
        }

        public static Vector3d[] ReadDenseOrAdaptiveVector3dArray(ref WireReader reader, ref DecodeBudget budget)
        {
            byte present = reader.ReadByte();
            if (present == 0)
            {
                return null;
            }

            if (present != 1)
            {
                throw new InvalidDataException($"Invalid Vector3d[] presence byte {present}.");
            }

            int length = checked((int)reader.ReadVarUInt32());
            if (length > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"Vector3d[] length {length} exceeds limit.");
            }

            byte layout = reader.ReadByte();
            long denseBytes = checked((long)length * 24L);
            if (layout == (byte)StorageLayoutFlags.Sparse)
            {
                // Charge destination dense array BEFORE allocating either sparse temporaries or dense output.
                budget.Charge(denseBytes);
                var sparseBudget = new DecodeBudget(budget.RemainingBytes);
                ReadSparseVector3dPayload(ref reader, length, ref sparseBudget, out int[] indices, out Vector3d[] nzValues);
                var result = length == 0 ? Array.Empty<Vector3d>() : new Vector3d[length];
                for (int i = 0; i < indices.Length; i++)
                {
                    result[indices[i]] = nzValues[i];
                }
                return result;
            }

            if (layout == (byte)StorageLayoutFlags.Dense)
            {
                return ReadVector3dValuesWithOptionalPalette(ref reader, length, ref budget);
            }

            throw new InvalidDataException($"Invalid Vector3d[] layout byte {layout}.");
        }

        // ============================================================
        // Public entry points for IndexedBuffer<double> & double[]
        // ============================================================

        public static void WriteIndexedDouble(
            WireWriter writer,
            IndexedBuffer<double> buffer,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (buffer == null)
            {
                writer.WriteByte(0);
                return;
            }

            ValidateDomainAndCountForWrite(buffer.Domain, buffer.DomainCount);
            writer.WriteByte(1);
            writer.WriteByte((byte)buffer.Domain);
            writer.WriteVarUInt32(checked((uint)buffer.DomainCount));

            ExtractExactNonZero(buffer.Indices, buffer.Values, out int[] nzIndices, out double[] nzValues);
            WriteSparseDoublePayload(writer, nzIndices, nzValues, semantic, options);
        }

        public static IndexedBuffer<double> ReadIndexedDouble(ref WireReader reader, ref DecodeBudget budget)
        {
            byte present = reader.ReadByte();
            if (present == 0)
            {
                return null;
            }

            if (present != 1)
            {
                throw new InvalidDataException($"Invalid IndexedBuffer<double> presence byte {present}.");
            }

            var domain = ReadCheckedDomain(ref reader);
            int domainCount = checked((int)reader.ReadVarUInt32());
            if (domainCount > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"IndexedBuffer<double> DomainCount {domainCount} exceeds limit.");
            }

            ReadSparseDoublePayload(ref reader, domainCount, ref budget, out int[] indices, out double[] values);
            return new IndexedBuffer<double>(domain, domainCount, indices, values, takeOwnership: true);
        }

        public static void WriteDenseOrAdaptiveDoubleArray(
            WireWriter writer,
            double[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (values == null)
            {
                writer.WriteByte(0);
                return;
            }

            if (values.Length > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"double[] length {values.Length} exceeds limit ({WireLimits.MaxCollectionCount}).");
            }

            writer.WriteByte(1);
            writer.WriteVarUInt32(checked((uint)values.Length));

            int nonZeroCount = 0;
            for (int i = 0; i < values.Length; i++)
            {
                ValidateFinite(values[i], i);
                if (values[i] != 0d)
                {
                    nonZeroCount++;
                }
            }

            bool useSparse = values.Length > 0 && nonZeroCount * 2 <= values.Length;
            if (useSparse)
            {
                var indices = new int[nonZeroCount];
                var nz = new double[nonZeroCount];
                int dst = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] != 0d)
                    {
                        indices[dst] = i;
                        nz[dst] = values[i];
                        dst++;
                    }
                }

                writer.WriteByte((byte)StorageLayoutFlags.Sparse);
                WriteSparseDoublePayload(writer, indices, nz, semantic, options);
            }
            else
            {
                writer.WriteByte((byte)StorageLayoutFlags.Dense);
                WriteDoubleValues(writer, values, semantic, options);
            }
        }

        public static double[] ReadDenseOrAdaptiveDoubleArray(ref WireReader reader, ref DecodeBudget budget)
        {
            byte present = reader.ReadByte();
            if (present == 0)
            {
                return null;
            }

            if (present != 1)
            {
                throw new InvalidDataException($"Invalid double[] presence byte {present}.");
            }

            int length = checked((int)reader.ReadVarUInt32());
            if (length > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"double[] length {length} exceeds limit.");
            }

            byte layout = reader.ReadByte();
            long denseBytes = checked((long)length * sizeof(double));
            if (layout == (byte)StorageLayoutFlags.Sparse)
            {
                budget.Charge(denseBytes);
                var sparseBudget = new DecodeBudget(budget.RemainingBytes);
                ReadSparseDoublePayload(ref reader, length, ref sparseBudget, out int[] indices, out double[] nzValues);
                var result = length == 0 ? Array.Empty<double>() : new double[length];
                for (int i = 0; i < indices.Length; i++)
                {
                    result[indices[i]] = nzValues[i];
                }
                return result;
            }

            if (layout == (byte)StorageLayoutFlags.Dense)
            {
                return ReadDoubleValues(ref reader, length, ref budget);
            }

            throw new InvalidDataException($"Invalid double[] layout byte {layout}.");
        }

        // ============================================================
        // Internal Vector3d encoding: Quantization + Adaptive Palette
        // ============================================================

        private static void WriteSparseVector3dPayload(
            WireWriter writer,
            int[] indices,
            Vector3d[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            writer.WriteVarUInt32(checked((uint)indices.Length));
            WriteDeltaIndices(writer, indices);
            WriteVector3dValuesWithOptionalPalette(writer, values, semantic, options);
        }

        private static void ReadSparseVector3dPayload(
            ref WireReader reader,
            int domainCount,
            ref DecodeBudget budget,
            out int[] indices,
            out Vector3d[] values)
        {
            int count = checked((int)reader.ReadVarUInt32());
            if (count > domainCount || count > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"Sparse Vector3d count {count} exceeds domain count {domainCount}.");
            }

            budget.Charge(checked((long)count * sizeof(int)));
            indices = ReadDeltaIndices(ref reader, count, domainCount);
            values = ReadVector3dValuesWithOptionalPalette(ref reader, count, ref budget);
        }

        private static void WriteVector3dValuesWithOptionalPalette(
            WireWriter writer,
            Vector3d[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (values.Length == 0)
            {
                writer.WriteByte((byte)VectorEncodingKind.Float64);
                return;
            }

            for (int i = 0; i < values.Length; i++)
            {
                ValidateFinite(values[i], i);
            }

            var encoding = SelectVectorEncoding(values, semantic, options, out double maxAbsX, out double maxAbsY, out double maxAbsZ);
            byte[] directBytes = EncodeVectorStream(values, encoding, maxAbsX, maxAbsY, maxAbsZ);

            if (options.EnableAdaptiveVectorPalette && values.Length >= 4)
            {
                if (TryBuildDeterministicPalette(values, encoding, maxAbsX, maxAbsY, maxAbsZ, out var palette, out var slotIndices))
                {
                    using (var paletteMs = new MemoryStream(directBytes.Length))
                    {
                        var pw = new WireWriter(paletteMs);
                        pw.WriteVarUInt32(checked((uint)palette.Length));
                        byte[] paletteTableBytes = EncodeVectorStream(palette, encoding, maxAbsX, maxAbsY, maxAbsZ);
                        pw.WriteBytesRaw(paletteTableBytes);

                        byte indexWidth = palette.Length <= 256 ? (byte)1 : palette.Length <= 65536 ? (byte)2 : (byte)4;
                        pw.WriteByte(indexWidth);
                        WritePaletteIndices(pw, slotIndices, indexWidth);

                        if (paletteMs.Length < directBytes.Length)
                        {
                            writer.WriteByte((byte)((byte)encoding | (byte)StorageLayoutFlags.PaletteFlag));
                            writer.WriteBytesRaw(paletteMs.ToArray());
                            return;
                        }
                    }
                }
            }

            writer.WriteByte((byte)encoding);
            writer.WriteBytesRaw(directBytes);
        }

        private static Vector3d[] ReadVector3dValuesWithOptionalPalette(ref WireReader reader, int count, ref DecodeBudget budget)
        {
            byte header = reader.ReadByte();
            bool isPalette = (header & (byte)StorageLayoutFlags.PaletteFlag) != 0;
            var encoding = (VectorEncodingKind)(header & ~(byte)StorageLayoutFlags.PaletteFlag);
            if (encoding > VectorEncodingKind.NormalSnorm16)
            {
                throw new InvalidDataException($"Unsupported Vector3d encoding kind {encoding}.");
            }

            if (count == 0)
            {
                if (isPalette)
                {
                    throw new InvalidDataException("Empty vector stream cannot use palette flag.");
                }

                return Array.Empty<Vector3d>();
            }

            if (!isPalette)
            {
                budget.Charge(checked((long)count * 24L));
                return DecodeVectorStream(ref reader, count, encoding);
            }

            int paletteCount = checked((int)reader.ReadVarUInt32());
            if (paletteCount <= 0 || paletteCount > count || paletteCount > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"Invalid palette size {paletteCount} for count {count}.");
            }

            // Charge final result AND guard temporary palette buffer against remaining decode budget.
            long resultBytes = checked((long)count * 24L);
            long tempPaletteBytes = checked((long)paletteCount * 24L);
            if (checked(resultBytes + tempPaletteBytes) > budget.RemainingBytes)
            {
                throw new PayloadBudgetExceededException(checked(resultBytes + tempPaletteBytes), budget.RemainingBytes);
            }

            budget.Charge(resultBytes);
            Vector3d[] palette = DecodeVectorStream(ref reader, paletteCount, encoding);
            byte indexWidth = reader.ReadByte();
            var result = new Vector3d[count];
            for (int i = 0; i < count; i++)
            {
                int slot;
                if (indexWidth == 1)
                {
                    slot = reader.ReadByte();
                }
                else if (indexWidth == 2)
                {
                    slot = reader.ReadFixedUInt16();
                }
                else if (indexWidth == 4)
                {
                    slot = reader.ReadFixedInt32();
                }
                else
                {
                    throw new InvalidDataException($"Invalid palette index width {indexWidth}.");
                }

                if ((uint)slot >= (uint)paletteCount)
                {
                    throw new InvalidDataException($"Palette index {slot} out of range [0, {paletteCount}).");
                }

                result[i] = palette[slot];
            }

            return result;
        }

        private static VectorEncodingKind SelectVectorEncoding(
            Vector3d[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options,
            out double maxAbsX,
            out double maxAbsY,
            out double maxAbsZ)
        {
            maxAbsX = 0d;
            maxAbsY = 0d;
            maxAbsZ = 0d;

            if (options.Quantization == QuantizationMode.LosslessFloat64)
            {
                return VectorEncodingKind.Float64;
            }

            bool allFloat32Finite = true;
            for (int i = 0; i < values.Length; i++)
            {
                var v = values[i];
                double ax = Math.Abs(v.x);
                double ay = Math.Abs(v.y);
                double az = Math.Abs(v.z);
                if (ax > maxAbsX) maxAbsX = ax;
                if (ay > maxAbsY) maxAbsY = ay;
                if (az > maxAbsZ) maxAbsZ = az;

                if (allFloat32Finite &&
                    (float.IsInfinity((float)v.x) || float.IsInfinity((float)v.y) || float.IsInfinity((float)v.z)))
                {
                    allFloat32Finite = false;
                }
            }

            if (options.Quantization == QuantizationMode.FullFloat32)
            {
                return allFloat32Finite ? VectorEncodingKind.Float32 : VectorEncodingKind.Float64;
            }

            if (semantic == DataSemantic.NormalDelta)
            {
                if (maxAbsX <= 2d && maxAbsY <= 2d && maxAbsZ <= 2d)
                {
                    return VectorEncodingKind.NormalSnorm16;
                }

                return allFloat32Finite ? VectorEncodingKind.Float32 : VectorEncodingKind.Float64;
            }

            if (semantic == DataSemantic.PositionDelta)
            {
                var candidate = options.Quantization == QuantizationMode.CompactQuant12
                    ? VectorEncodingKind.SymmetricQuant12
                    : VectorEncodingKind.SymmetricQuant16;

                int maxQ = candidate == VectorEncodingKind.SymmetricQuant12 ? 2047 : 32767;
                double budgetErr = options.EffectiveMaxPositionQuantizationErrorUnits;
                if (options.MaxPositionQuantizationErrorMeters > 0d)
                {
                    if (!(budgetErr > 0d) || double.IsNaN(budgetErr))
                    {
                        return VectorEncodingKind.Float64;
                    }

                    double invBudget = 1d / budgetErr;

                    // Measure actual maximum per-axis AND 3D Euclidean reconstruction error in stored coordinate units
                    // (scaled from physical meters via PositionUnitMeters / EffectivePositionUnitMeters).
                    bool quantExceedsBudget = false;
                    for (int i = 0; i < values.Length; i++)
                    {
                        var v = values[i];
                        double dx = 0d;
                        if (maxAbsX > 0d)
                        {
                            short qx = QuantizeSymmetric(v.x, maxAbsX, maxQ);
                            double rx = DequantizeSymmetric(qx, maxAbsX, maxQ);
                            dx = v.x - rx;
                            if (Math.Abs(dx) > budgetErr)
                            {
                                quantExceedsBudget = true;
                                break;
                            }
                        }

                        double dy = 0d;
                        if (maxAbsY > 0d)
                        {
                            short qy = QuantizeSymmetric(v.y, maxAbsY, maxQ);
                            double ry = DequantizeSymmetric(qy, maxAbsY, maxQ);
                            dy = v.y - ry;
                            if (Math.Abs(dy) > budgetErr)
                            {
                                quantExceedsBudget = true;
                                break;
                            }
                        }

                        double dz = 0d;
                        if (maxAbsZ > 0d)
                        {
                            short qz = QuantizeSymmetric(v.z, maxAbsZ, maxQ);
                            double rz = DequantizeSymmetric(qz, maxAbsZ, maxQ);
                            dz = v.z - rz;
                            if (Math.Abs(dz) > budgetErr)
                            {
                                quantExceedsBudget = true;
                                break;
                            }
                        }

                        double nx = dx * invBudget;
                        double ny = dy * invBudget;
                        double nz = dz * invBudget;
                        if (!(nx * nx + ny * ny + nz * nz <= 1d))
                        {
                            quantExceedsBudget = true;
                            break;
                        }
                    }

                    if (quantExceedsBudget)
                    {
                        if (!allFloat32Finite)
                        {
                            return VectorEncodingKind.Float64;
                        }

                        // Verify Float32 reconstruction also satisfies both per-axis and 3D Euclidean physical error budgets; otherwise promote to Float64.
                        for (int i = 0; i < values.Length; i++)
                        {
                            var v = values[i];
                            double fx = v.x - (float)v.x;
                            double fy = v.y - (float)v.y;
                            double fz = v.z - (float)v.z;
                            if (Math.Abs(fx) > budgetErr ||
                                Math.Abs(fy) > budgetErr ||
                                Math.Abs(fz) > budgetErr)
                            {
                                return VectorEncodingKind.Float64;
                            }

                            double nfx = fx * invBudget;
                            double nfy = fy * invBudget;
                            double nfz = fz * invBudget;
                            if (!(nfx * nfx + nfy * nfy + nfz * nfz <= 1d))
                            {
                                return VectorEncodingKind.Float64;
                            }
                        }

                        return VectorEncodingKind.Float32;
                    }
                }

                return candidate;
            }

            return allFloat32Finite ? VectorEncodingKind.Float32 : VectorEncodingKind.Float64;
        }

        private static byte[] EncodeVectorStream(
            Vector3d[] values,
            VectorEncodingKind encoding,
            double maxAbsX,
            double maxAbsY,
            double maxAbsZ)
        {
            using (var ms = new MemoryStream(values.Length * 8 + 32))
            {
                var w = new WireWriter(ms);
                switch (encoding)
                {
                    case VectorEncodingKind.Float64:
                        for (int i = 0; i < values.Length; i++) w.WriteVector3d(values[i]);
                        break;

                    case VectorEncodingKind.Float32:
                        for (int i = 0; i < values.Length; i++)
                        {
                            w.WriteSingle((float)values[i].x);
                            w.WriteSingle((float)values[i].y);
                            w.WriteSingle((float)values[i].z);
                        }
                        break;

                    case VectorEncodingKind.SymmetricQuant16:
                    case VectorEncodingKind.SymmetricQuant12:
                    {
                        int maxQ = encoding == VectorEncodingKind.SymmetricQuant12 ? 2047 : 32767;
                        byte axisMask = 0;
                        if (maxAbsX > 0d) axisMask |= 1;
                        if (maxAbsY > 0d) axisMask |= 2;
                        if (maxAbsZ > 0d) axisMask |= 4;
                        w.WriteByte(axisMask);
                        if ((axisMask & 1) != 0) w.WriteDouble(maxAbsX);
                        if ((axisMask & 2) != 0) w.WriteDouble(maxAbsY);
                        if ((axisMask & 4) != 0) w.WriteDouble(maxAbsZ);

                        for (int i = 0; i < values.Length; i++)
                        {
                            if ((axisMask & 1) != 0) w.WriteFixedInt16(QuantizeSymmetric(values[i].x, maxAbsX, maxQ));
                            if ((axisMask & 2) != 0) w.WriteFixedInt16(QuantizeSymmetric(values[i].y, maxAbsY, maxQ));
                            if ((axisMask & 4) != 0) w.WriteFixedInt16(QuantizeSymmetric(values[i].z, maxAbsZ, maxQ));
                        }
                        break;
                    }

                    case VectorEncodingKind.NormalSnorm16:
                        for (int i = 0; i < values.Length; i++)
                        {
                            w.WriteFixedInt16(QuantizeSymmetric(values[i].x, 2d, 32767));
                            w.WriteFixedInt16(QuantizeSymmetric(values[i].y, 2d, 32767));
                            w.WriteFixedInt16(QuantizeSymmetric(values[i].z, 2d, 32767));
                        }
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported encoding {encoding}.");
                }

                return ms.ToArray();
            }
        }

        private static Vector3d[] DecodeVectorStream(ref WireReader reader, int count, VectorEncodingKind encoding)
        {
            var result = new Vector3d[count];
            switch (encoding)
            {
                case VectorEncodingKind.Float64:
                    for (int i = 0; i < count; i++)
                    {
                        var v = reader.ReadVector3d();
                        ValidateFinite(v, i);
                        result[i] = v;
                    }
                    break;

                case VectorEncodingKind.Float32:
                    for (int i = 0; i < count; i++)
                    {
                        var v = new Vector3d(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        ValidateFinite(v, i);
                        result[i] = v;
                    }
                    break;

                case VectorEncodingKind.SymmetricQuant16:
                case VectorEncodingKind.SymmetricQuant12:
                {
                    double maxQ = encoding == VectorEncodingKind.SymmetricQuant12 ? 2047d : 32767d;
                    byte axisMask = reader.ReadByte();
                    if ((axisMask & ~0x7) != 0)
                    {
                        throw new InvalidDataException($"Invalid symmetric quantization axis mask {axisMask}.");
                    }

                    double maxAbsX = (axisMask & 1) != 0 ? ReadPositiveFiniteExtent(ref reader) : 0d;
                    double maxAbsY = (axisMask & 2) != 0 ? ReadPositiveFiniteExtent(ref reader) : 0d;
                    double maxAbsZ = (axisMask & 4) != 0 ? ReadPositiveFiniteExtent(ref reader) : 0d;

                    for (int i = 0; i < count; i++)
                    {
                        double x = (axisMask & 1) != 0 ? DequantizeSymmetric(reader.ReadFixedInt16(), maxAbsX, maxQ) : 0d;
                        double y = (axisMask & 2) != 0 ? DequantizeSymmetric(reader.ReadFixedInt16(), maxAbsY, maxQ) : 0d;
                        double z = (axisMask & 4) != 0 ? DequantizeSymmetric(reader.ReadFixedInt16(), maxAbsZ, maxQ) : 0d;
                        result[i] = new Vector3d(x, y, z);
                    }
                    break;
                }

                case VectorEncodingKind.NormalSnorm16:
                    for (int i = 0; i < count; i++)
                    {
                        double x = DequantizeSymmetric(reader.ReadFixedInt16(), 2d, 32767d);
                        double y = DequantizeSymmetric(reader.ReadFixedInt16(), 2d, 32767d);
                        double z = DequantizeSymmetric(reader.ReadFixedInt16(), 2d, 32767d);
                        result[i] = new Vector3d(x, y, z);
                    }
                    break;

                default:
                    throw new InvalidDataException($"Unsupported Vector3d encoding {encoding}.");
            }

            return result;
        }

        private static bool TryBuildDeterministicPalette(
            Vector3d[] values,
            VectorEncodingKind encoding,
            double maxAbsX,
            double maxAbsY,
            double maxAbsZ,
            out Vector3d[] palette,
            out int[] slotIndices)
        {
            var keyToSlot = new Dictionary<(long, long, long), int>();
            var unique = new List<Vector3d>();
            slotIndices = new int[values.Length];

            int maxUsefulPalette = Math.Max(1, (values.Length * 3) / 4);

            for (int i = 0; i < values.Length; i++)
            {
                var key = MakeQuantizedKey(values[i], encoding, maxAbsX, maxAbsY, maxAbsZ);
                if (!keyToSlot.TryGetValue(key, out int slot))
                {
                    if (unique.Count >= maxUsefulPalette)
                    {
                        palette = null;
                        slotIndices = null;
                        return false;
                    }

                    slot = unique.Count;
                    keyToSlot.Add(key, slot);
                    unique.Add(values[i]);
                }

                slotIndices[i] = slot;
            }

            palette = unique.ToArray();
            return true;
        }

        private static (long, long, long) MakeQuantizedKey(
            Vector3d v,
            VectorEncodingKind encoding,
            double maxAbsX,
            double maxAbsY,
            double maxAbsZ)
        {
            switch (encoding)
            {
                case VectorEncodingKind.Float64:
                    return (
                        BitConverter.DoubleToInt64Bits(v.x == 0d ? 0d : v.x),
                        BitConverter.DoubleToInt64Bits(v.y == 0d ? 0d : v.y),
                        BitConverter.DoubleToInt64Bits(v.z == 0d ? 0d : v.z));

                case VectorEncodingKind.Float32:
                {
                    float fx = (float)v.x;
                    float fy = (float)v.y;
                    float fz = (float)v.z;
                    return (
                        BitConverter.SingleToInt32Bits(fx == 0f ? 0f : fx),
                        BitConverter.SingleToInt32Bits(fy == 0f ? 0f : fy),
                        BitConverter.SingleToInt32Bits(fz == 0f ? 0f : fz));
                }

                case VectorEncodingKind.SymmetricQuant16:
                case VectorEncodingKind.SymmetricQuant12:
                {
                    int maxQ = encoding == VectorEncodingKind.SymmetricQuant12 ? 2047 : 32767;
                    return (
                        maxAbsX > 0d ? QuantizeSymmetric(v.x, maxAbsX, maxQ) : 0,
                        maxAbsY > 0d ? QuantizeSymmetric(v.y, maxAbsY, maxQ) : 0,
                        maxAbsZ > 0d ? QuantizeSymmetric(v.z, maxAbsZ, maxQ) : 0);
                }

                case VectorEncodingKind.NormalSnorm16:
                    return (
                        QuantizeSymmetric(v.x, 2d, 32767),
                        QuantizeSymmetric(v.y, 2d, 32767),
                        QuantizeSymmetric(v.z, 2d, 32767));

                default:
                    throw new ArgumentOutOfRangeException(nameof(encoding));
            }
        }

        private static void WritePaletteIndices(WireWriter writer, int[] slotIndices, byte indexWidth)
        {
            if (indexWidth == 1)
            {
                for (int i = 0; i < slotIndices.Length; i++) writer.WriteByte((byte)slotIndices[i]);
            }
            else if (indexWidth == 2)
            {
                for (int i = 0; i < slotIndices.Length; i++) writer.WriteFixedUInt16((ushort)slotIndices[i]);
            }
            else
            {
                for (int i = 0; i < slotIndices.Length; i++) writer.WriteFixedInt32(slotIndices[i]);
            }
        }

        // ============================================================
        // Internal Double encoding (NormalizedWeight unorm16 / Float32 / Float64)
        // ============================================================

        private static void WriteSparseDoublePayload(
            WireWriter writer,
            int[] indices,
            double[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            writer.WriteVarUInt32(checked((uint)indices.Length));
            WriteDeltaIndices(writer, indices);
            WriteDoubleValues(writer, values, semantic, options);
        }

        private static void ReadSparseDoublePayload(
            ref WireReader reader,
            int domainCount,
            ref DecodeBudget budget,
            out int[] indices,
            out double[] values)
        {
            int count = checked((int)reader.ReadVarUInt32());
            if (count > domainCount || count > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"Sparse double count {count} exceeds domain count {domainCount}.");
            }

            budget.Charge(checked((long)count * sizeof(int)));
            indices = ReadDeltaIndices(ref reader, count, domainCount);
            values = ReadDoubleValues(ref reader, count, ref budget);
        }

        private static void WriteDoubleValues(
            WireWriter writer,
            double[] values,
            DataSemantic semantic,
            PayloadEncodeOptions options)
        {
            if (values.Length == 0)
            {
                writer.WriteByte((byte)ScalarEncodingKind.Float64);
                return;
            }

            bool allFloat32Finite = true;
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                ValidateFinite(v, i);
                if (semantic == DataSemantic.NormalizedWeight && (v < 0d || v > 1d))
                {
                    throw new InvalidDataException($"NormalizedWeight value at index {i} ({v}) is outside [0, 1].");
                }

                if (semantic == DataSemantic.SignedWeightDelta && (v < -1d || v > 1d))
                {
                    throw new InvalidDataException($"SignedWeightDelta value at index {i} ({v}) is outside [-1, 1].");
                }

                if (allFloat32Finite && float.IsInfinity((float)v))
                {
                    allFloat32Finite = false;
                }
            }

            ScalarEncodingKind encoding;
            if (options.Quantization == QuantizationMode.LosslessFloat64)
            {
                encoding = ScalarEncodingKind.Float64;
            }
            else if (semantic == DataSemantic.NormalizedWeight &&
                     (options.Quantization == QuantizationMode.BalancedQuant16 || options.Quantization == QuantizationMode.CompactQuant12))
            {
                encoding = ScalarEncodingKind.Unorm16;
            }
            else if (semantic == DataSemantic.SignedWeightDelta &&
                     (options.Quantization == QuantizationMode.BalancedQuant16 || options.Quantization == QuantizationMode.CompactQuant12))
            {
                encoding = ScalarEncodingKind.Snorm16;
            }
            else
            {
                encoding = allFloat32Finite ? ScalarEncodingKind.Float32 : ScalarEncodingKind.Float64;
            }

            writer.WriteByte((byte)encoding);
            switch (encoding)
            {
                case ScalarEncodingKind.Float64:
                    for (int i = 0; i < values.Length; i++) writer.WriteDouble(values[i]);
                    break;
                case ScalarEncodingKind.Float32:
                    for (int i = 0; i < values.Length; i++) writer.WriteSingle((float)values[i]);
                    break;
                case ScalarEncodingKind.Unorm16:
                    for (int i = 0; i < values.Length; i++)
                    {
                        double v = values[i] < options.WeightZeroThreshold ? 0d : values[i];
                        int q = (int)Math.Round(v * 65535d, MidpointRounding.AwayFromZero);
                        if (q < 0) q = 0;
                        if (q > 65535) q = 65535;
                        writer.WriteFixedUInt16((ushort)q);
                    }
                    break;
                case ScalarEncodingKind.Snorm16:
                    for (int i = 0; i < values.Length; i++)
                    {
                        double v = Math.Abs(values[i]) < options.WeightZeroThreshold ? 0d : values[i];
                        int q = (int)Math.Round(v * 32767d, MidpointRounding.AwayFromZero);
                        if (q < -32767) q = -32767;
                        if (q > 32767) q = 32767;
                        writer.WriteFixedInt16((short)q);
                    }
                    break;
            }
        }

        private static double[] ReadDoubleValues(ref WireReader reader, int count, ref DecodeBudget budget)
        {
            byte raw = reader.ReadByte();
            if (raw > (byte)ScalarEncodingKind.Snorm16)
            {
                throw new InvalidDataException($"Unsupported scalar encoding {raw}.");
            }

            var encoding = (ScalarEncodingKind)raw;
            if (count == 0)
            {
                return Array.Empty<double>();
            }

            budget.Charge(checked((long)count * sizeof(double)));
            var result = new double[count];
            switch (encoding)
            {
                case ScalarEncodingKind.Float64:
                    for (int i = 0; i < count; i++)
                    {
                        double v = reader.ReadDouble();
                        ValidateFinite(v, i);
                        result[i] = v;
                    }
                    break;
                case ScalarEncodingKind.Float32:
                    for (int i = 0; i < count; i++)
                    {
                        double v = reader.ReadSingle();
                        ValidateFinite(v, i);
                        result[i] = v;
                    }
                    break;
                case ScalarEncodingKind.Unorm16:
                    for (int i = 0; i < count; i++)
                    {
                        result[i] = reader.ReadFixedUInt16() / 65535d;
                    }
                    break;
                case ScalarEncodingKind.Snorm16:
                    for (int i = 0; i < count; i++)
                    {
                        short q = reader.ReadFixedInt16();
                        if (q < -32767)
                        {
                            throw new InvalidDataException($"SignedWeightDelta snorm16 value {q} out of symmetric range [-32767, 32767].");
                        }

                        result[i] = q / 32767d;
                    }
                    break;
            }

            return result;
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static void WriteDeltaIndices(WireWriter writer, int[] indices)
        {
            int previous = -1;
            for (int i = 0; i < indices.Length; i++)
            {
                int current = indices[i];
                if (current <= previous)
                {
                    throw new InvalidDataException("Sparse indices must be non-negative and strictly increasing.");
                }

                uint delta = i == 0 ? (uint)current : (uint)(current - previous - 1);
                writer.WriteVarUInt32(delta);
                previous = current;
            }
        }

        private static int[] ReadDeltaIndices(ref WireReader reader, int count, int domainCount)
        {
            if (count == 0)
            {
                return Array.Empty<int>();
            }

            var indices = new int[count];
            long current = 0L;
            for (int i = 0; i < count; i++)
            {
                uint delta = reader.ReadVarUInt32();
                current = i == 0 ? delta : checked(current + 1L + delta);
                if (current >= domainCount)
                {
                    throw new InvalidDataException($"Decoded sparse index {current} exceeds domainCount {domainCount}.");
                }

                indices[i] = (int)current;
            }

            return indices;
        }

        private static short QuantizeSymmetric(double value, double maxAbs, int maxQ)
        {
            if (maxAbs <= 0d || value == 0d)
            {
                return 0;
            }

            double normalized = value / maxAbs;
            if (normalized > 1d) normalized = 1d;
            if (normalized < -1d) normalized = -1d;

            int q = (int)Math.Round(normalized * maxQ, MidpointRounding.AwayFromZero);
            if (q > maxQ) q = maxQ;
            if (q < -maxQ) q = -maxQ;
            return (short)q;
        }

        private static double DequantizeSymmetric(short q, double maxAbs, double maxQ)
        {
            if (q == 0 || maxAbs <= 0d)
            {
                return 0d;
            }

            if (q < -maxQ || q > maxQ)
            {
                throw new InvalidDataException($"Quantized component {q} out of symmetric range [-{maxQ}, {maxQ}].");
            }

            return (q / maxQ) * maxAbs;
        }

        private static double ReadPositiveFiniteExtent(ref WireReader reader)
        {
            double extent = reader.ReadDouble();
            if (double.IsNaN(extent) || double.IsInfinity(extent) || extent <= 0d)
            {
                throw new InvalidDataException($"Invalid quantization extent {extent}.");
            }

            return extent;
        }

        private static void ValidateDomainAndCountForWrite(IndexDomain domain, int domainCount)
        {
            if (!IndexDomainUtility.IsDefinedValid(domain))
            {
                throw new InvalidDataException($"Invalid IndexDomain value {domain}.");
            }

            if (domainCount < 0 || domainCount > WireLimits.MaxCollectionCount)
            {
                throw new InvalidDataException($"IndexedBuffer DomainCount {domainCount} exceeds limit ({WireLimits.MaxCollectionCount}).");
            }
        }

        private static IndexDomain ReadCheckedDomain(ref WireReader reader)
        {
            var domain = (IndexDomain)reader.ReadByte();
            if (!IndexDomainUtility.IsDefinedValid(domain))
            {
                throw new InvalidDataException($"Invalid IndexDomain value {(byte)domain}.");
            }

            return domain;
        }

        private static void ExtractExactNonZero(
            ReadOnlySpan<int> indices,
            ReadOnlySpan<Vector3d> values,
            out int[] outIndices,
            out Vector3d[] outValues)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                ValidateFinite(values[i], i);
                if (!values[i].IsZero()) count++;
            }

            outIndices = count == 0 ? Array.Empty<int>() : new int[count];
            outValues = count == 0 ? Array.Empty<Vector3d>() : new Vector3d[count];
            int dst = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (!values[i].IsZero())
                {
                    outIndices[dst] = indices[i];
                    outValues[dst] = values[i];
                    dst++;
                }
            }
        }

        private static void ExtractExactNonZero(
            ReadOnlySpan<int> indices,
            ReadOnlySpan<double> values,
            out int[] outIndices,
            out double[] outValues)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                ValidateFinite(values[i], i);
                if (values[i] != 0d) count++;
            }

            outIndices = count == 0 ? Array.Empty<int>() : new int[count];
            outValues = count == 0 ? Array.Empty<double>() : new double[count];
            int dst = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != 0d)
                {
                    outIndices[dst] = indices[i];
                    outValues[dst] = values[i];
                    dst++;
                }
            }
        }

        private static void ValidateFinite(Vector3d v, int index)
        {
            if (double.IsNaN(v.x) || double.IsInfinity(v.x) ||
                double.IsNaN(v.y) || double.IsInfinity(v.y) ||
                double.IsNaN(v.z) || double.IsInfinity(v.z))
            {
                throw new InvalidDataException($"Non-finite Vector3d at index {index}.");
            }
        }

        private static void ValidateFinite(double v, int index)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                throw new InvalidDataException($"Non-finite double at index {index}.");
            }
        }
    }
}
