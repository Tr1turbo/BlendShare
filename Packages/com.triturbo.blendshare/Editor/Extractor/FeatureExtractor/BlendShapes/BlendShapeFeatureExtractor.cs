using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using Triturbo.BlendShapeShare.Util;
#endif

namespace Triturbo.BlendShapeShare.Extractor
{
    public sealed class BlendShapeFeatureExtractor
        : MeshFeatureExtractor<BlendShapeFeatureObject, BlendShapeExtractionOptions>
    {
        public override string FeatureId => BlendShapeFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            BlendShapeExtractionOptions options,
            out BlendShapeFeatureObject feature)
        {
            feature = null;

            var selectedShapeNames = options.GetSelectedBlendShapeNames(context.MeshPath, context.MeshName);
            if (selectedShapeNames.Count == 0)
            {
                return MeshFeatureExtractionResult.Skipped("No blendshapes selected for this mesh.");
            }

#if ENABLE_FBX_SDK
            if (!TryExtractFbxBlendShapes(context, options, selectedShapeNames, out var records, out string error))
            {
                return MeshFeatureExtractionResult.Failed(error);
            }

            if (records.Count == 0)
            {
                return MeshFeatureExtractionResult.Skipped("No blendshape records were extracted for this mesh.");
            }

            feature = ScriptableObject.CreateInstance<BlendShapeFeatureObject>();
            feature.SetBlendShapes(records);
            feature.SetActiveBlendShapeNames(selectedShapeNames);

            var unityCache = ExtractUnityBlendShapeCache(context, options, selectedShapeNames);
            context.Session.SetUnityBlendShapeCache(context.MeshPath, context.MeshName, unityCache);
            return MeshFeatureExtractionResult.Success();
#else
            return MeshFeatureExtractionResult.Failed("Autodesk FBX SDK is required to extract blendshapes.");
#endif
        }

#if ENABLE_FBX_SDK
        private static bool TryExtractFbxBlendShapes(
            MeshFeatureExtractionContext context,
            BlendShapeExtractionOptions options,
            IReadOnlyCollection<string> selectedShapeNames,
            out List<BlendShapeRecord> records,
            out string error)
        {
            records = new List<BlendShapeRecord>();
            error = null;

            if (!context.Session.SourceFbxScene.TryGetRootNode(out var sourceRootNode, out error))
            {
                return false;
            }

            if (!context.Session.OriginFbxScene.TryGetRootNode(out var originRootNode, out error))
            {
                return false;
            }

            var sourceNode = sourceRootNode.FindMeshChild(context.MeshName);
            var sourceMesh = sourceNode?.GetMesh();
            if (sourceMesh == null)
            {
                error = $"Cannot find source FBX mesh '{context.MeshName}'.";
                return false;
            }

            var originNode = originRootNode.FindMeshChild(context.MeshName);
            var originMesh = originNode?.GetMesh();
            var selected = new HashSet<string>(selectedShapeNames);
            var relativeTransform = options.GetTransform(
                originNode?.EvaluateLocalTransform(),
                sourceNode.EvaluateLocalTransform());

            var baseMesh = options.BaseMesh == BlendShapeBaseMesh.Source ? sourceMesh : originMesh;
            var weldingGroups = options.WeldVertices && originMesh != null
                ? BlendShapesExtractor.GetWeldingGroups(originMesh)
                : null;

            try
            {
                int deformerCount = sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
                for (int deformerIndex = 0; deformerIndex < deformerCount; deformerIndex++)
                {
                    var deformer = sourceMesh.GetBlendShapeDeformer(deformerIndex);
                    for (int channelIndex = 0; channelIndex < deformer.GetBlendShapeChannelCount(); channelIndex++)
                    {
                        var channel = deformer.GetBlendShapeChannel(channelIndex);
                        string shapeName = channel.GetName();
                        if (!selected.Contains(shapeName))
                        {
                            continue;
                        }

                        var data = BlendShapesExtractor.GetFbxBlendShapeData(
                            channel,
                            sourceMesh,
                            weldingGroups,
                            relativeTransform,
                            baseMesh);

                        if (data != null)
                        {
                            records.Add(new BlendShapeRecord(shapeName, data));
                        }
                    }
                }
            }
            finally
            {
                relativeTransform?.Dispose();
            }

            return true;
        }

