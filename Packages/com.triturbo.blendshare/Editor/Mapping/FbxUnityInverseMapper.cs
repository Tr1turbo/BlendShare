using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Recovers exact face-corner and vertex correspondences from a Unity <see cref="Mesh"/> back
    /// to its source FBX <see cref="IFbxMeshGeometry"/> using float32 coordinate matching, reversed
    /// polygon winding, local connected-component polygon-layout DFS, and lazy UV0/blendshape refinement.
    /// Works without requiring <c>ModelImporter</c> settings while accepting optional <see cref="FbxInverseMapHints"/>.
    /// </summary>
    public static class FbxUnityInverseMapper
    {
        private const double k_BoundsRelativeSlack = 1e-4d;
        private const double k_BoundsAbsoluteSlack = 1e-5d;
        private const double k_ApproxBoundsRelativeSlack = 2e-2d;
        private const double k_ApproxBoundsAbsoluteSlack = 2e-3d;
        private const float k_MinRepresentableScale = 1e-7f;
        private const float k_Uv0SqrDistanceTolerance = 1.001e-6f;
        private const float k_CompressedUv0SqrDistanceTolerance = 1e-4f;
        private const float k_BlendShapeDeltaTolerance = 1.2e-5f;
        private const double k_BlendShapeOffsetThreshold = 1e-6d;
        private const double k_BlendShapeFrameWeightPercentTolerance = 2e-2d;
        private const int k_MaxUlpNeighborSteps = 3;
        private const int k_MaxDfsStepsPerComponent = 500000;

        private enum ComponentSolveStatus
        {
            Solved,
            Contradiction,
            BudgetExceeded
        }

        /// <summary>
        /// Maps a Unity mesh snapshot back to the given FBX mesh geometry.
        /// </summary>
        public static MeshCornerMappingResult Map(
            IFbxMeshGeometry fbxMesh,
            UnityMeshSnapshot unityMesh,
            FbxInverseMapHints hints = default)
        {
            if (fbxMesh == null || fbxMesh.ControlPointCount <= 0)
            {
                return MeshCornerMappingResult.Failed(
                    "FBX inverse mapping requires a non-empty FBX mesh.",
                    unityMesh?.Vertices.Length ?? 0);
            }

            if (unityMesh == null || unityMesh.Vertices.Length == 0)
            {
                return MeshCornerMappingResult.Failed("FBX inverse mapping requires a Unity mesh with vertices.");
            }

            var unityVertices = unityMesh.Vertices;
            int unityVertexCount = unityVertices.Length;
            for (int i = 0; i < unityVertexCount; i++)
            {
                var v = unityVertices[i];
                if (!IsFinite(v.x) || !IsFinite(v.y) || !IsFinite(v.z))
                {
                    return MeshCornerMappingResult.Failed(
                        "Unity mesh contains non-finite vertex positions.",
                        unityVertexCount);
                }
            }

            var fbxPoints64 = fbxMesh.GetControlPoints();
            var fbxFaceSizes = fbxMesh.GetFaceSizes();
            var fbxCornerCps = fbxMesh.GetFaceCornerControlPoints();
            int fbxCpCount = fbxPoints64.Length;
            int fbxFaceCount = fbxFaceSizes.Length;
            int fbxCornerCount = fbxCornerCps.Length;

            int[] unityTriangles = unityMesh.Triangles;
            int triangleCount = unityTriangles.Length / 3;

            for (int i = 0; i < triangleCount * 3; i++)
            {
                if ((uint)unityTriangles[i] >= (uint)unityVertexCount)
                {
                    return MeshCornerMappingResult.Failed(
                        $"Unity mesh triangle index {unityTriangles[i]} is out of range [0, {unityVertexCount}).",
                        unityVertexCount);
                }
            }

            // Precompute FBX face starts, corner -> face table, and corners belonging to faces with non-zero 3D area
            // so culled zero-area or collinear outlier faces cannot poison AABB scale seed ratios.
            var rawBase32 = new CoordKey[fbxCpCount];
            for (int i = 0; i < fbxCpCount; i++)
            {
                rawBase32[i] = new CoordKey((float)fbxPoints64[i].x, (float)fbxPoints64[i].y, (float)fbxPoints64[i].z);
            }

            var faceStarts = new int[fbxFaceCount];
            var cornerToFace = new int[fbxCornerCount];
            var nonDegenerateCornersList = new List<int>(fbxCornerCount);
            int cursor = 0;
            for (int f = 0; f < fbxFaceCount; f++)
            {
                int start = cursor;
                faceStarts[f] = start;
                int n = fbxFaceSizes[f];
                for (int k = 0; k < n && cursor < fbxCornerCount; k++, cursor++)
                {
                    cornerToFace[cursor] = f;
                }

                int actualLen = cursor - start;
                if (actualLen >= 3 && HasPositivePolygonArea(fbxPoints64, fbxCornerCps, start, actualLen))
                {
                    for (int cIdx = start; cIdx < cursor; cIdx++)
                    {
                        nonDegenerateCornersList.Add(fbxCornerCps[cIdx]);
                    }
                }
            }

            int[] nonDegenerateCornerCps = nonDegenerateCornersList.Count > 0 ? nonDegenerateCornersList.ToArray() : null;

            // Stage 1 + Stage 2/3 validation: test prioritized (geometryTransform, axisPerm, scale) candidates until one
            // satisfies exact coordinates, polygon winding, consistent polygon layouts, and blendshape transform agreement.
            var candidateTransforms = BuildCandidateTransforms(fbxMesh, hints);

            CoordinateMatch matchedCoords = null;
            List<CornerTriple>[] resolvedOptions = null;
            int matchedDegenerateTriangles = 0;
            string matchedRefinementNote = string.Empty;
            bool matchedIsBlendShapeSensitive = false;
            bool matchedExceededBudget = false;
            bool sawLayoutContradiction = false;

            CoordinateMatch fallbackShapeMismatchCoords = null;
            List<CornerTriple>[] fallbackShapeMismatchOptions = null;
            int fallbackDegenerateTriangles = 0;
            string fallbackRefinementNote = string.Empty;
            bool fallbackExceededBudget = false;

            int matchedTransformIndex = -1;
            for (int tIdx = 0; tIdx < candidateTransforms.Count && matchedCoords == null; tIdx++)
            {
                var transform = candidateTransforms[tIdx];
                var candidates = DiscoverCoordinateMatches(
                    fbxPoints64,
                    nonDegenerateCornerCps,
                    fbxCornerCps,
                    unityVertices,
                    unityTriangles,
                    transform,
                    hints.Scale);

                for (int c = 0; c < candidates.Count; c++)
                {
                    var coord = candidates[c];
                    if (triangleCount == 0)
                    {
                        matchedCoords = coord;
                        matchedTransformIndex = tIdx;
                        resolvedOptions = Array.Empty<List<CornerTriple>>();
                        break;
                    }

                    if (!TryMatchTriangles(
                            fbxFaceSizes,
                            faceStarts,
                            fbxCornerCps,
                            coord.FbxCpClass,
                            coord.ClassCount,
                            coord.UnityVertexClass,
                            coord.UnityVertexCandidateClasses,
                            unityTriangles,
                            triangleCount,
                            coord.Transform.AxisPerm.ReversesWinding,
                            out var rawOptions,
                            out int degenerateCount))
                    {
                        continue;
                    }

                    if (!TryResolveAmbiguousTriangles(
                            fbxMesh,
                            fbxPoints64,
                            rawBase32,
                            fbxFaceSizes,
                            faceStarts,
                            cornerToFace,
                            fbxCornerCps,
                            unityMesh,
                            unityTriangles,
                            triangleCount,
                            rawOptions,
                            coord.Scale,
                            coord.Transform,
                            hints.WeldVertices,
                            hints.SwapUvChannels,
                            k_Uv0SqrDistanceTolerance,
                            k_BlendShapeDeltaTolerance,
                            out var refined,
                            out string refinementNote,
                            out bool isBlendShapeSensitive,
                            out bool exceededLayoutDfsBudget))
                    {
                        sawLayoutContradiction = true;
                        continue;
                    }

                    if (!VerifyResolvedBlendShapesAgree(
                            fbxMesh,
                            fbxPoints64,
                            fbxCornerCps,
                            unityMesh.BlendShapes,
                            unityTriangles,
                            triangleCount,
                            refined,
                            coord.Scale,
                            coord.Transform,
                            k_BlendShapeDeltaTolerance))
                    {
                        if (fallbackShapeMismatchCoords == null)
                        {
                            fallbackShapeMismatchCoords = coord;
                            fallbackShapeMismatchOptions = refined;
                            fallbackDegenerateTriangles = degenerateCount;
                            fallbackRefinementNote = refinementNote;
                            fallbackExceededBudget = exceededLayoutDfsBudget;
                        }

                        continue;
                    }

                    matchedCoords = coord;
                    matchedTransformIndex = tIdx;
                    resolvedOptions = refined;
                    matchedDegenerateTriangles = degenerateCount;
                    matchedRefinementNote = refinementNote;
                    matchedIsBlendShapeSensitive = isBlendShapeSensitive || fallbackShapeMismatchCoords != null;
                    matchedExceededBudget = exceededLayoutDfsBudget;
                    break;
                }
            }

            if (matchedCoords == null && fallbackShapeMismatchCoords != null)
            {
                matchedCoords = fallbackShapeMismatchCoords;
                resolvedOptions = fallbackShapeMismatchOptions;
                matchedDegenerateTriangles = fallbackDegenerateTriangles;
                matchedRefinementNote = fallbackRefinementNote;
                matchedIsBlendShapeSensitive = true;
                matchedExceededBudget = fallbackExceededBudget;
            }
            else if (matchedCoords != null &&
                     !matchedIsBlendShapeSensitive &&
                     matchedTransformIndex >= 0 &&
                     HasEligibleSingleFrameBlendShape(fbxMesh) &&
                     HasAlternateGeometryValidLinearTransform(
                         fbxMesh,
                         fbxPoints64,
                         rawBase32,
                         nonDegenerateCornerCps,
                         fbxFaceSizes,
                         faceStarts,
                         cornerToFace,
                         fbxCornerCps,
                         unityVertices,
                         unityTriangles,
                         triangleCount,
                         candidateTransforms,
                         matchedTransformIndex,
                         matchedCoords,
                         hints))
            {
                // Base geometry (e.g. a planar mesh with out-of-plane GeometryToNodeMatrix) admits multiple distinct
                // linear deformation matrices; mark blendshape-sensitive so geometry-only caches/embeddings never
                // freeze one deformation matrix across targets with different blendshape deltas.
                matchedIsBlendShapeSensitive = true;
            }

            // Stage 1 fallback: quantized vertex tolerance matching for compressed Unity meshes (e.g. MeshCompression.High).
            if (matchedCoords == null)
            {
                CoordinateMatch approxShapeMismatchCoords = null;
                List<CornerTriple>[] approxShapeMismatchOptions = null;
                int approxFallbackDegenerate = 0;
                string approxFallbackNote = string.Empty;
                bool approxFallbackExceededBudget = false;

                for (int tIdx = 0; tIdx < candidateTransforms.Count && matchedCoords == null; tIdx++)
                {
                    var transform = candidateTransforms[tIdx];
                    var approxCandidates = DiscoverApproximateCoordinateMatches(
                        fbxPoints64,
                        nonDegenerateCornerCps,
                        fbxCornerCps,
                        unityVertices,
                        unityTriangles,
                        transform,
                        hints.Scale);

                    for (int c = 0; c < approxCandidates.Count; c++)
                    {
                        var coord = approxCandidates[c];
                        if (triangleCount == 0)
                        {
                            matchedCoords = coord;
                            resolvedOptions = Array.Empty<List<CornerTriple>>();
                            break;
                        }

                        if (!TryMatchTriangles(
                                fbxFaceSizes,
                                faceStarts,
                                fbxCornerCps,
                                coord.FbxCpClass,
                                coord.ClassCount,
                                coord.UnityVertexClass,
                                coord.UnityVertexCandidateClasses,
                                unityTriangles,
                                triangleCount,
                                coord.Transform.AxisPerm.ReversesWinding,
                                out var rawOptions,
                                out int degenerateCount))
                        {
                            continue;
                        }

                        float bsTol = Math.Max(1e-3f, coord.PositionTolerance * 2.5f);
                        if (!TryResolveAmbiguousTriangles(
                                fbxMesh,
                                fbxPoints64,
                                rawBase32,
                                fbxFaceSizes,
                                faceStarts,
                                cornerToFace,
                                fbxCornerCps,
                                unityMesh,
                                unityTriangles,
                                triangleCount,
                                rawOptions,
                                coord.Scale,
                                coord.Transform,
                                hints.WeldVertices,
                                hints.SwapUvChannels,
                                k_CompressedUv0SqrDistanceTolerance,
                                bsTol,
                                out var refined,
                                out string refinementNote,
                                out bool isBlendShapeSensitive,
                                out bool exceededLayoutDfsBudget))
                        {
                            sawLayoutContradiction = true;
                            continue;
                        }

                        if (!VerifyResolvedBlendShapesAgree(
                                fbxMesh,
                                fbxPoints64,
                                fbxCornerCps,
                                unityMesh.BlendShapes,
                                unityTriangles,
                                triangleCount,
                                refined,
                                coord.Scale,
                                coord.Transform,
                                bsTol))
                        {
                            if (approxShapeMismatchCoords == null)
                            {
                                approxShapeMismatchCoords = coord;
                                approxShapeMismatchOptions = refined;
                                approxFallbackDegenerate = degenerateCount;
                                approxFallbackNote = refinementNote;
                                approxFallbackExceededBudget = exceededLayoutDfsBudget;
                            }

                            continue;
                        }

                        matchedCoords = coord;
                        resolvedOptions = refined;
                        matchedDegenerateTriangles = degenerateCount;
                        matchedRefinementNote = string.IsNullOrEmpty(refinementNote)
                            ? "Matched using quantized coordinate tolerance fallback (compressed Unity mesh)."
                            : refinementNote;
                        matchedIsBlendShapeSensitive = isBlendShapeSensitive || approxShapeMismatchCoords != null;
                        matchedExceededBudget = exceededLayoutDfsBudget;
                        break;
                    }
                }

                if (matchedCoords == null && approxShapeMismatchCoords != null)
                {
                    matchedCoords = approxShapeMismatchCoords;
                    resolvedOptions = approxShapeMismatchOptions;
                    matchedDegenerateTriangles = approxFallbackDegenerate;
                    matchedRefinementNote = string.IsNullOrEmpty(approxFallbackNote)
                        ? "Matched using quantized coordinate tolerance fallback (compressed Unity mesh)."
                        : approxFallbackNote;
                    matchedIsBlendShapeSensitive = true;
                    matchedExceededBudget = approxFallbackExceededBudget;
                }
            }

            if (matchedCoords == null)
            {
                string message = sawLayoutContradiction
                    ? "FBX inverse mapping found a polygon-layout contradiction across all candidate coordinate matches."
                    : "Could not find a scale, axis orientation, and polygon winding matching the Unity mesh to the FBX mesh.";
                return MeshCornerMappingResult.Failed(message, unityVertexCount);
            }

            return BuildResult(
                fbxCpCount,
                fbxCornerCps,
                unityVertexCount,
                unityTriangles,
                triangleCount,
                resolvedOptions,
                matchedCoords,
                matchedDegenerateTriangles,
                matchedRefinementNote,
                matchedIsBlendShapeSensitive,
                matchedExceededBudget);
        }

        // ─── Stage 1a: Float32 Coordinate & Scale Matching ──────────────────────────

        private static bool HasAlternateGeometryValidLinearTransform(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            CoordKey[] rawBase32,
            int[] nonDegenerateCornerCps,
            int[] faceSizes,
            int[] faceStarts,
            int[] cornerToFace,
            int[] cornerCps,
            Vector3[] unityVertices,
            int[] unityTriangles,
            int triangleCount,
            List<CandidateTransform> candidateTransforms,
            int matchedTransformIndex,
            CoordinateMatch matchedCoords,
            FbxInverseMapHints hints)
        {
            var matchedLinear = matchedCoords.Transform.BuildRawToUnityLinearMatrix(matchedCoords.Scale);
            var geomOnlyMesh = new UnityMeshSnapshot(unityVertices, unityTriangles);

            for (int tIdx = matchedTransformIndex + 1; tIdx < candidateTransforms.Count; tIdx++)
            {
                var transform = candidateTransforms[tIdx];
                var cands = DiscoverCoordinateMatches(
                    fbxPoints64,
                    nonDegenerateCornerCps,
                    cornerCps,
                    unityVertices,
                    unityTriangles,
                    transform,
                    hints.Scale);

                for (int c = 0; c < cands.Count; c++)
                {
                    var alt = cands[c];
                    var altLinear = alt.Transform.BuildRawToUnityLinearMatrix(alt.Scale);
                    if (altLinear.Approximately(matchedLinear, 1e-7d))
                    {
                        continue;
                    }

                    if (triangleCount == 0)
                    {
                        return true;
                    }

                    if (!TryMatchTriangles(
                            faceSizes,
                            faceStarts,
                            cornerCps,
                            alt.FbxCpClass,
                            alt.ClassCount,
                            alt.UnityVertexClass,
                            alt.UnityVertexCandidateClasses,
                            unityTriangles,
                            triangleCount,
                            alt.Transform.AxisPerm.ReversesWinding,
                            out var rawOptions,
                            out _))
                    {
                        continue;
                    }

                    if (TryResolveAmbiguousTriangles(
                            fbxMesh,
                            fbxPoints64,
                            rawBase32,
                            faceSizes,
                            faceStarts,
                            cornerToFace,
                            cornerCps,
                            geomOnlyMesh,
                            unityTriangles,
                            triangleCount,
                            rawOptions,
                            alt.Scale,
                            alt.Transform,
                            hints.WeldVertices,
                            hints.SwapUvChannels,
                            k_Uv0SqrDistanceTolerance,
                            k_BlendShapeDeltaTolerance,
                            out _,
                            out _,
                            out _,
                            out _))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasPositivePolygonArea(Vector3d[] points64, int[] cornerCps, int start, int length)
        {
            int cp0 = cornerCps[start];
            if ((uint)cp0 >= (uint)points64.Length) return false;
            var p0 = points64[cp0];

            for (int k = 1; k + 1 < length; k++)
            {
                int cp1 = cornerCps[start + k];
                int cp2 = cornerCps[start + k + 1];
                if ((uint)cp1 >= (uint)points64.Length || (uint)cp2 >= (uint)points64.Length) continue;
                var p1 = points64[cp1];
                var p2 = points64[cp2];
                double ux = p1.x - p0.x, uy = p1.y - p0.y, uz = p1.z - p0.z;
                double vx = p2.x - p0.x, vy = p2.y - p0.y, vz = p2.z - p0.z;
                double cx = uy * vz - uz * vy;
                double cy = uz * vx - ux * vz;
                double cz = ux * vy - uy * vx;
                if (cx * cx + cy * cy + cz * cz > 1e-20d)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<CandidateTransform> BuildCandidateTransforms(IFbxMeshGeometry fbxMesh, FbxInverseMapHints hints)
        {
            var geomMatrices = new List<(FbxMatrix4x4 Matrix, bool IsIdentity)>();
            if (fbxMesh is IFbxMesh sceneMesh)
            {
                // Prioritize the target node's own GeometryToNodeMatrix ahead of Identity so planar meshes with
                // out-of-plane geometric scale (e.g. diag(1,1,2)) never falsely prefer Identity first.
                if (!string.IsNullOrEmpty(hints.NodePath) && sceneMesh.Instances != null)
                {
                    string normTarget = FbxNameUtility.NormalizePath(hints.NodePath);
                    for (int i = 0; i < sceneMesh.Instances.Count; i++)
                    {
                        var inst = sceneMesh.Instances[i];
                        if (inst != null && string.Equals(FbxNameUtility.NormalizePath(inst.Path), normTarget, StringComparison.Ordinal))
                        {
                            TryAddDistinctGeometryMatrix(inst, geomMatrices);
                            break;
                        }
                    }
                }
                else if (sceneMesh.PrimaryNode != null && (sceneMesh.Instances == null || sceneMesh.Instances.Count <= 1))
                {
                    TryAddDistinctGeometryMatrix(sceneMesh.PrimaryNode, geomMatrices);
                }

                TryAddIdentityGeometryMatrix(geomMatrices);
                TryAddDistinctGeometryMatrix(sceneMesh.PrimaryNode, geomMatrices);
                if (sceneMesh.Instances != null)
                {
                    for (int i = 0; i < sceneMesh.Instances.Count; i++)
                    {
                        TryAddDistinctGeometryMatrix(sceneMesh.Instances[i], geomMatrices);
                    }
                }
            }
            else
            {
                geomMatrices.Add((FbxMatrix4x4.Identity, true));
            }

            FbxCoordinateAxes? sceneAxes = hints.SceneAxes;
            if (!sceneAxes.HasValue &&
                fbxMesh is Triturbo.BlendShare.Fbx.Ufbx.UfbxElement ufbxElement &&
                ufbxElement.OwnerScene != null)
            {
                sceneAxes = ufbxElement.OwnerScene.Metadata.Axes;
            }

            SignedAxisPerm3x3 sceneBakedPerm = default;
            bool hasSceneBakedPerm = sceneAxes.HasValue &&
                                     TryBuildSceneBakedAxisPerm(sceneAxes.Value, out sceneBakedPerm) &&
                                     (sceneBakedPerm.ReversesWinding || hints.BakeAxisConversion == true);
            SignedAxisPerm3x3 primaryBakedPerm = hasSceneBakedPerm ? sceneBakedPerm : SignedAxisPerm3x3.MayaBaked;

            bool firstBake = hints.BakeAxisConversion ?? false;
            var firstPerm = firstBake ? primaryBakedPerm : SignedAxisPerm3x3.MayaUnbaked;
            var secondPerm = firstBake ? SignedAxisPerm3x3.MayaUnbaked : primaryBakedPerm;

            var list = new List<CandidateTransform>(geomMatrices.Count * 3);
            for (int g = 0; g < geomMatrices.Count; g++)
            {
                var (mat, isId) = geomMatrices[g];
                AddUniqueCandidateTransform(list, new CandidateTransform(mat, isId, firstPerm, firstBake));
                AddUniqueCandidateTransform(list, new CandidateTransform(mat, isId, secondPerm, !firstBake));
                if (hasSceneBakedPerm && !sceneBakedPerm.Equals(SignedAxisPerm3x3.MayaBaked))
                {
                    AddUniqueCandidateTransform(list, new CandidateTransform(mat, isId, SignedAxisPerm3x3.MayaBaked, true));
                }
            }

            return list;
        }

        private static void AddUniqueCandidateTransform(List<CandidateTransform> list, CandidateTransform candidate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsIdentityGeometry == candidate.IsIdentityGeometry &&
                    list[i].AxisPerm.Equals(candidate.AxisPerm) &&
                    list[i].GeometryToNode.Approximately(candidate.GeometryToNode, 1e-9d))
                {
                    return;
                }
            }

            list.Add(candidate);
        }

        private static void TryAddIdentityGeometryMatrix(List<(FbxMatrix4x4 Matrix, bool IsIdentity)> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsIdentity) return;
            }

            list.Add((FbxMatrix4x4.Identity, true));
        }

        private static void TryAddDistinctGeometryMatrix(
            IFbxNode node,
            List<(FbxMatrix4x4 Matrix, bool IsIdentity)> list)
        {
            if (node == null) return;
            var m = node.GeometryToNodeMatrix;
            if (m.Approximately(FbxMatrix4x4.Identity, 1e-9d))
            {
                TryAddIdentityGeometryMatrix(list);
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Matrix.Approximately(m, 1e-9d)) return;
            }

            list.Add((m, false));
        }

        private static List<CoordinateMatch> DiscoverCoordinateMatches(
            Vector3d[] fbxPoints64,
            int[] nonDegenerateCornerCps,
            int[] fbxCornerCps,
            Vector3[] unityVertices,
            int[] unityTriangles,
            CandidateTransform transform,
            float? scaleHint)
        {
            int fbxCpCount = fbxPoints64.Length;
            int unityVertexCount = unityVertices.Length;

            var fbxAxis32 = new Float3[fbxCpCount];
            var fbxValid = new bool[fbxCpCount];
            for (int i = 0; i < fbxCpCount; i++)
            {
                var p = transform.ApplyPreScale32(fbxPoints64[i]);
                if (!IsFinite(p.X) || !IsFinite(p.Y) || !IsFinite(p.Z))
                {
                    continue;
                }

                fbxValid[i] = true;
                fbxAxis32[i] = p;
            }

            // Compute extents over non-degenerate face points, all face points, and all points for scale seeds.
            ComputeExtents(fbxAxis32, fbxValid, nonDegenerateCornerCps, out double survAx, out double survAy, out double survAz);
            ComputeExtents(fbxAxis32, fbxValid, fbxCornerCps, out double usedAx, out double usedAy, out double usedAz);
            ComputeExtents(fbxAxis32, fbxValid, null, out double allAx, out double allAy, out double allAz);
            ComputeUnityExtents(unityVertices, unityTriangles, out double usedBx, out double usedBy, out double usedBz);
            ComputeUnityExtents(unityVertices, null, out double allBx, out double allBy, out double allBz);

            var seedList = new List<float>(64);
            var seenScaleBits = new HashSet<int>();

            if (scaleHint.HasValue && scaleHint.Value > k_MinRepresentableScale && IsFinite(scaleHint.Value))
            {
                AddScaleWithNeighbors(scaleHint.Value, seedList, seenScaleBits);
            }

            if (survAx > 0d) AddScaleWithNeighbors((float)(usedBx / survAx), seedList, seenScaleBits);
            if (survAy > 0d) AddScaleWithNeighbors((float)(usedBy / survAy), seedList, seenScaleBits);
            if (survAz > 0d) AddScaleWithNeighbors((float)(usedBz / survAz), seedList, seenScaleBits);
            if (usedAx > 0d) AddScaleWithNeighbors((float)(usedBx / usedAx), seedList, seenScaleBits);
            if (usedAy > 0d) AddScaleWithNeighbors((float)(usedBy / usedAy), seedList, seenScaleBits);
            if (usedAz > 0d) AddScaleWithNeighbors((float)(usedBz / usedAz), seedList, seenScaleBits);
            if (allAx > 0d) AddScaleWithNeighbors((float)(allBx / allAx), seedList, seenScaleBits);
            if (allAy > 0d) AddScaleWithNeighbors((float)(allBy / allAy), seedList, seenScaleBits);
            if (allAz > 0d) AddScaleWithNeighbors((float)(allBz / allAz), seedList, seenScaleBits);

            // Always include canonical meter (1.0) and centimeter (0.01) seeds even when outlier culled faces skew AABB ratios.
            AddScaleWithNeighbors(1f, seedList, seenScaleBits);
            AddScaleWithNeighbors(0.01f, seedList, seenScaleBits);

            var results = new List<CoordinateMatch>();
            var classByKey = new Dictionary<CoordKey, int>(fbxCpCount);

            for (int s = 0; s < seedList.Count; s++)
            {
                float scale = seedList[s];
                if (scale <= k_MinRepresentableScale || !IsFinite(scale))
                {
                    continue;
                }

                // Relative slack accounts for float32 endpoint rounding (e.g., 10000f * 0.01f == 100f in float32
                // while double computes 99.99999776); exact float32 coordinate lookup below enforces precision.
                if (allAx * scale * (1d + k_BoundsRelativeSlack) + k_BoundsAbsoluteSlack < allBx ||
                    allAy * scale * (1d + k_BoundsRelativeSlack) + k_BoundsAbsoluteSlack < allBy ||
                    allAz * scale * (1d + k_BoundsRelativeSlack) + k_BoundsAbsoluteSlack < allBz)
                {
                    continue;
                }

                classByKey.Clear();
                var fbxCpClass = new int[fbxCpCount];
                int nextClass = 0;
                for (int i = 0; i < fbxCpCount; i++)
                {
                    if (!fbxValid[i])
                    {
                        fbxCpClass[i] = -1;
                        continue;
                    }

                    var p = fbxAxis32[i];
                    var key = new CoordKey(p.X * scale, p.Y * scale, p.Z * scale);
                    if (!classByKey.TryGetValue(key, out int cls))
                    {
                        cls = nextClass++;
                        classByKey.Add(key, cls);
                    }

                    fbxCpClass[i] = cls;
                }

                var unityVertexClass = new int[unityVertexCount];
                bool allMatched = true;
                for (int v = 0; v < unityVertexCount; v++)
                {
                    var u = unityVertices[v];
                    var key = new CoordKey(u.x, u.y, u.z);
                    if (!classByKey.TryGetValue(key, out int cls))
                    {
                        allMatched = false;
                        break;
                    }

                    unityVertexClass[v] = cls;
                }

                if (!allMatched)
                {
                    continue;
                }

                if (!ContainsEquivalentPartition(results, fbxCpClass, unityVertexClass, null))
                {
                    results.Add(new CoordinateMatch(scale, transform, nextClass, fbxCpClass, unityVertexClass, null, isApproximate: false, positionTolerance: 0f));
                }
            }

            return results;
        }

        private static List<CoordinateMatch> DiscoverApproximateCoordinateMatches(
            Vector3d[] fbxPoints64,
            int[] nonDegenerateCornerCps,
            int[] fbxCornerCps,
            Vector3[] unityVertices,
            int[] unityTriangles,
            CandidateTransform transform,
            float? scaleHint)
        {
            int fbxCpCount = fbxPoints64.Length;
            int unityVertexCount = unityVertices.Length;

            var fbxAxis32 = new Float3[fbxCpCount];
            var fbxValid = new bool[fbxCpCount];
            for (int i = 0; i < fbxCpCount; i++)
            {
                var p = transform.ApplyPreScale32(fbxPoints64[i]);
                if (!IsFinite(p.X) || !IsFinite(p.Y) || !IsFinite(p.Z))
                {
                    continue;
                }

                fbxValid[i] = true;
                fbxAxis32[i] = p;
            }

            ComputeExtents(fbxAxis32, fbxValid, nonDegenerateCornerCps, out double survAx, out double survAy, out double survAz);
            ComputeExtents(fbxAxis32, fbxValid, fbxCornerCps, out double usedAx, out double usedAy, out double usedAz);
            ComputeExtents(fbxAxis32, fbxValid, null, out double allAx, out double allAy, out double allAz);
            ComputeUnityExtents(unityVertices, unityTriangles, out double usedBx, out double usedBy, out double usedBz);
            ComputeUnityExtents(unityVertices, null, out double allBx, out double allBy, out double allBz);

            var scaleList = new List<float>(16);
            var seenScaleBits = new HashSet<int>();

            void AddApproxScale(float candidate)
            {
                if (candidate <= k_MinRepresentableScale || !IsFinite(candidate)) return;
                if (Math.Abs(candidate - 0.01f) <= 0.01f * 1e-2f) AddUniqueScale(0.01f, scaleList, seenScaleBits);
                if (Math.Abs(candidate - 1f) <= 1e-2f) AddUniqueScale(1f, scaleList, seenScaleBits);
                AddUniqueScale(candidate, scaleList, seenScaleBits);
            }

            if (scaleHint.HasValue) AddApproxScale(scaleHint.Value);
            if (survAx > 1e-5d && usedBx > 1e-5d) AddApproxScale((float)(usedBx / survAx));
            if (survAy > 1e-5d && usedBy > 1e-5d) AddApproxScale((float)(usedBy / survAy));
            if (survAz > 1e-5d && usedBz > 1e-5d) AddApproxScale((float)(usedBz / survAz));
            if (usedAx > 1e-5d && usedBx > 1e-5d) AddApproxScale((float)(usedBx / usedAx));
            if (usedAy > 1e-5d && usedBy > 1e-5d) AddApproxScale((float)(usedBy / usedAy));
            if (usedAz > 1e-5d && usedBz > 1e-5d) AddApproxScale((float)(usedBz / usedAz));
            AddUniqueScale(1f, scaleList, seenScaleBits);
            AddUniqueScale(0.01f, scaleList, seenScaleBits);

            double maxUnityExtent = Math.Max(allBx, Math.Max(allBy, allBz));
            float posTol = Math.Max(2.5e-4f, (float)(maxUnityExtent * 4e-3d));
            float posTolSqr = posTol * posTol;

            var results = new List<CoordinateMatch>();
            var clusterCenters = new List<Vector3>(fbxCpCount);
            var clusterGrid = new Dictionary<SpatialCellKey, List<int>>(fbxCpCount);
            var lookupGrid = new Dictionary<SpatialCellKey, List<int>>(fbxCpCount);
            var candScratch = new List<int>(4);

            for (int s = 0; s < scaleList.Count; s++)
            {
                float scale = scaleList[s];
                if (allAx * scale * (1d + k_ApproxBoundsRelativeSlack) + k_ApproxBoundsAbsoluteSlack < allBx ||
                    allAy * scale * (1d + k_ApproxBoundsRelativeSlack) + k_ApproxBoundsAbsoluteSlack < allBy ||
                    allAz * scale * (1d + k_ApproxBoundsRelativeSlack) + k_ApproxBoundsAbsoluteSlack < allBz)
                {
                    continue;
                }

                clusterCenters.Clear();
                clusterGrid.Clear();
                lookupGrid.Clear();

                float clusterTol = Math.Max(1e-6f, 1e-5f * scale);
                float clusterTolSqr = clusterTol * clusterTol;
                float invClusterCell = 1f / clusterTol;
                var fbxCpClass = new int[fbxCpCount];

                for (int i = 0; i < fbxCpCount; i++)
                {
                    if (!fbxValid[i])
                    {
                        fbxCpClass[i] = -1;
                        continue;
                    }

                    var p = new Vector3(fbxAxis32[i].X * scale, fbxAxis32[i].Y * scale, fbxAxis32[i].Z * scale);
                    var baseCell = SpatialCellKey.FromPoint(p, invClusterCell);
                    int matchedCluster = -1;

                    for (int dx = -1; dx <= 1 && matchedCluster < 0; dx++)
                    {
                        for (int dy = -1; dy <= 1 && matchedCluster < 0; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                var key = new SpatialCellKey(baseCell.X + dx, baseCell.Y + dy, baseCell.Z + dz);
                                if (!clusterGrid.TryGetValue(key, out var bucket)) continue;
                                for (int b = 0; b < bucket.Count; b++)
                                {
                                    int cIdx = bucket[b];
                                    if ((clusterCenters[cIdx] - p).sqrMagnitude <= clusterTolSqr)
                                        {
                                        matchedCluster = cIdx;
                                        break;
                                    }
                                }

                                if (matchedCluster >= 0) break;
                            }
                        }
                    }

                    if (matchedCluster < 0)
                    {
                        matchedCluster = clusterCenters.Count;
                        clusterCenters.Add(p);
                        if (!clusterGrid.TryGetValue(baseCell, out var ownBucket))
                        {
                            ownBucket = new List<int>(2);
                            clusterGrid.Add(baseCell, ownBucket);
                        }

                        ownBucket.Add(matchedCluster);
                    }

                    fbxCpClass[i] = matchedCluster;
                }

                if (clusterCenters.Count == 0)
                {
                    continue;
                }

                float invPosCell = 1f / posTol;
                for (int cIdx = 0; cIdx < clusterCenters.Count; cIdx++)
                {
                    var key = SpatialCellKey.FromPoint(clusterCenters[cIdx], invPosCell);
                    if (!lookupGrid.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>(2);
                        lookupGrid.Add(key, bucket);
                    }

                    bucket.Add(cIdx);
                }

                var unityVertexClass = new int[unityVertexCount];
                var unityCandidateClasses = new int[unityVertexCount][];
                bool allMatched = true;
                for (int v = 0; v < unityVertexCount; v++)
                {
                    var u = unityVertices[v];
                    var baseCell = SpatialCellKey.FromPoint(u, invPosCell);
                    candScratch.Clear();
                    int nearestCluster = -1;
                    float nearestDistSqr = float.PositiveInfinity;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                var key = new SpatialCellKey(baseCell.X + dx, baseCell.Y + dy, baseCell.Z + dz);
                                if (!lookupGrid.TryGetValue(key, out var bucket)) continue;
                                for (int b = 0; b < bucket.Count; b++)
                                {
                                    int cIdx = bucket[b];
                                    float dSqr = (clusterCenters[cIdx] - u).sqrMagnitude;
                                    if (dSqr <= posTolSqr)
                                    {
                                        candScratch.Add(cIdx);
                                        if (dSqr < nearestDistSqr)
                                        {
                                            nearestDistSqr = dSqr;
                                            nearestCluster = cIdx;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (candScratch.Count == 0)
                    {
                        allMatched = false;
                        break;
                    }

                    candScratch.Sort();
                    unityVertexClass[v] = nearestCluster >= 0 ? nearestCluster : candScratch[0];
                    unityCandidateClasses[v] = candScratch.ToArray();
                }

                if (!allMatched)
                {
                    continue;
                }

                if (!ContainsEquivalentPartition(results, fbxCpClass, unityVertexClass, unityCandidateClasses))
                {
                    results.Add(new CoordinateMatch(
                        scale,
                        transform,
                        clusterCenters.Count,
                        fbxCpClass,
                        unityVertexClass,
                        unityCandidateClasses,
                        isApproximate: true,
                        positionTolerance: posTol));
                }
            }

            return results;
        }

        private static void AddScaleWithNeighbors(float center, List<float> list, HashSet<int> seenBits)
        {
            if (center <= k_MinRepresentableScale || !IsFinite(center))
            {
                return;
            }

            AddUniqueScale(center, list, seenBits);
            float lo = center;
            float hi = center;
            for (int step = 0; step < k_MaxUlpNeighborSteps; step++)
            {
                lo = NextDownPositive(lo);
                hi = NextUpPositive(hi);
                AddUniqueScale(lo, list, seenBits);
                AddUniqueScale(hi, list, seenBits);
            }
        }

        private static void AddUniqueScale(float value, List<float> list, HashSet<int> seenBits)
        {
            if (value <= k_MinRepresentableScale || !IsFinite(value))
            {
                return;
            }

            int bits = BitConverter.SingleToInt32Bits(value);
            if (seenBits.Add(bits))
            {
                list.Add(value);
            }
        }

        private static float NextDownPositive(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            return bits > 1 ? BitConverter.Int32BitsToSingle(bits - 1) : 0f;
        }

        private static float NextUpPositive(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            return bits < 0x7F7FFFFF ? BitConverter.Int32BitsToSingle(bits + 1) : float.PositiveInfinity;
        }

        private static void ComputeExtents(
            Float3[] points,
            bool[] valid,
            int[] usedCps,
            out double ex,
            out double ey,
            out double ez)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            bool any = false;

            if (usedCps != null && usedCps.Length > 0)
            {
                for (int i = 0; i < usedCps.Length; i++)
                {
                    int cp = usedCps[i];
                    if ((uint)cp >= (uint)points.Length || !valid[cp]) continue;
                    var p = points[cp];
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                    if (p.Z > maxZ) maxZ = p.Z;
                    any = true;
                }
            }
            else
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if (!valid[i]) continue;
                    var p = points[i];
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                    if (p.Z > maxZ) maxZ = p.Z;
                    any = true;
                }
            }

            if (!any)
            {
                ex = ey = ez = 0d;
                return;
            }

            ex = maxX - minX;
            ey = maxY - minY;
            ez = maxZ - minZ;
        }

        private static void ComputeUnityExtents(
            Vector3[] vertices,
            int[] usedIndices,
            out double ex,
            out double ey,
            out double ez)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            bool any = false;

            if (usedIndices != null && usedIndices.Length > 0)
            {
                for (int i = 0; i < usedIndices.Length; i++)
                {
                    int v = usedIndices[i];
                    if ((uint)v >= (uint)vertices.Length) continue;
                    var p = vertices[v];
                    if (p.x < minX) minX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.z < minZ) minZ = p.z;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y > maxY) maxY = p.y;
                    if (p.z > maxZ) maxZ = p.z;
                    any = true;
                }
            }
            else
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    var p = vertices[i];
                    if (p.x < minX) minX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.z < minZ) minZ = p.z;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y > maxY) maxY = p.y;
                    if (p.z > maxZ) maxZ = p.z;
                    any = true;
                }
            }

            if (!any)
            {
                ex = ey = ez = 0d;
                return;
            }

            ex = maxX - minX;
            ey = maxY - minY;
            ez = maxZ - minZ;
        }

        private static bool ContainsEquivalentPartition(
            List<CoordinateMatch> existing,
            int[] fbxCpClass,
            int[] unityVertexClass,
            int[][] unityCandidateClasses)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                var other = existing[i];
                if (ArraysEqual(other.FbxCpClass, fbxCpClass) &&
                    ArraysEqual(other.UnityVertexClass, unityVertexClass) &&
                    JaggedArraysEqual(other.UnityVertexCandidateClasses, unityCandidateClasses))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool JaggedArraysEqual(int[][] a, int[][] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!ArraysEqual(a[i], b[i])) return false;
            }

            return true;
        }

        private static bool ArraysEqual(int[] a, int[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        // ─── Stage 1b: Polygon & Winding Corner Matching ───────────────────────────

        private static bool TryMatchTriangles(
            int[] faceSizes,
            int[] faceStarts,
            int[] cornerCps,
            int[] fbxCpClass,
            int classCount,
            int[] unityVertexClass,
            int[][] unityVertexCandidateClasses,
            int[] unityTriangles,
            int triangleCount,
            bool reversesWinding,
            out List<CornerTriple>[] options,
            out int degenerateCount)
        {
            int faceCount = faceSizes.Length;
            int cornerCount = cornerCps.Length;
            var cornerClasses = new int[cornerCount];
            var classToFaces = new List<int>[classCount];

            for (int f = 0; f < faceCount; f++)
            {
                int lo = faceStarts[f];
                int n = faceSizes[f];
                for (int k = 0; k < n; k++)
                {
                    int raw = lo + k;
                    int cp = cornerCps[raw];
                    int cls = (uint)cp < (uint)fbxCpClass.Length ? fbxCpClass[cp] : -1;
                    cornerClasses[raw] = cls;
                    if (cls < 0) continue;

                    var list = classToFaces[cls];
                    if (list == null)
                    {
                        list = new List<int>(4);
                        classToFaces[cls] = list;
                    }

                    if (list.Count == 0 || list[list.Count - 1] != f)
                    {
                        list.Add(f);
                    }
                }
            }

            options = new List<CornerTriple>[triangleCount];
            degenerateCount = 0;
            var markA = new int[faceCount];
            var markB = new int[faceCount];
            var markC = new int[faceCount];
            int stamp = 0;

            var candidateFacesA = new List<int>(8);
            var slotsA = new List<int>(4);
            var slotsB = new List<int>(4);
            var slotsC = new List<int>(4);
            var singleA = new int[1];
            var singleB = new int[1];
            var singleC = new int[1];

            for (int t = 0; t < triangleCount; t++)
            {
                int v0 = unityTriangles[t * 3];
                int v1 = unityTriangles[t * 3 + 1];
                int v2 = unityTriangles[t * 3 + 2];

                // Truly index-degenerate triangles have no 3 distinct corners.
                if (v0 == v1 || v1 == v2 || v2 == v0)
                {
                    degenerateCount++;
                    options[t] = new List<CornerTriple>(0);
                    continue;
                }

                int ca = unityVertexClass[v0];
                int cb = unityVertexClass[v1];
                int cc = unityVertexClass[v2];
                // Distinct Unity vertices at coincident positions still search for 3 distinct FBX corner slots
                // so UV0 and BlendShape refinement can resolve them (matching inverse.py).
                bool coincidentPositions = ca == cb || cb == cc || cc == ca;
                if (coincidentPositions)
                {
                    degenerateCount++;
                }

                int[] candsA;
                int[] candsB;
                int[] candsC;
                if (unityVertexCandidateClasses != null)
                {
                    candsA = unityVertexCandidateClasses[v0];
                    candsB = unityVertexCandidateClasses[v1];
                    candsC = unityVertexCandidateClasses[v2];
                }
                else
                {
                    singleA[0] = ca;
                    singleB[0] = cb;
                    singleC[0] = cc;
                    candsA = singleA;
                    candsB = singleB;
                    candsC = singleC;
                }

                stamp++;
                candidateFacesA.Clear();
                for (int a = 0; a < candsA.Length; a++)
                {
                    var faces = classToFaces[candsA[a]];
                    if (faces == null) continue;
                    for (int i = 0; i < faces.Count; i++)
                    {
                        int f = faces[i];
                        if (markA[f] != stamp)
                        {
                            markA[f] = stamp;
                            candidateFacesA.Add(f);
                        }
                    }
                }

                if (candidateFacesA.Count == 0)
                {
                    return false;
                }

                bool anyB = false;
                for (int b = 0; b < candsB.Length; b++)
                {
                    var faces = classToFaces[candsB[b]];
                    if (faces == null) continue;
                    anyB = true;
                    for (int i = 0; i < faces.Count; i++) markB[faces[i]] = stamp;
                }

                bool anyC = false;
                for (int c = 0; c < candsC.Length; c++)
                {
                    var faces = classToFaces[candsC[c]];
                    if (faces == null) continue;
                    anyC = true;
                    for (int i = 0; i < faces.Count; i++) markC[faces[i]] = stamp;
                }

                if (!anyB || !anyC)
                {
                    return false;
                }

                var triOpts = new List<CornerTriple>(2);
                for (int i = 0; i < candidateFacesA.Count; i++)
                {
                    int f = candidateFacesA[i];
                    if (markB[f] != stamp || markC[f] != stamp) continue;

                    int lo = faceStarts[f];
                    int n = faceSizes[f];
                    slotsA.Clear();
                    slotsB.Clear();
                    slotsC.Clear();
                    for (int k = 0; k < n; k++)
                    {
                        int cls = cornerClasses[lo + k];
                        if (ContainsClass(candsA, cls)) slotsA.Add(k);
                        if (ContainsClass(candsB, cls)) slotsB.Add(k);
                        if (ContainsClass(candsC, cls)) slotsC.Add(k);
                    }

                    for (int ia = 0; ia < slotsA.Count; ia++)
                    {
                        int u = slotsA[ia];
                        for (int ib = 0; ib < slotsB.Count; ib++)
                        {
                            int v = slotsB[ib];
                            if (v == u) continue;
                            for (int ic = 0; ic < slotsC.Count; ic++)
                            {
                                int w = slotsC[ic];
                                if (w == u || w == v) continue;

                                int d1 = reversesWinding ? Mod(u - v, n) : Mod(v - u, n);
                                int d2 = reversesWinding ? Mod(v - w, n) : Mod(w - v, n);
                                int d3 = reversesWinding ? Mod(w - u, n) : Mod(u - w, n);
                                if (d1 + d2 + d3 == n)
                                {
                                    triOpts.Add(new CornerTriple(lo + u, lo + v, lo + w));
                                }
                            }
                        }
                    }
                }

                if (triOpts.Count == 0)
                {
                    return false;
                }

                options[t] = triOpts;
            }

            return true;
        }

        private static bool ContainsClass(int[] classes, int target)
        {
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i] == target) return true;
            }

            return false;
        }

        private static int Mod(int value, int modulus)
        {
            int r = value % modulus;
            return r < 0 ? r + modulus : r;
        }

        /// <summary>
        /// Returns true if <paramref name="fbxMesh"/> has at least one eligible single- or multi-frame blendshape
        /// channel that Stage 3b can evaluate. Shared with cache identity checks so sensitivity and keys never drift.
        /// </summary>
        public static bool HasEligibleSingleFrameBlendShape(IFbxMeshGeometry fbxMesh)
        {
            if (fbxMesh == null || fbxMesh.BlendDeformers == null || fbxMesh.BlendDeformers.Count == 0)
            {
                return false;
            }

            foreach (var deformer in fbxMesh.BlendDeformers)
            {
                if (deformer == null || deformer.Channels == null) continue;
                foreach (var channel in deformer.Channels)
                {
                    if (IsEligibleBlendChannel(channel))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsEligibleBlendChannel(IFbxBlendChannel channel)
        {
            if (channel == null ||
                string.IsNullOrEmpty(channel.Name) ||
                channel.Keyframes == null ||
                channel.Keyframes.Count == 0)
            {
                return false;
            }

            bool anyValid = false;
            for (int i = 0; i < channel.Keyframes.Count; i++)
            {
                var keyframe = channel.Keyframes[i];
                if (keyframe?.Shape != null &&
                    !double.IsNaN(keyframe.TargetWeight) &&
                    !double.IsInfinity(keyframe.TargetWeight) &&
                    Math.Abs(keyframe.TargetWeight) > 1e-9d)
                {
                    anyValid = true;
                }
            }

            return anyValid;
        }

        // ─── Stage 2 & 3: Connected-Component Layout DFS + Lazy UV0 / BlendShape ──

        private static bool TryResolveAmbiguousTriangles(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            CoordKey[] fbxBase32,
            int[] faceSizes,
            int[] faceStarts,
            int[] cornerToFace,
            int[] cornerCps,
            UnityMeshSnapshot unityMesh,
            int[] unityTriangles,
            int triangleCount,
            List<CornerTriple>[] rawOptions,
            float scale,
            CandidateTransform transform,
            bool? weldVerticesHint,
            bool? swapUvHint,
            float uvSqrTolerance,
            float blendShapeDeltaTolerance,
            out List<CornerTriple>[] output,
            out string refinementNote,
            out bool isBlendShapeSensitive,
            out bool exceededLayoutDfsBudget)
        {
            refinementNote = string.Empty;
            isBlendShapeSensitive = false;
            exceededLayoutDfsBudget = false;
            bool allowDistinctCpWeld = weldVerticesHint != false;
            bool reversesWinding = transform.AxisPerm.ReversesWinding;

            var ambiguousTriangles = new List<int>();
            var dirtyFaces = new HashSet<int>();
            for (int t = 0; t < triangleCount; t++)
            {
                if (rawOptions[t] != null && rawOptions[t].Count > 1)
                {
                    ambiguousTriangles.Add(t);
                    var opts = rawOptions[t];
                    for (int j = 0; j < opts.Count; j++)
                    {
                        dirtyFaces.Add(cornerToFace[opts[j].A]);
                    }
                }
            }

            // Validate singleton-only FBX faces too so two Unity triangles can never silently map onto the same
            // unique FBX 3-gon (e.g. under approximate nearest-cluster fallback).
            var singletonUsedByFace = new Dictionary<int, HashSet<SortedCornerTriple>>();
            for (int t = 0; t < triangleCount; t++)
            {
                var opts = rawOptions[t];
                if (opts == null || opts.Count != 1) continue;
                int f = cornerToFace[opts[0].A];
                if (dirtyFaces.Contains(f)) continue;

                if (!singletonUsedByFace.TryGetValue(f, out var set))
                {
                    set = new HashSet<SortedCornerTriple>();
                    singletonUsedByFace.Add(f, set);
                }

                var sorted = SortedCornerTriple.Create(opts[0].A, opts[0].B, opts[0].C);
                if (!set.Add(sorted))
                {
                    output = null;
                    return false;
                }
            }

            foreach (var kv in singletonUsedByFace)
            {
                int f = kv.Key;
                var layoutSet = ComputeFaceLayouts(
                    faceStarts[f],
                    faceSizes[f],
                    fbxBase32,
                    cornerCps,
                    allowDistinctCpWeld,
                    reversesWinding);
                if (!layoutSet.MatchesExact(kv.Value))
                {
                    output = null;
                    return false;
                }
            }

            if (ambiguousTriangles.Count == 0)
            {
                output = rawOptions;
                return true;
            }

            var candidatesByTri = new Dictionary<int, List<CornerTriple>>(ambiguousTriangles.Count * 2);
            for (int t = 0; t < triangleCount; t++)
            {
                var opts = rawOptions[t];
                if (opts == null || opts.Count == 0) continue;
                if (opts.Count > 1 || dirtyFaces.Contains(cornerToFace[opts[0].A]))
                {
                    candidatesByTri[t] = opts;
                }
            }

            var faceVariants = new Dictionary<int, FaceLayoutSet>(dirtyFaces.Count);
            foreach (int f in dirtyFaces)
            {
                faceVariants[f] = ComputeFaceLayouts(
                    faceStarts[f],
                    faceSizes[f],
                    fbxBase32,
                    cornerCps,
                    allowDistinctCpWeld,
                    reversesWinding);
            }

            // Partition dirty faces into independent connected components via Union-Find.
            var ufParent = new Dictionary<int, int>(dirtyFaces.Count);
            foreach (int f in dirtyFaces) ufParent[f] = f;

            int Find(int f)
            {
                while (ufParent[f] != f)
                {
                    ufParent[f] = ufParent[ufParent[f]];
                    f = ufParent[f];
                }

                return f;
            }

            foreach (var pair in candidatesByTri)
            {
                var opts = pair.Value;
                int root0 = Find(cornerToFace[opts[0].A]);
                for (int i = 1; i < opts.Count; i++)
                {
                    int rootI = Find(cornerToFace[opts[i].A]);
                    if (rootI != root0)
                    {
                        ufParent[rootI] = root0;
                    }
                }
            }

            var componentTriangles = new Dictionary<int, List<int>>();
            foreach (var pair in candidatesByTri)
            {
                int root = Find(cornerToFace[pair.Value[0].A]);
                if (!componentTriangles.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    componentTriangles.Add(root, list);
                }

                list.Add(pair.Key);
            }

            // Preserve each component's full original FBX face set across all filtering passes so
            // dropping candidates for a face can never skip checking that face's required layout.
            var componentFacesByRoot = new Dictionary<int, int[]>(componentTriangles.Count);
            foreach (var kv in componentTriangles)
            {
                var faces = new HashSet<int>();
                var tris = kv.Value;
                for (int i = 0; i < tris.Count; i++)
                {
                    var opts = candidatesByTri[tris[i]];
                    for (int j = 0; j < opts.Count; j++)
                    {
                        faces.Add(cornerToFace[opts[j].A]);
                    }
                }

                var arr = new int[faces.Count];
                faces.CopyTo(arr);
                componentFacesByRoot[kv.Key] = arr;
            }

            // Stage 2: Geometry-only component layout DFS.
            var currentAllowed = new Dictionary<int, List<CornerTriple>>(candidatesByTri.Count);
            bool anyBudgetExceeded = false;
            foreach (var kv in componentTriangles)
            {
                var comp = kv.Value;
                var compFaces = componentFacesByRoot[kv.Key];
                var status = SolveComponent(
                    comp,
                    compFaces,
                    candidatesByTri,
                    cornerToFace,
                    faceVariants,
                    out var supported);

                if (status == ComponentSolveStatus.Contradiction)
                {
                    output = null;
                    return false;
                }

                if (status == ComponentSolveStatus.Solved)
                {
                    foreach (var entry in supported)
                    {
                        currentAllowed[entry.Key] = entry.Value;
                    }
                }
                else
                {
                    anyBudgetExceeded = true;
                    for (int i = 0; i < comp.Count; i++)
                    {
                        int t = comp[i];
                        currentAllowed[t] = candidatesByTri[t];
                    }
                }
            }

            // Stage 3a: Lazy UV0 disambiguation if ambiguities remain.
            // Respect explicit SwapUvChannels hints when known; when unknown (null), if BOTH UvSets[0] and UvSets[1]
            // explain every triangle across the mesh with opposite assignments, retain their union so Stage 3b blendshapes
            // can disambiguate or honest ambiguity is preserved instead of returning a wrong singleton assignment.
            if (HasRemainingAmbiguity(currentAllowed) &&
                unityMesh.Uv0 != null &&
                unityMesh.Uv0.Length == unityMesh.Vertices.Length &&
                fbxMesh is IFbxMesh fullFbxMesh &&
                fullFbxMesh.UvSets != null &&
                fullFbxMesh.UvSets.Count > 0)
            {
                var unityUv0 = unityMesh.Uv0;
                bool canUseUv0 = swapUvHint != true || fullFbxMesh.UvSets.Count == 1;
                bool canUseUv1 = swapUvHint != false && fullFbxMesh.UvSets.Count > 1;

                Vector2d[] fbxUv0 = null;
                bool uv0Valid = canUseUv0 &&
                                TryReadPerCornerUvs(fullFbxMesh.UvSets[0], cornerCps.Length, out fbxUv0) &&
                                IsUvSetGloballyConsistent(fbxUv0, unityUv0, unityTriangles, triangleCount, rawOptions, currentAllowed, uvSqrTolerance);
                Vector2d[] fbxUv1 = null;
                bool uv1Valid = canUseUv1 &&
                                TryReadPerCornerUvs(fullFbxMesh.UvSets[1], cornerCps.Length, out fbxUv1) &&
                                IsUvSetGloballyConsistent(fbxUv1, unityUv0, unityTriangles, triangleCount, rawOptions, currentAllowed, uvSqrTolerance);

                if (uv0Valid && !uv1Valid)
                {
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        currentAllowed,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            UvMatches(fbxUv0[opt.A], unityUv0[unityTriangles[t * 3]], uvSqrTolerance) &&
                            UvMatches(fbxUv0[opt.B], unityUv0[unityTriangles[t * 3 + 1]], uvSqrTolerance) &&
                            UvMatches(fbxUv0[opt.C], unityUv0[unityTriangles[t * 3 + 2]], uvSqrTolerance));
                }
                else if (!uv0Valid && uv1Valid)
                {
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        currentAllowed,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            UvMatches(fbxUv1[opt.A], unityUv0[unityTriangles[t * 3]], uvSqrTolerance) &&
                            UvMatches(fbxUv1[opt.B], unityUv0[unityTriangles[t * 3 + 1]], uvSqrTolerance) &&
                            UvMatches(fbxUv1[opt.C], unityUv0[unityTriangles[t * 3 + 2]], uvSqrTolerance));
                }
                else if (uv0Valid && uv1Valid)
                {
                    var allowed0 = CloneAllowedMap(currentAllowed);
                    var allowed1 = CloneAllowedMap(currentAllowed);
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        allowed0,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            UvMatches(fbxUv0[opt.A], unityUv0[unityTriangles[t * 3]], uvSqrTolerance) &&
                            UvMatches(fbxUv0[opt.B], unityUv0[unityTriangles[t * 3 + 1]], uvSqrTolerance) &&
                            UvMatches(fbxUv0[opt.C], unityUv0[unityTriangles[t * 3 + 2]], uvSqrTolerance));
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        allowed1,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            UvMatches(fbxUv1[opt.A], unityUv0[unityTriangles[t * 3]], uvSqrTolerance) &&
                            UvMatches(fbxUv1[opt.B], unityUv0[unityTriangles[t * 3 + 1]], uvSqrTolerance) &&
                            UvMatches(fbxUv1[opt.C], unityUv0[unityTriangles[t * 3 + 2]], uvSqrTolerance));

                    foreach (var key in new List<int>(currentAllowed.Keys))
                    {
                        var set = new HashSet<CornerTriple>(allowed0[key]);
                        var other = allowed1[key];
                        for (int i = 0; i < other.Count; i++) set.Add(other[i]);
                        var merged = new List<CornerTriple>(set);
                        merged.Sort();
                        currentAllowed[key] = merged;
                    }
                }
            }

            // Stage 3b: Lazy single- and multi-frame BlendShape disambiguation if ambiguities STILL remain.
            // Compute sensitivity BEFORE checking Unity-side BlendShapes so a target without matching shapes
            // still flags blendshape sensitivity and cannot cache an ambiguous union under a geometry-only key.
            bool hasRemainingAmbiguityAfterUv = HasRemainingAmbiguity(currentAllowed);
            bool fbxHasEligibleShapes = hasRemainingAmbiguityAfterUv && HasEligibleSingleFrameBlendShape(fbxMesh);
            isBlendShapeSensitive = fbxHasEligibleShapes;

            if (fbxHasEligibleShapes && unityMesh.BlendShapes != null)
            {
                var pairErrors = ComputeBlendShapePairErrors(
                    fbxMesh,
                    fbxPoints64,
                    cornerCps,
                    unityMesh.BlendShapes,
                    unityTriangles,
                    currentAllowed,
                    scale,
                    transform);
                if (pairErrors != null)
                {
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        currentAllowed,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3], cornerCps[opt.A], blendShapeDeltaTolerance) &&
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3 + 1], cornerCps[opt.B], blendShapeDeltaTolerance) &&
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3 + 2], cornerCps[opt.C], blendShapeDeltaTolerance));
                }
            }

            exceededLayoutDfsBudget = anyBudgetExceeded;
            if (anyBudgetExceeded)
            {
                refinementNote = "One or more high-symmetry polygon components exceeded the layout DFS budget and kept filtered candidate sets.";
            }

            output = new List<CornerTriple>[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                output[t] = currentAllowed.TryGetValue(t, out var refined) ? refined : rawOptions[t];
            }

            return true;
        }

        private static bool HasRemainingAmbiguity(Dictionary<int, List<CornerTriple>> allowed)
        {
            foreach (var pair in allowed)
            {
                if (pair.Value.Count > 1) return true;
            }

            return false;
        }

        private static void ApplyAttributeFilterPerComponent(
            Dictionary<int, List<int>> componentTriangles,
            Dictionary<int, int[]> componentFacesByRoot,
            Dictionary<int, List<CornerTriple>> currentAllowed,
            int[] cornerToFace,
            Dictionary<int, FaceLayoutSet> faceVariants,
            Func<int, CornerTriple, bool> predicate)
        {
            foreach (var kv in componentTriangles)
            {
                var comp = kv.Value;
                bool compAmbiguous = false;
                for (int i = 0; i < comp.Count; i++)
                {
                    if (currentAllowed[comp[i]].Count > 1)
                    {
                        compAmbiguous = true;
                        break;
                    }
                }

                if (!compAmbiguous) continue;

                var filtered = new Dictionary<int, List<CornerTriple>>(comp.Count);
                bool anyEmpty = false;
                for (int i = 0; i < comp.Count; i++)
                {
                    int t = comp[i];
                    var opts = currentAllowed[t];
                    if (opts.Count <= 1)
                    {
                        filtered[t] = opts;
                        continue;
                    }

                    var kept = new List<CornerTriple>(opts.Count);
                    for (int j = 0; j < opts.Count; j++)
                    {
                        if (predicate(t, opts[j]))
                        {
                            kept.Add(opts[j]);
                        }
                    }

                    if (kept.Count == 0)
                    {
                        anyEmpty = true;
                        break;
                    }

                    filtered[t] = kept;
                }

                if (anyEmpty) continue;

                var status = SolveComponent(
                    comp,
                    componentFacesByRoot[kv.Key],
                    filtered,
                    cornerToFace,
                    faceVariants,
                    out var supported);

                if (status == ComponentSolveStatus.Solved)
                {
                    foreach (var entry in supported)
                    {
                        currentAllowed[entry.Key] = entry.Value;
                    }
                }
            }
        }

        private static bool TryReadPerCornerUvs(IFbxUvSet uvSet, int cornerCount, out Vector2d[] perCorner)
        {
            perCorner = null;
            if (uvSet?.Uvs == null || !uvSet.Uvs.Exists)
            {
                return false;
            }

            var buf = new Vector2d[cornerCount];
            if (!uvSet.Uvs.TryCopyPerCornerValues(buf, out int written) || written != cornerCount)
            {
                return false;
            }

            perCorner = buf;
            return true;
        }

        private static bool IsUvSetGloballyConsistent(
            Vector2d[] fbxUv,
            Vector2[] unityUv0,
            int[] unityTriangles,
            int triangleCount,
            List<CornerTriple>[] rawOptions,
            Dictionary<int, List<CornerTriple>> currentAllowed,
            float uvSqrTolerance)
        {
            for (int t = 0; t < triangleCount; t++)
            {
                var opts = currentAllowed.TryGetValue(t, out var refined) ? refined : rawOptions[t];
                if (opts == null || opts.Count == 0) continue;

                int v0 = unityTriangles[t * 3];
                int v1 = unityTriangles[t * 3 + 1];
                int v2 = unityTriangles[t * 3 + 2];
                bool anyMatch = false;
                for (int i = 0; i < opts.Count; i++)
                {
                    var opt = opts[i];
                    if (UvMatches(fbxUv[opt.A], unityUv0[v0], uvSqrTolerance) &&
                        UvMatches(fbxUv[opt.B], unityUv0[v1], uvSqrTolerance) &&
                        UvMatches(fbxUv[opt.C], unityUv0[v2], uvSqrTolerance))
                    {
                        anyMatch = true;
                        break;
                    }
                }

                if (!anyMatch)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool UvMatches(Vector2d fbxUv, Vector2 unityUv, float uvSqrTolerance)
        {
            float dx = (float)fbxUv.x - unityUv.x;
            float dy = (float)fbxUv.y - unityUv.y;
            return dx * dx + dy * dy <= uvSqrTolerance;
        }

        private static bool BlendShapePairMatches(Dictionary<long, float> pairErrors, int unityVertex, int fbxCp, float tolerance)
        {
            long key = ((long)unityVertex << 32) | (uint)fbxCp;
            return !pairErrors.TryGetValue(key, out float err) || err <= tolerance;
        }

        private static Dictionary<int, List<CornerTriple>> CloneAllowedMap(Dictionary<int, List<CornerTriple>> source)
        {
            var copy = new Dictionary<int, List<CornerTriple>>(source.Count);
            foreach (var kv in source)
            {
                copy[kv.Key] = new List<CornerTriple>(kv.Value);
            }

            return copy;
        }

        private static bool TryCollectUniqueMatchingFramePairs(
            IUnityBlendShapeSnapshot unityBlendShapes,
            IFbxBlendChannel channel,
            List<(IFbxBlendShape Shape, Vector3[] UnityDeltas)> destination)
        {
            string name = channel.Name;
            if (!unityBlendShapes.TryGetShapeFrames(name, out var frames) || frames == null || frames.Length == 0)
            {
                if (channel.Keyframes.Count == 1 &&
                    Math.Abs(channel.Keyframes[0].TargetWeight * 100d - 100d) <= k_BlendShapeFrameWeightPercentTolerance &&
                    unityBlendShapes.TryGetSingleFrameDeltaVertices(name, out var singleDeltas) &&
                    singleDeltas != null)
                {
                    frames = new[] { new UnityBlendShapeFrame(100f, singleDeltas) };
                }
                else
                {
                    return false;
                }
            }

            int beforeCount = destination.Count;
            for (int i = 0; i < channel.Keyframes.Count; i++)
            {
                var kf = channel.Keyframes[i];
                if (kf?.Shape == null ||
                    double.IsNaN(kf.TargetWeight) ||
                    double.IsInfinity(kf.TargetWeight) ||
                    Math.Abs(kf.TargetWeight) <= 1e-9d)
                {
                    continue;
                }

                double targetPercent = kf.TargetWeight * 100d;

                // Skip duplicate/ambiguous FBX keyframe weights so duplicate weights never pair arbitrarily by index.
                bool duplicateFbxWeight = false;
                for (int j = 0; j < channel.Keyframes.Count; j++)
                {
                    if (j == i || channel.Keyframes[j] == null) continue;
                    if (Math.Abs(channel.Keyframes[j].TargetWeight * 100d - targetPercent) <= k_BlendShapeFrameWeightPercentTolerance)
                    {
                        duplicateFbxWeight = true;
                        break;
                    }
                }

                if (duplicateFbxWeight)
                {
                    continue;
                }

                int matchedUnityIndex = -1;
                bool duplicateUnityWeight = false;
                for (int u = 0; u < frames.Length; u++)
                {
                    if (frames[u].DeltaVertices == null) continue;
                    if (Math.Abs(frames[u].Weight - targetPercent) <= k_BlendShapeFrameWeightPercentTolerance)
                    {
                        if (matchedUnityIndex >= 0)
                        {
                            duplicateUnityWeight = true;
                            break;
                        }

                        matchedUnityIndex = u;
                    }
                }

                if (matchedUnityIndex >= 0 && !duplicateUnityWeight)
                {
                    destination.Add((kf.Shape, frames[matchedUnityIndex].DeltaVertices));
                }
            }

            return destination.Count > beforeCount;
        }

        private static bool VerifyResolvedBlendShapesAgree(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            int[] cornerCps,
            IUnityBlendShapeSnapshot unityBlendShapes,
            int[] unityTriangles,
            int triangleCount,
            List<CornerTriple>[] resolvedOptions,
            float scale,
            CandidateTransform transform,
            float tolerance)
        {
            if (unityBlendShapes == null || fbxMesh.BlendDeformers == null || fbxMesh.BlendDeformers.Count == 0)
            {
                return true;
            }

            var unityNames = unityBlendShapes.ShapeNames ?? unityBlendShapes.SingleFrameShapeNames;
            if (unityNames == null || unityNames.Count == 0)
            {
                return true;
            }

            var unityNameSet = new HashSet<string>(unityNames, StringComparer.Ordinal);
            var matchedFramePairs = new List<(IFbxBlendShape Shape, Vector3[] UnityDeltas)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var deformer in fbxMesh.BlendDeformers)
            {
                if (deformer?.Channels == null) continue;
                foreach (var channel in deformer.Channels)
                {
                    if (!IsEligibleBlendChannel(channel)) continue;
                    if (!seenNames.Add(channel.Name) || !unityNameSet.Contains(channel.Name)) continue;
                    TryCollectUniqueMatchingFramePairs(unityBlendShapes, channel, matchedFramePairs);
                }
            }

            if (matchedFramePairs.Count == 0)
            {
                return true;
            }

            int[] idxBuf = Array.Empty<int>();
            Vector3d[] posBuf = Array.Empty<Vector3d>();
            var fbxDeltaByCp = new Dictionary<int, Vector3>();

            for (int s = 0; s < matchedFramePairs.Count; s++)
            {
                var (shape, unityDeltas) = matchedFramePairs[s];
                if (unityDeltas == null) continue;

                fbxDeltaByCp.Clear();
                int offsetCount = shape.OffsetCount;
                if (offsetCount > 0)
                {
                    if (idxBuf.Length < offsetCount)
                    {
                        idxBuf = new int[offsetCount];
                        posBuf = new Vector3d[offsetCount];
                    }

                    if (!shape.TryCopyOffsets(
                            idxBuf.AsSpan(0, offsetCount),
                            posBuf.AsSpan(0, offsetCount),
                            Span<Vector3d>.Empty,
                            Span<double>.Empty,
                            out int written) ||
                        written != offsetCount)
                    {
                        continue;
                    }

                    for (int i = 0; i < written; i++)
                    {
                        int cp = idxBuf[i];
                        if ((uint)cp >= (uint)fbxPoints64.Length) continue;
                        var off = posBuf[i];
                        double ox = Math.Abs(off.x) > k_BlendShapeOffsetThreshold ? off.x : 0d;
                        double oy = Math.Abs(off.y) > k_BlendShapeOffsetThreshold ? off.y : 0d;
                        double oz = Math.Abs(off.z) > k_BlendShapeOffsetThreshold ? off.z : 0d;
                        var b = fbxPoints64[cp];
                        var basePre = transform.ApplyPreScale32(b);
                        var targetPre = transform.ApplyPreScale32(new Vector3d(b.x + ox, b.y + oy, b.z + oz));
                        fbxDeltaByCp[cp] = new Vector3(
                            targetPre.X * scale - basePre.X * scale,
                            targetPre.Y * scale - basePre.Y * scale,
                            targetPre.Z * scale - basePre.Z * scale);
                    }
                }

                for (int t = 0; t < triangleCount; t++)
                {
                    var opts = resolvedOptions[t];
                    if (opts == null || opts.Count == 0) continue;
                    int v0 = unityTriangles[t * 3];
                    int v1 = unityTriangles[t * 3 + 1];
                    int v2 = unityTriangles[t * 3 + 2];
                    bool anyOptMatches = false;
                    for (int i = 0; i < opts.Count; i++)
                    {
                        var opt = opts[i];
                        if (SingleCornerDeltaMatches(fbxDeltaByCp, unityDeltas, v0, cornerCps[opt.A], tolerance) &&
                            SingleCornerDeltaMatches(fbxDeltaByCp, unityDeltas, v1, cornerCps[opt.B], tolerance) &&
                            SingleCornerDeltaMatches(fbxDeltaByCp, unityDeltas, v2, cornerCps[opt.C], tolerance))
                        {
                            anyOptMatches = true;
                            break;
                        }
                    }

                    if (!anyOptMatches)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SingleCornerDeltaMatches(
            Dictionary<int, Vector3> fbxDeltaByCp,
            Vector3[] unityDeltas,
            int unityVertex,
            int fbxCp,
            float tolerance)
        {
            if ((uint)unityVertex >= (uint)unityDeltas.Length) return true;
            fbxDeltaByCp.TryGetValue(fbxCp, out Vector3 expected);
            Vector3 actual = unityDeltas[unityVertex];
            return Math.Abs(expected.x - actual.x) <= tolerance &&
                   Math.Abs(expected.y - actual.y) <= tolerance &&
                   Math.Abs(expected.z - actual.z) <= tolerance;
        }

        private static Dictionary<long, float> ComputeBlendShapePairErrors(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            int[] cornerCps,
            IUnityBlendShapeSnapshot unityBlendShapes,
            int[] unityTriangles,
            Dictionary<int, List<CornerTriple>> currentAllowed,
            float scale,
            CandidateTransform transform)
        {
            var unityNames = unityBlendShapes.ShapeNames ?? unityBlendShapes.SingleFrameShapeNames;
            if (unityNames == null || unityNames.Count == 0)
            {
                return null;
            }

            var unityNameSet = new HashSet<string>(unityNames, StringComparer.Ordinal);
            var matchedFramePairs = new List<(IFbxBlendShape Shape, Vector3[] UnityDeltas)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var deformer in fbxMesh.BlendDeformers)
            {
                if (deformer == null || deformer.Channels == null) continue;
                foreach (var channel in deformer.Channels)
                {
                    if (!IsEligibleBlendChannel(channel))
                    {
                        continue;
                    }

                    string name = channel.Name;
                    if (!seenNames.Add(name) || !unityNameSet.Contains(name)) continue;
                    TryCollectUniqueMatchingFramePairs(unityBlendShapes, channel, matchedFramePairs);
                }
            }

            if (matchedFramePairs.Count == 0)
            {
                return null;
            }

            var pairList = new List<(int UnityVertex, int FbxCp)>();
            var pairSet = new HashSet<long>();
            var neededCps = new HashSet<int>();
            foreach (var kv in currentAllowed)
            {
                if (kv.Value.Count <= 1) continue;
                int t = kv.Key;
                int v0 = unityTriangles[t * 3];
                int v1 = unityTriangles[t * 3 + 1];
                int v2 = unityTriangles[t * 3 + 2];
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var opt = kv.Value[i];
                    AddPair(v0, cornerCps[opt.A], pairSet, pairList, neededCps);
                    AddPair(v1, cornerCps[opt.B], pairSet, pairList, neededCps);
                    AddPair(v2, cornerCps[opt.C], pairSet, pairList, neededCps);
                }
            }

            if (pairList.Count == 0)
            {
                return null;
            }

            var maxErrByPair = new Dictionary<long, float>(pairList.Count);
            for (int i = 0; i < pairList.Count; i++)
            {
                long key = ((long)pairList[i].UnityVertex << 32) | (uint)pairList[i].FbxCp;
                maxErrByPair[key] = 0f;
            }

            int[] idxBuf = Array.Empty<int>();
            Vector3d[] posBuf = Array.Empty<Vector3d>();
            var fbxDeltaByCp = new Dictionary<int, Vector3>(neededCps.Count);
            int evaluatedFrames = 0;

            for (int s = 0; s < matchedFramePairs.Count; s++)
            {
                var (shape, unityDeltas) = matchedFramePairs[s];
                if (unityDeltas == null)
                {
                    continue;
                }

                fbxDeltaByCp.Clear();
                int offsetCount = shape.OffsetCount;
                if (offsetCount > 0)
                {
                    if (idxBuf.Length < offsetCount)
                    {
                        idxBuf = new int[offsetCount];
                        posBuf = new Vector3d[offsetCount];
                    }

                    // Skip the entire shape on copy failure or partial read so unread entries never act as zero deltas.
                    if (!shape.TryCopyOffsets(
                            idxBuf.AsSpan(0, offsetCount),
                            posBuf.AsSpan(0, offsetCount),
                            Span<Vector3d>.Empty,
                            Span<double>.Empty,
                            out int written) ||
                        written != offsetCount)
                    {
                        continue;
                    }

                    for (int i = 0; i < written; i++)
                    {
                        int cp = idxBuf[i];
                        if (!neededCps.Contains(cp)) continue;

                        var off = posBuf[i];
                        double ox = Math.Abs(off.x) > k_BlendShapeOffsetThreshold ? off.x : 0d;
                        double oy = Math.Abs(off.y) > k_BlendShapeOffsetThreshold ? off.y : 0d;
                        double oz = Math.Abs(off.z) > k_BlendShapeOffsetThreshold ? off.z : 0d;
                        var b = fbxPoints64[cp];

                        // Match Unity's exact float32 target - base subtraction in transformed mesh space.
                        var basePre = transform.ApplyPreScale32(b);
                        var targetPre = transform.ApplyPreScale32(new Vector3d(b.x + ox, b.y + oy, b.z + oz));
                        fbxDeltaByCp[cp] = new Vector3(
                            targetPre.X * scale - basePre.X * scale,
                            targetPre.Y * scale - basePre.Y * scale,
                            targetPre.Z * scale - basePre.Z * scale);
                    }
                }

                evaluatedFrames++;
                for (int i = 0; i < pairList.Count; i++)
                {
                    var (v, cp) = pairList[i];
                    if ((uint)v >= (uint)unityDeltas.Length) continue;
                    fbxDeltaByCp.TryGetValue(cp, out Vector3 expected);
                    Vector3 actual = unityDeltas[v];
                    float err = Math.Max(
                        Math.Abs(expected.x - actual.x),
                        Math.Max(Math.Abs(expected.y - actual.y), Math.Abs(expected.z - actual.z)));
                    long key = ((long)v << 32) | (uint)cp;
                    if (err > maxErrByPair[key])
                    {
                        maxErrByPair[key] = err;
                    }
                }
            }

            return evaluatedFrames > 0 ? maxErrByPair : null;
        }

        private static void AddPair(
            int unityVertex,
            int fbxCp,
            HashSet<long> pairSet,
            List<(int, int)> pairList,
            HashSet<int> neededCps)
        {
            long key = ((long)unityVertex << 32) | (uint)fbxCp;
            if (pairSet.Add(key))
            {
                pairList.Add((unityVertex, fbxCp));
                neededCps.Add(fbxCp);
            }
        }

        private static FaceLayoutSet ComputeFaceLayouts(
            int start,
            int size,
            CoordKey[] fbxBase32,
            int[] cornerCps,
            bool allowDistinctCpWeld,
            bool reversesWinding)
        {
            if (size < 3)
            {
                return FaceLayoutSet.Empty;
            }

            if (size > 4)
            {
                return FaceLayoutSet.CreateNGon(start, size);
            }

            // Unity reverses polygon winding when det(sceneAxisMatrix) < 0, keeping corner 0 first;
            // left-handed baked scene axes with det(sceneAxisMatrix) > 0 preserve forward corner order.
            var q = new int[size];
            var qCp = new int[size];
            var qPos = new CoordKey[size];
            q[0] = start;
            for (int k = 1; k < size; k++)
            {
                q[k] = reversesWinding ? start + (size - k) : start + k;
            }

            for (int k = 0; k < size; k++)
            {
                qCp[k] = cornerCps[q[k]];
                qPos[k] = fbxBase32[qCp[k]];
            }

            var partitions = new List<int[]> { new int[0] };
            for (int i = 0; i < size; i++)
            {
                var next = new List<int[]>();
                for (int p = 0; p < partitions.Count; p++)
                {
                    var prefix = partitions[p];
                    int sameCpGroup = -1;
                    for (int j = 0; j < i; j++)
                    {
                        if (qCp[j] == qCp[i])
                        {
                            sameCpGroup = prefix[j];
                            break;
                        }
                    }

                    if (sameCpGroup >= 0)
                    {
                        next.Add(AppendInt(prefix, sameCpGroup));
                    }
                    else
                    {
                        next.Add(AppendInt(prefix, i));
                        if (allowDistinctCpWeld)
                        {
                            var seenGroups = new HashSet<int>();
                            for (int j = 0; j < i; j++)
                            {
                                if (qPos[j].Equals(qPos[i]) && seenGroups.Add(prefix[j]))
                                {
                                    next.Add(AppendInt(prefix, prefix[j]));
                                }
                            }
                        }
                    }
                }

                partitions = next;
            }

            var masks = new List<SortedCornerTriple[]>();
            var maskKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int p = 0; p < partitions.Count; p++)
            {
                var group = partitions[p];
                var order = new List<int>(size);
                for (int j = 0; j < size; j++) order.Add(j);

                if (size == 4 && CountDistinct(group) == 3)
                {
                    // Collapsed quad drops the corner whose successor shares its welded index.
                    order.Clear();
                    for (int j = 0; j < 4; j++)
                    {
                        if (group[j] != group[(j + 1) % 4])
                        {
                            order.Add(j);
                        }
                    }
                }

                SortedCornerTriple[] layout;
                if (order.Count < 3 || CountDistinctAt(group, order) != order.Count)
                {
                    layout = Array.Empty<SortedCornerTriple>();
                }
                else if (order.Count == 3)
                {
                    layout = new[] { SortedCornerTriple.Create(q[order[0]], q[order[1]], q[order[2]]) };
                }
                else
                {
                    var t0 = SortedCornerTriple.Create(q[order[0]], q[order[1]], q[order[2]]);
                    var t1 = SortedCornerTriple.Create(q[order[0]], q[order[2]], q[order[3]]);
                    layout = t0.CompareTo(t1) <= 0 ? new[] { t0, t1 } : new[] { t1, t0 };
                }

                string sig = FormatLayoutSignature(layout);
                if (maskKeys.Add(sig))
                {
                    masks.Add(layout);
                }
            }

            return FaceLayoutSet.CreateExact(masks);
        }

        private static ComponentSolveStatus SolveComponent(
            List<int> componentTriangles,
            int[] componentFaces,
            Dictionary<int, List<CornerTriple>> allowed,
            int[] cornerToFace,
            Dictionary<int, FaceLayoutSet> faceVariants,
            out Dictionary<int, List<CornerTriple>> supportedOptions)
        {
            var ts = new List<int>(componentTriangles);
            ts.Sort((a, b) => allowed[a].Count.CompareTo(allowed[b].Count));

            for (int i = 0; i < ts.Count; i++)
            {
                if (allowed[ts[i]].Count == 0)
                {
                    supportedOptions = null;
                    return ComponentSolveStatus.Contradiction;
                }
            }

            var usedByFace = new Dictionary<int, HashSet<SortedCornerTriple>>(componentFaces.Length);
            for (int i = 0; i < componentFaces.Length; i++)
            {
                usedByFace[componentFaces[i]] = new HashSet<SortedCornerTriple>();
            }

            var supportSets = new Dictionary<int, HashSet<CornerTriple>>(ts.Count);
            for (int i = 0; i < ts.Count; i++)
            {
                supportSets[ts[i]] = new HashSet<CornerTriple>();
            }

            // Explicit heap-allocated DFS stack avoids managed stack overflow on large connected components.
            int n = ts.Count;
            var optsByDepth = new List<CornerTriple>[n];
            for (int i = 0; i < n; i++) optsByDepth[i] = allowed[ts[i]];

            var nextOptionIndex = new int[n];
            var picked = new CornerTriple[n];
            var pickedFace = new int[n];
            var pickedSorted = new SortedCornerTriple[n];
            var hasPicked = new bool[n];

            int depth = 0;
            int solutions = 0;
            int steps = 0;

            while (depth >= 0)
            {
                if (++steps > k_MaxDfsStepsPerComponent)
                {
                    supportedOptions = null;
                    return ComponentSolveStatus.BudgetExceeded;
                }

                if (depth == n)
                {
                    bool allExact = true;
                    for (int i = 0; i < componentFaces.Length; i++)
                    {
                        int f = componentFaces[i];
                        if (!faceVariants[f].MatchesExact(usedByFace[f]))
                        {
                            allExact = false;
                            break;
                        }
                    }

                    if (allExact)
                    {
                        solutions++;
                        for (int i = 0; i < n; i++)
                        {
                            supportSets[ts[i]].Add(picked[i]);
                        }
                    }

                    depth--;
                    continue;
                }

                if (hasPicked[depth])
                {
                    usedByFace[pickedFace[depth]].Remove(pickedSorted[depth]);
                    hasPicked[depth] = false;
                }

                var opts = optsByDepth[depth];
                bool advanced = false;
                while (nextOptionIndex[depth] < opts.Count)
                {
                    var opt = opts[nextOptionIndex[depth]++];
                    int f = cornerToFace[opt.A];
                    var sorted = SortedCornerTriple.Create(opt.A, opt.B, opt.C);
                    var used = usedByFace[f];
                    if (!used.Add(sorted)) continue;

                    if (faceVariants[f].CanExtend(used, sorted))
                    {
                        picked[depth] = opt;
                        pickedFace[depth] = f;
                        pickedSorted[depth] = sorted;
                        hasPicked[depth] = true;
                        depth++;
                        if (depth < n)
                        {
                            nextOptionIndex[depth] = 0;
                            hasPicked[depth] = false;
                        }

                        advanced = true;
                        break;
                    }

                    used.Remove(sorted);
                }

                if (!advanced)
                {
                    nextOptionIndex[depth] = 0;
                    depth--;
                }
            }

            if (solutions == 0)
            {
                supportedOptions = null;
                return ComponentSolveStatus.Contradiction;
            }

            supportedOptions = new Dictionary<int, List<CornerTriple>>(n);
            for (int i = 0; i < n; i++)
            {
                int t = ts[i];
                var list = new List<CornerTriple>(supportSets[t]);
                list.Sort();
                supportedOptions[t] = list;
            }

            return ComponentSolveStatus.Solved;
        }

        // ─── Result Projection ─────────────────────────────────────────────────────

        private static MeshCornerMappingResult BuildResult(
            int fbxCpCount,
            int[] fbxCornerCps,
            int unityVertexCount,
            int[] unityTriangles,
            int triangleCount,
            List<CornerTriple>[] resolvedOptions,
            CoordinateMatch matchedCoords,
            int degenerateTriangles,
            string refinementNote,
            bool isBlendShapeSensitive,
            bool exceededLayoutDfsBudget)
        {
            int cornerCount = triangleCount * 3;
            var unityCornerVertices = new int[cornerCount];
            var firstFbxCorners = new int[cornerCount];
            var firstFbxCps = new int[cornerCount];
            var ambiguousCornerOptions = new Dictionary<int, int[]>();
            var vertexCpSets = new HashSet<int>[unityVertexCount];

            int uniqueCorners = 0;
            int ambiguousCorners = 0;
            int multiCpCorners = 0;
            var cornerScratch = new List<int>(8);

            for (int t = 0; t < triangleCount; t++)
            {
                var opts = resolvedOptions[t];
                for (int k = 0; k < 3; k++)
                {
                    int slot = t * 3 + k;
                    int unityV = unityTriangles[slot];
                    unityCornerVertices[slot] = unityV;

                    if (opts == null || opts.Count == 0)
                    {
                        firstFbxCorners[slot] = -1;
                        firstFbxCps[slot] = -1;
                        continue;
                    }

                    cornerScratch.Clear();
                    for (int i = 0; i < opts.Count; i++)
                    {
                        int r = k == 0 ? opts[i].A : (k == 1 ? opts[i].B : opts[i].C);
                        if (!cornerScratch.Contains(r))
                        {
                            cornerScratch.Add(r);
                        }
                    }

                    cornerScratch.Sort();
                    int firstCorner = cornerScratch[0];
                    firstFbxCorners[slot] = firstCorner;
                    firstFbxCps[slot] = fbxCornerCps[firstCorner];

                    var cpSet = vertexCpSets[unityV];
                    if (cpSet == null)
                    {
                        cpSet = new HashSet<int>();
                        vertexCpSets[unityV] = cpSet;
                    }

                    int firstCp = fbxCornerCps[firstCorner];
                    bool hasMultipleCps = false;
                    for (int i = 0; i < cornerScratch.Count; i++)
                    {
                        int cp = fbxCornerCps[cornerScratch[i]];
                        cpSet.Add(cp);
                        if (cp != firstCp) hasMultipleCps = true;
                    }

                    if (cornerScratch.Count == 1)
                    {
                        uniqueCorners++;
                    }
                    else
                    {
                        ambiguousCorners++;
                        ambiguousCornerOptions[slot] = cornerScratch.ToArray();
                        if (hasMultipleCps) multiCpCorners++;
                    }
                }
            }

            List<int>[] classToFbxCps = null;
            int isolatedVertices = 0;
            var vertexGroups = new int[unityVertexCount][];

            for (int v = 0; v < unityVertexCount; v++)
            {
                var cpSet = vertexCpSets[v];
                if (cpSet != null && cpSet.Count > 0)
                {
                    var sorted = new int[cpSet.Count];
                    cpSet.CopyTo(sorted);
                    Array.Sort(sorted);
                    vertexGroups[v] = sorted;
                    continue;
                }

                // Isolated vertex not referenced by any mapped triangle: fall back to exact float32 position class.
                if (classToFbxCps == null)
                {
                    classToFbxCps = new List<int>[matchedCoords.ClassCount];
                    for (int cp = 0; cp < fbxCpCount; cp++)
                    {
                        int cls = matchedCoords.FbxCpClass[cp];
                        if (cls < 0) continue;
                        var list = classToFbxCps[cls];
                        if (list == null)
                        {
                            list = new List<int>(2);
                            classToFbxCps[cls] = list;
                        }

                        list.Add(cp);
                    }
                }

                if (matchedCoords.UnityVertexCandidateClasses != null &&
                    matchedCoords.UnityVertexCandidateClasses[v] != null)
                {
                    var isoSet = new HashSet<int>();
                    var cands = matchedCoords.UnityVertexCandidateClasses[v];
                    for (int cIdx = 0; cIdx < cands.Length; cIdx++)
                    {
                        int cls = cands[cIdx];
                        var list = (uint)cls < (uint)classToFbxCps.Length ? classToFbxCps[cls] : null;
                        if (list == null) continue;
                        for (int m = 0; m < list.Count; m++) isoSet.Add(list[m]);
                    }

                    var arr = new int[isoSet.Count];
                    isoSet.CopyTo(arr);
                    Array.Sort(arr);
                    vertexGroups[v] = arr;
                }
                else
                {
                    int vCls = matchedCoords.UnityVertexClass[v];
                    var fallback = (uint)vCls < (uint)classToFbxCps.Length ? classToFbxCps[vCls] : null;
                    vertexGroups[v] = fallback != null && fallback.Count > 0 ? fallback.ToArray() : Array.Empty<int>();
                }

                isolatedVertices++;
            }

            string diagnostic = refinementNote;
            if (degenerateTriangles > 0 && string.IsNullOrEmpty(diagnostic))
            {
                diagnostic = $"Encountered {degenerateTriangles} degenerate or coincident-position Unity triangle(s).";
            }

            return new MeshCornerMappingResult(
                succeeded: true,
                diagnostic: diagnostic,
                matchedScale: matchedCoords.Scale,
                matchedBakeAxisConversion: matchedCoords.BakeAxisConversion,
                unityVertexCount: unityVertexCount,
                triangleCount: triangleCount,
                unityCornerVertices: unityCornerVertices,
                firstFbxCorners: firstFbxCorners,
                firstFbxControlPoints: firstFbxCps,
                ambiguousCornerOptions: ambiguousCornerOptions,
                vertexControlPointGroups: vertexGroups,
                uniqueCornerCount: uniqueCorners,
                ambiguousCornerCount: ambiguousCorners,
                multiControlPointCornerCount: multiCpCorners,
                isolatedVertexCount: isolatedVertices,
                isBlendShapeSensitive: isBlendShapeSensitive,
                exceededLayoutDfsBudget: exceededLayoutDfsBudget,
                matchedRawToUnityMatrix: matchedCoords.Transform.BuildRawToUnityAffineMatrix(matchedCoords.Scale),
                hasNonStandardRawTransform: matchedCoords.Transform.IsNonStandard,
                isApproximateCoordinateMatch: matchedCoords.IsApproximate,
                matchedRawToUnitySceneMatrix: matchedCoords.Transform.BuildRawToUnitySceneMatrix(matchedCoords.Scale));
        }

        // ─── Helpers & Small Value Types ───────────────────────────────────────────

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static int[] AppendInt(int[] prefix, int value)
        {
            var next = new int[prefix.Length + 1];
            Array.Copy(prefix, next, prefix.Length);
            next[prefix.Length] = value;
            return next;
        }

        private static int CountDistinct(int[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                bool seen = false;
                for (int j = 0; j < i; j++)
                {
                    if (values[j] == values[i])
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen) count++;
            }

            return count;
        }

        private static int CountDistinctAt(int[] values, List<int> indices)
        {
            int count = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                int val = values[indices[i]];
                bool seen = false;
                for (int j = 0; j < i; j++)
                {
                    if (values[indices[j]] == val)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen) count++;
            }

            return count;
        }

        private static string FormatLayoutSignature(SortedCornerTriple[] layout)
        {
            if (layout.Length == 0) return string.Empty;
            if (layout.Length == 1)
            {
                var t = layout[0];
                return $"{t.A},{t.B},{t.C}";
            }

            var t0 = layout[0];
            var t1 = layout[1];
            return $"{t0.A},{t0.B},{t0.C}|{t1.A},{t1.B},{t1.C}";
        }

        private static bool TryBuildSceneBakedAxisPerm(FbxCoordinateAxes axes, out SignedAxisPerm3x3 perm)
        {
            perm = default;
            if (!TryDecodeCoordinateAxis(axes.Right, out int p0, out int s0) ||
                !TryDecodeCoordinateAxis(axes.Up, out int p1, out int s1) ||
                !TryDecodeCoordinateAxis(axes.Front, out int p2, out int sFront))
            {
                return false;
            }

            if (p0 == p1 || p1 == p2 || p0 == p2)
            {
                return false;
            }

            // In ufbx, Front points opposite forward, so Unity's +Z forward is -Front (+Right, +Up, -Front).
            // Unity reverses source polygon winding iff det((+Right, +Up, -Front)) == -1.
            int s2 = -sFront;
            int parity = ((p0 == 0 && p1 == 1 && p2 == 2) ||
                          (p0 == 1 && p1 == 2 && p2 == 0) ||
                          (p0 == 2 && p1 == 0 && p2 == 1))
                ? 1
                : -1;
            bool reversesWinding = (parity * s0 * s1 * s2) < 0;

            perm = new SignedAxisPerm3x3(p0, p1, p2, s0, s1, s2, reversesWinding);
            return true;
        }

        private static bool TryDecodeCoordinateAxis(FbxCoordinateAxis axis, out int componentIndex, out int sign)
        {
            switch (axis)
            {
                case FbxCoordinateAxis.PositiveX: componentIndex = 0; sign = 1; return true;
                case FbxCoordinateAxis.NegativeX: componentIndex = 0; sign = -1; return true;
                case FbxCoordinateAxis.PositiveY: componentIndex = 1; sign = 1; return true;
                case FbxCoordinateAxis.NegativeY: componentIndex = 1; sign = -1; return true;
                case FbxCoordinateAxis.PositiveZ: componentIndex = 2; sign = 1; return true;
                case FbxCoordinateAxis.NegativeZ: componentIndex = 2; sign = -1; return true;
                default: componentIndex = 0; sign = 0; return false;
            }
        }

        private readonly struct SpatialCellKey : IEquatable<SpatialCellKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public SpatialCellKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public static SpatialCellKey FromPoint(Vector3 p, float invCellSize) =>
                new(
                    (int)Math.Floor(p.x * invCellSize),
                    (int)Math.Floor(p.y * invCellSize),
                    (int)Math.Floor(p.z * invCellSize));

            public bool Equals(SpatialCellKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is SpatialCellKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = X * 73856093;
                    h ^= Y * 19349663;
                    h ^= Z * 83492791;
                    return h;
                }
            }
        }

        private readonly struct SignedAxisPerm3x3 : IEquatable<SignedAxisPerm3x3>
        {
            public static readonly SignedAxisPerm3x3 MayaUnbaked = new(0, 1, 2, -1, 1, 1, reversesWinding: true);
            public static readonly SignedAxisPerm3x3 MayaBaked = new(0, 1, 2, 1, 1, -1, reversesWinding: true);

            public readonly int P0;
            public readonly int P1;
            public readonly int P2;
            public readonly int S0;
            public readonly int S1;
            public readonly int S2;
            public readonly bool ReversesWinding;

            public SignedAxisPerm3x3(int p0, int p1, int p2, int s0, int s1, int s2, bool reversesWinding)
            {
                P0 = p0;
                P1 = p1;
                P2 = p2;
                S0 = s0;
                S1 = s1;
                S2 = s2;
                ReversesWinding = reversesWinding;
            }

            public bool IsStandardMaya => Equals(MayaUnbaked) || Equals(MayaBaked);

            public Float3 Apply(float x, float y, float z)
            {
                float c0 = P0 == 0 ? x : (P0 == 1 ? y : z);
                float c1 = P1 == 0 ? x : (P1 == 1 ? y : z);
                float c2 = P2 == 0 ? x : (P2 == 1 ? y : z);
                return new Float3(S0 * c0, S1 * c1, S2 * c2);
            }

            public FbxMatrix4x4 BuildRowVectorScaleMatrix(double scale)
            {
                var values = new double[16];
                values[P0 * 4 + 0] = S0 * scale;
                values[P1 * 4 + 1] = S1 * scale;
                values[P2 * 4 + 2] = S2 * scale;
                values[15] = 1d;
                return FbxMatrix4x4.FromRowMajor(values);
            }

            public bool Equals(SignedAxisPerm3x3 other) =>
                P0 == other.P0 && P1 == other.P1 && P2 == other.P2 &&
                S0 == other.S0 && S1 == other.S1 && S2 == other.S2 &&
                ReversesWinding == other.ReversesWinding;

            public override bool Equals(object obj) => obj is SignedAxisPerm3x3 other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = P0;
                    h = h * 31 + P1;
                    h = h * 31 + P2;
                    h = h * 31 + S0;
                    h = h * 31 + S1;
                    h = h * 31 + S2;
                    h = h * 31 + (ReversesWinding ? 1 : 0);
                    return h;
                }
            }
        }

        private readonly struct CandidateTransform
        {
            public readonly FbxMatrix4x4 GeometryToNode;
            public readonly bool IsIdentityGeometry;
            public readonly SignedAxisPerm3x3 AxisPerm;
            public readonly bool BakeAxisConversion;

            public CandidateTransform(
                FbxMatrix4x4 geometryToNode,
                bool isIdentityGeometry,
                SignedAxisPerm3x3 axisPerm,
                bool bakeAxisConversion)
            {
                GeometryToNode = geometryToNode;
                IsIdentityGeometry = isIdentityGeometry;
                AxisPerm = axisPerm;
                BakeAxisConversion = bakeAxisConversion;
            }

            public bool MatchesMayaConvention =>
                BakeAxisConversion
                    ? AxisPerm.Equals(SignedAxisPerm3x3.MayaBaked)
                    : AxisPerm.Equals(SignedAxisPerm3x3.MayaUnbaked);

            public bool IsNonStandard => !IsIdentityGeometry || !MatchesMayaConvention;

            public Float3 ApplyPreScale32(Vector3d rawPoint)
            {
                Vector3d p = IsIdentityGeometry ? rawPoint : GeometryToNode.TransformPoint(rawPoint);
                return AxisPerm.Apply((float)p.x, (float)p.y, (float)p.z);
            }

            public FbxMatrix4x4 BuildRawToUnitySceneMatrix(float scale)
            {
                if (MatchesMayaConvention)
                {
                    return UnitySpaceContractMath.BuildFbxRawToUnityMatrix(scale, BakeAxisConversion);
                }

                return AxisPerm.BuildRowVectorScaleMatrix(scale);
            }

            public FbxMatrix4x4 BuildRawToUnityAffineMatrix(float scale)
            {
                var axisScale = BuildRawToUnitySceneMatrix(scale);
                return IsIdentityGeometry ? axisScale : (GeometryToNode * axisScale);
            }

            public FbxMatrix4x4 BuildRawToUnityLinearMatrix(float scale)
            {
                var axisScale = BuildRawToUnitySceneMatrix(scale);
                if (IsIdentityGeometry)
                {
                    return axisScale;
                }

                // Strip translation row strictly when comparing blendshape delta sensitivity.
                var linearGeom = new FbxMatrix4x4(
                    GeometryToNode[0, 0], GeometryToNode[0, 1], GeometryToNode[0, 2], 0d,
                    GeometryToNode[1, 0], GeometryToNode[1, 1], GeometryToNode[1, 2], 0d,
                    GeometryToNode[2, 0], GeometryToNode[2, 1], GeometryToNode[2, 2], 0d,
                    0d, 0d, 0d, 1d);
                return linearGeom * axisScale;
            }
        }

        private sealed class CoordinateMatch
        {
            public readonly float Scale;
            public readonly CandidateTransform Transform;
            public bool BakeAxisConversion => Transform.BakeAxisConversion;
            public readonly int ClassCount;
            public readonly int[] FbxCpClass;
            public readonly int[] UnityVertexClass;
            public readonly int[][] UnityVertexCandidateClasses;
            public readonly bool IsApproximate;
            public readonly float PositionTolerance;

            public CoordinateMatch(
                float scale,
                CandidateTransform transform,
                int classCount,
                int[] fbxCpClass,
                int[] unityVertexClass,
                int[][] unityVertexCandidateClasses,
                bool isApproximate,
                float positionTolerance)
            {
                Scale = scale;
                Transform = transform;
                ClassCount = classCount;
                FbxCpClass = fbxCpClass;
                UnityVertexClass = unityVertexClass;
                UnityVertexCandidateClasses = unityVertexCandidateClasses;
                IsApproximate = isApproximate;
                PositionTolerance = positionTolerance;
            }
        }

        private sealed class FaceLayoutSet
        {
            public static readonly FaceLayoutSet Empty = new(false, 0, 0, new List<SortedCornerTriple[]>());

            private readonly bool isNGon;
            private readonly int ngonStart;
            private readonly int maxNGonTriangles;
            private readonly List<SortedCornerTriple[]> exactLayouts;

            private FaceLayoutSet(bool isNGon, int ngonStart, int maxNGonTriangles, List<SortedCornerTriple[]> exactLayouts)
            {
                this.isNGon = isNGon;
                this.ngonStart = ngonStart;
                this.maxNGonTriangles = maxNGonTriangles;
                this.exactLayouts = exactLayouts;
            }

            public static FaceLayoutSet CreateExact(List<SortedCornerTriple[]> layouts) =>
                new(false, 0, 0, layouts);

            public static FaceLayoutSet CreateNGon(int start, int size) =>
                new(true, start, size - 2, null);

            public bool CanExtend(HashSet<SortedCornerTriple> used, SortedCornerTriple newlyAdded)
            {
                if (isNGon)
                {
                    if (used.Count > maxNGonTriangles) return false;
                    // Reject any N-gon assignment whose newly added triangle has a chord strictly crossing
                    // an already-used triangle's chord in cyclic polygon order (prevents overlapping interiors).
                    int a = newlyAdded.A - ngonStart;
                    int b = newlyAdded.B - ngonStart;
                    int c = newlyAdded.C - ngonStart;
                    foreach (var existing in used)
                    {
                        if (existing.Equals(newlyAdded)) continue;
                        int x = existing.A - ngonStart;
                        int y = existing.B - ngonStart;
                        int z = existing.C - ngonStart;
                        if (TrianglesOverlapInCycle(a, b, c, x, y, z))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                for (int i = 0; i < exactLayouts.Count; i++)
                {
                    var layout = exactLayouts[i];
                    if (used.Count > layout.Length) continue;
                    bool subset = true;
                    foreach (var tri in used)
                    {
                        bool found = false;
                        for (int j = 0; j < layout.Length; j++)
                        {
                            if (layout[j].Equals(tri))
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            subset = false;
                            break;
                        }
                    }

                    if (subset) return true;
                }

                return false;
            }

            public bool MatchesExact(HashSet<SortedCornerTriple> used)
            {
                if (isNGon)
                {
                    // Every triangle already passed non-crossing chord checks in CanExtend.
                    return used.Count <= maxNGonTriangles;
                }

                for (int i = 0; i < exactLayouts.Count; i++)
                {
                    var layout = exactLayouts[i];
                    if (used.Count != layout.Length) continue;
                    bool match = true;
                    for (int j = 0; j < layout.Length; j++)
                    {
                        if (!used.Contains(layout[j]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match) return true;
                }

                return false;
            }

            private static bool TrianglesOverlapInCycle(int a, int b, int c, int x, int y, int z)
            {
                // Inputs are sorted: a < b < c and x < y < z. Two distinct triangles on a cyclic polygon
                // have overlapping interiors iff at least one pair of edges strictly crosses in cyclic order.
                return ChordsStrictlyCross(a, b, x, y) || ChordsStrictlyCross(a, b, y, z) || ChordsStrictlyCross(a, b, x, z) ||
                       ChordsStrictlyCross(b, c, x, y) || ChordsStrictlyCross(b, c, y, z) || ChordsStrictlyCross(b, c, x, z) ||
                       ChordsStrictlyCross(a, c, x, y) || ChordsStrictlyCross(a, c, y, z) || ChordsStrictlyCross(a, c, x, z);
            }

            private static bool ChordsStrictlyCross(int p1, int q1, int p2, int q2)
            {
                // Requires p1 < q1 and p2 < q2.
                return (p1 < p2 && p2 < q1 && q1 < q2) ||
                       (p2 < p1 && p1 < q2 && q2 < q1);
            }
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

        private readonly struct CoordKey : IEquatable<CoordKey>
        {
            public readonly int XBits;
            public readonly int YBits;
            public readonly int ZBits;

            public CoordKey(float x, float y, float z)
            {
                XBits = BitConverter.SingleToInt32Bits(x == 0f ? 0f : x);
                YBits = BitConverter.SingleToInt32Bits(y == 0f ? 0f : y);
                ZBits = BitConverter.SingleToInt32Bits(z == 0f ? 0f : z);
            }

            public bool Equals(CoordKey other) =>
                XBits == other.XBits && YBits == other.YBits && ZBits == other.ZBits;

            public override bool Equals(object obj) => obj is CoordKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + XBits;
                    hash = hash * 31 + YBits;
                    hash = hash * 31 + ZBits;
                    return hash;
                }
            }
        }

        private readonly struct CornerTriple : IEquatable<CornerTriple>, IComparable<CornerTriple>
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;

            public CornerTriple(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public bool Equals(CornerTriple other) => A == other.A && B == other.B && C == other.C;
            public override bool Equals(object obj) => obj is CornerTriple other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = A;
                    h = h * 397 ^ B;
                    h = h * 397 ^ C;
                    return h;
                }
            }

            public int CompareTo(CornerTriple other)
            {
                int cmp = A.CompareTo(other.A);
                if (cmp != 0) return cmp;
                cmp = B.CompareTo(other.B);
                return cmp != 0 ? cmp : C.CompareTo(other.C);
            }
        }

        private readonly struct SortedCornerTriple : IEquatable<SortedCornerTriple>, IComparable<SortedCornerTriple>
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;

            private SortedCornerTriple(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public static SortedCornerTriple Create(int x, int y, int z)
            {
                if (x > y) (x, y) = (y, x);
                if (y > z) (y, z) = (z, y);
                if (x > y) (x, y) = (y, x);
                return new SortedCornerTriple(x, y, z);
            }

            public bool Equals(SortedCornerTriple other) => A == other.A && B == other.B && C == other.C;
            public override bool Equals(object obj) => obj is SortedCornerTriple other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = A;
                    h = h * 397 ^ B;
                    h = h * 397 ^ C;
                    return h;
                }
            }

            public int CompareTo(SortedCornerTriple other)
            {
                int cmp = A.CompareTo(other.A);
                if (cmp != 0) return cmp;
                cmp = B.CompareTo(other.B);
                return cmp != 0 ? cmp : C.CompareTo(other.C);
            }
        }
    }
}
