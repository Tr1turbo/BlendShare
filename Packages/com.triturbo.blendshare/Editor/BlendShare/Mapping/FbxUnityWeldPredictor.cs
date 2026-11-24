using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Unity ModelImporter settings that control the pre-split <c>WeldVertices</c> stage.
    /// </summary>
    public readonly struct FbxUnityWeldOptions
    {
        /// <summary>Default Unity ModelImporter weld settings (both welding and blendshape import enabled).</summary>
        public static readonly FbxUnityWeldOptions Default = new(weldVertices: true, importBlendShapes: true);

        /// <summary>Corresponds to <c>ModelImporter.weldVertices</c>.</summary>
        public bool WeldVertices { get; }

        /// <summary>Corresponds to <c>ModelImporter.importBlendShapes</c>.</summary>
        public bool ImportBlendShapes { get; }

        /// <summary>Creates a new weld option set.</summary>
        public FbxUnityWeldOptions(bool weldVertices, bool importBlendShapes)
        {
            WeldVertices = weldVertices;
            ImportBlendShapes = importBlendShapes;
        }
    }

    /// <summary>
    /// Control-point weld grouping predicted for Unity's <c>WeldVertices</c> importer stage.
    /// </summary>
    public sealed class FbxUnityWeldResult
    {
        /// <summary>Empty weld result for null or zero-control-point meshes.</summary>
        public static readonly FbxUnityWeldResult Empty = new(
            0,
            Array.Empty<int>(),
            Array.Empty<int[]>(),
            Array.Empty<int[]>());

        /// <summary>Number of source FBX control points covered by this result.</summary>
        public int ControlPointCount { get; }

        /// <summary>Number of unique welded control-point groups.</summary>
        public int WeldGroupCount => Groups.Count;

        /// <summary>Maps each FBX control point index to its predicted weld group index (<c>groups[cp]</c>).</summary>
        public IReadOnlyList<int> ControlPointToGroup { get; }

        /// <summary>All weld groups in creation order, each holding its member FBX control-point indices (<c>src[group]</c>).</summary>
        public IReadOnlyList<int[]> Groups { get; }

        /// <summary>Subset of <see cref="Groups"/> containing two or more welded control points.</summary>
        public IReadOnlyList<int[]> MultiMemberGroups { get; }

        /// <summary>Whether at least two control points weld into a shared group.</summary>
        public bool HasMultiMemberGroups => MultiMemberGroups.Count > 0;

        internal FbxUnityWeldResult(
            int controlPointCount,
            int[] controlPointToGroup,
            int[][] groups,
            int[][] multiMemberGroups)
        {
            ControlPointCount = controlPointCount;
            ControlPointToGroup = controlPointToGroup;
            Groups = groups;
            MultiMemberGroups = multiMemberGroups;
        }
    }

    /// <summary>
    /// Predicts Unity 2022.3's pre-split <c>WeldVertices</c> control-point grouping directly from FBX geometry.
    /// </summary>
    public static class FbxUnityWeldPredictor
    {
        private const double BlendShapeOffsetThreshold = 1e-6d;
        private const float SkinWeightTolerance = 1e-6f;

        /// <summary>
        /// Predicts which FBX control points weld together during Unity's <c>WeldVertices</c> stage.
        /// Matches Unity's float32 base position, normalized float32 skin weights, and effective
        /// float32 blendshape position delta comparison rules.
        /// </summary>
        public static FbxUnityWeldResult Predict(IFbxMeshGeometry mesh, FbxUnityWeldOptions options)
        {
            if (mesh == null || mesh.ControlPointCount <= 0)
            {
                return FbxUnityWeldResult.Empty;
            }

            int controlPointCount = mesh.ControlPointCount;
            if (!options.WeldVertices)
            {
                var identityMap = new int[controlPointCount];
                var singletonGroups = new int[controlPointCount][];
                for (int i = 0; i < controlPointCount; i++)
                {
                    identityMap[i] = i;
                    singletonGroups[i] = new[] { i };
                }

                return new FbxUnityWeldResult(
                    controlPointCount,
                    identityMap,
                    singletonGroups,
                    Array.Empty<int[]>());
            }

            var base64 = mesh.GetControlPoints();
            int count = base64.Length;
            if (count == 0)
            {
                return FbxUnityWeldResult.Empty;
            }

            // Unity compares raw FBX control points narrowed to float32 before scale and X-axis flip.
            // Points with NaN coordinates never equal any point (including themselves) and stay singletons.
            var base32 = new Float3[count];
            var base32Keys = new Base32Key[count];
            var isValidBase32 = new bool[count];
            var positionCounts = new Dictionary<Base32Key, int>(count);
            for (int i = 0; i < count; i++)
            {
                float x = (float)base64[i].x;
                float y = (float)base64[i].y;
                float z = (float)base64[i].z;
                base32[i] = new Float3(x, y, z);
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
                {
                    continue;
                }

                isValidBase32[i] = true;
                var key = new Base32Key(x, y, z);
                base32Keys[i] = key;
                positionCounts.TryGetValue(key, out int existing);
                positionCounts[key] = existing + 1;
            }

            // Only control points sharing a valid float32 base position can possibly weld.
            var hasCoincidentPeer = new bool[count];
            bool anyCoincident = false;
            for (int i = 0; i < count; i++)
            {
                if (isValidBase32[i] && positionCounts[base32Keys[i]] > 1)
                {
                    hasCoincidentPeer[i] = true;
                    anyCoincident = true;
                }
            }

            if (!anyCoincident)
            {
                var identityMap = new int[count];
                var singletonGroups = new int[count][];
                for (int i = 0; i < count; i++)
                {
                    identityMap[i] = i;
                    singletonGroups[i] = new[] { i };
                }

                return new FbxUnityWeldResult(
                    count,
                    identityMap,
                    singletonGroups,
                    Array.Empty<int[]>());
            }

            // Collect effective float32 blendshape position deltas only for coincident control points.
            List<ShapeDeltaEntry>[] shapeSignatures = null;
            if (options.ImportBlendShapes && mesh.BlendDeformers.Count > 0)
            {
                shapeSignatures = BuildBlendShapeSignatures(mesh, base64, base32, hasCoincidentPeer);
            }

            // Collect raw non-zero skin cluster weights per control point across all skin deformers.
            Dictionary<string, double>[] rawSkinWeights = null;
            if (mesh.SkinDeformers.Count > 0)
            {
                rawSkinWeights = CollectSkinWeights(mesh, count, hasCoincidentPeer);
            }

            var controlPointToGroup = new int[count];
            var groupMembers = new List<List<int>>(count);
            var buckets = new Dictionary<WeldBucketKey, List<WeldCandidate>>(count);

            for (int i = 0; i < count; i++)
            {
                if (!hasCoincidentPeer[i])
                {
                    int groupId = groupMembers.Count;
                    controlPointToGroup[i] = groupId;
                    groupMembers.Add(new List<int>(1) { i });
                    continue;
                }

                BuildNormalizedSkinWeights(
                    rawSkinWeights != null ? rawSkinWeights[i] : null,
                    out string[] sortedBones,
                    out float[] normalizedWeights);

                var shapeSig = shapeSignatures != null ? shapeSignatures[i] : null;
                var bucketKey = new WeldBucketKey(base32Keys[i], sortedBones, shapeSig);

                if (!buckets.TryGetValue(bucketKey, out var candidates))
                {
                    candidates = new List<WeldCandidate>(2);
                    buckets.Add(bucketKey, candidates);
                }

                int matchedGroup = -1;
                // Unity scans matching candidates in reverse insertion order with a 1e-6 weight tolerance.
                for (int c = candidates.Count - 1; c >= 0; c--)
                {
                    if (WeightsMatch(candidates[c].NormalizedWeights, normalizedWeights))
                    {
                        matchedGroup = candidates[c].GroupId;
                        break;
                    }
                }

                if (matchedGroup < 0)
                {
                    matchedGroup = groupMembers.Count;
                    candidates.Add(new WeldCandidate(matchedGroup, normalizedWeights));
                    groupMembers.Add(new List<int>(2));
                }

                controlPointToGroup[i] = matchedGroup;
                groupMembers[matchedGroup].Add(i);
            }

            var allGroups = new int[groupMembers.Count][];
            var multiGroups = new List<int[]>();
            for (int g = 0; g < groupMembers.Count; g++)
            {
                var members = groupMembers[g].ToArray();
                allGroups[g] = members;
                if (members.Length > 1)
                {
                    multiGroups.Add(members);
                }
            }

            return new FbxUnityWeldResult(
                count,
                controlPointToGroup,
                allGroups,
                multiGroups.ToArray());
        }

        private static List<ShapeDeltaEntry>[] BuildBlendShapeSignatures(
            IFbxMeshGeometry mesh,
            Vector3d[] base64,
            Float3[] base32,
            bool[] hasCoincidentPeer)
        {
            int controlPointCount = base64.Length;
            var signatures = new List<ShapeDeltaEntry>[controlPointCount];
            int[] indexBuffer = Array.Empty<int>();
            Vector3d[] deltaBuffer = Array.Empty<Vector3d>();
            int frameIndex = 0;

            foreach (var deformer in mesh.BlendDeformers)
            {
                if (deformer == null) continue;
                foreach (var channel in deformer.Channels)
                {
                    if (channel == null) continue;
                    foreach (var keyframe in channel.Keyframes)
                    {
                        int currentFrame = frameIndex++;
                        var shape = keyframe?.Shape;
                        if (shape == null || shape.OffsetCount <= 0)
                        {
                            continue;
                        }

                        int offsetCount = shape.OffsetCount;
                        if (indexBuffer.Length < offsetCount)
                        {
                            indexBuffer = new int[offsetCount];
                            deltaBuffer = new Vector3d[offsetCount];
                        }

                        var idxSpan = indexBuffer.AsSpan(0, offsetCount);
                        var posSpan = deltaBuffer.AsSpan(0, offsetCount);
                        if (!shape.TryCopyOffsets(idxSpan, posSpan, Span<Vector3d>.Empty, Span<double>.Empty, out int written) ||
                            written <= 0)
                        {
                            continue;
                        }

                        for (int i = 0; i < written; i++)
                        {
                            int cp = indexBuffer[i];
                            if ((uint)cp >= (uint)controlPointCount || !hasCoincidentPeer[cp])
                            {
                                continue;
                            }

                            var offset = deltaBuffer[i];
                            double ox = Math.Abs(offset.x) > BlendShapeOffsetThreshold ? offset.x : 0d;
                            double oy = Math.Abs(offset.y) > BlendShapeOffsetThreshold ? offset.y : 0d;
                            double oz = Math.Abs(offset.z) > BlendShapeOffsetThreshold ? offset.z : 0d;

                            // Match Unity's exact quantization: double base + thresholded double delta -> float32 - base32.
                            float dx = (float)(base64[cp].x + ox) - base32[cp].X;
                            float dy = (float)(base64[cp].y + oy) - base32[cp].Y;
                            float dz = (float)(base64[cp].z + oz) - base32[cp].Z;
                            if (dx == 0f && dy == 0f && dz == 0f)
                            {
                                continue;
                            }

                            var list = signatures[cp];
                            if (list == null)
                            {
                                list = new List<ShapeDeltaEntry>(4);
                                signatures[cp] = list;
                            }

                            list.Add(new ShapeDeltaEntry(
                                currentFrame,
                                BitConverter.SingleToInt32Bits(dx == 0f ? 0f : dx),
                                BitConverter.SingleToInt32Bits(dy == 0f ? 0f : dy),
                                BitConverter.SingleToInt32Bits(dz == 0f ? 0f : dz)));
                        }
                    }
                }
            }

            return signatures;
        }

        private static Dictionary<string, double>[] CollectSkinWeights(
            IFbxMeshGeometry mesh,
            int controlPointCount,
            bool[] hasCoincidentPeer)
        {
            var weightsByPoint = new Dictionary<string, double>[controlPointCount];
            int[] indexBuffer = Array.Empty<int>();
            double[] weightBuffer = Array.Empty<double>();

            for (int skinIndex = 0; skinIndex < mesh.SkinDeformers.Count; skinIndex++)
            {
                var skin = mesh.SkinDeformers[skinIndex];
                if (skin == null) continue;
                for (int clusterIndex = 0; clusterIndex < skin.Clusters.Count; clusterIndex++)
                {
                    var cluster = skin.Clusters[clusterIndex];
                    if (cluster == null || cluster.WeightCount <= 0)
                    {
                        continue;
                    }

                    int weightCount = cluster.WeightCount;
                    if (indexBuffer.Length < weightCount)
                    {
                        indexBuffer = new int[weightCount];
                        weightBuffer = new double[weightCount];
                    }

                    if (!cluster.TryCopyControlPointIndices(indexBuffer.AsSpan(0, weightCount), out int idxWritten) ||
                        !cluster.TryCopyWeights(weightBuffer.AsSpan(0, weightCount), out int wWritten))
                    {
                        continue;
                    }

                    // Key by bone node identity when available so distinct bones sharing a path never collapse,
                    // falling back to cluster name or skin/cluster slot. Match prototype last-nonzero semantics.
                    string clusterKey = cluster.BoneNode != null
                        ? $"node:{cluster.BoneNode.Id}"
                        : !string.IsNullOrEmpty(cluster.Name)
                            ? $"name:{cluster.Name}"
                            : $"slot:{skinIndex}:{clusterIndex}";
                    int pairCount = Math.Min(idxWritten, wWritten);
                    for (int i = 0; i < pairCount; i++)
                    {
                        int cp = indexBuffer[i];
                        double weight = weightBuffer[i];
                        if ((uint)cp >= (uint)controlPointCount || !hasCoincidentPeer[cp] || weight == 0d)
                        {
                            continue;
                        }

                        var map = weightsByPoint[cp];
                        if (map == null)
                        {
                            map = new Dictionary<string, double>(4, StringComparer.Ordinal);
                            weightsByPoint[cp] = map;
                        }

                        map[clusterKey] = weight;
                    }
                }
            }

            return weightsByPoint;
        }

        private static void BuildNormalizedSkinWeights(
            Dictionary<string, double> rawWeights,
            out string[] sortedBones,
            out float[] normalizedWeights)
        {
            if (rawWeights == null || rawWeights.Count == 0)
            {
                sortedBones = Array.Empty<string>();
                normalizedWeights = Array.Empty<float>();
                return;
            }

            int count = rawWeights.Count;
            sortedBones = new string[count];
            double sum = 0d;
            int idx = 0;
            foreach (var pair in rawWeights)
            {
                sortedBones[idx++] = pair.Key;
                sum += pair.Value;
            }

            Array.Sort(sortedBones, StringComparer.Ordinal);
            normalizedWeights = new float[count];
            bool validSum = sum != 0d && !double.IsNaN(sum) && !double.IsInfinity(sum);
            for (int i = 0; i < count; i++)
            {
                // Malformed zero-sum or non-finite weight sets receive NaN so WeightsMatch rejects them.
                normalizedWeights[i] = validSum ? (float)(rawWeights[sortedBones[i]] / sum) : float.NaN;
            }
        }

        private static bool WeightsMatch(float[] first, float[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                // Match Unity's float32 subtraction and reject NaN via negated <= comparison.
                float absDiff = Math.Abs(first[i] - second[i]);
                if (!(absDiff <= SkinWeightTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private readonly struct Float3
        {
            public readonly float X;
            public readonly float Y;
            public readonly float Z;

            public Float3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        private readonly struct Base32Key : IEquatable<Base32Key>
        {
            // Normalize -0.0f to +0.0f so dictionary hashing matches IEEE 754 / Python float equality.
            public readonly float X;
            public readonly float Y;
            public readonly float Z;

            public Base32Key(float x, float y, float z)
            {
                X = x == 0f ? 0f : x;
                Y = y == 0f ? 0f : y;
                Z = z == 0f ? 0f : z;
            }

            public bool Equals(Base32Key other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is Base32Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + BitConverter.SingleToInt32Bits(X);
                    hash = hash * 31 + BitConverter.SingleToInt32Bits(Y);
                    hash = hash * 31 + BitConverter.SingleToInt32Bits(Z);
                    return hash;
                }
            }
        }

        private readonly struct ShapeDeltaEntry : IEquatable<ShapeDeltaEntry>
        {
            public readonly int FrameIndex;
            public readonly int DxBits;
            public readonly int DyBits;
            public readonly int DzBits;

            public ShapeDeltaEntry(int frameIndex, int dxBits, int dyBits, int dzBits)
            {
                FrameIndex = frameIndex;
                DxBits = dxBits;
                DyBits = dyBits;
                DzBits = dzBits;
            }

            public bool Equals(ShapeDeltaEntry other) =>
                FrameIndex == other.FrameIndex &&
                DxBits == other.DxBits &&
                DyBits == other.DyBits &&
                DzBits == other.DzBits;

            public override bool Equals(object obj) => obj is ShapeDeltaEntry other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = FrameIndex;
                    hash = hash * 31 + DxBits;
                    hash = hash * 31 + DyBits;
                    hash = hash * 31 + DzBits;
                    return hash;
                }
            }
        }

        private readonly struct WeldCandidate
        {
            public readonly int GroupId;
            public readonly float[] NormalizedWeights;

            public WeldCandidate(int groupId, float[] normalizedWeights)
            {
                GroupId = groupId;
                NormalizedWeights = normalizedWeights;
            }
        }

        private readonly struct WeldBucketKey : IEquatable<WeldBucketKey>
        {
            private readonly Base32Key base32;
            private readonly string[] sortedBones;
            private readonly List<ShapeDeltaEntry> shapeSignature;
            private readonly int cachedHash;

            public WeldBucketKey(Base32Key base32, string[] sortedBones, List<ShapeDeltaEntry> shapeSignature)
            {
                this.base32 = base32;
                this.sortedBones = sortedBones ?? Array.Empty<string>();
                this.shapeSignature = shapeSignature;

                unchecked
                {
                    int hash = base32.GetHashCode();
                    for (int i = 0; i < this.sortedBones.Length; i++)
                    {
                        hash = hash * 31 + StringComparer.Ordinal.GetHashCode(this.sortedBones[i]);
                    }

                    if (shapeSignature != null)
                    {
                        for (int i = 0; i < shapeSignature.Count; i++)
                        {
                            hash = hash * 31 + shapeSignature[i].GetHashCode();
                        }
                    }

                    cachedHash = hash;
                }
            }

            public bool Equals(WeldBucketKey other)
            {
                if (!base32.Equals(other.base32)) return false;
                if (sortedBones.Length != other.sortedBones.Length) return false;

                int sigCount = shapeSignature?.Count ?? 0;
                int otherSigCount = other.shapeSignature?.Count ?? 0;
                if (sigCount != otherSigCount) return false;

                for (int i = 0; i < sortedBones.Length; i++)
                {
                    if (!string.Equals(sortedBones[i], other.sortedBones[i], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                for (int i = 0; i < sigCount; i++)
                {
                    if (!shapeSignature[i].Equals(other.shapeSignature[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj) => obj is WeldBucketKey other && Equals(other);
            public override int GetHashCode() => cachedHash;
        }
    }
}
