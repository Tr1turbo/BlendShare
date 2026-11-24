using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Hashing;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    public enum FbxInspectionSpace
    {
        MeshLocal,
        NodeLocal,
        BindPoseNormalized
    }

    public sealed class FbxInspectionSession : IDisposable
    {
        private bool disposed;
        private readonly Dictionary<string, FbxMeshCompatibilityResult> topologyCompatibilityByPath =
            new(StringComparer.Ordinal);

        public FbxInspectionAsset Source { get; }
        public FbxInspectionAsset Origin { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        private FbxInspectionSession(GameObject sourceFbx, GameObject originFbx)
        {
            var diagnostics = new List<string>();
            Source = FbxInspectionAsset.Open(sourceFbx, "Source", diagnostics);
            Origin = FbxInspectionAsset.Open(originFbx, "Original", diagnostics);
            Diagnostics = diagnostics;
        }

        public static FbxInspectionSession Open(GameObject sourceFbx, GameObject originFbx)
        {
            return new FbxInspectionSession(sourceFbx, originFbx);
        }

        public FbxImporterSettingsComparison GetImporterComparison()
        {
            return FbxImporterSettingsComparison.Compare(Origin?.Importer, Source?.Importer);
        }

        public bool TryGetMeshPair(string path, out FbxInspectionMeshPair pair)
        {
            ThrowIfDisposed();
            string normalizedPath = MeshNodePath.Normalize(path);
            var sourceNode = Source?.GetNode(normalizedPath);
            var originNode = Origin?.GetNode(normalizedPath);
            var sourceMesh = Source?.GetMesh(normalizedPath);
            var originMesh = Origin?.GetMesh(normalizedPath);
            var diagnostics = new List<string>();

            if (sourceMesh == null)
            {
                diagnostics.Add($"Source FBX mesh was not found at '{normalizedPath}'.");
            }

            if (originMesh == null)
            {
                diagnostics.Add($"Original FBX mesh was not found at '{normalizedPath}'.");
            }

            pair = new FbxInspectionMeshPair(
                normalizedPath,
                sourceNode,
                originNode,
                sourceMesh,
                originMesh,
                diagnostics);
            return sourceMesh != null || originMesh != null;
        }

        public FbxMeshCompatibilityResult GetTopologyCompatibility(string path)
        {
            ThrowIfDisposed();
            string normalizedPath = MeshNodePath.Normalize(path);
            if (topologyCompatibilityByPath.TryGetValue(normalizedPath, out var compatibility))
            {
                return compatibility;
            }

            TryGetMeshPair(normalizedPath, out var pair);
            compatibility = FbxMeshCompatibility.Evaluate(pair?.OriginMesh, pair?.SourceMesh);
            topologyCompatibilityByPath[normalizedPath] = compatibility;
            return compatibility;
        }

        public IReadOnlyList<IFbxBlendChannel> GetSourceBlendShapes(string path)
        {
            return Source?.GetBlendShapes(path) ?? Array.Empty<IFbxBlendChannel>();
        }

        public IReadOnlyList<IFbxBlendChannel> GetOriginBlendShapes(string path)
        {
            return Origin?.GetBlendShapes(path) ?? Array.Empty<IFbxBlendChannel>();
        }

        public IFbxSkinDeformer GetSourceSkin(string path)
        {
            return Source?.GetPrimarySkin(path);
        }

        public IFbxSkinDeformer GetOriginSkin(string path)
        {
            return Origin?.GetPrimarySkin(path);
        }

        public IReadOnlyList<IFbxNode> GetSourceBoneNodes()
        {
            return Source?.BoneNodes ?? Array.Empty<IFbxNode>();
        }

        public IReadOnlyList<IFbxNode> GetOriginBoneNodes()
        {
            return Origin?.BoneNodes ?? Array.Empty<IFbxNode>();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Source?.Dispose();
            Origin?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FbxInspectionSession));
            }
        }
    }

    public sealed class FbxInspectionAsset : IDisposable
    {
        private bool disposed;

        public string Label { get; }
        public GameObject FbxObject { get; }
        public string AssetPath { get; }
        public ModelImporter Importer { get; }
        public IFbxScene Scene { get; }
        public IReadOnlyList<IFbxNode> BoneNodes { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        private FbxInspectionAsset(
            string label,
            GameObject fbxObject,
            string assetPath,
            ModelImporter importer,
            IFbxScene scene,
            IReadOnlyList<string> diagnostics)
        {
            Label = label;
            FbxObject = fbxObject;
            AssetPath = assetPath;
            Importer = importer;
            Scene = scene;
            Diagnostics = diagnostics ?? Array.Empty<string>();
            BoneNodes = scene?.Nodes
                .Where(node => node != null && node.IsBone)
                .ToArray() ?? Array.Empty<IFbxNode>();
        }

        public static FbxInspectionAsset Open(
            GameObject fbxObject,
            string label,
            ICollection<string> sessionDiagnostics)
        {
            var diagnostics = new List<string>();
            if (fbxObject == null)
            {
                diagnostics.Add($"{label} FBX is not assigned.");
                AddRange(sessionDiagnostics, diagnostics);
                return new FbxInspectionAsset(label, null, null, null, null, diagnostics);
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxObject);
            var importer = !string.IsNullOrEmpty(assetPath)
                ? AssetImporter.GetAtPath(assetPath) as ModelImporter
                : null;
            if (importer == null)
            {
                diagnostics.Add($"{label} asset is not a readable FBX ModelImporter asset.");
            }

            IFbxScene scene = null;
            var sceneResult = FbxUnityAssetReader.ReadScene(fbxObject);
            if (sceneResult.Success)
            {
                scene = sceneResult.Value;
            }
            else
            {
                diagnostics.Add($"{label} FBX read failed: {sceneResult.Message}");
            }

            AddRange(sessionDiagnostics, diagnostics);
            return new FbxInspectionAsset(label, fbxObject, assetPath, importer, scene, diagnostics);
        }

        public IFbxNode GetNode(string path)
        {
            ThrowIfDisposed();
            return Scene?.FindNodeByPath(MeshNodePath.ToFbxPath(path));
        }

        public IFbxMesh GetMesh(string path)
        {
            ThrowIfDisposed();
            return Scene?.FindMeshByNodePath(MeshNodePath.ToFbxPath(path));
        }

        public IReadOnlyList<IFbxBlendChannel> GetBlendShapes(string path)
        {
            return GetMesh(path)?.BlendDeformers
                .SelectMany(deformer => deformer.Channels)
                .Where(channel => channel != null)
                .ToArray() ?? Array.Empty<IFbxBlendChannel>();
        }

        public IFbxSkinDeformer GetPrimarySkin(string path)
        {
            return GetMesh(path)?.SkinDeformers
                .FirstOrDefault(deformer => deformer != null && deformer.Clusters.Any(cluster => cluster.WeightCount > 0));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Scene?.Dispose();
        }

        private static void AddRange<T>(ICollection<T> destination, IEnumerable<T> source)
        {
            if (destination == null || source == null)
            {
                return;
            }

            foreach (var value in source)
            {
                destination.Add(value);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FbxInspectionAsset));
            }
        }
    }

    public sealed class FbxInspectionMeshPair
    {
        public string Path { get; }
        public IFbxNode SourceNode { get; }
        public IFbxNode OriginNode { get; }
        public IFbxMesh SourceMesh { get; }
        public IFbxMesh OriginMesh { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public FbxInspectionMeshPair(
            string path,
            IFbxNode sourceNode,
            IFbxNode originNode,
            IFbxMesh sourceMesh,
            IFbxMesh originMesh,
            IEnumerable<string> diagnostics)
        {
            Path = MeshNodePath.Normalize(path);
            SourceNode = sourceNode;
            OriginNode = originNode;
            SourceMesh = sourceMesh;
            OriginMesh = originMesh;
            Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>();
        }
    }

    public sealed class BoneInspectionSummary
    {
        public IReadOnlyList<string> SourceBonePaths = Array.Empty<string>();
        public IReadOnlyList<string> OriginBonePaths = Array.Empty<string>();
        public IReadOnlyList<string> Diagnostics = Array.Empty<string>();
    }
}
