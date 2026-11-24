using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Inspection
{
    public enum FbxImporterComparisonStatus
    {
        MissingAssets = 0,
        Match = 1,
        Different = 2
    }

    /// <summary>
    /// Compares Unity <see cref="ModelImporter"/> geometry settings between Origin and Source FBX assets.
    /// Kept independent of UI localization so <c>Pipeline.Editor</c> stays free of <c>UI.Editor</c>.
    /// </summary>
    public sealed class FbxImporterSettingsComparison
    {
        public FbxImporterComparisonStatus Status { get; set; }
        public bool CanCompare { get; set; }
        public bool GlobalScaleMatches { get; set; }
        public bool BakeAxisConversionMatches { get; set; }
        public float OriginGlobalScale { get; set; }
        public float SourceGlobalScale { get; set; }
        public bool OriginBakeAxisConversion { get; set; }
        public bool SourceBakeAxisConversion { get; set; }
        public string Message { get; set; } = string.Empty;

        public bool Matches => CanCompare && GlobalScaleMatches && BakeAxisConversionMatches;
        public bool HasDifferences => CanCompare && !Matches;

        public static FbxImporterSettingsComparison Compare(GameObject originFbx, GameObject sourceFbx)
        {
            return Compare(GetModelImporter(originFbx), GetModelImporter(sourceFbx));
        }

        public static FbxImporterSettingsComparison Compare(ModelImporter originImporter, ModelImporter sourceImporter)
        {
            var result = new FbxImporterSettingsComparison();
            if (originImporter == null || sourceImporter == null)
            {
                result.Status = FbxImporterComparisonStatus.MissingAssets;
                result.Message = "Assign both Original and Source FBX assets to compare ModelImporter settings.";
                return result;
            }

            result.CanCompare = true;
            result.OriginGlobalScale = originImporter.globalScale;
            result.SourceGlobalScale = sourceImporter.globalScale;
            result.OriginBakeAxisConversion = originImporter.bakeAxisConversion;
            result.SourceBakeAxisConversion = sourceImporter.bakeAxisConversion;
            result.GlobalScaleMatches = Mathf.Approximately(result.OriginGlobalScale, result.SourceGlobalScale);
            result.BakeAxisConversionMatches = result.OriginBakeAxisConversion == result.SourceBakeAxisConversion;
            result.Status = result.Matches ? FbxImporterComparisonStatus.Match : FbxImporterComparisonStatus.Different;
            result.Message = result.Matches
                ? "ModelImporter geometry settings match."
                : "ModelImporter geometry settings differ between Original and Source FBX.";
            return result;
        }

        public static bool CopyGeometrySettings(GameObject originFbx, GameObject sourceFbx)
        {
            var originImporter = GetModelImporter(originFbx);
            var sourceImporter = GetModelImporter(sourceFbx);
            if (originImporter == null || sourceImporter == null)
            {
                return false;
            }

            sourceImporter.globalScale = originImporter.globalScale;
            sourceImporter.bakeAxisConversion = originImporter.bakeAxisConversion;
            sourceImporter.SaveAndReimport();
            return true;
        }

        private static ModelImporter GetModelImporter(GameObject fbx)
        {
            if (fbx == null)
            {
                return null;
            }

            string path = AssetDatabase.GetAssetPath(fbx);
            return string.IsNullOrEmpty(path)
                ? null
                : AssetImporter.GetAtPath(path) as ModelImporter;
        }
    }
}
