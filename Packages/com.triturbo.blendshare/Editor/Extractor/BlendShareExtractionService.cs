using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Triturbo.BlendShapeShare.BlendShapeData;

namespace Triturbo.BlendShapeShare.Extractor
{
    public static class BlendShareExtractionService
    {
        public static BlendShareObject Extract(
            GameObject blendShapeSource,
            GameObject originObject,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshRequests,
            MeshFeatureExtractionOptionsSet featureOptions)
        {
            return new MeshFeatureExtractionPipeline().Extract(
                blendShapeSource,
                originObject,
                meshRequests,
                featureOptions);
        }

        public static BlendShareObject ExtractAndSave(
            GameObject blendShapeSource,
            GameObject originObject,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshRequests,
            MeshFeatureExtractionOptionsSet featureOptions,
            string path,
            string defaultGeneratedAssetName)
        {
            var blendShare = Extract(blendShapeSource, originObject, meshRequests, featureOptions);
            return SaveBlendShare(blendShare, path, defaultGeneratedAssetName);
        }

        public static BlendShareObject ExtractBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options)
        {
            return ExtractBlendShapes(blendShapeSource, originObject, meshDataList, options, null);
        }

        public static BlendShareObject ExtractBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options,
            MeshFeatureExtractionOptionsSet featureOptions)
        {
            var optionsSet = CreateBlendShapeOptions(meshDataList, options, originObject, featureOptions);
            var requests = CreateMeshRequests(meshDataList, originObject);
            return new MeshFeatureExtractionPipeline().Extract(
                blendShapeSource,
                originObject,
                requests,
                optionsSet);
        }

        public static BlendShareObject ExtractAndSaveBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options,
            string path,
            string defaultGeneratedAssetName)
        {
            return ExtractAndSaveBlendShapes(
                blendShapeSource,
                originObject,
                meshDataList,
                options,
                path,
                defaultGeneratedAssetName,
                null);
        }

        public static BlendShareObject ExtractAndSaveBlendShapes(
            GameObject blendShapeSource,
            GameObject originObject,
            List<MeshData> meshDataList,
            BlendShapesExtractorOptions options,
            string path,
            string defaultGeneratedAssetName,
            MeshFeatureExtractionOptionsSet featureOptions)
        {
            var blendShare = ExtractBlendShapes(blendShapeSource, originObject, meshDataList, options, featureOptions);
            return SaveBlendShare(blendShare, path, defaultGeneratedAssetName);
        }

        private static BlendShareObject SaveBlendShare(
            BlendShareObject blendShare,
            string path,
            string defaultGeneratedAssetName)
        {
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

        private static MeshFeatureExtractionOptionsSet CreateBlendShapeOptions(
            IEnumerable<MeshData> meshDataList,
            BlendShapesExtractorOptions legacyOptions,
            GameObject originObject,
            MeshFeatureExtractionOptionsSet featureOptions = null)
        {
            var optionsSet = featureOptions ?? new MeshFeatureExtractionOptionsSet();
            var blendShapeOptions = new BlendShapeExtractionOptions
            {
                BaseMesh = legacyOptions != null && legacyOptions.baseMesh == BlendShapesExtractorOptions.BaseMesh.Original
                    ? BlendShapeBaseMesh.Original
                    : BlendShapeBaseMesh.Source,
                WeldVertices = legacyOptions?.weldVertices ?? true,
                ApplyRotation = legacyOptions?.applyRotation ?? false,
                ApplyScale = legacyOptions?.applyScale ?? false,
                ApplyTranslate = legacyOptions?.applyTranslate ?? false,
                BlendShapeScale = legacyOptions?.blendShapesScale ?? 1f
            };

            var pathSource = new UnityMeshExtractionSource(originObject);
            foreach (var meshData in meshDataList ?? Enumerable.Empty<MeshData>())
            {
                if (meshData == null)
                {
                    continue;
                }

                string meshPath = pathSource.GetMeshPath(meshData.m_MeshName);
                blendShapeOptions.SetSelectedBlendShapeNames(meshPath, meshData.m_MeshName, meshData.m_ShapeNames);
            }

            optionsSet.Set(blendShapeOptions);
            return optionsSet;
        }

        private static List<MeshFeatureExtractionMeshRequest> CreateMeshRequests(
            IEnumerable<MeshData> meshDataList,
            GameObject originObject)
        {
            var pathSource = new UnityMeshExtractionSource(originObject);
            return (meshDataList ?? Enumerable.Empty<MeshData>())
                .Where(meshData => meshData != null)
                .Select(meshData => new MeshFeatureExtractionMeshRequest(
                    pathSource.GetMeshPath(meshData.m_MeshName),
                    meshData.m_MeshName))
                .GroupBy(request => MeshFeatureExtractionSession.BuildMeshKey(request.MeshPath, request.MeshName))
                .Select(group => group.First())
                .ToList();
        }
    }
}
