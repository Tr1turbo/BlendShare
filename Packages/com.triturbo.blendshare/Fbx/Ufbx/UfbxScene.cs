using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public sealed class UfbxScene : IDisposable
    {
        private NativeSceneHandle handle;
        private readonly Dictionary<string, UfbxNode> nodesByPath;
        private readonly Dictionary<string, UfbxMesh> meshesByNodePath;

        public string AssetPath { get; }
        public bool IsDisposed => handle == null || handle.IsClosed || handle.IsInvalid;
        public IReadOnlyList<UfbxNode> Nodes { get; }
        public IReadOnlyList<UfbxMesh> Meshes { get; }
        public UfbxNode RootNode => Nodes.Count > 0 ? Nodes[0] : null;

        private UfbxScene(NativeSceneHandle handle, string assetPath)
        {
            this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
            AssetPath = assetPath;
            Nodes = BuildNodes();
            Meshes = BuildMeshes();
            nodesByPath = Nodes
                .Where(node => !string.IsNullOrEmpty(node.Path))
                .GroupBy(node => node.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            meshesByNodePath = Meshes
                .Where(mesh => mesh.OwnerNode != null && !string.IsNullOrEmpty(mesh.OwnerNode.Path))
                .GroupBy(mesh => mesh.OwnerNode.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        public static UfbxScene Load(string assetPath)
        {
            var result = TryLoad(assetPath);
            if (!result.Success)
            {
                throw new FbxReadException(result.Status, result.Message, result.Diagnostics);
            }

            return result.Value;
        }

        public static FbxReadResult<UfbxScene> TryLoad(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX asset path is empty.");
            }

            if (!File.Exists(assetPath))
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.FileNotFound, $"FBX file '{assetPath}' was not found.");
            }

            IntPtr loaded = IntPtr.Zero;
            try
            {
                var error = new StringBuilder(2048);
                if (UfbxNative.Load(assetPath, out loaded, error, error.Capacity) == 0 || loaded == IntPtr.Zero)
                {
                    return FbxReadResult<UfbxScene>.Failed(
                        FbxReadStatus.ParseError,
                        string.IsNullOrWhiteSpace(error.ToString()) ? "ufbx failed to load the FBX file." : error.ToString());
                }

                var sceneHandle = new NativeSceneHandle(loaded);
                loaded = IntPtr.Zero;
                try
                {
                    return FbxReadResult<UfbxScene>.Succeeded(new UfbxScene(sceneHandle, assetPath));
                }
                catch
                {
                    sceneHandle.Dispose();
                    throw;
                }
            }
            catch (DllNotFoundException exception)
            {
                if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.SectionUnavailable, exception.Message);
            }
            catch (EntryPointNotFoundException exception)
            {
                if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.SectionUnavailable, exception.Message);
            }
            catch (Exception exception) when (exception is SEHException || exception is InvalidOperationException || exception is ArgumentException)
            {
                if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.ParseError, $"ufbx reader failed: {exception.Message}");
            }
        }

        public void Dispose()
        {
            if (handle == null)
            {
                return;
            }

            handle.Dispose();
            handle = null;
        }

        public UfbxNode FindNodeByPath(string path)
        {
            EnsureAlive();
            nodesByPath.TryGetValue(FbxNameUtility.NormalizePath(path), out var node);
            return node;
        }

        public UfbxMesh FindMeshByNodePath(string nodePath)
        {
            EnsureAlive();
            meshesByNodePath.TryGetValue(FbxNameUtility.NormalizePath(nodePath), out var mesh);
            return mesh;
        }

        internal IntPtr Handle
        {
            get
            {
                EnsureAlive();
                return handle.DangerousGetHandle();
            }
        }

        internal void EnsureAlive()
        {
            if (handle == null || handle.IsClosed || handle.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(UfbxScene));
            }
        }

        private sealed class NativeSceneHandle : SafeHandle
        {
            public NativeSceneHandle()
                : base(IntPtr.Zero, true)
            {
            }

            public NativeSceneHandle(IntPtr handle)
                : base(IntPtr.Zero, true)
            {
                SetHandle(handle);
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                UfbxNative.Free(handle);
                return true;
            }
        }

        private IReadOnlyList<UfbxNode> BuildNodes()
        {
            int count = Math.Max(0, UfbxNative.GetNodeCount(Handle));
            var nodes = new UfbxNode[count];
            var parentIndices = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetNodeInfo(Handle, i, out var info) == 0)
                {
                    parentIndices[i] = -1;
                    nodes[i] = new UfbxNode(this, i, 0, string.Empty, string.Empty, UfbxNodeType.Unknown, UfbxTransform.Identity);
                    continue;
                }

                parentIndices[i] = info.ParentIndex;
                string name = CopyString(info.NameLength, builder => UfbxNative.CopyNodeName(Handle, i, builder, builder.Capacity));
                string path = CopyString(info.PathLength, builder => UfbxNative.CopyNodePath(Handle, i, builder, builder.Capacity));
                var lclTransform = new FbxTransform(
                    new Vector3d(info.LclTranslationX, info.LclTranslationY, info.LclTranslationZ),
                    new Vector3d(info.LclRotationX, info.LclRotationY, info.LclRotationZ),
                    new Vector3d(info.LclScaleX, info.LclScaleY, info.LclScaleZ));
                var localTransform = new UfbxTransform(
                    new Vector3d(info.UfbxLocalTranslationX, info.UfbxLocalTranslationY, info.UfbxLocalTranslationZ),
                    new Quaterniond(info.UfbxLocalRotationX, info.UfbxLocalRotationY, info.UfbxLocalRotationZ, info.UfbxLocalRotationW),
                    new Vector3d(info.UfbxLocalScaleX, info.UfbxLocalScaleY, info.UfbxLocalScaleZ));
                nodes[i] = new UfbxNode(
                    this,
                    i,
                    (long)info.Id,
                    name,
                    path,
                    ToNodeType(info.Type),
                    localTransform,
                    lclTransform,
                    new Vector3d(info.EulerRotationX, info.EulerRotationY, info.EulerRotationZ),
                    new Vector3d(info.PreRotationX, info.PreRotationY, info.PreRotationZ),
                    new Vector3d(info.PostRotationX, info.PostRotationY, info.PostRotationZ));
            }

            var children = nodes.ToDictionary(node => node, _ => new List<UfbxNode>());
            for (int i = 0; i < nodes.Length; i++)
            {
                if (parentIndices[i] >= 0 && parentIndices[i] < nodes.Length)
                {
                    nodes[i].SetParent(nodes[parentIndices[i]]);
                    children[nodes[parentIndices[i]]].Add(nodes[i]);
                }
            }

            foreach (var pair in children)
            {
                pair.Key.SetChildren(pair.Value);
            }

            return Array.AsReadOnly(nodes);
        }

        private IReadOnlyList<UfbxMesh> BuildMeshes()
        {
            int count = Math.Max(0, UfbxNative.GetMeshCount(Handle));
            var meshes = new UfbxMesh[count];
            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetMeshInfo(Handle, i, out var info) == 0)
                {
                    meshes[i] = new UfbxMesh(this, i, 0, string.Empty, null, 0, 0, 0, 0, 0);
                    continue;
                }

                string name = CopyString(info.NameLength, builder => UfbxNative.CopyMeshName(Handle, i, builder, builder.Capacity));
                var ownerNode = info.NodeIndex >= 0 && info.NodeIndex < Nodes.Count ? Nodes[info.NodeIndex] : null;
                meshes[i] = new UfbxMesh(
                    this,
                    i,
                    (long)info.Id,
                    name,
                    ownerNode,
                    info.ControlPointCount,
                    info.FaceCount,
                    info.FaceIndexCount,
                    info.SkinCount,
                    info.BlendDeformerCount);
            }

            return Array.AsReadOnly(meshes);
        }

        internal static string CopyString(int length, Func<StringBuilder, int> copy)
        {
            var builder = new StringBuilder(Math.Max(1, length + 1));
            copy(builder);
            return builder.ToString();
        }

        internal static FbxMatrix4x4 ToMatrix(UfbxNative.Matrix matrix)
        {
            return FbxMatrix4x4.FromRowMajor(matrix.ToRowMajorArray());
        }

        private static UfbxNodeType ToNodeType(int type)
        {
            return type switch
            {
                1 => UfbxNodeType.Mesh,
                3 => UfbxNodeType.LimbNode,
                4 => UfbxNodeType.Root,
                _ => UfbxNodeType.Null
            };
        }
    }

}
