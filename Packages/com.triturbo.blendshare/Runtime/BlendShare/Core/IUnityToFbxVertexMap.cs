namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Narrow read-only mapping contract consumed by Unity mesh feature generators.
    /// Implemented by serialized <see cref="UnityVertexMappingObject"/> assets and transient remapped maps.
    /// </summary>
    public interface IUnityToFbxVertexMap
    {
        /// <summary>Number of Unity mesh vertices covered by this mapping.</summary>
        int UnityVertexCount { get; }

        /// <summary>FBX-to-Unity coordinate space conversion matching the mapped Unity mesh.</summary>
        FbxUnitySpaceConversion SpaceConversion { get; }

        /// <summary>Gets the FBX control-point index group mapped to <paramref name="unityVertexIndex"/>.</summary>
        bool TryGetFbxGroup(int unityVertexIndex, out FbxIndexGroup group);
    }
}
