using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    public interface IBlendShareEmbeddedEditor
    {
        Type TargetType { get; }
        string DisplayName { get; }
        VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context);
    }
}
