using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Container.Wire
{
    /// <summary>
    /// Wire types for BlendShare declarative tag-length-value binary encoding.
    /// </summary>
    public enum WireType : byte
    {
        Varint = 0,
        Fixed32 = 1,
        Fixed64 = 2,
        LengthDelimited = 3
    }

    /// <summary>
    /// Safety limits enforced against untrusted or corrupt BlendShare files before allocating collections.
    /// </summary>
    public static class WireLimits
    {
        public const int MaxNestingDepth = 32;
        public const int MaxCollectionCount = 16 * 1024 * 1024;
        public const int MaxStringBytes = 4 * 1024 * 1024;
        public const int MaxByteArrayBytes = 128 * 1024 * 1024;
        public const int MaxPayloadCount = 1 * 1024 * 1024;
        public const long MaxUncompressedEnvelopeBytes = 256L * 1024L * 1024L;
    }

    /// <summary>
    /// Little-endian binary &amp; LEB128 varint writer with deterministic encoding.
    /// </summary>
    public sealed class WireWriter
    {
        private readonly Stream m_Stream;
        private readonly byte[] m_Scratch = new byte[16];

        public WireWriter(Stream stream)
        {
            m_Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public void WriteByte(byte value) => m_Stream.WriteByte(value);

        public void WriteBool(bool value) => m_Stream.WriteByte(value ? (byte)1 : (byte)0);

        public void WriteTag(int fieldTag, WireType wireType)
        {
            if (fieldTag <= 0 || fieldTag > 0x1FFFFFFF)
            {
                throw new ArgumentOutOfRangeException(nameof(fieldTag), "Field tag must be in [1, 0x1FFFFFFF].");
            }

            uint encoded = ((uint)fieldTag << 3) | ((uint)wireType & 0x7u);
            WriteVarUInt32(encoded);
        }

        public void WriteVarUInt32(uint value)
        {
            int count = 0;
            while (value >= 0x80u)
            {
                m_Scratch[count++] = (byte)((value & 0x7Fu) | 0x80u);
                value >>= 7;
            }

            m_Scratch[count++] = (byte)value;
            m_Stream.Write(m_Scratch, 0, count);
        }

        public void WriteVarUInt64(ulong value)
        {
            int count = 0;
            while (value >= 0x80UL)
            {
                m_Scratch[count++] = (byte)((value & 0x7FUL) | 0x80UL);
                value >>= 7;
            }

            m_Scratch[count++] = (byte)value;
            m_Stream.Write(m_Scratch, 0, count);
        }

        public void WriteVarInt32(int value)
        {
            // ZigZag encoding keeps small negative numbers compact.
            uint zigzag = (uint)((value << 1) ^ (value >> 31));
            WriteVarUInt32(zigzag);
        }

        public void WriteVarInt64(long value)
        {
            ulong zigzag = (ulong)((value << 1) ^ (value >> 63));
            WriteVarUInt64(zigzag);
        }

        public void WriteFixedUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(m_Scratch, value);
            m_Stream.Write(m_Scratch, 0, 2);
        }

        public void WriteFixedInt16(short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(m_Scratch, value);
            m_Stream.Write(m_Scratch, 0, 2);
        }

        public void WriteFixedInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(m_Scratch, value);
            m_Stream.Write(m_Scratch, 0, 4);
        }

        public void WriteFixedInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(m_Scratch, value);
            m_Stream.Write(m_Scratch, 0, 8);
        }

        public void WriteSingle(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            WriteFixedInt32(bits);
        }

        public void WriteDouble(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            WriteFixedInt64(bits);
        }

        public void WriteBytesRaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return;
            }

            // NetStandard 2.1 Stream.Write supports ReadOnlySpan<byte>.
            m_Stream.Write(bytes);
        }

        public void WriteBytesWithLength(ReadOnlySpan<byte> bytes)
        {
            WriteVarUInt32(checked((uint)bytes.Length));
            WriteBytesRaw(bytes);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteVarInt32(-1);
                return;
            }

            if (value.Length == 0)
            {
                WriteVarInt32(0);
                return;
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (utf8.Length > WireLimits.MaxStringBytes)
            {
                throw new InvalidDataException($"String exceeds maximum allowed UTF-8 byte length ({utf8.Length} > {WireLimits.MaxStringBytes}).");
            }

            WriteVarInt32(utf8.Length);
            m_Stream.Write(utf8, 0, utf8.Length);
        }

        public void WriteVector2d(Vector2d value)
        {
            WriteDouble(value.x);
            WriteDouble(value.y);
        }

        public void WriteVector3d(Vector3d value)
        {
            WriteDouble(value.x);
            WriteDouble(value.y);
            WriteDouble(value.z);
        }

        public void WriteVector4d(Vector4d value)
        {
            WriteDouble(value.x);
            WriteDouble(value.y);
            WriteDouble(value.z);
            WriteDouble(value.w);
        }

        public void WriteMatrix4x4(FbxMatrix4x4 value)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    WriteDouble(value[r, c]);
                }
            }
        }
    }

    /// <summary>
    /// Zero-allocation ReadOnlySpan&lt;byte&gt; binary &amp; LEB128 varint reader with strict bounds validation.
    /// </summary>
    public ref struct WireReader
    {
        private readonly ReadOnlySpan<byte> m_Buffer;
        private int m_Position;

        public int Position => m_Position;
        public int Length => m_Buffer.Length;
        public int Remaining => m_Buffer.Length - m_Position;
        public bool IsAtEnd => m_Position >= m_Buffer.Length;

        public WireReader(ReadOnlySpan<byte> buffer)
        {
            m_Buffer = buffer;
            m_Position = 0;
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return m_Buffer[m_Position++];
        }

        public bool ReadBool()
        {
            byte value = ReadByte();
            if (value > 1)
            {
                throw new InvalidDataException($"Invalid boolean wire value {value}.");
            }

            return value == 1;
        }

        public bool TryReadTag(out int fieldTag, out WireType wireType)
        {
            if (IsAtEnd)
            {
                fieldTag = 0;
                wireType = WireType.Varint;
                return false;
            }

            uint encoded = ReadVarUInt32();
            fieldTag = (int)(encoded >> 3);
            uint rawWire = encoded & 0x7u;
            if (fieldTag <= 0 || rawWire > (uint)WireType.LengthDelimited)
            {
                throw new InvalidDataException($"Malformed field header (tag={fieldTag}, wireType={rawWire}).");
            }

            wireType = (WireType)rawWire;
            return true;
        }

        public void SkipField(WireType wireType)
        {
            switch (wireType)
            {
                case WireType.Varint:
                    ReadVarUInt64();
                    break;
                case WireType.Fixed32:
                    EnsureAvailable(4);
                    m_Position += 4;
                    break;
                case WireType.Fixed64:
                    EnsureAvailable(8);
                    m_Position += 8;
                    break;
                case WireType.LengthDelimited:
                    int length = ReadCheckedLength(WireLimits.MaxByteArrayBytes);
                    EnsureAvailable(length);
                    m_Position += length;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported wire type {wireType}.");
            }
        }

        public uint ReadVarUInt32()
        {
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = ReadByte();
                if (i == 4 && (b & 0xF0) != 0)
                {
                    throw new InvalidDataException("VarUInt32 overflow.");
                }

                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("Unterminated VarUInt32.");
        }

        public ulong ReadVarUInt64()
        {
            ulong result = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                byte b = ReadByte();
                if (i == 9 && (b & 0xFE) != 0)
                {
                    throw new InvalidDataException("VarUInt64 overflow.");
                }

                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("Unterminated VarUInt64.");
        }

        public int ReadVarInt32()
        {
            uint zigzag = ReadVarUInt32();
            return (int)((zigzag >> 1) ^ (uint)-(int)(zigzag & 1u));
        }

        public long ReadVarInt64()
        {
            ulong zigzag = ReadVarUInt64();
            return (long)((zigzag >> 1) ^ (ulong)-(long)(zigzag & 1UL));
        }

        public int ReadCheckedLength(int maxAllowed)
        {
            uint raw = ReadVarUInt32();
            if (raw > (uint)maxAllowed || raw > (uint)Remaining)
            {
                throw new InvalidDataException($"Length {raw} exceeds limit ({maxAllowed}) or remaining bytes ({Remaining}).");
            }

            return (int)raw;
        }

        public ushort ReadFixedUInt16()
        {
            EnsureAvailable(2);
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(m_Buffer.Slice(m_Position, 2));
            m_Position += 2;
            return v;
        }

        public short ReadFixedInt16()
        {
            EnsureAvailable(2);
            short v = BinaryPrimitives.ReadInt16LittleEndian(m_Buffer.Slice(m_Position, 2));
            m_Position += 2;
            return v;
        }

        public int ReadFixedInt32()
        {
            EnsureAvailable(4);
            int v = BinaryPrimitives.ReadInt32LittleEndian(m_Buffer.Slice(m_Position, 4));
            m_Position += 4;
            return v;
        }

        public long ReadFixedInt64()
        {
            EnsureAvailable(8);
            long v = BinaryPrimitives.ReadInt64LittleEndian(m_Buffer.Slice(m_Position, 8));
            m_Position += 8;
            return v;
        }

        public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadFixedInt32());

        public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadFixedInt64());

        public ReadOnlySpan<byte> ReadSlice(int length)
        {
            if (length < 0)
            {
                throw new InvalidDataException($"Negative slice length {length}.");
            }

            EnsureAvailable(length);
            var slice = m_Buffer.Slice(m_Position, length);
            m_Position += length;
            return slice;
        }

        public ReadOnlySpan<byte> ReadLengthDelimitedSlice(int maxAllowed = WireLimits.MaxByteArrayBytes)
        {
            int length = ReadCheckedLength(maxAllowed);
            return ReadSlice(length);
        }

        public string ReadString()
        {
            int length = ReadVarInt32();
            if (length == -1)
            {
                return null;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            if (length < -1 || length > WireLimits.MaxStringBytes)
            {
                throw new InvalidDataException($"Invalid UTF-8 string length {length}.");
            }

            var slice = ReadSlice(length);
            return Encoding.UTF8.GetString(slice);
        }

        public Vector2d ReadVector2d() => new Vector2d(ReadDouble(), ReadDouble());
        public Vector3d ReadVector3d() => new Vector3d(ReadDouble(), ReadDouble(), ReadDouble());
        public Vector4d ReadVector4d() => new Vector4d(ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble());

        public FbxMatrix4x4 ReadMatrix4x4()
        {
            return new FbxMatrix4x4(
                ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
                ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
                ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
                ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble());
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException($"Unexpected end of wire buffer (needed {count} bytes, remaining {Remaining}).");
            }
        }
    }
}
