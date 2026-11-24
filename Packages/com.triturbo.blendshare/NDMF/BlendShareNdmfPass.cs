using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Runtime.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareNdmfPass : Pass<BlendShareNdmfPass>
    {
        public override string QualifiedName => "com.triturbo.blendshare.apply";
        public override string DisplayName => "Apply BlendShare";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            var components = root.GetComponentsInChildren<BlendSharePatchMesh>().Where(c => c.isActiveAndEnabled).ToArray();
            var proxies = root.GetComponentsInChildren<BlendSharePatchBoneProxy>().Where(c => c.isActiveAndEnabled).ToArray();
            var mapped = new HashSet<Transform>(proxies.SelectMany(p => p.Bindings).Where(b => b != null).Select(b => b.TargetTransform));
            bool succeeded = true;
            foreach (var group in components.GroupBy(c => c.TargetRenderer))
            {
                var renderer = group.Key;
                if (!renderer)
                {
                    foreach (var component in group)
                        ErrorReport.ReportError(new ApplyError($"Missing target renderer on {component.name}."));
                    succeeded = false;
                    continue;
                }

                var incoming = renderer.sharedMesh;
                var result = BlendShareAvatarApplyService.ApplyToRenderer(new AvatarRendererApplyRequest
                {
                    IncomingMesh = incoming, IncomingBones = renderer.bones, Components = group.ToArray(),
                    BoneProxies = proxies, RendererTransform = renderer.transform, TargetRootTransform = root.transform,
                    CreateMissingBones = true, FlushDirtySettings = true
                });
                if (!result.Succeeded)
                {
                    ErrorReport.ReportError(new ApplyError(string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))));
                    succeeded = false;
                    continue;
                }

                var output = result.MeshResult.OutputMesh;
                var weights = result.ResolvedBlendShapeWeights.ToList();
                for (int i = 0; i < incoming.blendShapeCount; i++)
                    weights.Add(new KeyValuePair<string, float>(incoming.GetBlendShapeName(i), renderer.GetBlendShapeWeight(i)));
                ObjectRegistry.RegisterReplacedObject(incoming, output);
                context.AssetSaver.SaveAsset(output);
                renderer.sharedMesh = output;
                var bones = result.MeshResult.OutputBones;
                if (bones != null && bones.Length > 0)
                {
                    renderer.bones = bones;
                    mapped.UnionWith(bones);
                }
                foreach (var weight in weights)
                {
                    int index = output.GetBlendShapeIndex(weight.Key);
                    if (index >= 0) renderer.SetBlendShapeWeight(index, weight.Value);
                }
            }
            if (!succeeded) return;
            var allAuthoring = root.GetComponentsInChildren<BlendSharePatchMesh>(true).Cast<Component>()
                .Concat(root.GetComponentsInChildren<BlendSharePatchBoneProxy>(true))
                .ToArray();
            foreach (var component in allAuthoring)
            {
                if (!component) continue;
                var host = component.gameObject;
                bool dedicated = host != root && host.transform.childCount == 0 && !mapped.Contains(host.transform)
                    && host.GetComponents<Component>().Length == 2;
                Object.DestroyImmediate(dedicated ? (Object)host : component);
            }
        }

        private sealed class ApplyError : IError
        {
            private readonly string m_Message;
            public ApplyError(string message) => m_Message = message;
            public ErrorSeverity Severity => ErrorSeverity.Error;
            public string ToMessage() => m_Message;
            public VisualElement CreateVisualElement(ErrorReport report) => new Label(m_Message);
            public void AddReference(ObjectReference obj) { }
        }
    }
}
