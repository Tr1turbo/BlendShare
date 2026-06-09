using System;
using System.Runtime.InteropServices;
using System.Text;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    internal static class UfbxNative
    {
        private const string LibraryName = "BlendShareUfbx";

        [StructLayout(LayoutKind.Sequential)]
        internal struct Matrix
        {
            public double M00;
            public double M01;
            public double M02;
            public double M03;
            public double M10;
            public double M11;
            public double M12;
            public double M13;
            public double M20;
            public double M21;
            public double M22;
            public double M23;
            public double M30;
            public double M31;
            public double M32;
            public double M33;

            public double[] ToRowMajorArray()
            {
                return new[]
                {
                    M00, M01, M02, M03,
                    M10, M11, M12, M13,
                    M20, M21, M22, M23,
                    M30, M31, M32, M33
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NodeInfo
        {
            public ulong Id;
            public int ParentIndex;
            public int Type;
            public double LclTranslationX;
            public double LclTranslationY;
            public double LclTranslationZ;
            public double LclRotationX;
            public double LclRotationY;
            public double LclRotationZ;
            public double LclScaleX;
            public double LclScaleY;
            public double LclScaleZ;
            public int NameLength;
            public int PathLength;
            public double EulerRotationX;
            public double EulerRotationY;
            public double EulerRotationZ;
            public double PreRotationX;
            public double PreRotationY;
            public double PreRotationZ;
            public double PostRotationX;
            public double PostRotationY;
            public double PostRotationZ;
            public double UfbxLocalTranslationX;
            public double UfbxLocalTranslationY;
            public double UfbxLocalTranslationZ;
            public double UfbxLocalRotationX;
            public double UfbxLocalRotationY;
            public double UfbxLocalRotationZ;
            public double UfbxLocalRotationW;
            public double UfbxLocalScaleX;
            public double UfbxLocalScaleY;
            public double UfbxLocalScaleZ;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MeshInfo
        {
            public ulong Id;
            public int NodeIndex;
            public int ControlPointCount;
            public int SkinCount;
            public int BlendDeformerCount;
            public int NameLength;
            public int FaceCount;
            public int FaceIndexCount;
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
            public Matrix MeshBindWorld;
            public Matrix BoneBindWorld;
            public Matrix MeshNodeToBone;
            public Matrix GeometryToBone;
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
            public int FrameCount;
            public int NameLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendFrameInfo
        {
            public ulong Id;
            public double Weight;
            public int OffsetCount;
            public int NameLength;
        }

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_load")]
        internal static extern int Load(string path, out IntPtr scene, StringBuilder error, int errorSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_free")]
        internal static extern void Free(IntPtr scene);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_node_count")]
        internal static extern int GetNodeCount(IntPtr scene);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_node_info")]
        internal static extern int GetNodeInfo(IntPtr scene, int nodeIndex, out NodeInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_name")]
        internal static extern int CopyNodeName(IntPtr scene, int nodeIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_node_path")]
        internal static extern int CopyNodePath(IntPtr scene, int nodeIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_mesh_count")]
        internal static extern int GetMeshCount(IntPtr scene);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_mesh_info")]
        internal static extern int GetMeshInfo(IntPtr scene, int meshIndex, out MeshInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_mesh_name")]
        internal static extern int CopyMeshName(IntPtr scene, int meshIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_control_points")]
        internal static extern int CopyControlPoints(IntPtr scene, int meshIndex, [Out] double[] dst, int dstVertexCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_control_point_normals")]
        internal static extern int CopyControlPointNormals(IntPtr scene, int meshIndex, [Out] double[] dst, int dstVertexCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_control_point_tangents")]
        internal static extern int CopyControlPointTangents(IntPtr scene, int meshIndex, [Out] double[] dst, int dstVertexCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_sizes")]
        internal static extern int CopyFaceSizes(IntPtr scene, int meshIndex, [Out] int[] dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_face_vertex_indices")]
        internal static extern int CopyFaceVertexIndices(IntPtr scene, int meshIndex, [Out] int[] dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_skin_info")]
        internal static extern int GetSkinInfo(IntPtr scene, int meshIndex, int skinIndex, out SkinInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_skin_name")]
        internal static extern int CopySkinName(IntPtr scene, int meshIndex, int skinIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_skin_cluster_info")]
        internal static extern int GetSkinClusterInfo(IntPtr scene, int meshIndex, int skinIndex, int clusterIndex, out ClusterInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_name")]
        internal static extern int CopyClusterName(IntPtr scene, int meshIndex, int skinIndex, int clusterIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_indices")]
        internal static extern int CopyClusterIndices(IntPtr scene, int meshIndex, int skinIndex, int clusterIndex, [Out] int[] dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_cluster_weights")]
        internal static extern int CopyClusterWeights(IntPtr scene, int meshIndex, int skinIndex, int clusterIndex, [Out] double[] dst, int dstCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_deformer_info")]
        internal static extern int GetBlendDeformerInfo(IntPtr scene, int meshIndex, int deformerIndex, out BlendDeformerInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_deformer_name")]
        internal static extern int CopyBlendDeformerName(IntPtr scene, int meshIndex, int deformerIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_channel_info")]
        internal static extern int GetBlendChannelInfo(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, out BlendChannelInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_channel_name")]
        internal static extern int CopyBlendChannelName(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_get_blend_frame_info")]
        internal static extern int GetBlendFrameInfo(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, int frameIndex, out BlendFrameInfo info);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_frame_name")]
        internal static extern int CopyBlendFrameName(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, int frameIndex, StringBuilder dst, int dstSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bs_ufbx_copy_blend_frame_offsets")]
        internal static extern int CopyBlendFrameOffsets(IntPtr scene, int meshIndex, int deformerIndex, int channelIndex, int frameIndex, [Out] int[] dstIndices, [Out] double[] dstPosition, [Out] double[] dstNormal, int dstCount);
    }
}
