using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShapeShare.FbxReader;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class MeshFeatureExtractionPipeline
    {
        public BlendShareObject Extract(
            GameObject source,
            GameObject origin,
            IEnumerable<MeshFeatureExtractionMeshRequest> meshes,
            MeshFeatureExtractionOptionsSet options)
        {
            var meshRequests = meshes?
                .Where(mesh => mesh != null && (!string.IsNullOrEmpty(mesh.MeshPath) || !string.IsNullOrEmpty(mesh.MeshName)))
                .ToArray() ?? System.Array.Empty<MeshFeatureExtractionMeshRequest>();

            if (source == null || origin == null || meshRequests.Length == 0)
            {
                return null;
            }

            using (var session = new MeshFeatureExtractionSession(source, origin, options, meshRequests))
            {
                var extractedMeshes = new List<MeshDataObject>();

                foreach (var request in meshRequests)
                {
                    var context = new MeshFeatureExtractionContext(session, request.MeshPath, request.MeshName);
                    var meshDataObject = CreateMeshData(context);

                    foreach (var extractor in MeshFeatureExtractorRegistry.Extractors)
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

                var blendShare = ScriptableObject.CreateInstance<BlendShareObject>();
                blendShare.m_Original = origin;
                blendShare.SetMeshes(extractedMeshes);
                return blendShare;
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

        private static MeshDataObject CreateMeshData(MeshFeatureExtractionContext context)
        {
            var meshDataObject = ScriptableObject.CreateInstance<MeshDataObject>();
            var fbxMesh = context.GetOriginFbxMesh(FbxMeshReadOptions.ControlPointPositions) ??
                          context.GetSourceFbxMesh(FbxMeshReadOptions.ControlPointPositions);
            meshDataObject.Initialize(
                string.IsNullOrEmpty(context.MeshPath) ? context.MeshName : context.MeshPath,
                context.MeshName,
                fbxMesh?.ControlPointCount ?? -1);
            return meshDataObject;
        }

        private static UnityVertexMappingObject[] BuildMappings(MeshFeatureExtractionContext context)
        {
            var originMesh = context.GetOriginUnityMesh();
            var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(
                string.IsNullOrEmpty(context.MeshPath) ? context.MeshName : context.MeshPath,
                originMesh,
                context.Session.OriginFbxAsset,
                out _);

            if (mapping != null && !mapping.m_IsValid)
            {
                mapping.SetUnityBlendShapeCache(context.Session.GetUnityBlendShapeCache(context.MeshPath, context.MeshName));
            }

            return mapping != null
                ? new[] { mapping }
                : System.Array.Empty<UnityVertexMappingObject>();
        }

        private static string FormatMesh(MeshFeatureExtractionContext context)
        {
            return string.IsNullOrEmpty(context.MeshPath) ? context.MeshName : context.MeshPath;
        }
    }
}
