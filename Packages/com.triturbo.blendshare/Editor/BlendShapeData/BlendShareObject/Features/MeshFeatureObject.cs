using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public abstract class MeshFeatureObject : ScriptableObject
    {
        public abstract string FeatureId { get; }

        public virtual void Sanitize(MeshDataObject owner) { }
    }
}
