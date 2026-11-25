using System;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Allocates arrays for callers of the span-based FBX mesh readers.</summary>
    public static class FbxMeshExtensions
    {
        /// <summary>Gets the first UV set in file order when one exists.</summary>
        public static bool TryGetPrimaryUvSet(this IFbxMesh mesh, out IFbxUvSet uvSet)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var sets = mesh.UvSets;
            uvSet = sets.Count > 0 ? sets[0] : null;
            return uvSet != null;
        }

        /// <summary>Reads control points into an array.</summary>
        public static Vector3d[] GetControlPoints(this IFbxMeshGeometry mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<Vector3d>(mesh.ControlPointCount, mesh.TryCopyControlPoints);
        }

        /// <summary>Reads face sizes into an array.</summary>
        public static int[] GetFaceSizes(this IFbxMeshTopology mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<int>(mesh.FaceCount, mesh.TryCopyFaceSizes);
        }

        /// <summary>Reads face corner control points into an array.</summary>
        public static int[] GetFaceCornerControlPoints(this IFbxMeshTopology mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<int>(mesh.FaceCornerCount, mesh.TryCopyFaceCornerControlPoints);
        }

        /// <summary>Reads face index begins into an array.</summary>
        public static int[] GetFaceIndexBegins(this IFbxMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<int>(mesh.FaceCount, mesh.TryCopyFaceIndexBegins);
        }

        /// <summary>Reads face smoothing groups into an array. Returns an empty array when unavailable.</summary>
        public static uint[] GetFaceSmoothingGroups(this IFbxMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (!mesh.HasFaceSmoothingGroups) return Array.Empty<uint>();
            return ReadArray<uint>(mesh.FaceCount, mesh.TryCopyFaceSmoothingGroups);
        }

        /// <summary>Reads face materials into an array. Returns an empty array when unavailable.</summary>
        public static int[] GetFaceMaterials(this IFbxMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (!mesh.HasFaceMaterials) return Array.Empty<int>();
            return ReadArray<int>(mesh.FaceCount, mesh.TryCopyFaceMaterials);
        }

        /// <summary>Reads material part usage order into an array.</summary>
        public static int[] GetMaterialPartUsageOrder(this IFbxMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<int>(mesh.MaterialPartUsageCount, mesh.TryCopyMaterialPartUsageOrder);
        }

        /// <summary>Reads first corner normals into an array. Returns an empty array when unavailable.</summary>
        public static Vector3d[] GetFirstCornerNormals(this IFbxMeshGeometry mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<Vector3d>(mesh.ControlPointCount, mesh.TryCopyFirstCornerNormals, optional: true);
        }

        /// <summary>Reads first corner tangents into an array. Returns an empty array when unavailable.</summary>
        public static Vector3d[] GetFirstCornerTangents(this IFbxMeshGeometry mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            return ReadArray<Vector3d>(mesh.ControlPointCount, mesh.TryCopyFirstCornerTangents, optional: true);
        }

        /// <summary>Reads values into an array. Returns an empty array when unavailable.</summary>
        public static T[] GetValues<T>(this IFbxIndexedAttribute<T> attribute) where T : unmanaged
        {
            if (attribute == null) throw new ArgumentNullException(nameof(attribute));
            if (!attribute.Exists) return Array.Empty<T>();
            return ReadArray<T>(attribute.ValueCount, attribute.TryCopyValues);
        }

        /// <summary>Reads corner indices into an array. Returns an empty array when unavailable.</summary>
        public static int[] GetCornerIndices<T>(this IFbxIndexedAttribute<T> attribute) where T : unmanaged
        {
            if (attribute == null) throw new ArgumentNullException(nameof(attribute));
            if (!attribute.Exists) return Array.Empty<int>();
            return ReadArray<int>(attribute.CornerCount, attribute.TryCopyCornerIndices);
        }

        /// <summary>Reads per corner values into an array. Returns an empty array when unavailable.</summary>
        public static T[] GetPerCornerValues<T>(this IFbxIndexedAttribute<T> attribute) where T : unmanaged
        {
            if (attribute == null) throw new ArgumentNullException(nameof(attribute));
            if (!attribute.Exists) return Array.Empty<T>();
            return ReadArray<T>(attribute.CornerCount, attribute.TryCopyPerCornerValues);
        }

        /// <summary>Reads values w into an array. Returns an empty array when unavailable.</summary>
        public static double[] GetValuesW<T>(this IFbxIndexedAttribute<T> attribute) where T : unmanaged
        {
            if (attribute == null) throw new ArgumentNullException(nameof(attribute));
            if (!attribute.Exists) return Array.Empty<double>();
            return ReadArray<double>(attribute.ComponentWCount, attribute.TryCopyValuesW);
        }

        /// <summary>Reads face indices into an array.</summary>
        public static int[] GetFaceIndices(this IFbxMeshPart part)
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            return ReadArray<int>(part.FaceCount, part.TryCopyFaceIndices);
        }

        /// <summary>Reads control point indices into an array.</summary>
        public static int[] GetControlPointIndices(this IFbxSkinCluster cluster)
        {
            if (cluster == null) throw new ArgumentNullException(nameof(cluster));
            return ReadArray<int>(cluster.WeightCount, cluster.TryCopyControlPointIndices);
        }

        /// <summary>Reads weights into an array.</summary>
        public static double[] GetWeights(this IFbxSkinCluster cluster)
        {
            if (cluster == null) throw new ArgumentNullException(nameof(cluster));
            return ReadArray<double>(cluster.WeightCount, cluster.TryCopyWeights);
        }

        private delegate bool CopyTo<T>(Span<T> destination, out int written);

        private static T[] ReadArray<T>(int count, CopyTo<T> copy, bool optional = false)
        {
            if (count == 0) return Array.Empty<T>();
            var result = new T[count];
            if (!copy(result, out int written))
            {
                if (optional) return Array.Empty<T>();
                throw new InvalidOperationException("Unable to copy the requested FBX data.");
            }

            if (written != count) throw new InvalidOperationException("Incomplete FBX data.");
            return result;
        }
    }
}
