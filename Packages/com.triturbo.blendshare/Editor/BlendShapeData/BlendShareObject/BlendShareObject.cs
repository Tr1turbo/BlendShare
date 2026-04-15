using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    [CreateAssetMenu(fileName = "BlendShareData", menuName = "BlendShare/BlendShareObject", order = 1)]
    public class BlendShareObject : ScriptableObject
    {
        public GameObject m_Original;
        public string m_DefaultGeneratedAssetName;
        public bool m_Applied = false;
        public string m_DeformerID = "+BlendShare";

        [SerializeField]
        private List<MeshDataObject> m_Meshes = new();

        public IReadOnlyList<MeshDataObject> Meshes => m_Meshes;

        public string DefaultMeshAssetName
        {
            get
            {
                if (string.IsNullOrEmpty(m_DefaultGeneratedAssetName))
                {
                    if (m_Original != null)
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

        public MeshDataObject GetMeshData(string meshPathOrName)
        {
            if (string.IsNullOrEmpty(meshPathOrName) || m_Meshes == null)
            {
                return null;
            }

            return m_Meshes.FirstOrDefault(mesh =>
                mesh != null &&
                (mesh.m_MeshPath == meshPathOrName || mesh.m_MeshName == meshPathOrName));
        }

        public void SetMeshes(IEnumerable<MeshDataObject> meshes)
        {
            m_Meshes = meshes?.Where(mesh => mesh != null).Distinct().ToList() ?? new List<MeshDataObject>();
            Sanitize();
        }

        public void AddMesh(MeshDataObject meshData)
        {
            if (meshData == null)
            {
                return;
            }

            m_Meshes ??= new List<MeshDataObject>();
            if (!m_Meshes.Contains(meshData))
            {
                m_Meshes.Add(meshData);
            }
            Sanitize();
        }

        public void Sanitize()
        {
            m_Meshes ??= new List<MeshDataObject>();
            m_Meshes = m_Meshes.Where(mesh => mesh != null).Distinct().ToList();

            foreach (var mesh in m_Meshes)
            {
                mesh.Sanitize();
            }
        }
    }
}
