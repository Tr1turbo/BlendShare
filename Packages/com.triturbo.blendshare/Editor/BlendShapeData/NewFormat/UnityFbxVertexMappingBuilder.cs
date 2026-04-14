using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class UnityFbxVertexMappingBuilder
    {
        private const float FingerprintEpsilon = 1e-5f;

        public static UnityMeshVerticesMappingObject BuildFromLegacy(
            string unityRendererPath,
            int unityVertexCount,
            int unityVerticesHash,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes)
        {
            return BuildFromLegacy(
                unityRendererPath,
                unityVertexCount,
                unityVerticesHash,
                fbxBlendShapes,
                unityBlendShapes,
                null,
                null);
        }

        internal static UnityMeshVerticesMappingObject BuildFromLegacy(
            string unityRendererPath,
            int unityVertexCount,
            int unityVerticesHash,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes,
            Mesh unityMesh = null,
            FbxMeshSnapshot fbxMesh = null)
        {
            var records = fbxBlendShapes?.ToArray()
                          ?? System.Array.Empty<BlendShapeRecord>();
            var unityCache = unityBlendShapes?.ToArray()
                             ?? System.Array.Empty<MappingUnityBlendShapeCache>();
            var unityBlendShapesByName = BuildUnityBlendShapeLookup(unityCache);
            BuildBlendShapeDataSequences(
                records,
                unityBlendShapesByName,
                out var blendShapeSequence,
                out var fbxBlendShapeSequence,
                out var unityBlendShapeSequence);

            var mapping = ScriptableObject.CreateInstance<UnityMeshVerticesMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityVertexCount = unityVertexCount;
            mapping.m_UnityVerticesHash = unityVerticesHash;
            mapping.m_FbxToUnityScale = fbxMesh?.ImportScale ?? 1f;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.LegacyUpgrade;
            mapping.m_SourceBlendShapeNames = blendShapeSequence;
            mapping.m_Indices = Enumerable.Repeat(-1, Mathf.Max(0, unityVertexCount)).ToArray();

            if (blendShapeSequence.Length == 0 || unityBlendShapesByName.Count == 0 || unityVertexCount <= 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires both FBX and Unity blendshape data.";
                mapping.SetLegacyCache(unityCache);
                return mapping;
            }

            int sampleCount = PositionFingerprintFactory.CountSamples(unityBlendShapeSequence);
            if (sampleCount == 0)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires matching FBX and Unity blendshape frames.";
                mapping.SetLegacyCache(unityCache);
                return mapping;
            }

            if (!HasUnityMesh(unityMesh, unityVertexCount) || !HasFbxMesh(fbxMesh))
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Legacy mapping requires inferable Unity and FBX base positions.";
                mapping.SetLegacyCache(unityCache);
                return mapping;
            }

            var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(
                fbxMesh,
                fbxBlendShapeSequence);
            var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(
                unityMesh,
                unityBlendShapeSequence);

            Debug.Log(unityRendererPath);

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_Indices,
                unityFingerprints,
                fbxFingerprints);

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"Legacy mapping has {unresolved} unresolved Unity vertices.";

            if (!mapping.m_IsValid)
            {
                mapping.SetLegacyCache(unityCache);
            }

            return mapping;
        }

#if ENABLE_FBX_SDK
        public static UnityMeshVerticesMappingObject BuildFromExtraction(
            string unityRendererPath,
            Mesh unityMesh,
            FbxMesh fbxMesh,
            IEnumerable<BlendShapeRecord> fbxBlendShapes,
            IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes,
            float fbxImportScale = 1f)
        {
            var records = fbxBlendShapes?.ToArray()
                          ?? System.Array.Empty<BlendShapeRecord>();
            var unityCache = unityBlendShapes?.ToArray()
                             ?? System.Array.Empty<MappingUnityBlendShapeCache>();
            var unityBlendShapesByName = BuildUnityBlendShapeLookup(unityCache);
            BuildBlendShapeDataSequences(
                records,
                unityBlendShapesByName,
                out var blendShapeSequence,
                out var fbxBlendShapeSequence,
                out var unityBlendShapeSequence);

            var mapping = ScriptableObject.CreateInstance<UnityMeshVerticesMappingObject>();
            mapping.m_UnityRendererPath = unityRendererPath;
            mapping.m_UnityMesh = unityMesh;
            mapping.m_UnityVertexCount = unityMesh != null ? unityMesh.vertexCount : 0;
            mapping.m_UnityVerticesHash = unityMesh != null ? MeshData.GetVerticesHash(unityMesh) : 0;
            mapping.m_FbxToUnityScale = fbxImportScale;
            mapping.m_BuildMode = UnityFbxMappingBuildMode.Extraction;
            mapping.m_SourceBlendShapeNames = blendShapeSequence;
            mapping.m_Indices = Enumerable.Repeat(-1, Mathf.Max(0, mapping.m_UnityVertexCount)).ToArray();

            if (unityMesh == null || fbxMesh == null)
            {
                mapping.m_IsValid = false;
                mapping.m_InvalidReason = "Extraction mapping requires Unity mesh and FBX mesh.";
                mapping.SetLegacyCache(unityCache);
                return mapping;
            }

            var fbxMeshSnapshot = CreateFbxMeshSnapshot(fbxMesh, fbxImportScale);
            var fbxFingerprints = PositionFingerprintFactory.CreateFromFbx(
                fbxMeshSnapshot,
                fbxBlendShapeSequence);
            var unityFingerprints = PositionFingerprintFactory.CreateFromUnity(
                unityMesh,
                unityBlendShapeSequence);

            int unresolved = MapUnityVerticesByFingerprintMatch(
                mapping.m_Indices,
                unityFingerprints,
                fbxFingerprints);

            mapping.m_IsValid = unresolved == 0;
            mapping.m_InvalidReason = mapping.m_IsValid
                ? string.Empty
                : $"Extraction mapping has {unresolved} unresolved Unity vertices.";

            if (!mapping.m_IsValid)
            {
                mapping.SetLegacyCache(unityCache);
            }

            return mapping;
        }
