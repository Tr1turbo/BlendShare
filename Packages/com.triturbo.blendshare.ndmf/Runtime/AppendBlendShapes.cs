using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Ndmf.Runtime
{
    [Serializable]
    public class BlendShapeWeightEntry
    {
        public string shapeName;
        public float weight;
        
        public BlendShapeWeightEntry(string name, float w = 0f)
        {
            shapeName = name;
            weight = w;
        }
    }
    
    [Serializable]
    public class MeshWeightGroup
    {
        public string meshName;
        public bool isExpanded = true;
        public List<BlendShapeWeightEntry> weights = new();
    }
    
    [Serializable]
    public class BlendShapeDataEntry
    {
        public BlendShapeDataSO blendShapeData;
        public bool isExpanded = true;
        public List<MeshWeightGroup> meshWeightGroups = new();
        
        public void SyncWeightsWithData()
        {
            if (!blendShapeData || blendShapeData.m_MeshDataList == null)
            {
                meshWeightGroups.Clear();
                return;
            }
            
            var existingWeights = new Dictionary<string, Dictionary<string, float>>();
            foreach (var group in meshWeightGroups)
            {
                if (string.IsNullOrEmpty(group.meshName)) continue;
                var shapeWeights = new Dictionary<string, float>();
                foreach (var entry in group.weights.Where(e => !string.IsNullOrEmpty(e.shapeName)))
                {
                    shapeWeights[entry.shapeName] = entry.weight;
                }
                existingWeights[group.meshName] = shapeWeights;
            }
            
            meshWeightGroups.Clear();
            foreach (var meshData in blendShapeData.m_MeshDataList)
            {
                if (meshData.m_ShapeNames == null) continue;
                
                var group = new MeshWeightGroup
                {
                    meshName = meshData.m_MeshName,
                    isExpanded = true
                };
                
                existingWeights.TryGetValue(meshData.m_MeshName, out var meshWeights);
                
                foreach (var shapeName in meshData.m_ShapeNames)
                {
                    var w = 0f;
                    if (meshWeights != null && meshWeights.TryGetValue(shapeName, out var existing))
                        w = existing;
                    group.weights.Add(new BlendShapeWeightEntry(shapeName, w));
                }
                
                meshWeightGroups.Add(group);
            }
        }
        
        public Dictionary<string, float> GetWeightsDictionary()
        {
            var dict = new Dictionary<string, float>();
            foreach (var entry in meshWeightGroups
                         .SelectMany(group => group.weights.Where(entry => !string.IsNullOrEmpty(entry.shapeName) && !dict.ContainsKey(entry.shapeName))))
                dict[entry.shapeName] = entry.weight;
            
            return dict;
        }
    }

    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("BlendShare/Append BlendShapes")]
    public class AppendBlendShapes : MonoBehaviour, INDMFEditorOnly
    {
        public List<BlendShapeDataEntry> blendShapeDataEntries = new();
        public GameObject target;
        
        private void Reset()
        {
            target = gameObject;
        }
        
        public Dictionary<string, float> GetAllWeights()
        {
            var result = new Dictionary<string, float>();
            foreach (var kv in blendShapeDataEntries.Select(entry => entry.GetWeightsDictionary()).SelectMany(weights => weights))
            {
                result[kv.Key] = kv.Value;
            }
            return result;
        }
        
        public List<BlendShapeDataSO> GetAllBlendShapeData()
        {
            return (from entry in blendShapeDataEntries where entry.blendShapeData != null select entry.blendShapeData).ToList();
        }
    }
}