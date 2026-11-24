using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Serialization;

namespace Triturbo.BlendShare.Model.Documents
{
    /// <summary>
    /// Shared armature bone table referenced by ArmatureId from SkinWeightFeature.
    /// Stored once per PatchDocument instead of as hidden ScriptableObject subassets.
    /// </summary>
    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class ArmatureDocument
    {
        [BlendShareField(1)] public string ArmatureId = string.Empty;
        [BlendShareField(2)] public string Name = string.Empty;
        [BlendShareField(3)] public string RootBonePath = string.Empty;
        [BlendShareField(4)] public List<ArmatureBoneRecord> Bones = new List<ArmatureBoneRecord>();

        /// <summary>
        /// Coordinate frame provenance for this armature's bone bind matrices.
        /// Populated with <see cref="MeshSpaceProvenance.OriginRawLocalV2"/> by BlendShare 2.0 extraction;
        /// <c>null</c> indicates a legacy v1 armature whose matrices are stored in <see cref="MeshSpaceProvenance.CanonicalMeshLocalV1"/>.
        /// </summary>
        [BlendShareField(5)] public MeshSpaceProvenance SpaceProvenance;
    }

    [BlendShareRecord(SchemaVersion = 1)]
    public sealed class ArmatureBoneRecord
    {
        [BlendShareField(1)] public string Path = string.Empty;
        [BlendShareField(2)] public string ParentPath = string.Empty;
        [BlendShareField(3)] public FbxMatrix4x4 LocalBindMatrix = FbxMatrix4x4.Identity;
        [BlendShareField(4)] public FbxMatrix4x4 GlobalBindMatrix = FbxMatrix4x4.Identity;
    }
}
