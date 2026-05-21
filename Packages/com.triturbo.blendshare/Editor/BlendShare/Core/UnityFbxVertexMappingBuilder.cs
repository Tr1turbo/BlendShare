using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

using Triturbo.Fbx;
using Triturbo.Fbx.Unity;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Builds vertex mappings between imported Unity meshes and FBX control points.
    /// </summary>
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-6f;
        private const int ParallelMatchThreshold = 1024;
        private const string LogPrefix = "[BlendShare Vertex Mapping]";

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxGo)
        {
            return BuildFromFbx(unityRendererPath, unityMesh, fbxGo, out FbxMeshGeometry _);
        }

        public static UnityVertexMappingObject BuildFromFbx(
            string unityRendererPath,
            Mesh unityMesh,
            GameObject fbxGo,
            out FbxMeshGeometry fbxMesh)
        {
            var stopwatch = Stopwatch.StartNew();
            double lastLogMs = 0d;

            fbxMesh = null;
            float importScale = FbxUnityAssetReader.GetImportScale(fbxGo);
            string nodePath = MeshNodePath.Normalize(unityRendererPath);
            if (fbxGo != null)
            {
                var documentResult = FbxUnityAssetReader.Read(
                    fbxGo,
                    new[] { MeshNodePath.ToFbxPath(nodePath) });
                if (documentResult.Success)
                {
                    var fbxMeshResult = documentResult.Value.TryFindMesh(MeshNodePath.ToFbxPath(nodePath));
                    if (fbxMeshResult.Success)
                    {
                        fbxMesh = fbxMeshResult.Value;
                    }
                }
            }
            LogTiming("FbxAsset", nodePath,
                "Read FBX control point positions and blendshapes by node path", stopwatch, ref lastLogMs,
                $"found={(fbxMesh != null ? 1 : 0)}, controlPoints={fbxMesh?.ControlPointCount ?? 0}, blendShapes={CountBlendShapeChannels(fbxMesh)}");

            var mapping = ScriptableObject.CreateInstance<UnityVertexMappingObject>();
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVertexHash = UnityVertexPositionHash.Calculate(unityMesh);
            mapping.m_FbxToUnityScale = importScale;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.FbxAsset;
            mapping.m_IndexGroups = CreateEmptyGroups(Mathf.Max(0, mapping.m_UnityVertexCount));
            LogTiming("FbxAsset", nodePath, "Create mapping object", stopwatch, ref lastLogMs,
                $"unityVertices={mapping.m_IndexGroups.Length}");

            if (unityMesh == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires a Unity mesh asset.";
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (fbxGo == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires an FBX asset.";
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (fbxMesh == null || fbxMesh.ControlPointCount == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping could not read matching FBX control point positions.";
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            if (mapping.m_UnityVertexCount <= 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires a Unity mesh with vertices.";
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            var pair = PositionFingerprintFactory.CreatePair(fbxMesh, unityMesh, importScale);
            LogTiming("FbxAsset", nodePath, "CreatePair", stopwatch, ref lastLogMs,
                $"usableBlendShapes={pair.BlendShapeNames?.Length ?? 0}");

            if (!pair.IsValid)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "FBX asset mapping requires matching FBX and Unity mesh blendshapes, or no usable blendshapes on either mesh for position-only matching.";
                LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
                return mapping;
            }

            LogTiming("FbxAsset", nodePath, "Create FBX fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.FbxFingerprints.Length}");
            LogTiming("FbxAsset", nodePath, "Create Unity fingerprints", stopwatch, ref lastLogMs,
                $"fingerprints={pair.UnityFingerprints.Length}");

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_IndexGroups,
                pair.UnityFingerprints,
                pair.FbxFingerprints);
            LogTiming("FbxGo", nodePath, "Match Unity vertices to FBX control points", stopwatch, ref lastLogMs,
                $"unresolved={unresolved}");

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"FBX asset mapping has {unresolved} Unity vertices without matching FBX control point positions.";

            LogCompletion("FbxAsset", nodePath, stopwatch, mapping.m_IsValid, mapping.m_InvalidReason);
            return mapping;
        }

        // ─── Core matching ─────────────────────────────────────────────────────────

        private static int MapUnityVerticesByFingerprintMatch(
            FbxIndexGroup[] mappingGroups,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            PositionFingerprint[] fbxFingerprints)
        {
            var sw = Stopwatch.StartNew();

            // Build welding groups: K unique fingerprint groups (K ≤ M control points).
            var weldingIndex = new PositionFingerprint.WeldingGroupIndex(fbxFingerprints, FingerprintEpsilon);
            double weldMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: WeldingGroupIndex built in {weldMs:0.###} ms" +
                      $" (fbxControlPoints={fbxFingerprints?.Length ?? 0}, groups={weldingIndex.GroupCount})");

            // Build spatial candidate index over K representatives only (faster than M).
            var candidateIndex = new PositionFingerprint.CandidateIndex(
                weldingIndex.GetAllRepresentatives(),
                FingerprintEpsilon);
            double indexMs = sw.Elapsed.TotalMilliseconds - weldMs;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: CandidateIndex built in {indexMs:0.###} ms");

            double matchStart = sw.Elapsed.TotalMilliseconds;
            int unresolved = 0;
            int unityVertexCount = unityFingerprints.Count;
            if (unityVertexCount >= ParallelMatchThreshold)
            {
                Parallel.For(
                    0,
                    unityVertexCount,
                    () => 0,
                    (unityIndex, _, localUnresolved) =>
                    {
                        if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(candidateIndex, out int groupId))
                        {
                            localUnresolved++;
                            return localUnresolved;
                        }

                        mappingGroups[unityIndex] = new FbxIndexGroup
                        {
                            m_Indices = weldingIndex.GetGroupMembers(groupId)
                        };

                        return localUnresolved;
                    },
                    localUnresolved => Interlocked.Add(ref unresolved, localUnresolved));
            }
            else
            {
                for (int unityIndex = 0; unityIndex < unityVertexCount; unityIndex++)
                {
                    if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(candidateIndex, out int groupId))
                    {
                        // Leave group as empty (initialized above)
                        unresolved++;
                        continue;
                    }

                    mappingGroups[unityIndex] = new FbxIndexGroup
                    {
                        m_Indices = weldingIndex.GetGroupMembers(groupId)
                    };
                }
            }
            double matchMs = sw.Elapsed.TotalMilliseconds - matchStart;
            Debug.Log($"{LogPrefix} MapUnityVerticesByFingerprintMatch: Matching loop done in {matchMs:0.###} ms" +
                      $" (unityVertices={unityVertexCount}, unresolved={unresolved}, " +
                      $"mode={(unityVertexCount >= ParallelMatchThreshold ? "parallel" : "sequential")})");
            candidateIndex.LogStats(LogPrefix);

            return unresolved;
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static FbxIndexGroup[] CreateEmptyGroups(int count)
        {
            var groups = new FbxIndexGroup[count];
            for (int i = 0; i < count; i++)
            {
                groups[i] = new FbxIndexGroup { m_Indices = System.Array.Empty<int>() };
            }
            return groups;
        }

        private static int CountBlendShapeChannels(FbxMeshGeometry mesh)
        {
            return mesh?.BlendShapeDeformers.Sum(deformer => deformer.Channels.Count) ?? 0;
        }

        // ─── Logging ───────────────────────────────────────────────────────────────

        private static void LogTiming(
            string buildMode,
            string unityRendererPath,
            string section,
            Stopwatch stopwatch,
            ref double previousMs,
            string details = null)
        {
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
            bool isValid,
            string invalidReason)
        {
            string status = isValid ? "valid" : $"invalid: {invalidReason}";
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
