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

        private bool TryFindClosestRelativeCandidateIndex(
            IReadOnlyList<PositionFingerprint> candidates,
            out int candidateIndex)
        {
            candidateIndex = -1;
            if (candidates == null)
            {
                return false;
            }

            double bestDistance = double.PositiveInfinity;
            for (int currentCandidateIndex = 0; currentCandidateIndex < candidates.Count; currentCandidateIndex++)
            {
                double distance = RelativeDistanceTo(candidates[currentCandidateIndex], bestDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    candidateIndex = currentCandidateIndex;
                }
            }

            return candidateIndex >= 0;
        }

        private bool TryFindClosestRelativeCandidateIndex(
            IReadOnlyList<PositionFingerprint> candidates,
            IReadOnlyList<int> candidateIndices,
            out int candidateIndex)
        {
            candidateIndex = -1;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < candidateIndices.Count; i++)
            {
                int currentCandidateIndex = candidateIndices[i];
                if (currentCandidateIndex < 0 || currentCandidateIndex >= candidates.Count)
                {
                    continue;
                }

                double distance = RelativeDistanceTo(candidates[currentCandidateIndex], bestDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    candidateIndex = currentCandidateIndex;
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
            private readonly List<int> _basePositionMatches = new List<int>();
            private readonly float _epsilon;
            private readonly float _cellSize;

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
                    return false;
                }

                _basePositionMatches.Clear();
                AddBasePositionMatches(fingerprint, _basePositionMatches);

                if (_basePositionMatches.Count == 0)
                {
                    return fingerprint.TryFindClosestRelativeCandidateIndex(_candidates, out candidateIndex);
                }

                if (_basePositionMatches.Count == 1)
                {
                    candidateIndex = _basePositionMatches[0];
                    return true;
                }

                return fingerprint.TryFindClosestRelativeCandidateIndex(
                    _candidates,
                    _basePositionMatches,
                    out candidateIndex);
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
