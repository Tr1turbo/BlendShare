using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;
using Vector3d = Triturbo.BlendShare.Fbx.Vector3d;

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

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult CanApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<BlendShapeFeatureObject> features)
        {
            if (context?.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Blendshape generation requires a target mesh.");
            }

            return features.Any(feature => context.GetMappingFor(feature, context.WorkingMesh) != null)
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult("No blendshape feature has a compatible Unity vertex mapping.");
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<BlendShapeFeatureObject> features)
        {
            var mesh = context.WorkingMesh;
            int writeOrder = mesh.blendShapeCount;
            var slots = new Dictionary<string, BlendShapeCandidate>(System.StringComparer.Ordinal);
            var mappingValidity = new Dictionary<IUnityToFbxVertexMap, bool>(ReferenceComparer<IUnityToFbxVertexMap>.Instance);
            foreach (var feature in features)
            {
                var mapping = context.GetMappingFor(feature, mesh);
                if (mapping == null)
                {
                    Debug.LogError(
                        $"[BlendShare] Skipped blendshape feature from '{context.GetHandle(feature).Patch?.name}': no compatible Unity vertex mapping.");
                    continue;
                }

                if (!mappingValidity.TryGetValue(mapping, out bool mappingIsValid))
                {
                    mappingIsValid = MappingCoversMesh(mapping, mesh.vertexCount);
                    mappingValidity.Add(mapping, mappingIsValid);
                }

                if (!mappingIsValid)
                {
                    Debug.LogError(
                        $"[BlendShare] Skipped blendshape feature from '{context.GetHandle(feature).Patch?.name}': mapping does not cover every Unity vertex.");
                    continue;
                }

                foreach (var blendShape in feature.GetActiveBlendShapes())
                {
                    if (blendShape == null ||
                        string.IsNullOrWhiteSpace(blendShape.m_Name) ||
                        blendShape.m_Frames == null ||
                        !blendShape.m_Frames.Any(frame => frame != null))
                    {
                        continue;
                    }

                    slots[blendShape.m_Name] = new BlendShapeCandidate(blendShape, mapping, ++writeOrder);
                }
            }

            var selected = slots.Values
                .OrderBy(candidate => candidate.WriteOrder)
                .ToArray();
            if (selected.Length == 0)
            {
                return MeshFeatureGenerationResult.Skipped("No valid blendshape frames were selected.");
            }

            var existingNames = Enumerable.Range(0, mesh.blendShapeCount)
                .Select(mesh.GetBlendShapeName)
                .ToHashSet(System.StringComparer.Ordinal);
            bool rebuild = selected.Any(candidate => existingNames.Contains(candidate.Shape.m_Name));
            var deltaVertices = new Vector3[mesh.vertexCount];
            var deltaNormals = new Vector3[mesh.vertexCount];
            if (!rebuild)
            {
                foreach (var candidate in selected)
                {
                    AppendCandidate(context, candidate, mesh, deltaVertices, deltaNormals);
                }

                context.WorkingMesh = mesh;
                return MeshFeatureGenerationResult.Success();
            }

            var sourceMesh = Object.Instantiate(mesh);
            sourceMesh.name = mesh.name;
            var deltaTangents = new Vector3[mesh.vertexCount];
            try
            {
                var selectedByName = selected.ToDictionary(
                    candidate => candidate.Shape.m_Name,
                    System.StringComparer.Ordinal);
                mesh.ClearBlendShapes();
                for (int blendShapeIndex = 0; blendShapeIndex < sourceMesh.blendShapeCount; blendShapeIndex++)
                {
                    string name = sourceMesh.GetBlendShapeName(blendShapeIndex);
                    if (selectedByName.ContainsKey(name))
                    {
                        continue;
                    }

                    int frameCount = sourceMesh.GetBlendShapeFrameCount(blendShapeIndex);
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        sourceMesh.GetBlendShapeFrameVertices(
                            blendShapeIndex,
                            frameIndex,
                            deltaVertices,
                            deltaNormals,
                            deltaTangents);
                        mesh.AddBlendShapeFrame(
                            name,
                            sourceMesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex),
                            deltaVertices,
                            deltaNormals,
                            deltaTangents);
                    }
                }

                foreach (var candidate in selected)
                {
                    AppendCandidate(context, candidate, mesh, deltaVertices, deltaNormals);
                }
            }
            finally
            {
                Object.DestroyImmediate(sourceMesh);
            }

            context.WorkingMesh = mesh;
            return MeshFeatureGenerationResult.Success();
        }

        private static bool MappingCoversMesh(IUnityToFbxVertexMap mapping, int vertexCount)
        {
            if (mapping == null || vertexCount <= 0)
            {
                return false;
            }

            for (int index = 0; index < vertexCount; index++)
            {
                if (!mapping.TryGetFbxGroup(index, out _))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AppendCandidate(
            UnityMeshFeatureContext context,
            BlendShapeCandidate candidate,
            Mesh mesh,
            Vector3[] deltaVertices,
            Vector3[] deltaNormals)
        {
            bool emitNormals = ShouldEmitBlendShapeNormals(context, mesh);
            foreach (var frame in candidate.Shape.m_Frames ?? System.Array.Empty<FbxBlendShapeFrame>())
            {
                if (frame == null)
                {
                    continue;
                }

                for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
                {
                    candidate.Mapping.TryGetFbxGroup(unityIndex, out FbxIndexGroup group);
                    var delta = GetDeltaFromGroup(frame, group);
                    deltaVertices[unityIndex] = candidate.Mapping.SpaceConversion.ConvertVector(
                        new Vector3((float)delta.x, (float)delta.y, (float)delta.z));
                    if (emitNormals)
                    {
                        var normalDelta = GetNormalDeltaFromGroup(frame, group);
                        deltaNormals[unityIndex] = candidate.Mapping.SpaceConversion.ConvertNormalDelta(
                            new Vector3((float)normalDelta.x, (float)normalDelta.y, (float)normalDelta.z));
                    }
                }

                mesh.AddBlendShapeFrame(
                    candidate.Shape.m_Name,
                    (float)frame.FrameWeight,
                    deltaVertices,
                    emitNormals ? deltaNormals : null,
                    null);
            }
        }

        private static bool ShouldEmitBlendShapeNormals(UnityMeshFeatureContext context, Mesh targetMesh)
        {
            var importer = ResolveModelImporter(context, targetMesh);
            return importer != null &&
                   importer.importBlendShapeNormals != ModelImporterNormals.None &&
                   !UsesLegacyBlendShapeNormals(importer);
        }

        private static ModelImporter ResolveModelImporter(UnityMeshFeatureContext context, Mesh targetMesh)
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

        private readonly struct BlendShapeCandidate
        {
            internal BlendShapeCandidate(
                FbxBlendShapeData shape,
                IUnityToFbxVertexMap mapping,
                int writeOrder)
            {
                Shape = shape;
                Mapping = mapping;
                WriteOrder = writeOrder;
            }

            internal FbxBlendShapeData Shape { get; }
            internal IUnityToFbxVertexMap Mapping { get; }
            internal int WriteOrder { get; }
        }

#if ENABLE_FBX_SDK
        private sealed class FbxBlendShapeSlot
        {
            internal FbxBlendShapeSlot(string name, BlendShareObject ownerPatch)
            {
                Name = name;
                OwnerPatch = ownerPatch;
            }

            internal string Name { get; }
            internal BlendShareObject OwnerPatch { get; }
            internal FbxBlendShapeData Shape { get; set; }
            internal int Order { get; set; }
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult CanApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<BlendShapeFeatureObject> features)
        {
            return MeshFeatureGenerationResult.Success(false);
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult ApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<BlendShapeFeatureObject> features)
        {
            var targetMesh = context.TargetMesh;
            if (targetMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult($"Can not find mesh at path: {context.MeshKey} in FBX file");
            }

            var contributors = features
                .Select(feature => context.GetHandle(feature).Patch)
                .Where(patch => patch != null)
                .Distinct()
                .ToArray();
            bool modified = false;
            foreach (var patch in contributors)
            {
                var deformer = GetDeformer(patch, targetMesh, false);
                if (deformer != null)
                {
                    deformer.Destroy();
                    modified = true;
                }
            }

            var existingChannels = new Dictionary<string, List<FbxBlendShapeChannel>>(System.StringComparer.Ordinal);
            for (int deformerIndex = 0;
                 deformerIndex < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);
                 deformerIndex++)
            {
                var deformer = targetMesh.GetBlendShapeDeformer(deformerIndex);
                for (int channelIndex = 0; channelIndex < deformer.GetBlendShapeChannelCount(); channelIndex++)
                {
                    var channel = deformer.GetBlendShapeChannel(channelIndex);
                    string channelName = channel.GetName();
                    if (!existingChannels.TryGetValue(channelName, out var channels))
                    {
                        channels = new List<FbxBlendShapeChannel>();
                        existingChannels.Add(channelName, channels);
                    }

                    channels.Add(channel);
                }
            }

            var slots = new Dictionary<string, FbxBlendShapeSlot>(System.StringComparer.Ordinal);
            int order = 0;
            foreach (var feature in features)
            {
                var patch = context.GetHandle(feature).Patch;
                foreach (var shape in feature.GetActiveBlendShapes())
                {
                    if (shape == null || string.IsNullOrWhiteSpace(shape.m_Name))
                    {
                        continue;
                    }

                    if (!slots.TryGetValue(shape.m_Name, out var slot))
                    {
                        slot = new FbxBlendShapeSlot(shape.m_Name, patch);
                        slots.Add(slot.Name, slot);
                    }

                    slot.Shape = shape;
                    slot.Order = ++order;
                }
            }

            foreach (var slot in slots.Values.OrderBy(slot => slot.Order))
            {
                if (existingChannels.TryGetValue(slot.Name, out var channels))
                {
                    foreach (var channel in channels)
                    {
                        for (int shapeIndex = channel.GetTargetShapeCount() - 1; shapeIndex >= 0; shapeIndex--)
                        {
                            channel.RemoveTargetShape(channel.GetTargetShape(shapeIndex));
                        }

                        CreateFbxBlendShapeChannel(channel, targetMesh, slot.Shape);
                    }

                    modified = true;
                }
                else
                {
                    GetDeformer(slot.OwnerPatch, targetMesh)
                        .AddBlendShapeChannel(CreateFbxBlendShapeChannel(slot.Name, targetMesh, slot.Shape));
                    modified = true;
                }
            }

            return modified
                ? MeshFeatureGenerationResult.Success()
                : MeshFeatureGenerationResult.Skipped();
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

                fbxBlendShapeChannel.AddTargetShape(newShape, frame.FrameWeight);
            }

            return fbxBlendShapeChannel;
        }
#endif
    }
}
