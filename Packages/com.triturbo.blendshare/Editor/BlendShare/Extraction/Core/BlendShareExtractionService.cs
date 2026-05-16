using System.Collections.Generic;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Public entry point for extracting BlendShare assets from source and origin FBX assets.
    /// </summary>
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
    }
}
