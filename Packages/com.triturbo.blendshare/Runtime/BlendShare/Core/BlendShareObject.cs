using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;


namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Root BlendShare asset containing extracted mesh feature data and generation defaults.
    /// </summary>
    public class BlendShareObject : ScriptableObject
    {
        [FbxAsset]
        public GameObject m_Original;
        public string m_DefaultGeneratedAssetName;
        public bool m_Applied = false;
        public string m_PatchId = "+BlendShare";

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

        /// <summary>
        /// Finds stored mesh data by its canonical renderer/node path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <returns>The matching mesh data object, or <c>null</c> when the path is not stored.</returns>
        public MeshDataObject GetMeshData(string path)
        {
            if (m_Meshes == null)
            {
                return null;
            }

            string normalizedPath = MeshNodePath.Normalize(path);
            return m_Meshes.FirstOrDefault(mesh =>
                mesh != null &&
                MeshNodePath.Normalize(mesh.m_Path) == normalizedPath);
        }

        /// <summary>
        /// Replaces all stored mesh data objects.
        /// </summary>
        /// <param name="meshes">Mesh data objects to store.</param>
        public void SetMeshes(IEnumerable<MeshDataObject> meshes)
        {
            m_Meshes = meshes?
                .Where(mesh => mesh != null)
                .GroupBy(mesh => MeshNodePath.Normalize(mesh.m_Path))
                .Select(group => group.First())
                .ToList() ?? new List<MeshDataObject>();
            Sanitize();
        }

        /// <summary>
        /// Adds mesh data when its path has not already been stored.
        /// </summary>
        /// <param name="meshData">Mesh data object to add.</param>
        public void AddMesh(MeshDataObject meshData)
        {
            if (meshData == null)
            {
                return;
            }

            meshData.m_Path = MeshNodePath.Normalize(meshData.m_Path);
            m_Meshes ??= new List<MeshDataObject>();
            if (m_Meshes.All(mesh => mesh == null || MeshNodePath.Normalize(mesh.m_Path) != meshData.m_Path))
            {
                m_Meshes.Add(meshData);
            }
            Sanitize();
        }

        /// <summary>
        /// Removes invalid mesh references and sanitizes all child mesh data.
        /// </summary>
        public void Sanitize()
        {
            m_Meshes ??= new List<MeshDataObject>();
            m_Meshes = m_Meshes
                .Where(mesh => mesh != null)
                .GroupBy(mesh => MeshNodePath.Normalize(mesh.m_Path))
                .Select(group => group.First())
                .ToList();

            foreach (var mesh in m_Meshes)
            {
                mesh.Sanitize();
            }
        }
    }
}
