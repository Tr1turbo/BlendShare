using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Precomputed source-to-origin alignment transform shared across extraction and comparison.
    /// Encapsulates affine point, linear vector, inverse-transpose normal delta, and skin bind-matrix rules.
    /// </summary>
    public readonly struct FbxSourceSpaceTransform
    {
        /// <summary>Identity transform that leaves all FBX geometry unchanged.</summary>
        public static readonly FbxSourceSpaceTransform Identity = new(FbxMatrix4x4.Identity, FbxMatrix4x4.Identity, true);

        /// <summary>Forward source-to-origin matrix.</summary>
        public FbxMatrix4x4 Offset { get; }

        /// <summary>Inverse of <see cref="Offset"/>.</summary>
        public FbxMatrix4x4 InverseOffset { get; }

        /// <summary>Whether this transform is identity.</summary>
        public bool IsIdentity { get; }

        /// <summary>Whether this transform holds a valid invertible matrix.</summary>
        public bool IsValid { get; }

        private FbxSourceSpaceTransform(FbxMatrix4x4 offset, FbxMatrix4x4 inverseOffset, bool isIdentity)
        {
            Offset = offset;
            InverseOffset = inverseOffset;
            IsIdentity = isIdentity;
            IsValid = true;
        }

        /// <summary>
        /// Creates an invertible <see cref="FbxSourceSpaceTransform"/> from a forward matrix.
        /// Returns <c>false</c> when the matrix is singular.
        /// </summary>
        public static bool TryCreate(FbxMatrix4x4 offset, out FbxSourceSpaceTransform transform)
        {
            if (offset.IsIdentity)
            {
                transform = Identity;
                return true;
            }

            if (!offset.TryInverse(out var inverse))
            {
                transform = default;
                return false;
            }

            transform = new FbxSourceSpaceTransform(offset, inverse, false);
            return true;
        }

        /// <summary>Transforms a control-point position including translation.</summary>
        public Vector3d TransformPoint(Vector3d point)
        {
            return IsIdentity ? point : Offset.TransformPoint(point);
        }

        /// <summary>Transforms a displacement delta using only the linear part (no translation).</summary>
        public Vector3d TransformVector(Vector3d vector)
        {
            return IsIdentity ? vector : Offset.TransformVector(vector);
        }

        /// <summary>Transforms a base normal using the inverse-transpose linear transform.</summary>
        public Vector3d TransformNormal(Vector3d normal)
        {
            return IsIdentity ? normal : InverseOffset.TransformNormalWithInverse(normal);
        }

        /// <summary>
        /// Transforms a blendshape normal delta relative to its untransformed base normal so
        /// non-uniform scaling preserves the deformed normal direction.
        /// </summary>
        public Vector3d TransformNormalDelta(Vector3d baseNormal, Vector3d normalDelta)
        {
            if (IsIdentity)
            {
                return normalDelta;
            }

            if (baseNormal.sqrMagnitude <= Vector3d.Epsilon)
            {
                // Preserve delta magnitude when base normals are unavailable (TransformNormalWithInverse normalizes).
                double magnitude = normalDelta.magnitude;
                if (magnitude <= Vector3d.Epsilon)
                {
                    return Vector3d.zero;
                }

                return InverseOffset.TransformNormalWithInverse(normalDelta) * magnitude;
            }

            return InverseOffset.TransformNormalWithInverse(baseNormal + normalDelta) -
                   InverseOffset.TransformNormalWithInverse(baseNormal);
        }

        /// <summary>Corrects a source skin cluster's mesh-bind-world matrix for the source offset.</summary>
        public FbxMatrix4x4 TransformMeshBindWorld(FbxMatrix4x4 meshBindWorld)
        {
            return IsIdentity ? meshBindWorld : InverseOffset * meshBindWorld;
        }
    }
}
