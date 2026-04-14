using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Triturbo.BlendShapeShare.BlendShapeData;

namespace Triturbo.BlendShapeShare.Extractor
{
    public static class BlendShareExtractionService
    {
        public static BlendShareObject ExtractBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options)
        {
            var legacy = BlendShapesExtractor.ExtractBlendShapes(blendShapeSource, originObject, meshDataList, options);
            return legacy == null ? null : BlendShareUpgradeService.ConvertLegacy(legacy);
        }

        public static BlendShareObject ExtractAndSaveBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options,
            string path,
            string defaultGeneratedAssetName)
        {
            var blendShare = ExtractBlendShapes(blendShapeSource, originObject, meshDataList, options);
            if (blendShare == null)
            {
                return null;
            }

            blendShare.m_DefaultGeneratedAssetName = defaultGeneratedAssetName;
            blendShare.m_DeformerID = "+BlendShare-" + defaultGeneratedAssetName;
            var saved = BlendShareAssetService.Save(blendShare, path, blendShare.Meshes);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return saved;
        }
    }
}
