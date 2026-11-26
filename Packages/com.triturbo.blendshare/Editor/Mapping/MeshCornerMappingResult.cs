using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Pure face-corner and projected vertex correspondence produced by <see cref="FbxUnityInverseMapper"/>.
    /// Independent of Unity serialized ScriptableObject storage types.
    /// </summary>
    public sealed class MeshCornerMappingResult
    {
        /// <summary>Whether the inverse mapping matched the Unity mesh to the FBX mesh.</summary>
        public bool Succeeded { get; }

        /// <summary>Failure or warning diagnostic message, or empty string on clean success.</summary>
        public string Diagnostic { get; }

        /// <summary>Matched Unity float32 vertex import scale.</summary>
        public float MatchedScale { get; }

        /// <summary>Whether the matched coordinate mapping used Unity's baked axis conversion.</summary>
        public bool MatchedBakeAxisConversion { get; }

        /// <summary>
        /// Full affine row-vector matrix transforming raw FBX mesh-local points and delta vectors into Unity mesh-local space
        /// (includes any node <c>GeometryToNodeMatrix</c> translation/rotation/scale, baked axis permutation, and import scale).
        /// </summary>
        public FbxMatrix4x4 MatchedRawToUnityMatrix { get; }

        /// <summary>
        /// Linear row-vector matrix transforming FBX scene/node-level coordinates into Unity scene-local space
        /// (includes baked scene axis permutation and import scale, excluding mesh-node <c>GeometryToNodeMatrix</c>).
        /// </summary>
        public FbxMatrix4x4 MatchedRawToUnitySceneMatrix { get; }

        /// <summary>
        /// True when the matched transform required a non-Maya axis permutation (e.g. Z-Up baked axes) or a
        /// non-identity node <c>GeometryToNodeMatrix</c>.
        /// </summary>
        public bool HasNonStandardRawTransform { get; }

        /// <summary>
        /// True when coordinate matching required quantized tolerance fallback (e.g. <c>ModelImporter.meshCompression != Off</c>).
        /// Approximate matches are valid for direct application but are never persisted into lossless embedded mappings or disk caches.
        /// </summary>
        public bool IsApproximateCoordinateMatch { get; }

        /// <summary>Number of Unity mesh vertices.</summary>
        public int UnityVertexCount { get; }

        /// <summary>Number of Unity triangles mapped.</summary>
        public int TriangleCount { get; }

        /// <summary>Total number of Unity triangle corners (<c>TriangleCount * 3</c>).</summary>
        public int CornerCount => UnityCornerVertices.Length;

        /// <summary>Unity vertex index at each triangle corner.</summary>
        public int[] UnityCornerVertices { get; }

        /// <summary>Representative matched FBX face-corner index for each Unity triangle corner, or <c>-1</c> if degenerate.</summary>
        public int[] FirstFbxCorners { get; }

        /// <summary>Representative matched FBX control-point index for each Unity triangle corner, or <c>-1</c> if degenerate.</summary>
        public int[] FirstFbxControlPoints { get; }

        /// <summary>
        /// Surviving FBX face-corner candidates for Unity corners that remained genuinely ambiguous
        /// after polygon-layout, UV, and blendshape disambiguation.
        /// </summary>
        public IReadOnlyDictionary<int, int[]> AmbiguousCornerOptions { get; }

        /// <summary>
        /// Projected FBX control-point index groups per Unity vertex (sorted ascending, deduplicated).
        /// </summary>
        public int[][] VertexControlPointGroups { get; }

        /// <summary>Count of Unity corners resolved to a single FBX face corner.</summary>
        public int UniqueCornerCount { get; }

        /// <summary>Count of Unity corners retaining multiple indistinguishable FBX face corners.</summary>
        public int AmbiguousCornerCount { get; }

        /// <summary>Count of ambiguous Unity corners whose surviving candidates span more than one FBX control point.</summary>
        public int MultiControlPointCornerCount { get; }

        /// <summary>Count of Unity vertices not referenced by any mapped triangle that fell back to position class.</summary>
        public int IsolatedVertexCount { get; }

        /// <summary>
        /// True when ambiguity remained after geometry and UV disambiguation and the FBX mesh has at least one
        /// eligible blendshape channel (independent of whether the Unity mesh supplied matching shapes).
        /// Such results are blendshape-sensitive and must not be cached or embedded under a geometry+UV0-only key.
        /// </summary>
        public bool IsBlendShapeSensitive { get; }

        /// <summary>
        /// True when one or more high-symmetry components exceeded the polygon-layout DFS step budget.
        /// </summary>
        public bool ExceededLayoutDfsBudget { get; }

        internal MeshCornerMappingResult(
            bool succeeded,
            string diagnostic,
            float matchedScale,
            bool matchedBakeAxisConversion,
            int unityVertexCount,
            int triangleCount,
            int[] unityCornerVertices,
            int[] firstFbxCorners,
            int[] firstFbxControlPoints,
            IReadOnlyDictionary<int, int[]> ambiguousCornerOptions,
            int[][] vertexControlPointGroups,
            int uniqueCornerCount,
            int ambiguousCornerCount,
            int multiControlPointCornerCount,
            int isolatedVertexCount,
            bool isBlendShapeSensitive = false,
            bool exceededLayoutDfsBudget = false,
            FbxMatrix4x4? matchedRawToUnityMatrix = null,
            bool hasNonStandardRawTransform = false,
            bool isApproximateCoordinateMatch = false,
            FbxMatrix4x4? matchedRawToUnitySceneMatrix = null)
        {
            Succeeded = succeeded;
            Diagnostic = diagnostic ?? string.Empty;
            MatchedScale = matchedScale;
            MatchedBakeAxisConversion = matchedBakeAxisConversion;
            var defaultRawToUnity = succeeded && matchedScale > 0f && !float.IsNaN(matchedScale) && !float.IsInfinity(matchedScale)
                ? UnitySpaceContractMath.BuildFbxRawToUnityMatrix(matchedScale, matchedBakeAxisConversion)
                : FbxMatrix4x4.Identity;
            MatchedRawToUnityMatrix = matchedRawToUnityMatrix ?? defaultRawToUnity;
            MatchedRawToUnitySceneMatrix = matchedRawToUnitySceneMatrix ?? MatchedRawToUnityMatrix;
            HasNonStandardRawTransform = hasNonStandardRawTransform;
            IsApproximateCoordinateMatch = isApproximateCoordinateMatch;
            UnityVertexCount = unityVertexCount;
            TriangleCount = triangleCount;
            UnityCornerVertices = unityCornerVertices ?? Array.Empty<int>();
            FirstFbxCorners = firstFbxCorners ?? Array.Empty<int>();
            FirstFbxControlPoints = firstFbxControlPoints ?? Array.Empty<int>();
            AmbiguousCornerOptions = ambiguousCornerOptions ?? new Dictionary<int, int[]>();
            VertexControlPointGroups = vertexControlPointGroups ?? Array.Empty<int[]>();
            UniqueCornerCount = uniqueCornerCount;
            AmbiguousCornerCount = ambiguousCornerCount;
            MultiControlPointCornerCount = multiControlPointCornerCount;
            IsolatedVertexCount = isolatedVertexCount;
            IsBlendShapeSensitive = isBlendShapeSensitive;
            ExceededLayoutDfsBudget = exceededLayoutDfsBudget;
        }

        /// <summary>Creates a failed mapping result with the given diagnostic message.</summary>
        public static MeshCornerMappingResult Failed(string diagnostic, int unityVertexCount = 0)
        {
            var emptyGroups = new int[Math.Max(0, unityVertexCount)][];
            for (int i = 0; i < emptyGroups.Length; i++)
            {
                emptyGroups[i] = Array.Empty<int>();
            }

            return new MeshCornerMappingResult(
                succeeded: false,
                diagnostic: diagnostic,
                matchedScale: 0f,
                matchedBakeAxisConversion: false,
                unityVertexCount: Math.Max(0, unityVertexCount),
                triangleCount: 0,
                unityCornerVertices: Array.Empty<int>(),
                firstFbxCorners: Array.Empty<int>(),
                firstFbxControlPoints: Array.Empty<int>(),
                ambiguousCornerOptions: new Dictionary<int, int[]>(),
                vertexControlPointGroups: emptyGroups,
                uniqueCornerCount: 0,
                ambiguousCornerCount: 0,
                multiControlPointCornerCount: 0,
                isolatedVertexCount: 0);
        }
    }
}
