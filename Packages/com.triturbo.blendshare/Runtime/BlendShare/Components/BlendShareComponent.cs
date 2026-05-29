using UnityEngine;

#if BS_HAS_VRCSDK3_BASE
using VRC.SDKBase;
#endif

namespace Triturbo.BlendShare.Components
{
    public abstract class BlendShareComponent : MonoBehaviour
    #if BS_HAS_VRCSDK3_BASE
    , IEditorOnly
    #endif
    {
    }
}
