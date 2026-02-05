using System;
using System.Security.Cryptography;
using System.Text;
using nadena.dev.ndmf;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Triturbo.BlendShapeShare.Ndmf.Runtime
{
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("BlendShare/Append BlendShapes")]
    public class AppendBlendShapes : MonoBehaviour, INDMFEditorOnly
    {
        public BlendShapeDataSO[] blendShapeData;
        public float[] blendShapeWeights;
        public GameObject target;
        
        private void Reset()
        {
            target = gameObject;
        }
    }
}