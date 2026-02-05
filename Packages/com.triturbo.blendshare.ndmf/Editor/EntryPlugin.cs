using nadena.dev.ndmf;
using Triturbo.BlendShapeShare.Ndmf.Editor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(EntryPlugin))]
namespace Triturbo.BlendShapeShare.Ndmf.Editor
{
    public class EntryPlugin : Plugin<EntryPlugin>
    {
        public override string QualifiedName => "Triturbo.BlendShapeShare.Ndmf";
        public override string DisplayName => "BlendShapeShare Ndmf Plugin";
        public override Color? ThemeColor => new Color(0x56 / 255f, 0xca / 255f, 0xee / 255f, 1);
        
        protected override void Configure()
        {
            var seq = InPhase(BuildPhase.FirstChance);

            seq.Run(AppendBlendShapesPass.Instance);
        }

        class AppendBlendShapesPass : Pass<AppendBlendShapesPass>
        {
            protected override void Execute(BuildContext context)
            {
                new AppendBlendShapesHook().Process(context);
            }
        }
    }
}