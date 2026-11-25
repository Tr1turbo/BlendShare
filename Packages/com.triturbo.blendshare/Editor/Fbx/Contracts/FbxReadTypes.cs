using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Outcome of an FBX read operation.</summary>
    public enum FbxReadStatus
    {
        /// <summary>Success.</summary>
        Success,
        /// <summary>File not found.</summary>
        FileNotFound,
        /// <summary>Parse error.</summary>
        ParseError,
        /// <summary>Node not found.</summary>
        NodeNotFound,
        /// <summary>Section unavailable.</summary>
        SectionUnavailable,
        /// <summary>Invalid argument.</summary>
        InvalidArgument
    }

    /// <summary>Importance of a reader diagnostic.</summary>
    public enum FbxDiagnosticSeverity
    {
        /// <summary>Info.</summary>
        Info,
        /// <summary>Warning.</summary>
        Warning,
        /// <summary>Error.</summary>
        Error
    }

    /// <summary>A diagnostic associated with a read operation.</summary>
    public sealed class FbxDiagnostic
    {
        /// <summary>Severity.</summary>
        public FbxDiagnosticSeverity Severity { get; }
        /// <summary>Status.</summary>
        public FbxReadStatus Status { get; }
        /// <summary>Message.</summary>
        public string Message { get; }

        /// <summary>Creates a new FbxDiagnostic from the supplied values.</summary>
        public FbxDiagnostic(FbxDiagnosticSeverity severity, FbxReadStatus status, string message)
        {
            Severity = severity;
            Status = status;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>The outcome, value and diagnostics of a read operation.</summary>
    public sealed class FbxReadResult<T>
    {
        /// <summary>Success.</summary>
        public bool Success => Status == FbxReadStatus.Success;
        /// <summary>Value.</summary>
        public T Value { get; }
        /// <summary>Status.</summary>
        public FbxReadStatus Status { get; }
        /// <summary>Message.</summary>
        public string Message { get; }
        /// <summary>Diagnostics recorded during the read.</summary>
        public IReadOnlyList<FbxDiagnostic> Diagnostics { get; }

        /// <summary>Creates a new FbxReadResult from the supplied values.</summary>
        public FbxReadResult(
            FbxReadStatus status,
            T value,
            string message = null,
            IEnumerable<FbxDiagnostic> diagnostics = null)
        {
            Status = status;
            Value = value;
            Message = message ?? string.Empty;
            Diagnostics = FbxReadOnlyList.ToReadOnly(diagnostics);
        }

        /// <summary>Creates a successful result containing the supplied value.</summary>
        public static FbxReadResult<T> Succeeded(T value, IEnumerable<FbxDiagnostic> diagnostics = null)
        {
            return new FbxReadResult<T>(FbxReadStatus.Success, value, null, diagnostics);
        }

        /// <summary>Creates a failed result carrying the supplied status.</summary>
        public static FbxReadResult<T> Failed(
            FbxReadStatus status,
            string message,
            IEnumerable<FbxDiagnostic> diagnostics = null)
        {
            return new FbxReadResult<T>(status, default, message, diagnostics);
        }
    }

    /// <summary>A read failure with structured diagnostics.</summary>
    public sealed class FbxReadException : Exception
    {
        /// <summary>Status.</summary>
        public FbxReadStatus Status { get; }
        /// <summary>Diagnostics recorded during the read.</summary>
        public IReadOnlyList<FbxDiagnostic> Diagnostics { get; }

        /// <summary>Creates a new FbxReadException from the supplied values.</summary>
        public FbxReadException(FbxReadStatus status, string message, IEnumerable<FbxDiagnostic> diagnostics = null)
            : base(message)
        {
            Status = status;
            Diagnostics = FbxReadOnlyList.ToReadOnly(diagnostics);
        }
    }

    internal static class FbxReadOnlyList
    {
        /// <summary>Snapshots the sequence as a read-only list.</summary>
        public static IReadOnlyList<T> ToReadOnly<T>(IEnumerable<T> values)
        {
            if (values == null)
            {
                return Array.AsReadOnly(Array.Empty<T>());
            }

            if (values is T[] array)
            {
                return Array.AsReadOnly((T[])array.Clone());
            }

            if (values is List<T> list)
            {
                return new ReadOnlyCollection<T>(list.ToArray());
            }

            return new ReadOnlyCollection<T>(new List<T>(values));
        }
    }
}
