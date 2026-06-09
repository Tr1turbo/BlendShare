using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Hashing;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;


namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Per-mesh asset data keyed by FBX node path and containing feature subassets.
    /// </summary>
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public class MeshDataObject : ScriptableObject
    {
        // Canonical mesh identity. This is the FBX node path / Unity renderer path, not a Unity asset path.
        [FormerlySerializedAs("m_MeshPath")]
        public string m_Path;
        public FbxTopologySignature m_FbxTopologySignature = new FbxTopologySignature();

        [SerializeField]
        private List<MeshFeatureObject> m_Features = new();

        [SerializeField, NonReorderable]
        public UnityVertexMappingObject[] m_Mappings = System.Array.Empty<UnityVertexMappingObject>();

        public IReadOnlyList<MeshFeatureObject> Features =>
            m_Features != null ? m_Features : System.Array.Empty<MeshFeatureObject>();

        public int FbxControlPointCount => m_FbxTopologySignature?.ControlPointCount ?? -1;

        /// <summary>
        /// Initializes this mesh data object with its canonical renderer/node path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <param name="fbxTopologySignature">Topology signature calculated from the source FBX mesh.</param>
        public void Initialize(string path, FbxTopologySignature fbxTopologySignature)
        {
            m_Path = MeshNodePath.Normalize(path);
            m_FbxTopologySignature = fbxTopologySignature ?? new FbxTopologySignature();
            Sanitize();
        }

        public void Initialize(string path, int fbxControlPointCount)
        {
            Initialize(path, new FbxTopologySignature(string.Empty, fbxControlPointCount, -1, false));
        }

        /// <summary>
        /// Gets the first stored feature object of the requested type.
        /// </summary>
        /// <typeparam name="T">Feature object type to retrieve.</typeparam>
        /// <returns>The stored feature object, or <c>null</c> when no matching feature is present.</returns>
        public T GetFeature<T>() where T : MeshFeatureObject
        {
            return (m_Features ?? new List<MeshFeatureObject>())
                .OfType<T>()
                .FirstOrDefault();
        }

        /// <summary>
        /// Replaces all stored feature objects with the supplied feature collection.
        /// </summary>
        /// <param name="features">Feature objects to store under this mesh.</param>
        public void SetFeatures(IEnumerable<MeshFeatureObject> features)
        {
            m_Features = features?
                .Where(feature => feature != null && !string.IsNullOrWhiteSpace(feature.FeatureId))
                .GroupBy(feature => feature.FeatureId)
                .Select(group => group.First())
                .ToList() ?? new List<MeshFeatureObject>();
            Sanitize();
        }

        /// <summary>
        /// Adds one feature object if a feature with the same id is not already stored.
        /// </summary>
        /// <param name="feature">Feature object to add.</param>
        public void AddFeature(MeshFeatureObject feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.FeatureId))
            {
                return;
            }

            m_Features ??= new List<MeshFeatureObject>();
            if (m_Features.Any(existing => existing != null && existing.FeatureId == feature.FeatureId))
            {
                return;
            }

            m_Features.Add(feature);
            Sanitize();
        }

        /// <summary>
        /// Removes invalid feature references and lets each feature clean its stored data.
        /// </summary>
        public void Sanitize()
        {
            m_Path = MeshNodePath.Normalize(m_Path);
            m_Features ??= new List<MeshFeatureObject>();
            m_Features = m_Features
                .Where(feature => feature != null && !string.IsNullOrWhiteSpace(feature.FeatureId))
                .GroupBy(feature => feature.FeatureId)
                .Select(group => group.First())
                .ToList();

            foreach (var feature in m_Features)
            {
                feature.Sanitize(this);
            }
        }

        /// <summary>
        /// Checks whether any stored mapping matches a target Unity mesh.
        /// </summary>
        /// <param name="targetMesh">Target Unity mesh.</param>
        /// <returns><c>true</c> when a compatible mapping exists.</returns>
        public bool IsValidTarget(Mesh targetMesh)
        {
            return targetMesh != null &&
                   (m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                   .Any(mapping => mapping != null && mapping.IsCompatibleWith(this, targetMesh));
        }
    }
}
