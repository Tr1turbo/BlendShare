using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    /// <summary>
    /// Short-lived editor inspection session opening Source and Original FBX scenes via ufbx and sharing
    /// the exact <c>OriginRawLocalV2</c> scene/world (<c>W</c>) and per-mesh (<c>L = W * O</c>) transform algebra
    /// used by <see cref="Extraction.PatchExtractionPipeline"/>.
    /// </summary>
    public sealed class FbxInspectionSession : IDisposable
    {
        private bool m_Disposed;
        private readonly Dictionary<string, FbxTopologyCompatibilityResult> m_TopologyCompatibilityByPath =
            new Dictionary<string, FbxTopologyCompatibilityResult>(StringComparer.Ordinal);

        public FbxInspectionAsset Source { get; }
        public FbxInspectionAsset Origin { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public FbxCoordinateAxes OriginAxes { get; }
        public double OriginUnitMeters { get; }
        public FbxCoordinateAxes SourceAxes { get; }
        public double SourceUnitMeters { get; }
        public bool IsExactSameSpace { get; }

        /// <summary>
        /// Scene/world basis and unit conversion <c>W</c> from Source raw space to Origin raw space.
        /// </summary>
        public FbxAffineSpaceTransform SourceWorldToOriginRaw { get; }

        private FbxInspectionSession(GameObject sourceFbx, GameObject originFbx)
        {
            var diagnostics = new List<string>();
            Source = FbxInspectionAsset.Open(sourceFbx, "Source", diagnostics);
            Origin = FbxInspectionAsset.Open(originFbx, "Original", diagnostics);

            if (Origin.Scene != null)
            {
                CanonicalMeshSpace.SanitizeMetadata(Origin.Scene.Metadata, out var originAxes, out double originUnit, out _);
                OriginAxes = originAxes;
                OriginUnitMeters = originUnit;
            }
            else
            {
                OriginAxes = new FbxCoordinateAxes(
                    FbxCoordinateAxis.PositiveX,
                    FbxCoordinateAxis.PositiveY,
                    FbxCoordinateAxis.PositiveZ);
                OriginUnitMeters = 1d;
            }

            if (Source.Scene != null)
            {
                CanonicalMeshSpace.SanitizeMetadata(Source.Scene.Metadata, out var sourceAxes, out double sourceUnit, out _);
                SourceAxes = sourceAxes;
                SourceUnitMeters = sourceUnit;
            }
            else
            {
                SourceAxes = OriginAxes;
                SourceUnitMeters = OriginUnitMeters;
            }

            if (Origin.Scene != null && Source.Scene != null)
            {
                var wMatrix = CanonicalMeshSpace.BuildSpaceToSpace(
                    SourceAxes,
                    SourceUnitMeters,
                    OriginAxes,
                    OriginUnitMeters,
                    out _,
                    out bool exactSame);
                IsExactSameSpace = exactSame;
                if (FbxAffineSpaceTransform.TryCreate(wMatrix, out var wTransform))
                {
                    SourceWorldToOriginRaw = wTransform;
                }
                else
                {
                    SourceWorldToOriginRaw = FbxAffineSpaceTransform.Identity;
                    diagnostics.Add("Source-to-original scene coordinate space transform is singular; falling back to identity.");
                }
            }
            else
            {
                IsExactSameSpace = true;
                SourceWorldToOriginRaw = FbxAffineSpaceTransform.Identity;
            }

            Diagnostics = diagnostics;
        }

        public static FbxInspectionSession Open(GameObject sourceFbx, GameObject originFbx)
        {
            return new FbxInspectionSession(sourceFbx, originFbx);
        }

        /// <summary>
        /// Computes the exact per-mesh transform <c>L = W * sourceToOriginRawOffset</c> matching
        /// <see cref="Extraction.PatchExtractionPipeline"/>.
        /// </summary>
        public bool TryBuildSourceMeshToOriginTransform(
            FbxMatrix4x4 sourceToOriginRawOffset,
            out FbxAffineSpaceTransform transform)
        {
            if (FbxAffineSpaceTransform.IsExactIdentity(sourceToOriginRawOffset))
            {
                transform = SourceWorldToOriginRaw;
                return transform.IsValid;
            }

            if (!FbxAffineSpaceTransform.TryCreate(sourceToOriginRawOffset, out _))
            {
                transform = default;
                return false;
            }

            var total = IsExactSameSpace
                ? sourceToOriginRawOffset
                : SourceWorldToOriginRaw.Offset * sourceToOriginRawOffset;
            return FbxAffineSpaceTransform.TryCreate(total, out transform);
        }

        public FbxImporterSettingsComparison GetImporterComparison()
        {
            return FbxImporterSettingsComparison.Compare(Origin?.Importer, Source?.Importer);
        }

        public bool TryGetMeshPair(string path, out FbxInspectionMeshPair pair)
        {
            ThrowIfDisposed();
            string normalizedPath = FbxNameUtility.NormalizePath(path ?? string.Empty);
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

        public FbxTopologyCompatibilityResult GetTopologyCompatibility(string path)
        {
            ThrowIfDisposed();
            string normalizedPath = FbxNameUtility.NormalizePath(path ?? string.Empty);
            if (m_TopologyCompatibilityByPath.TryGetValue(normalizedPath, out var compatibility))
            {
                return compatibility;
            }

            TryGetMeshPair(normalizedPath, out var pair);
            compatibility = FbxTopologyUtility.EvaluateCompatibility(pair?.OriginMesh, pair?.SourceMesh);
            m_TopologyCompatibilityByPath[normalizedPath] = compatibility;
            return compatibility;
        }

        public IReadOnlyList<string> GetSharedMeshPaths()
        {
            ThrowIfDisposed();
            var originPaths = Origin?.MeshPaths ?? Array.Empty<string>();
            var sourceSet = new HashSet<string>(Source?.MeshPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
            return originPaths
                .Where(sourceSet.Contains)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyList<string> GetSkippedMeshPaths()
        {
            ThrowIfDisposed();
            var originSet = new HashSet<string>(Origin?.MeshPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
            var sourceSet = new HashSet<string>(Source?.MeshPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
            return originSet.Concat(sourceSet)
                .Distinct(StringComparer.Ordinal)
                .Where(p => !(originSet.Contains(p) && sourceSet.Contains(p)))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
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
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            Source?.Dispose();
            Origin?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException(nameof(FbxInspectionSession));
            }
        }
    }

    public sealed class FbxInspectionAsset : IDisposable
    {
        private bool m_Disposed;

        public string Label { get; }
        public GameObject FbxObject { get; }
        public string AssetPath { get; }
        public ModelImporter Importer { get; }
        public IFbxScene Scene { get; }
        public IReadOnlyList<IFbxNode> BoneNodes { get; }
        public IReadOnlyList<string> MeshPaths { get; }
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
            MeshPaths = CollectMeshPaths(scene);
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
            var sceneResult = FbxUnityAssetReader.ReadScene(fbxObject, UfbxLoadOptions.Default);
            if (sceneResult.Success && sceneResult.Value != null)
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
            string norm = FbxNameUtility.NormalizePath(path ?? string.Empty);
            return string.IsNullOrEmpty(norm) ? null : Scene?.FindNodeByPath(norm);
        }

        public IFbxMesh GetMesh(string path)
        {
            ThrowIfDisposed();
            string norm = FbxNameUtility.NormalizePath(path ?? string.Empty);
            return string.IsNullOrEmpty(norm) ? null : Scene?.FindMeshByNodePath(norm);
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
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            Scene?.Dispose();
        }

        private static IReadOnlyList<string> CollectMeshPaths(IFbxScene scene)
        {
            if (scene == null || scene.Meshes == null)
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            for (int m = 0; m < scene.Meshes.Count; m++)
            {
                var mesh = scene.Meshes[m];
                if (mesh == null || mesh.ControlPointCount <= 0) continue;

                bool addedInstance = false;
                var instances = mesh.Instances;
                if (instances != null)
                {
                    for (int i = 0; i < instances.Count; i++)
                    {
                        string p = FbxNameUtility.NormalizePath(instances[i]?.Path ?? string.Empty);
                        if (!string.IsNullOrEmpty(p) && seen.Add(p))
                        {
                            list.Add(p);
                            addedInstance = true;
                        }
                    }
                }

                if (!addedInstance)
                {
                    string fallback = FbxNameUtility.NormalizePath(mesh.PrimaryNode?.Path ?? mesh.Name ?? string.Empty);
                    if (!string.IsNullOrEmpty(fallback) && seen.Add(fallback))
                    {
                        list.Add(fallback);
                    }
                }
            }

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void AddRange<T>(ICollection<T> destination, IEnumerable<T> source)
        {
            if (destination == null || source == null) return;
            foreach (var value in source) destination.Add(value);
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
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
            Path = FbxNameUtility.NormalizePath(path ?? string.Empty);
            SourceNode = sourceNode;
            OriginNode = originNode;
            SourceMesh = sourceMesh;
            OriginMesh = originMesh;
            Diagnostics = diagnostics?.ToArray() ?? Array.Empty<string>();
        }
    }
}
