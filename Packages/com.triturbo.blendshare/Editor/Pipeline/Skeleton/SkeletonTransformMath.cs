using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Shared row-vector / Unity column-vector affine math for skeleton alignment and bindpose conversion.
    /// </summary>
    public static class SkeletonTransformMath
    {
        /// <summary>
        /// Computes the row-vector geometry-to-bone bindpose matrix <c>T_geom * L_bone^-1</c>.
        /// </summary>
        public static bool ComputeCanonicalBindPose(
            FbxMatrix4x4 transformMatrix,
            FbxMatrix4x4 transformLinkMatrix,
            out FbxMatrix4x4 canonicalBindPose)
        {
            canonicalBindPose = default;
            if (!FbxAffineSpaceTransform.TryCreate(transformLinkMatrix, out var link) ||
                !FbxAffineSpaceTransform.TryCreate(transformMatrix, out _))
            {
                return false;
            }

            canonicalBindPose = transformMatrix * link.InverseOffset;
            return FbxAffineSpaceTransform.TryCreate(canonicalBindPose, out _);
        }

        /// <summary>
        /// Conjugates a canonical row-vector bindpose into Unity column-vector <see cref="Matrix4x4"/>.
        /// </summary>
        public static bool ConvertCanonicalBindPoseToUnity(
            FbxMatrix4x4 canonicalBindPose,
            FbxMatrix4x4 canonicalToUnityMeshLocal,
            out Matrix4x4 unityBindPose) =>
            ConvertCanonicalMatrixToUnity(canonicalBindPose, canonicalToUnityMeshLocal, out unityBindPose);

        /// <summary>
        /// Converts a stored explicit cluster bindpose (<c>T_{geom,s} * L_s^-1</c>) into a Unity <c>Mesh.bindposes</c> matrix,
        /// using <paramref name="storedToUnityMeshLocal"/> (<c>M_mesh</c>) on the vertex side and post-edit endpoint
        /// calibration <paramref name="endpointD"/> (<c>D_frame(b)</c>) on the bone-local side:
        /// <c>P_unity = (M_mesh^-1 * T_{geom,s} * L_s^-1 * D_frame(b))^T</c>.
        /// </summary>
        public static bool ConvertCalibratedBindPoseToUnity(
            FbxMatrix4x4 storedGeomBindWorld,
            FbxMatrix4x4 storedBoneBindWorld,
            FbxMatrix4x4 storedToUnityMeshLocal,
            FbxMatrix4x4 endpointD,
            out Matrix4x4 unityBindPose)
        {
            unityBindPose = default;
            if (!ComputeCanonicalBindPose(storedGeomBindWorld, storedBoneBindWorld, out var geomToBoneStored) ||
                !FbxAffineSpaceTransform.TryCreate(storedToUnityMeshLocal, out var meshSpace) ||
                !FbxAffineSpaceTransform.TryCreate(endpointD, out _))
            {
                return false;
            }

            unityBindPose = (meshSpace.InverseOffset * geomToBoneStored * endpointD).ToUnityColumnMatrix();
            return IsFinite(unityBindPose);
        }

        /// <summary>
        /// Conjugates a row-vector matrix by <paramref name="canonicalToUnityMeshLocal"/> and converts to Unity column-vector form.
        /// </summary>
        public static bool ConvertCanonicalMatrixToUnity(
            FbxMatrix4x4 canonicalMatrix,
            FbxMatrix4x4 canonicalToUnityMeshLocal,
            out Matrix4x4 unityMatrix)
        {
            unityMatrix = default;
            if (!FbxAffineSpaceTransform.TryCreate(canonicalToUnityMeshLocal, out var space))
            {
                return false;
            }

            unityMatrix = (space.InverseOffset * canonicalMatrix * space.Offset).ToUnityColumnMatrix();
            return IsFinite(unityMatrix);
        }

        /// <summary>
        /// Converts a Unity column-vector <see cref="Matrix4x4"/> into an exact row-vector <see cref="FbxMatrix4x4"/> (<c>M = U^T</c>).
        /// </summary>
        public static FbxMatrix4x4 FromUnityColumnMatrix(Matrix4x4 unityMatrix)
        {
            return new FbxMatrix4x4(
                unityMatrix.m00, unityMatrix.m10, unityMatrix.m20, unityMatrix.m30,
                unityMatrix.m01, unityMatrix.m11, unityMatrix.m21, unityMatrix.m31,
                unityMatrix.m02, unityMatrix.m12, unityMatrix.m22, unityMatrix.m32,
                unityMatrix.m03, unityMatrix.m13, unityMatrix.m23, unityMatrix.m33);
        }

        /// <summary>
        /// Decomposes a Unity column-vector local matrix into <c>(localPosition, localRotation, localScale)</c>,
        /// rejecting perspective, singular, non-finite, or sheared matrices.
        /// </summary>
        public static bool TryDecomposeUnityLocalTransform(
            Matrix4x4 localMatrix,
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 scale,
            out string diagnostic)
        {
            position = localMatrix.GetColumn(3);
            rotation = Quaternion.identity;
            scale = default;
            diagnostic = "Non-finite or perspective local transform.";
            if (!IsFinite(localMatrix) ||
                Mathf.Abs(localMatrix.m30) > 1e-5f ||
                Mathf.Abs(localMatrix.m31) > 1e-5f ||
                Mathf.Abs(localMatrix.m32) > 1e-5f ||
                Mathf.Abs(localMatrix.m33 - 1f) > 1e-5f)
            {
                return false;
            }

            Vector3 x = localMatrix.GetColumn(0);
            Vector3 y = localMatrix.GetColumn(1);
            Vector3 z = localMatrix.GetColumn(2);
            scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            diagnostic = "Singular or unrepresentable local scale.";
            if (scale.x == 0f || scale.y == 0f || scale.z == 0f ||
                float.IsInfinity(scale.x) || float.IsInfinity(scale.y) || float.IsInfinity(scale.z))
            {
                return false;
            }

            x /= scale.x;
            y /= scale.y;
            z /= scale.z;
            diagnostic = "Sheared local transform cannot be represented by Unity TRS.";
            if (Mathf.Abs(Vector3.Dot(x, y)) > 1e-4f ||
                Mathf.Abs(Vector3.Dot(x, z)) > 1e-4f ||
                Mathf.Abs(Vector3.Dot(y, z)) > 1e-4f)
            {
                return false;
            }

            if (Vector3.Dot(x, Vector3.Cross(y, z)) < 0f)
            {
                scale.x = -scale.x;
            }

            rotation = Quaternion.LookRotation(z, y);
            diagnostic = null;
            return true;
        }

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; i++)
            {
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