        private static IEnumerable<MappingUnityBlendShapeCache> ExtractUnityBlendShapeCache(
            MeshFeatureExtractionContext context,
            BlendShapeExtractionOptions options,
            IReadOnlyCollection<string> selectedShapeNames)
        {
            var sourceMesh = context.GetSourceUnityMesh();
            if (sourceMesh == null)
            {
                return Enumerable.Empty<MappingUnityBlendShapeCache>();
            }

            var selected = new HashSet<string>(selectedShapeNames);
            var originMesh = options.BaseMesh == BlendShapeBaseMesh.Original
                ? context.GetOriginUnityMesh()
                : null;
            var cache = new List<MappingUnityBlendShapeCache>();

            for (int shapeIndex = 0; shapeIndex < sourceMesh.blendShapeCount; shapeIndex++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(shapeIndex);
                if (!selected.Contains(shapeName))
                {
                    continue;
                }

                cache.Add(new MappingUnityBlendShapeCache(
                    shapeName,
                    CreateUnityBlendShapeData(sourceMesh, originMesh, shapeIndex)));
            }

            return cache;
        }

        private static UnityBlendShapeData CreateUnityBlendShapeData(
            Mesh sourceMesh,
            Mesh originMesh,
            int shapeIndex)
        {
            int frameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
            var data = new UnityBlendShapeData(frameCount);
            bool calculateDiffs = originMesh != null && originMesh != sourceMesh && originMesh.vertexCount == sourceMesh.vertexCount;

            var vertexDiffs = new Vector3[sourceMesh.vertexCount];
            var normalDiffs = new Vector3[sourceMesh.vertexCount];
            var tangentDiffs = new Vector3[sourceMesh.vertexCount];

            if (calculateDiffs)
            {
                var vertices = sourceMesh.vertices;
                var normals = sourceMesh.normals;
                var tangents = sourceMesh.tangents;
                var originVertices = originMesh.vertices;
                var originNormals = originMesh.normals;
                var originTangents = originMesh.tangents;

                for (int vertexIndex = 0; vertexIndex < sourceMesh.vertexCount; vertexIndex++)
                {
                    vertexDiffs[vertexIndex] = vertices[vertexIndex] - originVertices[vertexIndex];
                    if (vertexIndex < normals.Length && vertexIndex < originNormals.Length)
                    {
                        normalDiffs[vertexIndex] = normals[vertexIndex] - originNormals[vertexIndex];
                    }

                    if (vertexIndex < tangents.Length && vertexIndex < originTangents.Length)
                    {
                        tangentDiffs[vertexIndex] = (Vector3)(tangents[vertexIndex] - originTangents[vertexIndex]);
                    }
                }
            }

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float frameWeight = sourceMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                var deltaVertices = new Vector3[sourceMesh.vertexCount];
                var deltaNormals = new Vector3[sourceMesh.vertexCount];
                var deltaTangents = new Vector3[sourceMesh.vertexCount];
                sourceMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

                if (calculateDiffs)
                {
                    for (int vertexIndex = 0; vertexIndex < sourceMesh.vertexCount; vertexIndex++)
                    {
                        deltaVertices[vertexIndex] += vertexDiffs[vertexIndex];
                        deltaNormals[vertexIndex] += normalDiffs[vertexIndex];
                        deltaTangents[vertexIndex] += tangentDiffs[vertexIndex];
                    }
                }

                data.AddFrameAt(frameIndex, new UnityBlendShapeFrame(
                    frameWeight,
                    sourceMesh.vertexCount,
                    deltaVertices,
                    deltaNormals,
                    deltaTangents));
            }

            return data;
        }
#endif
    }
}
