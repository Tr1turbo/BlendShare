using System;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>Base class for scene-backed ufbx elements.</summary>
    public abstract class UfbxElement : IFbxElement
    {
        /// <summary>Initializes common element metadata.</summary>
        protected UfbxElement(UfbxScene scene, FbxElementType elementType, int index, long id, string name)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            ElementType = elementType;
            Index = index;
            Id = id;
            Name = name ?? string.Empty;
        }

        /// <inheritdoc />
        public long Id { get; }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public FbxElementType ElementType { get; }

        internal UfbxScene Scene { get; }
        internal int Index { get; }

        /// <summary>Acquires the active scene handle or throws <see cref="ObjectDisposedException"/>.</summary>
        internal NativeSceneHandle RequireHandle()
        {
            return Scene.RequireHandle();
        }
    }
}
