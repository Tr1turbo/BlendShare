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
        private const double BoundsRelativeSlack = 1e-4d;
        private const double BoundsAbsoluteSlack = 1e-5d;
        private const float MinRepresentableScale = 1e-7f;
        private const float Uv0SqrDistanceTolerance = 1.001e-6f;
        private const float BlendShapeDeltaTolerance = 1.2e-5f;
        private const double BlendShapeOffsetThreshold = 1e-6d;
        private const double BlendShapeWeight100Tolerance = 1e-3d;
        private const int MaxUlpNeighborSteps = 3;
        private const int MaxDfsStepsPerComponent = 500000;

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

            // Precompute FBX face starts and corner -> face table.
            var faceStarts = new int[fbxFaceCount];
            var cornerToFace = new int[fbxCornerCount];
            int cursor = 0;
            for (int f = 0; f < fbxFaceCount; f++)
            {
                faceStarts[f] = cursor;
                int n = fbxFaceSizes[f];
                for (int k = 0; k < n && cursor < fbxCornerCount; k++, cursor++)
                {
                    cornerToFace[cursor] = f;
                }
            }

            // Stage 1 + Stage 2/3 validation: test prioritized (bakeAxis, scale) candidates until one
            // satisfies exact coordinates, reversed polygon winding, and consistent polygon layouts.
            var axisModes = hints.BakeAxisConversion.HasValue
                ? new[] { hints.BakeAxisConversion.Value, !hints.BakeAxisConversion.Value }
                : new[] { false, true };

            CoordinateMatch matchedCoords = null;
            List<CornerTriple>[] resolvedOptions = null;
            int matchedDegenerateTriangles = 0;
            string matchedRefinementNote = string.Empty;
            bool matchedIsBlendShapeSensitive = false;
            bool matchedExceededBudget = false;
            bool sawLayoutContradiction = false;

            for (int aIdx = 0; aIdx < axisModes.Length && matchedCoords == null; aIdx++)
            {
                bool bakeAxis = axisModes[aIdx];
                var candidates = DiscoverCoordinateMatches(
                    fbxPoints64,
                    fbxCornerCps,
                    unityVertices,
                    unityTriangles,
                    bakeAxis,
                    hints.Scale);

                for (int c = 0; c < candidates.Count; c++)
                {
                    var coord = candidates[c];
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
                            unityTriangles,
                            triangleCount,
                            out var rawOptions,
                            out int degenerateCount))
                    {
                        continue;
                    }

                    if (!TryResolveAmbiguousTriangles(
                            fbxMesh,
                            fbxPoints64,
                            fbxFaceSizes,
                            faceStarts,
                            cornerToFace,
                            fbxCornerCps,
                            unityMesh,
                            unityTriangles,
                            triangleCount,
                            rawOptions,
                            coord.Scale,
                            coord.BakeAxisConversion,
                            hints.WeldVertices,
                            out var refined,
                            out string refinementNote,
                            out bool isBlendShapeSensitive,
                            out bool exceededLayoutDfsBudget))
                    {
                        sawLayoutContradiction = true;
                        continue;
                    }

                    matchedCoords = coord;
                    resolvedOptions = refined;
                    matchedDegenerateTriangles = degenerateCount;
                    matchedRefinementNote = refinementNote;
                    matchedIsBlendShapeSensitive = isBlendShapeSensitive;
                    matchedExceededBudget = exceededLayoutDfsBudget;
                    break;
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

        private static List<CoordinateMatch> DiscoverCoordinateMatches(
            Vector3d[] fbxPoints64,
            int[] fbxCornerCps,
            Vector3[] unityVertices,
            int[] unityTriangles,
            bool bakeAxis,
            float? scaleHint)
        {
            int fbxCpCount = fbxPoints64.Length;
            int unityVertexCount = unityVertices.Length;

            // Cast raw FBX coordinates to float32 with axis sign flip (matching Unity before scale).
            var fbxAxis32 = new Float3[fbxCpCount];
            var fbxValid = new bool[fbxCpCount];
            for (int i = 0; i < fbxCpCount; i++)
            {
                float x = (float)fbxPoints64[i].x;
                float y = (float)fbxPoints64[i].y;
                float z = (float)fbxPoints64[i].z;
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                {
                    continue;
                }

                fbxValid[i] = true;
                fbxAxis32[i] = bakeAxis
                    ? new Float3(x, y, -z)
                    : new Float3(-x, y, z);
            }

            // Compute extents over face-referenced points for scale seeds, and over all points for bounds filtering
            // (so meshes containing isolated vertices outside face bounds still discover the true scale).
            ComputeExtents(fbxAxis32, fbxValid, fbxCornerCps, out double usedAx, out double usedAy, out double usedAz);
            ComputeExtents(fbxAxis32, fbxValid, null, out double allAx, out double allAy, out double allAz);
            ComputeUnityExtents(unityVertices, unityTriangles, out double usedBx, out double usedBy, out double usedBz);
            ComputeUnityExtents(unityVertices, null, out double allBx, out double allBy, out double allBz);

            var seedList = new List<float>(48);
            var seenScaleBits = new HashSet<int>();

            if (scaleHint.HasValue && scaleHint.Value > MinRepresentableScale && IsFinite(scaleHint.Value))
            {
                AddScaleWithNeighbors(scaleHint.Value, seedList, seenScaleBits);
            }

            if (usedAx > 0d) AddScaleWithNeighbors((float)(usedBx / usedAx), seedList, seenScaleBits);
            if (usedAy > 0d) AddScaleWithNeighbors((float)(usedBy / usedAy), seedList, seenScaleBits);
            if (usedAz > 0d) AddScaleWithNeighbors((float)(usedBz / usedAz), seedList, seenScaleBits);
            if (allAx > 0d) AddScaleWithNeighbors((float)(allBx / allAx), seedList, seenScaleBits);
            if (allAy > 0d) AddScaleWithNeighbors((float)(allBy / allAy), seedList, seenScaleBits);
            if (allAz > 0d) AddScaleWithNeighbors((float)(allBz / allAz), seedList, seenScaleBits);

            if (seedList.Count == 0)
            {
                AddScaleWithNeighbors(1f, seedList, seenScaleBits);
                AddScaleWithNeighbors(0.01f, seedList, seenScaleBits);
            }

            var results = new List<CoordinateMatch>();
            var classByKey = new Dictionary<CoordKey, int>(fbxCpCount);

            for (int s = 0; s < seedList.Count; s++)
            {
                float scale = seedList[s];
                if (scale <= MinRepresentableScale || !IsFinite(scale))
                {
                    continue;
                }

                // Relative slack accounts for float32 endpoint rounding (e.g., 10000f * 0.01f == 100f in float32
                // while double computes 99.99999776); exact float32 coordinate lookup below enforces precision.
                if (allAx * scale * (1d + BoundsRelativeSlack) + BoundsAbsoluteSlack < allBx ||
                    allAy * scale * (1d + BoundsRelativeSlack) + BoundsAbsoluteSlack < allBy ||
                    allAz * scale * (1d + BoundsRelativeSlack) + BoundsAbsoluteSlack < allBz)
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

                if (!ContainsEquivalentPartition(results, fbxCpClass, unityVertexClass))
                {
                    results.Add(new CoordinateMatch(scale, bakeAxis, nextClass, fbxCpClass, unityVertexClass));
                }
            }

            return results;
        }

        private static void AddScaleWithNeighbors(float center, List<float> list, HashSet<int> seenBits)
        {
            if (center <= MinRepresentableScale || !IsFinite(center))
            {
                return;
            }

            AddUniqueScale(center, list, seenBits);
            float lo = center;
            float hi = center;
            for (int step = 0; step < MaxUlpNeighborSteps; step++)
            {
                lo = NextDownPositive(lo);
                hi = NextUpPositive(hi);
                AddUniqueScale(lo, list, seenBits);
                AddUniqueScale(hi, list, seenBits);
            }
        }

        private static void AddUniqueScale(float value, List<float> list, HashSet<int> seenBits)
        {
            if (value <= MinRepresentableScale || !IsFinite(value))
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
            int[] unityVertexClass)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                var other = existing[i];
                if (ArraysEqual(other.FbxCpClass, fbxCpClass) &&
                    ArraysEqual(other.UnityVertexClass, unityVertexClass))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ArraysEqual(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        // ─── Stage 1b: Polygon & Reversed-Winding Corner Matching ──────────────────

        private static bool TryMatchTriangles(
            int[] faceSizes,
            int[] faceStarts,
            int[] cornerCps,
            int[] fbxCpClass,
            int classCount,
            int[] unityVertexClass,
            int[] unityTriangles,
            int triangleCount,
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
            var markB = new int[faceCount];
            var markC = new int[faceCount];
            int stamp = 0;

            var slotsA = new List<int>(4);
            var slotsB = new List<int>(4);
            var slotsC = new List<int>(4);

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

                var facesA = classToFaces[ca];
                var facesB = classToFaces[cb];
                var facesC = classToFaces[cc];
                if (facesA == null || facesB == null || facesC == null)
                {
                    return false;
                }

                stamp++;
                for (int i = 0; i < facesB.Count; i++) markB[facesB[i]] = stamp;
                for (int i = 0; i < facesC.Count; i++) markC[facesC[i]] = stamp;

                var triOpts = new List<CornerTriple>(2);
                for (int i = 0; i < facesA.Count; i++)
                {
                    int f = facesA[i];
                    if (markB[f] != stamp || markC[f] != stamp) continue;

                    int lo = faceStarts[f];
                    int n = faceSizes[f];
                    slotsA.Clear();
                    slotsB.Clear();
                    slotsC.Clear();
                    for (int k = 0; k < n; k++)
                    {
                        int cls = cornerClasses[lo + k];
                        if (cls == ca) slotsA.Add(k);
                        if (cls == cb) slotsB.Add(k);
                        if (cls == cc) slotsC.Add(k);
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

                                // Unity reverses source FBX polygon winding (both bakeAxis=false and true flip handedness).
                                int duv = Mod(u - v, n);
                                int dvw = Mod(v - w, n);
                                int dwu = Mod(w - u, n);
                                if (duv + dvw + dwu == n)
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

        private static int Mod(int value, int modulus)
        {
            int r = value % modulus;
            return r < 0 ? r + modulus : r;
        }

        /// <summary>
        /// Returns true if <paramref name="fbxMesh"/> has at least one eligible single-frame weight-100 blendshape
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
                    if (IsEligibleSingleFrameChannel(channel, out _))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsEligibleSingleFrameChannel(IFbxBlendChannel channel, out IFbxBlendShape shape)
        {
            shape = null;
            if (channel == null ||
                string.IsNullOrEmpty(channel.Name) ||
                channel.Keyframes == null ||
                channel.Keyframes.Count != 1)
            {
                return false;
            }

            var keyframe = channel.Keyframes[0];
            if (keyframe?.Shape == null ||
                Math.Abs(keyframe.TargetWeight - 100d) > BlendShapeWeight100Tolerance)
            {
                return false;
            }

            shape = keyframe.Shape;
            return true;
        }

        // ─── Stage 2 & 3: Connected-Component Layout DFS + Lazy UV0 / BlendShape ──

        private static bool TryResolveAmbiguousTriangles(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            int[] faceSizes,
            int[] faceStarts,
            int[] cornerToFace,
            int[] cornerCps,
            UnityMeshSnapshot unityMesh,
            int[] unityTriangles,
            int triangleCount,
            List<CornerTriple>[] rawOptions,
            float scale,
            bool bakeAxis,
            bool? weldVerticesHint,
            out List<CornerTriple>[] output,
            out string refinementNote,
            out bool isBlendShapeSensitive,
            out bool exceededLayoutDfsBudget)
        {
            refinementNote = string.Empty;
            isBlendShapeSensitive = false;
            exceededLayoutDfsBudget = false;
            var ambiguousTriangles = new List<int>();
            for (int t = 0; t < triangleCount; t++)
            {
                if (rawOptions[t] != null && rawOptions[t].Count > 1)
                {
                    ambiguousTriangles.Add(t);
                }
            }

            if (ambiguousTriangles.Count == 0)
            {
                output = rawOptions;
                return true;
            }

            var dirtyFaces = new HashSet<int>();
            for (int i = 0; i < ambiguousTriangles.Count; i++)
            {
                var opts = rawOptions[ambiguousTriangles[i]];
                for (int j = 0; j < opts.Count; j++)
                {
                    dirtyFaces.Add(cornerToFace[opts[j].A]);
                }
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

            var fbxBase32 = new CoordKey[fbxPoints64.Length];
            for (int i = 0; i < fbxPoints64.Length; i++)
            {
                fbxBase32[i] = new CoordKey((float)fbxPoints64[i].x, (float)fbxPoints64[i].y, (float)fbxPoints64[i].z);
            }

            bool allowDistinctCpWeld = weldVerticesHint != false;
            var faceVariants = new Dictionary<int, FaceLayoutSet>(dirtyFaces.Count);
            foreach (int f in dirtyFaces)
            {
                faceVariants[f] = ComputeFaceLayouts(
                    faceStarts[f],
                    faceSizes[f],
                    fbxBase32,
                    cornerCps,
                    allowDistinctCpWeld);
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
            if (HasRemainingAmbiguity(currentAllowed) &&
                unityMesh.Uv0 != null &&
                fbxMesh is IFbxMesh fullFbxMesh &&
                fullFbxMesh.UvSets.Count > 0 &&
                fullFbxMesh.UvSets[0]?.Uvs != null &&
                fullFbxMesh.UvSets[0].Uvs.Exists)
            {
                var fbxUv0 = new Vector2d[cornerCps.Length];
                if (fullFbxMesh.UvSets[0].Uvs.TryCopyPerCornerValues(fbxUv0, out int uvWritten) &&
                    uvWritten == cornerCps.Length)
                {
                    var unityUv0 = unityMesh.Uv0;
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        currentAllowed,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            UvMatches(fbxUv0[opt.A], unityUv0[unityTriangles[t * 3]]) &&
                            UvMatches(fbxUv0[opt.B], unityUv0[unityTriangles[t * 3 + 1]]) &&
                            UvMatches(fbxUv0[opt.C], unityUv0[unityTriangles[t * 3 + 2]]));
                }
            }

            // Stage 3b: Lazy single-frame (weight 100) BlendShape disambiguation if ambiguities STILL remain.
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
                    bakeAxis);
                if (pairErrors != null)
                {
                    ApplyAttributeFilterPerComponent(
                        componentTriangles,
                        componentFacesByRoot,
                        currentAllowed,
                        cornerToFace,
                        faceVariants,
                        (t, opt) =>
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3], cornerCps[opt.A]) &&
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3 + 1], cornerCps[opt.B]) &&
                            BlendShapePairMatches(pairErrors, unityTriangles[t * 3 + 2], cornerCps[opt.C]));
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

        private static bool UvMatches(Vector2d fbxUv, Vector2 unityUv)
        {
            float dx = (float)fbxUv.x - unityUv.x;
            float dy = (float)fbxUv.y - unityUv.y;
            return dx * dx + dy * dy <= Uv0SqrDistanceTolerance;
        }

        private static bool BlendShapePairMatches(Dictionary<long, float> pairErrors, int unityVertex, int fbxCp)
        {
            long key = ((long)unityVertex << 32) | (uint)fbxCp;
            return !pairErrors.TryGetValue(key, out float err) || err <= BlendShapeDeltaTolerance;
        }

        private static Dictionary<long, float> ComputeBlendShapePairErrors(
            IFbxMeshGeometry fbxMesh,
            Vector3d[] fbxPoints64,
            int[] cornerCps,
            IUnityBlendShapeSnapshot unityBlendShapes,
            int[] unityTriangles,
            Dictionary<int, List<CornerTriple>> currentAllowed,
            float scale,
            bool bakeAxis)
        {
            var unityNames = unityBlendShapes.SingleFrameShapeNames;
            if (unityNames == null || unityNames.Count == 0)
            {
                return null;
            }

            var unityNameSet = new HashSet<string>(unityNames, StringComparer.Ordinal);
            var matchedFbxShapes = new List<(string Name, IFbxBlendShape Shape)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var deformer in fbxMesh.BlendDeformers)
            {
                if (deformer == null || deformer.Channels == null) continue;
                foreach (var channel in deformer.Channels)
                {
                    if (!IsEligibleSingleFrameChannel(channel, out var shape))
                    {
                        continue;
                    }

                    string name = channel.Name;
                    if (!unityNameSet.Contains(name) || !seenNames.Add(name)) continue;
                    matchedFbxShapes.Add((name, shape));
                }
            }

            if (matchedFbxShapes.Count == 0)
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
            int evaluatedShapes = 0;

            for (int s = 0; s < matchedFbxShapes.Count; s++)
            {
                var (name, shape) = matchedFbxShapes[s];
                if (!unityBlendShapes.TryGetSingleFrameDeltaVertices(name, out var unityDeltas) ||
                    unityDeltas == null)
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
                        double ox = Math.Abs(off.x) > BlendShapeOffsetThreshold ? off.x : 0d;
                        double oy = Math.Abs(off.y) > BlendShapeOffsetThreshold ? off.y : 0d;
                        double oz = Math.Abs(off.z) > BlendShapeOffsetThreshold ? off.z : 0d;
                        var b = fbxPoints64[cp];

                        // Match Unity's exact float32 target - base subtraction in mesh space.
                        float dx = (float)(b.x + ox) * scale - (float)b.x * scale;
                        float dy = (float)(b.y + oy) * scale - (float)b.y * scale;
                        float dz = (float)(b.z + oz) * scale - (float)b.z * scale;
                        fbxDeltaByCp[cp] = bakeAxis
                            ? new Vector3(dx, dy, -dz)
                            : new Vector3(-dx, dy, dz);
                    }
                }

                evaluatedShapes++;
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

            return evaluatedShapes > 0 ? maxErrByPair : null;
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
            bool allowDistinctCpWeld)
        {
            if (size < 3)
            {
                return FaceLayoutSet.Empty;
            }

            if (size > 4)
            {
                return FaceLayoutSet.CreateNGon(start, size);
            }

            // Unity reverses polygon winding while keeping corner 0 first: [raw[0], raw[n-1], ..., raw[1]].
            var q = new int[size];
            var qCp = new int[size];
            var qPos = new CoordKey[size];
            q[0] = start;
            for (int k = 1; k < size; k++)
            {
                q[k] = start + (size - k);
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
                if (++steps > MaxDfsStepsPerComponent)
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

                int vCls = matchedCoords.UnityVertexClass[v];
                var fallback = (uint)vCls < (uint)classToFbxCps.Length ? classToFbxCps[vCls] : null;
                vertexGroups[v] = fallback != null && fallback.Count > 0 ? fallback.ToArray() : Array.Empty<int>();
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
                exceededLayoutDfsBudget: exceededLayoutDfsBudget);
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

        private sealed class CoordinateMatch
        {
            public readonly float Scale;
            public readonly bool BakeAxisConversion;
            public readonly int ClassCount;
            public readonly int[] FbxCpClass;
            public readonly int[] UnityVertexClass;

            public CoordinateMatch(
                float scale,
                bool bakeAxisConversion,
                int classCount,
                int[] fbxCpClass,
                int[] unityVertexClass)
            {
                Scale = scale;
                BakeAxisConversion = bakeAxisConversion;
                ClassCount = classCount;
                FbxCpClass = fbxCpClass;
                UnityVertexClass = unityVertexClass;
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
