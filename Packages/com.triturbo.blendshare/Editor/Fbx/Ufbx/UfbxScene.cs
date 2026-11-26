using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Scene-backed FBX reader powered by vendored ufbx through the BlendShareUfbx native bridge (ABI v4).</summary>
    public sealed class UfbxScene : IFbxScene
    {
        private NativeSceneHandle handle;
        private readonly Dictionary<string, IFbxNode> nodesByPath;
        private readonly Dictionary<string, IFbxMesh> meshesByNodePath;

        /// <inheritdoc />
        public string AssetPath { get; }

        /// <inheritdoc />
        public bool IsDisposed
        {
            get
            {
                var current = Volatile.Read(ref handle);
                return current == null || current.IsClosed || current.IsInvalid;
            }
        }

        /// <inheritdoc />
        public FbxSceneMetadata Metadata { get; }

        /// <inheritdoc />
        public FbxCoordinateAxes? RequestedTargetAxes { get; }

        /// <inheritdoc />
        public double? RequestedTargetUnitMeters { get; }

        /// <inheritdoc />
        public IFbxNode RootNode => Nodes.Count > 0 ? Nodes[0] : null;

        /// <inheritdoc />
        public IReadOnlyList<IFbxNode> Nodes { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxMesh> Meshes { get; }

        /// <inheritdoc />
        public IReadOnlyList<IFbxMaterial> Materials { get; }

        internal delegate int NativeCopyStringDelegate(ref byte dst, int dstSize);

        private UfbxScene(NativeSceneHandle sceneHandle, string assetPath, UfbxLoadOptions options)
        {
            handle = sceneHandle ?? throw new ArgumentNullException(nameof(sceneHandle));
            AssetPath = assetPath ?? string.Empty;
            RequestedTargetAxes = options.TargetAxes;
            RequestedTargetUnitMeters = options.TargetUnitMeters;

            if (UfbxNative.GetSceneInfo(sceneHandle, out var sceneInfo) != UfbxNative.StatusOk)
            {
                throw new FbxReadException(
                    FbxReadStatus.ParseError,
                    "The BlendShare ufbx native plugin failed to read scene metadata.");
            }

            Metadata = new FbxSceneMetadata(
                sceneInfo.FileVersion,
                ToCoordinateAxis(sceneInfo.OriginalUpAxis),
                sceneInfo.UnitMeters,
                sceneInfo.OriginalUnitMeters,
                sceneInfo.FramesPerSecond,
                SanitizeAxes(sceneInfo.Axes));

            var materialArray = BuildMaterials(sceneHandle, sceneInfo.MaterialCount);
            Materials = Array.AsReadOnly<IFbxMaterial>(materialArray);

            var nodeArray = BuildNodes(sceneHandle, sceneInfo.NodeCount, materialArray);
            Nodes = Array.AsReadOnly<IFbxNode>(nodeArray);

            var meshArray = BuildMeshes(sceneHandle, sceneInfo.MeshCount, nodeArray, materialArray, out _);
            Meshes = Array.AsReadOnly<IFbxMesh>(meshArray);

            nodesByPath = new Dictionary<string, IFbxNode>(nodeArray.Length, StringComparer.Ordinal);
            meshesByNodePath = new Dictionary<string, IFbxMesh>(meshArray.Length, StringComparer.Ordinal);

            for (int i = 0; i < nodeArray.Length; i++)
            {
                var node = nodeArray[i];
                if (node.MeshIndex >= 0 && node.MeshIndex < meshArray.Length)
                {
                    node.SetMesh(meshArray[node.MeshIndex]);
                }

                if (!string.IsNullOrEmpty(node.Path))
                {
                    if (!nodesByPath.ContainsKey(node.Path))
                    {
                        nodesByPath.Add(node.Path, node);
                    }

                    if (node.Mesh != null && !meshesByNodePath.ContainsKey(node.Path))
                    {
                        meshesByNodePath.Add(node.Path, node.Mesh);
                    }
                }
            }
        }

        /// <summary>Loads an FBX file from disk or throws <see cref="FbxReadException"/> on failure.</summary>
        public static UfbxScene Load(string assetPath, UfbxLoadOptions? options = null)
        {
            var result = TryLoad(assetPath, options);
            if (!result.Success)
            {
                throw new FbxReadException(result.Status, result.Message, result.Diagnostics);
            }

            return result.Value;
        }

        /// <summary>Attempts to load an FBX file from disk.</summary>
        public static FbxReadResult<UfbxScene> TryLoad(string assetPath, UfbxLoadOptions? options = null)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX asset path is empty.");
            }

            if (!File.Exists(assetPath))
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.FileNotFound, $"FBX file '{assetPath}' was not found.");
            }

            var resolvedOptions = options ?? UfbxLoadOptions.Default;
            var abiCheck = CheckAbiVersion();
            if (abiCheck != null)
            {
                return abiCheck;
            }

            IntPtr loaded = IntPtr.Zero;
            byte[] pathBytes = null;
            try
            {
                var nativeOpts = BuildNativeOptions(resolvedOptions);
                int pathByteLen = Encoding.UTF8.GetByteCount(assetPath);
                pathBytes = ArrayPool<byte>.Shared.Rent(pathByteLen + 1);
                int written = Encoding.UTF8.GetBytes(assetPath, 0, assetPath.Length, pathBytes, 0);
                pathBytes[written] = 0;

                Span<byte> errorBuf = stackalloc byte[2048];
                int status = UfbxNative.LoadFile(
                    ref pathBytes[0],
                    ref nativeOpts,
                    out loaded,
                    ref MemoryMarshal.GetReference(errorBuf),
                    errorBuf.Length);

                if (status != UfbxNative.StatusOk || loaded == IntPtr.Zero)
                {
                    if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                    string errorMessage = DecodeUtf8(errorBuf, errorBuf.Length);
                    return FbxReadResult<UfbxScene>.Failed(
                        MapStatus(status),
                        string.IsNullOrWhiteSpace(errorMessage) ? $"ufbx failed to load FBX file '{assetPath}'." : errorMessage);
                }

                return WrapNativeScene(ref loaded, assetPath, resolvedOptions);
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
            catch (FbxReadException exception)
            {
                return FbxReadResult<UfbxScene>.Failed(exception.Status, exception.Message, exception.Diagnostics);
            }
            catch (Exception exception) when (exception is SEHException || exception is InvalidOperationException || exception is ArgumentException)
            {
                if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.ParseError, $"ufbx reader failed: {exception.Message}");
            }
            finally
            {
                if (pathBytes != null)
                {
                    ArrayPool<byte>.Shared.Return(pathBytes);
                }
            }
        }

        /// <summary>Loads an FBX scene from an in-memory byte span or throws <see cref="FbxReadException"/> on failure.</summary>
        public static UfbxScene LoadFromMemory(ReadOnlySpan<byte> data, UfbxLoadOptions? options = null, string sourceName = null)
        {
            var result = TryLoadFromMemory(data, options, sourceName);
            if (!result.Success)
            {
                throw new FbxReadException(result.Status, result.Message, result.Diagnostics);
            }

            return result.Value;
        }

        /// <summary>Attempts to load an FBX scene from an in-memory byte span.</summary>
        public static FbxReadResult<UfbxScene> TryLoadFromMemory(ReadOnlySpan<byte> data, UfbxLoadOptions? options = null, string sourceName = null)
        {
            if (data.IsEmpty)
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX memory buffer is empty.");
            }

            var resolvedOptions = options ?? UfbxLoadOptions.Default;
            var abiCheck = CheckAbiVersion();
            if (abiCheck != null)
            {
                return abiCheck;
            }

            IntPtr loaded = IntPtr.Zero;
            byte[] filenameBytes = null;
            try
            {
                var nativeOpts = BuildNativeOptions(resolvedOptions);
                int fnLen = !string.IsNullOrEmpty(sourceName) ? Encoding.UTF8.GetByteCount(sourceName) : 0;
                filenameBytes = ArrayPool<byte>.Shared.Rent(fnLen + 1);
                if (fnLen > 0)
                {
                    int fnWritten = Encoding.UTF8.GetBytes(sourceName, 0, sourceName.Length, filenameBytes, 0);
                    filenameBytes[fnWritten] = 0;
                }
                else
                {
                    filenameBytes[0] = 0;
                }

                Span<byte> errorBuf = stackalloc byte[2048];
                int status = UfbxNative.LoadMemory(
                    ref MemoryMarshal.GetReference(data),
                    data.Length,
                    ref filenameBytes[0],
                    ref nativeOpts,
                    out loaded,
                    ref MemoryMarshal.GetReference(errorBuf),
                    errorBuf.Length);

                if (status != UfbxNative.StatusOk || loaded == IntPtr.Zero)
                {
                    if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                    string errorMessage = DecodeUtf8(errorBuf, errorBuf.Length);
                    return FbxReadResult<UfbxScene>.Failed(
                        MapStatus(status),
                        string.IsNullOrWhiteSpace(errorMessage) ? "ufbx failed to load the FBX memory buffer." : errorMessage);
                }

                return WrapNativeScene(ref loaded, sourceName ?? string.Empty, resolvedOptions);
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
            catch (FbxReadException exception)
            {
                return FbxReadResult<UfbxScene>.Failed(exception.Status, exception.Message, exception.Diagnostics);
            }
            catch (Exception exception) when (exception is SEHException || exception is InvalidOperationException || exception is ArgumentException)
            {
                if (loaded != IntPtr.Zero) UfbxNative.Free(loaded);
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.ParseError, $"ufbx reader failed: {exception.Message}");
            }
            finally
            {
                if (filenameBytes != null)
                {
                    ArrayPool<byte>.Shared.Return(filenameBytes);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref handle, null);
            current?.Dispose();
        }

        /// <inheritdoc />
        public IFbxNode FindNodeByPath(string path)
        {
            RequireHandle();
            nodesByPath.TryGetValue(FbxNameUtility.NormalizePath(path), out var node);
            return node;
        }

        /// <inheritdoc />
        public IFbxMesh FindMeshByNodePath(string nodePath)
        {
            RequireHandle();
            meshesByNodePath.TryGetValue(FbxNameUtility.NormalizePath(nodePath), out var mesh);
            return mesh;
        }

        internal NativeSceneHandle RequireHandle()
        {
            var current = Volatile.Read(ref handle);
            if (current == null || current.IsClosed || current.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(UfbxScene));
            }

            return current;
        }

        private static FbxReadResult<UfbxScene> CheckAbiVersion()
        {
            int abiVersion;
            try
            {
                abiVersion = UfbxNative.GetAbiVersion();
            }
            catch (DllNotFoundException exception)
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.SectionUnavailable, exception.Message);
            }
            catch (EntryPointNotFoundException)
            {
                return FbxReadResult<UfbxScene>.Failed(
                    FbxReadStatus.SectionUnavailable,
                    "The loaded BlendShareUfbx native plugin uses an obsolete ABI. Restart Unity after updating the native plugin.");
            }

            if (abiVersion != UfbxNative.ExpectedAbiVersion)
            {
                return FbxReadResult<UfbxScene>.Failed(
                    FbxReadStatus.SectionUnavailable,
                    $"BlendShareUfbx ABI {abiVersion} is incompatible with the required ABI {UfbxNative.ExpectedAbiVersion}. " +
                    "Rebuild or update the native plugin and restart Unity.");
            }

            return null;
        }

        private static FbxReadResult<UfbxScene> WrapNativeScene(
            ref IntPtr loaded,
            string assetPath,
            UfbxLoadOptions options)
        {
            // Transfer ownership to SafeHandle immediately and clear the caller's raw pointer
            // so construction failures cannot double-free the native scene in caller catch blocks.
            var sceneHandle = new NativeSceneHandle(loaded);
            loaded = IntPtr.Zero;
            try
            {
                var scene = new UfbxScene(sceneHandle, assetPath, options);
                return FbxReadResult<UfbxScene>.Succeeded(scene);
            }
            catch
            {
                sceneHandle.Dispose();
                throw;
            }
        }

        private static UfbxNative.LoadOptionsNative BuildNativeOptions(UfbxLoadOptions options)
        {
            uint flags = UfbxNative.LoadFlagNone;
            if (options.IgnoreAnimation) flags |= UfbxNative.LoadFlagIgnoreAnimation;
            if (options.IgnoreEmbedded) flags |= UfbxNative.LoadFlagIgnoreEmbedded;
            if (options.GenerateMissingNormals) flags |= UfbxNative.LoadFlagGenerateMissingNormals;
            if (options.LoadExternalFiles) flags |= UfbxNative.LoadFlagLoadExternalFiles;
            if (options.RetainVertexAttribW) flags |= UfbxNative.LoadFlagRetainVertexAttribW;
            if (options.TargetAxes.HasValue)
            {
                if (!UfbxLoadOptions.IsValidCoordinateAxes(options.TargetAxes.Value))
                {
                    throw new ArgumentException("Target coordinate axes must specify three distinct orthogonal X, Y, and Z axes.", nameof(options));
                }

                flags |= UfbxNative.LoadFlagUseTargetAxes;
            }

            if (options.TargetUnitMeters.HasValue)
            {
                double unitMeters = options.TargetUnitMeters.Value;
                if (double.IsNaN(unitMeters) || double.IsInfinity(unitMeters) || !(unitMeters > 0d))
                {
                    throw new ArgumentOutOfRangeException(nameof(options), "Target unit meters must be a finite positive number.");
                }

                flags |= UfbxNative.LoadFlagUseTargetUnitMeters;
            }

            return new UfbxNative.LoadOptionsNative
            {
                StructSize = (uint)Marshal.SizeOf<UfbxNative.LoadOptionsNative>(),
                Flags = flags,
                TargetAxes = options.TargetAxes ?? new FbxCoordinateAxes(FbxCoordinateAxis.Unknown, FbxCoordinateAxis.Unknown, FbxCoordinateAxis.Unknown),
                Pad0 = 0,
                TargetUnitMeters = options.TargetUnitMeters ?? 0d
            };
        }

        private UfbxMaterial[] BuildMaterials(NativeSceneHandle sceneHandle, int materialCount)
        {
            int count = Math.Max(0, materialCount);
            var result = new UfbxMaterial[count];
            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetMaterialInfo(sceneHandle, i, out var info) != UfbxNative.StatusOk)
                {
                    throw new FbxReadException(
                        FbxReadStatus.ParseError,
                        $"The BlendShare ufbx native plugin could not read material {i}.");
                }

                string name = CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyMaterialName(sceneHandle, i, ref dst, size));
                result[i] = new UfbxMaterial(this, i, (long)info.Id, name);
            }

            return result;
        }

        private UfbxNode[] BuildNodes(NativeSceneHandle sceneHandle, int nodeCount, UfbxMaterial[] sceneMaterials)
        {
            int count = Math.Max(0, nodeCount);
            var nodes = new UfbxNode[count];
            var parentIndices = new int[count];
            var nonMeshAttribs = new Dictionary<long, (UfbxNodeAttribute Attr, List<IFbxNode> Instances)>();

            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetNodeInfo(sceneHandle, i, out var info) != UfbxNative.StatusOk)
                {
                    throw new FbxReadException(
                        FbxReadStatus.ParseError,
                        $"The BlendShare ufbx native plugin could not read node {i}.");
                }

                parentIndices[i] = info.ParentIndex;
                string name = CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyNodeName(sceneHandle, i, ref dst, size));
                string path = CopyString(
                    info.PathLength,
                    (ref byte dst, int size) => UfbxNative.CopyNodePath(sceneHandle, i, ref dst, size));

                var localTransform = new FbxTransform(info.LocalTranslation, info.LocalRotation, info.LocalScale);
                var geometryTransform = new FbxTransform(info.GeometryTranslation, info.GeometryRotation, info.GeometryScale);
                var rawTransform = new FbxNodeRawTransform(
                    info.LclTranslation,
                    info.LclRotation,
                    info.LclScale,
                    ToRotationOrder(info.RotationOrder, path),
                    ToInheritMode(info.InheritMode, path),
                    ToInheritMode(info.OriginalInheritMode, path),
                    info.RotationActive != 0,
                    info.PreRotation,
                    info.PostRotation,
                    info.RotationPivot,
                    info.ScalingPivot,
                    info.RotationOffset,
                    info.ScalingOffset);

                var attribKind = ToAttributeKind(info.AttribKind);
                var node = new UfbxNode(
                    this,
                    i,
                    (long)info.Id,
                    name,
                    path,
                    info.IsRoot != 0,
                    info.IsBone != 0,
                    attribKind,
                    info.MeshIndex,
                    localTransform,
                    geometryTransform,
                    info.NodeToParent.ToFbxMatrix(),
                    info.NodeToWorld.ToFbxMatrix(),
                    info.GeometryToNode.ToFbxMatrix(),
                    info.GeometryToWorld.ToFbxMatrix(),
                    rawTransform);

                if (info.MaterialCount > 0)
                {
                    var nodeMats = ReadMaterialSlots(
                        info.MaterialCount,
                        sceneMaterials,
                        (ref int dst, int len) => UfbxNative.CopyNodeMaterialIndices(sceneHandle, i, ref dst, len));
                    node.SetMaterials(nodeMats);
                }

                // Group shared non-mesh attributes by scene-local element_id so IFbxNode.Attribute.Instances is accurate.
                if (info.MeshIndex < 0 && info.AttribId != 0 && attribKind != FbxNodeAttributeKind.None)
                {
                    long attrId = (long)info.AttribId;
                    if (!nonMeshAttribs.TryGetValue(attrId, out var entry))
                    {
                        string attrName = CopyString(
                            info.AttribNameLength,
                            (ref byte dst, int size) => UfbxNative.CopyNodeAttribName(sceneHandle, i, ref dst, size));
                        entry = (new UfbxNodeAttribute(this, attrId, attrName, attribKind), new List<IFbxNode>());
                        nonMeshAttribs.Add(attrId, entry);
                    }

                    entry.Instances.Add(node);
                    node.SetAttribute(entry.Attr);
                }

                nodes[i] = node;
            }

            foreach (var entry in nonMeshAttribs.Values)
            {
                entry.Attr.SetInstances(entry.Instances);
            }

            var childrenByParent = new List<IFbxNode>[count];
            for (int i = 0; i < count; i++)
            {
                int parentIndex = parentIndices[i];
                if (parentIndex >= 0 && parentIndex < count)
                {
                    nodes[i].SetParent(nodes[parentIndex]);
                    childrenByParent[parentIndex] ??= new List<IFbxNode>();
                    childrenByParent[parentIndex].Add(nodes[i]);
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (childrenByParent[i] != null)
                {
                    nodes[i].SetChildren(childrenByParent[i]);
                }
            }

            return nodes;
        }

        private UfbxMesh[] BuildMeshes(
            NativeSceneHandle sceneHandle,
            int meshCount,
            UfbxNode[] sceneNodes,
            UfbxMaterial[] sceneMaterials,
            out bool hasAnySkins)
        {
            hasAnySkins = false;
            int count = Math.Max(0, meshCount);
            var meshes = new UfbxMesh[count];
            for (int i = 0; i < count; i++)
            {
                if (UfbxNative.GetMeshInfo(sceneHandle, i, out var info) != UfbxNative.StatusOk)
                {
                    throw new FbxReadException(
                        FbxReadStatus.ParseError,
                        $"The BlendShare ufbx native plugin could not read mesh {i}.");
                }

                if (info.SkinCount > 0)
                {
                    hasAnySkins = true;
                }

                string name = CopyString(
                    info.NameLength,
                    (ref byte dst, int size) => UfbxNative.CopyMeshName(sceneHandle, i, ref dst, size));
                var mesh = new UfbxMesh(this, i, name, info);

                if (info.InstanceCount > 0)
                {
                    int[] instIndices = ArrayPool<int>.Shared.Rent(info.InstanceCount);
                    try
                    {
                        int written = UfbxNative.CopyMeshInstanceNodeIndices(
                            sceneHandle,
                            i,
                            ref MemoryMarshal.GetReference(instIndices.AsSpan(0, info.InstanceCount)),
                            info.InstanceCount);
                        if (written > 0)
                        {
                            var instNodes = new List<IFbxNode>(written);
                            for (int k = 0; k < written; k++)
                            {
                                int ni = instIndices[k];
                                if (ni >= 0 && ni < sceneNodes.Length)
                                {
                                    instNodes.Add(sceneNodes[ni]);
                                }
                            }

                            mesh.SetInstances(instNodes);
                        }
                    }
                    finally
                    {
                        ArrayPool<int>.Shared.Return(instIndices);
                    }
                }

                if (info.MaterialCount > 0)
                {
                    var meshMats = ReadMaterialSlots(
                        info.MaterialCount,
                        sceneMaterials,
                        (ref int dst, int len) => UfbxNative.CopyMeshMaterialIndices(sceneHandle, i, ref dst, len));
                    mesh.SetMaterials(meshMats);
                }

                meshes[i] = mesh;
            }

            return meshes;
        }

        private delegate int CopyIndicesDelegate(ref int dst, int dstCount);

        private static List<IFbxMaterial> ReadMaterialSlots(int count, UfbxMaterial[] sceneMaterials, CopyIndicesDelegate copy)
        {
            var result = new List<IFbxMaterial>(count);
            int[] rented = ArrayPool<int>.Shared.Rent(count);
            try
            {
                int written = copy(ref MemoryMarshal.GetReference(rented.AsSpan(0, count)), count);
                for (int i = 0; i < written; i++)
                {
                    int mi = rented[i];
                    result.Add(mi >= 0 && mi < sceneMaterials.Length ? sceneMaterials[mi] : null);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }

            return result;
        }

        internal static string CopyString(int expectedLength, NativeCopyStringDelegate copy)
        {
            int capacity = Math.Max(1, expectedLength + 1);
            if (capacity <= 256)
            {
                Span<byte> stackBuf = stackalloc byte[256];
                int len = copy(ref MemoryMarshal.GetReference(stackBuf), stackBuf.Length);
                if (len <= 0)
                {
                    return string.Empty;
                }

                if (len < stackBuf.Length)
                {
                    return DecodeUtf8(stackBuf, len);
                }

                capacity = len + 1;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(capacity);
            try
            {
                int len = copy(ref MemoryMarshal.GetReference(rented.AsSpan(0, capacity)), capacity);
                if (len <= 0)
                {
                    return string.Empty;
                }

                if (len >= capacity)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    capacity = len + 1;
                    rented = ArrayPool<byte>.Shared.Rent(capacity);
                    len = copy(ref MemoryMarshal.GetReference(rented.AsSpan(0, capacity)), capacity);
                }

                return DecodeUtf8(rented.AsSpan(0, Math.Min(len, capacity)), len);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static string DecodeUtf8(ReadOnlySpan<byte> buffer, int byteCount)
        {
            if (buffer.IsEmpty || byteCount <= 0)
            {
                return string.Empty;
            }

            int length = Math.Min(byteCount, buffer.Length);
            int nullIndex = buffer.Slice(0, length).IndexOf((byte)0);
            if (nullIndex >= 0)
            {
                length = nullIndex;
            }

            if (length <= 0)
            {
                return string.Empty;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                buffer.Slice(0, length).CopyTo(rented);
                return Encoding.UTF8.GetString(rented, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static FbxReadStatus MapStatus(int nativeStatus)
        {
            return nativeStatus switch
            {
                UfbxNative.StatusOk => FbxReadStatus.Success,
                UfbxNative.StatusInvalidArg => FbxReadStatus.InvalidArgument,
                UfbxNative.StatusUnavailable => FbxReadStatus.SectionUnavailable,
                _ => FbxReadStatus.ParseError
            };
        }

        private static FbxCoordinateAxis ToCoordinateAxis(int value)
        {
            return value >= (int)FbxCoordinateAxis.PositiveX && value <= (int)FbxCoordinateAxis.Unknown
                ? (FbxCoordinateAxis)value
                : FbxCoordinateAxis.Unknown;
        }

        private static FbxCoordinateAxes SanitizeAxes(FbxCoordinateAxes axes)
        {
            return new FbxCoordinateAxes(
                ToCoordinateAxis((int)axes.Right),
                ToCoordinateAxis((int)axes.Up),
                ToCoordinateAxis((int)axes.Front));
        }

        private static FbxNodeAttributeKind ToAttributeKind(int value)
        {
            return value >= (int)FbxNodeAttributeKind.None && value <= (int)FbxNodeAttributeKind.Other
                ? (FbxNodeAttributeKind)value
                : FbxNodeAttributeKind.None;
        }

        private static FbxRotationOrder ToRotationOrder(int value, string nodePath)
        {
            if (value >= (int)FbxRotationOrder.XYZ && value <= (int)FbxRotationOrder.SphericXYZ)
            {
                return (FbxRotationOrder)value;
            }

            throw new FbxReadException(
                FbxReadStatus.ParseError,
                $"FBX node '{nodePath}' uses unsupported rotation order value {value}.");
        }

        private static FbxTransformInheritMode ToInheritMode(int value, string nodePath)
        {
            return value switch
            {
                0 => FbxTransformInheritMode.RSrs,
                1 => FbxTransformInheritMode.Rrs,
                2 => FbxTransformInheritMode.RrSs,
                _ => throw new FbxReadException(
                    FbxReadStatus.ParseError,
                    $"FBX node '{nodePath}' uses unsupported inheritance mode value {value}.")
            };
        }
    }
}
