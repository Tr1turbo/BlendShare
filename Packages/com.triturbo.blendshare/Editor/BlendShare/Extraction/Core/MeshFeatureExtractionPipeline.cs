using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Hashing;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Coordinates mesh feature extraction and builds the unsaved BlendShare asset graph.
    /// </summary>
    public sealed class MeshFeatureExtractionPipeline
    {
        public BlendShareObject Extract(
            GameObject source,
            GameObject origin,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshes,
            MeshFeatureExtractionOptionsSet options)
        {
            var meshRequests = meshes?
                .Where(mesh => mesh != null)
                .Select(mesh => new MeshFeatureExtractionMeshRequest(mesh.Path))
                .GroupBy(mesh => MeshFeatureExtractionSession.BuildMeshKey(mesh.Path))
                .Select(group => group.First())
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();

            if (source == null || origin == null || meshRequests.Length == 0)
            {
                return null;
            }

            bool rawFbxSdkAccessAllowed = RequiresRawFbxSdk(options);
            using (var session = new MeshFeatureExtractionSession(
                       source,
                       origin,
                       options,
                       meshRequests,
                       rawFbxSdkAccessAllowed))
            {
                foreach (var request in meshRequests)
                {
                    var compatibility = FbxMeshCompatibility.Evaluate(
                        session.GetOriginFbxMesh(request.Path),
                        session.GetRawSourceFbxMesh(request.Path));
                    if (!compatibility.IsCompatible)
                    {
                        Debug.LogError(
                            $"[BlendShare] Cannot extract mesh '{request.Path}': topology compatibility is {compatibility.State} " +
                            $"(Original {compatibility.OriginalControlPointCount}, Source {compatibility.SourceControlPointCount}).");
                        return null;
                    }
                }

                var extractedMeshes = new List<MeshDataObject>();

                foreach (var request in meshRequests)
                {
                    var context = new MeshFeatureExtractionContext(session, request.Path);
                    var meshDataObject = CreateMeshData(context);

                    foreach (var extractor in BlendShareFeatureModules.All.Select(module => module?.Extractor))
                    {
                        if (!ShouldRunExtractor(session.Options, extractor))
                        {
                            continue;
                        }

                        var result = extractor.TryExtract(context, out var feature);
                        if (result.Succeeded)
                        {
                            if (feature != null)
                            {
                                meshDataObject.AddFeature(feature);
                            }
                            else
                            {
                                Debug.LogWarning($"[BlendShare] Extractor '{extractor.FeatureId}' returned success without a feature for mesh '{FormatMesh(context)}'.");
                            }
                        }
                        else if (result.Status == MeshFeatureExtractionStatus.Failed)
                        {
                            Debug.LogError($"[BlendShare] Failed to extract feature '{extractor.FeatureId}' for mesh '{FormatMesh(context)}': {result.Message}");
                        }
                    }

                    if (meshDataObject.Features.Count > 0)
                    {
                        meshDataObject.m_Mappings = BuildMappings(context);
                        extractedMeshes.Add(meshDataObject);
                    }
                }

                if (extractedMeshes.Count == 0)
                {
                    return null;
                }

                var patch = ScriptableObject.CreateInstance<BlendShareObject>();
                patch.m_Target = origin;
                patch.SetMeshes(extractedMeshes);
                return patch;
            }
        }

        private static bool ShouldRunExtractor(
            MeshFeatureExtractionOptionsSet options,
            IMeshFeatureExtractor extractor)
        {
            return options != null &&
                   extractor != null &&
                   options.TryGet(extractor.OptionsType, out var featureOptions) &&
                   featureOptions != null &&
                   featureOptions.Enabled;
        }

        private static bool RequiresRawFbxSdk(MeshFeatureExtractionOptionsSet options)
        {
            return BlendShareFeatureModules.All.Any(module =>
                module != null &&
                module.RequiresRawFbxSdk &&
                ShouldRunExtractor(options, module.Extractor));
        }

        private static MeshDataObject CreateMeshData(MeshFeatureExtractionContext context)
        {
            var meshDataObject = ScriptableObject.CreateInstance<MeshDataObject>();
            var fbxMesh = context.GetOriginFbxMesh() ?? context.GetSourceFbxMesh();
            meshDataObject.Initialize(
                context.GetResolvedFbxNodePath(),
                FbxTopologyHash.Calculate(fbxMesh));
            return meshDataObject;
        }

        private static UnityVertexMappingObject[] BuildMappings(MeshFeatureExtractionContext context)
        {
            var originMesh = context.GetOriginUnityMesh();
            string nodePath = context.GetResolvedFbxNodePath();
            var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(
                nodePath,
                originMesh,
                context.Session.OriginScene,
                FbxUnityAssetReader.GetImportScale(context.Session.OriginFbxGo),
                FbxUnityAssetReader.GetBakeAxisConversion(context.Session.OriginFbxGo),
                FbxUnityAssetReader.GetImporterSpaceTransform(context.Session.OriginFbxGo),
                out _);

            return mapping != null
                ? new[] { mapping }
                : System.Array.Empty<UnityVertexMappingObject>();
        }

        private static string FormatMesh(MeshFeatureExtractionContext context)
        {
            return context.Path;
        }

    }
}
