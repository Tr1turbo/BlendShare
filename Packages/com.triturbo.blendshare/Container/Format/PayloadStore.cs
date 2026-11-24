using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Container.Format
{
    public sealed class PayloadStore : ILazyPayloadSource, IRawPayloadCopySource, IDisposable
    {
        private sealed class Entry
        {
            internal PayloadRef Reference;
            internal Type Type;
            internal object Value;
            internal long Bytes;
            internal int Pins;
            internal LinkedListNode<Entry> Node;
        }

        private readonly object m_Gate = new object();
        private readonly IContainerStreamProvider m_Streams;
        private readonly IBlendShareRecordSerializer m_Serializer;
        private readonly BlendShareContainerReader m_Reader;
        private readonly long m_Budget, m_SingleLimit;
        private readonly Dictionary<(string, int, Type, string, int), Entry> m_Cache =
            new Dictionary<(string, int, Type, string, int), Entry>();
        private readonly LinkedList<Entry> m_Lru = new LinkedList<Entry>();
        private readonly HashSet<string> m_Invalidated = new HashSet<string>(StringComparer.Ordinal);
        private long m_Decoded, m_Pinned;
        private bool m_Disposed;

        public long CurrentDecodedBytes { get { lock (m_Gate) return m_Decoded; } }
        public long PinnedDecodedBytes { get { lock (m_Gate) return m_Pinned; } }

        public PayloadStore(IContainerStreamProvider streamProvider, IBlendShareRecordSerializer serializer = null,
            long maxBudgetBytes = 64L << 20, long maxSinglePayloadBytes = 16L << 20)
        {
            if (maxBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBudgetBytes));
            if (maxSinglePayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxSinglePayloadBytes));
            m_Streams = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
            m_Serializer = serializer ?? BlendShareSerializer.Default;
            m_Reader = new BlendShareContainerReader(m_Serializer);
            m_Budget = maxBudgetBytes;
            m_SingleLimit = maxSinglePayloadBytes;
        }

        public PayloadLease<T> Acquire<T>(PayloadRef payloadRef) where T : class
        {
            lock (m_Gate)
            {
                CheckReference(payloadRef);
                CheckSingle(payloadRef.UncompressedBytes);
                var key = (payloadRef.SourceRevision, payloadRef.PayloadIndex, typeof(T), payloadRef.CodecId, payloadRef.CodecVersion);
                if (!m_Cache.TryGetValue(key, out var entry))
                {
                    long maxAvailableForDecode = Math.Min(m_SingleLimit, m_Budget - m_Pinned);
                    if (maxAvailableForDecode <= 0L)
                    {
                        throw new PayloadBudgetExceededException(Math.Max(1L, payloadRef.UncompressedBytes), Math.Max(0L, maxAvailableForDecode));
                    }

                    var bytes = ReadBytes(payloadRef, out _);
                    // Enforce strict pre-allocation DecodeBudget inside the decoder using exact CodecId + CodecVersion.
                    var value = m_Serializer.DeserializePayloadRecord<T>(
                        bytes,
                        payloadRef.CodecId,
                        payloadRef.CodecVersion,
                        maxAvailableForDecode,
                        out long size);

                    if (value == null) throw new InvalidDataException("Decoded null payload.");
                    CheckSingle(size);
                    MakeRoom(size);

                    entry = new Entry { Reference = payloadRef, Type = typeof(T), Value = value, Bytes = size };
                    m_Cache.Add(key, entry);
                    entry.Node = m_Lru.AddLast(entry);
                    m_Decoded = checked(m_Decoded + size);
                }
                else
                {
                    if (entry.Reference != payloadRef) throw new InvalidDataException("Inconsistent cached payload reference.");
                    m_Lru.Remove(entry.Node);
                    m_Lru.AddLast(entry.Node);
                }

                // Hand out an isolated lease view (zero-copy for immutable IndexedBuffer<T>, defensive clone for wrappers/mutable arrays).
                T leasedView = m_Serializer.CloneForLease((T)entry.Value);

                if (entry.Pins++ == 0) m_Pinned = checked(m_Pinned + entry.Bytes);
                bool released = false;
                return new PayloadLease<T>(leasedView, () =>
                {
                    lock (m_Gate)
                    {
                        if (released) return;
                        released = true;
                        if (--entry.Pins != 0) return;
                        m_Pinned -= entry.Bytes;
                        if (m_Disposed || m_Invalidated.Contains(entry.Reference.SourceRevision)) Remove(entry);
                    }
                });
            }
        }

        public bool TryCopyRawPayload(PayloadRef sourceRef, Stream destination, out byte[] sha256, out long uncompressedBytes)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            byte[] bytes;
            lock (m_Gate)
            {
                CheckReference(sourceRef);
                bytes = ReadBytes(sourceRef, out var entry);
                sha256 = entry.Sha256;
                uncompressedBytes = entry.UncompressedBytes;
            }
            destination.Write(bytes, 0, bytes.Length);
            return true;
        }

        private byte[] ReadBytes(PayloadRef reference, out ContainerIndexEntry entry)
        {
            using var stream = m_Streams.OpenRead(reference.SourceRevision, out var toc);
            if (stream == null || toc == null || toc.SourceRevision != reference.SourceRevision ||
                !toc.TryGetPayload(reference.PayloadIndex, out entry) || toc.ResolvePayloadRef(reference.PayloadIndex) != reference)
                throw new InvalidDataException("Missing or stale payload reference.");
            FormatIO.Check(entry.Slice.Length <= entry.UncompressedBytes + 6, "Oversized payload envelope.");
            var result = m_Reader.ReadVerifiedPayloadBytes(stream, toc, reference.PayloadIndex);
            if (!result.Success) throw new InvalidDataException($"{result.Status}: {result.Message}");
            return result.Value;
        }

        private void CheckReference(PayloadRef reference)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(PayloadStore));
            if (!reference.IsValid || m_Invalidated.Contains(reference.SourceRevision))
                throw new InvalidDataException("Invalid or invalidated payload revision.");
        }

        private void CheckSingle(long size)
        {
            if (size < 0 || size > m_SingleLimit)
                throw new PayloadBudgetExceededException(size, m_SingleLimit);
        }

        private void MakeRoom(long required)
        {
            if (required > m_Budget - m_Pinned)
                throw new PayloadBudgetExceededException(required, Math.Max(0L, m_Budget - m_Pinned));
            var node = m_Lru.First;
            while (node != null && m_Decoded > m_Budget - required)
            {
                var current = node;
                node = node.Next;
                if (current.Value.Pins == 0) Remove(current.Value);
            }
        }

        private void Remove(Entry entry)
        {
            var r = entry.Reference;
            m_Cache.Remove((r.SourceRevision, r.PayloadIndex, entry.Type, r.CodecId, r.CodecVersion));
            m_Lru.Remove(entry.Node);
            m_Decoded -= entry.Bytes;
        }

        public void InvalidateRevision(string sourceRevision)
        {
            lock (m_Gate)
            {
                m_Invalidated.Add(sourceRevision);
                foreach (var e in m_Lru.Where(e => e.Reference.SourceRevision == sourceRevision && e.Pins == 0).ToArray())
                    Remove(e);
            }
        }

        public void Dispose()
        {
            lock (m_Gate)
            {
                m_Disposed = true;
                foreach (var e in m_Lru.Where(e => e.Pins == 0).ToArray()) Remove(e);
            }
        }
    }
}
