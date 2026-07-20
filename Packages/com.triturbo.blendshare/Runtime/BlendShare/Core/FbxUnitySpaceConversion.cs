using System;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>Represents an absolute local transform in Unity space.</summary>
    [Serializable]
    public struct UnityLocalTransform
    {
        public Vector3 m_Position;
        public Quaternion m_Rotation;
        public Vector3 m_Scale;

        /// <summary>Gets an identity Unity local transform.</summary>
        public static UnityLocalTransform Identity => new UnityLocalTransform(
            Vector3.zero,
            Quaternion.identity,
            Vector3.one);

        /// <summary>Creates an absolute Unity local transform.</summary>
        public UnityLocalTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            m_Position = position;
            m_Rotation = rotation;
            m_Scale = scale;
        }

        /// <summary>Gets the local position.</summary>
        public Vector3 Position => m_Position;

        /// <summary>Gets the local rotation.</summary>
        public Quaternion Rotation => m_Rotation;

        /// <summary>Gets the local scale.</summary>
        public Vector3 Scale => m_Scale;

        /// <summary>Builds a Unity matrix from this transform.</summary>
        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.TRS(m_Position, m_Rotation, m_Scale);
        }

        /// <summary>Replaces a Unity Transform's absolute local values.</summary>
        public void ApplyTo(Transform target)
        {
            if (target == null)
            {
                return;
            }

            target.localPosition = m_Position;
            target.localRotation = m_Rotation;
            target.localScale = m_Scale;
        }
    }

    /// <summary>Converts raw FBX values into the coordinate space produced by Unity's model importer.</summary>
    [Serializable]
    public struct FbxUnitySpaceConversion : IEquatable<FbxUnitySpaceConversion>
    {
        private const float DefaultEpsilon = 0.0001f;

        [SerializeField] private float m_ImportScale;
        [SerializeField] private bool m_BakeAxisConversion;

        /// <summary>Creates a conversion matching Unity model importer settings.</summary>
        public FbxUnitySpaceConversion(float importScale, bool bakeAxisConversion)
        {
            m_ImportScale = Mathf.Approximately(importScale, 0f) ? 1f : importScale;
            m_BakeAxisConversion = bakeAxisConversion;
        }

        /// <summary>Gets the Unity model importer scale.</summary>
        public float ImportScale => Mathf.Approximately(m_ImportScale, 0f) ? 1f : m_ImportScale;

        /// <summary>Gets whether Unity bakes axis conversion into imported transforms.</summary>
        public bool BakeAxisConversion => m_BakeAxisConversion;

        /// <summary>Gets the FBX-to-Unity change-of-basis matrix.</summary>
        public Matrix4x4 Basis => Matrix4x4.Scale(m_BakeAxisConversion
            ? new Vector3(ImportScale, ImportScale, -ImportScale)
            : new Vector3(-ImportScale, ImportScale, ImportScale));

        /// <summary>Converts an FBX point into Unity importer space.</summary>
        public Vector3 ConvertPoint(Vector3 fbxPoint)
        {
            return Basis.MultiplyPoint3x4(fbxPoint);
        }

        /// <summary>Converts an FBX vector into Unity importer space.</summary>
        public Vector3 ConvertVector(Vector3 fbxVector)
        {
            return Basis.MultiplyVector(fbxVector);
        }

        /// <summary>Converts an FBX normal delta without applying model import scale.</summary>
        public Vector3 ConvertNormalDelta(Vector3 fbxNormalDelta)
        {
            return ConvertVector(fbxNormalDelta) / ImportScale;
        }

        /// <summary>Converts and normalizes an FBX direction.</summary>
        public Vector3 ConvertDirection(Vector3 fbxDirection)
        {
            return ConvertVector(fbxDirection).normalized;
        }

        /// <summary>Converts an FBX matrix using one basis conjugation.</summary>
        public Matrix4x4 ConvertMatrix(FbxMatrix4x4 fbxMatrix)
        {
            Matrix4x4 basis = Basis;
            return basis * fbxMatrix.ToUnityColumnMatrix() * basis.inverse;
        }

        /// <summary>Converts and decomposes an evaluated FBX local matrix into an absolute Unity local transform.</summary>
        public bool TryConvertLocalTransform(
            FbxMatrix4x4 evaluatedNodeToParentMatrix,
            out UnityLocalTransform unityTransform,
            out string diagnostic)
        {
            return TryDecompose(
                ConvertMatrix(evaluatedNodeToParentMatrix),
                out unityTransform,
                out diagnostic);
        }

        /// <summary>Compares two importer-space conversion values.</summary>
        public bool ApproximatelyEquals(FbxUnitySpaceConversion other, float epsilon = DefaultEpsilon)
        {
            return m_BakeAxisConversion == other.m_BakeAxisConversion &&
                   Mathf.Abs(ImportScale - other.ImportScale) <= epsilon;
        }

        /// <summary>Tests exact serialized equality.</summary>
        public bool Equals(FbxUnitySpaceConversion other)
        {
            return m_ImportScale.Equals(other.m_ImportScale) &&
                   m_BakeAxisConversion == other.m_BakeAxisConversion;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is FbxUnitySpaceConversion other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (m_ImportScale.GetHashCode() * 397) ^ m_BakeAxisConversion.GetHashCode();
            }
        }

        private static bool TryDecompose(
            Matrix4x4 matrix,
            out UnityLocalTransform transform,
            out string diagnostic)
        {
            transform = UnityLocalTransform.Identity;
            if (!IsFinite(matrix))
            {
                diagnostic = "The evaluated FBX transform contains non-finite values.";
                return false;
            }

            if (Mathf.Abs(matrix[3, 0]) > DefaultEpsilon ||
                Mathf.Abs(matrix[3, 1]) > DefaultEpsilon ||
                Mathf.Abs(matrix[3, 2]) > DefaultEpsilon ||
                Mathf.Abs(matrix[3, 3] - 1f) > DefaultEpsilon)
            {
                diagnostic = "The evaluated FBX transform is not an affine local transform.";
                return false;
            }

            Vector3 x = matrix.GetColumn(0);
            Vector3 y = matrix.GetColumn(1);
            Vector3 z = matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (scale.x <= DefaultEpsilon || scale.y <= DefaultEpsilon || scale.z <= DefaultEpsilon)
            {
                diagnostic = "The evaluated FBX transform is singular and cannot be represented by a Unity Transform.";
                return false;
            }

            x /= scale.x;
            y /= scale.y;
            z /= scale.z;
            if (Mathf.Abs(Vector3.Dot(x, y)) > DefaultEpsilon ||
                Mathf.Abs(Vector3.Dot(x, z)) > DefaultEpsilon ||
                Mathf.Abs(Vector3.Dot(y, z)) > DefaultEpsilon)
            {
                diagnostic = "The evaluated FBX transform contains shear and cannot be represented by one Unity Transform.";
                return false;
            }

            float determinant = Vector3.Dot(Vector3.Cross(x, y), z);
            if (Mathf.Abs(determinant) <= DefaultEpsilon)
            {
                diagnostic = "The evaluated FBX transform has a singular rotation basis.";
                return false;
            }

            if (determinant < 0f)
            {
                scale.x = -scale.x;
                x = -x;
            }

            var rotationMatrix = Matrix4x4.identity;
            rotationMatrix.SetColumn(0, new Vector4(x.x, x.y, x.z, 0f));
            rotationMatrix.SetColumn(1, new Vector4(y.x, y.y, y.z, 0f));
            rotationMatrix.SetColumn(2, new Vector4(z.x, z.y, z.z, 0f));
            Quaternion rotation = rotationMatrix.rotation;
            var position = new Vector3(matrix[0, 3], matrix[1, 3], matrix[2, 3]);
            transform = new UnityLocalTransform(position, rotation, scale);

            Matrix4x4 reconstructed = transform.ToMatrix();
            if (!Approximately(matrix, reconstructed, DefaultEpsilon))
            {
                transform = UnityLocalTransform.Identity;
                diagnostic = "The evaluated FBX transform cannot be decomposed accurately into Unity position, rotation, and scale.";
                return false;
            }

            diagnostic = null;
            return true;
        }

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    float value = matrix[row, column];
                    if (float.IsNaN(value) || float.IsInfinity(value))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool Approximately(Matrix4x4 first, Matrix4x4 second, float epsilon)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(first[row, column] - second[row, column]) > epsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
