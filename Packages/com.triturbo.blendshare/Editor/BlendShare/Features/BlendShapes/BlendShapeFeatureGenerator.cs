using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;
using Vector3d = Triturbo.BlendShare.Fbx.Vector3d;
using UnityBlendShapeData = Triturbo.BlendShapeShare.BlendShapeData.UnityBlendShapeData;
using UnityBlendShapeFrame = Triturbo.BlendShapeShare.BlendShapeData.UnityBlendShapeFrame;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShare.Features.BlendShapes
{
    /// <summary>
    /// Applies stored blendshape feature data to Unity mesh assets and FBX mesh nodes.
    /// </summary>
    public sealed class BlendShapeFeatureGenerator : MeshFeatureGenerator<BlendShapeFeatureObject>
    {
        public static readonly BlendShapeFeatureGenerator Instance = new();

        protected override MeshFeatureGenerationResult CanApplyToUnityMesh(
            UnityMeshGenerationContext context,
            BlendShapeFeatureObject feature)
        {
            if (context.MeshData == null || context.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Blendshape generation requires mesh data and a target mesh.");
            }

            return context.GetMappingFor(context.WorkingMesh) != null
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult("Target mesh does not match any stored Unity vertex mapping.");
        }

        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshGenerationContext context,
            BlendShapeFeatureObject feature)
        {
            if (context.MeshData == null || context.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Blendshape generation requires mesh data and a target mesh.");
            }

            if (context.GetMappingFor(context.WorkingMesh) == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Target mesh does not match any stored Unity vertex mapping.");
            }

            var mesh = context.WorkingMesh;
            var activeBlendShapes = feature.GetActiveBlendShapes().ToArray();
            var activeNames = new HashSet<string>(activeBlendShapes.Select(blendShape => blendShape.m_Name));
            var blendShapesToApply = new List<(string name, UnityBlendShapeData data)>();

            // Preserve existing target blendshapes that are not replaced by the active BlendShare shapes.
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (!activeNames.Contains(name))
                {
                    blendShapesToApply.Add((name, new UnityBlendShapeData(mesh, i)));
                }
            }

            foreach (var blendShape in activeBlendShapes)
            {
                var unityData = CreateUnityBlendShapeData(context, blendShape, mesh);
                if (unityData == null)
                {
                    Debug.LogError($"[BlendShare] Cannot generate Unity blendshape '{blendShape.m_Name}' for mesh '{context.MeshData.m_Path}'.");
                    continue;
                }

                blendShapesToApply.Add((blendShape.m_Name, unityData));
            }

            if (blendShapesToApply.Count == 0)
            {
                return MeshFeatureGenerationResult.FailedResult("No blendshape frames could be generated.");
            }

            mesh.ClearBlendShapes();
            foreach (var blendShape in blendShapesToApply)
            {
                foreach (var frame in blendShape.data.m_Frames ?? System.Array.Empty<UnityBlendShapeFrame>())
                {
                    frame?.AddBlendShapeFrame(ref mesh, blendShape.name);
                }
            }

            context.WorkingMesh = mesh;
            return MeshFeatureGenerationResult.Success();
        }

        private static UnityBlendShapeData CreateUnityBlendShapeData(
            UnityMeshGenerationContext context,
            FbxBlendShapeData blendShape,
            Mesh targetMesh)
        {
            var mapping = context.GetMappingFor(targetMesh);

            if (mapping != null)
            {
                return CreateUnityBlendShapeDataFromMapping(
                    mapping,
                    blendShape,
                    targetMesh.vertexCount,
                    ShouldEmitBlendShapeNormals(context, targetMesh));
            }

            return null;
        }

        private static UnityBlendShapeData CreateUnityBlendShapeDataFromMapping(
            UnityVertexMappingObject mapping,
            FbxBlendShapeData blendShape,
            int unityVertexCount,
            bool emitNormalDeltas)
        {
            var frames = blendShape?.m_Frames;
            if (frames == null || frames.Length == 0)
            {
                return null;
            }

            var unityData = new UnityBlendShapeData(frames.Length);
            for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var deltaVertices = new Vector3[unityVertexCount];
                var deltaNormals = emitNormalDeltas ? new Vector3[unityVertexCount] : null;
                for (int unityIndex = 0; unityIndex < unityVertexCount; unityIndex++)
                {
                    if (!mapping.TryGetFbxGroup(unityIndex, out FbxIndexGroup group))
                    {
                        return null;
                    }

                    var delta = GetDeltaFromGroup(frames[frameIndex], group);
                    deltaVertices[unityIndex] = mapping.ConvertFbxVectorToUnity(new Vector3((float)delta.x, (float)delta.y, (float)delta.z));
                    if (emitNormalDeltas)
                    {
                        var normalDelta = GetNormalDeltaFromGroup(frames[frameIndex], group);
                        deltaNormals[unityIndex] = mapping.ConvertFbxNormalDeltaToUnity(new Vector3((float)normalDelta.x, (float)normalDelta.y, (float)normalDelta.z));
                    }
                }

                float weight = frames[frameIndex]?.m_FrameWeight ?? 100f;
                unityData.AddFrameAt(frameIndex, new UnityBlendShapeFrame(
                    weight,
                    unityVertexCount,
                    deltaVertices,
                    deltaNormals,
                    null));
            }

            return unityData;
        }

        private static bool ShouldEmitBlendShapeNormals(UnityMeshGenerationContext context, Mesh targetMesh)
        {
            var importer = ResolveModelImporter(context, targetMesh);
            return importer != null &&
                   importer.importBlendShapeNormals != ModelImporterNormals.None &&
                   !UsesLegacyBlendShapeNormals(importer);
        }

        private static ModelImporter ResolveModelImporter(UnityMeshGenerationContext context, Mesh targetMesh)
        {
            var importer = GetModelImporter(targetMesh);
            if (importer != null)
            {
                return importer;
            }

            importer = GetModelImporter(context?.OriginalMesh);
            if (importer != null)
            {
                return importer;
            }

            importer = GetModelImporter(context?.TargetRenderer?.sharedMesh);
            if (importer != null)
            {
                return importer;
            }

            return GetModelImporter(context?.Session?.TargetMeshContainer);
        }

        private static ModelImporter GetModelImporter(Object asset)
        {
            if (asset == null)
            {
                return null;
            }

            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as ModelImporter;
        }

        private static bool UsesLegacyBlendShapeNormals(ModelImporter importer)
        {
            return GetBoolImporterProperty(importer, "legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes");
        }

        private static bool GetBoolImporterProperty(ModelImporter importer, string propertyName)
        {
            var property = typeof(ModelImporter).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null &&
                   property.PropertyType == typeof(bool) &&
                   property.GetValue(importer) is true;
        }

        private static Vector3d GetDeltaFromGroup(FbxBlendShapeFrame frame, FbxIndexGroup group)
        {
            if (group.m_Indices == null)
            {
                return Vector3d.zero;
            }

            // All welded members share the same delta; sparse FBX storage may omit zero-delta entries.
            for (int i = 0; i < group.m_Indices.Length; i++)
            {
                var delta = frame.GetDeltaPositionAt(group.m_Indices[i]);
                if (!delta.IsZero())
                {
                    return delta;
                }
            }

            return Vector3d.zero;
        }

        private static Vector3d GetNormalDeltaFromGroup(FbxBlendShapeFrame frame, FbxIndexGroup group)
        {
            if (group.m_Indices == null)
            {
                return Vector3d.zero;
            }

            for (int i = 0; i < group.m_Indices.Length; i++)
            {
                var delta = frame.GetDeltaNormalAt(group.m_Indices[i]);
                if (!delta.IsZero())
                {
                    return delta;
                }
            }

            return Vector3d.zero;
        }

