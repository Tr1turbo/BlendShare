using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Triturbo.Fbx
{
    public enum UfbxElementType
    {
        Unknown,
        Node,
        Mesh,
        SkinDeformer,
        SkinCluster,
        BlendDeformer,
        BlendChannel,
        BlendShape
    }

    public enum UfbxNodeType
    {
        Unknown,
        Null,
        Mesh,
        LimbNode,
        Root
    }

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
                    meshes[i] = new UfbxMesh(this, i, 0, string.Empty, null, 0, 0, 0);
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

        internal static Vector3d[] ToVector3dArray(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<Vector3d>();
            }

            var result = new Vector3d[values.Length / 3];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new Vector3d(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            }

            return result;
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

    public abstract class UfbxElement
    {
        protected UfbxElement(UfbxScene scene, UfbxElementType elementType, int index, long id, string name)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            ElementType = elementType;
            Index = index;
            Id = id;
            Name = name ?? string.Empty;
        }

        public long Id { get; }
        public string Name { get; }
        public UfbxElementType ElementType { get; }
        internal UfbxScene Scene { get; }
        internal int Index { get; }

        protected void EnsureAlive()
        {
            Scene.EnsureAlive();
        }
    }

    public sealed class UfbxNode : UfbxElement
    {
        private IReadOnlyList<UfbxNode> children = Array.AsReadOnly(Array.Empty<UfbxNode>());

        internal UfbxNode(
            UfbxScene scene,
            int nodeIndex,
            long id,
            string name,
            string path,
            UfbxNodeType nodeType,
            UfbxTransform localTransform,
            FbxTransform? lclTransform = null,
            Vector3d eulerRotation = default,
            Vector3d preRotation = default,
            Vector3d postRotation = default)
            : base(scene, UfbxElementType.Node, nodeIndex, id, name)
        {
            Path = FbxNameUtility.NormalizePath(path);
            NodeType = nodeType;
            LocalTransform = localTransform;
            LclTransform = lclTransform ?? FbxTransform.Identity;
            EulerRotation = eulerRotation;
            PreRotation = preRotation;
            PostRotation = postRotation;
        }

        public string Path { get; }
        public UfbxNodeType NodeType { get; }
        public UfbxTransform LocalTransform { get; }
        public Vector3d LocalTranslation => LocalTransform.Translation;
        public Quaterniond LocalRotation => LocalTransform.Rotation;
        public Vector3d LocalScale => LocalTransform.Scale;
        public FbxTransform LclTransform { get; }
        public Vector3d LclTranslation => LclTransform.Translation;
        public Vector3d LclRotation => LclTransform.Rotation;
        public Vector3d LclScale => LclTransform.Scale;
        public Vector3d EulerRotation { get; }
        public Vector3d PreRotation { get; }
        public Vector3d PostRotation { get; }
        public UfbxNode Parent { get; private set; }
        public IReadOnlyList<UfbxNode> Children => children;

        internal void SetParent(UfbxNode parent)
        {
            Parent = parent;
        }

        internal void SetChildren(IEnumerable<UfbxNode> nodes)
        {
            children = FbxCollection.ToReadOnly(nodes);
        }
    }

    public sealed class UfbxMesh : UfbxElement
    {
        private IReadOnlyList<UfbxDeformer> deformers;
        private IReadOnlyList<UfbxSkinDeformer> skinDeformers;
        private IReadOnlyList<UfbxBlendDeformer> blendDeformers;

        internal UfbxMesh(
            UfbxScene scene,
            int meshIndex,
            long id,
            string name,
            UfbxNode ownerNode,
            int controlPointCount,
            int skinCount,
            int blendDeformerCount)
            : base(scene, UfbxElementType.Mesh, meshIndex, id, name)
        {
            OwnerNode = ownerNode;
            ControlPointCount = Math.Max(0, controlPointCount);
            SkinCount = Math.Max(0, skinCount);
            BlendDeformerCount = Math.Max(0, blendDeformerCount);
        }

        public UfbxNode OwnerNode { get; }
        public int ControlPointCount { get; }
        public int SkinCount { get; }
        public int BlendDeformerCount { get; }
        public IReadOnlyList<UfbxDeformer> Deformers => deformers ??= BuildDeformers();
        public IReadOnlyList<UfbxSkinDeformer> SkinDeformers => skinDeformers ??= FbxCollection.ToReadOnly(Deformers.OfType<UfbxSkinDeformer>());
        public IReadOnlyList<UfbxBlendDeformer> BlendDeformers => blendDeformers ??= FbxCollection.ToReadOnly(Deformers.OfType<UfbxBlendDeformer>());

        public int CopyVertices(double[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyControlPoints(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public int CopyNormals(double[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyControlPointNormals(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public int CopyTangents(double[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyControlPointTangents(Scene.Handle, Index, destination, GetVectorCapacity(destination));
        }

        public Vector3d[] GetVertices()
        {
            var values = new double[ControlPointCount * 3];
            return CopyVertices(values) != 0 ? UfbxScene.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        public Vector3d[] GetNormals()
        {
            var values = new double[ControlPointCount * 3];
            return CopyNormals(values) != 0 ? UfbxScene.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        public Vector3d[] GetTangents()
        {
            var values = new double[ControlPointCount * 3];
            return CopyTangents(values) != 0 ? UfbxScene.ToVector3dArray(values) : Array.Empty<Vector3d>();
        }

        private IReadOnlyList<UfbxDeformer> BuildDeformers()
        {
            EnsureAlive();
            var result = new List<UfbxDeformer>(SkinCount + BlendDeformerCount);
            for (int i = 0; i < SkinCount; i++)
            {
                if (UfbxNative.GetSkinInfo(Scene.Handle, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, builder => UfbxNative.CopySkinName(Scene.Handle, Index, i, builder, builder.Capacity));
                result.Add(new UfbxSkinDeformer(Scene, this, i, (long)info.Id, name, info.ClusterCount));
            }

            for (int i = 0; i < BlendDeformerCount; i++)
            {
                if (UfbxNative.GetBlendDeformerInfo(Scene.Handle, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, builder => UfbxNative.CopyBlendDeformerName(Scene.Handle, Index, i, builder, builder.Capacity));
                result.Add(new UfbxBlendDeformer(Scene, this, i, (long)info.Id, name, info.ChannelCount));
            }

            return FbxCollection.ToReadOnly(result);
        }

        private static int GetVectorCapacity(double[] destination)
        {
            return destination?.Length / 3 ?? 0;
        }
    }

    public abstract class UfbxDeformer : UfbxElement
    {
        protected UfbxDeformer(UfbxScene scene, UfbxElementType elementType, UfbxMesh ownerMesh, int index, long id, string name)
            : base(scene, elementType, index, id, name)
        {
            OwnerMesh = ownerMesh;
        }

        public UfbxMesh OwnerMesh { get; }
    }

    public sealed class UfbxSkinDeformer : UfbxDeformer
    {
        private IReadOnlyList<UfbxSkinCluster> clusters;

        internal UfbxSkinDeformer(UfbxScene scene, UfbxMesh ownerMesh, int skinIndex, long id, string name, int clusterCount)
            : base(scene, UfbxElementType.SkinDeformer, ownerMesh, skinIndex, id, name)
        {
            ClusterCount = Math.Max(0, clusterCount);
        }

        public int ClusterCount { get; }
        public IReadOnlyList<UfbxSkinCluster> Clusters => clusters ??= BuildClusters();

        private IReadOnlyList<UfbxSkinCluster> BuildClusters()
        {
            EnsureAlive();
            var result = new List<UfbxSkinCluster>(ClusterCount);
            for (int i = 0; i < ClusterCount; i++)
            {
                if (UfbxNative.GetSkinClusterInfo(Scene.Handle, OwnerMesh.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, builder => UfbxNative.CopyClusterName(Scene.Handle, OwnerMesh.Index, Index, i, builder, builder.Capacity));
                var boneNode = info.BoneNodeIndex >= 0 && info.BoneNodeIndex < Scene.Nodes.Count ? Scene.Nodes[info.BoneNodeIndex] : null;
                result.Add(new UfbxSkinCluster(Scene, OwnerMesh, this, i, (long)info.Id, name, boneNode, info));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxSkinCluster : UfbxElement
    {
        internal UfbxSkinCluster(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxSkinDeformer ownerSkin,
            int clusterIndex,
            long id,
            string name,
            UfbxNode boneNode,
            UfbxNative.ClusterInfo info)
            : base(scene, UfbxElementType.SkinCluster, clusterIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerSkin = ownerSkin;
            BoneNode = boneNode;
            WeightCount = Math.Max(0, info.WeightCount);
            MeshBindWorld = UfbxScene.ToMatrix(info.MeshBindWorld);
            BindToWorld = UfbxScene.ToMatrix(info.BoneBindWorld);
            MeshNodeToBone = UfbxScene.ToMatrix(info.MeshNodeToBone);
            GeometryToBone = UfbxScene.ToMatrix(info.GeometryToBone);
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxSkinDeformer OwnerSkin { get; }
        public UfbxNode BoneNode { get; }
        public int WeightCount { get; }
        public FbxMatrix4x4 MeshBindWorld { get; }
        public FbxMatrix4x4 BindToWorld { get; }
        public FbxMatrix4x4 MeshNodeToBone { get; }
        public FbxMatrix4x4 GeometryToBone { get; }

        public int CopyIndices(int[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyClusterIndices(Scene.Handle, OwnerMesh.Index, OwnerSkin.Index, Index, destination, destination?.Length ?? 0);
        }

        public int CopyVertices(int[] destination)
        {
            return CopyIndices(destination);
        }

        public int CopyWeights(double[] destination)
        {
            EnsureAlive();
            return UfbxNative.CopyClusterWeights(Scene.Handle, OwnerMesh.Index, OwnerSkin.Index, Index, destination, destination?.Length ?? 0);
        }

        public int[] GetIndices()
        {
            var values = new int[WeightCount];
            return CopyIndices(values) != 0 ? values : Array.Empty<int>();
        }

        public int[] GetVertices()
        {
            return GetIndices();
        }

        public double[] GetWeights()
        {
            var values = new double[WeightCount];
            return CopyWeights(values) != 0 ? values : Array.Empty<double>();
        }
    }

    public sealed class UfbxBlendDeformer : UfbxDeformer
    {
        private IReadOnlyList<UfbxBlendChannel> channels;

        internal UfbxBlendDeformer(UfbxScene scene, UfbxMesh ownerMesh, int deformerIndex, long id, string name, int channelCount)
            : base(scene, UfbxElementType.BlendDeformer, ownerMesh, deformerIndex, id, name)
        {
            ChannelCount = Math.Max(0, channelCount);
        }

        public int ChannelCount { get; }
        public IReadOnlyList<UfbxBlendChannel> Channels => channels ??= BuildChannels();

        private IReadOnlyList<UfbxBlendChannel> BuildChannels()
        {
            EnsureAlive();
            var result = new List<UfbxBlendChannel>(ChannelCount);
            for (int i = 0; i < ChannelCount; i++)
            {
                if (UfbxNative.GetBlendChannelInfo(Scene.Handle, OwnerMesh.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, builder => UfbxNative.CopyBlendChannelName(Scene.Handle, OwnerMesh.Index, Index, i, builder, builder.Capacity));
                result.Add(new UfbxBlendChannel(Scene, OwnerMesh, this, i, (long)info.Id, name, info.FrameCount));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxBlendChannel : UfbxElement
    {
        private IReadOnlyList<UfbxBlendShape> blendShapes;

        internal UfbxBlendChannel(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            int channelIndex,
            long id,
            string name,
            int blendShapeCount)
            : base(scene, UfbxElementType.BlendChannel, channelIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            BlendShapeCount = Math.Max(0, blendShapeCount);
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxBlendDeformer OwnerDeformer { get; }
        public int BlendShapeCount { get; }
        public IReadOnlyList<UfbxBlendShape> BlendShapes => blendShapes ??= BuildBlendShapes();

        private IReadOnlyList<UfbxBlendShape> BuildBlendShapes()
        {
            EnsureAlive();
            var result = new List<UfbxBlendShape>(BlendShapeCount);
            for (int i = 0; i < BlendShapeCount; i++)
            {
                if (UfbxNative.GetBlendFrameInfo(Scene.Handle, OwnerMesh.Index, OwnerDeformer.Index, Index, i, out var info) == 0)
                {
                    continue;
                }

                string name = UfbxScene.CopyString(info.NameLength, builder => UfbxNative.CopyBlendFrameName(Scene.Handle, OwnerMesh.Index, OwnerDeformer.Index, Index, i, builder, builder.Capacity));
                result.Add(new UfbxBlendShape(Scene, OwnerMesh, OwnerDeformer, this, i, (long)info.Id, name, info.Weight, info.OffsetCount));
            }

            return FbxCollection.ToReadOnly(result);
        }
    }

    public sealed class UfbxBlendShape : UfbxElement
    {
        internal UfbxBlendShape(
            UfbxScene scene,
            UfbxMesh ownerMesh,
            UfbxBlendDeformer ownerDeformer,
            UfbxBlendChannel ownerChannel,
            int blendShapeIndex,
            long id,
            string name,
            double weight,
            int offsetCount)
            : base(scene, UfbxElementType.BlendShape, blendShapeIndex, id, name)
        {
            OwnerMesh = ownerMesh;
            OwnerDeformer = ownerDeformer;
            OwnerChannel = ownerChannel;
            Weight = weight;
            OffsetCount = Math.Max(0, offsetCount);
        }

        public UfbxMesh OwnerMesh { get; }
        public UfbxBlendDeformer OwnerDeformer { get; }
        public UfbxBlendChannel OwnerChannel { get; }
        public double Weight { get; }
        public int OffsetCount { get; }

        public int CopyOffsets(int[] destinationIndices, double[] destinationPositions, double[] destinationNormals)
        {
            EnsureAlive();
            int destinationCount = Math.Min(
                destinationIndices?.Length ?? 0,
                destinationPositions?.Length / 3 ?? 0);
            if (destinationNormals != null)
            {
                destinationCount = Math.Min(destinationCount, destinationNormals.Length / 3);
            }

            return UfbxNative.CopyBlendFrameOffsets(
                Scene.Handle,
                OwnerMesh.Index,
                OwnerDeformer.Index,
                OwnerChannel.Index,
                Index,
                destinationIndices,
                destinationPositions,
                destinationNormals,
                destinationCount);
        }
    }
}
