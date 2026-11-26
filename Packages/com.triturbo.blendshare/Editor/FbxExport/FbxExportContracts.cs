using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Export;
using UnityEngine;

namespace Triturbo.BlendShare.FbxExport
{
    public enum FbxFeatureWriteStatus : byte
    {
        Succeeded = 0,
        Skipped = 1,
        Failed = 2
    }

    public sealed class FbxFeatureWriteResult
    {
        public FbxFeatureWriteStatus Status { get; }
        public bool ModifiedMesh { get; }
        public IReadOnlyList<FbxExportDiagnostic> Diagnostics { get; }

        private FbxFeatureWriteResult(
            FbxFeatureWriteStatus status,
            bool modifiedMesh,
            FbxExportDiagnostic[] diagnostics)
        {
            Status = status;
            ModifiedMesh = modifiedMesh;
            Diagnostics = diagnostics ?? Array.Empty<FbxExportDiagnostic>();
        }

        public static FbxFeatureWriteResult Succeeded(bool modifiedMesh = true, params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Succeeded, modifiedMesh, diagnostics);

        public static FbxFeatureWriteResult Skipped(params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Skipped, false, diagnostics);

        public static FbxFeatureWriteResult Failed(params FbxExportDiagnostic[] diagnostics) =>
            new FbxFeatureWriteResult(FbxFeatureWriteStatus.Failed, false, diagnostics);
    }

    public sealed class FbxExportSpace
    {
        public FbxMatrix4x4 StoredToTargetRaw { get; }
        public FbxMatrix4x4 TargetRawToStored { get; }
        public FbxAffineSpaceTransform MeshTransform { get; }
        public FbxMatrix4x4 StoredWorldToTargetWorld { get; }
        public FbxMatrix4x4 TargetWorldToStoredWorld { get; }
        public FbxMatrix4x4 StoredToCanonical { get; }
        public bool IsExactIdentity { get; }
        public bool IsSceneExactIdentity { get; }
        private readonly bool m_IsMeshSimilarity;
        private readonly FbxMatrix4x4 m_MeshOrthogonalNormalToRaw;

        private FbxExportSpace(
            FbxMatrix4x4 meshForward,
            FbxAffineSpaceTransform meshTransform,
            FbxMatrix4x4 sceneForward,
            FbxMatrix4x4 sceneInverse,
            FbxMatrix4x4 canonical,
            bool isMeshSimilarity,
            FbxMatrix4x4 meshOrthogonalNormalToRaw,
            bool meshIdentity,
            bool sceneIdentity)
        {
            StoredToTargetRaw = meshForward;
            TargetRawToStored = meshTransform.InverseOffset;
            MeshTransform = meshTransform;
            StoredWorldToTargetWorld = sceneForward;
            TargetWorldToStoredWorld = sceneInverse;
            StoredToCanonical = canonical;
            m_IsMeshSimilarity = isMeshSimilarity;
            m_MeshOrthogonalNormalToRaw = meshOrthogonalNormalToRaw;
            IsExactIdentity = meshIdentity;
            IsSceneExactIdentity = sceneIdentity;
        }

        public static bool TryCreate(PatchSpaceProvenance stored, FbxSceneMetadata target, out FbxExportSpace space)
        {
            space = null;
            if (stored == null || !stored.IsValid ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.Axes, stored.MetersPerUnit, target.Axes, target.UnitMeters, out var forward, out bool identity) ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.Axes, stored.MetersPerUnit, CanonicalMeshSpace.CanonicalAxes, CanonicalMeshSpace.CanonicalUnitMeters, out var canonical, out _) ||
                !CanonicalMeshSpace.TryBuildSpaceToSpace(stored.Axes, 1d, target.Axes, 1d, out var normal, out _) ||
                !FbxAffineSpaceTransform.TryCreate(forward, out var transform))
            {
                return false;
            }

