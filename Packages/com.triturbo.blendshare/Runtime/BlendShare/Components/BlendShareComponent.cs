using UnityEngine;

namespace Triturbo.BlendShare.Components
{
    /// <summary>
    /// Base MonoBehaviour marker for BlendShare runtime/authoring avatar components.
    /// Zero Editor, FBX, or NDMF dependencies so standalone Player builds always succeed cleanly.
    /// </summary>
    public abstract class BlendShareComponent : MonoBehaviour
    {
    }
}
