using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Optional Unity <c>ModelImporter</c> and scene hints for <see cref="FbxUnityInverseMapper"/>.
    /// Mapping still succeeds without hints by inferring scale and axis orientation from geometry.
    /// </summary>
    public readonly struct FbxInverseMapHints
    {
        /// <summary>No hints; infer all importer space parameters from geometry.</summary>
        public static readonly FbxInverseMapHints None = default;

        /// <summary>Optional effective Unity vertex import scale (<c>globalScale * fileScale</c>).</summary>
        public float? Scale { get; }

        /// <summary>Optional <c>ModelImporter.bakeAxisConversion</c> setting.</summary>
        public bool? BakeAxisConversion { get; }

        /// <summary>Optional <c>ModelImporter.weldVertices</c> setting.</summary>
        public bool? WeldVertices { get; }

        /// <summary>Optional <c>ModelImporter.swapUVChannels</c> setting.</summary>
        public bool? SwapUvChannels { get; }

        /// <summary>Optional source FBX scene coordinate axes (enables baked non-Maya axis resolution on <see cref="IFbxScene"/> overrides).</summary>
        public FbxCoordinateAxes? SceneAxes { get; }

        /// <summary>Optional target mesh node path in the FBX scene (prioritizes that instance's <c>GeometryToNodeMatrix</c>).</summary>
        public string NodePath { get; }

        /// <summary>Creates an optional hint bundle for FBX-to-Unity inverse mapping.</summary>
        public FbxInverseMapHints(
            float? scale = null,
            bool? bakeAxisConversion = null,
            bool? weldVertices = null,
            bool? swapUvChannels = null,
            FbxCoordinateAxes? sceneAxes = null,
            string nodePath = null)
        {
            Scale = scale;
            BakeAxisConversion = bakeAxisConversion;
            WeldVertices = weldVertices;
            SwapUvChannels = swapUvChannels;
            SceneAxes = sceneAxes;
            NodePath = nodePath;
        }

        /// <summary>Builds inverse-mapping hints from a <see cref="FbxUnityImportSettings"/> snapshot and optional scene context.</summary>
        public static FbxInverseMapHints FromImportSettings(
            FbxUnityImportSettings settings,
            FbxCoordinateAxes? sceneAxes = null,
            string nodePath = null)
        {
            if (!settings.HasImporter)
            {
                return new FbxInverseMapHints(sceneAxes: sceneAxes, nodePath: nodePath);
            }

            float effectiveScale = settings.EffectiveScale;
            return new FbxInverseMapHints(
                scale: effectiveScale > 0f ? (float?)effectiveScale : null,
                bakeAxisConversion: settings.BakeAxisConversion,
                weldVertices: settings.WeldVertices,
                swapUvChannels: settings.SwapUvChannels,
                sceneAxes: sceneAxes,
                nodePath: nodePath);
        }
    }
}
