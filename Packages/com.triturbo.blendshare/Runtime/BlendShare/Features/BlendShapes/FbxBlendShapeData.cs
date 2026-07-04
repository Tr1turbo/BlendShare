using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [System.Obsolete("Vector4d is kept only for deserializing legacy FBX blendshape data. Use Vector3d.")]
    [System.Serializable]
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class Vector4d
    {
        public double m_X;
        public double m_Y;
        public double m_Z;
        public double m_W;

        public Vector4d(double x, double y, double z, double w)
        {
            m_X = x;
            m_Y = y;
            m_Z = z;
            m_W = w;
        }

        public bool IsZero()
        {
            return m_X == 0 && m_Y == 0 && m_Z == 0 && m_W == 0;
        }

        public static Vector4d zero => new Vector4d(0, 0, 0, 0);
    }

    [System.Serializable]
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class FbxBlendShapeData
    {
        public FbxBlendShapeFrame[] m_Frames;

        public FbxBlendShapeData(int frameCount)
        {
            m_Frames = new FbxBlendShapeFrame[frameCount];
        }

        public FbxBlendShapeData(FbxBlendShapeFrame[] frames)
        {
            m_Frames = frames;
        }

        public void MigrateLegacyVectors()
        {
            foreach (var frame in m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
            {
                frame?.MigrateLegacyVectors();
            }
        }
    }

    [System.Serializable]
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class FbxBlendShapeFrame
    {
        public List<int> m_PointsIndices;
        public List<Vector3d> m_DeltaControlPoints;
        public List<Vector3d> m_DeltaNormals;

        [SerializeField, HideInInspector]
        private List<Vector4d> m_DeltaControlPointsList;

        public Dictionary<int, Vector3d> _deltaControlPointsDict;
        public Dictionary<int, Vector3d> _deltaNormalsDict;

        public FbxBlendShapeFrame()
        {
            m_PointsIndices = new List<int>();
            m_DeltaControlPoints = new List<Vector3d>();
            m_DeltaNormals = new List<Vector3d>();
        }

        public void AddDeltaControlPointAt(Vector3d controlPoint, int index)
        {
            AddDeltaControlPointAt(controlPoint, index, Vector3d.zero);
        }

        public void AddDeltaControlPointAt(Vector3d controlPoint, int index, Vector3d normal)
        {
            m_PointsIndices ??= new List<int>();
            m_DeltaControlPoints ??= new List<Vector3d>();
            m_DeltaNormals ??= new List<Vector3d>();

            m_PointsIndices.Add(index);
            m_DeltaControlPoints.Add(controlPoint);
            m_DeltaNormals.Add(normal);
            _deltaControlPointsDict = null;
            _deltaNormalsDict = null;
        }

        [System.Obsolete("Use AddDeltaControlPointAt(Vector3d, int). This overload is kept for legacy BlendShapeDataSO APIs.")]
        public void AddDeltaControlPointAt(Vector4d controlPoint, int index)
        {
            AddDeltaControlPointAt(
                controlPoint == null
                    ? Vector3d.zero
                    : new Vector3d(controlPoint.m_X, controlPoint.m_Y, controlPoint.m_Z),
                index);
        }

        public Vector3d GetDeltaControlPointAt(int index)
        {
            MigrateLegacyVectors();

            if (_deltaControlPointsDict == null)
            {
                _deltaControlPointsDict = new Dictionary<int, Vector3d>();
                int count = System.Math.Min(m_PointsIndices?.Count ?? 0, m_DeltaControlPoints?.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    _deltaControlPointsDict[m_PointsIndices[i]] = m_DeltaControlPoints[i];
                }
            }

            if (!_deltaControlPointsDict.TryGetValue(index, out Vector3d point))
            {
                point = Vector3d.zero;
            }

            return point;
        }

        public Vector3d GetDeltaNormalAt(int index)
        {
            MigrateLegacyVectors();

            if (_deltaNormalsDict == null)
            {
                _deltaNormalsDict = new Dictionary<int, Vector3d>();
                int count = System.Math.Min(m_PointsIndices?.Count ?? 0, m_DeltaNormals?.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    _deltaNormalsDict[m_PointsIndices[i]] = m_DeltaNormals[i];
                }
            }

            if (!_deltaNormalsDict.TryGetValue(index, out Vector3d normal))
            {
                normal = Vector3d.zero;
            }

            return normal;
        }

        public void MigrateLegacyVectors()
        {
            m_PointsIndices ??= new List<int>();
            m_DeltaControlPoints ??= new List<Vector3d>();
            m_DeltaNormals ??= new List<Vector3d>();

            if (m_DeltaControlPoints.Count == 0 && m_DeltaControlPointsList != null && m_DeltaControlPointsList.Count > 0)
            {
                foreach (var legacyPoint in m_DeltaControlPointsList)
                {
                    m_DeltaControlPoints.Add(legacyPoint == null
                        ? Vector3d.zero
                        : new Vector3d(legacyPoint.m_X, legacyPoint.m_Y, legacyPoint.m_Z));
                }
            }

            if (m_DeltaControlPointsList != null)
            {
                m_DeltaControlPointsList = null;
                _deltaControlPointsDict = null;
            }

            while (m_DeltaNormals.Count < m_DeltaControlPoints.Count)
            {
                m_DeltaNormals.Add(Vector3d.zero);
                _deltaNormalsDict = null;
            }
        }
    }
}
