using System;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    public abstract class UfbxElement
    {
        protected UfbxElement(UfbxScene scene, UfbxElementType elementType, int index, long id, string name)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            ElementType = elementType;
            Index = index;
            Id = id;
            Name = name ?? string.Empty;
        }

        public long Id { get; }
        public string Name { get; }
        public UfbxElementType ElementType { get; }
        internal UfbxScene Scene { get; }
        internal int Index { get; }

        protected void EnsureAlive()
        {
            Scene.EnsureAlive();
        }
    }

}
