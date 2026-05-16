using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Triturbo.Fbx
{
    public enum FbxReadStatus
    {
        Success,
        FileNotFound,
        NotBinaryFbx,
        UnsupportedVersion,
        ParseError,
        NodeNotFound,
        MeshNotFound,
        AmbiguousMesh,
        SectionUnavailable,
        InvalidArgument
    }

    public enum FbxDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class FbxDiagnostic
    {
        public FbxDiagnosticSeverity Severity { get; }
        public FbxReadStatus Status { get; }
        public string Message { get; }

        public FbxDiagnostic(FbxDiagnosticSeverity severity, FbxReadStatus status, string message)
        {
            Severity = severity;
            Status = status;
            Message = message ?? string.Empty;
        }
    }

    public sealed class FbxReadResult<T>
    {
        public bool Success => Status == FbxReadStatus.Success;
        public T Value { get; }
        public FbxReadStatus Status { get; }
        public string Message { get; }
        public IReadOnlyList<FbxDiagnostic> Diagnostics { get; }
        public IReadOnlyList<FbxMeshDescriptor> CandidateMeshes { get; }

        public FbxReadResult(
            FbxReadStatus status,
            T value,
            string message = null,
            IEnumerable<FbxDiagnostic> diagnostics = null,
            IEnumerable<FbxMeshDescriptor> candidateMeshes = null)
        {
            Status = status;
            Value = value;
            Message = message ?? string.Empty;
            Diagnostics = FbxCollection.ToReadOnly(diagnostics);
            CandidateMeshes = FbxCollection.ToReadOnly(candidateMeshes);
        }

        public static FbxReadResult<T> Succeeded(T value, IEnumerable<FbxDiagnostic> diagnostics = null)
        {
            return new FbxReadResult<T>(FbxReadStatus.Success, value, null, diagnostics);
        }

        public static FbxReadResult<T> Failed(
            FbxReadStatus status,
            string message,
            IEnumerable<FbxDiagnostic> diagnostics = null,
            IEnumerable<FbxMeshDescriptor> candidateMeshes = null)
        {
            return new FbxReadResult<T>(status, default, message, diagnostics, candidateMeshes);
        }
    }

    public sealed class FbxReadException : Exception
    {
        public FbxReadStatus Status { get; }
        public IReadOnlyList<FbxDiagnostic> Diagnostics { get; }

        public FbxReadException(FbxReadStatus status, string message, IEnumerable<FbxDiagnostic> diagnostics = null)
            : base(message)
        {
            Status = status;
            Diagnostics = FbxCollection.ToReadOnly(diagnostics);
        }
    }

    internal static class FbxCollection
    {
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