#endif

        private static Dictionary<string, UnityBlendShapeData> BuildUnityBlendShapeLookup(IEnumerable<MappingUnityBlendShapeCache> unityBlendShapes)
        {
            return unityBlendShapes?
                .Where(blendShape => blendShape?.m_UnityBlendShapeData != null && !string.IsNullOrEmpty(blendShape.m_Name))
                .GroupBy(blendShape => blendShape.m_Name)
                .ToDictionary(group => group.Key, group => group.First().m_UnityBlendShapeData)
                ?? new Dictionary<string, UnityBlendShapeData>();
        }

        private static void BuildBlendShapeDataSequences(
            IEnumerable<BlendShapeRecord> records,
            IReadOnlyDictionary<string, UnityBlendShapeData> unityBlendShapesByName,
            out string[] blendShapeSequence,
            out FbxBlendShapeData[] fbxBlendShapeSequence,
            out UnityBlendShapeData[] unityBlendShapeSequence)
        {
            var names = new List<string>();
            var fbxBlendShapes = new List<FbxBlendShapeData>();
            var unityBlendShapes = new List<UnityBlendShapeData>();

            foreach (var record in records ?? Enumerable.Empty<BlendShapeRecord>())
            {
                if (record == null ||
                    string.IsNullOrEmpty(record.m_Name) ||
                    record.m_FbxBlendShapeData == null ||
                    unityBlendShapesByName == null ||
                    !unityBlendShapesByName.TryGetValue(record.m_Name, out var unityBlendShapeData) ||
                    unityBlendShapeData == null ||
                    !PositionFingerprintFactory.HasSamples(record.m_FbxBlendShapeData) ||
                    !PositionFingerprintFactory.HasSamples(unityBlendShapeData))
                {
                    continue;
                }

                names.Add(record.m_Name);
                fbxBlendShapes.Add(record.m_FbxBlendShapeData);
                unityBlendShapes.Add(unityBlendShapeData);
            }

            blendShapeSequence = names.ToArray();
            fbxBlendShapeSequence = fbxBlendShapes.ToArray();
            unityBlendShapeSequence = unityBlendShapes.ToArray();
        }

        private static bool HasUnityMesh(Mesh mesh, int requiredVertexCount)
        {
            return mesh != null && requiredVertexCount > 0 && mesh.vertexCount == requiredVertexCount;
        }

        private static bool HasFbxMesh(FbxMeshSnapshot mesh)
        {
            return mesh != null && mesh.ControlPointCount > 0;
        }

        private static int MapUnityVerticesByFingerprintMatch(
            int[] mappingIndices,
            IReadOnlyList<PositionFingerprint> unityFingerprints,
            IReadOnlyList<PositionFingerprint> fbxFingerprints)
        {
            var fbxFingerprintIndex = new PositionFingerprint.CandidateIndex(
                fbxFingerprints,
                FingerprintEpsilon);
            int unresolved = 0;
            for (int unityIndex = 0; unityIndex < unityFingerprints.Count; unityIndex++)
            {
                if (!unityFingerprints[unityIndex].TryFindMatchingCandidateIndex(
                        fbxFingerprintIndex,
                        out int fbxIndex))
                {
                    unresolved++;
                    continue;
                }

                mappingIndices[unityIndex] = fbxIndex;
            }

            return unresolved;
        }

#if ENABLE_FBX_SDK
        private static FbxMeshSnapshot CreateFbxMeshSnapshot(FbxMesh fbxMesh, float importScale)
        {
            int controlPointCount = fbxMesh.GetControlPointsCount();
            var positions = new Vector3[controlPointCount];
            for (int i = 0; i < controlPointCount; i++)
            {
                positions[i] = ToVector3(fbxMesh.GetControlPointAt(i));
            }

            return new FbxMeshSnapshot(
                fbxMesh.GetName(),
                positions,
                System.Array.Empty<BlendShapeRecord>(),
                importScale);
        }

        private static Vector3 ToVector3(FbxVector4 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }
#endif

    }
}
