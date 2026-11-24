namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Scene-backed ufbx material element.</summary>
    public sealed class UfbxMaterial : UfbxElement, IFbxMaterial
    {
        internal UfbxMaterial(UfbxScene scene, int materialIndex, long id, string name)
            : base(scene, FbxElementType.Material, materialIndex, id, name)
        {
        }
    }
}
