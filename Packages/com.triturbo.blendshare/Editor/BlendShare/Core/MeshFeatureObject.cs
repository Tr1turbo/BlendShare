using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Base ScriptableObject for one extracted feature stored under a mesh.
    /// </summary>
    [MovedFrom(true, "Triturbo.BlendShapeShare.BlendShapeData", "Triturbo.BlendShapeShare.Data.Editor")]
    public abstract class MeshFeatureObject : ScriptableObject
    {
        public abstract string FeatureId { get; }

        public virtual void Sanitize(MeshDataObject owner) { }
    }
}
