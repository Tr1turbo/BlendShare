using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.FbxReader;
using UnityEditor;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class UnityMeshExtractionSource
    {
        private readonly GameObject root;
        private Dictionary<string, Mesh> meshesByPath;
        private Dictionary<string, Mesh> meshesByName;
        private Dictionary<string, string> pathsByName;

        public UnityMeshExtractionSource(GameObject root)
        {
            this.root = root;
        }

        public Mesh GetMesh(string meshPath, string meshName)
        {
            EnsureLookups();

            if (!string.IsNullOrEmpty(meshPath) && meshesByPath.TryGetValue(meshPath, out var byPath))
            {
                return byPath;
            }

            if (!string.IsNullOrEmpty(meshName) && meshesByName.TryGetValue(meshName, out var byName))
            {
                return byName;
            }

            return null;
        }

        public string GetMeshPath(string meshName)
        {
            EnsureLookups();
            return !string.IsNullOrEmpty(meshName) && pathsByName.TryGetValue(meshName, out var path)
                ? path
                : meshName;
        }

        private void EnsureLookups()
        {
            if (meshesByPath != null)
            {
                return;
            }

            meshesByPath = new Dictionary<string, Mesh>();
            meshesByName = new Dictionary<string, Mesh>();
            pathsByName = new Dictionary<string, string>();

            if (root == null)
            {
                return;
            }

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                string path = GetRelativePath(renderer.transform, root.transform);
                if (!string.IsNullOrEmpty(path) && !meshesByPath.ContainsKey(path))
                {
                    meshesByPath.Add(path, mesh);
                }

                if (!string.IsNullOrEmpty(mesh.name))
                {
                    if (!meshesByName.ContainsKey(mesh.name))
                    {
                        meshesByName.Add(mesh.name, mesh);
                    }

                    if (!pathsByName.ContainsKey(mesh.name))
                    {
                        pathsByName.Add(mesh.name, string.IsNullOrEmpty(path) ? mesh.name : path);
                    }
                }
            }
        }

        private static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
            {
                return string.Empty;
            }

            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }
    }

    public sealed class FbxMeshExtractionSource
    {
        private readonly GameObject fbxAsset;
        private readonly Dictionary<CacheKey, FbxMeshSnapshot> snapshots = new();

        public FbxMeshExtractionSource(GameObject fbxAsset)
        {
            this.fbxAsset = fbxAsset;
        }

        public FbxMeshSnapshot GetMesh(
            string meshPath,
            string meshName,
            FbxMeshReadOptions options)
        {
            var key = new CacheKey(meshPath, meshName, NormalizeReadOptions(options));
            if (snapshots.TryGetValue(key, out var snapshot))
            {
                return snapshot;
            }

            snapshot = ReadMesh(meshPath, meshName, key.Options);
            snapshots[key] = snapshot;
            return snapshot;
        }

        private FbxMeshSnapshot ReadMesh(string meshPath, string meshName, FbxMeshReadOptions options)
        {
            if (fbxAsset == null)
            {
                return null;
            }

            var candidates = new[] { meshPath, meshName }
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct()
                .ToArray();

            if (candidates.Length == 0)
            {
                return null;
            }

            var results = BinaryFbxMeshReader.TryReadMeshes(fbxAsset, candidates, options);
            foreach (string candidate in candidates)
            {
                if (results.TryGetValue(candidate, out var snapshot))
                {
                    return snapshot;
                }
            }

            return null;
        }

        private static FbxMeshReadOptions NormalizeReadOptions(FbxMeshReadOptions options)
        {
            if ((options & (FbxMeshReadOptions.BlendShapes | FbxMeshReadOptions.BoneWeights)) != 0)
            {
                options |= FbxMeshReadOptions.ControlPointPositions;
            }

            return options;
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly string meshPath;
            private readonly string meshName;
            public readonly FbxMeshReadOptions Options;

            public CacheKey(string meshPath, string meshName, FbxMeshReadOptions options)
            {
                this.meshPath = meshPath ?? string.Empty;
                this.meshName = meshName ?? string.Empty;
                Options = options;
            }

            public bool Equals(CacheKey other)
            {
                return meshPath == other.meshPath &&
                       meshName == other.meshName &&
                       Options == other.Options;
            }

            public override bool Equals(object obj)
            {
                return obj is CacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = meshPath.GetHashCode();
                    hash = (hash * 397) ^ meshName.GetHashCode();
                    hash = (hash * 397) ^ (int)Options;
                    return hash;
                }
            }
        }
    }

#if ENABLE_FBX_SDK
    public sealed class FbxSdkExtractionSource : IDisposable
    {
        private readonly GameObject fbxAsset;
        private FbxManager fbxManager;
        private FbxScene scene;
        private bool imported;
        private bool disposed;
        private string importError;

        public FbxSdkExtractionSource(GameObject fbxAsset)
        {
            this.fbxAsset = fbxAsset;
        }

        public bool TryGetRootNode(out FbxNode rootNode, out string error)
        {
            ThrowIfDisposed();
            EnsureImported();
            error = importError;
            rootNode = string.IsNullOrEmpty(importError) ? scene?.GetRootNode() : null;
            return rootNode != null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            scene?.Destroy();
            scene = null;
            fbxManager?.Destroy();
            fbxManager = null;
        }

        private void EnsureImported()
        {
            ThrowIfDisposed();
            if (imported)
            {
                return;
            }

            imported = true;
            if (fbxAsset == null)
            {
                importError = "FBX asset is null.";
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                importError = $"Cannot resolve asset path for '{fbxAsset.name}'.";
                return;
            }

            fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            scene = FbxScene.Create(fbxManager, fbxAsset.name);
            var importer = FbxImporter.Create(fbxManager, "");
            int fileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (!importer.Initialize(assetPath, fileFormat, fbxManager.GetIOSettings()))
            {
                importError = $"Failed to initialize FBX importer for '{assetPath}'.";
                importer.Destroy();
                return;
            }

            importer.Import(scene);
            importer.Destroy();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FbxSdkExtractionSource));
            }
        }
    }
#endif
}
