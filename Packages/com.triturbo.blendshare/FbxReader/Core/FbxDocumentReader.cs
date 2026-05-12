using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Triturbo.FBX
{
    public static class FbxDocumentReader
    {
        private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

        public static FbxReadResult<FbxDocument> Read(string assetPath, FbxReadSettings settings = null)
        {
            settings ??= FbxReadSettings.MetadataOnly;
            if (string.IsNullOrEmpty(assetPath))
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.InvalidArgument, "FBX asset path is empty.");
            }

            if (!File.Exists(assetPath))
            {
                return FbxReadResult<FbxDocument>.Failed(FbxReadStatus.FileNotFound, $"FBX file '{assetPath}' was not found.");
            }

            try
            {
                var scene = ReadScene(assetPath, settings.ReadOptions);
                return FbxReadResult<FbxDocument>.Succeeded(
                    BuildDocument(scene, settings.ReadOptions),
                    scene.Diagnostics);
            }
            catch (UnsupportedFbxVersionException exception)
            {
                return FbxReadResult<FbxDocument>.Failed(
                    FbxReadStatus.UnsupportedVersion,
                    exception.Message,
                    CreateExceptionDiagnostics(FbxReadStatus.UnsupportedVersion, exception));
            }
            catch (InvalidDataException exception)
            {
                var status = exception.Message.IndexOf("not a binary FBX", StringComparison.OrdinalIgnoreCase) >= 0
                    ? FbxReadStatus.NotBinaryFbx
                    : FbxReadStatus.ParseError;
                return FbxReadResult<FbxDocument>.Failed(status, exception.Message, CreateExceptionDiagnostics(status, exception));
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is ArgumentException ||
                exception is NotSupportedException)
            {
                return FbxReadResult<FbxDocument>.Failed(
                    FbxReadStatus.ParseError,
                    $"Failed to read '{assetPath}': {exception.Message}",
                    CreateExceptionDiagnostics(FbxReadStatus.ParseError, exception));
            }
        }

        public static FbxDocument ReadOrThrow(string assetPath, FbxReadSettings settings = null)
        {
            var result = Read(assetPath, settings);
            if (!result.Success)
            {
                throw new FbxReadException(result.Status, result.Message, result.Diagnostics);
            }

            return result.Value;
        }

        private static IEnumerable<FbxDiagnostic> CreateExceptionDiagnostics(FbxReadStatus status, Exception exception)
        {
            return new[]
            {
                new FbxDiagnostic(FbxDiagnosticSeverity.Error, status, exception.Message)
            };
        }

        private static BinaryFbxScene ReadScene(string assetPath, FbxMeshReadOptions readOptions)
        {
            using var stream = File.OpenRead(assetPath);
            using var reader = new BinaryReader(stream);

            ValidateHeader(reader);
            int version = reader.ReadInt32();
            if (version <= 0)
            {
                throw new UnsupportedFbxVersionException(version);
            }

            var scene = new BinaryFbxScene();
            long fileLength = stream.Length;
            while (stream.Position < fileLength)
            {
                if (!TryReadNodeHeader(reader, version, fileLength, out var header) || header.IsNull)
                {
                    break;
                }

                _ = ReadProperties(reader, header.PropertyCount, false);
                if (header.Name == "Objects")
                {
                    ReadObjects(reader, version, header.EndOffset, scene, readOptions);
                }
                else if (header.Name == "Connections")
                {
                    ReadConnections(reader, version, header.EndOffset, scene);
                }

                Seek(reader, header.EndOffset);
            }

            return scene;
        }

        private static void ReadObjects(
            BinaryReader reader,
            int version,
            long endOffset,
            BinaryFbxScene scene,
            FbxMeshReadOptions readOptions)
        {
            while (reader.BaseStream.Position < endOffset)
            {
                if (!TryReadNodeHeader(reader, version, endOffset, out var header) || header.IsNull)
                {
                    break;
                }

                var properties = ReadProperties(reader, header.PropertyCount, false);
                if (header.Name == "Geometry")
                {
                    ReadGeometry(reader, version, header, properties, scene, readOptions);
                }
                else if (header.Name == "Model")
                {
                    ReadModel(reader, version, header, properties, scene);
                }
                else if (header.Name == "Deformer")
                {
                    ReadDeformer(reader, version, header, properties, scene, readOptions);
                }

                Seek(reader, header.EndOffset);
            }
        }

        private static void ReadGeometry(
            BinaryReader reader,
            int version,
            FbxNodeHeader header,
            object[] properties,
            BinaryFbxScene scene,
            FbxMeshReadOptions readOptions)
        {
            long id = GetLong(properties, 0);
            string name = FbxNameUtility.CleanObjectName(GetString(properties, 1));
            string type = GetString(properties, 2);
            bool isMesh = string.Equals(type, "Mesh", StringComparison.Ordinal);
            bool isShape = string.Equals(type, "Shape", StringComparison.Ordinal);
            if (id == 0 || (!isMesh && !isShape))
            {
                return;
            }

            bool readBasePositions = isMesh && Includes(readOptions, FbxMeshReadOptions.ControlPointPositions);
            bool readShapePositions = isShape && Includes(readOptions, FbxMeshReadOptions.BlendShapes);
            if (isShape && !readShapePositions)
            {
                return;
            }

            double[] vertices = null;
            int[] indices = null;
            int controlPointCount = 0;
            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                bool isVerticesNode = childHeader.Name == "Vertices";
                bool readArrayValues = (isVerticesNode && (readBasePositions || readShapePositions)) ||
                                       (readShapePositions && childHeader.Name == "Indexes");
                var childProperties = ReadProperties(reader, childHeader.PropertyCount, readArrayValues);
                if (isVerticesNode)
                {
                    vertices = GetDoubleArray(childProperties);
                    int vertexValueCount = vertices?.Length ?? GetArrayLength(childProperties);
                    controlPointCount = Math.Max(controlPointCount, vertexValueCount / 3);
                }
                else if (isShape && childHeader.Name == "Indexes")
                {
                    indices = GetIntArray(childProperties);
                }

                Seek(reader, childHeader.EndOffset);
            }

            if ((readBasePositions || readShapePositions) && (vertices == null || vertices.Length < 3))
            {
                return;
            }

            scene.Geometries[id] = new GeometryRecord
            {
                Id = id,
                Name = name,
                Type = type,
                Indices = indices,
                ControlPointCount = controlPointCount,
                ControlPointPositions = vertices != null
                    ? ToVector3dArray(vertices)
                    : Array.Empty<Vector3d>()
            };
        }

        private static void ReadModel(
            BinaryReader reader,
            int version,
            FbxNodeHeader header,
            object[] properties,
            BinaryFbxScene scene)
        {
            long id = GetLong(properties, 0);
            if (id == 0)
            {
                return;
            }

            var localTranslation = Vector3d.zero;
            var localRotation = Vector3d.zero;
            var localScale = Vector3d.one;
            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                if (childHeader.Name == "Properties70")
                {
                    ReadModelProperties(reader, version, childHeader.EndOffset, ref localTranslation, ref localRotation, ref localScale);
                }
                else
                {
                    _ = ReadProperties(reader, childHeader.PropertyCount, false);
                }

                Seek(reader, childHeader.EndOffset);
            }

            scene.Models[id] = new ModelRecord
            {
                Id = id,
                Name = FbxNameUtility.CleanObjectName(GetString(properties, 1)),
                Type = GetString(properties, 2),
                LocalTransform = new FbxTransform(localTranslation, localRotation, localScale)
            };
        }

        private static void ReadModelProperties(
            BinaryReader reader,
            int version,
            long endOffset,
            ref Vector3d localTranslation,
            ref Vector3d localRotation,
            ref Vector3d localScale)
        {
            while (reader.BaseStream.Position < endOffset)
            {
                if (!TryReadNodeHeader(reader, version, endOffset, out var header) || header.IsNull)
                {
                    break;
                }

                var properties = ReadProperties(reader, header.PropertyCount, false);
                if (header.Name == "P")
                {
                    string propertyName = GetString(properties, 0);
                    if (propertyName == "Lcl Translation")
                    {
                        localTranslation = GetVector3d(properties, 4, localTranslation);
                    }
                    else if (propertyName == "Lcl Rotation")
                    {
                        localRotation = GetVector3d(properties, 4, localRotation);
                    }
                    else if (propertyName == "Lcl Scaling")
                    {
                        localScale = GetVector3d(properties, 4, localScale);
                    }
                }

                Seek(reader, header.EndOffset);
            }
        }

        private static void ReadDeformer(
            BinaryReader reader,
            int version,
            FbxNodeHeader header,
            object[] properties,
            BinaryFbxScene scene,
            FbxMeshReadOptions readOptions)
        {
            long id = GetLong(properties, 0);
            if (id == 0)
            {
                return;
            }

            string name = FbxNameUtility.CleanObjectName(GetString(properties, 1));
            string type = GetString(properties, 2);
            bool isCluster = string.Equals(type, "Cluster", StringComparison.Ordinal);
            bool isBlendShapeChannel = string.Equals(type, "BlendShapeChannel", StringComparison.Ordinal);
            bool readBoneWeights = Includes(readOptions, FbxMeshReadOptions.BoneWeights) && isCluster;
            bool readBlendShapeWeights = Includes(readOptions, FbxMeshReadOptions.BlendShapes) && isBlendShapeChannel;

            int[] indices = null;
            double[] weights = null;
            double[] fullWeights = null;
            FbxClusterLinkMode linkMode = FbxClusterLinkMode.Unknown;
            FbxMatrix4x4 transformMatrix = FbxMatrix4x4.Identity;
            FbxMatrix4x4 transformLinkMatrix = FbxMatrix4x4.Identity;
            FbxMatrix4x4 transformAssociateModelMatrix = FbxMatrix4x4.Identity;
            bool hasTransformMatrix = false;
            bool hasTransformLinkMatrix = false;
            bool hasTransformAssociateModelMatrix = false;
            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                bool readArrayValues = (readBoneWeights &&
                                        (childHeader.Name == "Indexes" ||
                                         childHeader.Name == "Weights" ||
                                         childHeader.Name == "Transform" ||
                                         childHeader.Name == "TransformLink" ||
                                         childHeader.Name == "TransformAssociateModel")) ||
                                       (readBlendShapeWeights && childHeader.Name == "FullWeights");
                var childProperties = ReadProperties(reader, childHeader.PropertyCount, readArrayValues);
                if (childHeader.Name == "Indexes")
                {
                    indices = GetIntArray(childProperties);
                }
                else if (childHeader.Name == "Weights")
                {
                    weights = GetDoubleArray(childProperties);
                }
                else if (childHeader.Name == "FullWeights")
                {
                    fullWeights = GetDoubleArray(childProperties);
                }
                else if (childHeader.Name == "Mode")
                {
                    linkMode = ParseClusterLinkMode(childProperties);
                }
                else if (childHeader.Name == "Transform")
                {
                    transformMatrix = GetMatrix(childProperties, out hasTransformMatrix);
                }
                else if (childHeader.Name == "TransformLink")
                {
                    transformLinkMatrix = GetMatrix(childProperties, out hasTransformLinkMatrix);
                }
                else if (childHeader.Name == "TransformAssociateModel")
                {
                    transformAssociateModelMatrix = GetMatrix(childProperties, out hasTransformAssociateModelMatrix);
                }

                Seek(reader, childHeader.EndOffset);
            }

            scene.Deformers[id] = CreateDeformerRecord(
                id,
                name,
                type,
                indices,
                weights,
                fullWeights,
                linkMode,
                transformMatrix,
                hasTransformMatrix,
                transformLinkMatrix,
                hasTransformLinkMatrix,
                transformAssociateModelMatrix,
                hasTransformAssociateModelMatrix);
        }

        private static void ReadConnections(BinaryReader reader, int version, long endOffset, BinaryFbxScene scene)
        {
            while (reader.BaseStream.Position < endOffset)
            {
                if (!TryReadNodeHeader(reader, version, endOffset, out var header) || header.IsNull)
                {
                    break;
                }

                var properties = ReadProperties(reader, header.PropertyCount, false);
                if (header.Name == "C" && string.Equals(GetString(properties, 0), "OO", StringComparison.Ordinal))
                {
                    long childId = GetLong(properties, 1);
                    long parentId = GetLong(properties, 2);
                    if (childId != 0)
                    {
                        if (!scene.ObjectParents.ContainsKey(childId))
                        {
                            scene.ObjectParents[childId] = parentId;
                        }

                        if (!scene.ChildrenByParent.TryGetValue(parentId, out var childList))
                        {
                            childList = new List<long>();
                            scene.ChildrenByParent[parentId] = childList;
                        }

                        childList.Add(childId);
                    }
                }

                Seek(reader, header.EndOffset);
            }
        }

        private static FbxDocument BuildDocument(BinaryFbxScene scene, FbxMeshReadOptions requestedOptions)
        {
            var rootNode = new FbxSceneNode(0, string.Empty, string.Empty, FbxSceneNodeType.Root, FbxTransform.Identity);
            var nodesById = scene.Models.Values.ToDictionary(
                model => model.Id,
                model => new FbxSceneNode(
                    model.Id,
                    model.Name,
                    BuildModelPath(scene, model.Id),
                    ToSceneNodeType(model.Type),
                    model.LocalTransform));

            var childrenByNode = new Dictionary<FbxSceneNode, List<FbxSceneNode>>();
            childrenByNode[rootNode] = new List<FbxSceneNode>();
            foreach (var node in nodesById.Values)
            {
                childrenByNode[node] = new List<FbxSceneNode>();
            }

            foreach (var model in scene.Models.Values)
            {
                var node = nodesById[model.Id];
                FbxSceneNode parentNode = rootNode;
                if (scene.ObjectParents.TryGetValue(model.Id, out long parentId) &&
                    nodesById.TryGetValue(parentId, out var modelParent))
                {
                    parentNode = modelParent;
                }

                node.SetParent(parentNode);
                childrenByNode[parentNode].Add(node);
            }

            foreach (var pair in childrenByNode)
            {
                pair.Key.SetChildren(pair.Value);
            }

            var meshes = new List<FbxMeshGeometry>();
            foreach (var geometry in scene.Geometries.Values)
            {
                if (!string.Equals(geometry.Type, "Mesh", StringComparison.Ordinal))
                {
                    continue;
                }

                scene.ObjectParents.TryGetValue(geometry.Id, out long modelId);
                nodesById.TryGetValue(modelId, out var ownerNode);
                ownerNode ??= rootNode;

                int controlPointCount = Math.Max(geometry.ControlPointCount, geometry.ControlPointPositions.Length);
                var deformers = BuildDeformers(scene, geometry, requestedOptions, controlPointCount, nodesById);
                FbxMeshReadOptions availableOptions = GetAvailableOptions(geometry, deformers, requestedOptions);
                var mesh = new FbxMeshGeometry(
                    geometry.Id,
                    geometry.Name,
                    ownerNode,
                    controlPointCount,
                    geometry.ControlPointPositions,
                    deformers,
                    requestedOptions,
                    availableOptions);

                foreach (var deformer in deformers)
                {
                    deformer.OwnerMesh = mesh;
                }

                ownerNode.SetMesh(mesh);
                meshes.Add(mesh);
            }

            var nodes = new List<FbxSceneNode> { rootNode };
            nodes.AddRange(nodesById.Values);
            return new FbxDocument(rootNode, nodes, meshes, requestedOptions);
        }

        private static List<FbxDeformer> BuildDeformers(
            BinaryFbxScene scene,
            GeometryRecord geometry,
            FbxMeshReadOptions readOptions,
            int controlPointCount,
            IReadOnlyDictionary<long, FbxSceneNode> nodesById)
        {
            var deformers = new List<FbxDeformer>();
            if (!scene.ChildrenByParent.TryGetValue(geometry.Id, out var children))
            {
                return deformers;
            }

            foreach (long childId in children)
            {
                if (!scene.Deformers.TryGetValue(childId, out var deformer))
                {
                    continue;
                }

                switch (deformer)
                {
                    case SkinDeformerRecord skin:
                        deformers.Add(BuildSkinDeformer(scene, skin, controlPointCount, nodesById));
                        break;
                    case BlendShapeDeformerRecord blendShape:
                        deformers.Add(BuildBlendShapeDeformer(scene, blendShape, geometry, readOptions));
                        break;
                    case VertexCacheDeformerRecord vertexCache:
                        deformers.Add(new FbxVertexCacheDeformer(vertexCache.Id, vertexCache.Name));
                        break;
                    case UnknownDeformerRecord unknown:
                        deformers.Add(new FbxUnknownDeformer(unknown.Id, unknown.Name, unknown.Type));
                        break;
                }
            }

            return deformers;
        }

        private static FbxBlendShapeDeformer BuildBlendShapeDeformer(
            BinaryFbxScene scene,
            BlendShapeDeformerRecord deformer,
            GeometryRecord geometry,
            FbxMeshReadOptions readOptions)
        {
            var channels = new List<FbxBlendShapeChannel>();
            foreach (var channel in FindChildDeformers<BlendShapeChannelRecord>(scene, deformer.Id))
            {
                var frames = new List<FbxShapeFrame>();
                if (Includes(readOptions, FbxMeshReadOptions.BlendShapes))
                {
                    var shapes = FindChildShapeGeometries(scene, channel.Id).ToArray();
                    for (int frameIndex = 0; frameIndex < shapes.Length; frameIndex++)
                    {
                        frames.Add(BuildShapeFrame(
                            GetFrameWeight(channel, frameIndex, shapes.Length),
                            geometry,
                            shapes[frameIndex]));
                    }
                }

                channels.Add(new FbxBlendShapeChannel(channel.Id, channel.Name, frames));
            }

            return new FbxBlendShapeDeformer(deformer.Id, deformer.Name, channels);
        }

        private static FbxShapeFrame BuildShapeFrame(
            double frameWeight,
            GeometryRecord baseGeometry,
            GeometryRecord shapeGeometry)
        {
            int controlPointCount = baseGeometry?.ControlPointPositions?.Length ?? 0;
            var shapePositions = shapeGeometry?.ControlPointPositions ?? Array.Empty<Vector3d>();
            if (controlPointCount == 0 || shapePositions.Length == 0)
            {
                return new FbxShapeFrame(frameWeight, Array.Empty<int>(), Array.Empty<Vector3d>());
            }

            if (shapeGeometry?.Indices != null && shapeGeometry.Indices.Length > 0)
            {
                int count = Math.Min(shapeGeometry.Indices.Length, shapePositions.Length);
                var indices = new List<int>(count);
                var deltas = new List<Vector3d>(count);
                for (int i = 0; i < count; i++)
                {
                    int controlPointIndex = shapeGeometry.Indices[i];
                    if (controlPointIndex < 0 || controlPointIndex >= controlPointCount)
                    {
                        continue;
                    }

                    var delta = shapePositions[i] - baseGeometry.ControlPointPositions[controlPointIndex];
                    if (delta.IsZero())
                    {
                        continue;
                    }

                    indices.Add(controlPointIndex);
                    deltas.Add(delta);
                }

                return new FbxShapeFrame(frameWeight, indices, deltas);
            }

            int denseCount = Math.Min(controlPointCount, shapePositions.Length);
            var denseIndices = new List<int>(denseCount);
            var denseDeltas = new List<Vector3d>(denseCount);
            for (int i = 0; i < denseCount; i++)
            {
                var delta = shapePositions[i] - baseGeometry.ControlPointPositions[i];
                if (delta.IsZero())
                {
                    continue;
                }

                denseIndices.Add(i);
                denseDeltas.Add(delta);
            }

            return new FbxShapeFrame(frameWeight, denseIndices, denseDeltas);
        }

        private static FbxSkinDeformer BuildSkinDeformer(
            BinaryFbxScene scene,
            SkinDeformerRecord skin,
            int controlPointCount,
            IReadOnlyDictionary<long, FbxSceneNode> nodesById)
        {
            var bones = new List<FbxBoneBinding>();
            var boneIndexByNodeId = new Dictionary<long, int>();
            var boneIndexByFallbackKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var clusters = new List<FbxCluster>();
            var weightsByControlPoint = new List<FbxControlPointBoneWeight>[Math.Max(0, controlPointCount)];

            foreach (var cluster in FindChildDeformers<ClusterDeformerRecord>(scene, skin.Id))
            {
                long boneNodeId = ResolveClusterBoneNodeId(scene, cluster);
                nodesById.TryGetValue(boneNodeId, out var boneNode);
                int boneIndex = GetOrAddBoneBinding(
                    bones,
                    boneIndexByNodeId,
                    boneIndexByFallbackKey,
                    boneNodeId,
                    boneNode,
                    ResolveClusterBoneName(scene, cluster, boneNode),
                    ResolveClusterBonePath(cluster, boneNode));
                var bone = bones[boneIndex];
                bone.Node?.MarkBone();

                bool hasWeightData = (cluster.Indices?.Length ?? 0) > 0 && (cluster.Weights?.Length ?? 0) > 0;
                if (hasWeightData && (!cluster.HasTransformMatrix || !cluster.HasTransformLinkMatrix))
                {
                    scene.Diagnostics.Add(new FbxDiagnostic(
                        FbxDiagnosticSeverity.Warning,
                        FbxReadStatus.SectionUnavailable,
                        $"FBX cluster '{cluster.Name}' has weights but is missing bind matrices."));
                }

                if (hasWeightData && bone.Node == null)
                {
                    scene.Diagnostics.Add(new FbxDiagnostic(
                        FbxDiagnosticSeverity.Warning,
                        FbxReadStatus.SectionUnavailable,
                        $"FBX cluster '{cluster.Name}' has weights but no resolvable bone node."));
                }

                clusters.Add(new FbxCluster(
                    cluster.Id,
                    cluster.Name,
                    boneIndex,
                    bone,
                    cluster.LinkMode,
                    cluster.TransformMatrix,
                    cluster.HasTransformMatrix,
                    cluster.TransformLinkMatrix,
                    cluster.HasTransformLinkMatrix,
                    cluster.TransformAssociateModelMatrix,
                    cluster.HasTransformAssociateModelMatrix,
                    cluster.Indices ?? Array.Empty<int>(),
                    cluster.Weights ?? Array.Empty<double>()));

                if (cluster.Indices == null || cluster.Weights == null)
                {
                    continue;
                }

                int influenceCount = Math.Min(cluster.Indices.Length, cluster.Weights.Length);
                for (int i = 0; i < influenceCount; i++)
                {
                    int controlPointIndex = cluster.Indices[i];
                    if (controlPointIndex < 0 || controlPointIndex >= weightsByControlPoint.Length)
                    {
                        continue;
                    }

                    float weight = (float)cluster.Weights[i];
                    if (weight == 0f)
                    {
                        continue;
                    }

                    weightsByControlPoint[controlPointIndex] ??= new List<FbxControlPointBoneWeight>();
                    weightsByControlPoint[controlPointIndex].Add(new FbxControlPointBoneWeight(boneIndex, weight));
                }
            }

            var packedWeights = new IReadOnlyList<FbxControlPointBoneWeight>[weightsByControlPoint.Length];
            for (int i = 0; i < packedWeights.Length; i++)
            {
                packedWeights[i] = FbxCollection.ToReadOnly(
                    weightsByControlPoint[i]?
                        .OrderByDescending(weight => weight.Weight)
                        .ToArray() ?? Array.Empty<FbxControlPointBoneWeight>());
            }

            return new FbxSkinDeformer(skin.Id, skin.Name, bones, clusters, packedWeights);
        }

        private static FbxMeshReadOptions GetAvailableOptions(
            GeometryRecord geometry,
            IEnumerable<FbxDeformer> deformers,
            FbxMeshReadOptions requestedOptions)
        {
            FbxMeshReadOptions options = FbxMeshReadOptions.None;
            if (geometry.ControlPointPositions.Length > 0)
            {
                options |= FbxMeshReadOptions.ControlPointPositions;
            }

            var deformerList = deformers as IReadOnlyList<FbxDeformer> ?? deformers.ToArray();
            if (Includes(requestedOptions, FbxMeshReadOptions.BlendShapes) &&
                deformerList.OfType<FbxBlendShapeDeformer>().Any(blendShape => blendShape.Channels.Count > 0))
            {
                options |= FbxMeshReadOptions.BlendShapes;
            }

            if (Includes(requestedOptions, FbxMeshReadOptions.BoneWeights) &&
                deformerList.OfType<FbxSkinDeformer>().Any(skin => skin.HasWeights))
            {
                options |= FbxMeshReadOptions.BoneWeights;
            }

            return options;
        }

        private static IEnumerable<GeometryRecord> FindChildShapeGeometries(BinaryFbxScene scene, long parentId)
        {
            if (!scene.ChildrenByParent.TryGetValue(parentId, out var children))
            {
                yield break;
            }

            foreach (long childId in children)
            {
                if (scene.Geometries.TryGetValue(childId, out var geometry) &&
                    string.Equals(geometry.Type, "Shape", StringComparison.Ordinal))
                {
                    yield return geometry;
                }
            }
        }

        private static IEnumerable<TDeformer> FindChildDeformers<TDeformer>(BinaryFbxScene scene, long parentId)
            where TDeformer : DeformerRecord
        {
            if (!scene.ChildrenByParent.TryGetValue(parentId, out var children))
            {
                yield break;
            }

            foreach (long childId in children)
            {
                if (scene.Deformers.TryGetValue(childId, out var deformer) &&
                    deformer is TDeformer typedDeformer)
                {
                    yield return typedDeformer;
                }
            }
        }

        private static double GetFrameWeight(BlendShapeChannelRecord channel, int frameIndex, int frameCount)
        {
            if (channel?.FullWeights != null &&
                frameIndex >= 0 &&
                frameIndex < channel.FullWeights.Length)
            {
                return channel.FullWeights[frameIndex];
            }

            return frameCount > 0 ? 100.0 * (frameIndex + 1) / frameCount : 0.0;
        }

        private static long ResolveClusterBoneNodeId(BinaryFbxScene scene, ClusterDeformerRecord cluster)
        {
            if (scene.ChildrenByParent.TryGetValue(cluster.Id, out var children))
            {
                foreach (long childId in children)
                {
                    if (scene.Models.ContainsKey(childId))
                    {
                        return childId;
                    }
                }
            }

            return 0;
        }

        private static string ResolveClusterBoneName(
            BinaryFbxScene scene,
            ClusterDeformerRecord cluster,
            FbxSceneNode boneNode)
        {
            if (!string.IsNullOrEmpty(boneNode?.Name))
            {
                return boneNode.Name;
            }

            return string.IsNullOrEmpty(cluster.Name) ? $"Cluster_{cluster.Id}" : cluster.Name;
        }

        private static string ResolveClusterBonePath(ClusterDeformerRecord cluster, FbxSceneNode boneNode)
        {
            if (!string.IsNullOrEmpty(boneNode?.Path))
            {
                return boneNode.Path;
            }

            return ResolveClusterBoneName(null, cluster, boneNode);
        }

        private static int GetOrAddBoneBinding(
            List<FbxBoneBinding> bones,
            Dictionary<long, int> boneIndexByNodeId,
            Dictionary<string, int> boneIndexByFallbackKey,
            long nodeId,
            FbxSceneNode node,
            string boneName,
            string bonePath)
        {
            boneName = string.IsNullOrEmpty(boneName) ? "<unnamed>" : boneName;
            bonePath = bonePath ?? string.Empty;

            if (nodeId != 0 && boneIndexByNodeId.TryGetValue(nodeId, out int index))
            {
                return index;
            }

            string fallbackKey = !string.IsNullOrEmpty(bonePath) ? bonePath : boneName;
            if (nodeId == 0 && boneIndexByFallbackKey.TryGetValue(fallbackKey, out index))
            {
                return index;
            }

            index = bones.Count;
            bones.Add(new FbxBoneBinding(index, nodeId, boneName, bonePath, node));
            if (nodeId != 0)
            {
                boneIndexByNodeId.Add(nodeId, index);
            }
            else
            {
                boneIndexByFallbackKey.Add(fallbackKey, index);
            }

            return index;
        }

        private static FbxSceneNodeType ToSceneNodeType(string type)
        {
            return type switch
            {
                "Null" => FbxSceneNodeType.Null,
                "Mesh" => FbxSceneNodeType.Mesh,
                "Skeleton" => FbxSceneNodeType.Skeleton,
                "LimbNode" => FbxSceneNodeType.LimbNode,
                "Root" => FbxSceneNodeType.Root,
                _ => FbxSceneNodeType.Unknown
            };
        }

        private static string BuildModelPath(BinaryFbxScene scene, long modelId)
        {
            var parts = new Stack<string>();
            var seen = new HashSet<long>();
            long currentId = modelId;
            while (currentId != 0 && seen.Add(currentId) && scene.Models.TryGetValue(currentId, out var model))
            {
                if (!string.IsNullOrEmpty(model.Name))
                {
                    parts.Push(model.Name);
                }

                if (!scene.ObjectParents.TryGetValue(currentId, out currentId))
                {
                    break;
                }
            }

            return string.Join("/", parts);
        }

        private static DeformerRecord CreateDeformerRecord(
            long id,
            string name,
            string type,
            int[] indices,
            double[] weights,
            double[] fullWeights,
            FbxClusterLinkMode linkMode,
            FbxMatrix4x4 transformMatrix,
            bool hasTransformMatrix,
            FbxMatrix4x4 transformLinkMatrix,
            bool hasTransformLinkMatrix,
            FbxMatrix4x4 transformAssociateModelMatrix,
            bool hasTransformAssociateModelMatrix)
        {
            switch (type)
            {
                case "Skin":
                    return new SkinDeformerRecord(id, name);
                case "Cluster":
                    return new ClusterDeformerRecord(
                        id,
                        name,
                        indices,
                        weights,
                        linkMode,
                        transformMatrix,
                        hasTransformMatrix,
                        transformLinkMatrix,
                        hasTransformLinkMatrix,
                        transformAssociateModelMatrix,
                        hasTransformAssociateModelMatrix);
                case "BlendShape":
                    return new BlendShapeDeformerRecord(id, name);
                case "BlendShapeChannel":
                    return new BlendShapeChannelRecord(id, name, fullWeights);
                case "VertexCache":
                    return new VertexCacheDeformerRecord(id, name);
                default:
                    return new UnknownDeformerRecord(id, name, type);
            }
        }

        private static void ValidateHeader(BinaryReader reader)
        {
            byte[] header = reader.ReadBytes(BinaryHeader.Length);
            if (header.Length != BinaryHeader.Length || !header.SequenceEqual(BinaryHeader))
            {
                throw new InvalidDataException("File is not a binary FBX.");
            }
        }

        private static bool TryReadNodeHeader(
            BinaryReader reader,
            int version,
            long parentEndOffset,
            out FbxNodeHeader header)
        {
            header = default;
            int scalarSize = version >= 7500 ? 8 : 4;
            int headerSize = scalarSize * 3 + 1;
            if (reader.BaseStream.Position + headerSize > parentEndOffset)
            {
                return false;
            }

            long endOffset = ReadOffset(reader, version);
            long propertyCount = ReadOffset(reader, version);
            long propertyListLength = ReadOffset(reader, version);
            byte nameLength = reader.ReadByte();
            if (endOffset == 0 && propertyCount == 0 && propertyListLength == 0 && nameLength == 0)
            {
                header = new FbxNodeHeader { IsNull = true };
                return true;
            }

            if (endOffset < reader.BaseStream.Position || endOffset > reader.BaseStream.Length)
            {
                throw new InvalidDataException($"Invalid FBX node end offset: {endOffset}.");
            }

            header = new FbxNodeHeader
            {
                EndOffset = endOffset,
                PropertyCount = propertyCount,
                Name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength))
            };
            return true;
        }

        private static object[] ReadProperties(BinaryReader reader, long propertyCount, bool readArrayValues)
        {
            if (propertyCount <= 0)
            {
                return Array.Empty<object>();
            }

            if (propertyCount > int.MaxValue)
            {
                throw new InvalidDataException($"Unsupported FBX property count: {propertyCount}.");
            }

            var properties = new object[(int)propertyCount];
            for (int i = 0; i < properties.Length; i++)
            {
                properties[i] = ReadProperty(reader, readArrayValues);
            }

            return properties;
        }

        private static object ReadProperty(BinaryReader reader, bool readArrayValues)
        {
            char typeCode = (char)reader.ReadByte();
            switch (typeCode)
            {
                case 'Y':
                    return reader.ReadInt16();
                case 'C':
                    return reader.ReadByte() != 0;
                case 'I':
                    return reader.ReadInt32();
                case 'F':
                    return reader.ReadSingle();
                case 'D':
                    return reader.ReadDouble();
                case 'L':
                    return reader.ReadInt64();
                case 'S':
                    return ReadStringProperty(reader);
                case 'R':
                    SkipBytes(reader, reader.ReadInt32());
                    return null;
                case 'f':
                case 'd':
                case 'l':
                case 'i':
                case 'b':
                case 'c':
                    return ReadArrayProperty(reader, typeCode, readArrayValues);
                default:
                    throw new InvalidDataException($"Unsupported FBX property type '{typeCode}'.");
            }
        }

        private static object ReadArrayProperty(BinaryReader reader, char typeCode, bool readValues)
        {
            int length = reader.ReadInt32();
            int encoding = reader.ReadInt32();
            int encodedByteCount = reader.ReadInt32();
            if (length < 0 || encodedByteCount < 0)
            {
                throw new InvalidDataException($"Invalid FBX array length: {length} ({encodedByteCount} bytes).");
            }

            if (!readValues)
            {
                SkipBytes(reader, encodedByteCount);
                return new SkippedArrayProperty(typeCode, length);
            }

            if (encoding == 0)
            {
                return ReadTypedArray(reader, typeCode, length);
            }

            byte[] decompressed = InflateZlib(reader.ReadBytes(encodedByteCount));
            using var decompressedStream = new MemoryStream(decompressed);
            using var decompressedReader = new BinaryReader(decompressedStream);
            return ReadTypedArray(decompressedReader, typeCode, length);
        }

        private static object ReadTypedArray(BinaryReader reader, char typeCode, int length)
        {
            switch (typeCode)
            {
                case 'f':
                    var floats = new float[length];
                    for (int i = 0; i < floats.Length; i++) floats[i] = reader.ReadSingle();
                    return floats;
                case 'd':
                    var doubles = new double[length];
                    for (int i = 0; i < doubles.Length; i++) doubles[i] = reader.ReadDouble();
                    return doubles;
                case 'l':
                    var longs = new long[length];
                    for (int i = 0; i < longs.Length; i++) longs[i] = reader.ReadInt64();
                    return longs;
                case 'i':
                    var ints = new int[length];
                    for (int i = 0; i < ints.Length; i++) ints[i] = reader.ReadInt32();
                    return ints;
                case 'b':
                case 'c':
                    var bools = new bool[length];
                    for (int i = 0; i < bools.Length; i++) bools[i] = reader.ReadByte() != 0;
                    return bools;
                default:
                    return null;
            }
        }

        private static byte[] InflateZlib(byte[] bytes)
        {
            if (bytes == null || bytes.Length <= 6)
            {
                return Array.Empty<byte>();
            }

            using var compressed = new MemoryStream(bytes, 2, bytes.Length - 6);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            deflate.CopyTo(decompressed);
            return decompressed.ToArray();
        }

        private static string ReadStringProperty(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException($"Invalid FBX string length: {length}.");
            }

            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static long ReadOffset(BinaryReader reader, int version)
        {
            return version >= 7500 ? reader.ReadInt64() : reader.ReadUInt32();
        }

        private static bool Includes(FbxMeshReadOptions readOptions, FbxMeshReadOptions option)
        {
            return (readOptions & option) == option;
        }

        private static long GetLong(object[] properties, int index)
        {
            if (properties == null || index < 0 || index >= properties.Length)
            {
                return 0;
            }

            return properties[index] switch
            {
                long l  => l,
                int i   => i,
                short s => s,
                _       => 0
            };
        }

        private static string GetString(object[] properties, int index)
        {
            return properties != null && index >= 0 && index < properties.Length
                ? properties[index] as string
                : null;
        }

        private static int GetArrayLength(object[] properties)
        {
            if (properties == null)
            {
                return 0;
            }

            foreach (object property in properties)
            {
                if (property is SkippedArrayProperty skipped)
                {
                    return skipped.Length;
                }
            }

            return 0;
        }

        private static int[] GetIntArray(object[] properties)
        {
            if (properties == null)
            {
                return null;
            }

            foreach (object property in properties)
            {
                if (property is int[] intValues)
                {
                    return intValues;
                }

                if (property is long[] longValues)
                {
                    return longValues.Select(value => (int)value).ToArray();
                }
            }

            return null;
        }

        private static double[] GetDoubleArray(object[] properties)
        {
            if (properties == null)
            {
                return null;
            }

            foreach (object property in properties)
            {
                if (property is double[] doubleValues)
                {
                    return doubleValues;
                }

                if (property is float[] floatValues)
                {
                    return floatValues.Select(value => (double)value).ToArray();
                }
            }

            return null;
        }

        private static double GetDouble(object[] properties, int index, double defaultValue = 0d)
        {
            if (properties == null || index < 0 || index >= properties.Length)
            {
                return defaultValue;
            }

            return properties[index] switch
            {
                double d => d,
                float f => f,
                long l => l,
                int i => i,
                short s => s,
                _ => defaultValue
            };
        }

        private static Vector3d GetVector3d(object[] properties, int startIndex, Vector3d defaultValue)
        {
            if (properties == null || startIndex < 0 || startIndex + 2 >= properties.Length)
            {
                return defaultValue;
            }

            return new Vector3d(
                GetDouble(properties, startIndex, defaultValue.x),
                GetDouble(properties, startIndex + 1, defaultValue.y),
                GetDouble(properties, startIndex + 2, defaultValue.z));
        }

        private static FbxMatrix4x4 GetMatrix(object[] properties, out bool hasMatrix)
        {
            var values = GetDoubleArray(properties);
            hasMatrix = values != null && values.Length >= 16;
            return hasMatrix ? FbxMatrix4x4.FromRowMajor(values) : FbxMatrix4x4.Identity;
        }

        private static FbxClusterLinkMode ParseClusterLinkMode(object[] properties)
        {
            if (properties == null || properties.Length == 0)
            {
                return FbxClusterLinkMode.Unknown;
            }

            string value = GetString(properties, 0);
            if (!string.IsNullOrEmpty(value))
            {
                value = value.Trim();
                return value switch
                {
                    "Normalize" or "eNormalize" => FbxClusterLinkMode.Normalize,
                    "Additive" or "eAdditive" => FbxClusterLinkMode.Additive,
                    "TotalOne" or "eTotalOne" => FbxClusterLinkMode.TotalOne,
                    _ => FbxClusterLinkMode.Unknown
                };
            }

            return GetLong(properties, 0) switch
            {
                0 => FbxClusterLinkMode.Normalize,
                1 => FbxClusterLinkMode.Additive,
                2 => FbxClusterLinkMode.TotalOne,
                _ => FbxClusterLinkMode.Unknown
            };
        }

        private static Vector3d[] ToVector3dArray(double[] values)
        {
            int count = values.Length / 3;
            var positions = new Vector3d[count];
            for (int i = 0; i < positions.Length; i++)
            {
                int valueIndex = i * 3;
                positions[i] = new Vector3d(
                    values[valueIndex],
                    values[valueIndex + 1],
                    values[valueIndex + 2]);
            }

            return positions;
        }

        private static void SkipBytes(BinaryReader reader, int length)
        {
            if (length < 0)
            {
                throw new InvalidDataException($"Invalid byte length: {length}.");
            }

            reader.BaseStream.Seek(length, SeekOrigin.Current);
        }

        private static void Seek(BinaryReader reader, long offset)
        {
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
        }

        private readonly struct SkippedArrayProperty
        {
            public readonly char TypeCode;
            public readonly int Length;

            public SkippedArrayProperty(char typeCode, int length)
            {
                TypeCode = typeCode;
                Length = length;
            }
        }

        private struct FbxNodeHeader
        {
            public long EndOffset;
            public long PropertyCount;
            public string Name;
            public bool IsNull;
        }

        private sealed class BinaryFbxScene
        {
            public readonly Dictionary<long, GeometryRecord> Geometries = new();
            public readonly Dictionary<long, ModelRecord> Models = new();
            public readonly Dictionary<long, DeformerRecord> Deformers = new();
            public readonly Dictionary<long, long> ObjectParents = new();
            public readonly Dictionary<long, List<long>> ChildrenByParent = new();
            public readonly List<FbxDiagnostic> Diagnostics = new();
        }

        private sealed class GeometryRecord
        {
            public long Id;
            public string Name;
            public string Type;
            public int[] Indices;
            public int ControlPointCount;
            public Vector3d[] ControlPointPositions;
        }

        private sealed class ModelRecord
        {
            public long Id;
            public string Name;
            public string Type;
            public FbxTransform LocalTransform;
        }

        private abstract class DeformerRecord
        {
            public long Id { get; }
            public string Name { get; }
            public string Type { get; }

            protected DeformerRecord(long id, string name, string type)
            {
                Id = id;
                Name = name;
                Type = type;
            }
        }

        private sealed class SkinDeformerRecord : DeformerRecord
        {
            public SkinDeformerRecord(long id, string name)
                : base(id, name, "Skin")
            {
            }
        }

        private sealed class ClusterDeformerRecord : DeformerRecord
        {
            public int[] Indices { get; }
            public double[] Weights { get; }
            public FbxClusterLinkMode LinkMode { get; }
            public FbxMatrix4x4 TransformMatrix { get; }
            public FbxMatrix4x4 TransformLinkMatrix { get; }
            public FbxMatrix4x4 TransformAssociateModelMatrix { get; }
            public bool HasTransformMatrix { get; }
            public bool HasTransformLinkMatrix { get; }
            public bool HasTransformAssociateModelMatrix { get; }

            public ClusterDeformerRecord(
                long id,
                string name,
                int[] indices,
                double[] weights,
                FbxClusterLinkMode linkMode,
                FbxMatrix4x4 transformMatrix,
                bool hasTransformMatrix,
                FbxMatrix4x4 transformLinkMatrix,
                bool hasTransformLinkMatrix,
                FbxMatrix4x4 transformAssociateModelMatrix,
                bool hasTransformAssociateModelMatrix)
                : base(id, name, "Cluster")
            {
                Indices = indices;
                Weights = weights;
                LinkMode = linkMode;
                TransformMatrix = transformMatrix;
                HasTransformMatrix = hasTransformMatrix;
                TransformLinkMatrix = transformLinkMatrix;
                HasTransformLinkMatrix = hasTransformLinkMatrix;
                TransformAssociateModelMatrix = transformAssociateModelMatrix;
                HasTransformAssociateModelMatrix = hasTransformAssociateModelMatrix;
            }
        }

        private sealed class BlendShapeDeformerRecord : DeformerRecord
        {
            public BlendShapeDeformerRecord(long id, string name)
                : base(id, name, "BlendShape")
            {
            }
        }

        private sealed class BlendShapeChannelRecord : DeformerRecord
        {
            public double[] FullWeights { get; }

            public BlendShapeChannelRecord(long id, string name, double[] fullWeights)
                : base(id, name, "BlendShapeChannel")
            {
                FullWeights = fullWeights;
            }
        }

        private sealed class VertexCacheDeformerRecord : DeformerRecord
        {
            public VertexCacheDeformerRecord(long id, string name)
                : base(id, name, "VertexCache")
            {
            }
        }

        private sealed class UnknownDeformerRecord : DeformerRecord
        {
            public UnknownDeformerRecord(long id, string name, string type)
                : base(id, name, type)
            {
            }
        }

        private sealed class UnsupportedFbxVersionException : Exception
        {
            public UnsupportedFbxVersionException(int version)
                : base($"Unsupported FBX version: {version}.")
            {
            }
        }
    }
}
