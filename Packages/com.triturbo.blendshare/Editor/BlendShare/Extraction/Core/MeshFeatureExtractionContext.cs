using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.Fbx;
using Triturbo.Fbx.Unity;
using UnityEngine;
using ReaderFbxDocument = Triturbo.Fbx.FbxDocument;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using Triturbo.BlendShapeShare.Util;
#endif

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Request for extracting features from one FBX node path.
    /// </summary>
    public sealed class MeshFeatureExtractionMeshRequest
    {
        public string Path;

        /// <summary>
        /// Creates an empty mesh extraction request for Unity serialization.
        /// </summary>
        public MeshFeatureExtractionMeshRequest() { }

        /// <summary>
        /// Creates a mesh extraction request for a renderer/node path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        public MeshFeatureExtractionMeshRequest(string path)
        {
            Path = MeshNodePath.Normalize(path);
        }
    }

    /// <summary>
    /// Owns shared extraction sources, normalized FBX geometry caches, and optional SDK lifetime.
    /// </summary>
    public sealed class MeshFeatureExtractionSession : System.IDisposable
    {
        private readonly Dictionary<string, FbxControlPointWelding> fbxWeldingByMesh = new();
        private readonly HashSet<string> requestedFbxPaths;
        private readonly bool rawFbxSdkAccessAllowed;
        private readonly UnityMeshExtractionSource sourceUnityMeshes;
        private readonly UnityMeshExtractionSource originUnityMeshes;

#if ENABLE_FBX_SDK
        private FbxSdkExtractionSource sourceSdkSource;
        private FbxSdkExtractionSource originSdkSource;
#endif
        private bool disposed;

        public GameObject SourceFbxGo { get; }
        public GameObject OriginFbxGo { get; }
        public MeshFeatureExtractionOptionsSet Options { get; }
        public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }

        public ReaderFbxDocument SourceDocument { get; }
        public ReaderFbxDocument OriginDocument { get; }
        public bool RawFbxSdkAccessAllowed => rawFbxSdkAccessAllowed;
        public bool SourceFbxSdkLoaded
        {
            get
            {
#if ENABLE_FBX_SDK
                return sourceSdkSource != null;
#else
                return false;
#endif
            }
        }
        public bool OriginFbxSdkLoaded
        {
            get
            {
#if ENABLE_FBX_SDK
                return originSdkSource != null;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Creates an extraction session for a source/origin FBX pair.
        /// </summary>
        /// <param name="sourceFbxGo">Source FBX asset that contains features to extract.</param>
        /// <param name="originFbxGo">Origin FBX asset used as the compatible base mesh source.</param>
        /// <param name="options">Feature extraction options.</param>
        /// <param name="meshes">Mesh path requests to process.</param>
        public MeshFeatureExtractionSession(
            GameObject sourceFbxGo,
            GameObject originFbxGo,
            MeshFeatureExtractionOptionsSet options,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshes = null,
            bool rawFbxSdkAccessAllowed = false)
        {
            SourceFbxGo = sourceFbxGo;
            OriginFbxGo = originFbxGo;
            Options = options ?? new MeshFeatureExtractionOptionsSet();
            Meshes = meshes?.Where(mesh => mesh != null).ToArray() ??
                     System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            this.rawFbxSdkAccessAllowed = rawFbxSdkAccessAllowed;
            sourceUnityMeshes = new UnityMeshExtractionSource(sourceFbxGo);
            originUnityMeshes = new UnityMeshExtractionSource(originFbxGo);
            var nodePaths = Meshes
                .Select(mesh => MeshNodePath.ToFbxPath(mesh.Path))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            requestedFbxPaths = new HashSet<string>(nodePaths, StringComparer.Ordinal);
            OriginDocument = ReadDocument(originFbxGo, nodePaths, "origin");
            SourceDocument = NormalizeSourceDocument(ReadDocument(sourceFbxGo, nodePaths, "source"));
        }

        /// <summary>
        /// Releases FBX SDK resources owned by this extraction session.
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
#if ENABLE_FBX_SDK
            sourceSdkSource?.Dispose();
            sourceSdkSource = null;
            originSdkSource?.Dispose();
            originSdkSource = null;
#endif
        }

        internal FbxSceneNode GetSourceNode(string path)
        {
            return GetNode(SourceDocument, path);
        }

        internal FbxSceneNode GetOriginNode(string path)
        {
            return GetNode(OriginDocument, path);
        }

        internal FbxMeshGeometry GetSourceFbxMesh(string path)
        {
            return GetFbxMesh(SourceDocument, path, true);
        }

        internal FbxMeshGeometry GetOriginFbxMesh(string path)
        {
            return GetFbxMesh(OriginDocument, path, false);
        }

        internal Mesh GetSourceUnityMesh(string path)
        {
            return sourceUnityMeshes.GetMesh(path);
        }

        internal Mesh GetOriginUnityMesh(string path)
        {
            return originUnityMeshes.GetMesh(path);
        }

        private FbxSceneNode GetNode(ReaderFbxDocument document, string path)
        {
            var result = document?.TryFindNode(MeshNodePath.ToFbxPath(path));
            return result != null && result.Success ? result.Value : null;
        }

        private FbxMeshGeometry GetFbxMesh(ReaderFbxDocument document, string path, bool source)
        {
            string fbxPath = MeshNodePath.ToFbxPath(path);
            var result = document?.TryFindMesh(fbxPath);
            var mesh = result != null && result.Success ? result.Value : null;
#if ENABLE_FBX_SDK
            if (mesh == null && IsRequestedFbxPath(fbxPath))
            {
                mesh = GetSdkFbxMesh(path, source);
                if (mesh != null && source)
                {
                    mesh = FbxMeshGeometryNormalizer.Normalize(mesh, GetFbxControlPointWelding(path));
                }
            }
#endif
            return mesh;
        }

        internal FbxControlPointWelding GetFbxControlPointWelding(string path)
        {
            string key = BuildMeshKey(path);
            if (fbxWeldingByMesh.TryGetValue(key, out var welding))
            {
                return welding;
            }

            welding = BuildFbxControlPointWelding(path);
            fbxWeldingByMesh[key] = welding;
            return welding;
        }

        private FbxControlPointWelding BuildFbxControlPointWelding(string path)
        {
            string fbxPath = MeshNodePath.ToFbxPath(path);
            var originMeshResult = OriginDocument?.TryFindMesh(fbxPath);
            var readerMesh = originMeshResult != null && originMeshResult.Success ? originMeshResult.Value : null;
            if (readerMesh != null && readerMesh.ControlPoints.Count > 0)
            {
                return FbxControlPointWelding.FromReaderMesh(readerMesh);
            }

#if ENABLE_FBX_SDK
            if (!IsRequestedFbxPath(fbxPath))
            {
                return FbxControlPointWelding.Empty;
            }

            if (!GetOrCreateOriginSdkSource().TryGetRootNode(out var originRootNode, out _))
            {
                return FbxControlPointWelding.Empty;
            }

            var originNode = originRootNode.FindMeshChildByPath(path);
            var originMesh = originNode?.GetMesh();
            return originMesh != null
                ? FbxControlPointWelding.FromMesh(originMesh)
                : FbxControlPointWelding.Empty;
#else
            return FbxControlPointWelding.Empty;
#endif
        }

        internal static string BuildMeshKey(string path)
        {
            return MeshNodePath.Normalize(path);
        }

        private bool IsRequestedFbxPath(string path)
        {
            return requestedFbxPaths.Count == 0 || requestedFbxPaths.Contains(MeshNodePath.ToFbxPath(path));
        }

        private static ReaderFbxDocument ReadDocument(GameObject fbxGo, IEnumerable<string> nodePaths, string label)
        {
            var result = FbxUnityAssetReader.Read(fbxGo, nodePaths);
            if (!result.Success)
            {
                Debug.LogWarning($"[BlendShare] Failed to read {label} FBX document: {result.Message}");
                return null;
            }

            return result.Value;
        }

        private ReaderFbxDocument NormalizeSourceDocument(ReaderFbxDocument sourceDocument)
        {
            if (sourceDocument == null)
            {
                return null;
            }

            return FbxExtractionDocumentNormalizer.NormalizeMeshes(
                sourceDocument,
                mesh => FbxMeshGeometryNormalizer.Normalize(mesh, GetFbxControlPointWelding(mesh.OwnerNode?.Path)));
        }

#if ENABLE_FBX_SDK
        internal bool TryGetSourceRawSdkMesh(
            string path,
            out FbxNode node,
            out FbxMesh mesh,
            out string error)
        {
            return TryGetRawSdkMesh(path, ref sourceSdkSource, SourceFbxGo, out node, out mesh, out error);
        }

        internal bool TryGetOriginRawSdkMesh(
            string path,
            out FbxNode node,
            out FbxMesh mesh,
            out string error)
        {
            return TryGetRawSdkMesh(path, ref originSdkSource, OriginFbxGo, out node, out mesh, out error);
        }

        private FbxMeshGeometry GetSdkFbxMesh(
            string path,
            bool source)
        {
            var sdkSource = source ? GetOrCreateSourceSdkSource() : GetOrCreateOriginSdkSource();
            if (!sdkSource.TryGetRootNode(out var rootNode, out _))
            {
                return null;
            }

            var node = rootNode.FindMeshChildByPath(path);
            return FbxSdkMeshGeometryAdapter.Create(rootNode, node);
        }

        private bool TryGetRawSdkMesh(
            string path,
            ref FbxSdkExtractionSource sdkSource,
            GameObject fbxGo,
            out FbxNode node,
            out FbxMesh mesh,
            out string error)
        {
            node = null;
            mesh = null;
            error = null;

            if (!rawFbxSdkAccessAllowed && sdkSource == null)
            {
                error = "Raw FBX SDK access was not registered by any enabled feature.";
                return false;
            }

            sdkSource ??= new FbxSdkExtractionSource(fbxGo);
            if (!sdkSource.TryGetRootNode(out var rootNode, out error))
            {
                return false;
            }

            node = rootNode.FindMeshChildByPath(path);
            mesh = node?.GetMesh();
            if (mesh != null)
            {
                return true;
            }

            error = $"Cannot find FBX SDK mesh at path '{MeshNodePath.Normalize(path)}'.";
            return false;
        }

        private FbxSdkExtractionSource GetOrCreateSourceSdkSource()
        {
            return sourceSdkSource ??= new FbxSdkExtractionSource(SourceFbxGo);
        }

        private FbxSdkExtractionSource GetOrCreateOriginSdkSource()
        {
            return originSdkSource ??= new FbxSdkExtractionSource(OriginFbxGo);
        }
#endif

    }

    /// <summary>
    /// Per-mesh extraction view over shared session sources and options.
    /// </summary>
    public sealed class MeshFeatureExtractionContext
    {
        public MeshFeatureExtractionSession Session { get; }
        public MeshFeatureExtractionOptionsSet Options => Session.Options;
        public string Path { get; }

        /// <summary>
        /// Creates a context for one mesh path within an extraction session.
        /// </summary>
        /// <param name="session">Parent extraction session.</param>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        public MeshFeatureExtractionContext(
            MeshFeatureExtractionSession session,
            string path)
        {
            Session = session;
            Path = MeshNodePath.Normalize(path);
        }

        /// <summary>
        /// Gets the source Unity mesh at the current path.
        /// </summary>
        /// <returns>The source Unity mesh, or <c>null</c> when the path cannot be resolved.</returns>
        public Mesh GetSourceUnityMesh()
        {
            return Session.GetSourceUnityMesh(Path);
        }

        /// <summary>
        /// Gets the origin Unity mesh at the current path.
        /// </summary>
        /// <returns>The origin Unity mesh, or <c>null</c> when the path cannot be resolved.</returns>
        public Mesh GetOriginUnityMesh()
        {
            return Session.GetOriginUnityMesh(Path);
        }

        /// <summary>
        /// Gets the source FBX mesh geometry at the current path.
        /// </summary>
        /// <returns>The source FBX mesh geometry, or <c>null</c> when the path cannot be resolved.</returns>
        public FbxMeshGeometry GetSourceFbxMesh()
        {
            return Session.GetSourceFbxMesh(Path);
        }

        /// <summary>
        /// Gets the origin FBX mesh geometry at the current path.
        /// </summary>
        /// <returns>The origin FBX mesh geometry, or <c>null</c> when the path cannot be resolved.</returns>
        public FbxMeshGeometry GetOriginFbxMesh()
        {
            return Session.GetOriginFbxMesh(Path);
        }

        /// <summary>
        /// Gets the source FBX scene node at the current path.
        /// </summary>
        public FbxSceneNode GetSourceNode()
        {
            return Session.GetSourceNode(Path);
        }

        /// <summary>
        /// Gets the origin FBX scene node at the current path.
        /// </summary>
        public FbxSceneNode GetOriginNode()
        {
            return Session.GetOriginNode(Path);
        }

        /// <summary>
        /// Gets the canonical FBX node path resolved for the current request.
        /// </summary>
        /// <returns>The resolved FBX owner-node path, or the request path when no FBX mesh can be resolved.</returns>
        public string GetResolvedFbxNodePath()
        {
            var node = GetOriginNode() ?? GetSourceNode();
            return MeshNodePath.Normalize(node?.Path ?? Path);
        }

        /// <summary>
        /// Gets shared FBX control-point welding for this mesh path.
        /// Feature extractors use this to normalize per-control-point values without owning welding details.
        /// </summary>
        public FbxControlPointWelding GetFbxControlPointWelding()
        {
            return Session.GetFbxControlPointWelding(Path);
        }

#if ENABLE_FBX_SDK
        public bool TryGetSourceRawSdkMesh(
            out FbxNode node,
            out FbxMesh mesh,
            out string error)
        {
            return Session.TryGetSourceRawSdkMesh(Path, out node, out mesh, out error);
        }

        public bool TryGetOriginRawSdkMesh(
            out FbxNode node,
            out FbxMesh mesh,
            out string error)
        {
            return Session.TryGetOriginRawSdkMesh(Path, out node, out mesh, out error);
        }
#endif
    }
}
