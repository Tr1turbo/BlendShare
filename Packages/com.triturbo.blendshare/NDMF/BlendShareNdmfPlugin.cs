using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;

[assembly: ExportsPlugin(typeof(Triturbo.BlendShare.NDMF.BlendShareNdmfPlugin))]

namespace Triturbo.BlendShare.NDMF
{
    [RunsOnAllPlatforms]
    internal sealed class BlendShareNdmfPlugin : Plugin<BlendShareNdmfPlugin>
    {
        public override string QualifiedName => "com.triturbo.blendshare";
        public override string DisplayName => "BlendShare";

        protected override void Configure()
        {
            // Run in Generating: after PlatformInit and Resolving (RemoveEditorOnlyPass, MA PlatformFilter,
            // MA ResolveObjectReferences, AAO FetchOriginalState) and strictly before all Transforming/Optimizing
            // plugins mutate meshes or armatures.
            var sequence = InPhase(BuildPhase.Generating);
            sequence.WithRequiredExtension(typeof(AnimatorServicesContext), apply =>
            {
                apply.Run(BlendShareNdmfPass.Instance)
                    .PreviewingWith(new BlendShareNdmfPreview());
            });
            // A normal pass boundary closes animator services before its controller context
            // commits clips. Controller assignment can reset SMR weights by their old indices.
            sequence.Run("Restore BlendShare renderer weights", BlendShareNdmfPass.RestoreRendererWeights);
        }
    }
}
