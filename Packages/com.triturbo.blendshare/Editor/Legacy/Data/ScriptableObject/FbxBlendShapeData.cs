using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [System.Serializable]
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
    public class FbxBlendShapeFrame
    {
        public List<int> m_PointsIndices;
        public List<Vector4d> m_DeltaControlPointsList;

        public Dictionary<int, Vector3d> _deltaControlPointsDict;

        public FbxBlendShapeFrame()
        {
            m_PointsIndices = new List<int>();
            m_DeltaControlPointsList = new List<Vector4d>();
        }

        public void AddDeltaControlPointAt(Vector3d controlPoint, int index)
        {
            AddDeltaControlPointAt(new Vector4d(controlPoint.x, controlPoint.y, controlPoint.z, 0), index);
        }

        public void AddDeltaControlPointAt(Vector4d controlPoint, int index)
        {
            m_PointsIndices ??= new List<int>();
            m_DeltaControlPointsList ??= new List<Vector4d>();

            m_PointsIndices.Add(index);
            m_DeltaControlPointsList.Add(controlPoint ?? Vector4d.zero);
            _deltaControlPointsDict = null;
        }

        public Vector3d GetDeltaControlPointAt(int index)
        {
            MigrateLegacyVectors();

            if (_deltaControlPointsDict == null)
            {
                _deltaControlPointsDict = new Dictionary<int, Vector3d>();
                int count = System.Math.Min(m_PointsIndices?.Count ?? 0, m_DeltaControlPointsList?.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    var legacyPoint = m_DeltaControlPointsList[i];
                    _deltaControlPointsDict[m_PointsIndices[i]] = legacyPoint == null
                        ? Vector3d.zero
                        : new Vector3d(legacyPoint.m_X, legacyPoint.m_Y, legacyPoint.m_Z);
                }
            }

            if (!_deltaControlPointsDict.TryGetValue(index, out Vector3d point))
            {
                point = Vector3d.zero;
            }

            return point;
        }

        public void MigrateLegacyVectors()
        {
            m_PointsIndices ??= new List<int>();
            m_DeltaControlPointsList ??= new List<Vector4d>();
        }
    }
}
