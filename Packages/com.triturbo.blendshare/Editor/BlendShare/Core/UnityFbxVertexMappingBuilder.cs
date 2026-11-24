using System;
using System.Linq;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Mapping;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Builds serialized <see cref="UnityVertexMappingObject"/> assets from Unity meshes and FBX geometry
    /// using <see cref="FbxUnityInverseMapper"/>.
    /// </summary>
    public static class UnityFbxVertexMappingBuilder
    {
        private const string LogPrefix = "[BlendShare Vertex Mapping]";

        /// <summary>When enabled, emits per-stage timing logs to the Unity Console.</summary>
        public static bool VerboseLogging { get; set; }

        /// <summary>
        /// Builds a <see cref="UnityVertexMappingObject"/> by opening the FBX asset behind
        /// <paramref name="fbxGo"/> and mapping <paramref name="unityMesh"/> back to its FBX control points.
        /// </summary>
        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxGo)
        {
            IFbxScene scene = null;
            try
            {
                if (fbxGo != null)
                {
                    var sceneResult = FbxUnityAssetReader.ReadScene(fbxGo);
                    if (sceneResult.Success)
                    {
                        scene = sceneResult.Value;
                    }
                }

                var hints = FbxInverseMapHints.FromImportSettings(FbxUnityAssetReader.GetImportSettings(fbxGo));
                return BuildFromFbx(
                    unityRendererPath,
                    unityMesh,
                    scene,
                    hints,
                    out _);
            }
            finally
            {
                scene?.Dispose();
            }
        }

        /// <summary>
        /// Builds a <see cref="UnityVertexMappingObject"/> from an already-open FBX scene using
        /// optional <see cref="FbxInverseMapHints"/>.
        /// </summary>
        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            IFbxScene fbxScene,
            FbxInverseMapHints hints,
            out IFbxMesh fbxMesh)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMs = 0d;

            string nodePath = MeshNodePath.Normalize(unityRendererPath);
            fbxMesh = fbxScene?.FindMeshByNodePath(MeshNodePath.ToFbxPath(nodePath));
            LogTiming("FbxAsset", nodePath,
                "Resolve FBX mesh by node path", stopwatch, ref lastLogMs,
                $"found={(fbxMesh != null ? 1 : 0)}, fbxVertices={fbxMesh?.ControlPointCount ?? 0}, blendShapes={CountBlendShapeChannels(fbxMesh)}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVertexHash = UnityMeshEditorDataUtility.TryCalculatePositionHash(unityMesh, out string unityVertexHash)
                ? unityVertexHash
                : string.Empty;
            mapping.m_SpaceConversion = new FbxUnitySpaceConversion(
                hints.Scale ?? 1f,
                hints.BakeAxisConversion ?? false);
            mapping.m_IndexGroups = CreateEmptyGroups(Mathf.Max(0, mapping.m_UnityVertexCount));
            mapping.m_Report = string.Empty;

            if (unityMesh == null)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires a Unity mesh asset.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (fbxScene == null)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires an open ufbx scene.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (fbxMesh == null || fbxMesh.ControlPointCount == 0)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping could not read matching FBX vertex positions.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (mapping.m_UnityVertexCount <= 0)
            {
                SetMappingStatus(mapping, false, "FBX asset mapping requires a Unity mesh with vertices.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            if (!UnityMeshEditorDataUtility.TryReadMeshSnapshot(unityMesh, out var snapshot))
            {
                SetMappingStatus(mapping, false, "FBX asset mapping could not read Unity mesh vertex and triangle data.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            var result = FbxUnityInverseMapper.Map(fbxMesh, snapshot, hints);
            LogTiming("FbxAsset", nodePath, "Inverse map Unity corners to FBX control points", stopwatch, ref lastLogMs,
                $"succeeded={result.Succeeded}, scale={result.MatchedScale:0.######}, bakeAxis={result.MatchedBakeAxisConversion}, " +
                $"corners={result.CornerCount}, unique={result.UniqueCornerCount}, ambiguous={result.AmbiguousCornerCount}, " +
                $"multiCp={result.MultiControlPointCornerCount}, isolatedVerts={result.IsolatedVertexCount}");

            if (!result.Succeeded)
            {
                SetMappingStatus(mapping, false, result.Diagnostic);
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            mapping.m_SpaceConversion = new FbxUnitySpaceConversion(
                result.MatchedScale,
                result.MatchedBakeAxisConversion);
            mapping.m_IndexGroups = ToSerializedIndexGroups(result.VertexControlPointGroups);

            int unresolvedVertices = CountUnresolvedVertices(mapping.m_IndexGroups);
            if (unresolvedVertices > 0)
            {
                SetMappingStatus(mapping, false, $"FBX asset mapping has {unresolvedVertices} Unity vertices without matching FBX control points.");
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
                return mapping;
            }

            SetMappingStatus(mapping, true, result.Diagnostic);
            LogCompletion("FbxAsset", nodePath, stopwatch, mapping);
            return mapping;
        }

        internal static FbxIndexGroup[] ToSerializedIndexGroups(int[][] rawGroups)
        {
            if (rawGroups == null)
            {
                return Array.Empty<FbxIndexGroup>();
            }

            var groups = new FbxIndexGroup[rawGroups.Length];
            for (int i = 0; i < rawGroups.Length; i++)
            {
                groups[i] = new FbxIndexGroup
                {
                    m_Indices = rawGroups[i] ?? Array.Empty<int>()
                };
            }

            return groups;
        }

        private static int CountUnresolvedVertices(FbxIndexGroup[] groups)
        {
            if (groups == null) return 0;
            int unresolved = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                var indices = groups[i].m_Indices;
                if (indices == null || indices.Length == 0 || indices[0] < 0)
                {
                    unresolved++;
                }
            }

            return unresolved;
        }

        private static FbxIndexGroup[] CreateEmptyGroups(int count)
        {
            var groups = new FbxIndexGroup[count];
            for (int i = 0; i < count; i++)
            {
                groups[i] = new FbxIndexGroup { m_Indices = Array.Empty<int>() };
            }

            return groups;
        }

        private static int CountBlendShapeChannels(IFbxMesh mesh)
        {
            return mesh?.BlendDeformers.Sum(deformer => deformer.Channels.Count) ?? 0;
        }

        private static void SetMappingStatus(UnityVertexMappingObject mapping, bool isValid, string summary)
        {
            mapping.m_IsValid = isValid;
            mapping.m_Report = summary ?? string.Empty;
        }

        private static void LogTiming(
            string buildMode,
            string unityRendererPath,
            string section,
            Stopwatch stopwatch,
            ref double previousMs,
            string details = null)
        {
            if (!VerboseLogging) return;
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            double sectionMs = elapsed - previousMs;
            previousMs = elapsed;
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': {section} took {sectionMs:0.###} ms (total {elapsed:0.###} ms){FormatLogDetails(details)}");
        }

        private static void LogCompletion(
            string buildMode,
            string unityRendererPath,
            Stopwatch stopwatch,
            UnityVertexMappingObject mapping)
        {
            if (!VerboseLogging && mapping != null && mapping.m_IsValid) return;
            string status = mapping != null && mapping.m_IsValid
                ? "valid"
                : $"invalid: {mapping?.m_Report}";
            Debug.Log(
                $"{LogPrefix} {buildMode} '{FormatLogTarget(unityRendererPath)}': Finished in {stopwatch.Elapsed.TotalMilliseconds:0.###} ms ({status})");
        }

        private static string FormatLogTarget(string path)
        {
            return string.IsNullOrEmpty(path) ? "<unknown>" : path;
        }

        private static string FormatLogDetails(string details)
        {
            return string.IsNullOrEmpty(details) ? string.Empty : $" ({details})";
        }
    }
}
