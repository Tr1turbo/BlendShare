using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.EditorAssets.Mapping
{
    public enum VertexMappingVerificationStatus : byte
    {
        ExactFastPath = 0,
        CoincidentProofFallback = 1,
        VertexCountMismatch = 2,
        ControlPointCountMismatch = 3,
        PositionsMismatch = 4,
        TopologyIncidenceMismatch = 5,
        CoincidentClassConflict = 6,
        InvalidCsr = 7,
        InvalidSpaceContract = 8,
        MissingPayload = 9
    }

    public sealed class VertexMappingVerificationResult
    {
        public VertexMappingVerificationStatus Status { get; }
        public bool Succeeded =>
            Status == VertexMappingVerificationStatus.ExactFastPath ||
            Status == VertexMappingVerificationStatus.CoincidentProofFallback;
        public CsrVertexMap VertexMap { get; }
        public MappingSpaceContract SpaceContract { get; }
        public string Diagnostic { get; }

        public VertexMappingVerificationResult(
            VertexMappingVerificationStatus status,
            CsrVertexMap vertexMap,
            MappingSpaceContract spaceContract,
            string diagnostic)
        {
            Status = status;
            VertexMap = vertexMap;
            SpaceContract = spaceContract;
            Diagnostic = diagnostic ?? string.Empty;
        }
    }

    /// <summary>
    /// Builds and performs 2-stage fail-closed verification of embedded <see cref="VertexMappingEntry"/> records.
    /// Stage 1 (Fast Path): exact vertex count + ordered positions + canonical triangle incidence + UV0 digest.
    /// Stage 2 (UV0/SubMesh Fallback): when UV0 differs, verifies every coincident-position equivalence class proof.
    /// </summary>
    public static class VertexMappingCodec
    {
        public static VertexMappingEntry Build(
            UnityMeshSnapshot targetSnapshot,
            MeshCornerMappingResult inverseMapping,
            int controlPointCount,
            FbxSceneMetadata targetSceneMetadata,
            string targetIdentity = null,
            PatchSpaceProvenance storedSpace = null,
            string label = null)
        {
            if (targetSnapshot == null)
            {
                throw new ArgumentNullException(nameof(targetSnapshot));
            }

            if (inverseMapping == null || !inverseMapping.Succeeded)
            {
                throw new ArgumentException("A succeeded MeshCornerMappingResult is required.", nameof(inverseMapping));
            }

            if (inverseMapping.IsBlendShapeSensitive ||
                inverseMapping.ExceededLayoutDfsBudget ||
                inverseMapping.IsApproximateCoordinateMatch)
            {
                throw new ArgumentException(
                    "Cannot build a standalone geometry+UV0 embedded vertex mapping from a blendshape-sensitive, DFS-budget-exceeded, or lossy-compressed inverse mapping result.",
                    nameof(inverseMapping));
            }

            if (inverseMapping.UnityVertexCount != targetSnapshot.Vertices.Length)
            {
                throw new ArgumentException("Inverse mapping vertex count does not match Unity mesh snapshot.");
            }

            var csr = CsrVertexMapFactory.FromJaggedGroups(
                inverseMapping.VertexControlPointGroups,
                controlPointCount,
                IndexDomain.UnityVertex,
                IndexDomain.ControlPoint);

            var digests = UnityMeshGeometryDigests.Compute(targetSnapshot);
            var contract = BuildSpaceContract(
                storedSpace,
                targetSceneMetadata,
                inverseMapping.MatchedScale,
                inverseMapping.MatchedBakeAxisConversion,
                targetIdentity,
                inverseMapping.HasNonStandardRawTransform ? inverseMapping.MatchedRawToUnityMatrix : (FbxMatrix4x4?)null,
                inverseMapping.HasNonStandardRawTransform ? inverseMapping.MatchedRawToUnitySceneMatrix : (FbxMatrix4x4?)null);

            var coincidentClasses = UnityMeshGeometryDigests.FindCoincidentPositionClasses(targetSnapshot);
            var proofs = new List<CoincidentClassProof>(coincidentClasses.Count);
            for (int c = 0; c < coincidentClasses.Count; c++)
            {
                var cls = coincidentClasses[c];
                bool allIdentical = AllRowsIdentical(csr, cls.UnityVertexIndices);
                bool swapDistinguishable = allIdentical || AreDifferingCsrRowsDistinguishedByIncidence(csr, cls);
                proofs.Add(new CoincidentClassProof
                {
                    UnityVertexIndices = (int[])cls.UnityVertexIndices.Clone(),
                    DiscriminatorDigest = swapDistinguishable ? cls.DiscriminatorDigest : string.Empty
                });
            }

            var payload = new VertexMappingPayload
            {
                CsrRowOffsets = csr.RowOffsets.ToArray(),
                CsrControlPointIndices = csr.TargetIndices.ToArray(),
                FaceCornerIndices = inverseMapping.FirstFbxCorners != null && inverseMapping.FirstFbxCorners.Length > 0
                    ? (int[])inverseMapping.FirstFbxCorners.Clone()
                    : null,
                SpaceContract = contract,
                CoincidentClasses = proofs
            };

            return new VertexMappingEntry
            {
                Label = label ?? string.Empty,
                UnityVertexCount = targetSnapshot.Vertices.Length,
                ControlPointCount = controlPointCount,
                OrderedPositionsDigest = digests.OrderedPositionsDigest,
                CanonicalTriangleIncidenceDigest = digests.CanonicalTriangleIncidenceDigest,
                Uv0Digest = digests.Uv0Digest,
                Payload = LazyPayload<VertexMappingPayload>.FromValue(payload)
            };
        }

        /// <summary>
        /// Builds the coordinate space contract mapping stored patch coordinates into Unity mesh-local and scene-local spaces.
        /// </summary>
        public static MappingSpaceContract BuildSpaceContract(
            PatchSpaceProvenance storedSpace,
            FbxSceneMetadata target,
            float scale,
            bool bakeAxes,
            string targetIdentity,
            FbxMatrix4x4? rawToUnityOverride = null,
            FbxMatrix4x4? rawToUnitySceneOverride = null)
        {
            var effectiveSpace = storedSpace != null
                ? PatchSpaceProvenance.FromMetadata(storedSpace.Axes, storedSpace.MetersPerUnit)
                : PatchSpaceProvenance.FromMetadata(target.Axes, target.UnitMeters);

            if (!effectiveSpace.IsValid)
                throw new ArgumentException("Stored patch space requires valid axes and a strictly positive unit scale.");

            FbxMatrix4x4 matrix;
            FbxMatrix4x4 sceneMatrix;
            bool hasSeparateScene = false;

            if (rawToUnityOverride.HasValue)
            {
                if (!CanonicalMeshSpace.IsValidAxes(target.Axes) || !CanonicalMeshSpace.IsValidUnitMeters(target.UnitMeters))
                    throw new ArgumentException("Unsupported or invalid target FBX coordinate axes or unit scale.");

                var storedToTargetRaw = CanonicalMeshSpace.BuildSpaceToSpace(
                    effectiveSpace.Axes,
                    effectiveSpace.MetersPerUnit,
                    target.Axes,
                    target.UnitMeters,
                    out _,
                    out bool isExactIdentity);
                matrix = isExactIdentity ? rawToUnityOverride.Value : storedToTargetRaw * rawToUnityOverride.Value;
                if (!FbxAffineSpaceTransform.TryCreate(matrix, out _))
                    throw new ArgumentException("Unsupported or singular mapping coordinate transform.");

                var sceneRaw = rawToUnitySceneOverride ?? rawToUnityOverride.Value;
                sceneMatrix = isExactIdentity ? sceneRaw : storedToTargetRaw * sceneRaw;
                if (!FbxAffineSpaceTransform.TryCreate(sceneMatrix, out _))
                    throw new ArgumentException("Unsupported or singular mapping scene coordinate transform.");

                hasSeparateScene = !sceneMatrix.Equals(matrix);
            }
            else if (!UnitySpaceContractMath.TryBuildStoredToUnityMatrix(
                    effectiveSpace.Axes, effectiveSpace.MetersPerUnit, target, scale, bakeAxes, out matrix, out _) ||
                !FbxAffineSpaceTransform.TryCreate(matrix, out _))
            {
                throw new ArgumentException("Unsupported or singular mapping coordinate transform.");
            }
            else
            {
                sceneMatrix = matrix;
            }

            return new MappingSpaceContract
            {
                StoredToUnityMeshLocal = matrix,
                HasSeparateSceneTransform = hasSeparateScene,
                StoredToUnitySceneLocal = hasSeparateScene ? sceneMatrix : FbxMatrix4x4.Identity,
                StoredSpace = effectiveSpace,
                TargetIdentity = targetIdentity ?? string.Empty
            };
        }

        /// <summary>
        /// Pure in-memory header pre-check (positions + canonical triangle incidence + counts) that avoids opening
        /// non-matching payloads. Intentionally does NOT check <see cref="VertexMappingEntry.HasPayload"/> so callers
        /// can distinguish a non-matching header from an eligible header whose payload is missing/corrupt.
        /// </summary>
        public static bool MatchesHeaderGeometry(
            VertexMappingEntry entry,
            in UnityMeshGeometryDigest targetDigests,
            int targetVertexCount,
            int expectedControlPointCount = -1)
        {
            if (entry == null)
            {
                return false;
            }

            if (expectedControlPointCount >= 0 && entry.ControlPointCount != expectedControlPointCount)
            {
                return false;
            }

            return entry.UnityVertexCount == targetVertexCount &&
                   string.Equals(entry.OrderedPositionsDigest, targetDigests.OrderedPositionsDigest, StringComparison.Ordinal) &&
                   string.Equals(entry.CanonicalTriangleIncidenceDigest, targetDigests.CanonicalTriangleIncidenceDigest, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure in-memory header pre-check for Stage 1 exact match (including UV0 digest).
        /// </summary>
        public static bool MatchesHeaderExact(
            VertexMappingEntry entry,
            in UnityMeshGeometryDigest targetDigests,
            int targetVertexCount,
            int expectedControlPointCount = -1)
        {
            return MatchesHeaderGeometry(entry, targetDigests, targetVertexCount, expectedControlPointCount) &&
                   string.Equals(entry.Uv0Digest ?? string.Empty, targetDigests.Uv0Digest ?? string.Empty, StringComparison.Ordinal);
        }

        internal static VertexMappingVerificationStatus DiagnoseHeaderMismatch(
            VertexMappingEntry entry,
            in UnityMeshGeometryDigest targetDigests,
            int targetVertexCount,
            int expectedControlPointCount,
            out string diagnostic)
        {
            if (expectedControlPointCount >= 0 && entry.ControlPointCount != expectedControlPointCount)
            {
                diagnostic = $"Embedded mapping control-point count ({entry.ControlPointCount}) does not match mesh header ({expectedControlPointCount}).";
                return VertexMappingVerificationStatus.ControlPointCountMismatch;
            }

            if (entry.UnityVertexCount != targetVertexCount)
            {
                diagnostic = $"Target mesh vertex count ({targetVertexCount}) does not match embedded mapping ({entry.UnityVertexCount}).";
                return VertexMappingVerificationStatus.VertexCountMismatch;
            }

            if (!string.Equals(entry.OrderedPositionsDigest, targetDigests.OrderedPositionsDigest, StringComparison.Ordinal))
            {
                diagnostic = "Target Unity mesh ordered vertex positions digest does not match embedded mapping.";
                return VertexMappingVerificationStatus.PositionsMismatch;
            }

            diagnostic = "Target Unity mesh canonical triangle incidence digest does not match embedded mapping.";
            return VertexMappingVerificationStatus.TopologyIncidenceMismatch;
        }

        public static VertexMappingVerificationResult Verify(
            VertexMappingEntry entry,
            UnityMeshSnapshot targetSnapshot,
            int expectedControlPointCount = -1,
            PatchSpaceProvenance expectedSpace = null,
            ILazyPayloadSource payloadSource = null)
        {
            if (entry == null)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.InvalidCsr,
                    null,
                    null,
                    "VertexMappingEntry is null.");
            }

            if (targetSnapshot == null)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.VertexCountMismatch,
                    null,
                    null,
                    "Target Unity mesh snapshot is null.");
            }

            // Check header counts and geometry digests BEFORE acquiring lazy payload so mismatched entries
            // perform zero decompression or backing-store I/O.
            int targetVertexCount = targetSnapshot.Vertices.Length;
            var targetDigests = UnityMeshGeometryDigests.Compute(targetSnapshot);
            if (!MatchesHeaderGeometry(entry, targetDigests, targetVertexCount, expectedControlPointCount))
            {
                var mismatchStatus = DiagnoseHeaderMismatch(entry, targetDigests, targetVertexCount, expectedControlPointCount, out string mismatchDiag);
                return new VertexMappingVerificationResult(mismatchStatus, null, null, mismatchDiag);
            }

            if (!entry.HasPayload)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.MissingPayload,
                    null,
                    null,
                    "VertexMappingEntry has no payload.");
            }

            using var lease = entry.Payload.Acquire(payloadSource);
            return Verify(entry, lease.Value, targetSnapshot, expectedControlPointCount, expectedSpace, targetDigests);
        }

        public static VertexMappingVerificationResult Verify(
            VertexMappingEntry entry,
            VertexMappingPayload payload,
            UnityMeshSnapshot targetSnapshot,
            int expectedControlPointCount = -1,
            PatchSpaceProvenance expectedSpace = null,
            UnityMeshGeometryDigest? precomputedDigests = null)
        {
            if (entry == null)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.InvalidCsr,
                    null,
                    null,
                    "VertexMappingEntry is null.");
            }

            if (payload == null)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.MissingPayload,
                    null,
                    null,
                    "VertexMappingPayload is null.");
            }

            if (targetSnapshot == null)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.VertexCountMismatch,
                    null,
                    null,
                    "Target Unity mesh snapshot is null.");
            }

            var resolvedSpace = payload.SpaceContract?.StoredSpace ?? expectedSpace;
            if (payload.SpaceContract == null ||
                resolvedSpace == null ||
                !resolvedSpace.IsValid ||
                (payload.SpaceContract.StoredSpace != null && expectedSpace != null &&
                 !payload.SpaceContract.StoredSpace.MatchesSpace(expectedSpace)) ||
                !FbxAffineSpaceTransform.TryCreate(payload.SpaceContract.StoredToUnityMeshLocal, out _) ||
                (payload.SpaceContract.HasSeparateSceneTransform &&
                 !FbxAffineSpaceTransform.TryCreate(payload.SpaceContract.StoredToUnitySceneLocal, out _)))
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.InvalidSpaceContract,
                    null,
                    null,
                    "Embedded mapping SpaceContract is missing, mismatched, or non-affine/singular.");
            }

            var hydratedContract = new MappingSpaceContract
            {
                TargetIdentity = payload.SpaceContract.TargetIdentity ?? string.Empty,
                StoredToUnityMeshLocal = payload.SpaceContract.StoredToUnityMeshLocal,
                HasSeparateSceneTransform = payload.SpaceContract.HasSeparateSceneTransform,
                StoredToUnitySceneLocal = payload.SpaceContract.HasSeparateSceneTransform
                    ? payload.SpaceContract.StoredToUnitySceneLocal
                    : FbxMatrix4x4.Identity,
                StoredSpace = PatchSpaceProvenance.FromMetadata(resolvedSpace.Axes, resolvedSpace.MetersPerUnit)
            };

            if (expectedControlPointCount >= 0 &&
                entry.ControlPointCount != expectedControlPointCount)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.ControlPointCountMismatch,
                    null,
                    null,
                    $"Embedded mapping control-point count ({entry.ControlPointCount}) does not match mesh header ({expectedControlPointCount}).");
            }

            if (entry.UnityVertexCount != targetSnapshot.Vertices.Length)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.VertexCountMismatch,
                    null,
                    null,
                    $"Target mesh vertex count ({targetSnapshot.Vertices.Length}) does not match embedded mapping ({entry.UnityVertexCount}).");
            }

            CsrVertexMap csr;
            try
            {
                csr = new CsrVertexMap(
                    IndexDomain.UnityVertex,
                    IndexDomain.ControlPoint,
                    entry.UnityVertexCount,
                    entry.ControlPointCount,
                    payload.CsrRowOffsets,
                    payload.CsrControlPointIndices);
            }
            catch (Exception ex)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.InvalidCsr,
                    null,
                    null,
                    $"Embedded mapping CSR validation failed: {ex.Message}");
            }

            var targetDigests = precomputedDigests ?? UnityMeshGeometryDigests.Compute(targetSnapshot);
            if (!string.Equals(entry.OrderedPositionsDigest, targetDigests.OrderedPositionsDigest, StringComparison.Ordinal))
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.PositionsMismatch,
                    null,
                    null,
                    "Target Unity mesh ordered vertex positions digest does not match embedded mapping.");
            }

            if (!string.Equals(entry.CanonicalTriangleIncidenceDigest, targetDigests.CanonicalTriangleIncidenceDigest, StringComparison.Ordinal))
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.TopologyIncidenceMismatch,
                    null,
                    null,
                    "Target Unity mesh canonical triangle incidence digest does not match embedded mapping.");
            }

            // Stage 1: Exact fast path (positions + canonical triangle incidence + UV0 all match).
            if (string.Equals(entry.Uv0Digest ?? string.Empty, targetDigests.Uv0Digest ?? string.Empty, StringComparison.Ordinal))
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.ExactFastPath,
                    csr,
                    hydratedContract,
                    string.Empty);
            }

            // Stage 2: UV0 changed (e.g., UV re-layout or SubMesh regrouping).
            // Fail closed unless EVERY coincident-position class in the target mesh is proven safe.
            var targetClasses = UnityMeshGeometryDigests.FindCoincidentPositionClasses(targetSnapshot);
            if (targetClasses.Count == 0)
            {
                // No coincident positions exist, so ordered position + triangle incidence uniquely identifies every vertex.
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.CoincidentProofFallback,
                    csr,
                    hydratedContract,
                    "UV0 changed; verified safely because mesh has zero coincident-position vertex classes.");
            }

            if (payload.CoincidentClasses == null || payload.CoincidentClasses.Count != targetClasses.Count)
            {
                return new VertexMappingVerificationResult(
                    VertexMappingVerificationStatus.CoincidentClassConflict,
                    null,
                    null,
                    "UV0 changed and embedded mapping lacks matching coincident-vertex equivalence proofs.");
            }

            for (int c = 0; c < targetClasses.Count; c++)
            {
                var proof = payload.CoincidentClasses[c];
                var targetCls = targetClasses[c];
                if (proof == null || !AreEqualIntArrays(proof.UnityVertexIndices, targetCls.UnityVertexIndices))
                {
                    return new VertexMappingVerificationResult(
                        VertexMappingVerificationStatus.CoincidentClassConflict,
                        null,
                        null,
                        $"Coincident vertex class {c} membership diverged after UV0 change.");
                }

                // Re-verify CSR invariance directly from the CSR map rather than trusting the flag blindly.
                bool rowsActuallyIdentical = AllRowsIdentical(csr, targetCls.UnityVertexIndices);
                if (rowsActuallyIdentical)
                {
                    continue;
                }

                // Fail closed if any pair of coincident vertices with differing CSR rows share the same
                // oriented-neighbor incidence signature (e.g., symmetric double triangles or isolated vertices),
                // because swapping them preserves positions, triangle multiset, and per-vertex neighbor lists.
                if (!AreDifferingCsrRowsDistinguishedByIncidence(csr, targetCls) ||
                    string.IsNullOrEmpty(proof.DiscriminatorDigest) ||
                    !string.Equals(proof.DiscriminatorDigest, targetCls.DiscriminatorDigest, StringComparison.Ordinal))
                {
                    return new VertexMappingVerificationResult(
                        VertexMappingVerificationStatus.CoincidentClassConflict,
                        null,
                        null,
                        $"Coincident vertex class {c} maps to distinct FBX control points and cannot be proven swap-free after UV0 change.");
                }
            }

            return new VertexMappingVerificationResult(
                VertexMappingVerificationStatus.CoincidentProofFallback,
                csr,
                hydratedContract,
                "UV0 changed; verified via coincident-vertex equivalence proofs.");
        }

        private static bool AreDifferingCsrRowsDistinguishedByIncidence(CsrVertexMap csr, CoincidentVertexClass cls)
        {
            var indices = cls.UnityVertexIndices;
            var perVertexDigests = cls.PerVertexIncidenceDigests;
            if (indices == null || perVertexDigests == null || indices.Length != perVertexDigests.Length)
            {
                return false;
            }

            for (int i = 0; i < indices.Length; i++)
            {
                for (int j = i + 1; j < indices.Length; j++)
                {
                    if (RowsEqual(csr.GetRow(indices[i]), csr.GetRow(indices[j])))
                    {
                        continue;
                    }

                    // Two coincident vertices with different CSR rows must have distinct incidence signatures;
                    // otherwise swapping them leaves positions, canonical triangles, and neighbor lists unchanged.
                    if (string.Equals(perVertexDigests[i], perVertexDigests[j], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool RowsEqual(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        private static bool AllRowsIdentical(CsrVertexMap csr, int[] vertexIndices)
        {
            if (vertexIndices == null || vertexIndices.Length <= 1)
            {
                return true;
            }

            var first = csr.GetRow(vertexIndices[0]);
            for (int i = 1; i < vertexIndices.Length; i++)
            {
                var current = csr.GetRow(vertexIndices[i]);
                if (current.Length != first.Length)
                {
                    return false;
                }

                for (int j = 0; j < first.Length; j++)
                {
                    if (current[j] != first[j])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool AreEqualIntArrays(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
