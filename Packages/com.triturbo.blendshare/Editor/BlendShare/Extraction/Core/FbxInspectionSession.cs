using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
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

        public IReadOnlyList<UfbxBlendChannel> GetSourceBlendShapes(string path)
        {
            return Source?.GetBlendShapes(path) ?? Array.Empty<UfbxBlendChannel>();
        }

        public IReadOnlyList<UfbxBlendChannel> GetOriginBlendShapes(string path)
        {
            return Origin?.GetBlendShapes(path) ?? Array.Empty<UfbxBlendChannel>();
        }

        public UfbxSkinDeformer GetSourceSkin(string path)
        {
            return Source?.GetPrimarySkin(path);
        }

        public UfbxSkinDeformer GetOriginSkin(string path)
        {
            return Origin?.GetPrimarySkin(path);
        }

        public IReadOnlyList<UfbxNode> GetSourceBoneNodes()
        {
            return Source?.BoneNodes ?? Array.Empty<UfbxNode>();
        }

        public IReadOnlyList<UfbxNode> GetOriginBoneNodes()
        {
            return Origin?.BoneNodes ?? Array.Empty<UfbxNode>();
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
        public UfbxScene Scene { get; }
        public IReadOnlyList<UfbxNode> BoneNodes { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        private FbxInspectionAsset(
            string label,
            GameObject fbxObject,
            string assetPath,
            ModelImporter importer,
            UfbxScene scene,
            IReadOnlyList<string> diagnostics)
        {
            Label = label;
            FbxObject = fbxObject;
            AssetPath = assetPath;
            Importer = importer;
            Scene = scene;
            Diagnostics = diagnostics ?? Array.Empty<string>();
            BoneNodes = scene?.Nodes
                .Where(node => node != null && node.NodeType == UfbxNodeType.LimbNode)
                .ToArray() ?? Array.Empty<UfbxNode>();
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

            UfbxScene scene = null;
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

        public UfbxNode GetNode(string path)
        {
            ThrowIfDisposed();
            return Scene?.FindNodeByPath(MeshNodePath.ToFbxPath(path));
        }

        public UfbxMesh GetMesh(string path)
        {
            ThrowIfDisposed();
            return Scene?.FindMeshByNodePath(MeshNodePath.ToFbxPath(path));
        }

        public IReadOnlyList<UfbxBlendChannel> GetBlendShapes(string path)
        {
            return GetMesh(path)?.BlendDeformers
                .SelectMany(deformer => deformer.Channels)
                .Where(channel => channel != null)
                .ToArray() ?? Array.Empty<UfbxBlendChannel>();
        }

        public UfbxSkinDeformer GetPrimarySkin(string path)
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
        public UfbxNode SourceNode { get; }
        public UfbxNode OriginNode { get; }
        public UfbxMesh SourceMesh { get; }
        public UfbxMesh OriginMesh { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public FbxInspectionMeshPair(
            string path,
            UfbxNode sourceNode,
            UfbxNode originNode,
            UfbxMesh sourceMesh,
            UfbxMesh originMesh,
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
