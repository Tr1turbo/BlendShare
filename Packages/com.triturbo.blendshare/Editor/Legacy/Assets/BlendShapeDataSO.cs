using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    /// <summary>
    /// Deserialization-only compatibility shell for BlendShare 1.x (<c>.asset</c>) patch assets.
    /// Preserves all 1.0.4 serialized fields and the original script GUID so existing assets can be inspected,
    /// edited without data loss, and upgraded to <c>.blendshare</c>. Legacy procedural APIs are omitted.
    /// </summary>
    public sealed class BlendShapeDataSO : ScriptableObject
    {
        internal const string OriginalFieldName = nameof(m_Original);
        internal const string DefaultGeneratedAssetNameFieldName = nameof(m_DefaultGeneratedAssetName);
        internal const string AppliedFieldName = nameof(m_Applied);
        internal const string DeformerIdFieldName = nameof(m_DeformerID);
        internal const string MeshDataListFieldName = nameof(m_MeshDataList);

        [SerializeField] private GameObject m_Original;
        [SerializeField] private string m_DefaultGeneratedAssetName = string.Empty;
        [SerializeField] private bool m_Applied;
        [SerializeField] private string m_DeformerID = "+BlendShare";
        [SerializeField] private List<MeshData> m_MeshDataList = new List<MeshData>();

        public GameObject Original => m_Original;
        public string DefaultGeneratedAssetName => m_DefaultGeneratedAssetName ?? string.Empty;
        public bool Applied => m_Applied;
        public string DeformerId => m_DeformerID ?? string.Empty;
        public IReadOnlyList<MeshData> MeshDataList => m_MeshDataList ?? (IReadOnlyList<MeshData>)Array.Empty<MeshData>();

        private void OnValidate()
        {
            if (m_MeshDataList == null)
            {
                return;
            }

            for (int i = 0; i < m_MeshDataList.Count; i++)
            {
                m_MeshDataList[i]?.SanitizeShapeNames();
            }
        }
    }

    /// <summary>
    /// Deserialization shell for a single mesh entry inside <see cref="BlendShapeDataSO"/>.
    /// </summary>
    [Serializable]
    public sealed class MeshData
    {
        internal const string OriginMeshFieldName = nameof(m_OriginMesh);
        internal const string MeshNameFieldName = nameof(m_MeshName);
        internal const string ShapeNamesFieldName = nameof(m_ShapeNames);
        internal const string BlendShapesFieldName = nameof(m_BlendShapes);

        [SerializeField] private Mesh m_OriginMesh;
        [SerializeField] private int m_VertexCount;
        [SerializeField] private int m_VerticesHash;
        [SerializeField] private string m_MeshName = string.Empty;
        [SerializeField] private List<string> m_ShapeNames = new List<string>();
        [SerializeField] private List<BlendShapeWrapper> m_BlendShapes = new List<BlendShapeWrapper>();

        public Mesh ReferenceUnityMesh => m_OriginMesh;
        public int VertexCount => m_VertexCount;
        public int VerticesHash => m_VerticesHash;
        public string MeshName => m_MeshName ?? string.Empty;
        public IReadOnlyList<string> ShapeNames => m_ShapeNames ?? (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<BlendShapeWrapper> RawBlendShapes => m_BlendShapes ?? (IReadOnlyList<BlendShapeWrapper>)Array.Empty<BlendShapeWrapper>();

        internal List<string> GetAllBlendShapeNames()
        {
            if (m_BlendShapes == null)
            {
                return new List<string>();
            }

            return m_BlendShapes
                .Where(b => b != null && !string.IsNullOrEmpty(b.ShapeName))
                .Select(b => b.ShapeName)
                .ToList();
        }

        internal void SetWorkingShapeNames(IEnumerable<string> shapeNames)
        {
            m_ShapeNames = shapeNames != null ? new List<string>(shapeNames) : new List<string>();
        }

        internal bool SanitizeShapeNames()
        {
            m_BlendShapes ??= new List<BlendShapeWrapper>();
            m_ShapeNames ??= new List<string>();

            var validShapeNames = new HashSet<string>(
                m_BlendShapes
                    .Where(b => b != null && !string.IsNullOrWhiteSpace(b.ShapeName))
                    .Select(b => b.ShapeName),
                StringComparer.Ordinal);

            var sanitized = new List<string>(m_ShapeNames.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < m_ShapeNames.Count; i++)
            {
                string name = m_ShapeNames[i];
                if (string.IsNullOrWhiteSpace(name) || !validShapeNames.Contains(name) || !seen.Add(name))
                {
                    continue;
                }

                sanitized.Add(name);
            }

            if (sanitized.Count == m_ShapeNames.Count)
            {
                return false;
            }

            m_ShapeNames = sanitized;
            return true;
        }
    }

    /// <summary>
    /// Deserialization shell for a single blendshape channel inside <see cref="MeshData"/>.
    /// Retains both <c>m_FbxBlendShapeData</c> and <c>m_UnityBlendShapeData</c> so editing/saving a 1.x asset
    /// never strips serialized data.
    /// </summary>
    [Serializable]
    public sealed class BlendShapeWrapper
    {
        internal const string ShapeNameFieldName = nameof(m_ShapeName);

        [SerializeField] private string m_ShapeName = string.Empty;
        [SerializeField] private FbxBlendShapeData m_FbxBlendShapeData;
        [SerializeField] private UnityBlendShapeData m_UnityBlendShapeData;

        public string ShapeName => m_ShapeName ?? string.Empty;
        public FbxBlendShapeData FbxBlendShapeData => m_FbxBlendShapeData;
        public UnityBlendShapeData UnityBlendShapeData => m_UnityBlendShapeData;
    }

    /// <summary>
    /// Deserialization shell for sparse FBX control-point keyframes on a 1.x blendshape channel.
    /// </summary>
    [Serializable]
    public sealed class FbxBlendShapeData
    {
        [SerializeField] private FbxBlendShapeFrame[] m_Frames = Array.Empty<FbxBlendShapeFrame>();

        public IReadOnlyList<FbxBlendShapeFrame> Frames => m_Frames ?? Array.Empty<FbxBlendShapeFrame>();
    }

    /// <summary>
    /// Deserialization shell for a single sparse FBX control-point keyframe in 1.x.
    /// </summary>
    [Serializable]
    public sealed class FbxBlendShapeFrame
    {
        [SerializeField] private List<int> m_PointsIndices = new List<int>();
        [SerializeField] private List<Vector4d> m_DeltaControlPointsList = new List<Vector4d>();

        public IReadOnlyList<int> PointIndices => m_PointsIndices ?? (IReadOnlyList<int>)Array.Empty<int>();
        public IReadOnlyList<Vector4d> DeltaControlPoints => m_DeltaControlPointsList ?? (IReadOnlyList<Vector4d>)Array.Empty<Vector4d>();
    }

    /// <summary>
    /// Deserialization shell matching 1.x <c>Triturbo.BlendShapeShare.BlendShapeData.Vector4d</c>.
    /// </summary>
    [Serializable]
    public sealed class Vector4d
    {
        [SerializeField] private double m_X;
        [SerializeField] private double m_Y;
        [SerializeField] private double m_Z;
        [SerializeField] private double m_W;

        public double X => m_X;
        public double Y => m_Y;
        public double Z => m_Z;
        public double W => m_W;
    }

    /// <summary>
    /// Deserialization shell preserving 1.x <c>m_UnityBlendShapeData</c> across Inspector saves.
    /// </summary>
    [Serializable]
    public sealed class UnityBlendShapeData
    {
        [SerializeField] private UnityBlendShapeFrame[] m_Frames = Array.Empty<UnityBlendShapeFrame>();

        public IReadOnlyList<UnityBlendShapeFrame> Frames => m_Frames ?? Array.Empty<UnityBlendShapeFrame>();
    }

    /// <summary>
    /// Deserialization shell preserving 1.x <c>UnityBlendShapeFrame</c> fields across Inspector saves.
    /// </summary>
    [Serializable]
    public sealed class UnityBlendShapeFrame
    {
        [SerializeField] private float m_FrameWeight;
        [SerializeField] private List<int> m_VertexIndices = new List<int>();
        [SerializeField] private List<int> m_NormalIndices = new List<int>();
        [SerializeField] private List<int> m_TangentIndices = new List<int>();
        [SerializeField] private List<Vector3> m_DeltaVertices = new List<Vector3>();
        [SerializeField] private List<Vector3> m_DeltaNormals = new List<Vector3>();
        [SerializeField] private List<Vector3> m_DeltaTangents = new List<Vector3>();

        public float FrameWeight => m_FrameWeight;
        public IReadOnlyList<int> VertexIndices => m_VertexIndices ?? (IReadOnlyList<int>)Array.Empty<int>();
        public IReadOnlyList<int> NormalIndices => m_NormalIndices ?? (IReadOnlyList<int>)Array.Empty<int>();
        public IReadOnlyList<int> TangentIndices => m_TangentIndices ?? (IReadOnlyList<int>)Array.Empty<int>();
        public IReadOnlyList<Vector3> DeltaVertices => m_DeltaVertices ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>();
        public IReadOnlyList<Vector3> DeltaNormals => m_DeltaNormals ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>();
        public IReadOnlyList<Vector3> DeltaTangents => m_DeltaTangents ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>();
    }
}
