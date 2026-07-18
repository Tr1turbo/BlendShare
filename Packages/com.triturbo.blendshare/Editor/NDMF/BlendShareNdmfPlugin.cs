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
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .WithRequiredExtension(typeof(AnimatorServicesContext), sequence =>
                {
                    sequence.Run(BlendShareNdmfPass.Instance)
                        .PreviewingWith(new BlendShareNdmfPreview());
                });

            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("nadena.dev.modular-avatar")
                .WithRequiredExtension(typeof(AnimatorServicesContext), sequence =>
                {
                    sequence.Run(BlendShareBoneMergePass.Instance);
                });
        }
    }
}
