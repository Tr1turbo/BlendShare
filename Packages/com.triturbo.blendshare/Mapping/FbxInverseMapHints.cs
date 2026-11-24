using Triturbo.BlendShare.Fbx.Unity;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Optional Unity <c>ModelImporter</c> hints for <see cref="FbxUnityInverseMapper"/>.
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

        /// <summary>Creates an optional hint bundle for FBX-to-Unity inverse mapping.</summary>
        public FbxInverseMapHints(
            float? scale = null,
            bool? bakeAxisConversion = null,
            bool? weldVertices = null)
        {
            Scale = scale;
            BakeAxisConversion = bakeAxisConversion;
            WeldVertices = weldVertices;
        }

        /// <summary>Builds inverse-mapping hints from a <see cref="FbxUnityImportSettings"/> snapshot.</summary>
        public static FbxInverseMapHints FromImportSettings(FbxUnityImportSettings settings)
        {
            if (!settings.HasImporter)
            {
                return None;
            }

            float effectiveScale = settings.EffectiveScale;
            return new FbxInverseMapHints(
                scale: effectiveScale > 0f ? (float?)effectiveScale : null,
                bakeAxisConversion: settings.BakeAxisConversion,
                weldVertices: settings.WeldVertices);
        }
    }
}
