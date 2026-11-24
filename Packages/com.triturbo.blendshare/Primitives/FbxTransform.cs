namespace Triturbo.BlendShare.Fbx
{
    /// <summary>An evaluated translation, rotation and scale.</summary>
    public readonly struct FbxTransform
    {
        /// <summary>Identity.</summary>
        public static readonly FbxTransform Identity = new FbxTransform(Vector3d.zero, Quaterniond.Identity, Vector3d.one);

        /// <summary>Translation component of the evaluated transform.</summary>
        public Vector3d Position { get; }
        /// <summary>Rotation.</summary>
        public Quaterniond Rotation { get; }
        /// <summary>Scale factors along the local coordinate axes.</summary>
        public Vector3d Scale { get; }
        /// <summary>Row-vector matrix composed from the evaluated scale, rotation and translation.</summary>
        public FbxMatrix4x4 LocalMatrix => FbxMatrix4x4.FromTranslationRotationScale(Position, Rotation, Scale);

        /// <summary>Creates a new FbxTransform from the supplied values.</summary>
        public FbxTransform(Vector3d position, Quaterniond rotation, Vector3d scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    /// <summary>Authored FBX transform properties before matrix evaluation.</summary>
    public readonly struct FbxNodeRawTransform
    {
        /// <summary>Lcl translation.</summary>
        public readonly Vector3d LclTranslation;
        /// <summary>Authored local Euler angles in degrees.</summary>
        public readonly Vector3d LclRotation;
        /// <summary>Lcl scaling.</summary>
        public readonly Vector3d LclScaling;
        /// <summary>Rotation order.</summary>
        public readonly FbxRotationOrder RotationOrder;
        /// <summary>Inherit mode.</summary>
        public readonly FbxTransformInheritMode InheritMode;
        /// <summary>Inheritance mode recorded in the original file.</summary>
        public readonly FbxTransformInheritMode OriginalInheritMode;
        /// <summary>Rotation active.</summary>
        public readonly bool RotationActive;
        /// <summary>Pre rotation.</summary>
        public readonly Vector3d PreRotation;
        /// <summary>Post rotation.</summary>
        public readonly Vector3d PostRotation;
        /// <summary>Rotation pivot.</summary>
        public readonly Vector3d RotationPivot;
        /// <summary>Scaling pivot.</summary>
        public readonly Vector3d ScalingPivot;
        /// <summary>Rotation offset.</summary>
        public readonly Vector3d RotationOffset;
        /// <summary>Scaling offset.</summary>
        public readonly Vector3d ScalingOffset;

        /// <summary>Creates a new FbxNodeRawTransform from the supplied values.</summary>
        public FbxNodeRawTransform(
            Vector3d lclTranslation,
            Vector3d lclRotation,
            Vector3d lclScaling,
            FbxRotationOrder rotationOrder,
            FbxTransformInheritMode inheritMode,
            FbxTransformInheritMode originalInheritMode,
            bool rotationActive,
            Vector3d preRotation,
            Vector3d postRotation,
            Vector3d rotationPivot,
            Vector3d scalingPivot,
            Vector3d rotationOffset,
            Vector3d scalingOffset)
        {
            LclTranslation = lclTranslation;
            LclRotation = lclRotation;
            LclScaling = lclScaling;
            RotationOrder = rotationOrder;
            InheritMode = inheritMode;
            OriginalInheritMode = originalInheritMode;
            RotationActive = rotationActive;
            PreRotation = preRotation;
            PostRotation = postRotation;
            RotationPivot = rotationPivot;
            ScalingPivot = scalingPivot;
            RotationOffset = rotationOffset;
            ScalingOffset = scalingOffset;
        }
    }
}
