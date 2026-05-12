using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.FbxReader
{
    /// <summary>
    /// Reads selected mesh data directly from binary FBX files without loading the full FBX SDK scene.
    /// </summary>
    public static class BinaryFbxMeshReader
    {
        private const string LogPrefix = "[BlendShare Binary FBX Reader]";
        private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

        /// <summary>
        /// Reads one mesh snapshot from the binary FBX file referenced by a Unity FBX asset.
        /// </summary>
        /// <param name="fbxAsset">Unity FBX asset whose source file should be read.</param>
        /// <param name="meshPathOrName">FBX mesh geometry name, node name, or root-relative node path.</param>
        /// <param name="snapshot">Decoded mesh snapshot when the requested mesh is found.</param>
        /// <param name="readOptions">Data sections to decode from the binary FBX file.</param>
        /// <returns>True when a matching mesh snapshot was decoded.</returns>
        public static bool TryReadMesh(
            GameObject fbxAsset,
            string meshPathOrName,
            out FbxMeshSnapshot snapshot,
            FbxMeshReadOptions readOptions = FbxMeshReadOptions.All)
        {
            snapshot = null;
            if (string.IsNullOrEmpty(meshPathOrName))
            {
                return false;
            }

            var snapshots = TryReadMeshes(fbxAsset, new[] { meshPathOrName }, readOptions);
            return snapshots.TryGetValue(meshPathOrName, out snapshot);
        }

        /// <summary>
        /// Reads mesh snapshots from the binary FBX file referenced by a Unity FBX asset.
        /// </summary>
        /// <param name="fbxAsset">Unity FBX asset whose source file should be read.</param>
        /// <param name="meshPathsOrNames">FBX mesh geometry names, node names, or root-relative node paths.</param>
        /// <param name="readOptions">Data sections to decode from the binary FBX file.</param>
        /// <returns>Decoded mesh snapshots keyed by the requested path or name that matched each mesh.</returns>
        public static Dictionary<string, FbxMeshSnapshot> TryReadMeshes(
            GameObject fbxAsset,
            IEnumerable<string> meshPathsOrNames,
            FbxMeshReadOptions readOptions = FbxMeshReadOptions.All)
        {
            if (fbxAsset == null)
            {
                return new Dictionary<string, FbxMeshSnapshot>();
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
            return TryReadMeshes(assetPath, meshPathsOrNames, readOptions);
        }

        /// <summary>
        /// Reads mesh snapshots from a binary FBX file.
        /// </summary>
        /// <param name="assetPath">Project-relative path to the source binary FBX file.</param>
        /// <param name="meshPathsOrNames">FBX mesh geometry names, node names, or root-relative node paths.</param>
        /// <param name="readOptions">Data sections to decode from the binary FBX file.</param>
        /// <returns>Decoded mesh snapshots keyed by the requested path or name that matched each mesh.</returns>
        public static Dictionary<string, FbxMeshSnapshot> TryReadMeshes(
            string assetPath,
            IEnumerable<string> meshPathsOrNames,
            FbxMeshReadOptions readOptions = FbxMeshReadOptions.All)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            readOptions = NormalizeReadOptions(readOptions);
            var requestedKeys = meshPathsOrNames?
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Distinct()
                .ToArray() ?? Array.Empty<string>();

            if (string.IsNullOrEmpty(assetPath) || requestedKeys.Length == 0 || !File.Exists(assetPath))
            {
                return new Dictionary<string, FbxMeshSnapshot>();
            }

            try
            {
                var scene = ReadScene(assetPath, readOptions);
                var snapshots = BuildRequestedSnapshots(scene, requestedKeys, GetImportScale(assetPath), readOptions);
                Debug.Log(
                    $"{LogPrefix} TryReadMeshes finished in {stopwatch.Elapsed.TotalMilliseconds:0.###} ms (found={snapshots.Count}/{requestedKeys.Length})");
                return snapshots;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is NotSupportedException)
            {
                Debug.LogWarning($"{LogPrefix} Failed to read '{assetPath}': {exception.Message}");
                return new Dictionary<string, FbxMeshSnapshot>();
            }
        }

        private static FbxMeshReadOptions NormalizeReadOptions(FbxMeshReadOptions readOptions)
        {
            if (IncludesAny(readOptions, FbxMeshReadOptions.BlendShapes | FbxMeshReadOptions.BoneWeights))
            {
                readOptions |= FbxMeshReadOptions.ControlPointPositions;
            }

            return readOptions;
        }

        private static BinaryFbxScene ReadScene(string assetPath, FbxMeshReadOptions readOptions)
        {
            using var stream = File.OpenRead(assetPath);
            using var reader = new BinaryReader(stream);

            ValidateHeader(reader);
            int version = reader.ReadInt32();
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
                    ReadModel(header, properties, scene);
                }
                else if (header.Name == "Deformer" && IncludesAny(
                             readOptions,
                             FbxMeshReadOptions.BlendShapes | FbxMeshReadOptions.BoneWeights))
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
            string name = CleanObjectName(GetString(properties, 1));
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
            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                bool readArrayValues = (childHeader.Name == "Vertices" && (readBasePositions || readShapePositions)) ||
                                       (readShapePositions && childHeader.Name == "Indexes");
                var childProperties = ReadProperties(reader, childHeader.PropertyCount, readArrayValues);
                if (childHeader.Name == "Vertices")
                {
                    vertices = GetDoubleArray(childProperties);
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
                ControlPointPositions = vertices != null
                    ? ToVector3dArray(vertices)
                    : Array.Empty<Vector3d>()
            };
        }

        private static void ReadModel(FbxNodeHeader header, object[] properties, BinaryFbxScene scene)
        {
            long id = GetLong(properties, 0);
            if (id == 0)
            {
                return;
            }

            scene.Models[id] = new ModelRecord
            {
                Id = id,
                Name = CleanObjectName(GetString(properties, 1))
            };
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

            string name = CleanObjectName(GetString(properties, 1));
            string type = GetString(properties, 2);
            bool readBoneWeights = Includes(readOptions, FbxMeshReadOptions.BoneWeights) &&
                                   (string.Equals(type, "Skin", StringComparison.Ordinal) ||
                                    string.Equals(type, "Cluster", StringComparison.Ordinal));
            bool readBlendShape = Includes(readOptions, FbxMeshReadOptions.BlendShapes) &&
                                  (string.Equals(type, "BlendShape", StringComparison.Ordinal) ||
                                   string.Equals(type, "BlendShapeChannel", StringComparison.Ordinal));
            if (!readBoneWeights && !readBlendShape)
            {
                return;
            }

            int[] indices = null;
            double[] weights = null;
            double[] fullWeights = null;

            while (reader.BaseStream.Position < header.EndOffset)
            {
                if (!TryReadNodeHeader(reader, version, header.EndOffset, out var childHeader) || childHeader.IsNull)
                {
                    break;
                }

                bool readArrayValues = (readBoneWeights &&
                                        (childHeader.Name == "Indexes" || childHeader.Name == "Weights")) ||
                                       (readBlendShape && childHeader.Name == "FullWeights");
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

                Seek(reader, childHeader.EndOffset);
            }

            scene.Deformers[id] = CreateDeformerRecord(id, name, type, indices, weights, fullWeights);
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

        private static DeformerRecord CreateDeformerRecord(
            long id,
            string name,
            string type,
            int[] indices,
            double[] weights,
            double[] fullWeights)
        {
            switch (type)
            {
                case "Skin":
                    return new SkinDeformerRecord(id, name);
                case "Cluster":
                    return new ClusterDeformerRecord(id, name, indices, weights);
                case "BlendShape":
                    return new BlendShapeDeformerRecord(id, name);
                case "BlendShapeChannel":
                    return new BlendShapeChannelRecord(id, name, fullWeights);
                default:
                    return new UnknownDeformerRecord(id, name, type);
            }
        }

        private static Dictionary<string, FbxMeshSnapshot> BuildRequestedSnapshots(
            BinaryFbxScene scene,
            IReadOnlyList<string> requestedKeys,
            float importScale,
            FbxMeshReadOptions readOptions)
        {
            var meshLookup = BuildMeshLookup(scene);
            var snapshots = new Dictionary<string, FbxMeshSnapshot>();
            foreach (string requestedKey in requestedKeys)
            {
                string normalizedKey = NormalizePath(requestedKey);
                if (!meshLookup.TryGetValue(normalizedKey, out var mesh))
                {
                    continue;
                }

                snapshots[requestedKey] = new FbxMeshSnapshot(
                    mesh.MeshName,
                    mesh.Geometry.ControlPointPositions,
                    Includes(readOptions, FbxMeshReadOptions.BlendShapes)
                        ? BuildBlendShapes(scene, mesh.Geometry)
                        : Array.Empty<FbxBlendShapeSnapshot>(),
                    importScale,
                    Includes(readOptions, FbxMeshReadOptions.BoneWeights)
                        ? BuildBoneWeights(scene, mesh.Geometry)
                        : null,
                    readOptions,
                    mesh.NodePath);
            }

            return snapshots;
        }

        private static FbxBlendShapeSnapshot[] BuildBlendShapes(BinaryFbxScene scene, GeometryRecord geometry)
        {
            if (scene == null || geometry?.ControlPointPositions == null)
            {
                return Array.Empty<FbxBlendShapeSnapshot>();
            }

            var blendShapes = new List<FbxBlendShapeSnapshot>();
            foreach (var deformer in FindChildDeformers<BlendShapeDeformerRecord>(scene, geometry.Id))
            {
                foreach (var channel in FindChildDeformers<BlendShapeChannelRecord>(scene, deformer.Id))
                {
                    var shapes = FindChildShapeGeometries(scene, channel.Id).ToArray();
                    if (shapes.Length == 0)
                    {
                        continue;
                    }

                    var frames = new FbxBlendShapeFrameSnapshot[shapes.Length];
                    for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                    {
                        frames[frameIndex] = BuildBlendShapeFrame(
                            GetFrameWeight(channel, frameIndex, frames.Length),
                            geometry,
                            shapes[frameIndex]);
                    }

                    blendShapes.Add(new FbxBlendShapeSnapshot(
                        deformer.Name,
                        channel.Name,
                        frames));
                }
            }

            return blendShapes.ToArray();
        }

        private static IEnumerable<GeometryRecord> FindChildShapeGeometries(
            BinaryFbxScene scene,
            long parentId)
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

        private static FbxBlendShapeFrameSnapshot BuildBlendShapeFrame(
            double frameWeight,
            GeometryRecord baseGeometry,
            GeometryRecord shapeGeometry)
        {
            int controlPointCount = baseGeometry?.ControlPointPositions?.Length ?? 0;
            var shapePositions = shapeGeometry?.ControlPointPositions ?? Array.Empty<Vector3d>();
            if (controlPointCount == 0 || shapePositions.Length == 0)
            {
                return new FbxBlendShapeFrameSnapshot(
                    frameWeight,
                    Array.Empty<int>(),
                    Array.Empty<Vector3d>());
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

                return new FbxBlendShapeFrameSnapshot(frameWeight, indices.ToArray(), deltas.ToArray());
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

            return new FbxBlendShapeFrameSnapshot(frameWeight, denseIndices.ToArray(), denseDeltas.ToArray());
        }

        private static FbxBoneWeightSnapshot BuildBoneWeights(BinaryFbxScene scene, GeometryRecord geometry)
        {
            if (scene == null || geometry?.ControlPointPositions == null)
            {
                return null;
            }

            int controlPointCount = geometry.ControlPointPositions.Length;
            var weightsByControlPoint = new List<FbxControlPointBoneWeight>[controlPointCount];
            var boneNames = new List<string>();
            var boneNameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            bool hasWeights = false;

            foreach (var skin in FindChildDeformers<SkinDeformerRecord>(scene, geometry.Id))
            {
                foreach (var cluster in FindChildDeformers<ClusterDeformerRecord>(scene, skin.Id))
                {
                    if (cluster.Indices == null || cluster.Weights == null)
                    {
                        continue;
                    }

                    int boneIndex = GetOrAddBoneIndex(
                        boneNames,
                        boneNameToIndex,
                        ResolveClusterBoneName(scene, cluster));
                    int influenceCount = Math.Min(cluster.Indices.Length, cluster.Weights.Length);
                    for (int i = 0; i < influenceCount; i++)
                    {
                        int controlPointIndex = cluster.Indices[i];
                        if (controlPointIndex < 0 || controlPointIndex >= controlPointCount)
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
                        hasWeights = true;
                    }
                }
            }

            if (!hasWeights)
            {
                return null;
            }

            var packedWeights = new FbxControlPointBoneWeight[controlPointCount][];
            for (int i = 0; i < packedWeights.Length; i++)
            {
                packedWeights[i] = weightsByControlPoint[i]?
                    .OrderByDescending(weight => weight.Weight)
                    .ToArray() ?? Array.Empty<FbxControlPointBoneWeight>();
            }

            return new FbxBoneWeightSnapshot(boneNames.ToArray(), packedWeights);
        }

        private static IEnumerable<TDeformer> FindChildDeformers<TDeformer>(
            BinaryFbxScene scene,
            long parentId)
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

        private static string ResolveClusterBoneName(BinaryFbxScene scene, ClusterDeformerRecord cluster)
        {
            // The joint model is typically connected as a child of the cluster: OO(jointId, clusterId).
            if (scene.ChildrenByParent.TryGetValue(cluster.Id, out var children))
            {
                foreach (long childId in children)
                {
                    if (scene.Models.TryGetValue(childId, out var childModel) &&
                        !string.IsNullOrEmpty(childModel.Name))
                    {
                        return childModel.Name;
                    }
                }
            }

            // Fallback: cluster is connected as a child of the model.
            if (scene.ObjectParents.TryGetValue(cluster.Id, out long parentModelId) &&
                scene.Models.TryGetValue(parentModelId, out var parentModel) &&
                !string.IsNullOrEmpty(parentModel.Name))
            {
                return parentModel.Name;
            }

            return string.IsNullOrEmpty(cluster.Name) ? $"Cluster_{cluster.Id}" : cluster.Name;
        }

        private static int GetOrAddBoneIndex(
            List<string> boneNames,
            Dictionary<string, int> boneNameToIndex,
            string boneName)
        {
            boneName = string.IsNullOrEmpty(boneName) ? "<unnamed>" : boneName;
            if (boneNameToIndex.TryGetValue(boneName, out int index))
            {
                return index;
            }

            index = boneNames.Count;
            boneNames.Add(boneName);
            boneNameToIndex.Add(boneName, index);
            return index;
        }

        private static Dictionary<string, MeshLookupRecord> BuildMeshLookup(BinaryFbxScene scene)
        {
            var lookup = new Dictionary<string, MeshLookupRecord>(StringComparer.Ordinal);
            foreach (var geometry in scene.Geometries.Values)
            {
                if (!string.Equals(geometry.Type, "Mesh", StringComparison.Ordinal))
                {
                    continue;
                }

                scene.ObjectParents.TryGetValue(geometry.Id, out long modelId);
                scene.Models.TryGetValue(modelId, out var model);

                string nodeName = model?.Name;
                string nodePath = model != null ? BuildModelPath(scene, model.Id) : null;
                var record = new MeshLookupRecord
                {
                    Geometry = geometry,
                    MeshName = geometry.Name,
                    NodePath = nodePath
                };

                AddLookupKey(lookup, geometry.Name, record);
                AddLookupKey(lookup, nodeName, record);
                if (model != null)
                {
                    AddPathLookupKeys(lookup, record.NodePath, record);
                }
            }

            return lookup;
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

        private static void AddLookupKey(
            Dictionary<string, MeshLookupRecord> lookup,
            string key,
            MeshLookupRecord record)
        {
            key = NormalizePath(key);
            if (!string.IsNullOrEmpty(key) && !lookup.ContainsKey(key))
            {
                lookup.Add(key, record);
            }
        }

        private static void AddPathLookupKeys(
            Dictionary<string, MeshLookupRecord> lookup,
            string path,
            MeshLookupRecord record)
        {
            path = NormalizePath(path);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            AddLookupKey(lookup, path, record);
            string[] parts = path.Split('/');
            for (int i = 1; i < parts.Length - 1; i++)
            {
                AddLookupKey(lookup, string.Join("/", parts, i, parts.Length - i), record);
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
            if (!readValues)
            {
                SkipBytes(reader, encodedByteCount);
                return null;
            }

            // Uncompressed: read directly from the main stream — no intermediate byte[] or MemoryStream.
            if (encoding == 0)
            {
                return ReadTypedArray(reader, typeCode, length);
            }

            // Compressed (deflate): decompress first, then read from the decompressed buffer.
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

        private static bool IncludesAny(FbxMeshReadOptions readOptions, FbxMeshReadOptions options)
        {
            return (readOptions & options) != 0;
        }

        private static long GetLong(object[] properties, int index)
        {
            if (properties == null || index < 0 || index >= properties.Length)
            {
                return 0;
            }

            return properties[index] switch
            {
                long l   => l,
                int i    => i,
                short s  => s,
                _        => 0
            };
        }

        private static string GetString(object[] properties, int index)
        {
            return properties != null && index >= 0 && index < properties.Length
                ? properties[index] as string
                : null;
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

        private static string CleanObjectName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            int classSeparator = value.IndexOf("::", StringComparison.Ordinal);
            if (classSeparator >= 0)
            {
                value = value.Substring(classSeparator + 2);
            }

            int nullSeparator = value.IndexOf('\0');
            if (nullSeparator >= 0)
            {
                value = value.Substring(0, nullSeparator);
            }

            return value;
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return string.Join(
                "/",
                value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(CleanObjectName));
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

        private static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
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
            // Keyed by parentId → list of childIds; built once in ReadConnections to replace O(n) linear scans.
            public readonly Dictionary<long, List<long>> ChildrenByParent = new();
        }

        private sealed class GeometryRecord
        {
            public long Id;
            public string Name;
            public string Type;
            public int[] Indices;
            public Vector3d[] ControlPointPositions;
        }

        private sealed class ModelRecord
        {
            public long Id;
            public string Name;
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

            public ClusterDeformerRecord(long id, string name, int[] indices, double[] weights)
                : base(id, name, "Cluster")
            {
                Indices = indices;
                Weights = weights;
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

        private sealed class UnknownDeformerRecord : DeformerRecord
        {
            public UnknownDeformerRecord(long id, string name, string type)
                : base(id, name, type)
            {
            }
        }

        private sealed class MeshLookupRecord
        {
            public GeometryRecord Geometry;
            public string MeshName;
            public string NodePath;
        }
    }
}
