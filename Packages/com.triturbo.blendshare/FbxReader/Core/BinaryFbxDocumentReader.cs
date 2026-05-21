using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Triturbo.Fbx
{
    public static class BinaryFbxDocumentReader
    {
        // Reference source for the binary FBX container layout:
        // https://code.blender.org/2013/08/fbx-binary-file-format-specification/
        // this reader implements only the nodes BlendShare needs.
        private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

        public static FbxReadResult<FbxDocument> Read(string assetPath, IEnumerable<string> nodePaths = null)
        {
            return ReadInternal(assetPath, nodePaths);
        }

        private static FbxReadResult<FbxDocument> ReadInternal(
            string assetPath,
            IEnumerable<string> nodePaths)
        {
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
                var requestedPaths = NormalizeRequestedNodePaths(nodePaths);
                if (requestedPaths.Length == 0)
                {
                    var fullScene = ReadScene(assetPath, FbxObjectReadFilter.All);
                    return FbxReadResult<FbxDocument>.Succeeded(
                        BuildDocument(fullScene, assetPath, null),
                        fullScene.Diagnostics);
                }

                var metadataScene = ReadScene(assetPath, FbxObjectReadFilter.MetadataOnly);
                var selectedModelIds = ResolveRequestedModelIds(metadataScene, requestedPaths, out var missingPaths);
                if (missingPaths.Count > 0)
                {
                    return FbxReadResult<FbxDocument>.Failed(
                        FbxReadStatus.NodeNotFound,
                        $"FBX node path was not found: {string.Join(", ", missingPaths)}.",
                        metadataScene.Diagnostics);
                }

                var materializedObjectIds = BuildMaterializedObjectIds(metadataScene, selectedModelIds);
                var includedModelIds = BuildIncludedModelIds(metadataScene, selectedModelIds, materializedObjectIds);
                var materializedScene = ReadScene(assetPath, new FbxObjectReadFilter(materializedObjectIds));
                return FbxReadResult<FbxDocument>.Succeeded(
                    BuildDocument(materializedScene, assetPath, includedModelIds),
                    materializedScene.Diagnostics);
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

        public static FbxDocument ReadOrThrow(string assetPath, IEnumerable<string> nodePaths = null)
        {
            var result = Read(assetPath, nodePaths);
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

        private static BinaryFbxScene ReadScene(string assetPath, FbxObjectReadFilter readFilter)
        {
            readFilter ??= FbxObjectReadFilter.All;
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
            // Top-level FBX nodes are section records. BlendShare only needs Objects
            // for models/geometries/deformers and Connections for their hierarchy.
            while (stream.Position < fileLength)
            {
                if (!TryReadNodeHeader(reader, version, fileLength, out var header) || header.IsNull)
                {
                    break;
                }

                _ = ReadProperties(reader, header.PropertyCount, false);
                if (header.Name == "Objects")
                {
                    ReadObjects(reader, version, header.EndOffset, scene, readFilter);
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
            FbxObjectReadFilter readFilter)
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
                    ReadGeometry(reader, version, header, properties, scene, readFilter);
                }
                else if (header.Name == "Model")
                {
                    ReadModel(reader, version, header, properties, scene);
                }
                else if (header.Name == "Deformer")
                {
                    ReadDeformer(reader, version, header, properties, scene, readFilter);
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
            FbxObjectReadFilter readFilter)
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

            bool materializePayload = readFilter.ShouldMaterialize(id);
            bool readBasePayload = isMesh && materializePayload;
            bool readShapePayload = isShape && materializePayload;

            // Mesh Geometry nodes hold base control points. Shape Geometry nodes hold
            // blendshape frame data, usually as sparse Indexes plus per-index values.
            double[] vertices = null;
            Vector3d[] normals = Array.Empty<Vector3d>();
            Vector3d[] tangents = Array.Empty<Vector3d>();
            int[] indices = null;
            int controlPointCount = 0;
            bool legacyStyle = true;
            bool absoluteMode = false;
            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                if (childHeader.Name == "LayerElementNormal")
                {
                    _ = ReadProperties(reader, childHeader.PropertyCount, false);
                    normals = ReadVectorLayerElement(
                        reader,
                        version,
                        childHeader.EndOffset,
                        "Normals",
                        "NormalsIndex",
                        readBasePayload || readShapePayload);
                    Seek(reader, childHeader.EndOffset);
                    continue;
                }

                if (childHeader.Name == "LayerElementTangent")
                {
                    _ = ReadProperties(reader, childHeader.PropertyCount, false);
                    tangents = ReadVectorLayerElement(
                        reader,
                        version,
                        childHeader.EndOffset,
                        "Tangents",
                        "TangentsIndex",
                        readBasePayload || readShapePayload);
                    Seek(reader, childHeader.EndOffset);
                    continue;
                }

                if (childHeader.Name == "Properties70")
                {
                    _ = ReadProperties(reader, childHeader.PropertyCount, false);
                    ReadGeometryProperties(reader, version, childHeader.EndOffset, ref legacyStyle, ref absoluteMode);
                    Seek(reader, childHeader.EndOffset);
                    continue;
                }

                bool isVerticesNode = childHeader.Name == "Vertices";
                bool isNormalsNode = childHeader.Name == "Normals";
                bool isTangentsNode = childHeader.Name == "Tangents";
                bool readArrayValues = (isVerticesNode && (readBasePayload || readShapePayload)) ||
                                       (isNormalsNode && (readBasePayload || readShapePayload)) ||
                                       (isTangentsNode && (readBasePayload || readShapePayload)) ||
                                       (readShapePayload && childHeader.Name == "Indexes");
                var childProperties = ReadProperties(reader, childHeader.PropertyCount, readArrayValues);
                if (isVerticesNode)
                {
                    vertices = GetDoubleArray(childProperties);
                    int vertexValueCount = vertices?.Length ?? GetArrayLength(childProperties);
                    controlPointCount = Math.Max(controlPointCount, vertexValueCount / 3);
                }
                else if (isNormalsNode)
                {
                    normals = ToVector3dArray(GetDoubleArray(childProperties));
                }
                else if (isTangentsNode)
                {
                    tangents = ToVector3dArray(GetDoubleArray(childProperties));
                }
                else if (isShape && childHeader.Name == "Indexes")
                {
                    indices = GetIntArray(childProperties);
                }
                else if (isShape && childHeader.Name == "LegacyStyle")
                {
                    legacyStyle = GetBool(childProperties, 0, legacyStyle);
                }
                else if (isShape && childHeader.Name == "AbsoluteMode")
                {
                    absoluteMode = GetBool(childProperties, 0, absoluteMode);
                }

                Seek(reader, childHeader.EndOffset);
            }

            if (vertices == null)
            {
                vertices = Array.Empty<double>();
            }

            if (materializePayload && vertices.Length < 3)
            {
                return;
            }

            // LegacyStyle/AbsoluteMode is FBX's way of distinguishing relative shape
            // deltas from absolute shape positions. BuildShapeFrame normalizes both
            // forms into deltas for downstream BlendShare code.
            scene.Geometries[id] = new GeometryRecord
            {
                Id = id,
                Name = name,
                Type = type,
                Indices = indices,
                ControlPointCount = controlPointCount,
                ControlPointPositions = ToVector3dArray(vertices),
                ControlPointNormals = normals ?? Array.Empty<Vector3d>(),
                ControlPointTangents = tangents ?? Array.Empty<Vector3d>(),
                IsPayloadMaterialized = materializePayload,
                ShapeValueMode = !legacyStyle && absoluteMode
                    ? FbxShapeValueMode.Absolute
                    : FbxShapeValueMode.Relative
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

        private static void ReadGeometryProperties(
            BinaryReader reader,
            int version,
            long endOffset,
            ref bool legacyStyle,
            ref bool absoluteMode)
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
                    if (propertyName == "LegacyStyle")
                    {
                        legacyStyle = GetBool(properties, 4, legacyStyle);
                    }
                    else if (propertyName == "AbsoluteMode")
                    {
                        absoluteMode = GetBool(properties, 4, absoluteMode);
                    }
                }

                Seek(reader, header.EndOffset);
            }
        }

        private static Vector3d[] ReadVectorLayerElement(
            BinaryReader reader,
            int version,
            long endOffset,
            string valuesNodeName,
            string indicesNodeName,
            bool readValues)
        {
            double[] values = null;
            int[] indices = null;
            string referenceMode = string.Empty;
            // Normals and tangents are LayerElement records. When they use an indexed
            // reference mode, the direct array must be expanded through the index array
            // before it can be associated with control points.
            while (reader.BaseStream.Position < endOffset)
            {
                if (!TryReadNodeHeader(reader, version, endOffset, out var header) || header.IsNull)
                {
                    break;
                }

                bool readArrayValues = readValues && (header.Name == valuesNodeName || header.Name == indicesNodeName);
                var properties = ReadProperties(reader, header.PropertyCount, readArrayValues);
                if (header.Name == valuesNodeName)
                {
                    values = GetDoubleArray(properties);
                }
                else if (header.Name == indicesNodeName)
                {
                    indices = GetIntArray(properties);
                }
                else if (header.Name == "ReferenceInformationType")
                {
                    referenceMode = GetString(properties, 0) ?? string.Empty;
                }

                Seek(reader, header.EndOffset);
            }

            if (!readValues)
            {
                return Array.Empty<Vector3d>();
            }

            var directValues = ToVector3dArray(values);
            if (directValues.Length == 0 || indices == null || indices.Length == 0)
            {
                return directValues;
            }

            if (referenceMode.IndexOf("Index", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return directValues;
            }

            var indexedValues = new Vector3d[indices.Length];
            for (int i = 0; i < indexedValues.Length; i++)
            {
                int directIndex = indices[i];
                indexedValues[i] = directIndex >= 0 && directIndex < directValues.Length
                    ? directValues[directIndex]
                    : Vector3d.zero;
            }

            return indexedValues;
        }

        private static void ReadDeformer(
            BinaryReader reader,
            int version,
            FbxNodeHeader header,
            object[] properties,
            BinaryFbxScene scene,
            FbxObjectReadFilter readFilter)
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
            bool materializePayload = readFilter.ShouldMaterialize(id);
            bool readBoneWeights = materializePayload && isCluster;
            bool readBlendShapeWeights = materializePayload && isBlendShapeChannel;

            // Deformer children carry the data for different FBX concepts:
            // Cluster -> skin weights and bind matrices,
            // BlendShapeChannel -> frame weights,
            // BlendShape/VertexCache -> grouping records.
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
                // Object-object connections are enough to rebuild model ownership,
                // deformer membership, blendshape frames, and bone links.
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

        private static FbxDocument BuildDocument(
            BinaryFbxScene scene,
            string assetPath,
            ISet<long> includedModelIds)
        {
            var rootNode = new FbxSceneNode(0, string.Empty, string.Empty, FbxSceneNodeType.Root, FbxTransform.Identity);
            // Parsing stores flat records keyed by FBX object id. This pass resolves
            // the connection table into the public node tree and mesh objects.
            var includedModels = includedModelIds != null
                ? scene.Models.Values.Where(model => includedModelIds.Contains(model.Id))
                : scene.Models.Values;
            var nodesById = includedModels.ToDictionary(
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

            foreach (var model in includedModels)
            {
                var node = nodesById[model.Id];
                if (model.Type == "Skeleton" || model.Type == "LimbNode")
                {
                    node.SetAttribute(new FbxSkeleton(model.Id, model.Name, node, model.Type));
                }

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

            foreach (var geometry in scene.Geometries.Values)
            {
                if (!string.Equals(geometry.Type, "Mesh", StringComparison.Ordinal) ||
                    !geometry.IsPayloadMaterialized)
                {
                    continue;
                }

                scene.ObjectParents.TryGetValue(geometry.Id, out long modelId);
                nodesById.TryGetValue(modelId, out var ownerNode);
                ownerNode ??= rootNode;

                int controlPointCount = Math.Max(geometry.ControlPointCount, geometry.ControlPointPositions.Length);
                var deformers = BuildDeformers(scene, geometry, controlPointCount, nodesById);
                var mesh = new FbxMeshGeometry(
                    geometry.Id,
                    geometry.Name,
                    ownerNode,
                    controlPointCount,
                    geometry.ControlPointPositions,
                    geometry.ControlPointNormals,
                    geometry.ControlPointTangents,
                    deformers);

                foreach (var deformer in deformers)
                {
                    deformer.OwnerMesh = mesh;
                }

                ownerNode.SetAttribute(mesh);
            }

            var nodes = new List<FbxSceneNode> { rootNode };
            nodes.AddRange(nodesById.Values);
            return new FbxDocument(
                rootNode,
                nodes,
                assetPath);
        }

        private static List<FbxDeformer> BuildDeformers(
            BinaryFbxScene scene,
            GeometryRecord geometry,
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
                        deformers.Add(BuildBlendShapeDeformer(scene, blendShape, geometry));
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
            GeometryRecord geometry)
        {
            var channels = new List<FbxBlendShapeChannel>();
            foreach (var channel in FindChildDeformers<BlendShapeChannelRecord>(scene, deformer.Id))
            {
                var frames = new List<FbxShapeFrame>();
                var shapes = FindChildShapeGeometries(scene, channel.Id).ToArray();
                for (int frameIndex = 0; frameIndex < shapes.Length; frameIndex++)
                {
                    frames.Add(BuildShapeFrame(
                        GetFrameWeight(channel, frameIndex, shapes.Length),
                        geometry,
                        shapes[frameIndex]));
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
            int controlPointCount = Math.Max(
                baseGeometry?.ControlPointCount ?? 0,
                baseGeometry?.ControlPointPositions?.Length ?? 0);
            var shapePositions = shapeGeometry?.ControlPointPositions ?? Array.Empty<Vector3d>();
            var shapeNormals = shapeGeometry?.ControlPointNormals ?? Array.Empty<Vector3d>();
            var shapeTangents = shapeGeometry?.ControlPointTangents ?? Array.Empty<Vector3d>();
            var valueMode = shapeGeometry?.ShapeValueMode ?? FbxShapeValueMode.Relative;
            bool isAbsolute = valueMode == FbxShapeValueMode.Absolute;
            if (controlPointCount == 0 || (shapePositions.Length == 0 && shapeNormals.Length == 0 && shapeTangents.Length == 0))
            {
                return new FbxShapeFrame(
                    frameWeight,
                    Array.Empty<int>(),
                    Array.Empty<Vector3d>(),
                    Array.Empty<Vector3d>(),
                    Array.Empty<Vector3d>(),
                    valueMode);
            }

            // Sparse shapes store only changed control point indices. Dense shapes
            // omit Indexes, so each shape vector corresponds to the same base index.
            if (shapeGeometry?.Indices != null && shapeGeometry.Indices.Length > 0)
            {
                int count = shapeGeometry.Indices.Length;
                var indices = new List<int>(count);
                var deltas = new List<Vector3d>(count);
                var normalDeltas = new List<Vector3d>(count);
                var tangentDeltas = new List<Vector3d>(count);
                for (int i = 0; i < count; i++)
                {
                    int controlPointIndex = shapeGeometry.Indices[i];
                    if (controlPointIndex < 0 || controlPointIndex >= controlPointCount)
                    {
                        continue;
                    }

                    var delta = GetShapeDelta(
                        shapePositions,
                        baseGeometry.ControlPointPositions,
                        controlPointIndex,
                        i,
                        isAbsolute);
                    var normalDelta = GetShapeDelta(
                        shapeNormals,
                        baseGeometry.ControlPointNormals,
                        controlPointIndex,
                        i,
                        isAbsolute);
                    var tangentDelta = GetShapeDelta(
                        shapeTangents,
                        baseGeometry.ControlPointTangents,
                        controlPointIndex,
                        i,
                        isAbsolute);
                    if (delta.IsZero() && normalDelta.IsZero() && tangentDelta.IsZero())
                    {
                        continue;
                    }

                    indices.Add(controlPointIndex);
                    deltas.Add(delta);
                    normalDeltas.Add(normalDelta);
                    tangentDeltas.Add(tangentDelta);
                }

                return new FbxShapeFrame(frameWeight, indices, deltas, normalDeltas, tangentDeltas, valueMode);
            }

            int denseCount = Math.Min(
                controlPointCount,
                Math.Max(shapePositions.Length, Math.Max(shapeNormals.Length, shapeTangents.Length)));
            var denseIndices = new List<int>(denseCount);
            var denseDeltas = new List<Vector3d>(denseCount);
            var denseNormalDeltas = new List<Vector3d>(denseCount);
            var denseTangentDeltas = new List<Vector3d>(denseCount);
            for (int i = 0; i < denseCount; i++)
            {
                var delta = GetShapeDelta(shapePositions, baseGeometry.ControlPointPositions, i, i, isAbsolute);
                var normalDelta = GetShapeDelta(shapeNormals, baseGeometry.ControlPointNormals, i, i, isAbsolute);
                var tangentDelta = GetShapeDelta(shapeTangents, baseGeometry.ControlPointTangents, i, i, isAbsolute);
                if (delta.IsZero() && normalDelta.IsZero() && tangentDelta.IsZero())
                {
                    continue;
                }

                denseIndices.Add(i);
                denseDeltas.Add(delta);
                denseNormalDeltas.Add(normalDelta);
                denseTangentDeltas.Add(tangentDelta);
            }

            return new FbxShapeFrame(frameWeight, denseIndices, denseDeltas, denseNormalDeltas, denseTangentDeltas, valueMode);
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

            // Each Cluster links one bone model to weighted control point indices.
            // Keeping cluster metadata lets callers inspect bind matrices even when
            // Unity later repacks weights for Mesh.boneWeights.
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
                    null,
                    null,
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

        private static string[] NormalizeRequestedNodePaths(IEnumerable<string> nodePaths)
        {
            return nodePaths?
                .Select(FbxNameUtility.NormalizePath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }

        private static HashSet<long> ResolveRequestedModelIds(
            BinaryFbxScene scene,
            IReadOnlyList<string> nodePaths,
            out List<string> missingPaths)
        {
            missingPaths = new List<string>();
            var modelIdsByPath = scene.Models.Values
                .GroupBy(model => FbxNameUtility.NormalizePath(BuildModelPath(scene, model.Id)))
                .ToDictionary(group => group.Key, group => group.Select(model => model.Id).ToArray(), StringComparer.Ordinal);
            var selected = new HashSet<long>();

            foreach (string path in nodePaths ?? Array.Empty<string>())
            {
                if (modelIdsByPath.TryGetValue(path, out var modelIds) && modelIds.Length == 1)
                {
                    selected.Add(modelIds[0]);
                }
                else
                {
                    missingPaths.Add(path);
                }
            }

            return selected;
        }

        private static HashSet<long> BuildMaterializedObjectIds(BinaryFbxScene scene, IEnumerable<long> selectedModelIds)
        {
            var objectIds = new HashSet<long>();
            foreach (long modelId in selectedModelIds ?? Enumerable.Empty<long>())
            {
                objectIds.Add(modelId);

                foreach (var geometry in scene.Geometries.Values.Where(geometry =>
                             string.Equals(geometry.Type, "Mesh", StringComparison.Ordinal) &&
                             scene.ObjectParents.TryGetValue(geometry.Id, out long ownerId) &&
                             ownerId == modelId))
                {
                    objectIds.Add(geometry.Id);
                    AddChildDependencies(scene, geometry.Id, objectIds);
                }
            }

            return objectIds;
        }

        private static HashSet<long> BuildIncludedModelIds(
            BinaryFbxScene scene,
            IEnumerable<long> selectedModelIds,
            ISet<long> materializedObjectIds)
        {
            var modelIds = new HashSet<long>();
            foreach (long modelId in selectedModelIds ?? Enumerable.Empty<long>())
            {
                modelIds.Add(modelId);
            }

            foreach (long objectId in materializedObjectIds != null ? materializedObjectIds : Enumerable.Empty<long>())
            {
                if (scene.Models.ContainsKey(objectId))
                {
                    modelIds.Add(objectId);
                }
            }

            return modelIds;
        }

        private static void AddChildDependencies(BinaryFbxScene scene, long parentId, HashSet<long> objectIds)
        {
            if (!scene.ChildrenByParent.TryGetValue(parentId, out var children))
            {
                return;
            }

            foreach (long childId in children)
            {
                if (scene.Geometries.TryGetValue(childId, out var geometry))
                {
                    if (string.Equals(geometry.Type, "Shape", StringComparison.Ordinal))
                    {
                        objectIds.Add(childId);
                    }
                    continue;
                }

                if (scene.Deformers.ContainsKey(childId))
                {
                    objectIds.Add(childId);
                    AddChildDependencies(scene, childId, objectIds);
                    continue;
                }

                if (scene.Models.ContainsKey(childId))
                {
                    objectIds.Add(childId);
                }
            }
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

        private static Vector3d GetShapeDelta(
            IReadOnlyList<Vector3d> shapeValues,
            IReadOnlyList<Vector3d> baseValues,
            int controlPointIndex,
            int shapeValueIndex,
            bool isAbsolute)
        {
            if (shapeValues == null || shapeValueIndex < 0 || shapeValueIndex >= shapeValues.Count)
            {
                return Vector3d.zero;
            }

            var shapeValue = shapeValues[shapeValueIndex];
            if (!isAbsolute)
            {
                return shapeValue;
            }

            return baseValues != null && controlPointIndex >= 0 && controlPointIndex < baseValues.Count
                ? shapeValue - baseValues[controlPointIndex]
                : shapeValue;
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
            // FBX 7500 and newer widened node offsets/counts from 32-bit to 64-bit.
            // A zero-filled header is the null sentinel that terminates a child list.
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
            // Property type codes follow the binary FBX scalar/array encoding. Heavy
            // arrays are optionally skipped so metadata-only scans avoid allocation.
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

            // FBX array payloads are either raw little-endian values or zlib-wrapped
            // deflate streams, depending on the encoding flag.
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

            // DeflateStream consumes the deflate payload, so strip the two-byte zlib
            // header and trailing Adler-32 checksum stored by FBX.
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

        private static bool GetBool(object[] properties, int index, bool defaultValue = false)
        {
            if (properties == null || index < 0 || index >= properties.Length)
            {
                return defaultValue;
            }

            return properties[index] switch
            {
                bool value => value,
                long l => l != 0,
                int i => i != 0,
                short s => s != 0,
                byte value => value != 0,
                string text when bool.TryParse(text, out bool parsed) => parsed,
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
            if (values == null || values.Length < 3)
            {
                return Array.Empty<Vector3d>();
            }

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
            // Metadata-only reads keep array lengths without materializing the values.
            // This preserves descriptor data such as control point counts.
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

        private sealed class FbxObjectReadFilter
        {
            private readonly ISet<long> materializedObjectIds;

            public static readonly FbxObjectReadFilter All = new(null, true);
            public static readonly FbxObjectReadFilter MetadataOnly = new(Array.Empty<long>());

            public FbxObjectReadFilter(IEnumerable<long> materializedObjectIds)
                : this(materializedObjectIds, false)
            {
            }

            private FbxObjectReadFilter(IEnumerable<long> materializedObjectIds, bool materializeAll = false)
            {
                this.materializedObjectIds = materializedObjectIds != null
                    ? new HashSet<long>(materializedObjectIds)
                    : null;
                MaterializeAll = materializeAll;
            }

            public bool MaterializeAll { get; }

            public bool ShouldMaterialize(long objectId)
            {
                return MaterializeAll || (materializedObjectIds != null && materializedObjectIds.Contains(objectId));
            }
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
            public Vector3d[] ControlPointNormals;
            public Vector3d[] ControlPointTangents;
            public bool IsPayloadMaterialized;
            public FbxShapeValueMode ShapeValueMode;
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
