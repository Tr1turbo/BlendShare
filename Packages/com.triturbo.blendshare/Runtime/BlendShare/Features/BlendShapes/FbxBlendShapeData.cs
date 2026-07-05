using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    [System.Serializable]
    public class FbxBlendShapeData
    {
        public string m_Name;
        public FbxBlendShapeFrame[] m_Frames;

        public FbxBlendShapeData(int frameCount)
        {
            m_Frames = new FbxBlendShapeFrame[frameCount];
        }

        public FbxBlendShapeData(string name, int frameCount)
        {
            m_Name = name;
            m_Frames = new FbxBlendShapeFrame[frameCount];
        }

        public FbxBlendShapeData(FbxBlendShapeFrame[] frames)
        {
            m_Frames = frames;
        }

        public FbxBlendShapeData(string name, FbxBlendShapeFrame[] frames)
        {
            m_Name = name;
            m_Frames = frames;
        }
    }

    [System.Serializable]
    public class FbxBlendShapeFrame
    {
        public float m_FrameWeight = 100f;
        public SparseArray<Vector3d> m_DeltaPositions = new();
        public SparseArray<Vector3d> m_DeltaNormals = new();

        public FbxBlendShapeFrame() { }

        public FbxBlendShapeFrame(float frameWeight)
        {
            m_FrameWeight = frameWeight;
        }

        public void SetDeltaPositionAt(int index, Vector3d position)
        {
            m_DeltaPositions ??= new SparseArray<Vector3d>();
            SetDelta(m_DeltaPositions, index, position);
        }

        public void SetDeltaNormalAt(int index, Vector3d normal)
        {
            m_DeltaNormals ??= new SparseArray<Vector3d>();
            SetDelta(m_DeltaNormals, index, normal);
        }

        public Vector3d GetDeltaPositionAt(int index)
        {
            return m_DeltaPositions?.Get(index, Vector3d.zero) ?? Vector3d.zero;
        }

        public Vector3d GetDeltaNormalAt(int index)
        {
            return m_DeltaNormals?.Get(index, Vector3d.zero) ?? Vector3d.zero;
        }

        public int StoredDeltaCount =>
            (m_DeltaPositions?.Count ?? 0) + (m_DeltaNormals?.Count ?? 0);

        public int MaxDeltaIndex =>
            System.Math.Max(m_DeltaPositions?.MaxIndex ?? -1, m_DeltaNormals?.MaxIndex ?? -1);

        private static void SetDelta(SparseArray<Vector3d> deltas, int index, Vector3d value)
        {
            if (deltas == null)
            {
                return;
            }

            if (value.IsZero())
            {
                deltas.Remove(index);
                return;
            }

            deltas.Set(index, value);
        }
    }
}
