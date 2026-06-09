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
            var patch = Extract(blendShapeSource, originObject, meshRequests, featureOptions);
            return SavePatch(patch, path, defaultGeneratedAssetName);
        }

        private static BlendShareObject SavePatch(
            BlendShareObject patch,
            string path,
            string defaultGeneratedAssetName)
        {
            if (patch == null)
            {
                return null;
            }

            patch.m_DefaultGeneratedAssetName = defaultGeneratedAssetName;
            patch.m_PatchId = "+BlendShare-" + defaultGeneratedAssetName;
            var saved = BlendShareAssetService.Save(patch, path, patch.Meshes);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return saved;
        }
    }
}
