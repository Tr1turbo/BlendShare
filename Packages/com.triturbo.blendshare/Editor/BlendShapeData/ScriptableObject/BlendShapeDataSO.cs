using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [PreferBinarySerialization]
    public class BlendShapeDataSO : ScriptableObject
    {
        public GameObject m_Original;

        public List<MeshData> m_MeshDataList;

        public string m_DefaultGeneratedAssetName;
        public bool m_Applied = false;
        public string m_DeformerID = "+BlendShare";


        public string DefaultMeshAssetName
        {
            get
            {
                if(string.IsNullOrEmpty(m_DefaultGeneratedAssetName))
                {
                    if(m_Original != null)
                    {
                        return m_Original.name;
                    }

                    return "MeshAsset";
                }
                return m_DefaultGeneratedAssetName;
            }
        }

        public string DefaultFbxName
        {
            get
            {
                if (string.IsNullOrEmpty(m_DefaultGeneratedAssetName))
                {
                    if (m_Original != null)
                    {
                        return $"{m_Original.name}_BlendShare";
                    }

                    return "BlendShareFbx";
                }
                return m_DefaultGeneratedAssetName;
            }
        }

    }



    [System.Serializable]
    public class BlendShapeWrapper
    {
        public string m_ShapeName;
        public FbxBlendShapeData m_FbxBlendShapeData;
        public UnityBlendShapeData m_UnityBlendShapeData;
    }


    [System.Serializable]
    public class MeshData
    {
        public Mesh m_OriginMesh;

        public int m_VertexCount;
        public int m_VerticesHash;

        public string m_MeshName;


        public List<string> m_ShapeNames;

        [SerializeField]
        private List<BlendShapeWrapper> m_BlendShapes;
        public List<BlendShapeWrapper> BlendShapes
        {
            get
            {
                var ordered = new List<BlendShapeWrapper>();
                foreach (var name in m_ShapeNames)
                {
                    ordered.Add(m_BlendShapes.Single(b => b.m_ShapeName == name));
                }
                return ordered;
            }
        }

        public bool ContainsBlendShape(string name)
        {
            return m_ShapeNames.Contains(name);
        }
        public BlendShapeWrapper GetBlendShape(string name)
        {
            if(m_ShapeNames.Contains(name))
            {
                BlendShapeWrapper blendShape = m_BlendShapes.SingleOrDefault(b => b.m_ShapeName == name);
                if (blendShape == null)
                {
                    blendShape = new BlendShapeWrapper(){ m_ShapeName = name };
                    m_BlendShapes.Add(blendShape);

                }

                return blendShape;
            }
            return null;
        }

        public bool AddBlendShape(BlendShapeWrapper blendShapeWrapper)
        {
            if (!ContainsBlendShape(blendShapeWrapper.m_ShapeName))
            {
                m_BlendShapes.Add(blendShapeWrapper);
                m_ShapeNames.Add(blendShapeWrapper.m_ShapeName);
                return true;
            }
            return false;
        }

        public void SetBlendShape(string name, FbxBlendShapeData fbxBlendShape)
        {
            var blendShape = GetBlendShape(name);
            if (blendShape == null)
            {
                blendShape = new BlendShapeWrapper() { m_ShapeName = name };
                m_BlendShapes.Add(blendShape);
                m_ShapeNames.Add(name);
            }

            blendShape.m_FbxBlendShapeData = fbxBlendShape;
        }

        public void SetBlendShape(string name, UnityBlendShapeData unityBlendShapeData)
        {
            var blendShape = GetBlendShape(name);
            if (blendShape == null)
            {
                blendShape = new BlendShapeWrapper() { m_ShapeName = name };
                m_BlendShapes.Add(blendShape);
                m_ShapeNames.Add(name);
            }

            blendShape.m_UnityBlendShapeData = unityBlendShapeData;
        }




        public MeshData(string name, Mesh mesh)
        {
            this.m_MeshName = name;
            m_OriginMesh = mesh;


            this.m_VertexCount = mesh.vertexCount;
            this.m_VerticesHash = GetVerticesHash(mesh);

            m_BlendShapes = new List<BlendShapeWrapper>();

            m_ShapeNames = new List<string>();

        }
        public MeshData(Mesh mesh, IEnumerable<string> blendShapeNames)
        {
            this.m_MeshName = mesh.name;
            m_OriginMesh = mesh;


            this.m_VertexCount = mesh.vertexCount;
            this.m_VerticesHash = GetVerticesHash(mesh);

            m_BlendShapes = new List<BlendShapeWrapper>();

            m_ShapeNames = blendShapeNames.ToList();
            foreach(string name in blendShapeNames)
            {
                m_BlendShapes.Add(new BlendShapeWrapper() { m_ShapeName = name });
            }

        }
        public static int GetVerticesHash(Mesh mesh)
        {
            return ((IStructuralEquatable)mesh.vertices).GetHashCode(EqualityComparer<Vector3>.Default);
        }
    }

    [System.Serializable]
    public class FbxBlendShapeFrame
    {
        public List<int> m_PointsIndices;
        public List<Vector4d> m_DeltaControlPointsList;

        public Dictionary<int, Vector4d> _deltaControlPointsDict;



        public FbxBlendShapeFrame()
        {
            m_PointsIndices = new List<int>();
            m_DeltaControlPointsList = new List<Vector4d>();
        }

        public void AddDeltaControlPointAt(Vector4d controlPoint, int index)
        {
            m_PointsIndices.Add(index);
            m_DeltaControlPointsList.Add(controlPoint);
        }

        public Vector4d GetDeltaControlPointAt(int index)
        {
            if(_deltaControlPointsDict == null)
            {
                _deltaControlPointsDict = new Dictionary<int, Vector4d>();
                for (int i = 0; i < m_PointsIndices.Count;i++)
                {
                    _deltaControlPointsDict.Add(m_PointsIndices[i], m_DeltaControlPointsList[i]);
                }
            }

            if(!_deltaControlPointsDict.TryGetValue(index, out Vector4d point))
            {
                point = Vector4d.zero;
            }

            return point;
        }

    }
    [System.Serializable]
    public class Vector4d
    {
        public double m_X;
        public double m_Y;
        public double m_Z;
        public double m_W;
        public Vector4d(double x, double y, double z, double w)
        {
            this.m_X = x;
            this.m_Y = y;
            this.m_Z = z;
            this.m_W = w;
        }

        public bool IsZero()
        {
            return m_X == 0 && m_Y == 0 && m_Z == 0 && m_W == 0;
        }

        public static Vector4d zero
        {
            get { return new Vector4d(0, 0, 0, 0); }
        }
    }

    [System.Serializable]
    public class FbxBlendShapeData
    {

        public FbxBlendShapeFrame[] m_Frames;

        public FbxBlendShapeData(int frameCount)
        {

            this.m_Frames = new FbxBlendShapeFrame[frameCount];
        }
        public FbxBlendShapeData(FbxBlendShapeFrame[] frames)
        {

            this.m_Frames = frames;
        }
    }

    [System.Serializable]
    public class UnityBlendShapeData
    {

        public UnityBlendShapeFrame[] m_Frames;
        public UnityBlendShapeData(int frameCount)
        {
            m_Frames = new UnityBlendShapeFrame[frameCount];
        }

        public void AddFrameAt(int i, UnityBlendShapeFrame frame)
        {
            m_Frames[i] = frame;
        }

        public UnityBlendShapeData(Mesh sourceMesh, int index)
        {
            int frameCount = sourceMesh.GetBlendShapeFrameCount(index);


            m_Frames = new UnityBlendShapeFrame[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(index, i);
                Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
                Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
                Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];

                sourceMesh.GetBlendShapeFrameVertices(index, i, deltaVertices, deltaNormals, deltaTangents);


                m_Frames[i] = new UnityBlendShapeFrame(frameWeight, sourceMesh.vertexCount,
                    deltaVertices, deltaNormals, deltaTangents);
            }
        }



    }



    [System.Serializable]
    public class UnityBlendShapeFrame
    {
        public float m_FrameWeight;

        public List<int> m_VertexIndices;
        public List<int> m_NormalIndices;
        public List<int> m_TangentIndices;

        public List<Vector3> m_DeltaVertices;
        public List<Vector3> m_DeltaNormals;
        public List<Vector3> m_DeltaTangents;


        public void AddBlendShapeFrame(ref Mesh targetMesh, string name)
        {
            var vertexCount = targetMesh.vertexCount;
            targetMesh.AddBlendShapeFrame(name, m_FrameWeight,
                GetDeltaVertices(vertexCount),
                GetDeltaNormals(vertexCount),
                GetDeltaTangents(vertexCount));
        }

  

        public UnityBlendShapeFrame(float weight, int vertexCount,
            Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)
        {
            this.m_FrameWeight = weight;

            this.m_VertexIndices = new List<int>(vertexCount);
            this.m_NormalIndices = new List<int>(vertexCount);
            this.m_TangentIndices = new List<int>(vertexCount);

            this.m_DeltaVertices = new List<Vector3>(vertexCount);
            this.m_DeltaNormals = new List<Vector3>(vertexCount);
            this.m_DeltaTangents = new List<Vector3>(vertexCount);

            for (int i = 0; i < vertexCount; i++)
            {

                if (deltaVertices[i] != Vector3.zero)
                {
                    this.m_VertexIndices.Add(i);
                    this.m_DeltaVertices.Add(deltaVertices[i]);
                }
                if (deltaNormals[i] != Vector3.zero)
                {
                    this.m_NormalIndices.Add(i);
                    this.m_DeltaVertices.Add(deltaNormals[i]);
                }
                if (deltaTangents[i] != Vector3.zero)
                {
                    this.m_TangentIndices.Add(i);
                    this.m_DeltaVertices.Add(deltaTangents[i]);
                }
            }


        }

        public Vector3[] GetDeltaVertices(int vertexCount)
        {
            Vector3[] delta = new Vector3[vertexCount];
            int i = 0;
            foreach (var index in m_VertexIndices)
            {
                if (index >= vertexCount) continue;
                delta[index] = m_DeltaVertices[i++];
            }
            return delta;
        }

        public Vector3[] GetDeltaNormals(int vertexCount)
        {
            Vector3[] delta = new Vector3[vertexCount];
            int i = 0;
            foreach (var index in m_NormalIndices)
            {
                if (index >= vertexCount) continue;
                delta[index] = m_DeltaNormals[i++];
            }
            return delta;

        }
        public Vector3[] GetDeltaTangents(int vertexCount)
        {
            Vector3[] delta = new Vector3[vertexCount];
            int i = 0;
            foreach (var index in m_TangentIndices)
            {
                if (index >= vertexCount) continue;
                delta[index] = m_DeltaTangents[i++];
            }
            return delta;

        }


    }
}

