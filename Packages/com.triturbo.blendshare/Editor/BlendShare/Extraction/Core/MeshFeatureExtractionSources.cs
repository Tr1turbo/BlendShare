using System;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using UnityEditor;
#endif

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Lazy lookup for Unity meshes under an imported FBX prefab root.
    /// </summary>
    public sealed class UnityMeshExtractionSource
    {
        private readonly GameObject root;
        private Dictionary<string, Mesh> meshesByPath;

        public UnityMeshExtractionSource(GameObject root)
        {
            this.root = root;
        }

        /// <summary>
        /// Gets a Unity mesh by renderer path.
        /// </summary>
        /// <param name="path">Renderer path relative to the FBX prefab root.</param>
        /// <returns>The matching Unity mesh, or <c>null</c> when the path is not found.</returns>
        public Mesh GetMesh(string path)
        {
            EnsureLookups();
            string normalizedPath = MeshNodePath.Normalize(path);
            if (meshesByPath.TryGetValue(normalizedPath, out var byPath))
            {
                return byPath;
            }

            return null;
        }

        private void EnsureLookups()
        {
            if (meshesByPath != null)
            {
                return;
            }

            meshesByPath = new Dictionary<string, Mesh>();

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

                string path = MeshNodePath.GetRelativePath(renderer.transform, root.transform);
                if (!meshesByPath.ContainsKey(path))
                {
                    meshesByPath.Add(path, mesh);
                }
            }
        }
    }

#if ENABLE_FBX_SDK
    /// <summary>
    /// Lazy FBX SDK scene owner for exact-path extraction and explicit raw SDK access.
    /// </summary>
    public sealed class FbxSdkExtractionSource : IDisposable
    {
        private readonly GameObject fbxGo;
        private FbxManager fbxManager;
        private FbxScene scene;
        private bool imported;
        private bool disposed;
        private string importError;

        public FbxSdkExtractionSource(GameObject go)
        {
            this.fbxGo = go;
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
            if (fbxGo == null)
            {
                importError = "FBX asset is null.";
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxGo);
            if (string.IsNullOrEmpty(assetPath))
            {
                importError = $"Cannot resolve asset path for '{fbxGo.name}'.";
                return;
            }

            fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            scene = FbxScene.Create(fbxManager, fbxGo.name);
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
