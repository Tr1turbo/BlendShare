using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.FBX;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class MeshFeatureExtractionMeshRequest
    {
        public string MeshPath;
        public string MeshName;

        public MeshFeatureExtractionMeshRequest() { }

        public MeshFeatureExtractionMeshRequest(string meshPath, string meshName)
        {
            MeshPath = meshPath;
            MeshName = meshName;
        }
    }

    public sealed class MeshFeatureExtractionSession : System.IDisposable
    {
        private readonly Dictionary<string, MappingUnityBlendShapeCache[]> unityBlendShapeCaches = new();
        private bool disposed;

        public GameObject SourceFbxAsset { get; }
        public GameObject OriginFbxAsset { get; }
        public MeshFeatureExtractionOptionsSet Options { get; }
        public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }

        public UnityMeshExtractionSource UnityMeshes => SourceUnityMeshes;
        public UnityMeshExtractionSource SourceUnityMeshes { get; }
        public UnityMeshExtractionSource OriginUnityMeshes { get; }
        public FbxMeshExtractionSource SourceFbxMeshes { get; }
        public FbxMeshExtractionSource OriginFbxMeshes { get; }
#if ENABLE_FBX_SDK
        public FbxSdkExtractionSource SourceFbxScene { get; }
        public FbxSdkExtractionSource OriginFbxScene { get; }
#endif

        public MeshFeatureExtractionSession(
            GameObject sourceFbxAsset,
            GameObject originFbxAsset,
            MeshFeatureExtractionOptionsSet options,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshes = null)
        {
            SourceFbxAsset = sourceFbxAsset;
            OriginFbxAsset = originFbxAsset;
            Options = options ?? new MeshFeatureExtractionOptionsSet();
            Meshes = meshes?.Where(mesh => mesh != null).ToArray() ??
                     System.Array.Empty<MeshFeatureExtractionMeshRequest>();
            SourceUnityMeshes = new UnityMeshExtractionSource(sourceFbxAsset);
            OriginUnityMeshes = new UnityMeshExtractionSource(originFbxAsset);
            SourceFbxMeshes = new FbxMeshExtractionSource(sourceFbxAsset);
            OriginFbxMeshes = new FbxMeshExtractionSource(originFbxAsset);
#if ENABLE_FBX_SDK
            SourceFbxScene = new FbxSdkExtractionSource(sourceFbxAsset);
            OriginFbxScene = new FbxSdkExtractionSource(originFbxAsset);
#endif
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
#if ENABLE_FBX_SDK
            SourceFbxScene?.Dispose();
            OriginFbxScene?.Dispose();
#endif
        }

        internal void SetUnityBlendShapeCache(
            string meshPath,
            string meshName,
            IEnumerable<MappingUnityBlendShapeCache> cache)
        {
            unityBlendShapeCaches[BuildMeshKey(meshPath, meshName)] =
                cache?.Where(entry => entry != null && entry.m_UnityBlendShapeData != null).ToArray() ??
                System.Array.Empty<MappingUnityBlendShapeCache>();
        }

        internal MappingUnityBlendShapeCache[] GetUnityBlendShapeCache(string meshPath, string meshName)
        {
            return unityBlendShapeCaches.TryGetValue(BuildMeshKey(meshPath, meshName), out var cache)
                ? cache
                : System.Array.Empty<MappingUnityBlendShapeCache>();
        }

        internal static string BuildMeshKey(string meshPath, string meshName)
        {
            return !string.IsNullOrEmpty(meshPath) ? meshPath : meshName ?? string.Empty;
        }
    }

    public sealed class MeshFeatureExtractionContext
    {
        public MeshFeatureExtractionSession Session { get; }
        public MeshFeatureExtractionOptionsSet Options => Session.Options;
        public string MeshPath { get; }
        public string MeshName { get; }

        public MeshFeatureExtractionContext(
            MeshFeatureExtractionSession session,
            string meshPath,
            string meshName)
        {
            Session = session;
            MeshPath = meshPath;
            MeshName = meshName;
        }

        public Mesh GetSourceUnityMesh()
        {
            return Session.SourceUnityMeshes.GetMesh(MeshPath, MeshName);
        }

        public Mesh GetOriginUnityMesh()
        {
            return Session.OriginUnityMeshes.GetMesh(MeshPath, MeshName);
        }

        public FbxMeshGeometry GetSourceFbxMesh(FbxMeshReadOptions options)
        {
            return Session.SourceFbxMeshes.GetMesh(MeshPath, MeshName, options);
        }

        public FbxMeshGeometry GetOriginFbxMesh(FbxMeshReadOptions options)
        {
            return Session.OriginFbxMeshes.GetMesh(MeshPath, MeshName, options);
        }
    }
}
