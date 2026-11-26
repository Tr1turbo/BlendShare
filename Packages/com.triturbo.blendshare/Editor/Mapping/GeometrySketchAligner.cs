using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Observed centroid-contrast vectors for a single 32-sample block of a target mesh.
    /// </summary>
    public readonly struct SketchBlockObservation
    {
        public int BlockIndex { get; }
        public bool IsValid { get; }
        public Vector3d Contrast1 { get; }
        public Vector3d Contrast2 { get; }
        public Vector3d Contrast3 { get; }

        public SketchBlockObservation(
            int blockIndex,
            bool isValid,
            Vector3d contrast1,
            Vector3d contrast2,
            Vector3d contrast3)
        {
            BlockIndex = blockIndex;
            IsValid = isValid;
            Contrast1 = contrast1;
            Contrast2 = contrast2;
            Contrast3 = contrast3;
        }

        public static SketchBlockObservation Invalid(int blockIndex) =>
            new SketchBlockObservation(blockIndex, false, Vector3d.zero, Vector3d.zero, Vector3d.zero);
    }

    /// <summary>
    /// Stored centroid-contrast vectors read from a <c>MeshDocument.Sketch</c> block.
    /// </summary>
    public readonly struct StoredSketchBlock
    {
        public int BlockIndex { get; }
        public uint ParticipationMask { get; }
        public Vector3d Contrast1 { get; }
        public Vector3d Contrast2 { get; }
        public Vector3d Contrast3 { get; }

        public StoredSketchBlock(
            int blockIndex,
            uint participationMask,
            Vector3d contrast1,
            Vector3d contrast2,
            Vector3d contrast3)
        {
            BlockIndex = blockIndex;
            ParticipationMask = participationMask;
            Contrast1 = contrast1;
            Contrast2 = contrast2;
            Contrast3 = contrast3;
        }
    }

    /// <summary>
    /// Calibrates mesh-local linear coordinate transforms at apply/export time by fitting an orthogonal similarity
    /// residual <c>s * Q</c> (<c>s &gt; 0</c>, <c>Q in O(3)</c>) on top of the known baseline linear transform <c>A</c>
    /// (<c>M = (s * Q) * A</c>) from deterministic sub-block centroid contrasts.
    /// Preserves non-uniform <c>GeometryToNode</c> scales (including out-of-plane scales on planar meshes) already encoded in <c>A</c>,
    /// rejects localized sculpts via block-majority consensus, and falls back to <c>A</c> on global non-rigid deformations.
    /// </summary>
    public static class GeometrySketchAligner
    {
        private const int k_SamplesPerBlock = 32;
        private const int k_MaxBlocks = 20;
        private const int k_MinGroupSamples = 4;
        private const double k_MinBlockNormSq = 1e-18d;
        private const double k_BlockInlierRelativeTolerance = 1.5e-2d;
        private const double k_BlockInlierFloorFactor = 1e-4d;
        private const double k_Rank2SingularRatioThreshold = 1e-5d;
        private const double k_Rank1SingularRatioThreshold = 1e-6d;
        private const double k_OrthogonalSnapFrobeniusTolerance = 1e-3d;
        private const double k_ScaleSnapRelativeTolerance = 5e-5d;

        private static readonly FbxMatrix4x4[] s_SignedPermutations48 = BuildSignedPermutations48();
        private static readonly double[] s_StandardScales = { 1d, 0.01d, 100d, 0.001d, 1000d, 0.1d, 10d };

        /// <summary>
        /// Extracts observed per-block centroid contrasts directly from target FBX raw control points.
        /// If any participating sample in a block is out-of-range or non-finite, the entire block is marked invalid.
        /// </summary>
        public static SketchBlockObservation[] ExtractTargetContrastsFromFbx(
            ReadOnlySpan<StoredSketchBlock> storedBlocks,
            int controlPointCount,
            ReadOnlySpan<Vector3d> targetControlPoints)
        {
            if (storedBlocks.IsEmpty || controlPointCount <= 0 || targetControlPoints.Length < controlPointCount)
            {
                return Array.Empty<SketchBlockObservation>();
            }

            int blockCount = storedBlocks.Length;
            var result = new SketchBlockObservation[blockCount];
            Span<Vector3d> groupSums = stackalloc Vector3d[4];
            Span<int> groupCounts = stackalloc int[4];

            for (int b = 0; b < blockCount; b++)
            {
                uint mask = storedBlocks[b].ParticipationMask;
                if (mask == 0u)
                {
                    result[b] = SketchBlockObservation.Invalid(b);
                    continue;
                }

                groupSums.Clear();
                groupCounts.Clear();
                bool blockValid = true;
                bool hasAnchor = false;
                Vector3d anchor = Vector3d.zero;

                for (int j = 0; j < k_SamplesPerBlock; j++)
                {
                    if (((mask >> j) & 1u) == 0u)
                    {
                        continue;
                    }

                    int cp = GetSampleControlPointIndex(controlPointCount, blockCount, b, j);
                    if ((uint)cp >= (uint)controlPointCount)
                    {
                        blockValid = false;
                        break;
                    }

                    Vector3d p = targetControlPoints[cp];
                    if (!IsFinite(p))
                    {
                        blockValid = false;
                        break;
                    }

                    if (!hasAnchor)
                    {
                        anchor = p;
                        hasAnchor = true;
                    }

                    int g = j & 3;
                    groupSums[g] += p - anchor;
                    groupCounts[g]++;
                }

                if (!blockValid ||
                    groupCounts[0] < k_MinGroupSamples ||
                    groupCounts[1] < k_MinGroupSamples ||
                    groupCounts[2] < k_MinGroupSamples ||
                    groupCounts[3] < k_MinGroupSamples)
                {
                    result[b] = SketchBlockObservation.Invalid(b);
                    continue;
                }

                Vector3d mu0 = groupSums[0] / groupCounts[0];
                Vector3d v1 = (groupSums[1] / groupCounts[1]) - mu0;
                Vector3d v2 = (groupSums[2] / groupCounts[2]) - mu0;
                Vector3d v3 = (groupSums[3] / groupCounts[3]) - mu0;
                result[b] = IsFinite(v1) && IsFinite(v2) && IsFinite(v3)
                    ? new SketchBlockObservation(b, true, v1, v2, v3)
                    : SketchBlockObservation.Invalid(b);
            }

            return result;
        }

        /// <summary>
        /// Extracts observed per-block centroid contrasts from a Unity <see cref="UnityMeshSnapshot"/> and <see cref="CsrVertexMap"/>.
        /// Averages split Unity vertices once per sampled control point so UV/normal seam multiplicity never biases block centroids,
        /// and drops an entire block whenever any participating control point is missing (e.g. dropped degenerate triangle) or position-divergent.
        /// </summary>
        public static SketchBlockObservation[] ExtractTargetContrastsFromUnity(
            ReadOnlySpan<StoredSketchBlock> storedBlocks,
            int controlPointCount,
            UnityMeshSnapshot snapshot,
            CsrVertexMap csr)
        {
            if (storedBlocks.IsEmpty ||
                controlPointCount <= 0 ||
                snapshot?.Vertices == null ||
                csr == null ||
                csr.SourceDomain != IndexDomain.UnityVertex ||
                csr.TargetDomain != IndexDomain.ControlPoint ||
                csr.SourceCount != snapshot.Vertices.Length)
            {
                return Array.Empty<SketchBlockObservation>();
            }

            int blockCount = storedBlocks.Length;
            var vertices = snapshot.Vertices;
            if (vertices.Length == 0)
            {
                return Array.Empty<SketchBlockObservation>();
            }

            // Collect only the distinct control points referenced by active sketch masks (<= 640 CPs).
            var sampledCpToSlot = new Dictionary<int, int>(blockCount * k_SamplesPerBlock);
            for (int b = 0; b < blockCount; b++)
            {
                uint mask = storedBlocks[b].ParticipationMask;
                if (mask == 0u)
                {
                    continue;
                }

                for (int j = 0; j < k_SamplesPerBlock; j++)
                {
                    if (((mask >> j) & 1u) == 0u)
                    {
                        continue;
                    }

                    int cp = GetSampleControlPointIndex(controlPointCount, blockCount, b, j);
                    if ((uint)cp < (uint)controlPointCount && !sampledCpToSlot.ContainsKey(cp))
                    {
                        sampledCpToSlot.Add(cp, sampledCpToSlot.Count);
                    }
                }
            }

            if (sampledCpToSlot.Count == 0)
            {
                return Array.Empty<SketchBlockObservation>();
            }

            // Compute mesh bounding-box extent for split-vertex agreement tolerance.
            Vector3d minPos = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            Vector3d maxPos = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            int finiteVertexCount = 0;
            for (int v = 0; v < vertices.Length; v++)
            {
                var p = vertices[v];
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
                    float.IsNaN(p.y) || float.IsInfinity(p.y) ||
                    float.IsNaN(p.z) || float.IsInfinity(p.z))
                {
                    continue;
                }

                finiteVertexCount++;
                if (p.x < minPos.x) minPos.x = p.x;
                if (p.y < minPos.y) minPos.y = p.y;
                if (p.z < minPos.z) minPos.z = p.z;
                if (p.x > maxPos.x) maxPos.x = p.x;
                if (p.y > maxPos.y) maxPos.y = p.y;
                if (p.z > maxPos.z) maxPos.z = p.z;
            }

            if (finiteVertexCount == 0)
            {
                return Array.Empty<SketchBlockObservation>();
            }

            double extent = (maxPos - minPos).magnitude;
            double splitAgreeTolSq = Math.Max(1e-6d, 1e-4d * extent);
            splitAgreeTolSq *= splitAgreeTolSq;

            int slotCount = sampledCpToSlot.Count;
            var cpFirstPos = new Vector3d[slotCount];
            var cpSumOffsets = new Vector3d[slotCount];
            var cpCounts = new int[slotCount];
            var cpDivergent = new bool[slotCount];

            for (int v = 0; v < vertices.Length; v++)
            {
                var row = csr.GetRow(v);
                if (row.IsEmpty)
                {
                    continue;
                }

                var vp = vertices[v];
                var p = new Vector3d(vp.x, vp.y, vp.z);
                bool finite = IsFinite(p);

                // Deduplicate CPs within a single vertex's CSR row if needed.
                int prevCp = -1;
                for (int r = 0; r < row.Length; r++)
                {
                    int cp = row[r];
                    if (cp == prevCp)
                    {
                        continue;
                    }

                    prevCp = cp;
                    if (!sampledCpToSlot.TryGetValue(cp, out int slot))
                    {
                        continue;
                    }

                    if (!finite)
                    {
                        cpDivergent[slot] = true;
                        continue;
                    }

                    int c = cpCounts[slot];
                    if (c == 0)
                    {
                        cpFirstPos[slot] = p;
                        cpCounts[slot] = 1;
                    }
                    else
                    {
                        Vector3d diff = p - cpFirstPos[slot];
                        if (diff.sqrMagnitude > splitAgreeTolSq)
                        {
                            cpDivergent[slot] = true;
                        }
                        else
                        {
                            cpSumOffsets[slot] += diff;
                            cpCounts[slot] = c + 1;
                        }
                    }
                }
            }

            var result = new SketchBlockObservation[blockCount];
            Span<Vector3d> groupSums = stackalloc Vector3d[4];
            Span<int> groupCounts = stackalloc int[4];

            for (int b = 0; b < blockCount; b++)
            {
                uint mask = storedBlocks[b].ParticipationMask;
                if (mask == 0u)
                {
                    result[b] = SketchBlockObservation.Invalid(b);
                    continue;
                }

                groupSums.Clear();
                groupCounts.Clear();
                bool blockValid = true;
                bool hasAnchor = false;
                Vector3d anchor = Vector3d.zero;

                for (int j = 0; j < k_SamplesPerBlock; j++)
                {
                    if (((mask >> j) & 1u) == 0u)
                    {
                        continue;
                    }

                    int cp = GetSampleControlPointIndex(controlPointCount, blockCount, b, j);
                    if (!sampledCpToSlot.TryGetValue(cp, out int slot) ||
                        cpCounts[slot] <= 0 ||
                        cpDivergent[slot])
                    {
                        // Drop the entire block if ANY set bit in mask is missing or position-ambiguous.
                        blockValid = false;
                        break;
                    }

                    Vector3d meanPos = cpFirstPos[slot] + (cpSumOffsets[slot] / cpCounts[slot]);
                    if (!hasAnchor)
                    {
                        anchor = meanPos;
                        hasAnchor = true;
                    }

                    int g = j & 3;
                    groupSums[g] += meanPos - anchor;
                    groupCounts[g]++;
                }

                if (!blockValid ||
                    groupCounts[0] < k_MinGroupSamples ||
                    groupCounts[1] < k_MinGroupSamples ||
                    groupCounts[2] < k_MinGroupSamples ||
                    groupCounts[3] < k_MinGroupSamples)
                {
                    result[b] = SketchBlockObservation.Invalid(b);
                    continue;
                }

                Vector3d mu0 = groupSums[0] / groupCounts[0];
                Vector3d v1 = (groupSums[1] / groupCounts[1]) - mu0;
                Vector3d v2 = (groupSums[2] / groupCounts[2]) - mu0;
                Vector3d v3 = (groupSums[3] / groupCounts[3]) - mu0;
                result[b] = IsFinite(v1) && IsFinite(v2) && IsFinite(v3)
                    ? new SketchBlockObservation(b, true, v1, v2, v3)
                    : SketchBlockObservation.Invalid(b);
            }

            return result;
        }

        /// <summary>
        /// Refines <paramref name="baseLinearTransform"/> (<c>A</c>) by fitting an orthogonal similarity residual
        /// <c>(s, Q)</c> against <paramref name="storedBlocks"/> and <paramref name="observations"/>.
        /// Keeps <paramref name="baseLinearTransform"/> unchanged when consensus fails or no residual adjustment is needed.
        /// </summary>
        public static bool TryRefineLinearTransform(
            ReadOnlySpan<StoredSketchBlock> storedBlocks,
            ReadOnlySpan<SketchBlockObservation> observations,
            FbxMatrix4x4 baseLinearTransform,
            out FbxMatrix4x4 refinedLinearTransform,
            out bool wasRefined,
            out string diagnostic)
        {
            refinedLinearTransform = baseLinearTransform;
            wasRefined = false;
            diagnostic = string.Empty;

            if (storedBlocks.IsEmpty || observations.IsEmpty || storedBlocks.Length != observations.Length ||
                storedBlocks.Length > k_MaxBlocks)
            {
                return true;
            }

            FbxMatrix4x4 linearA = FbxLinearAlgebra3d.ExtractLinear(baseLinearTransform);
            if (!FbxAffineSpaceTransform.TryCreate(linearA, out var xformA))
            {
                diagnostic = "GeometrySketch skipped: baseline linear transform is singular.";
                return false;
            }

            // Primary residual model: y = v * A^-1, fitting y ~= s * u * Q so M_lin = (s * Q) * A.
            bool leftOk = TrySolveResidualSide(
                storedBlocks,
                observations,
                linearA,
                xformA.InverseOffset,
                leftSide: true,
                out var leftRefined,
                out var leftResidual,
                out bool leftChanged,
                out _,
                out string leftDiag);

            // When A is an orthogonal similarity, left-side and right-side spaces are identical (s*Q*A == A*(s*A^-1*Q*A)).
            // When A is non-isotropic (e.g. non-uniform GeometryToNode), ALSO evaluate right-side residual M_lin = A * (s * Q)
            // and compare BOTH candidates directly in the common target observation space so an approximate ~1% left fit
            // never shadows an exact right-side rotation/scale fit!
            bool rightOk = false;
            FbxMatrix4x4 rightRefined = linearA;
            FbxMatrix4x4 rightResidual = FbxMatrix4x4.Identity;
            bool rightChanged = false;
            string rightDiag = string.Empty;
            if (!FbxLinearAlgebra3d.TryExtractOrthogonalSimilarity(linearA, out _, out _))
            {
                rightOk = TrySolveResidualSide(
                    storedBlocks,
                    observations,
                    linearA,
                    xformA.InverseOffset,
                    leftSide: false,
                    out rightRefined,
                    out rightResidual,
                    out rightChanged,
                    out _,
                    out rightDiag);
            }

            bool leftValid = false;
            int leftInliers = 0;
            double leftTargetRms = double.PositiveInfinity;
            double medianNormV = 0d;
            if (leftOk)
            {
                EvaluateTargetSpaceFit(storedBlocks, observations, leftRefined, out leftInliers, out int leftValidBlocks, out leftTargetRms, out medianNormV);
                leftValid = leftValidBlocks > 0 && (leftInliers * 2 > leftValidBlocks);
            }

            bool rightValid = false;
            int rightInliers = 0;
            double rightTargetRms = double.PositiveInfinity;
            if (rightOk)
            {
                EvaluateTargetSpaceFit(storedBlocks, observations, rightRefined, out rightInliers, out int rightValidBlocks, out rightTargetRms, out double rightMedianNormV);
                if (medianNormV <= 0d) medianNormV = rightMedianNormV;
                rightValid = rightValidBlocks > 0 && (rightInliers * 2 > rightValidBlocks);
            }

            if (leftValid && rightValid)
            {
                double tieTol = Math.Max(1e-9d, 1e-6d * medianNormV);
                bool preferRight = rightInliers > leftInliers ||
                                   (rightInliers == leftInliers && rightTargetRms + tieTol < leftTargetRms);
                if (preferRight)
                {
                    refinedLinearTransform = rightChanged ? (baseLinearTransform * rightResidual) : baseLinearTransform;
                    wasRefined = rightChanged;
                    diagnostic = rightDiag;
                    return true;
                }

                refinedLinearTransform = CopyTranslationWithLinear(baseLinearTransform, leftRefined);
                wasRefined = leftChanged;
                diagnostic = leftDiag;
                return true;
            }

            if (leftValid)
            {
                refinedLinearTransform = CopyTranslationWithLinear(baseLinearTransform, leftRefined);
                wasRefined = leftChanged;
                diagnostic = leftDiag;
                return true;
            }

            if (rightValid)
            {
                refinedLinearTransform = rightChanged ? (baseLinearTransform * rightResidual) : baseLinearTransform;
                wasRefined = rightChanged;
                diagnostic = rightDiag;
                return true;
            }

            // Graceful fallback: keep baseline transform untouched when no candidate retains a strict target-space inlier majority.
            refinedLinearTransform = baseLinearTransform;
            wasRefined = false;
            diagnostic = (leftOk || rightOk)
                ? "GeometrySketch kept baseline: candidate failed strict target-space inlier majority."
                : leftDiag;
            return true;
        }

        private static void EvaluateTargetSpaceFit(
            ReadOnlySpan<StoredSketchBlock> storedBlocks,
            ReadOnlySpan<SketchBlockObservation> observations,
            FbxMatrix4x4 candidateLinear,
            out int inlierCount,
            out int validCount,
            out double inlierRms,
            out double medianNormV)
        {
            inlierCount = 0;
            validCount = 0;
            inlierRms = double.PositiveInfinity;
            var normsV = new List<double>(storedBlocks.Length);
            for (int b = 0; b < storedBlocks.Length; b++)
            {
                if (storedBlocks[b].ParticipationMask == 0u || !observations[b].IsValid)
                {
                    continue;
                }

                double sumSqV = observations[b].Contrast1.sqrMagnitude +
                                observations[b].Contrast2.sqrMagnitude +
                                observations[b].Contrast3.sqrMagnitude;
                if (sumSqV > k_MinBlockNormSq)
                {
                    normsV.Add(Math.Sqrt(sumSqV / 3d));
                }
            }

            medianNormV = ComputeMedian(normsV);
            double absFloorTol = Math.Max(1e-7d, k_BlockInlierFloorFactor * medianNormV);
            double inlierSqErr = 0d;
            double totalSqErr = 0d;

            for (int b = 0; b < storedBlocks.Length; b++)
            {
                if (storedBlocks[b].ParticipationMask == 0u || !observations[b].IsValid)
                {
                    continue;
                }

                double sumSqU = storedBlocks[b].Contrast1.sqrMagnitude +
                                storedBlocks[b].Contrast2.sqrMagnitude +
                                storedBlocks[b].Contrast3.sqrMagnitude;
                Vector3d pred1 = candidateLinear.TransformVector(storedBlocks[b].Contrast1);
                Vector3d pred2 = candidateLinear.TransformVector(storedBlocks[b].Contrast2);
                Vector3d pred3 = candidateLinear.TransformVector(storedBlocks[b].Contrast3);
                double sumSqPred = pred1.sqrMagnitude + pred2.sqrMagnitude + pred3.sqrMagnitude;
                double sumSqV = observations[b].Contrast1.sqrMagnitude +
                                observations[b].Contrast2.sqrMagnitude +
                                observations[b].Contrast3.sqrMagnitude;

                // Include any valid observation whose stored, predicted, or observed contrasts are nondegenerate
                // so collapsed target blocks (sumSqV == 0) count in validCount against non-collapsed predictions.
                if (!(sumSqV > k_MinBlockNormSq) && !(sumSqU > k_MinBlockNormSq) && !(sumSqPred > k_MinBlockNormSq))
                {
                    continue;
                }

                validCount++;
                double rmsV = sumSqV > 0d ? Math.Sqrt(sumSqV / 3d) : 0d;
                Vector3d e1 = observations[b].Contrast1 - pred1;
                Vector3d e2 = observations[b].Contrast2 - pred2;
                Vector3d e3 = observations[b].Contrast3 - pred3;
                double blockErr = Math.Sqrt((e1.sqrMagnitude + e2.sqrMagnitude + e3.sqrMagnitude) / 3d);
                totalSqErr += blockErr * blockErr;

                double tol = Math.Max(absFloorTol, k_BlockInlierRelativeTolerance * rmsV);
                if (blockErr <= tol)
                {
                    inlierCount++;
                    inlierSqErr += blockErr * blockErr;
                }
            }

            if (inlierCount > 0)
            {
                inlierRms = Math.Sqrt(inlierSqErr / inlierCount);
            }
            else if (validCount > 0)
            {
                inlierRms = Math.Sqrt(totalSqErr / validCount);
            }
        }

        private readonly struct BlockPairData
        {
            public readonly int BlockIndex;
            public readonly Vector3d U1;
            public readonly Vector3d U2;
            public readonly Vector3d U3;
            public readonly Vector3d Y1;
            public readonly Vector3d Y2;
            public readonly Vector3d Y3;
            public readonly double RmsNormU;
            public readonly double RmsNormY;
            public readonly bool InternallyConsistent;

            public BlockPairData(
                int blockIndex,
                Vector3d u1,
                Vector3d u2,
                Vector3d u3,
                Vector3d y1,
                Vector3d y2,
                Vector3d y3,
                double rmsNormU,
                double rmsNormY,
                bool internallyConsistent)
            {
                BlockIndex = blockIndex;
                U1 = u1;
                U2 = u2;
                U3 = u3;
                Y1 = y1;
                Y2 = y2;
                Y3 = y3;
                RmsNormU = rmsNormU;
                RmsNormY = rmsNormY;
                InternallyConsistent = internallyConsistent;
            }
        }

        private static bool TrySolveResidualSide(
            ReadOnlySpan<StoredSketchBlock> storedBlocks,
            ReadOnlySpan<SketchBlockObservation> observations,
            FbxMatrix4x4 linearA,
            FbxMatrix4x4 invLinearA,
            bool leftSide,
            out FbxMatrix4x4 composedLinear,
            out FbxMatrix4x4 residualMatrix,
            out bool wasRefined,
            out double normalizedRms,
            out string diagnostic)
        {
            composedLinear = linearA;
            residualMatrix = FbxMatrix4x4.Identity;
            wasRefined = false;
            normalizedRms = double.PositiveInfinity;
            diagnostic = string.Empty;

            var validBlocks = new List<BlockPairData>(storedBlocks.Length);
            var scaleRatios = new List<double>(storedBlocks.Length);
            var normYs = new List<double>(storedBlocks.Length);

            for (int b = 0; b < storedBlocks.Length; b++)
            {
                if (storedBlocks[b].ParticipationMask == 0u || !observations[b].IsValid)
                {
                    continue;
                }

                Vector3d u1, u2, u3, y1, y2, y3;
                if (leftSide)
                {
                    u1 = storedBlocks[b].Contrast1;
                    u2 = storedBlocks[b].Contrast2;
                    u3 = storedBlocks[b].Contrast3;
                    y1 = invLinearA.TransformVector(observations[b].Contrast1);
                    y2 = invLinearA.TransformVector(observations[b].Contrast2);
                    y3 = invLinearA.TransformVector(observations[b].Contrast3);
                }
                else
                {
                    u1 = linearA.TransformVector(storedBlocks[b].Contrast1);
                    u2 = linearA.TransformVector(storedBlocks[b].Contrast2);
                    u3 = linearA.TransformVector(storedBlocks[b].Contrast3);
                    y1 = observations[b].Contrast1;
                    y2 = observations[b].Contrast2;
                    y3 = observations[b].Contrast3;
                }

                double sumSqU = u1.sqrMagnitude + u2.sqrMagnitude + u3.sqrMagnitude;
                double sumSqY = y1.sqrMagnitude + y2.sqrMagnitude + y3.sqrMagnitude;
                if (!(sumSqU > k_MinBlockNormSq) || !(sumSqY > k_MinBlockNormSq))
                {
                    continue;
                }

                double rmsU = Math.Sqrt(sumSqU / 3d);
                double rmsY = Math.Sqrt(sumSqY / 3d);
                double ratio = rmsY / rmsU;
                if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0d)
                {
                    continue;
                }

                // Check whether the 3 contrasts in this single block admit an internal similarity fit.
                bool consistent = false;
                var tempPair = new BlockPairData(b, u1, u2, u3, y1, y2, y3, rmsU, rmsY, true);
                if (TryFitSimilarityOnSubset(new[] { tempPair }, out double singleS, out var singleQ))
                {
                    double selfErr = ComputeBlockRmsError(in tempPair, singleS, in singleQ);
                    consistent = selfErr <= Math.Max(1e-5d, 2.5e-2d * rmsY);
                }

                validBlocks.Add(new BlockPairData(b, u1, u2, u3, y1, y2, y3, rmsU, rmsY, consistent));
                scaleRatios.Add(ratio);
                normYs.Add(rmsY);
            }

            if (validBlocks.Count == 0)
            {
                return true;
            }

            double medianScale = ComputeMedian(scaleRatios);
            double medianNormY = ComputeMedian(normYs);
            double absFloorTol = Math.Max(1e-7d, k_BlockInlierFloorFactor * medianNormY);

            int totalBlocks = validBlocks.Count;
            int minRequiredInliers = totalBlocks == 1 ? 1 : (totalBlocks / 2) + 1;

            int bestInliers = -1;
            double bestInlierSqErr = double.PositiveInfinity;
            double bestS = 1d;
            FbxMatrix4x4 bestQ = FbxMatrix4x4.Identity;

            void EvaluateCandidate(double candS, FbxMatrix4x4 candQ)
            {
                if (!(candS > 0d) || double.IsNaN(candS) || double.IsInfinity(candS))
                {
                    return;
                }

                int inliers = 0;
                double sqErrSum = 0d;
                for (int i = 0; i < validBlocks.Count; i++)
                {
                    var blk = validBlocks[i];
                    double err = ComputeBlockRmsError(in blk, candS, in candQ);
                    double tol = Math.Max(absFloorTol, k_BlockInlierRelativeTolerance * blk.RmsNormY);
                    if (err <= tol)
                    {
                        inliers++;
                        sqErrSum += err * err;
                    }
                }

                if (inliers > bestInliers || (inliers == bestInliers && sqErrSum < bestInlierSqErr))
                {
                    bestInliers = inliers;
                    bestInlierSqErr = sqErrSum;
                    bestS = candS;
                    bestQ = candQ;
                }
            }

            // 1. Test identity residual first.
            EvaluateCandidate(1d, FbxMatrix4x4.Identity);

            // 2. Test all 48 signed axis permutations with median scale (and 1.0).
            for (int p = 0; p < s_SignedPermutations48.Length; p++)
            {
                EvaluateCandidate(medianScale, s_SignedPermutations48[p]);
                if (Math.Abs(medianScale - 1d) > 1e-4d)
                {
                    EvaluateCandidate(1d, s_SignedPermutations48[p]);
                }
            }

            // 3. Test single-block and deterministic block-pair SVD candidates from internally consistent blocks.
            var seedBlocks = new List<BlockPairData>(validBlocks.Count);
            for (int i = 0; i < validBlocks.Count; i++)
            {
                if (validBlocks[i].InternallyConsistent)
                {
                    seedBlocks.Add(validBlocks[i]);
                }
            }

            var oneBuf = new BlockPairData[1];
            for (int i = 0; i < seedBlocks.Count; i++)
            {
                oneBuf[0] = seedBlocks[i];
                if (TryFitSimilarityOnSubset(oneBuf, out double s1, out var q1))
                {
                    EvaluateCandidate(s1, q1);
                }
            }

            var twoBuf = new BlockPairData[2];
            for (int i = 0; i < seedBlocks.Count; i++)
            {
                twoBuf[0] = seedBlocks[i];
                for (int j = i + 1; j < seedBlocks.Count; j++)
                {
                    twoBuf[1] = seedBlocks[j];
                    if (TryFitSimilarityOnSubset(twoBuf, out double s2, out var q2))
                    {
                        EvaluateCandidate(s2, q2);
                    }
                }
            }

            if (bestInliers < minRequiredInliers)
            {
                diagnostic = $"GeometrySketch consensus rejected non-rigid/ambiguous residual ({bestInliers}/{totalBlocks} inlier blocks); keeping baseline transform.";
                return false;
            }

            // Collect winning inliers and refit SVD across all inlier blocks jointly.
            var inlierList = new List<BlockPairData>(bestInliers);
            for (int i = 0; i < validBlocks.Count; i++)
            {
                var blk = validBlocks[i];
                double err = ComputeBlockRmsError(in blk, bestS, in bestQ);
                double tol = Math.Max(absFloorTol, k_BlockInlierRelativeTolerance * blk.RmsNormY);
                if (err <= tol)
                {
                    inlierList.Add(blk);
                }
            }

            if (!TryFitSimilarityOnSubset(inlierList, out double fitS, out var fitQ))
            {
                diagnostic = "GeometrySketch SVD refit was rank-deficient (< 2); keeping baseline transform.";
                return false;
            }

            // Verify refit preserves required consensus.
            int verifiedInliers = 0;
            double fitSqErr = 0d;
            double inlierNormSq = 0d;
            for (int i = 0; i < inlierList.Count; i++)
            {
                var blk = inlierList[i];
                double err = ComputeBlockRmsError(in blk, fitS, in fitQ);
                double tol = Math.Max(absFloorTol, k_BlockInlierRelativeTolerance * blk.RmsNormY);
                if (err <= tol)
                {
                    verifiedInliers++;
                }

                fitSqErr += err * err;
                inlierNormSq += blk.RmsNormY * blk.RmsNormY;
            }

            if (verifiedInliers < minRequiredInliers)
            {
                diagnostic = $"GeometrySketch refit lost block consensus ({verifiedInliers}/{totalBlocks}); keeping baseline transform.";
                return false;
            }

            double fitRms = Math.Sqrt(fitSqErr / inlierList.Count);
            double inlierRefRms = Math.Sqrt(inlierNormSq / inlierList.Count);
            normalizedRms = inlierRefRms > 0d ? fitRms / inlierRefRms : 0d;

            // Residual-verified snapping: preserve exact baseline (1, I) when indistinguishable within float32 sketch precision.
            double snapRmsCeiling = Math.Max(absFloorTol, 1.05d * fitRms);
            if (Math.Abs(fitS - 1d) <= k_ScaleSnapRelativeTolerance &&
                FrobeniusDiff3x3(fitQ, FbxMatrix4x4.Identity) <= k_OrthogonalSnapFrobeniusTolerance &&
                ComputeSubsetRmsError(inlierList, 1d, FbxMatrix4x4.Identity) <= snapRmsCeiling)
            {
                composedLinear = linearA;
                wasRefined = false;
                return true;
            }

            // Snap Q to nearest signed axis permutation within the SAME determinant sign when residual-verified.
            double detSign = FbxLinearAlgebra3d.Determinant3x3(fitQ) >= 0d ? 1d : -1d;
            double bestPermDist = double.PositiveInfinity;
            int bestPermIdx = -1;
            for (int p = 0; p < s_SignedPermutations48.Length; p++)
            {
                double permDet = FbxLinearAlgebra3d.Determinant3x3(s_SignedPermutations48[p]);
                if (permDet * detSign <= 0d)
                {
                    continue;
                }

                double dist = FrobeniusDiff3x3(fitQ, s_SignedPermutations48[p]);
                if (dist < bestPermDist)
                {
                    bestPermDist = dist;
                    bestPermIdx = p;
                }
            }

            if (bestPermIdx >= 0 &&
                bestPermDist <= k_OrthogonalSnapFrobeniusTolerance &&
                ComputeSubsetRmsError(inlierList, fitS, s_SignedPermutations48[bestPermIdx]) <= snapRmsCeiling)
            {
                fitQ = s_SignedPermutations48[bestPermIdx];
            }

            // Snap s to standard metric power-of-ten scale only when within tight proximity AND residual-verified.
            for (int k = 0; k < s_StandardScales.Length; k++)
            {
                double std = s_StandardScales[k];
                if (Math.Abs(fitS - std) / std <= k_ScaleSnapRelativeTolerance &&
                    ComputeSubsetRmsError(inlierList, std, fitQ) <= snapRmsCeiling)
                {
                    fitS = std;
                    break;
                }
            }

            if (fitS == 1d && FbxAffineSpaceTransform.IsExactIdentity(fitQ))
            {
                composedLinear = linearA;
                wasRefined = false;
                return true;
            }

            residualMatrix = ScaleLinear(fitQ, fitS);
            composedLinear = leftSide ? (residualMatrix * linearA) : (linearA * residualMatrix);
            wasRefined = true;
            diagnostic = $"GeometrySketch calibrated mesh transform (scale={fitS:G6}, inliers={verifiedInliers}/{totalBlocks}).";
            return true;
        }

        private static bool TryFitSimilarityOnSubset(
            IReadOnlyList<BlockPairData> blocks,
            out double scale,
            out FbxMatrix4x4 orthogonalQ)
        {
            scale = 1d;
            orthogonalQ = FbxMatrix4x4.Identity;
            if (blocks == null || blocks.Count == 0)
            {
                return false;
            }

            double h00 = 0d, h01 = 0d, h02 = 0d;
            double h10 = 0d, h11 = 0d, h12 = 0d;
            double h20 = 0d, h21 = 0d, h22 = 0d;
            double sumSqU = 0d;

            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                AccumulateOuter(b.U1, b.Y1, ref h00, ref h01, ref h02, ref h10, ref h11, ref h12, ref h20, ref h21, ref h22, ref sumSqU);
                AccumulateOuter(b.U2, b.Y2, ref h00, ref h01, ref h02, ref h10, ref h11, ref h12, ref h20, ref h21, ref h22, ref sumSqU);
                AccumulateOuter(b.U3, b.Y3, ref h00, ref h01, ref h02, ref h10, ref h11, ref h12, ref h20, ref h21, ref h22, ref sumSqU);
            }

            if (!(sumSqU > k_MinBlockNormSq))
            {
                return false;
            }

            var cov = new FbxMatrix4x4(
                h00, h01, h02, 0d,
                h10, h11, h12, 0d,
                h20, h21, h22, 0d,
                0d, 0d, 0d, 1d);

            if (!FbxLinearAlgebra3d.TrySvd3x3(cov, out var uMat, out var sigma, out var vMat))
            {
                return false;
            }

            // Require at least rank 2 (non-collinear contrasts) to determine 3D orientation.
            if (sigma.x <= 1e-12d || sigma.y <= k_Rank1SingularRatioThreshold * sigma.x)
            {
                return false;
            }

            // At rank 3 (sigma.z > 1e-5 * sigma.x), TrySvd3x3 already preserves sign(det(cov)) in U so det(U * V^T) = +-1.
            // At rank 2 (planar block/mesh), enforce residual parity +1 (Q in SO(3)) so noise in sigma.z never flips normal parity!
            FbxMatrix4x4 vt = Transpose3x3(vMat);
            if (sigma.z <= k_Rank2SingularRatioThreshold * sigma.x)
            {
                var qCandidate = uMat * vt;
                if (FbxLinearAlgebra3d.Determinant3x3(qCandidate) < 0d)
                {
                    uMat = new FbxMatrix4x4(
                        uMat[0, 0], uMat[0, 1], -uMat[0, 2], 0d,
                        uMat[1, 0], uMat[1, 1], -uMat[1, 2], 0d,
                        uMat[2, 0], uMat[2, 1], -uMat[2, 2], 0d,
                        0d, 0d, 0d, 1d);
                }
            }

            orthogonalQ = uMat * vt;

            double num = 0d;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                num += Vector3d.Dot(orthogonalQ.TransformVector(b.U1), b.Y1);
                num += Vector3d.Dot(orthogonalQ.TransformVector(b.U2), b.Y2);
                num += Vector3d.Dot(orthogonalQ.TransformVector(b.U3), b.Y3);
            }

            scale = num / sumSqU;
            return scale > 0d && !double.IsNaN(scale) && !double.IsInfinity(scale);
        }

        private static void AccumulateOuter(
            Vector3d u,
            Vector3d y,
            ref double h00, ref double h01, ref double h02,
            ref double h10, ref double h11, ref double h12,
            ref double h20, ref double h21, ref double h22,
            ref double sumSqU)
        {
            h00 += u.x * y.x; h01 += u.x * y.y; h02 += u.x * y.z;
            h10 += u.y * y.x; h11 += u.y * y.y; h12 += u.y * y.z;
            h20 += u.z * y.x; h21 += u.z * y.y; h22 += u.z * y.z;
            sumSqU += u.sqrMagnitude;
        }

        private static double ComputeBlockRmsError(in BlockPairData b, double s, in FbxMatrix4x4 q)
        {
            Vector3d e1 = b.Y1 - (q.TransformVector(b.U1) * s);
            Vector3d e2 = b.Y2 - (q.TransformVector(b.U2) * s);
            Vector3d e3 = b.Y3 - (q.TransformVector(b.U3) * s);
            return Math.Sqrt((e1.sqrMagnitude + e2.sqrMagnitude + e3.sqrMagnitude) / 3d);
        }

        private static double ComputeSubsetRmsError(IReadOnlyList<BlockPairData> blocks, double s, FbxMatrix4x4 q)
        {
            double sumSq = 0d;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                double e = ComputeBlockRmsError(in b, s, in q);
                sumSq += e * e;
            }

            return Math.Sqrt(sumSq / blocks.Count);
        }

        private static int GetSampleControlPointIndex(int controlPointCount, int blockCount, int blockIndex, int sampleSlot)
        {
            long start = ((long)blockIndex * controlPointCount) / blockCount;
            long end = ((long)(blockIndex + 1) * controlPointCount) / blockCount;
            long length = end - start;
            return (int)(start + ((2L * sampleSlot + 1L) * length) / (2L * k_SamplesPerBlock));
        }

        private static double ComputeMedian(List<double> values)
        {
            if (values.Count == 0)
            {
                return 1d;
            }

            var copy = new double[values.Count];
            values.CopyTo(copy);
            Array.Sort(copy);
            int mid = copy.Length / 2;
            return (copy.Length & 1) == 1 ? copy[mid] : 0.5d * (copy[mid - 1] + copy[mid]);
        }

        private static FbxMatrix4x4 Transpose3x3(FbxMatrix4x4 m)
        {
            return new FbxMatrix4x4(
                m[0, 0], m[1, 0], m[2, 0], 0d,
                m[0, 1], m[1, 1], m[2, 1], 0d,
                m[0, 2], m[1, 2], m[2, 2], 0d,
                0d, 0d, 0d, 1d);
        }

        private static FbxMatrix4x4 ScaleLinear(FbxMatrix4x4 m, double s)
        {
            return new FbxMatrix4x4(
                m[0, 0] * s, m[0, 1] * s, m[0, 2] * s, 0d,
                m[1, 0] * s, m[1, 1] * s, m[1, 2] * s, 0d,
                m[2, 0] * s, m[2, 1] * s, m[2, 2] * s, 0d,
                0d, 0d, 0d, 1d);
        }

        private static FbxMatrix4x4 CopyTranslationWithLinear(FbxMatrix4x4 original, FbxMatrix4x4 linear)
        {
            return new FbxMatrix4x4(
                linear[0, 0], linear[0, 1], linear[0, 2], 0d,
                linear[1, 0], linear[1, 1], linear[1, 2], 0d,
                linear[2, 0], linear[2, 1], linear[2, 2], 0d,
                original[3, 0], original[3, 1], original[3, 2], 1d);
        }

        private static double FrobeniusDiff3x3(FbxMatrix4x4 a, FbxMatrix4x4 b)
        {
            double sum = 0d;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = a[r, c] - b[r, c];
                    sum += d * d;
                }
            }

            return Math.Sqrt(sum);
        }

        private static FbxMatrix4x4[] BuildSignedPermutations48()
        {
            int[][] perms =
            {
                new[] { 0, 1, 2 },
                new[] { 0, 2, 1 },
                new[] { 1, 0, 2 },
                new[] { 1, 2, 0 },
                new[] { 2, 0, 1 },
                new[] { 2, 1, 0 }
            };

            var list = new FbxMatrix4x4[48];
            int idx = 0;
            for (int p = 0; p < perms.Length; p++)
            {
                var perm = perms[p];
                for (int sMask = 0; sMask < 8; sMask++)
                {
                    double s0 = (sMask & 1) != 0 ? -1d : 1d;
                    double s1 = (sMask & 2) != 0 ? -1d : 1d;
                    double s2 = (sMask & 4) != 0 ? -1d : 1d;
                    var m = new double[3, 3];
                    m[0, perm[0]] = s0;
                    m[1, perm[1]] = s1;
                    m[2, perm[2]] = s2;
                    list[idx++] = new FbxMatrix4x4(
                        m[0, 0], m[0, 1], m[0, 2], 0d,
                        m[1, 0], m[1, 1], m[1, 2], 0d,
                        m[2, 0], m[2, 1], m[2, 2], 0d,
                        0d, 0d, 0d, 1d);
                }
            }

            return list;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x) &&
                   !double.IsNaN(v.y) && !double.IsInfinity(v.y) &&
                   !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }
    }
}
