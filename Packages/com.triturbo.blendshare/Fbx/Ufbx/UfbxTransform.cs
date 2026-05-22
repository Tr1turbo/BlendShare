using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public readonly struct UfbxTransform
    {
        public static readonly UfbxTransform Identity = new UfbxTransform(Vector3d.zero, Quaterniond.Identity, Vector3d.one);

        public Vector3d Translation { get; }
        public Quaterniond Rotation { get; }
        public Vector3d Scale { get; }
        public FbxMatrix4x4 LocalMatrix => FbxMatrix4x4.FromTranslationRotationScale(Translation, Rotation, Scale);

        public UfbxTransform(Vector3d translation, Quaterniond rotation, Vector3d scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }
    }

}
