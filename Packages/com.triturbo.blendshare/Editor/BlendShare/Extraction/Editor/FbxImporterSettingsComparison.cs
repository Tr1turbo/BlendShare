using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    public sealed class FbxImporterSettingsComparison
    {
        public bool CanCompare;
        public bool GlobalScaleMatches;
        public bool BakeAxisConversionMatches;
        public float OriginGlobalScale;
        public float SourceGlobalScale;
        public bool OriginBakeAxisConversion;
        public bool SourceBakeAxisConversion;
        public string Message;

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
                result.Message = "Assign original and source FBX assets to compare importer settings.";
                return result;
            }

            result.CanCompare = true;
            result.OriginGlobalScale = originImporter.globalScale;
            result.SourceGlobalScale = sourceImporter.globalScale;
            result.OriginBakeAxisConversion = originImporter.bakeAxisConversion;
            result.SourceBakeAxisConversion = sourceImporter.bakeAxisConversion;
            result.GlobalScaleMatches = Mathf.Approximately(result.OriginGlobalScale, result.SourceGlobalScale);
            result.BakeAxisConversionMatches =
                result.OriginBakeAxisConversion == result.SourceBakeAxisConversion;
            result.Message = result.Matches
                ? "Geometry importer settings match."
                : "Geometry importer settings differ.";
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
