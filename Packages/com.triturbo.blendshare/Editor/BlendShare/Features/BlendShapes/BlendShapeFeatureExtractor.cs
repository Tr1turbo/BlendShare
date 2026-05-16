using System.Collections.Generic;
using Triturbo.BlendShare.Core;
using UnityEngine;
using FbxReaderTransform = Triturbo.Fbx.FbxTransform;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public sealed class BlendShapeFeatureExtractor
        : MeshFeatureExtractor<BlendShapeFeatureObject, BlendShapeExtractionOptions>
    {
        public static readonly BlendShapeFeatureExtractor Instance = new();

        public override string FeatureId => BlendShapeFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            BlendShapeExtractionOptions options,
            out BlendShapeFeatureObject feature)
        {
            feature = null;

            var selectedShapeNames = options.GetSelectedBlendShapeNames(context.Path);
            if (selectedShapeNames.Count == 0)
            {
                return MeshFeatureExtractionResult.Skipped("No blendshapes selected for this mesh.");
            }

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
            return MeshFeatureExtractionResult.Success();
        }

        private static bool TryExtractFbxBlendShapes(
            MeshFeatureExtractionContext context,
            BlendShapeExtractionOptions options,
            IReadOnlyCollection<string> selectedShapeNames,
            out List<BlendShapeRecord> records,
            out string error)
        {
            records = new List<BlendShapeRecord>();
            error = null;

            var sourceMesh = context.GetSourceFbxMesh();
            if (sourceMesh == null)
            {
                error = $"Reader cannot find source FBX mesh at path '{context.Path}'.";
                return false;
            }

            if (sourceMesh.BlendShapeDeformers.Count == 0)
            {
                error = $"Reader did not expose blendshape data for mesh '{context.Path}'.";
                return false;
            }

            var originMesh = context.GetOriginFbxMesh();
            var selected = new HashSet<string>(selectedShapeNames);
            var sourceTransform = sourceMesh.OwnerNode?.LocalTransform ?? FbxReaderTransform.Identity;
            var originTransform = originMesh?.OwnerNode?.LocalTransform ?? FbxReaderTransform.Identity;
            var relativeTransform = options.GetReaderTransform(originTransform, sourceTransform);
            var baseMesh = options.BaseMesh == BlendShapeBaseMesh.Source ? sourceMesh : originMesh;

            foreach (var deformer in sourceMesh.BlendShapeDeformers)
            {
                foreach (var channel in deformer.Channels)
                {
                    string shapeName = channel.Name;
                    if (!selected.Contains(shapeName))
                    {
                        continue;
                    }

                    var data = BlendShapeFbxExtractionUtility.GetFbxBlendShapeData(
                        channel,
                        sourceMesh,
                        relativeTransform,
                        baseMesh);

                    if (data != null)
                    {
                        records.Add(new BlendShapeRecord(shapeName, data));
                    }
                }
            }

            return true;
        }
    }
}
