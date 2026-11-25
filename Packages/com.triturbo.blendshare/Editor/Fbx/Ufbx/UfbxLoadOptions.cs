using System;

namespace Triturbo.BlendShare.Fbx.Ufbx
{
    /// <summary>
    /// Immutable options controlling how <see cref="UfbxScene"/> loads an FBX source.
    /// </summary>
    public readonly struct UfbxLoadOptions
    {
        /// <summary>Default load options optimized for static mesh, skeleton, skin, and blendshape reading.</summary>
        public static UfbxLoadOptions Default => new UfbxLoadOptions(
            ignoreAnimation: true,
            ignoreEmbedded: true,
            generateMissingNormals: false,
            loadExternalFiles: false,
            retainVertexAttribW: true,
            targetAxes: null,
            targetUnitMeters: null);

        /// <summary>Gets whether animation curves are skipped during load.</summary>
        public bool IgnoreAnimation { get; }

        /// <summary>Gets whether embedded binary content (such as embedded textures) is skipped during load.</summary>
        public bool IgnoreEmbedded { get; }

        /// <summary>Gets whether ufbx generates normals for meshes that lack authored normals.</summary>
        public bool GenerateMissingNormals { get; }

        /// <summary>Gets whether ufbx loads referenced external files.</summary>
        public bool LoadExternalFiles { get; }

        /// <summary>Gets whether optional fourth-component W values are retained for vertex attributes.</summary>
        public bool RetainVertexAttribW { get; }

        /// <summary>Gets the optional target coordinate axes requested for ufbx space conversion.</summary>
        public FbxCoordinateAxes? TargetAxes { get; }

        /// <summary>Gets the optional target world unit in meters requested for ufbx unit conversion.</summary>
        public double? TargetUnitMeters { get; }

        /// <summary>Creates a new immutable <see cref="UfbxLoadOptions"/> value.</summary>
        public UfbxLoadOptions(
            bool ignoreAnimation = true,
            bool ignoreEmbedded = true,
            bool generateMissingNormals = false,
            bool loadExternalFiles = false,
            bool retainVertexAttribW = true,
            FbxCoordinateAxes? targetAxes = null,
            double? targetUnitMeters = null)
        {
            if (targetAxes.HasValue && !IsValidCoordinateAxes(targetAxes.Value))
            {
                throw new ArgumentException("Target coordinate axes must specify three distinct orthogonal X, Y, and Z axes.", nameof(targetAxes));
            }

            if (targetUnitMeters.HasValue &&
                (double.IsNaN(targetUnitMeters.Value) || double.IsInfinity(targetUnitMeters.Value) || !(targetUnitMeters.Value > 0d)))
            {
                throw new ArgumentOutOfRangeException(nameof(targetUnitMeters), "Target unit meters must be a finite positive number.");
            }

            IgnoreAnimation = ignoreAnimation;
            IgnoreEmbedded = ignoreEmbedded;
            GenerateMissingNormals = generateMissingNormals;
            LoadExternalFiles = loadExternalFiles;
            RetainVertexAttribW = retainVertexAttribW;
            TargetAxes = targetAxes;
            TargetUnitMeters = targetUnitMeters;
        }

        internal static bool IsValidCoordinateAxes(FbxCoordinateAxes axes)
        {
            int right = (int)axes.Right;
            int up = (int)axes.Up;
            int front = (int)axes.Front;
            if (right < 0 || right >= (int)FbxCoordinateAxis.Unknown ||
                up < 0 || up >= (int)FbxCoordinateAxis.Unknown ||
                front < 0 || front >= (int)FbxCoordinateAxis.Unknown)
            {
                return false;
            }

            // Right, Up, and Front must span the three distinct X/Y/Z axis dimensions.
            int rightDim = right / 2;
            int upDim = up / 2;
            int frontDim = front / 2;
            return rightDim != upDim && rightDim != frontDim && upDim != frontDim;
        }

        /// <summary>Returns a copy with <see cref="IgnoreAnimation"/> updated.</summary>
        public UfbxLoadOptions WithIgnoreAnimation(bool value) =>
            new UfbxLoadOptions(value, IgnoreEmbedded, GenerateMissingNormals, LoadExternalFiles, RetainVertexAttribW, TargetAxes, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="IgnoreEmbedded"/> updated.</summary>
        public UfbxLoadOptions WithIgnoreEmbedded(bool value) =>
            new UfbxLoadOptions(IgnoreAnimation, value, GenerateMissingNormals, LoadExternalFiles, RetainVertexAttribW, TargetAxes, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="GenerateMissingNormals"/> updated.</summary>
        public UfbxLoadOptions WithGenerateMissingNormals(bool value) =>
            new UfbxLoadOptions(IgnoreAnimation, IgnoreEmbedded, value, LoadExternalFiles, RetainVertexAttribW, TargetAxes, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="LoadExternalFiles"/> updated.</summary>
        public UfbxLoadOptions WithLoadExternalFiles(bool value) =>
            new UfbxLoadOptions(IgnoreAnimation, IgnoreEmbedded, GenerateMissingNormals, value, RetainVertexAttribW, TargetAxes, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="RetainVertexAttribW"/> updated.</summary>
        public UfbxLoadOptions WithRetainVertexAttribW(bool value) =>
            new UfbxLoadOptions(IgnoreAnimation, IgnoreEmbedded, GenerateMissingNormals, LoadExternalFiles, value, TargetAxes, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="TargetAxes"/> updated.</summary>
        public UfbxLoadOptions WithTargetAxes(FbxCoordinateAxes? value) =>
            new UfbxLoadOptions(IgnoreAnimation, IgnoreEmbedded, GenerateMissingNormals, LoadExternalFiles, RetainVertexAttribW, value, TargetUnitMeters);

        /// <summary>Returns a copy with <see cref="TargetUnitMeters"/> updated.</summary>
        public UfbxLoadOptions WithTargetUnitMeters(double? value) =>
            new UfbxLoadOptions(IgnoreAnimation, IgnoreEmbedded, GenerateMissingNormals, LoadExternalFiles, RetainVertexAttribW, TargetAxes, value);
    }
}