            space = new FbxExportSpace(
                forward,
                transform,
                forward,
                transform.InverseOffset,
                canonical,
                isMeshSimilarity: true,
                normal,
                identity,
                identity);
            return true;
        }

        /// <summary>
        /// Returns a mesh-calibrated <see cref="FbxExportSpace"/> with updated mesh-local linear transform
        /// while keeping <see cref="StoredWorldToTargetWorld"/> strictly untouched for scene/skeleton transforms.
        /// </summary>
        public bool TryWithRefinedMeshLinearTransform(FbxMatrix4x4 refinedStoredToTargetRaw, out FbxExportSpace refinedSpace)
        {
            refinedSpace = this;
            if (!FbxAffineSpaceTransform.TryCreate(refinedStoredToTargetRaw, out var meshTransform))
            {
                return false;
            }

            bool meshIdentity = FbxAffineSpaceTransform.IsExactIdentity(refinedStoredToTargetRaw);
            bool isSimilarity = FbxLinearAlgebra3d.TryExtractOrthogonalSimilarity(
                refinedStoredToTargetRaw,
                out _,
                out var orthoQ);

            refinedSpace = new FbxExportSpace(
                refinedStoredToTargetRaw,
                meshTransform,
                StoredWorldToTargetWorld,
                TargetWorldToStoredWorld,
                StoredToCanonical,
                isSimilarity,
                isSimilarity ? orthoQ : FbxMatrix4x4.Identity,
                meshIdentity,
                IsSceneExactIdentity);
            return true;
        }

        public Vector3d PositionDeltaToRaw(Vector3d value) =>
            IsExactIdentity ? value : StoredToTargetRaw.TransformVector(value);

        public Vector3d NormalDeltaToRaw(Vector3d value) =>
            IsExactIdentity ? value : TransformNormalDeltaToRaw(Vector3d.zero, value);

        /// <summary>
        /// Transforms a stored blendshape normal delta into Target FBX raw space.
        /// Uses exact orthogonal rotation/reflection when <see cref="StoredToTargetRaw"/> is a similarity,
        /// and inverse-transpose unit-normal transport relative to <paramref name="targetRawBaseNormal"/> when non-isotropic.
        /// </summary>
        public Vector3d TransformNormalDeltaToRaw(Vector3d targetRawBaseNormal, Vector3d storedNormalDelta)
        {
            if (IsExactIdentity)
            {
                return storedNormalDelta;
            }

            if (m_IsMeshSimilarity)
            {
                return m_MeshOrthogonalNormalToRaw.TransformVector(storedNormalDelta);
            }

            Vector3d storedBaseNormal = targetRawBaseNormal.sqrMagnitude > Vector3d.Epsilon
                ? MeshTransform.Offset.TransformNormalWithInverse(targetRawBaseNormal)
                : Vector3d.zero;
            return MeshTransform.TransformNormalDelta(storedBaseNormal, storedNormalDelta);
        }

        /// <summary>
        /// Conjugates a scene/world matrix using the unrefined scene/world basis <see cref="StoredWorldToTargetWorld"/>.
        /// </summary>
        public FbxMatrix4x4 BindMatrixToRaw(FbxMatrix4x4 value) =>
            IsSceneExactIdentity ? value : TargetWorldToStoredWorld * value * StoredWorldToTargetWorld;
    }

    public sealed class FbxFeatureWriteInput
    {
        public FbxPatchExportItem PatchItem { get; }
        public MeshDocument MeshHeader { get; }
        public SkeletonDocument Skeleton { get; }
        public BlendShareFeatureData Feature { get; }
        public ILazyPayloadSource PayloadSource { get; }
        public string TargetNodePath { get; }
        public FbxExportSpace PatchSpace { get; }

        public FbxFeatureWriteInput(
            FbxPatchExportItem patchItem,
            MeshDocument meshHeader,
            SkeletonDocument skeleton,
            BlendShareFeatureData feature,
            ILazyPayloadSource payloadSource,
            string targetNodePath,
            FbxExportSpace patchSpace)
        {
            PatchSpace = patchSpace ?? throw new ArgumentNullException(nameof(patchSpace));
            PatchItem = patchItem ?? throw new ArgumentNullException(nameof(patchItem));
            MeshHeader = meshHeader ?? throw new ArgumentNullException(nameof(meshHeader));
            Skeleton = skeleton;
            Feature = feature ?? throw new ArgumentNullException(nameof(feature));
            PayloadSource = payloadSource;
            TargetNodePath = FbxNameUtility.NormalizePath(targetNodePath ?? string.Empty);
        }
    }

    public sealed class FbxExportMeshContext
    {
        public FbxManager Manager { get; }
        public FbxScene SdkScene { get; }
        public FbxNode RootNode { get; }
        public FbxNode TargetNode { get; }
        public FbxMesh TargetMesh { get; }
        public IFbxScene ReaderScene { get; }
        public IFbxMesh ReaderMesh { get; }
        public string TargetNodePath { get; }
        public FbxAffineSpaceTransform CanonicalToTargetRaw { get; }
        public FbxAffineSpaceTransform TargetRawToCanonical { get; }
        public double NumericVerificationToleranceMeters { get; }
        public IDictionary<string, object> SessionState { get; }
        public List<FbxExportDiagnostic> Diagnostics { get; }

        public FbxExportMeshContext(
            FbxManager manager,
            FbxScene sdkScene,
            FbxNode rootNode,
            FbxNode targetNode,
            FbxMesh targetMesh,
            IFbxScene readerScene,
            IFbxMesh readerMesh,
            string targetNodePath,
            FbxAffineSpaceTransform canonicalToTargetRaw,
            FbxAffineSpaceTransform targetRawToCanonical,
            double numericVerificationToleranceMeters,
            IDictionary<string, object> sessionState,
            List<FbxExportDiagnostic> diagnostics)
        {
            Manager = manager;
            SdkScene = sdkScene;
            RootNode = rootNode;
            TargetNode = targetNode;
            TargetMesh = targetMesh;
            ReaderScene = readerScene;
            ReaderMesh = readerMesh;
            TargetNodePath = targetNodePath ?? string.Empty;
            CanonicalToTargetRaw = canonicalToTargetRaw;
            TargetRawToCanonical = targetRawToCanonical;
            NumericVerificationToleranceMeters = numericVerificationToleranceMeters;
            SessionState = sessionState ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Diagnostics = diagnostics ?? new List<FbxExportDiagnostic>();
        }
    }

    public sealed class FbxExportVerifyMeshContext
    {
        public IFbxScene VerifiedScene { get; }
        public IFbxMesh VerifiedMesh { get; }
        public IFbxScene OriginalReaderScene { get; }
        public IFbxMesh OriginalReaderMesh { get; }
        public string TargetNodePath { get; }
        public FbxAffineSpaceTransform CanonicalToTargetRaw { get; }
        public FbxAffineSpaceTransform TargetRawToCanonical { get; }
        public double NumericVerificationToleranceMeters { get; }
        public bool VerifyNumerics { get; }
        public IDictionary<string, object> SessionState { get; }
        public List<FbxExportDiagnostic> Diagnostics { get; }
        public string VerifiedFbxPath { get; }

        private readonly Func<FbxScene> m_LazyVerifiedSdkScene;

        public FbxExportVerifyMeshContext(
            IFbxScene verifiedScene,
            IFbxMesh verifiedMesh,
            IFbxScene originalReaderScene,
            IFbxMesh originalReaderMesh,
            string targetNodePath,
            FbxAffineSpaceTransform canonicalToTargetRaw,
            FbxAffineSpaceTransform targetRawToCanonical,
            double numericVerificationToleranceMeters,
            IDictionary<string, object> sessionState,
            List<FbxExportDiagnostic> diagnostics,
            bool verifyNumerics = true,
            string verifiedFbxPath = null,
            Func<FbxScene> lazyVerifiedSdkScene = null)
        {
            VerifiedScene = verifiedScene;
            VerifiedMesh = verifiedMesh;
            OriginalReaderScene = originalReaderScene;
            OriginalReaderMesh = originalReaderMesh;
            TargetNodePath = targetNodePath ?? string.Empty;
            CanonicalToTargetRaw = canonicalToTargetRaw;
            TargetRawToCanonical = targetRawToCanonical;
            NumericVerificationToleranceMeters = numericVerificationToleranceMeters;
            VerifyNumerics = verifyNumerics;
            SessionState = sessionState ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Diagnostics = diagnostics ?? new List<FbxExportDiagnostic>();
            VerifiedFbxPath = verifiedFbxPath ?? string.Empty;
            m_LazyVerifiedSdkScene = lazyVerifiedSdkScene;
        }

        /// <summary>
        /// Returns the transaction-shared reopened Autodesk FBX SDK verification scene (imported at most once per transaction).
        /// </summary>
        public FbxScene GetVerifiedSdkScene() => m_LazyVerifiedSdkScene?.Invoke();
    }

    /// <summary>
    /// Capability Registry #6: Feature-specific writer for transactional Autodesk FBX SDK export
    /// and post-export ufbx numeric/channel round-trip verification.
    /// </summary>
    public interface IFeatureFbxWriter
    {
        string FeatureTypeId { get; }
        int ExecutionOrder { get; }

        void RegisterTransactionInputs(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
        FbxFeatureWriteResult Preflight(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
        FbxFeatureWriteResult Write(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
        FbxFeatureWriteResult Verify(FbxExportVerifyMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs);
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RegisterBlendShareFeatureFbxWriterAttribute : Attribute
    {
        public Type WriterType { get; }
        public RegisterBlendShareFeatureFbxWriterAttribute(Type writerType) => WriterType = writerType;
    }

    public sealed class FeatureFbxWriterRegistry
    {
        private static readonly Lazy<FeatureFbxWriterRegistry> s_Default =
            new Lazy<FeatureFbxWriterRegistry>(() => new FeatureFbxWriterRegistry());

        public static FeatureFbxWriterRegistry Default => s_Default.Value;

        private readonly Dictionary<string, IFeatureFbxWriter> m_Entries =
            new Dictionary<string, IFeatureFbxWriter>(StringComparer.Ordinal);

        public FeatureFbxWriterRegistry()
        {
            Register(new SkinWeightFbxWriter());
            Register(new BlendShapeFbxWriter());

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                RegisterBlendShareFeatureFbxWriterAttribute[] attributes;
                try
                {
                    attributes = assembly.GetCustomAttributes<RegisterBlendShareFeatureFbxWriterAttribute>().ToArray();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BlendShare.FbxExport] Failed reading FbxWriter attributes from '{assembly.FullName}': {ex.Message}");
                    continue;
                }

                foreach (var attribute in attributes.OrderBy(a => a.WriterType?.FullName, StringComparer.Ordinal))
                {
                    var type = attribute.WriterType;
                    if (type == null || type.IsAbstract || !typeof(IFeatureFbxWriter).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    try
                    {
                        Register((IFeatureFbxWriter)Activator.CreateInstance(type));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[BlendShare.FbxExport] Failed instantiating FbxWriter '{type.FullName}': {ex.Message}");
                    }
                }
            }
        }

        public void Register(IFeatureFbxWriter writer)
        {
            if (writer == null || string.IsNullOrWhiteSpace(writer.FeatureTypeId))
            {
                throw new ArgumentException("Feature FBX writer must have a non-empty FeatureTypeId.", nameof(writer));
            }

            lock (m_Entries)
            {
                if (m_Entries.ContainsKey(writer.FeatureTypeId))
                {
                    throw new InvalidOperationException($"Duplicate IFeatureFbxWriter registration for '{writer.FeatureTypeId}'.");
                }

                m_Entries.Add(writer.FeatureTypeId, writer);
            }
        }

        public bool Unregister(string featureTypeId)
        {
            if (string.IsNullOrWhiteSpace(featureTypeId))
            {
                return false;
            }

            lock (m_Entries)
            {
                return m_Entries.Remove(featureTypeId);
            }
        }

        public bool TryGet(string featureTypeId, out IFeatureFbxWriter writer)
        {
            if (string.IsNullOrWhiteSpace(featureTypeId))
            {
                writer = null;
                return false;
            }

            lock (m_Entries)
            {
                return m_Entries.TryGetValue(featureTypeId, out writer);
            }
        }

        public IReadOnlyList<IFeatureFbxWriter> GetAllOrdered()
        {
            lock (m_Entries)
            {
                return m_Entries.Values
                    .OrderBy(w => w.ExecutionOrder)
                    .ThenBy(w => w.FeatureTypeId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public static class FbxSdkNodeUtility
    {
        private static readonly System.Reflection.MethodInfo s_EmitterGetCPtr =
            typeof(FbxEmitter).GetMethod(
                "getCPtr",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static readonly System.Reflection.PropertyInfo s_EmitterSwigCPtrProperty =
            typeof(FbxEmitter).GetProperty(
                "swigCPtr",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static readonly System.Reflection.ConstructorInfo s_FbxSkinPtrCtor =
            typeof(FbxSkin).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null,
                new[] { typeof(IntPtr), typeof(bool) },
                null);

        /// <summary>
        /// Wraps an <see cref="FbxDeformer"/> returned by <c>FbxGeometry.GetDeformer(..., eSkin)</c> as a typed
        /// <see cref="FbxSkin"/> (Unity's SWIG wrapper returns a base <see cref="FbxDeformer"/> instance even when
        /// the underlying C++ object is an <c>FbxSkin</c>).
        /// Transfers ownership of the SWIG <c>WeakPointerHandle</c> from <paramref name="deformer"/> to
        /// <paramref name="skin"/> so <c>ReleaseWeakPointerHandle</c> is never double-freed by both finalizers.
        /// </summary>
        public static bool TryWrapSkinDeformer(FbxDeformer deformer, out FbxSkin skin)
        {
            skin = null;
            if (deformer == null || deformer.GetDeformerType() != FbxDeformer.EDeformerType.eSkin)
            {
                return false;
            }

            if (deformer is FbxSkin directSkin)
            {
                skin = directSkin;
                return true;
            }

            if (s_EmitterGetCPtr == null || s_EmitterSwigCPtrProperty == null || s_FbxSkinPtrCtor == null)
            {
                return false;
            }

            var handleRef = (System.Runtime.InteropServices.HandleRef)s_EmitterGetCPtr.Invoke(null, new object[] { deformer });
            if (handleRef.Handle == IntPtr.Zero)
            {
                return false;
            }

            skin = (FbxSkin)s_FbxSkinPtrCtor.Invoke(new object[] { handleRef.Handle, false });
            if (skin == null)
            {
                return false;
            }

            s_EmitterSwigCPtrProperty.SetValue(deformer, new System.Runtime.InteropServices.HandleRef(null, IntPtr.Zero));
            GC.SuppressFinalize(deformer);
            return true;
        }

        /// <summary>
        /// Enumerates all <see cref="FbxCluster"/> subdeformers attached to a single <paramref name="skin"/> deformer.
        /// </summary>
        public static List<FbxCluster> GetClustersOfSkin(FbxSkin skin)
        {
            var clusters = new List<FbxCluster>();
            if (skin == null)
            {
                return clusters;
            }

            int maxCandidates = Math.Max(0, skin.GetSrcObjectCount());
            for (int c = 0; c <= maxCandidates; c++)
            {
                var cluster = skin.GetCluster(c);
                if (cluster == null)
                {
                    break;
                }

                clusters.Add(cluster);
            }

            return clusters;
        }

        /// <summary>
        /// Selects the active primary <see cref="FbxSkin"/> deformer on <paramref name="mesh"/> using the same
        /// three-tier priority rule as <see cref="FbxDeformerUtility.FindPrimarySkinDeformer"/>:
        /// (1) first skin with a linked cluster carrying control-point weights,
        /// (2) first skin with a linked cluster,
        /// (3) first valid skin deformer on the mesh, or <c>null</c> when none exist.
        /// </summary>
        public static FbxSkin FindPrimarySkinDeformer(FbxMesh mesh, out int primarySkinIndex)
        {
            primarySkinIndex = -1;
            if (mesh == null)
            {
                return null;
            }

            int skinCount = mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin);
            FbxSkin firstSkin = null;
            int firstSkinIndex = -1;
            FbxSkin fallbackWithLinkedCluster = null;
            int fallbackIndex = -1;

            for (int s = 0; s < skinCount; s++)
            {
                var deformer = mesh.GetDeformer(s, FbxDeformer.EDeformerType.eSkin);
                if (!TryWrapSkinDeformer(deformer, out var skin))
                {
                    continue;
                }

                if (firstSkin == null)
                {
                    firstSkin = skin;
                    firstSkinIndex = s;
                }

                var skinClusters = GetClustersOfSkin(skin);
                for (int c = 0; c < skinClusters.Count; c++)
                {
                    var cl = skinClusters[c];
                    if (cl == null || cl.GetLink() == null)
                    {
                        continue;
                    }

                    if (fallbackWithLinkedCluster == null)
                    {
                        fallbackWithLinkedCluster = skin;
                        fallbackIndex = s;
                    }

                    if (cl.GetControlPointIndicesCount() > 0)
                    {
                        primarySkinIndex = s;
                        return skin;
                    }
                }
            }

            if (fallbackWithLinkedCluster != null)
            {
                primarySkinIndex = fallbackIndex;
                return fallbackWithLinkedCluster;
            }

            primarySkinIndex = firstSkinIndex;
            return firstSkin;
        }

        /// <summary>
        /// Enumerates all <see cref="FbxCluster"/> subdeformers attached to skin deformers on <paramref name="mesh"/>.
        /// </summary>
        public static List<FbxCluster> GetSkinClusters(FbxMesh mesh)
        {
            var clusters = new List<FbxCluster>();
            if (mesh == null)
            {
                return clusters;
            }

            int skinCount = mesh.GetDeformerCount(FbxDeformer.EDeformerType.eSkin);
            for (int s = 0; s < skinCount; s++)
            {
                var deformer = mesh.GetDeformer(s, FbxDeformer.EDeformerType.eSkin);
                if (!TryWrapSkinDeformer(deformer, out var skin))
                {
                    continue;
                }

                clusters.AddRange(GetClustersOfSkin(skin));
            }

            return clusters;
        }

        public static FbxNode FindNodeByPath(FbxNode rootNode, string path)
        {
            if (rootNode == null)
            {
                return null;
            }

            string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
            if (string.IsNullOrEmpty(normalized))
            {
                return rootNode;
            }

            FbxNode current = rootNode;
            string[] parts = normalized.Split('/');
            for (int p = 0; p < parts.Length; p++)
            {
                string part = parts[p];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                FbxNode found = null;
                int childCount = current.GetChildCount();
                for (int i = 0; i < childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (child != null && string.Equals(child.GetName(), part, StringComparison.Ordinal))
                    {
                        found = child;
                        break;
                    }
                }

                if (found == null)
                {
                    return null;
                }

                current = found;
            }

            return current;
        }

        public static string GetNormalizedNodePath(FbxNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            FbxNode current = node;
            while (current != null)
            {
                FbxNode parent = current.GetParent();
                if (parent == null)
                {
                    break;
                }

                parts.Add(current.GetName());
                current = parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }

        public static string GetLeafName(string normalizedPath)
        {
            string norm = FbxNameUtility.NormalizePath(normalizedPath ?? string.Empty);
            int slash = norm.LastIndexOf('/');
            return slash >= 0 && slash + 1 < norm.Length ? norm.Substring(slash + 1) : norm;
        }

        public static string GetParentPath(string normalizedPath)
        {
            string norm = FbxNameUtility.NormalizePath(normalizedPath ?? string.Empty);
            int slash = norm.LastIndexOf('/');
            return slash > 0 ? norm.Substring(0, slash) : string.Empty;
        }

        public static FbxAMatrix ToFbxAMatrix(FbxMatrix4x4 rowMajor)
        {
            var result = new FbxAMatrix();
            for (int r = 0; r < 4; r++)
            {
                result[r] = new FbxDouble4(rowMajor[r, 0], rowMajor[r, 1], rowMajor[r, 2], rowMajor[r, 3]);
            }

            return result;
        }

        public static FbxMatrix4x4 FromFbxAMatrix(FbxAMatrix matrix)
        {
            if (matrix == null)
            {
                return FbxMatrix4x4.Identity;
            }

            return new FbxMatrix4x4(
                matrix.Get(0, 0), matrix.Get(0, 1), matrix.Get(0, 2), matrix.Get(0, 3),
                matrix.Get(1, 0), matrix.Get(1, 1), matrix.Get(1, 2), matrix.Get(1, 3),
                matrix.Get(2, 0), matrix.Get(2, 1), matrix.Get(2, 2), matrix.Get(2, 3),
                matrix.Get(3, 0), matrix.Get(3, 1), matrix.Get(3, 2), matrix.Get(3, 3));
        }

        /// <summary>
        /// Conjugates a canonical row-vector transform matrix into Target FBX raw space:
        /// <c>M_raw = C^-1 * M_canon * C</c>, where <c>C = CanonicalToTargetRaw.Offset</c> maps canonical -> raw.
        /// </summary>
        public static FbxMatrix4x4 ConvertCanonicalMatrixToTargetRaw(
            FbxMatrix4x4 canonicalMatrix,
            FbxAffineSpaceTransform canonicalToTargetRaw)
        {
            if (canonicalToTargetRaw.IsIdentity)
            {
                return canonicalMatrix;
            }

            return canonicalToTargetRaw.InverseOffset * canonicalMatrix * canonicalToTargetRaw.Offset;
        }

        /// <summary>
        /// Decomposes a raw local affine matrix via Autodesk FBX SDK's <see cref="FbxAMatrix"/> into exact
        /// <c>(LclTranslation, LclRotation XYZ, LclScaling)</c> channels plus the corresponding unscaled local
        /// rotation-translation matrix and signed scale vector so both node writing and hierarchy evaluation
        /// use the exact same signed scale and rotation decomposition.
        /// </summary>
        public static bool TryDecomposeRawLocalTrs(
            FbxMatrix4x4 rawLocalMatrix,
            out FbxVector4 translation,
            out FbxVector4 rotationDegXyz,
            out FbxVector4 signedScale,
            out FbxMatrix4x4 unscaledLocalMatrix,
            out Vector3d signedScaleVector,
            out string error,
            string nodeName = null)
        {
            translation = default;
            rotationDegXyz = default;
            signedScale = default;
            unscaledLocalMatrix = FbxMatrix4x4.Identity;
            signedScaleVector = Vector3d.one;
            error = null;

            string label = string.IsNullOrEmpty(nodeName) ? "<unknown>" : nodeName;
            if (!FbxAffineSpaceTransform.TryCreate(rawLocalMatrix, out _))
            {
                error = $"Local matrix for FBX node '{label}' is singular, projective, or non-finite.";
                return false;
            }

            // Check orthogonality of linear rows (no shear).
            var r0 = new Vector3d(rawLocalMatrix[0, 0], rawLocalMatrix[0, 1], rawLocalMatrix[0, 2]);
            var r1 = new Vector3d(rawLocalMatrix[1, 0], rawLocalMatrix[1, 1], rawLocalMatrix[1, 2]);
            var r2 = new Vector3d(rawLocalMatrix[2, 0], rawLocalMatrix[2, 1], rawLocalMatrix[2, 2]);
            double s0 = r0.magnitude;
            double s1 = r1.magnitude;
            double s2 = r2.magnitude;
            if (s0 <= 1e-10d || s1 <= 1e-10d || s2 <= 1e-10d)
            {
                error = $"Local matrix for FBX node '{label}' has near-zero scale.";
                return false;
            }

            var u0 = r0 / s0;
            var u1 = r1 / s1;
            var u2 = r2 / s2;
            const double k_OrthoTol = 1e-4d;
            if (Math.Abs(Vector3d.Dot(u0, u1)) > k_OrthoTol ||
                Math.Abs(Vector3d.Dot(u0, u2)) > k_OrthoTol ||
                Math.Abs(Vector3d.Dot(u1, u2)) > k_OrthoTol)
            {
                error = $"Local matrix for FBX node '{label}' contains shear and cannot be decomposed into FBX TRS.";
                return false;
            }

            using var sdkMatrix = ToFbxAMatrix(rawLocalMatrix);
            translation = sdkMatrix.GetT();
            rotationDegXyz = sdkMatrix.GetR();
            signedScale = sdkMatrix.GetS();
            if (!IsFinite(translation) || !IsFinite(rotationDegXyz) || !IsFinite(signedScale) ||
                Math.Abs(signedScale.X) <= 1e-10d || Math.Abs(signedScale.Y) <= 1e-10d || Math.Abs(signedScale.Z) <= 1e-10d)
            {
                error = $"Decomposed TRS for FBX node '{label}' is non-finite or singular.";
                return false;
            }

            using var unscaledSdk = new FbxAMatrix(translation, rotationDegXyz, new FbxVector4(1d, 1d, 1d, 0d));
            unscaledLocalMatrix = FromFbxAMatrix(unscaledSdk);
            signedScaleVector = new Vector3d(signedScale.X, signedScale.Y, signedScale.Z);
            return true;
        }

        /// <summary>
        /// Applies a raw local affine matrix to an FBX SDK node via TRS decomposition (clearing pivots/pre/post rotations).
        /// Returns false if the matrix contains shear/perspective or non-finite entries.
        /// </summary>
        public static bool TryApplyRawLocalMatrixToNode(FbxNode node, FbxMatrix4x4 rawLocalMatrix, out string error)
        {
            error = null;
            if (node == null)
            {
                error = "Target FBX node is null.";
                return false;
            }

            if (!TryDecomposeRawLocalTrs(
                    rawLocalMatrix,
                    out var t,
                    out var r,
                    out var s,
                    out _,
                    out _,
                    out error,
                    node.GetName()))
            {
                return false;
            }

            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, FbxEuler.EOrder.eOrderXYZ);
            node.SetTransformationInheritType(Autodesk.Fbx.FbxTransform.EInheritType.eInheritRSrs);
            node.SetRotationActive(false);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(0d, 0d, 0d, 0d));
            node.LclTranslation.Set(new FbxDouble3(t.X, t.Y, t.Z));
            node.LclRotation.Set(new FbxDouble3(r.X, r.Y, r.Z));
            node.LclScaling.Set(new FbxDouble3(s.X, s.Y, s.Z));
            return true;
        }

        /// <summary>
        /// Writes exact lossless authored FBX node transform properties (Euler, rotation order, inherit mode,
        /// pre/post rotations, rotation/scaling pivots and offsets) verbatim onto an FBX SDK node.
        /// </summary>
        public static void ApplyRawTransformPropertiesToNode(FbxNode node, SkeletonRawTransform raw)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            node.LclTranslation.Set(new FbxDouble3(raw.LclTranslation.x, raw.LclTranslation.y, raw.LclTranslation.z));
            node.LclRotation.Set(new FbxDouble3(raw.LclRotation.x, raw.LclRotation.y, raw.LclRotation.z));
            node.LclScaling.Set(new FbxDouble3(raw.LclScaling.x, raw.LclScaling.y, raw.LclScaling.z));
            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, ToSdkRotationOrder(raw.RotationOrder));
            node.SetTransformationInheritType(ToSdkInheritType(raw.InheritMode));
            node.SetRotationActive(raw.RotationActive);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.PreRotation));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.PostRotation));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.RotationPivot));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.ScalingPivot));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.RotationOffset));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.ScalingOffset));
        }

        /// <summary>
        /// Applies a solved <see cref="FbxNodeRawTransform"/> stack onto an FBX SDK node, preserving its
        /// <c>PreRotation</c>, <c>PostRotation</c>, <c>RotationOrder</c>, pivots/offsets, and <c>OriginalInheritMode</c>.
        /// </summary>
        public static void ApplySolvedRawTransformToNode(FbxNode node, in FbxNodeRawTransform raw)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            node.LclTranslation.Set(new FbxDouble3(raw.LclTranslation.x, raw.LclTranslation.y, raw.LclTranslation.z));
            node.LclRotation.Set(new FbxDouble3(raw.LclRotation.x, raw.LclRotation.y, raw.LclRotation.z));
            node.LclScaling.Set(new FbxDouble3(raw.LclScaling.x, raw.LclScaling.y, raw.LclScaling.z));
            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, ToSdkRotationOrder(raw.RotationOrder));
            node.SetTransformationInheritType(ToSdkInheritType(raw.OriginalInheritMode));
            node.SetRotationActive(raw.RotationActive);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.PreRotation));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.PostRotation));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.RotationPivot));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.ScalingPivot));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.RotationOffset));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(raw.ScalingOffset));
        }

        private static FbxVector4 ToFbxVector4(Vector3d value) => new FbxVector4(value.x, value.y, value.z);

        private static FbxEuler.EOrder ToSdkRotationOrder(FbxRotationOrder order)
        {
            return order switch
            {
                FbxRotationOrder.XZY => FbxEuler.EOrder.eOrderXZY,
                FbxRotationOrder.YZX => FbxEuler.EOrder.eOrderYZX,
                FbxRotationOrder.YXZ => FbxEuler.EOrder.eOrderYXZ,
                FbxRotationOrder.ZXY => FbxEuler.EOrder.eOrderZXY,
                FbxRotationOrder.ZYX => FbxEuler.EOrder.eOrderZYX,
                FbxRotationOrder.SphericXYZ => FbxEuler.EOrder.eOrderSphericXYZ,
                _ => FbxEuler.EOrder.eOrderXYZ
            };
        }

        private static Autodesk.Fbx.FbxTransform.EInheritType ToSdkInheritType(FbxTransformInheritMode mode)
        {
            return mode switch
            {
                FbxTransformInheritMode.RrSs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrSs,
                FbxTransformInheritMode.Rrs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrs,
                _ => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRSrs
            };
        }

        private static bool IsFinite(FbxVector4 v)
        {
            return !double.IsNaN(v.X) && !double.IsInfinity(v.X) &&
                   !double.IsNaN(v.Y) && !double.IsInfinity(v.Y) &&
                   !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);
        }
    }
}
