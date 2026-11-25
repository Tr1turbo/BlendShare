using System;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Model.Serialization
{
    /// <summary>
    /// Explicit lifecycle state of a <see cref="LazyPayload{T}"/>.
    /// </summary>
    public enum LazyPayloadState : byte
    {
        /// <summary>No payload value or reference is attached.</summary>
        Absent = 0,

        /// <summary>In-memory value produced during extraction or authoring before container serialization.</summary>
        Value = 1,

        /// <summary>Reference to a compressed payload entry inside a verified BLSH container revision.</summary>
        Ref = 2
    }

    /// <summary>
    /// Narrow read-only payload acquisition contract implemented by Container.Editor's PayloadStore.
    /// Allows Model.Editor types to acquire typed leases without referencing Container.Editor.
    /// </summary>
    public interface ILazyPayloadSource
    {
        /// <summary>
        /// Acquires a pinned, read-only lease to the decoded payload referenced by <paramref name="payloadRef"/>.
        /// Caller must dispose the returned lease when done.
        /// </summary>
        PayloadLease<T> Acquire<T>(PayloadRef payloadRef) where T : class;
    }

    /// <summary>
    /// Scoped lease keeping a decoded payload pinned in PayloadStore until disposed.
    /// Uses a shared disposal handle so struct copies share one lifetime and cannot observe an unpinned value after disposal.
    /// </summary>
    public readonly struct PayloadLease<T> : IDisposable where T : class
    {
        private sealed class LeaseHandle
        {
            public T Value;
            public Action OnDispose;
            public bool Disposed;
        }

        private readonly LeaseHandle m_Handle;

        public T Value
        {
            get
            {
                if (m_Handle == null || m_Handle.Disposed)
                {
                    throw new ObjectDisposedException(nameof(PayloadLease<T>));
                }

                return m_Handle.Value;
            }
        }

        public bool IsValid => m_Handle != null && !m_Handle.Disposed && m_Handle.Value != null;

        public PayloadLease(T value, Action onDispose = null)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            m_Handle = new LeaseHandle
            {
                Value = value,
                OnDispose = onDispose,
                Disposed = false
            };
        }

        public void Dispose()
        {
            if (m_Handle == null || m_Handle.Disposed)
            {
                return;
            }

            m_Handle.Disposed = true;
            m_Handle.Value = null;
            var callback = m_Handle.OnDispose;
            m_Handle.OnDispose = null;
            callback?.Invoke();
        }
    }

    /// <summary>
    /// Explicit 3-state container (Absent | Value | Ref) for heavy feature payloads.
    /// </summary>
    public sealed class LazyPayload<T> where T : class
    {
        public static readonly LazyPayload<T> Empty = new LazyPayload<T>(LazyPayloadState.Absent, null, PayloadRef.Invalid);

        private readonly T m_Value;
        private readonly PayloadRef m_Reference;

        public LazyPayloadState State { get; }
        public bool HasPayload => State != LazyPayloadState.Absent;

        public T DirectValue
        {
            get
            {
                if (State != LazyPayloadState.Value)
                {
                    throw new InvalidOperationException($"LazyPayload<{typeof(T).Name}> is in state '{State}', not 'Value'.");
                }

                return m_Value;
            }
        }

        public PayloadRef Reference
        {
            get
            {
                if (State != LazyPayloadState.Ref)
                {
                    throw new InvalidOperationException($"LazyPayload<{typeof(T).Name}> is in state '{State}', not 'Ref'.");
                }

                return m_Reference;
            }
        }

        private LazyPayload(LazyPayloadState state, T value, PayloadRef reference)
        {
            State = state;
            m_Value = value;
            m_Reference = reference;
        }

        public static LazyPayload<T> FromValue(T value)
        {
            if (value == null)
            {
                return Empty;
            }

            return new LazyPayload<T>(LazyPayloadState.Value, value, PayloadRef.Invalid);
        }

        public static LazyPayload<T> FromRef(PayloadRef reference)
        {
            if (!reference.IsValid)
            {
                throw new ArgumentException("PayloadRef must be valid.", nameof(reference));
            }

            return new LazyPayload<T>(LazyPayloadState.Ref, null, reference);
        }

        /// <summary>
        /// Acquires a scoped lease to the payload value.
        /// For Value-backed payloads, returns a non-cached lease immediately without hitting the store.
        /// For Ref-backed payloads, delegates to <paramref name="source"/>.
        /// </summary>
        public PayloadLease<T> Acquire(ILazyPayloadSource source)
        {
            switch (State)
            {
                case LazyPayloadState.Value:
                    return new PayloadLease<T>(m_Value);
                case LazyPayloadState.Ref:
                    if (source == null)
                    {
                        throw new ArgumentNullException(nameof(source), "An ILazyPayloadSource is required to acquire a Ref-backed payload.");
                    }

                    return source.Acquire<T>(m_Reference);
                default:
                    throw new InvalidOperationException($"Cannot acquire an Absent LazyPayload<{typeof(T).Name}>.");
            }
        }
    }
}
