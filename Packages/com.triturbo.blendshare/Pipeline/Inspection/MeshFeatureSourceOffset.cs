using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    /// <summary>
    /// Editable per-mesh alignment offset expressed in <c>OriginRawLocalV2</c> space,
    /// applied AFTER automatic scene/world space conversion <c>W</c> (<c>L = W * Offset</c>).
    /// </summary>
    public sealed class MeshFeatureSourceOffset
    {
        public Vector3 Position { get; set; }
        public Vector3 Rotation { get; set; }
        public Vector3 Scale { get; set; } = Vector3.one;

        public bool IsIdentity =>
            Position == Vector3.zero &&
            Rotation == Vector3.zero &&
            Scale == Vector3.one;

        public void Reset()
        {
            Position = Vector3.zero;
            Rotation = Vector3.zero;
            Scale = Vector3.one;
        }

        public Matrix4x4 ToUnityMatrix()
        {
            return Matrix4x4.TRS(Position, Quaternion.Euler(Rotation), SanitizeScale(Scale));
        }

        /// <summary>
        /// Converts this column-vector Unity TRS matrix into BlendShare's row-vector <see cref="FbxMatrix4x4"/>.
        /// </summary>
        public FbxMatrix4x4 ToFbxMatrix()
        {
            if (IsIdentity)
            {
                return FbxMatrix4x4.Identity;
            }

            Matrix4x4 matrix = ToUnityMatrix();
            return new FbxMatrix4x4(
                matrix[0, 0], matrix[1, 0], matrix[2, 0], matrix[3, 0],
                matrix[0, 1], matrix[1, 1], matrix[2, 1], matrix[3, 1],
                matrix[0, 2], matrix[1, 2], matrix[2, 2], matrix[3, 2],
                matrix[0, 3], matrix[1, 3], matrix[2, 3], matrix[3, 3]);
        }

        private static Vector3 SanitizeScale(Vector3 scale)
        {
            return new Vector3(
                Mathf.Approximately(scale.x, 0f) ? 1f : scale.x,
                Mathf.Approximately(scale.y, 0f) ? 1f : scale.y,
                Mathf.Approximately(scale.z, 0f) ? 1f : scale.z);
        }
    }

    /// <summary>
    /// Path-keyed collection of per-mesh <see cref="MeshFeatureSourceOffset"/> instances shared across
    /// preview, inspection comparisons, and extraction requests.
    /// </summary>
    public sealed class SourceOffsetTable
    {
        private readonly Dictionary<string, MeshFeatureSourceOffset> m_OffsetsByPath =
            new Dictionary<string, MeshFeatureSourceOffset>(StringComparer.Ordinal);

        public MeshFeatureSourceOffset GetOrCreate(string nodePath)
        {
            string key = FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
            if (string.IsNullOrEmpty(key))
            {
                return new MeshFeatureSourceOffset();
            }

            if (!m_OffsetsByPath.TryGetValue(key, out var offset))
            {
                offset = new MeshFeatureSourceOffset();
                m_OffsetsByPath.Add(key, offset);
            }

            return offset;
        }

        public bool TryGet(string nodePath, out MeshFeatureSourceOffset offset)
        {
            string key = FbxNameUtility.NormalizePath(nodePath ?? string.Empty);
            if (string.IsNullOrEmpty(key))
            {
                offset = null;
                return false;
            }

            return m_OffsetsByPath.TryGetValue(key, out offset);
        }

        public FbxMatrix4x4 GetFbxMatrix(string nodePath)
        {
            return TryGet(nodePath, out var offset) && offset != null
                ? offset.ToFbxMatrix()
                : FbxMatrix4x4.Identity;
        }

        public void ResetAll()
        {
            foreach (var offset in m_OffsetsByPath.Values)
            {
                offset?.Reset();
            }
        }

        public void Clear()
        {
            m_OffsetsByPath.Clear();
        }
    }
}
