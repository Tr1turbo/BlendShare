using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Settings;

namespace Triturbo.BlendShare.Pipeline.Export
{
    public enum FbxExportDiagnosticSeverity { Info, Warning, Error }

    public readonly struct FbxExportDiagnostic
    {
        public FbxExportDiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string PatchId { get; }
        public string MeshId { get; }
        public string FeatureTypeId { get; }

        public FbxExportDiagnostic(
            FbxExportDiagnosticSeverity severity,
            string code,
            string message,
            string patchId = null,
            string meshId = null,
            string featureTypeId = null)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            PatchId = patchId;
            MeshId = meshId;
            FeatureTypeId = featureTypeId;
        }
    }

    public sealed class FbxExportBackendAvailability
    {
        public bool IsAvailable { get; }
        public string BackendId { get; }
        public string BackendDisplayName { get; }
        public string UnavailableReasonCode { get; }
        public string UnavailableDetails { get; }

        private FbxExportBackendAvailability(
            bool isAvailable,
            string backendId,
            string backendDisplayName,
            string unavailableReasonCode,
            string unavailableDetails)
        {
            IsAvailable = isAvailable;
            BackendId = backendId;
            BackendDisplayName = backendDisplayName;
            UnavailableReasonCode = unavailableReasonCode;
            UnavailableDetails = unavailableDetails;
        }

        public static FbxExportBackendAvailability Available(string id, string displayName) =>
            new FbxExportBackendAvailability(true, id, displayName, null, null);

        public static FbxExportBackendAvailability Unavailable(string reasonCode, string details) =>
            new FbxExportBackendAvailability(false, null, null, reasonCode, details);
    }

    public sealed class FbxPatchExportItem
    {
        public string PatchId { get; }
        public string SourceFilePath { get; }
        public ContainerToc Toc { get; }
        public PatchDocument Manifest { get; }
        public BlendShareImportSettings EffectiveSettings { get; }
        public IReadOnlyDictionary<string, string> MeshTargetNodeOverrides { get; }

        public FbxPatchExportItem(
            string patchId,
            string sourceFilePath,
            ContainerToc toc,
            PatchDocument manifest,
            BlendShareImportSettings effectiveSettings,
            IReadOnlyDictionary<string, string> meshTargetNodeOverrides = null)
        {
            PatchId = string.IsNullOrWhiteSpace(patchId) ? throw new ArgumentException(nameof(patchId)) : patchId;
            SourceFilePath = string.IsNullOrWhiteSpace(sourceFilePath) ? throw new ArgumentException(nameof(sourceFilePath)) : sourceFilePath;
            Toc = toc ?? throw new ArgumentNullException(nameof(toc));
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            EffectiveSettings = effectiveSettings ?? throw new ArgumentNullException(nameof(effectiveSettings));
            MeshTargetNodeOverrides = new ReadOnlyDictionary<string, string>((meshTargetNodeOverrides ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }


    public sealed class FbxPatchExportRequest
    {
        public string TargetFbxPath { get; set; }
        public IReadOnlyList<FbxPatchExportItem> Patches { get; set; } = Array.Empty<FbxPatchExportItem>();
        public bool CreateBackup { get; set; } = true;
        public bool VerifyRoundTripNumerics { get; set; } = true;
        public double NumericVerificationToleranceMeters { get; set; } = 1e-4d;
        public PayloadStore PayloadStore { get; set; }
    }

    public sealed class FbxPatchExportPreflightResult
    {
        public bool CanExport { get; }
        public IReadOnlyList<FbxExportDiagnostic> Diagnostics { get; }
        public IReadOnlyList<string> AffectedNodePaths { get; }
        public string SourceFileGenerationToken { get; }

        public FbxPatchExportPreflightResult(
            bool canExport,
            IReadOnlyList<FbxExportDiagnostic> diagnostics,
            IReadOnlyList<string> affectedNodePaths,
            string sourceFileGenerationToken)
        {
            CanExport = canExport;
            Diagnostics = diagnostics ?? Array.Empty<FbxExportDiagnostic>();
            AffectedNodePaths = affectedNodePaths ?? Array.Empty<string>();
            SourceFileGenerationToken = sourceFileGenerationToken;
        }
    }

    public sealed class FbxPatchExportResult
    {
        public bool Succeeded { get; }
        public string BackupPath { get; }
        public IReadOnlyList<FbxExportDiagnostic> Diagnostics { get; }
        public int AppliedPatchCount { get; }
        public int ModifiedMeshCount { get; }

        public FbxPatchExportResult(
            bool succeeded,
            string backupPath,
            IReadOnlyList<FbxExportDiagnostic> diagnostics,
            int appliedPatchCount,
            int modifiedMeshCount)
        {
            Succeeded = succeeded;
            BackupPath = backupPath;
            Diagnostics = diagnostics ?? Array.Empty<FbxExportDiagnostic>();
            AppliedPatchCount = appliedPatchCount;
            ModifiedMeshCount = modifiedMeshCount;
        }
    }


    public interface IFbxPatchExportBackend
    {
        string BackendId { get; }
        string DisplayName { get; }
        FbxPatchExportPreflightResult Preflight(FbxPatchExportRequest request);
        FbxPatchExportResult Export(FbxPatchExportRequest request);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareFbxExportBackendAttribute : Attribute
    {
        public Type BackendType { get; }
        public RegisterBlendShareFbxExportBackendAttribute(Type backendType) => BackendType = backendType;
    }

    public static class FbxPatchExportBackendRegistry
    {
        private const string k_Unavailable = "FBX_EXPORT_BACKEND_UNAVAILABLE";
        private const string k_Details = "No FBX export backend is registered.";
        private static readonly object s_Lock = new object();
        private static bool s_Discovered;
        private static IFbxPatchExportBackend s_Backend;

        private static void Discover()
        {
            if (s_Discovered) return;
            s_Discovered = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareFbxExportBackendAttribute[] attributes;
                try { attributes = assembly.GetCustomAttributes<RegisterBlendShareFbxExportBackendAttribute>().ToArray(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning(ex.Message); continue; }
                foreach (var attribute in attributes.OrderBy(a => a.BackendType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.BackendType;
                    if (type == null || type.IsAbstract || !typeof(IFbxPatchExportBackend).IsAssignableFrom(type)) continue;
                    try { Register((IFbxPatchExportBackend)Activator.CreateInstance(type)); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning(ex.Message); }
                }
            }
        }

        public static void Register(IFbxPatchExportBackend backend)
        {
            if (string.IsNullOrWhiteSpace(backend?.BackendId)) throw new ArgumentException(nameof(backend));
            lock (s_Lock)
            {
                Discover();
                if (s_Backend != null) throw new InvalidOperationException("An FBX export backend is already registered.");
                s_Backend = backend;
            }
        }

        public static bool Unregister(string backendId)
        {
            lock (s_Lock)
            {
                Discover();
                if (s_Backend == null || s_Backend.BackendId != backendId) return false;
                s_Backend = null;
                return true;
            }
        }

        public static bool TryGetBackend(out IFbxPatchExportBackend backend)
        {
            lock (s_Lock)
            {
                Discover();
                backend = s_Backend;
                return backend != null;
            }
        }

        public static FbxExportBackendAvailability QueryAvailability() =>
            TryGetBackend(out var backend)
                ? FbxExportBackendAvailability.Available(backend.BackendId, backend.DisplayName)
                : FbxExportBackendAvailability.Unavailable(k_Unavailable, k_Details);

        private static bool TryResolve(FbxPatchExportRequest request, out IFbxPatchExportBackend backend, out FbxExportDiagnostic error)
        {
            backend = null;
            if (request == null || string.IsNullOrWhiteSpace(request.TargetFbxPath) ||
                request.Patches == null || request.Patches.Count == 0 || request.Patches.Any(item => item == null) ||
                !(request.NumericVerificationToleranceMeters >= 0) || double.IsInfinity(request.NumericVerificationToleranceMeters))
                error = new FbxExportDiagnostic(FbxExportDiagnosticSeverity.Error, "FBX_EXPORT_INVALID_REQUEST", "Invalid export target, patch list or verification tolerance.");
            else if (!TryGetBackend(out backend))
                error = new FbxExportDiagnostic(FbxExportDiagnosticSeverity.Error, k_Unavailable, k_Details);
            else
            {
                error = default;
                return true;
            }
            return false;
        }

        public static FbxPatchExportPreflightResult Preflight(FbxPatchExportRequest request) =>
            TryResolve(request, out var backend, out var error) ? backend.Preflight(request) :
                new FbxPatchExportPreflightResult(false, new[] { error }, Array.Empty<string>(), null);

        public static FbxPatchExportResult Export(FbxPatchExportRequest request) =>
            TryResolve(request, out var backend, out var error) ? backend.Export(request) :
                new FbxPatchExportResult(false, null, new[] { error }, 0, 0);
    }
}
