using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    internal sealed class PositionFingerprint
    {
        public readonly Vector3 BasePosition;
        private readonly Vector3[] _relativeSamples;

        public PositionFingerprint(Vector3 basePosition, Vector3[] relativeSamples)
        {
            BasePosition = basePosition;
            _relativeSamples = relativeSamples ?? System.Array.Empty<Vector3>();
        }

        public bool SetRelativeSample(int index, Vector3 value)
        {
            if (index < 0 || index >= _relativeSamples.Length)
            {
                return false;
            }

            _relativeSamples[index] = value;
            return true;
        }

        public bool IsBasePositionMatch(PositionFingerprint other, float epsilon)
        {
            return other != null && IsSame(BasePosition, other.BasePosition, epsilon);
        }

        public bool TryFindMatchingCandidateIndex(
            IReadOnlyList<PositionFingerprint> candidates,
            float epsilon,
            out int candidateIndex)
        {
            candidateIndex = -1;
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            var basePositionMatches = new List<int>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsBasePositionMatch(candidates[i], epsilon))
                {
                    basePositionMatches.Add(i);
                }
            }

            if (basePositionMatches.Count == 0)
            {
                return TryFindClosestRelativeCandidateIndex(candidates, out candidateIndex);
            }

            if (basePositionMatches.Count == 1)
            {
                candidateIndex = basePositionMatches[0];
                return true;
            }

            return TryFindClosestRelativeCandidateIndex(candidates, basePositionMatches, out candidateIndex);
        }

        public bool TryFindMatchingCandidateIndex(
            CandidateIndex candidates,
            out int candidateIndex)
        {
            candidateIndex = -1;
            return candidates != null && candidates.TryFindMatchingCandidateIndex(this, out candidateIndex);
        }

        public double RelativeDistanceTo(PositionFingerprint other, double stopAfter = double.PositiveInfinity)
        {
            if (other == null || _relativeSamples.Length != other._relativeSamples.Length)
            {
                return double.PositiveInfinity;
            }

            double total = 0.0;
            for (int i = 0; i < _relativeSamples.Length; i++)
            {
                double deltaX = (double)_relativeSamples[i].x - other._relativeSamples[i].x;
                double deltaY = (double)_relativeSamples[i].y - other._relativeSamples[i].y;
                double deltaZ = (double)_relativeSamples[i].z - other._relativeSamples[i].z;
                total += System.Math.Sqrt(
                    deltaX * deltaX +
                    deltaY * deltaY +
                    deltaZ * deltaZ);
                if (total >= stopAfter)
                {
                    return total;
                }
            }

            return total;
        }

        // Squared-distance variant of RelativeDistanceTo.
        // Avoids sqrt — valid for finding the minimum since sqrt is monotone.
        // NOTE: sum-of-sqr-distances != (sum-of-distances)², so this can rank candidates
        // differently from RelativeDistanceTo in ambiguous cases. For genuine matches the
        // true candidate has near-zero contribution on every sample, so either metric wins.
        private double RelativeSqrDistanceTo(PositionFingerprint other, double stopAfter = double.PositiveInfinity)
        {
            if (other == null || _relativeSamples.Length != other._relativeSamples.Length)
            {
                return double.PositiveInfinity;
            }

            double total = 0.0;
            for (int i = 0; i < _relativeSamples.Length; i++)
            {
                double dx = (double)_relativeSamples[i].x - other._relativeSamples[i].x;
                double dy = (double)_relativeSamples[i].y - other._relativeSamples[i].y;
                double dz = (double)_relativeSamples[i].z - other._relativeSamples[i].z;
                total += dx * dx + dy * dy + dz * dz;
                if (total >= stopAfter)
                {
                    return total;
                }
            }

            return total;
        }



        // All-candidates search: uses squared distances + stopAfter to abort early within
        // each candidate once its running total already exceeds the known best.
        private bool TryFindClosestRelativeCandidateIndex(
            IReadOnlyList<PositionFingerprint> candidates,
            out int candidateIndex)
        {
            candidateIndex = -1;
            if (candidates == null)
            {
                return false;
            }

            double bestSqrDistance = double.PositiveInfinity;
            for (int currentCandidateIndex = 0; currentCandidateIndex < candidates.Count; currentCandidateIndex++)
            {
                double distance = RelativeSqrDistanceTo(candidates[currentCandidateIndex], bestSqrDistance);
                if (distance < bestSqrDistance)
                {
                    bestSqrDistance = distance;
                    candidateIndex = currentCandidateIndex;
                }
            }

            return candidateIndex >= 0;
        }

        // Indexed search for the case where multiple representative FBX control points share
        // the same base position. This must compare every valid candidate across all samples:
        // a candidate that is slightly worse on an early blendshape can still be the closest
        // after later blendshapes are included.
        private bool TryFindClosestRelativeCandidateIndex(
            IReadOnlyList<PositionFingerprint> candidates,
            IReadOnlyList<int> candidateIndices,
            out int candidateIndex)
        {
            candidateIndex = -1;
            if (candidates == null || candidateIndices == null || candidateIndices.Count == 0)
            {
                return false;
            }

            double bestSqrDistance = double.PositiveInfinity;
            for (int i = 0; i < candidateIndices.Count; i++)
            {
                int currentCandidateIndex = candidateIndices[i];
                if (currentCandidateIndex < 0 || currentCandidateIndex >= candidates.Count)
                {
                    continue;
                }

                double distance = RelativeSqrDistanceTo(candidates[currentCandidateIndex], bestSqrDistance);
                if (distance < bestSqrDistance)
                {
                    bestSqrDistance = distance;
                    candidateIndex = currentCandidateIndex;
                }
            }

            return candidateIndex >= 0;
        }


        // Test-only tolerance for the old incremental candidate filter.

        // Tolerance for incremental candidate filtering (in squared-distance units).
        // After each blendshape sample, candidates whose accumulated squared distance exceeds
        // the current minimum by more than (min * FilterRelTol + FilterAbsEps) are eliminated.
        // RelTol is metric-agnostic (relative comparison). FilterAbsEps = 1e-10 is the squared
        // equivalent of ~1e-5 total Euclidean distance across samples (sub-millimeter floor).
        private const double IncrementalFilterRelTol = 0.05;
        private const double IncrementalFilterAbsEps = 1e-10;

        // incremental sample-by-sample filter kept for test comparisons only.
        // Production uses the exact indexed scan above.

        // Incremental sample-by-sample filtering for the indexed case (multiple candidates
        // at the same base position). Processes one blendshape at a time and eliminates
        // candidates that are clearly worse after each sample, reducing O(K×M) work.
        // Indexed search for the case where multiple representative FBX control points share
        // the same base position. This must compare every valid candidate across all samples:
        // a candidate that is slightly worse on an early blendshape can still be the closest
        // after later blendshapes are included.
        private bool TryFindClosestRelativeCandidateIndexIncremental(
            IReadOnlyList<PositionFingerprint> candidates,
            IReadOnlyList<int> candidateIndices,
            out int candidateIndex)
        {
            candidateIndex = -1;
            int count = candidateIndices?.Count ?? 0;
            if (count == 0) return false;

            int sampleCount = _relativeSamples.Length;
            if (sampleCount == 0)
            {
                candidateIndex = candidateIndices[0];
                return candidateIndex >= 0 && candidates != null && candidateIndex < candidates.Count;
            }

            var accum = new double[count];
            var active = new bool[count];
            int activeCount = 0;

            for (int i = 0; i < count; i++)
            {
                int ci = candidateIndices[i];
                if (candidates != null && ci >= 0 && ci < candidates.Count && candidates[ci] != null &&
                    candidates[ci]._relativeSamples.Length == sampleCount)
                {
                    active[i] = true;
                    activeCount++;
                }
            }

            if (activeCount == 0) return false;

            int samplesUsed = 0;
            for (int si = 0; si < sampleCount && activeCount > 1; si++)
            {
                samplesUsed++;
                double minAccum = double.PositiveInfinity;

                for (int i = 0; i < count; i++)
                {
                    if (!active[i]) continue;
                    var otherSamples = candidates[candidateIndices[i]]._relativeSamples;

                    double dx = (double)_relativeSamples[si].x - otherSamples[si].x;
                    double dy = (double)_relativeSamples[si].y - otherSamples[si].y;
                    double dz = (double)_relativeSamples[si].z - otherSamples[si].z;
                    accum[i] += dx * dx + dy * dy + dz * dz;

                    if (accum[i] < minAccum) minAccum = accum[i];
                }

                double threshold = minAccum * (1.0 + IncrementalFilterRelTol) + IncrementalFilterAbsEps;
                for (int i = 0; i < count; i++)
                {
                    if (active[i] && accum[i] > threshold)
                    {
                        active[i] = false;
                        activeCount--;
                    }
                }
            }

            if (activeCount > 1)
            {
                Debug.Log(
                    $"[PositionFingerprint] Incremental test ambiguous: {count} candidates, " +
                    $"{sampleCount} blendshapes processed, still {activeCount} remaining.");
            }
            else
            {
                Debug.Log(
                    $"[PositionFingerprint] Incremental test resolved: {count} candidates -> 1 after " +
                    $"{samplesUsed}/{sampleCount} blendshapes");
            }

            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                if (!active[i]) continue;
                if (accum[i] < bestDist)
                {
                    bestDist = accum[i];
                    candidateIndex = candidateIndices[i];
                }
            }

            return candidateIndex >= 0;
        }

        private static bool IsSame(Vector3 a, Vector3 b, float epsilon)
        {
            return Mathf.Abs(a.x - b.x) <= epsilon &&
                   Mathf.Abs(a.y - b.y) <= epsilon &&
                   Mathf.Abs(a.z - b.z) <= epsilon;
        }

        // Shared spatial key — used by both WeldingGroupIndex and CandidateIndex.
        private readonly struct BasePositionKey : System.IEquatable<BasePositionKey>
        {
            public readonly long X;
            public readonly long Y;
            public readonly long Z;

            public BasePositionKey(long x, long y, long z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public static BasePositionKey From(Vector3 position, float cellSize)
            {
                return new BasePositionKey(
                    (long)System.Math.Floor(position.x / cellSize),
                    (long)System.Math.Floor(position.y / cellSize),
                    (long)System.Math.Floor(position.z / cellSize));
            }

            public bool Equals(BasePositionKey other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is BasePositionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + X.GetHashCode();
                    hash = hash * 31 + Y.GetHashCode();
                    hash = hash * 31 + Z.GetHashCode();
                    return hash;
                }
            }
        }

        // Groups FBX control points whose fingerprints are identical (same base position + same relative
        // samples). The matching step then builds a CandidateIndex over the K group representatives
        // (K ≤ M) instead of all M control points, which is faster when the mesh has many welded seams.
        internal sealed class WeldingGroupIndex
        {
            private readonly PositionFingerprint[] _representatives;
            private readonly int[][] _groupMembers;

            public int GroupCount => _representatives.Length;
            public PositionFingerprint[] GetAllRepresentatives() => _representatives;
            public int[] GetGroupMembers(int groupId) => _groupMembers[groupId];

            public WeldingGroupIndex(PositionFingerprint[] fingerprints, float epsilon)
            {
                int count = fingerprints?.Length ?? 0;
                float cellSize = epsilon > 0f ? epsilon : 1f;

                // Spatial bucket map for O(1) neighbourhood lookup
                var buckets = new Dictionary<BasePositionKey, List<int>>(count);
                for (int i = 0; i < count; i++)
                {
                    if (fingerprints[i] == null) continue;
                    var key = BasePositionKey.From(fingerprints[i].BasePosition, cellSize);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        buckets[key] = list;
                    }
                    list.Add(i);
                }

                var controlPointToGroup = new int[count];
                for (int i = 0; i < count; i++) controlPointToGroup[i] = -1;

                var representatives = new List<PositionFingerprint>(count);
                var memberLists = new List<int[]>(count);

                for (int i = 0; i < count; i++)
                {
                    if (controlPointToGroup[i] >= 0 || fingerprints[i] == null) continue;

                    int groupId = representatives.Count;
                    controlPointToGroup[i] = groupId;
                    var members = new List<int> { i };

                    // Probe 3×3×3 neighbourhood to find identical-fingerprint control points
                    var baseKey = BasePositionKey.From(fingerprints[i].BasePosition, cellSize);
                    for (long x = baseKey.X - 1; x <= baseKey.X + 1; x++)
                    for (long y = baseKey.Y - 1; y <= baseKey.Y + 1; y++)
                    for (long z = baseKey.Z - 1; z <= baseKey.Z + 1; z++)
                    {
                        if (!buckets.TryGetValue(new BasePositionKey(x, y, z), out var bucket)) continue;
                        for (int bi = 0; bi < bucket.Count; bi++)
                        {
                            int j = bucket[bi];
                            if (j == i || controlPointToGroup[j] >= 0 || fingerprints[j] == null) continue;
                            if (!fingerprints[i].IsBasePositionMatch(fingerprints[j], epsilon)) continue;
                            // stopAfter=1e-12: aborts after the first differing sample, so this is O(1)
                            // for non-identical pairs and O(samples) only for truly identical ones.
                            if (fingerprints[i].RelativeDistanceTo(fingerprints[j], stopAfter: 1e-12) != 0.0) continue;
                            controlPointToGroup[j] = groupId;
                            members.Add(j);
                        }
                    }

                    representatives.Add(fingerprints[i]);
                    memberLists.Add(members.ToArray());
                }

                _representatives = representatives.ToArray();
                _groupMembers = memberLists.ToArray();
            }
        }

        internal sealed class CandidateIndex
        {
            private readonly IReadOnlyList<PositionFingerprint> _candidates;
            private readonly Dictionary<BasePositionKey, List<int>> _indicesByBasePosition;
            private readonly float _epsilon;
            private readonly float _cellSize;
            [System.ThreadStatic]
            private static List<int> s_basePositionMatches;

            // Accumulated stats across all TryFindMatchingCandidateIndex calls
            private long _basePositionTicks;
            private long _allCandidatesSearchTicks;
            private long _indexedSearchTicks;
            private int _resolvedByBasePosition;
            private int _resolvedByAllCandidatesSearch;
            private int _resolvedByIndexedSearch;
            private int _unresolved;

            public CandidateIndex(IReadOnlyList<PositionFingerprint> candidates, float epsilon)
            {
                _candidates = candidates ?? System.Array.Empty<PositionFingerprint>();
                _epsilon = epsilon;
                _cellSize = epsilon > 0f ? epsilon : 1f;
                _indicesByBasePosition = new Dictionary<BasePositionKey, List<int>>(_candidates.Count);

                for (int i = 0; i < _candidates.Count; i++)
                {
                    PositionFingerprint candidate = _candidates[i];
                    if (candidate == null)
                    {
                        continue;
                    }

                    BasePositionKey key = BasePositionKey.From(candidate.BasePosition, _cellSize);
                    if (!_indicesByBasePosition.TryGetValue(key, out var indices))
                    {
                        indices = new List<int>();
                        _indicesByBasePosition.Add(key, indices);
                    }

                    indices.Add(i);
                }
            }

            public bool TryFindMatchingCandidateIndex(
                PositionFingerprint fingerprint,
                out int candidateIndex)
            {
                candidateIndex = -1;
                if (fingerprint == null || _candidates.Count == 0)
                {
                    System.Threading.Interlocked.Increment(ref _unresolved);
                    return false;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                List<int> basePositionMatches = s_basePositionMatches ?? (s_basePositionMatches = new List<int>());
                basePositionMatches.Clear();
                AddBasePositionMatches(fingerprint, basePositionMatches);
                System.Threading.Interlocked.Add(
                    ref _basePositionTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - t0);

                if (basePositionMatches.Count == 0)
                {
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool found = fingerprint.TryFindClosestRelativeCandidateIndex(_candidates, out candidateIndex);
                    System.Threading.Interlocked.Add(
                        ref _allCandidatesSearchTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - t1);
                    if (found) System.Threading.Interlocked.Increment(ref _resolvedByAllCandidatesSearch);
                    else System.Threading.Interlocked.Increment(ref _unresolved);
                    return found;
                }

                if (basePositionMatches.Count == 1)
                {
                    candidateIndex = basePositionMatches[0];
                    System.Threading.Interlocked.Increment(ref _resolvedByBasePosition);
                    return true;
                }

                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool foundIndexed = fingerprint.TryFindClosestRelativeCandidateIndex(
                    _candidates,
                    basePositionMatches,
                    out candidateIndex);
                System.Threading.Interlocked.Add(
                    ref _indexedSearchTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - t2);
                if (foundIndexed) System.Threading.Interlocked.Increment(ref _resolvedByIndexedSearch);
                else System.Threading.Interlocked.Increment(ref _unresolved);
                return foundIndexed;
            }

            public void LogStats(string prefix = "[CandidateIndex]")
            {
                double freq = System.Diagnostics.Stopwatch.Frequency;
                double bpMs    = _basePositionTicks          / freq * 1000.0;
                double allMs   = _allCandidatesSearchTicks   / freq * 1000.0;
                double idxMs   = _indexedSearchTicks         / freq * 1000.0;
                int total = _resolvedByBasePosition + _resolvedByAllCandidatesSearch
                          + _resolvedByIndexedSearch + _unresolved;
                Debug.Log(
                    $"{prefix} {total} vertices total\n" +
                    $"  AddBasePositionMatches:               {bpMs,10:0.###} ms\n" +
                    $"  Resolved by base position (unique):   {_resolvedByBasePosition,8} vertices\n" +
                    $"  TryFindClosest all-candidates:        {_resolvedByAllCandidatesSearch,8} vertices   {allMs,10:0.###} ms\n" +
                    $"  TryFindClosest indexed (ambiguous):   {_resolvedByIndexedSearch,8} vertices   {idxMs,10:0.###} ms\n" +
                    $"  Unresolved:                           {_unresolved,8}");
            }

            private void AddBasePositionMatches(PositionFingerprint fingerprint, List<int> matches)
            {
                BasePositionKey baseKey = BasePositionKey.From(fingerprint.BasePosition, _cellSize);
                for (long x = baseKey.X - 1; x <= baseKey.X + 1; x++)
                {
                    for (long y = baseKey.Y - 1; y <= baseKey.Y + 1; y++)
                    {
                        for (long z = baseKey.Z - 1; z <= baseKey.Z + 1; z++)
                        {
                            if (!_indicesByBasePosition.TryGetValue(new BasePositionKey(x, y, z), out var indices))
                            {
                                continue;
                            }

                            for (int i = 0; i < indices.Count; i++)
                            {
                                int candidateIdx = indices[i];
                                if (fingerprint.IsBasePositionMatch(_candidates[candidateIdx], _epsilon))
                                {
                                    matches.Add(candidateIdx);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
