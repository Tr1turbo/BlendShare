namespace Triturbo.BlendShapeShare.FbxReader
{
    /// <summary>
    /// Specifies which mesh data sections should be decoded from a binary FBX file.
    /// </summary>
    [System.Flags]
    public enum FbxMeshReadOptions
    {
        /// <summary>No mesh data sections are requested.</summary>
        None = 0,

        /// <summary>Read base control point positions from FBX mesh geometry.</summary>
        ControlPointPositions = 1 << 0,

        /// <summary>Read blend shape channels, frame weights, and control point deltas.</summary>
        BlendShapes = 1 << 1,

        /// <summary>Read skin cluster indices and weights for each control point.</summary>
        BoneWeights = 1 << 2,

        /// <summary>Read all supported mesh data sections.</summary>
        All = ControlPointPositions | BlendShapes | BoneWeights
    }

    /// <summary>
    /// Describes one bone influence for a single FBX control point.
    /// </summary>
    public struct FbxControlPointBoneWeight
    {
        /// <summary>Index into <see cref="FbxBoneWeightSnapshot.BoneNames"/>.</summary>
        public int BoneIndex { get; }

        /// <summary>Influence weight assigned by the FBX skin cluster.</summary>
        public float Weight { get; }

        /// <summary>
        /// Creates a bone influence record for one FBX control point.
        /// </summary>
        /// <param name="boneIndex">Index into <see cref="FbxBoneWeightSnapshot.BoneNames"/>.</param>
        /// <param name="weight">Influence weight assigned by the FBX skin cluster.</param>
        public FbxControlPointBoneWeight(int boneIndex, float weight)
        {
            BoneIndex = boneIndex;
            Weight = weight;
        }
    }

    /// <summary>
    /// Snapshot of FBX skinning weights for a mesh, grouped by control point.
    /// </summary>
    public sealed class FbxBoneWeightSnapshot
    {
        /// <summary>Bone names referenced by control point weight records.</summary>
        public string[] BoneNames { get; }

        /// <summary>Bone influences for each FBX control point.</summary>
        public FbxControlPointBoneWeight[][] ControlPointWeights { get; }

        /// <summary>True when at least one bone and one control point weight array were read.</summary>
        public bool HasWeights => BoneNames.Length > 0 && ControlPointWeights.Length > 0;

        /// <summary>
        /// Creates a snapshot of FBX skinning weights.
        /// </summary>
        /// <param name="boneNames">Bone names referenced by control point weight records.</param>
        /// <param name="controlPointWeights">Bone influences for each FBX control point.</param>
        public FbxBoneWeightSnapshot(
            string[] boneNames,
            FbxControlPointBoneWeight[][] controlPointWeights)
        {
            BoneNames = boneNames ?? System.Array.Empty<string>();
            ControlPointWeights = controlPointWeights ?? System.Array.Empty<FbxControlPointBoneWeight[]>();
        }
    }

    /// <summary>
    /// Snapshot of one FBX blend shape channel and its frames.
    /// </summary>
    public sealed class FbxBlendShapeSnapshot
    {
        /// <summary>Name of the parent FBX blend shape deformer.</summary>
        public string DeformerName { get; }

        /// <summary>Name of the FBX blend shape channel.</summary>
        public string BlendShapeName { get; }

        /// <summary>Frames attached to the blend shape channel.</summary>
        public FbxBlendShapeFrameSnapshot[] Frames { get; }

        /// <summary>
        /// Creates a snapshot of one FBX blend shape channel.
        /// </summary>
        /// <param name="deformerName">Name of the parent FBX blend shape deformer.</param>
        /// <param name="blendShapeName">Name of the FBX blend shape channel.</param>
        /// <param name="frames">Frames attached to the blend shape channel.</param>
        public FbxBlendShapeSnapshot(
            string deformerName,
            string blendShapeName,
            FbxBlendShapeFrameSnapshot[] frames)
        {
            DeformerName = deformerName;
            BlendShapeName = blendShapeName;
            Frames = frames ?? System.Array.Empty<FbxBlendShapeFrameSnapshot>();
        }
    }

    /// <summary>
    /// Snapshot of one FBX blend shape frame.
    /// </summary>
    public sealed class FbxBlendShapeFrameSnapshot
    {
        /// <summary>Frame weight stored by the FBX blend shape channel.</summary>
        public double FrameWeight { get; }

        /// <summary>Control point indices with non-zero deltas in this frame.</summary>
        public int[] ControlPointIndices { get; }

        /// <summary>Position deltas for <see cref="ControlPointIndices"/>.</summary>
        public Vector3d[] ControlPointDeltas { get; }

        /// <summary>
        /// Creates a snapshot of one FBX blend shape frame.
        /// </summary>
        /// <param name="frameWeight">Frame weight stored by the FBX blend shape channel.</param>
        /// <param name="controlPointIndices">Control point indices with non-zero deltas in this frame.</param>
        /// <param name="controlPointDeltas">Position deltas for <paramref name="controlPointIndices"/>.</param>
        public FbxBlendShapeFrameSnapshot(
            double frameWeight,
            int[] controlPointIndices,
            Vector3d[] controlPointDeltas)
        {
            FrameWeight = frameWeight;
            ControlPointIndices = controlPointIndices ?? System.Array.Empty<int>();
            ControlPointDeltas = controlPointDeltas ?? System.Array.Empty<Vector3d>();
        }
    }

    /// <summary>
    /// Snapshot of mesh data decoded from a binary FBX mesh geometry.
    /// </summary>
    public sealed class FbxMeshSnapshot
    {
        /// <summary>
        /// Name of the FBX mesh geometry object. This is distinct from the owning node name.
        /// </summary>
        public string MeshName { get; }

        /// <summary>
        /// Root-relative path of the FBX model node that owns this mesh geometry.
        /// </summary>
        public string NodePath { get; }

        /// <summary>
        /// Name of the owning FBX model node, derived from the last segment of <see cref="NodePath"/>.
        /// </summary>
        public string NodeName
        {
            get
            {
                if (string.IsNullOrEmpty(NodePath))
                {
                    return null;
                }

                int separatorIndex = NodePath.LastIndexOf('/');
                return separatorIndex >= 0
                    ? NodePath.Substring(separatorIndex + 1)
                    : NodePath;
            }
        }

        /// <summary>Base control point positions from the FBX mesh geometry.</summary>
        public Vector3d[] ControlPointPositions { get; }

        /// <summary>Blend shape channels decoded for the mesh.</summary>
        public FbxBlendShapeSnapshot[] BlendShapes { get; }

        /// <summary>Skinning weights decoded for the mesh, or null when unavailable or not requested.</summary>
        public FbxBoneWeightSnapshot BoneWeights { get; }

        /// <summary>Data sections that were requested and decoded for this snapshot.</summary>
        public FbxMeshReadOptions ReadOptions { get; }

        /// <summary>Unity model importer scale for the source FBX asset.</summary>
        public float ImportScale { get; }

        /// <summary>Number of base control points in <see cref="ControlPointPositions"/>.</summary>
        public int ControlPointCount => ControlPointPositions.Length;

        /// <summary>
        /// Creates a mesh snapshot from decoded FBX mesh data.
        /// </summary>
        /// <param name="meshName">Name of the FBX mesh geometry object.</param>
        /// <param name="controlPointPositions">Base control point positions from the FBX mesh geometry.</param>
        /// <param name="blendShapes">Blend shape channels decoded for the mesh.</param>
        /// <param name="importScale">Unity model importer scale for the source FBX asset.</param>
        /// <param name="boneWeights">Skinning weights decoded for the mesh, or null when unavailable.</param>
        /// <param name="readOptions">Data sections that were requested and decoded for this snapshot.</param>
        /// <param name="nodePath">Root-relative path of the FBX model node that owns this mesh geometry.</param>
        public FbxMeshSnapshot(
            string meshName,
            Vector3d[] controlPointPositions,
            FbxBlendShapeSnapshot[] blendShapes,
            float importScale,
            FbxBoneWeightSnapshot boneWeights = null,
            FbxMeshReadOptions readOptions = FbxMeshReadOptions.All,
            string nodePath = null)
        {
            MeshName = meshName;
            NodePath = nodePath;
            ControlPointPositions = controlPointPositions ?? System.Array.Empty<Vector3d>();
            BlendShapes = blendShapes ?? System.Array.Empty<FbxBlendShapeSnapshot>();
            ImportScale = importScale;
            BoneWeights = boneWeights;
            ReadOptions = readOptions;
        }
    }
}
