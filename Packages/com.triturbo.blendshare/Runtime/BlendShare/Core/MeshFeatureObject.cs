using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Base ScriptableObject for one extracted feature stored under a mesh.
    /// </summary>
    [PreferBinarySerialization]
    public abstract class MeshFeatureObject : ScriptableObject
    {
        public abstract string FeatureId { get; }

        public virtual void Sanitize(MeshDataObject owner) { }
    }
}
