using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.EditorAssets.Binding
{
    public enum OriginBindingVerificationStatus : byte
    {
        ExactFastPath = 0,
        CoincidentProofFallback = 1,
        VertexCountMismatch = 2,
        ControlPointCountMismatch = 3,
        PositionsMismatch = 4,
        TopologyIncidenceMismatch = 5,
        CoincidentClassConflict = 6,
        InvalidCsr = 7,
        InvalidSpaceContract = 8
    }

    public sealed class OriginBindingVerificationResult
    {
        public OriginBindingVerificationStatus Status { get; }
        public bool Succeeded =>
            Status == OriginBindingVerificationStatus.ExactFastPath ||
            Status == OriginBindingVerificationStatus.CoincidentProofFallback;
        public CsrVertexMap VertexMap { get; }
        public OriginSpaceContract SpaceContract { get; }
        public string Diagnostic { get; }

        public OriginBindingVerificationResult(
            OriginBindingVerificationStatus status,
            CsrVertexMap vertexMap,
            OriginSpaceContract spaceContract,
            string diagnostic)
        {
            Status = status;
            VertexMap = vertexMap;
            SpaceContract = spaceContract;
            Diagnostic = diagnostic ?? string.Empty;
        }
    }

    /// <summary>
    /// Builds and performs 2-stage fail-closed verification of frozen <see cref="OriginBindingDocument"/> payloads.
    /// Stage 1 (Fast Path): exact vertex count + ordered positions + canonical triangle incidence + UV0 digest.
    /// Stage 2 (UV0/SubMesh Fallback): when UV0 differs, verifies every coincident-position equivalence class proof.
    /// </summary>
    public static class OriginBindingCodec
    {
        public static OriginBindingDocument Build(
            UnityMeshSnapshot originSnapshot,
            MeshCornerMappingResult inverseMapping,
            int originControlPointCount,
            FbxSceneMetadata originSceneMetadata,
            string targetIdentity = null,
            PatchSpaceProvenance storedSpace = null)
        {
            if (originSnapshot == null)
            {
                throw new ArgumentNullException(nameof(originSnapshot));
            }

            if (inverseMapping == null || !inverseMapping.Succeeded)
            {
                throw new ArgumentException("A succeeded MeshCornerMappingResult is required.", nameof(inverseMapping));
            }

            if (inverseMapping.UnityVertexCount != originSnapshot.Vertices.Length)
            {
                throw new ArgumentException("Inverse mapping vertex count does not match origin Unity mesh snapshot.");
            }

            var csr = CsrVertexMapFactory.FromJaggedGroups(
                inverseMapping.VertexControlPointGroups,
                originControlPointCount,
                IndexDomain.UnityVertex,
                IndexDomain.ControlPoint);

            var digests = UnityMeshGeometryDigests.Compute(originSnapshot);
            var contract = BuildSpaceContract(
                storedSpace, originSceneMetadata, inverseMapping.MatchedScale, inverseMapping.MatchedBakeAxisConversion, targetIdentity);

            var coincidentClasses = UnityMeshGeometryDigests.FindCoincidentPositionClasses(originSnapshot);
            var proofs = new List<OriginCoincidentClassProof>(coincidentClasses.Count);
            for (int c = 0; c < coincidentClasses.Count; c++)
            {
                var cls = coincidentClasses[c];
                bool allIdentical = AllRowsIdentical(csr, cls.UnityVertexIndices);
                bool swapDistinguishable = allIdentical || AreDifferingCsrRowsDistinguishedByIncidence(csr, cls);
                proofs.Add(new OriginCoincidentClassProof
                {
                    UnityVertexIndices = (int[])cls.UnityVertexIndices.Clone(),
                    AllRowsShareIdenticalCsr = allIdentical,
                    DiscriminatorDigest = swapDistinguishable ? cls.DiscriminatorDigest : string.Empty
                });
            }

            return new OriginBindingDocument
            {
                BindingSchemaVersion = 1,
                UnityVertexCount = originSnapshot.Vertices.Length,
                OriginControlPointCount = originControlPointCount,
                OrderedPositionsDigest = digests.OrderedPositionsDigest,
                CanonicalTriangleIncidenceDigest = digests.CanonicalTriangleIncidenceDigest,
                Uv0Digest = digests.Uv0Digest,
                CsrRowOffsets = csr.RowOffsets.ToArray(),
                CsrControlPointIndices = csr.TargetIndices.ToArray(),
                FaceCornerIndices = inverseMapping.FirstFbxCorners != null && inverseMapping.FirstFbxCorners.Length > 0
                    ? (int[])inverseMapping.FirstFbxCorners.Clone()
                    : null,
                SpaceContract = contract,
                CoincidentClasses = proofs
            };
        }

        internal static OriginSpaceContract BuildSpaceContract(PatchSpaceProvenance storedSpace,
            FbxSceneMetadata target, float scale, bool bakeAxes, string targetIdentity)
        {
            var effectiveSpace = storedSpace != null
                ? PatchSpaceProvenance.FromMetadata(storedSpace.OriginAxes, storedSpace.OriginRawMetersPerUnit)
                : PatchSpaceProvenance.FromMetadata(target.Axes, target.UnitMeters);

            if (!effectiveSpace.IsValid)
                throw new ArgumentException("Stored patch space requires valid axes and a strictly positive unit scale.");

            if (!UnitySpaceContractMath.TryBuildStoredToUnityMatrix(
                    effectiveSpace.OriginAxes, effectiveSpace.OriginRawMetersPerUnit, target, scale, bakeAxes, out var matrix, out _) ||
                !FbxAffineSpaceTransform.TryCreate(matrix, out _))
                throw new ArgumentException("Unsupported or singular binding coordinate transform.");

            return new OriginSpaceContract
            {
                StoredToUnityMeshLocal = matrix,
                StoredSpace = effectiveSpace,
                TargetIdentity = targetIdentity ?? string.Empty
            };
        }

        public static OriginBindingVerificationResult Verify(
            OriginBindingDocument binding,
            UnityMeshSnapshot targetSnapshot,
            int expectedOriginControlPointCount = -1,
            PatchSpaceProvenance expectedSpace = null)
        {
            if (binding == null)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.InvalidCsr,
                    null,
                    null,
                    "OriginBindingDocument is null.");
            }

            if (targetSnapshot == null)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.VertexCountMismatch,
                    null,
                    null,
                    "Target Unity mesh snapshot is null.");
            }

            var resolvedSpace = binding.SpaceContract?.StoredSpace ?? expectedSpace;
            if (binding.SpaceContract == null ||
                resolvedSpace == null ||
                !resolvedSpace.IsValid ||
                (binding.SpaceContract.StoredSpace != null && expectedSpace != null &&
                 !binding.SpaceContract.StoredSpace.MatchesSpace(expectedSpace)) ||
                !FbxAffineSpaceTransform.TryCreate(binding.SpaceContract.StoredToUnityMeshLocal, out _))
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.InvalidSpaceContract,
                    null,
                    null,
                    "OriginBinding SpaceContract is missing, mismatched, or non-affine/singular.");
            }

            var hydratedContract = new OriginSpaceContract
            {
                TargetIdentity = binding.SpaceContract.TargetIdentity ?? string.Empty,
                StoredToUnityMeshLocal = binding.SpaceContract.StoredToUnityMeshLocal,
                StoredSpace = PatchSpaceProvenance.FromMetadata(resolvedSpace.OriginAxes, resolvedSpace.OriginRawMetersPerUnit)
            };

            if (expectedOriginControlPointCount >= 0 &&
                binding.OriginControlPointCount != expectedOriginControlPointCount)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.ControlPointCountMismatch,
                    null,
                    null,
                    $"OriginBinding control-point count ({binding.OriginControlPointCount}) does not match mesh header ({expectedOriginControlPointCount}).");
            }

            if (binding.UnityVertexCount != targetSnapshot.Vertices.Length)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.VertexCountMismatch,
                    null,
                    null,
                    $"Target mesh vertex count ({targetSnapshot.Vertices.Length}) does not match OriginBinding ({binding.UnityVertexCount}).");
            }

            CsrVertexMap csr;
            try
            {
                csr = new CsrVertexMap(
                    IndexDomain.UnityVertex,
                    IndexDomain.ControlPoint,
                    binding.UnityVertexCount,
                    binding.OriginControlPointCount,
                    binding.CsrRowOffsets,
                    binding.CsrControlPointIndices);
            }
            catch (Exception ex)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.InvalidCsr,
                    null,
                    null,
                    $"OriginBinding CSR validation failed: {ex.Message}");
            }

            var targetDigests = UnityMeshGeometryDigests.Compute(targetSnapshot);
            if (!string.Equals(binding.OrderedPositionsDigest, targetDigests.OrderedPositionsDigest, StringComparison.Ordinal))
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.PositionsMismatch,
                    null,
                    null,
                    "Target Unity mesh ordered vertex positions digest does not match OriginBinding.");
            }

            if (!string.Equals(binding.CanonicalTriangleIncidenceDigest, targetDigests.CanonicalTriangleIncidenceDigest, StringComparison.Ordinal))
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.TopologyIncidenceMismatch,
                    null,
                    null,
                    "Target Unity mesh canonical triangle incidence digest does not match OriginBinding.");
            }

            // Stage 1: Exact fast path (positions + canonical triangle incidence + UV0 all match).
            if (string.Equals(binding.Uv0Digest ?? string.Empty, targetDigests.Uv0Digest ?? string.Empty, StringComparison.Ordinal))
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.ExactFastPath,
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
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.CoincidentProofFallback,
                    csr,
                    hydratedContract,
                    "UV0 changed; verified safely because mesh has zero coincident-position vertex classes.");
            }

            if (binding.CoincidentClasses == null || binding.CoincidentClasses.Count != targetClasses.Count)
            {
                return new OriginBindingVerificationResult(
                    OriginBindingVerificationStatus.CoincidentClassConflict,
                    null,
                    null,
                    "UV0 changed and OriginBinding lacks matching coincident-vertex equivalence proofs.");
            }

            for (int c = 0; c < targetClasses.Count; c++)
            {
                var proof = binding.CoincidentClasses[c];
                var targetCls = targetClasses[c];
                if (proof == null || !AreEqualIntArrays(proof.UnityVertexIndices, targetCls.UnityVertexIndices))
                {
                    return new OriginBindingVerificationResult(
                        OriginBindingVerificationStatus.CoincidentClassConflict,
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
                    return new OriginBindingVerificationResult(
                        OriginBindingVerificationStatus.CoincidentClassConflict,
                        null,
                        null,
                        $"Coincident vertex class {c} maps to distinct FBX control points and cannot be proven swap-free after UV0 change.");
                }
            }

            return new OriginBindingVerificationResult(
                OriginBindingVerificationStatus.CoincidentProofFallback,
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
