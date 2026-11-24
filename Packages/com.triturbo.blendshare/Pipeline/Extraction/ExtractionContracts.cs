using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    public enum ExtractionDiagnosticSeverity : byte
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    public readonly struct ExtractionDiagnostic
    {
        public ExtractionDiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string MeshId { get; }
        public string FeatureTypeId { get; }

        public ExtractionDiagnostic(
            ExtractionDiagnosticSeverity severity,
            string code,
            string message,
            string meshId = null,
            string featureTypeId = null)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            MeshId = meshId ?? string.Empty;
            FeatureTypeId = featureTypeId ?? string.Empty;
        }

        public override string ToString()
        {
            string scope = string.IsNullOrEmpty(MeshId) ? string.Empty : $" [{MeshId}]";
            string feat = string.IsNullOrEmpty(FeatureTypeId) ? string.Empty : $" ({FeatureTypeId})";
            return $"[{Severity}] {Code}{scope}{feat}: {Message}";
        }
    }

    public enum FeatureExtractionStatus : byte
    {
        Succeeded = 0,
        Skipped = 1,
        Failed = 2
    }

    public sealed class FeatureExtractionResult
    {
        private static readonly ExtractionDiagnostic[] s_EmptyDiagnostics = Array.Empty<ExtractionDiagnostic>();

        public FeatureExtractionStatus Status { get; }
        public BlendShareFeatureData Feature { get; }
        public IReadOnlyList<ExtractionDiagnostic> Diagnostics { get; }

        public bool Succeeded => Status == FeatureExtractionStatus.Succeeded;

        private FeatureExtractionResult(
            FeatureExtractionStatus status,
            BlendShareFeatureData feature,
            IReadOnlyList<ExtractionDiagnostic> diagnostics)
        {
            Status = status;
            Feature = feature;
            Diagnostics = diagnostics ?? s_EmptyDiagnostics;
        }

        public static FeatureExtractionResult Success(
            BlendShareFeatureData feature,
            IReadOnlyList<ExtractionDiagnostic> diagnostics = null)
        {
            if (feature == null)
            {
                throw new ArgumentNullException(nameof(feature));
            }

            return new FeatureExtractionResult(FeatureExtractionStatus.Succeeded, feature, diagnostics);
        }

        public static FeatureExtractionResult Skipped(
            string code,
            string message,
            string meshId = null,
            string featureTypeId = null)
        {
            return new FeatureExtractionResult(
                FeatureExtractionStatus.Skipped,
                null,
                new[] { new ExtractionDiagnostic(ExtractionDiagnosticSeverity.Info, code, message, meshId, featureTypeId) });
        }

        public static FeatureExtractionResult Failed(
            string code,
            string message,
            string meshId = null,
            string featureTypeId = null,
            IReadOnlyList<ExtractionDiagnostic> additionalDiagnostics = null)
        {
            var list = new List<ExtractionDiagnostic>((additionalDiagnostics != null ? additionalDiagnostics.Count : 0) + 1);
            list.Add(new ExtractionDiagnostic(ExtractionDiagnosticSeverity.Error, code, message, meshId, featureTypeId));
            if (additionalDiagnostics != null)
            {
                for (int i = 0; i < additionalDiagnostics.Count; i++)
                {
                    list.Add(additionalDiagnostics[i]);
                }
            }

            return new FeatureExtractionResult(FeatureExtractionStatus.Failed, null, list);
        }
    }

    /// <summary>
    /// Base contract for per-feature extraction options passed into <see cref="IFeatureExtractor"/>.
    /// </summary>
    public interface IFeatureExtractionOptions
    {
        string FeatureTypeId { get; }
        bool Enabled { get; set; }
        bool ShouldExtractMesh(string normalizedNodePath);
    }

    /// <summary>
    /// Container for all per-feature extraction options keyed by FeatureTypeId.
    /// </summary>
    public sealed class ExtractionOptionsSet
    {
        private readonly Dictionary<string, IFeatureExtractionOptions> m_OptionsByTypeId =
            new Dictionary<string, IFeatureExtractionOptions>(StringComparer.Ordinal);

        public IReadOnlyCollection<IFeatureExtractionOptions> All => m_OptionsByTypeId.Values;

        public void Set(IFeatureExtractionOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.FeatureTypeId))
            {
                throw new ArgumentException("Options FeatureTypeId must be non-empty.", nameof(options));
            }

            m_OptionsByTypeId[options.FeatureTypeId] = options;
        }

        public bool TryGet(string featureTypeId, out IFeatureExtractionOptions options)
        {
            if (string.IsNullOrEmpty(featureTypeId))
            {
                options = null;
                return false;
            }

            return m_OptionsByTypeId.TryGetValue(featureTypeId, out options);
        }

        public bool TryGet<TOptions>(string featureTypeId, out TOptions options)
            where TOptions : class, IFeatureExtractionOptions
        {
            if (TryGet(featureTypeId, out var raw) && raw is TOptions typed)
            {
                options = typed;
                return true;
            }

            options = null;
            return false;
        }

        public TOptions GetOrCreate<TOptions>(string featureTypeId, Func<TOptions> factory)
            where TOptions : class, IFeatureExtractionOptions
        {
            if (TryGet<TOptions>(featureTypeId, out var existing))
            {
                return existing;
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            var created = factory();
            Set(created);
            return created;
        }
    }

    /// <summary>
    /// Per-mesh request for <see cref="PatchExtractionPipeline"/>.
    /// </summary>
    public sealed class MeshExtractionRequest
    {
        public string NodePath { get; }
        public string MeshId { get; }
        public string DisplayName { get; }

        /// <summary>
        /// Optional affine alignment offset applied in Origin FBX native local space AFTER converting Source raw space
        /// into Origin raw space (<c>L = W * SourceToOriginRawOffset</c>). Defaults to Identity.
        /// </summary>
        public FbxMatrix4x4 SourceToOriginRawOffset { get; }

        /// <summary>
        /// Optional explicit control-point weld groups (in Origin control-point index space) overriding resolver output.
        /// </summary>
        public IReadOnlyList<int[]> WeldGroupsOverride { get; }

        public MeshExtractionRequest(
            string nodePath,
            string meshId = null,
            string displayName = null,
            IReadOnlyList<int[]> weldGroupsOverride = null,
            FbxMatrix4x4? sourceToOriginRawOffset = null)
        {
            string normalizedPath = FbxNameUtility.NormalizePath(nodePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                throw new ArgumentException("Mesh node path must be non-empty.", nameof(nodePath));
            }

            NodePath = normalizedPath;
            MeshId = string.IsNullOrWhiteSpace(meshId) ? normalizedPath : meshId.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? ExtractLeafName(normalizedPath) : displayName.Trim();
            SourceToOriginRawOffset = sourceToOriginRawOffset ?? FbxMatrix4x4.Identity;
            WeldGroupsOverride = weldGroupsOverride;
        }

        private static string ExtractLeafName(string normalizedPath)
        {
            int slash = normalizedPath.LastIndexOf('/');
            return slash >= 0 && slash + 1 < normalizedPath.Length
                ? normalizedPath.Substring(slash + 1)
                : normalizedPath;
        }
    }

    /// <summary>
    /// Pure extraction request operating on loaded <see cref="IFbxScene"/> instances.
    /// </summary>
    public sealed class PatchExtractionRequest
    {
        public string PatchId { get; set; } = string.Empty;
        public IFbxScene SourceScene { get; set; }
        public IFbxScene OriginScene { get; set; }
        public IReadOnlyList<MeshExtractionRequest> Meshes { get; set; } = Array.Empty<MeshExtractionRequest>();
        public ExtractionOptionsSet FeatureOptions { get; set; } = new ExtractionOptionsSet();
        public PayloadEncodeOptions EncodeOptions { get; set; } = PayloadEncodeOptions.Default;

        /// <summary>
        /// Optional callback resolving Unity WeldVertices control-point groups for each raw origin mesh.
        /// </summary>
        public Func<IFbxMesh, IReadOnlyList<int[]>> WeldGroupResolver { get; set; }

        /// <summary>
        /// Optional callback building an embedded <see cref="OriginBindingDocument"/> for each extracted mesh
        /// so downstream consumers can bind without the base FBX.
        /// </summary>
        public Func<MeshExtractionRequest, IFbxMesh, FbxSceneMetadata, OriginBindingDocument> OriginBindingBuilder { get; set; }
    }

    /// <summary>
    /// Result of a patch extraction pass.
    /// </summary>
    public sealed class PatchExtractionResult
    {
        public bool Succeeded { get; }
        public PatchDocument Document { get; }
        public IReadOnlyList<ExtractionDiagnostic> Diagnostics { get; }

        public PatchExtractionResult(
            bool succeeded,
            PatchDocument document,
            IReadOnlyList<ExtractionDiagnostic> diagnostics)
        {
            Succeeded = succeeded;
            Document = document;
            Diagnostics = diagnostics ?? Array.Empty<ExtractionDiagnostic>();
        }
    }

    /// <summary>
    /// Describes one source FBX bone node registered into the patch's shared <see cref="SkeletonDocument"/> during extraction.
    /// </summary>
    public readonly struct SkeletonBoneRegistration
    {
        public string BonePath { get; }
        public IFbxNode SourceNode { get; }
        public bool CreateIfMissing { get; }

        public SkeletonBoneRegistration(string bonePath, IFbxNode sourceNode, bool createIfMissing)
        {
            BonePath = FbxNameUtility.NormalizePath(bonePath ?? string.Empty);
            SourceNode = sourceNode;
            CreateIfMissing = createIfMissing;
        }
    }

    /// <summary>
    /// Per-mesh extraction context providing Origin and Source meshes aligned into the patch's stored Origin FBX
    /// local space, keeping scene/world basis conversion (<c>W</c>) explicitly separated from per-mesh local alignment (<c>L</c>).
    /// </summary>
    public sealed class MeshExtractionContext
    {
        public string MeshId { get; }
        public string NodePath { get; }
        public string DisplayName { get; }
        public IFbxScene SourceScene { get; }
        public IFbxScene OriginScene { get; }
        public IFbxMesh RawSourceMesh { get; }
        public IFbxMesh RawOriginMesh { get; }

        /// <summary>
        /// Lazily normalized Source mesh aligned into the stored mesh frame via per-mesh transform <c>L</c>
        /// (with optional mesh-local alignment offset and welding applied).
        /// </summary>
        public IFbxMeshGeometry NormalizedSourceMesh { get; }

        /// <summary>
        /// Lazily normalized Origin mesh in its native Origin FBX local frame (<c>Identity</c> transform).
        /// </summary>
        public IFbxMeshGeometry NormalizedOriginMesh { get; }

        /// <summary>
        /// Scene/world coordinate basis and unit conversion <c>W</c> from Source world space into Stored world space.
        /// Used for skin/skeleton bone bind conjugation (<c>W^-1 * M_bone * W</c>) and mesh bind right factor
        /// (<c>L^-1 * M_meshBind * W</c>), strictly independent of per-mesh local offset <c>O</c>.
        /// </summary>
        public FbxAffineSpaceTransform SourceWorldToStoredSpace { get; }

        /// <summary>
        /// Per-mesh local alignment transform <c>L</c> from Source mesh-local space into Stored mesh-local space.
        /// </summary>
        public FbxAffineSpaceTransform SourceMeshToStoredSpace { get; }

        public FbxTopologyCompatibilityResult TopologyCompatibility { get; }
        public PatchSpaceProvenance SpaceProvenance { get; }
        public PayloadEncodeOptions EncodeOptions { get; }
        private readonly Action<IReadOnlyList<SkeletonBoneRegistration>, FbxAffineSpaceTransform> m_SkeletonRegistrar;

        public MeshExtractionContext(
            string meshId,
            string nodePath,
            string displayName,
            IFbxScene sourceScene,
            IFbxScene originScene,
            IFbxMesh rawSourceMesh,
            IFbxMesh rawOriginMesh,
            IFbxMeshGeometry normalizedSourceMesh,
            IFbxMeshGeometry normalizedOriginMesh,
            FbxTopologyCompatibilityResult topologyCompatibility,
            PatchSpaceProvenance spaceProvenance,
            PayloadEncodeOptions encodeOptions,
            Action<IReadOnlyList<SkeletonBoneRegistration>, FbxAffineSpaceTransform> skeletonRegistrar = null,
            FbxAffineSpaceTransform? sourceWorldToStoredSpace = null,
            FbxAffineSpaceTransform? sourceMeshToStoredSpace = null)
        {
            MeshId = meshId ?? string.Empty;
            NodePath = nodePath ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            SourceScene = sourceScene;
            OriginScene = originScene;
            RawSourceMesh = rawSourceMesh;
            RawOriginMesh = rawOriginMesh;
            NormalizedSourceMesh = normalizedSourceMesh;
            NormalizedOriginMesh = normalizedOriginMesh;
            TopologyCompatibility = topologyCompatibility;
            SpaceProvenance = spaceProvenance;
            EncodeOptions = encodeOptions;
            m_SkeletonRegistrar = skeletonRegistrar;
            SourceWorldToStoredSpace = sourceWorldToStoredSpace ?? default;
            SourceMeshToStoredSpace = sourceMeshToStoredSpace ?? default;
        }

        public void RegisterSkeletonBones(
            IReadOnlyList<SkeletonBoneRegistration> bones,
            FbxAffineSpaceTransform sourceWorldToStored)
        {
            m_SkeletonRegistrar?.Invoke(bones, sourceWorldToStored);
        }
    }

    /// <summary>
    /// Capability Registry #4: per-feature extractor producing a <see cref="BlendShareFeatureData"/>
    /// in the context's stored mesh coordinate frame from a <see cref="MeshExtractionContext"/>.
    /// </summary>
    public interface IFeatureExtractor
    {
        string FeatureTypeId { get; }
        int ExecutionOrder { get; }
        Type OptionsType { get; }
        IFeatureExtractionOptions CreateDefaultOptions();
        FeatureExtractionResult Extract(MeshExtractionContext context, IFeatureExtractionOptions options);
    }

    /// <summary>
    /// Assembly-level attribute for third-party packages registering custom <see cref="IFeatureExtractor"/> types.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareFeatureExtractorAttribute : Attribute
    {
        public Type ExtractorType { get; }

        public RegisterBlendShareFeatureExtractorAttribute(Type extractorType)
        {
            ExtractorType = extractorType ?? throw new ArgumentNullException(nameof(extractorType));
        }
    }

    /// <summary>
    /// Deterministic, lazily initialized registry of <see cref="IFeatureExtractor"/> keyed by FeatureTypeId.
    /// </summary>
    public static class FeatureExtractorRegistry
    {
        private static readonly object s_Lock = new object();
        private static Dictionary<string, IFeatureExtractor> s_Extractors;

        public static void Register(IFeatureExtractor extractor)
        {
            if (extractor == null)
            {
                throw new ArgumentNullException(nameof(extractor));
            }

            if (string.IsNullOrWhiteSpace(extractor.FeatureTypeId))
            {
                throw new ArgumentException("Extractor FeatureTypeId must be non-empty.", nameof(extractor));
            }

            lock (s_Lock)
            {
                EnsureInitializedLocked();
                if (s_Extractors.ContainsKey(extractor.FeatureTypeId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate IFeatureExtractor registered for FeatureTypeId '{extractor.FeatureTypeId}'.");
                }

                s_Extractors.Add(extractor.FeatureTypeId, extractor);
            }
        }

        public static bool TryGet(string featureTypeId, out IFeatureExtractor extractor)
        {
            if (string.IsNullOrEmpty(featureTypeId))
            {
                extractor = null;
                return false;
            }

            lock (s_Lock)
            {
                EnsureInitializedLocked();
                return s_Extractors.TryGetValue(featureTypeId, out extractor);
            }
        }

        public static IReadOnlyList<IFeatureExtractor> GetAllOrdered()
        {
            lock (s_Lock)
            {
                EnsureInitializedLocked();
                var list = new List<IFeatureExtractor>(s_Extractors.Values);
                list.Sort((a, b) =>
                {
                    int cmp = a.ExecutionOrder.CompareTo(b.ExecutionOrder);
                    return cmp != 0 ? cmp : string.CompareOrdinal(a.FeatureTypeId, b.FeatureTypeId);
                });
                return list;
            }
        }

        private static void EnsureInitializedLocked()
        {
            if (s_Extractors != null)
            {
                return;
            }

            s_Extractors = new Dictionary<string, IFeatureExtractor>(StringComparer.Ordinal);
            AddBuiltInLocked(new SkinWeightFeatureExtractor());
            AddBuiltInLocked(new BlendShapeFeatureExtractor());
            DiscoverExternalLocked();
        }

        private static void AddBuiltInLocked(IFeatureExtractor extractor)
        {
            s_Extractors.Add(extractor.FeatureTypeId, extractor);
        }

        private static void DiscoverExternalLocked()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                object[] attrs;
                try
                {
                    attrs = assemblies[i].GetCustomAttributes(typeof(RegisterBlendShareFeatureExtractorAttribute), false);
                }
                catch
                {
                    continue;
                }

                for (int a = 0; a < attrs.Length; a++)
                {
                    var attr = (RegisterBlendShareFeatureExtractorAttribute)attrs[a];
                    var type = attr.ExtractorType;
                    if (type == null || type.IsAbstract || !typeof(IFeatureExtractor).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    var instance = (IFeatureExtractor)Activator.CreateInstance(type);
                    if (instance != null && !string.IsNullOrWhiteSpace(instance.FeatureTypeId))
                    {
                        if (s_Extractors.ContainsKey(instance.FeatureTypeId))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate IFeatureExtractor registered via assembly attribute for '{instance.FeatureTypeId}'.");
                        }

                        s_Extractors.Add(instance.FeatureTypeId, instance);
                    }
                }
            }
        }
    }
}
