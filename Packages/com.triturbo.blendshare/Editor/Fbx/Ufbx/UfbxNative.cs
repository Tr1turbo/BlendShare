using System;
using System.Runtime.InteropServices;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    internal sealed class NativeSceneHandle : SafeHandle
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

    internal static class UfbxNative
    {
        private const string LibraryName = "BlendShareUfbx";
        internal const int ExpectedAbiVersion = 4;

        internal const int StatusOk = 0;
        internal const int StatusInvalidArg = -1;
        internal const int StatusOutOfRange = -2;
        internal const int StatusBufferTooSmall = -3;
        internal const int StatusUnavailable = -4;
        internal const int StatusParseFailed = -5;
        internal const int StatusOutOfMemory = -6;

        internal const uint LoadFlagNone = 0u;
        internal const uint LoadFlagIgnoreAnimation = 1u << 0;
        internal const uint LoadFlagIgnoreEmbedded = 1u << 1;
        internal const uint LoadFlagGenerateMissingNormals = 1u << 2;
        internal const uint LoadFlagLoadExternalFiles = 1u << 3;
        internal const uint LoadFlagRetainVertexAttribW = 1u << 4;
        internal const uint LoadFlagUseTargetAxes = 1u << 5;
        internal const uint LoadFlagUseTargetUnitMeters = 1u << 6;

        internal const int AttribKindNormal = 0;
        internal const int AttribKindTangent = 1;
        internal const int AttribKindBitangent = 2;
        internal const int AttribKindUv = 3;
        internal const int AttribKindColor = 4;
        internal const int AttribKindVertexCrease = 5;

        internal const uint BlendOutIndices = 1u << 0;
        internal const uint BlendOutPositions = 1u << 1;
        internal const uint BlendOutNormals = 1u << 2;
        internal const uint BlendOutWeights = 1u << 3;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMatrix
        {
            public double M00, M01, M02, M03;
            public double M10, M11, M12, M13;
            public double M20, M21, M22, M23;
            public double M30, M31, M32, M33;

            public FbxMatrix4x4 ToFbxMatrix()
            {
                return new FbxMatrix4x4(
                    M00, M01, M02, M03,
                    M10, M11, M12, M13,
                    M20, M21, M22, M23,
                    M30, M31, M32, M33);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LoadOptionsNative
        {
            public uint StructSize;
            public uint Flags;
            public FbxCoordinateAxes TargetAxes;
            public int Pad0;
            public double TargetUnitMeters;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SceneInfo
        {
            public uint FileVersion;
            public int OriginalUpAxis;
            public double UnitMeters;
            public double OriginalUnitMeters;
            public double FramesPerSecond;
            public FbxCoordinateAxes Axes;
            public int NodeCount;
            public int MeshCount;
            public int MaterialCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NodeInfo
        {
            public ulong Id;
            public ulong AttribId;
            public int ParentIndex;
            public int IsRoot;
            public int IsBone;
            public int AttribKind;
            public int MeshIndex;
            public int MaterialCount;
            public int RotationOrder;
            public int OriginalInheritMode;
            public int InheritMode;
            public int RotationActive;
            public int NameLength;
            public int PathLength;
            public int AttribNameLength;
            public int Pad0;

            public Vector3d LclTranslation;
            public Vector3d LclRotation;
            public Vector3d LclScale;
            public Vector3d PreRotation;
            public Vector3d PostRotation;
            public Vector3d RotationPivot;
            public Vector3d ScalingPivot;
            public Vector3d RotationOffset;
            public Vector3d ScalingOffset;

            public Vector3d LocalTranslation;
            public Quaterniond LocalRotation;
            public Vector3d LocalScale;
            public Vector3d GeometryTranslation;
            public Quaterniond GeometryRotation;
            public Vector3d GeometryScale;
            public NativeMatrix NodeToParent;
            public NativeMatrix NodeToWorld;
            public NativeMatrix GeometryToNode;
            public NativeMatrix GeometryToWorld;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MaterialInfo
        {
            public ulong Id;
            public int NameLength;
            public int Pad0;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshInfo
        {
            public ulong Id;
            public int NameLength;
            public int InstanceCount;
            public int ControlPointCount;
            public int FaceCount;
            public int FaceCornerCount;
            public int TriangleCount;
            public int MaxFaceTriangles;
            public int EdgeCount;
            public int UvSetCount;
            public int ColorSetCount;
            public int MaterialCount;
            public int MaterialPartCount;
            public int MaterialPartUsageCount;
            public int FaceGroupCount;
            public int SkinCount;
            public int BlendDeformerCount;
            public int HasFaceSmoothing;
            public int HasFaceSmoothingGroup;
            public int HasFaceMaterial;
            public int HasFaceGroup;
            public int HasFaceHole;
            public int HasEdgeSmoothing;
            public int HasEdgeCrease;
            public int HasEdgeVisibility;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshPartInfo
        {
            public int PartIndex;
            public int FaceCount;
            public int TriangleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LayerSetInfo
        {
            public int SetIndex;
            public int NameLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AttribInfo
        {
            public int Exists;
            public int UniquePerVertex;
            public int ValueCount;
            public int CornerCount;
            public int ValuesWCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SkinInfo
        {
            public ulong Id;
            public int ClusterCount;
            public int NameLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClusterInfo
        {
            public ulong Id;
            public int BoneNodeIndex;
            public int WeightCount;
            public int NameLength;
            public int LinkMode;
            public NativeMatrix MeshBindWorld;
            public NativeMatrix BoneBindWorld;
            public NativeMatrix MeshNodeToBone;
            public NativeMatrix GeometryToBone;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendDeformerInfo
        {
            public ulong Id;
            public int ChannelCount;
            public int NameLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendChannelInfo
        {
            public ulong Id;
            public int KeyframeCount;
            public int NameLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendKeyframeInfo
        {
            public ulong ShapeId;
            public double TargetWeight;
            public double EffectiveWeight;
            public int OffsetCount;
            public int HasNormalOffsets;
            public int HasOffsetWeights;
            public int ShapeNameLength;
        }

        /* Scene lifecycle & metadata */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_abi_version")]
        internal static extern int GetAbiVersion();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_load_file")]
        internal static extern int LoadFile(ref byte path, ref LoadOptionsNative opts, out IntPtr scene, ref byte error, int errorSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_load_memory")]
        internal static extern int LoadMemory(ref byte data, long dataSize, ref byte filename, ref LoadOptionsNative opts, out IntPtr scene, ref byte error, int errorSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_free")]
        internal static extern void Free(IntPtr scene);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_scene_info")]
        internal static extern int GetSceneInfo(NativeSceneHandle scene, out SceneInfo info);

        /* Nodes & materials */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_node_info")]
        internal static extern int GetNodeInfo(NativeSceneHandle scene, int nodeIndex, out NodeInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_name")]
        internal static extern int CopyNodeName(NativeSceneHandle scene, int nodeIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_path")]
        internal static extern int CopyNodePath(NativeSceneHandle scene, int nodeIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_attrib_name")]
        internal static extern int CopyNodeAttribName(NativeSceneHandle scene, int nodeIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_material_indices")]
        internal static extern int CopyNodeMaterialIndices(NativeSceneHandle scene, int nodeIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_material_info")]
        internal static extern int GetMaterialInfo(NativeSceneHandle scene, int materialIndex, out MaterialInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_material_name")]
        internal static extern int CopyMaterialName(NativeSceneHandle scene, int materialIndex, ref byte dst, int dstSize);

        /* Mesh metadata, topology, faces, edges & material parts */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_mesh_info")]
        internal static extern int GetMeshInfo(NativeSceneHandle scene, int meshIndex, out MeshInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_mesh_name")]
        internal static extern int CopyMeshName(NativeSceneHandle scene, int meshIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_mesh_instance_node_indices")]
        internal static extern int CopyMeshInstanceNodeIndices(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_mesh_material_indices")]
        internal static extern int CopyMeshMaterialIndices(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_control_points")]
        internal static extern int CopyControlPoints(NativeSceneHandle scene, int meshIndex, ref Vector3d dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_index_begins")]
        internal static extern int CopyFaceIndexBegins(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_sizes")]
        internal static extern int CopyFaceSizes(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_corner_control_points")]
        internal static extern int CopyFaceCornerControlPoints(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_vertex_first_corners")]
        internal static extern int CopyVertexFirstCorners(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_smoothing")]
        internal static extern int CopyFaceSmoothing(NativeSceneHandle scene, int meshIndex, ref byte dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_smoothing_groups")]
        internal static extern int CopyFaceSmoothingGroups(NativeSceneHandle scene, int meshIndex, ref uint dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_materials")]
        internal static extern int CopyFaceMaterials(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_groups")]
        internal static extern int CopyFaceGroups(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_holes")]
        internal static extern int CopyFaceHoles(NativeSceneHandle scene, int meshIndex, ref byte dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_edges")]
        internal static extern int CopyEdges(NativeSceneHandle scene, int meshIndex, ref FbxEdge dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_edge_smoothing")]
        internal static extern int CopyEdgeSmoothing(NativeSceneHandle scene, int meshIndex, ref byte dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_edge_creases")]
        internal static extern int CopyEdgeCreases(NativeSceneHandle scene, int meshIndex, ref double dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_edge_visibility")]
        internal static extern int CopyEdgeVisibility(NativeSceneHandle scene, int meshIndex, ref byte dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_material_part_info")]
        internal static extern int GetMaterialPartInfo(NativeSceneHandle scene, int meshIndex, int partIndex, out MeshPartInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_material_part_faces")]
        internal static extern int CopyMaterialPartFaces(NativeSceneHandle scene, int meshIndex, int partIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_material_part_usage_order")]
        internal static extern int CopyMaterialPartUsageOrder(NativeSceneHandle scene, int meshIndex, ref int dst, int dstCount);

        /* UV / Color sets & unified indexed vertex attributes */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_uv_set_info")]
        internal static extern int GetUvSetInfo(NativeSceneHandle scene, int meshIndex, int setIndex, out LayerSetInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_uv_set_name")]
        internal static extern int CopyUvSetName(NativeSceneHandle scene, int meshIndex, int setIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_color_set_info")]
        internal static extern int GetColorSetInfo(NativeSceneHandle scene, int meshIndex, int setIndex, out LayerSetInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_color_set_name")]
        internal static extern int CopyColorSetName(NativeSceneHandle scene, int meshIndex, int setIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_attrib_info")]
        internal static extern int GetAttribInfo(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, out AttribInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_indices")]
        internal static extern int CopyAttribIndices(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_values_w")]
        internal static extern int CopyAttribValuesW(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref double dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_values_real")]
        internal static extern int CopyAttribValuesReal(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref double dst, int dstCount, int expandPerCorner);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_values_vec2")]
        internal static extern int CopyAttribValuesVec2(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref Vector2d dst, int dstCount, int expandPerCorner);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_values_vec3")]
        internal static extern int CopyAttribValuesVec3(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref Vector3d dst, int dstCount, int expandPerCorner);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_attrib_values_vec4")]
        internal static extern int CopyAttribValuesVec4(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref Vector4d dst, int dstCount, int expandPerCorner);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_control_point_first_corner_attrib_vec3")]
        internal static extern int CopyControlPointFirstCornerAttribVec3(NativeSceneHandle scene, int meshIndex, int attribKind, int setIndex, ref Vector3d dst, int dstCount);

        /* Skin deformers & clusters */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_skin_info")]
        internal static extern int GetSkinInfo(NativeSceneHandle scene, int meshIndex, int skinIndex, out SkinInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_skin_name")]
        internal static extern int CopySkinName(NativeSceneHandle scene, int meshIndex, int skinIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_skin_cluster_info")]
        internal static extern int GetSkinClusterInfo(NativeSceneHandle scene, int meshIndex, int skinIndex, int clusterIndex, out ClusterInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_name")]
        internal static extern int CopyClusterName(NativeSceneHandle scene, int meshIndex, int skinIndex, int clusterIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_indices")]
        internal static extern int CopyClusterIndices(NativeSceneHandle scene, int meshIndex, int skinIndex, int clusterIndex, ref int dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_weights")]
        internal static extern int CopyClusterWeights(NativeSceneHandle scene, int meshIndex, int skinIndex, int clusterIndex, ref double dst, int dstCount);

        /* Blend deformers, channels & keyframes */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_deformer_info")]
        internal static extern int GetBlendDeformerInfo(NativeSceneHandle scene, int meshIndex, int deformerIndex, out BlendDeformerInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_deformer_name")]
        internal static extern int CopyBlendDeformerName(NativeSceneHandle scene, int meshIndex, int deformerIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_channel_info")]
        internal static extern int GetBlendChannelInfo(NativeSceneHandle scene, int meshIndex, int deformerIndex, int channelIndex, out BlendChannelInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_channel_name")]
        internal static extern int CopyBlendChannelName(NativeSceneHandle scene, int meshIndex, int deformerIndex, int channelIndex, ref byte dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_keyframe_info")]
        internal static extern int GetBlendKeyframeInfo(NativeSceneHandle scene, int meshIndex, int deformerIndex, int channelIndex, int keyframeIndex, out BlendKeyframeInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_shape_name")]
        internal static extern int CopyBlendShapeName(NativeSceneHandle scene, int meshIndex, int deformerIndex, int channelIndex, int keyframeIndex, ref byte dst, int dstSize);

        /* Separate concrete overloads so omitted optional spans always pass native NULL (IntPtr.Zero) or gated output_mask. */

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_shape_offsets")]
        internal static extern int CopyBlendShapeOffsets(
            NativeSceneHandle scene,
            int meshIndex,
            int deformerIndex,
            int channelIndex,
            int keyframeIndex,
            uint outputMask,
            ref int dstControlPointIndices,
            ref Vector3d dstPositionOffsets,
            ref Vector3d dstNormalOffsets,
            ref double dstOffsetWeights,
            int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_shape_offsets")]
        internal static extern int CopyBlendShapeOffsetsIndicesAndPositions(
            NativeSceneHandle scene,
            int meshIndex,
            int deformerIndex,
            int channelIndex,
            int keyframeIndex,
            uint outputMask,
            ref int dstControlPointIndices,
            ref Vector3d dstPositionOffsets,
            IntPtr dstNormalOffsets,
            IntPtr dstOffsetWeights,
            int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_shape_offsets")]
        internal static extern int CopyBlendShapeOffsetsIndicesPositionsNormals(
            NativeSceneHandle scene,
            int meshIndex,
            int deformerIndex,
            int channelIndex,
            int keyframeIndex,
            uint outputMask,
            ref int dstControlPointIndices,
            ref Vector3d dstPositionOffsets,
            ref Vector3d dstNormalOffsets,
            IntPtr dstOffsetWeights,
            int dstCount);
    }
}
