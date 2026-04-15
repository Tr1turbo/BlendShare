using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public class MeshDataObject : ScriptableObject
    {
        // FBX hierarchy path. This is not a Unity asset path.
        public string m_MeshPath;
        public string m_MeshName;
        public int m_FbxControlPointCount = -1;

        [SerializeField]
        private List<MeshFeatureObject> m_Features = new();

        public UnityVertexMappingObject[] m_Mappings;

        public IReadOnlyList<MeshFeatureObject> Features =>
            m_Features != null ? m_Features : System.Array.Empty<MeshFeatureObject>();
        public IReadOnlyList<BlendShapeRecord> BlendShapes =>
            GetFeature<BlendShapeFeatureObject>()?.BlendShapes ?? System.Array.Empty<BlendShapeRecord>();
        public IReadOnlyList<int> ActiveBlendShapeIndices =>
            GetFeature<BlendShapeFeatureObject>()?.ActiveBlendShapeIndices ?? System.Array.Empty<int>();

        public void Initialize(string meshPath, string meshName, int fbxControlPointCount)
        {
            m_MeshPath = meshPath;
            m_MeshName = meshName;
            m_FbxControlPointCount = fbxControlPointCount;
            Sanitize();
        }

        public T GetFeature<T>() where T : MeshFeatureObject
        {
            return (m_Features ?? new List<MeshFeatureObject>())
                .OfType<T>()
                .FirstOrDefault();
        }

        public void SetFeatures(IEnumerable<MeshFeatureObject> features)
        {
            m_Features = features?
                .Where(feature => feature != null && !string.IsNullOrWhiteSpace(feature.FeatureId))
                .GroupBy(feature => feature.FeatureId)
                .Select(group => group.First())
                .ToList() ?? new List<MeshFeatureObject>();
            Sanitize();
        }

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

        public void Sanitize()
        {
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

        public IEnumerable<BlendShapeRecord> GetActiveBlendShapes()
        {
            return GetFeature<BlendShapeFeatureObject>()?.GetActiveBlendShapes() ??
                   Enumerable.Empty<BlendShapeRecord>();
        }

        public List<string> GetAllBlendShapeNames()
        {
            return GetFeature<BlendShapeFeatureObject>()?.GetAllBlendShapeNames() ?? new List<string>();
        }

        public bool ContainsBlendShape(string name)
        {
            return GetFeature<BlendShapeFeatureObject>()?.ContainsBlendShape(name) ?? false;
        }

        public BlendShapeRecord GetBlendShape(string name)
        {
            return GetFeature<BlendShapeFeatureObject>()?.GetBlendShape(name);
        }

        public void SetBlendShape(string name, FbxBlendShapeData data)
        {
            GetOrCreateBlendShapeFeature().SetBlendShape(name, data);
        }

        public void SetBlendShapes(IEnumerable<BlendShapeRecord> blendShapes)
        {
            GetOrCreateBlendShapeFeature().SetBlendShapes(blendShapes);
        }

        public void SetActiveBlendShapeIndices(IEnumerable<int> indices)
        {
            GetOrCreateBlendShapeFeature().SetActiveBlendShapeIndices(indices);
        }

        public void SetActiveBlendShapeNames(IEnumerable<string> shapeNames)
        {
            GetOrCreateBlendShapeFeature().SetActiveBlendShapeNames(shapeNames);
        }

        public void ApplyPreset(BlendShapePresetObject preset)
        {
            GetFeature<BlendShapeFeatureObject>()?.ApplyPreset(this, preset);
        }

        public BlendShapePresetObject CreatePreset(string presetName)
        {
            return GetOrCreateBlendShapeFeature().CreatePreset(this, presetName);
        }

        public bool IsValidTarget(Mesh targetMesh)
        {
            return targetMesh != null &&
                   (m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                   .Any(mapping => mapping != null && mapping.MatchesUnityMesh(targetMesh));
        }

        public bool SanitizeShapeNames()
        {
            return GetFeature<BlendShapeFeatureObject>()?.SanitizeShapeNames() ?? false;
        }

        public int InferFbxControlPointCount()
        {
            if (m_FbxControlPointCount > 0)
            {
                return m_FbxControlPointCount;
            }

            int maxIndex = -1;
            foreach (var blendShape in BlendShapes)
            {
                foreach (var frame in blendShape?.m_FbxBlendShapeData?.m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
                {
                    if (frame?.m_PointsIndices == null || frame.m_PointsIndices.Count == 0)
                    {
                        continue;
                    }

                    maxIndex = Mathf.Max(maxIndex, frame.m_PointsIndices.Max());
                }
            }

            m_FbxControlPointCount = maxIndex >= 0 ? maxIndex + 1 : -1;
            return m_FbxControlPointCount;
        }

        private BlendShapeFeatureObject GetOrCreateBlendShapeFeature()
        {
            var feature = GetFeature<BlendShapeFeatureObject>();
            if (feature != null)
            {
                return feature;
            }

            feature = CreateInstance<BlendShapeFeatureObject>();
            AddFeature(feature);
            return feature;
        }
    }
}
