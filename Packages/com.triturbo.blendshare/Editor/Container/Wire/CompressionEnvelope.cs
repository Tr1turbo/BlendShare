using System;
using System.IO;
using System.IO.Compression;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Container.Wire
{
    /// <summary>
    /// Lossless outer compression envelope with strict pre-allocation uncompressed size cap.
    /// Format: [1 byte algorithm][VarUInt32 uncompressedBytes][compressed payload].
    /// </summary>
    public static class CompressionEnvelope
    {
        private static readonly Lazy<bool> s_BrotliSupported = new Lazy<bool>(ProbeBrotliSupport);

        public static bool IsBrotliSupported => s_BrotliSupported.Value;

        public static void Compress(ReadOnlySpan<byte> rawBytes, Stream output, CompressionAlgorithm requestedAlgorithm)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            var algorithm = requestedAlgorithm;
            if (algorithm == CompressionAlgorithm.Brotli && !IsBrotliSupported)
            {
                algorithm = CompressionAlgorithm.Deflate;
            }

            if (rawBytes.Length < 32 || algorithm == CompressionAlgorithm.None)
            {
                WriteUncompressed(rawBytes, output);
                return;
            }

            using (var compressed = new MemoryStream(rawBytes.Length))
            {
                if (algorithm == CompressionAlgorithm.Deflate)
                {
                    using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(rawBytes);
                    }
                }
                else if (algorithm == CompressionAlgorithm.Brotli)
                {
                    using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        brotli.Write(rawBytes);
                    }
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(requestedAlgorithm), $"Unsupported compression algorithm {requestedAlgorithm}.");
                }

                // Only store compressed bytes if they strictly save space.
                if (compressed.Length >= rawBytes.Length)
                {
                    WriteUncompressed(rawBytes, output);
                    return;
                }

                var writer = new WireWriter(output);
                writer.WriteByte((byte)algorithm);
                writer.WriteVarUInt32(checked((uint)rawBytes.Length));
                compressed.Position = 0;
                compressed.CopyTo(output);
            }
        }

        public static long PeekUncompressedLength(ReadOnlySpan<byte> envelopeBytes)
        {
            var reader = new WireReader(envelopeBytes);
            byte algoByte = reader.ReadByte();
            if (algoByte > (byte)CompressionAlgorithm.Brotli)
            {
                throw new InvalidDataException($"Unsupported compression envelope algorithm id {algoByte}.");
            }

            uint uncompressedLength = reader.ReadVarUInt32();
            if (uncompressedLength > WireLimits.MaxUncompressedEnvelopeBytes)
            {
                throw new InvalidDataException($"Envelope uncompressed length {uncompressedLength} exceeds cap ({WireLimits.MaxUncompressedEnvelopeBytes}).");
            }

            return uncompressedLength;
        }

        public static byte[] Decompress(ReadOnlySpan<byte> envelopeBytes, long maxAllowedUncompressedBytes = WireLimits.MaxUncompressedEnvelopeBytes)
        {
            var reader = new WireReader(envelopeBytes);
            byte algoByte = reader.ReadByte();
            if (algoByte > (byte)CompressionAlgorithm.Brotli)
            {
                throw new InvalidDataException($"Unsupported compression envelope algorithm id {algoByte}.");
            }

            var algorithm = (CompressionAlgorithm)algoByte;
            uint declaredLength = reader.ReadVarUInt32();
            long cap = Math.Min(maxAllowedUncompressedBytes, WireLimits.MaxUncompressedEnvelopeBytes);
            if (declaredLength > cap)
            {
                throw new InvalidDataException($"Declared uncompressed payload size {declaredLength} exceeds cap ({cap}).");
            }

            int expected = checked((int)declaredLength);
            var body = reader.ReadSlice(reader.Remaining);

            if (algorithm == CompressionAlgorithm.None)
            {
                if (body.Length != expected)
                {
                    throw new InvalidDataException($"Uncompressed envelope byte length ({body.Length}) != declared length ({expected}).");
                }

                return body.ToArray();
            }

            if (algorithm == CompressionAlgorithm.Brotli && !IsBrotliSupported)
            {
                throw new NotSupportedException("Brotli decompression is unavailable on this runtime platform.");
            }

            using (var input = new MemoryStream(body.ToArray(), writable: false))
            using (Stream decompressor = algorithm == CompressionAlgorithm.Deflate
                ? (Stream)new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false)
                : new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false))
            {
                var output = new byte[expected];
                int totalRead = 0;
                while (totalRead < expected)
                {
                    int read = decompressor.Read(output, totalRead, expected - totalRead);
                    if (read == 0)
                    {
                        throw new InvalidDataException($"Compressed payload truncated (read {totalRead} of {expected} bytes).");
                    }

                    totalRead += read;
                }

                // Ensure no trailing uncompressed bytes beyond declared length (zip-bomb guard).
                if (decompressor.ReadByte() != -1)
                {
                    throw new InvalidDataException("Compressed payload expanded beyond its declared uncompressed length.");
                }

                return output;
            }
        }

        private static void WriteUncompressed(ReadOnlySpan<byte> rawBytes, Stream output)
        {
            var writer = new WireWriter(output);
            writer.WriteByte((byte)CompressionAlgorithm.None);
            writer.WriteVarUInt32(checked((uint)rawBytes.Length));
            writer.WriteBytesRaw(rawBytes);
        }

        private static bool ProbeBrotliSupport()
        {
            try
            {
                byte[] sample = new byte[64];
                for (int i = 0; i < sample.Length; i++)
                {
                    sample[i] = (byte)(i & 0x7);
                }

                byte[] compressed;
                using (var ms = new MemoryStream())
                {
                    using (var bs = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        bs.Write(sample, 0, sample.Length);
                    }

                    compressed = ms.ToArray();
                }

                using (var ms = new MemoryStream(compressed))
                using (var bs = new BrotliStream(ms, CompressionMode.Decompress))
                {
                    byte[] roundTrip = new byte[sample.Length];
                    int read = 0;
                    while (read < roundTrip.Length)
                    {
                        int n = bs.Read(roundTrip, read, roundTrip.Length - read);
                        if (n == 0)
                        {
                            return false;
                        }

                        read += n;
                    }

                    for (int i = 0; i < sample.Length; i++)
                    {
                        if (roundTrip[i] != sample[i])
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
