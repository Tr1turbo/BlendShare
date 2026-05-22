using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Triturbo.BlendShare.NonDestructive.NDMF.BlendShareNdmfPlugin))]

namespace Triturbo.BlendShare.NonDestructive.NDMF
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
                .Run(BlendShareNdmfPass.Instance)
                .PreviewingWith(new BlendShareNdmfPreview());
        }
    }
}