#if ENABLE_FBX_SDK
        protected override MeshFeatureGenerationResult CanApplyToFbx(
            FbxGenerationContext context,
            BlendShapeFeatureObject feature)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        protected override MeshFeatureGenerationResult ApplyToFbx(
            FbxGenerationContext context,
            BlendShapeFeatureObject feature)
        {
            FbxMesh targetMesh = context.TargetMesh;
            if (targetMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult($"Can not find mesh at path: {context.MeshData?.m_Path} in FBX file");
            }

            GetDeformer(context.Patch, targetMesh, false)?.Destroy();

            var existingBlendShapes = new HashSet<string>();
            var activeBlendShapes = feature.GetActiveBlendShapes().ToArray();
            var activeNames = new HashSet<string>(activeBlendShapes.Select(record => record.m_Name));

            for (int i = 0; i < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
            {
                var deformer = targetMesh.GetBlendShapeDeformer(i);
                for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                {
                    var name = deformer.GetBlendShapeChannel(j).GetName();
                    if (!activeNames.Contains(name))
                    {
                        continue;
                    }

                    var channel = deformer.GetBlendShapeChannel(j);
                    for (int shape = channel.GetTargetShapeCount() - 1; shape >= 0; shape--)
                    {
                        channel.RemoveTargetShape(channel.GetTargetShape(shape));
                    }

                    CreateFbxBlendShapeChannel(channel, targetMesh, feature.GetBlendShape(name));
                    existingBlendShapes.Add(name);
                }
            }

            var targetDeformer = GetDeformer(context.Patch, targetMesh);
            foreach (var blendShape in activeBlendShapes)
            {
                if (existingBlendShapes.Contains(blendShape.m_Name))
                {
                    continue;
                }

                targetDeformer.AddBlendShapeChannel(CreateFbxBlendShapeChannel(
                    blendShape.m_Name,
                    targetMesh,
                    blendShape));
            }

            return MeshFeatureGenerationResult.Success();
        }

        protected override MeshFeatureGenerationResult RemoveFromFbx(
            FbxGenerationContext context,
            BlendShapeFeatureObject feature)
        {
            return MeshFeatureGenerationResult.FailedResult("Feature-level BlendShare revert is disabled; use baseline replay revert instead.");
#if false
            // Disabled until feature-level inverse can restore previous same-id state safely.
            FbxMesh targetMesh = context.TargetMesh;
            if (targetMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult($"Can not find mesh at path: {context.MeshData?.m_Path} in FBX file");
            }

            GetDeformer(context.Patch, targetMesh, false)?.Destroy();
            if (!context.RemoveInAllDeformer)
            {
                return MeshFeatureGenerationResult.Success();
            }

            var activeNames = new HashSet<string>(feature.GetActiveBlendShapes().Select(record => record.m_Name));
            for (int i = 0; i < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
            {
                var deformer = targetMesh.GetBlendShapeDeformer(i);
                for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                {
                    var name = deformer.GetBlendShapeChannel(j).GetName();
                    if (activeNames.Contains(name))
                    {
                        deformer.RemoveBlendShapeChannel(deformer.GetBlendShapeChannel(j));
                    }
                }
            }

            return MeshFeatureGenerationResult.Success();
#endif
        }

        private static FbxBlendShape GetDeformer(BlendShareObject patch, FbxMesh targetMesh, bool create = true)
        {
            if (!string.IsNullOrEmpty(patch.m_PatchId))
            {
                for (int i = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) - 1; i >= 0; i--)
                {
                    var deformer = targetMesh.GetBlendShapeDeformer(i);
                    if (deformer.GetName() == patch.m_PatchId)
                    {
                        return deformer;
                    }
                }
            }

            return create ? FbxBlendShape.Create(targetMesh, patch.m_PatchId) : null;
        }

        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(
            string name,
            FbxMesh mesh,
            FbxBlendShapeData fbxBlendShapeData)
        {
            return CreateFbxBlendShapeChannel(FbxBlendShapeChannel.Create(mesh, name), mesh, fbxBlendShapeData);
        }

        private static FbxBlendShapeChannel CreateFbxBlendShapeChannel(
            FbxBlendShapeChannel fbxBlendShapeChannel,
            FbxMesh mesh,
            FbxBlendShapeData fbxBlendShapeData)
        {
            int controlPointCount = mesh.GetControlPointsCount();
            int shapeCount = fbxBlendShapeData?.m_Frames?.Length ?? 0;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                FbxShape newShape = FbxShape.Create(mesh, fbxBlendShapeChannel.GetName());
                newShape.InitControlPoints(controlPointCount);
                FbxBlendShapeFrame frame = fbxBlendShapeData.m_Frames[shapeIndex];

                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var delta = frame.GetDeltaPositionAt(pointIndex);
                    var controlPoint = mesh.GetControlPointAt(pointIndex) + new FbxVector4(delta.x, delta.y, delta.z, 0.0);
                    newShape.SetControlPointAt(controlPoint, pointIndex);
                }

                fbxBlendShapeChannel.AddTargetShape(newShape, frame.m_FrameWeight);
            }

            return fbxBlendShapeChannel;
        }
#endif
    }
}
