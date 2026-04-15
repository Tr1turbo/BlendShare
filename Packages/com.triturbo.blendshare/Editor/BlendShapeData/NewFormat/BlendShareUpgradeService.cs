using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Triturbo.BlendShapeShare.FbxReader;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class BlendShareUpgradeService
    {
        private const string LogPrefix = "[BlendShare Upgrade]";

        public static BlendShareObject UpgradeSideBySide(BlendShapeDataSO legacyAsset)
        {
            if (legacyAsset == null)
            {
                return null;
            }

            string legacyPath = AssetDatabase.GetAssetPath(legacyAsset);
            if (!string.IsNullOrEmpty(legacyPath))
            {
                string directory = System.IO.Path.GetDirectoryName(legacyPath);
                string fileName = System.IO.Path.GetFileNameWithoutExtension(legacyPath);
                string preferredPath = System.IO.Path.Combine(directory ?? "Assets", $"{fileName}_v2.asset");
                var existing = AssetDatabase.LoadAssetAtPath<BlendShareObject>(preferredPath);
                if (existing != null)
                {
                    return existing;
                }
            }

            string path = BlendShareAssetService.GetUniqueSideBySidePath(legacyAsset);
            if (string.IsNullOrEmpty(path))
            {
                return ConvertLegacy(legacyAsset);
            }

            var converted = ConvertLegacy(legacyAsset);
            var presets = ConvertSelections(legacyAsset, converted);
            return BlendShareAssetService.Save(converted, path, converted.Meshes, presets);
        }

        public static BlendShareObject ConvertLegacy(BlendShapeDataSO legacyAsset)
        {
            if (legacyAsset == null)
            {
                return null;
            }

            var asset = ScriptableObject.CreateInstance<BlendShareObject>();
            asset.name = legacyAsset.name;
            asset.m_Original = legacyAsset.m_Original;
            asset.m_DefaultGeneratedAssetName = legacyAsset.m_DefaultGeneratedAssetName;
            asset.m_Applied = legacyAsset.m_Applied;
            asset.m_DeformerID = legacyAsset.m_DeformerID;

            var legacyMeshes = legacyAsset.m_MeshDataList?
                .Where(legacyMesh => legacyMesh != null)
                .ToList() ?? new List<MeshData>();
            var meshPaths = ResolveFbxMeshPaths(legacyAsset.m_Original, legacyMeshes);
            var fbxMeshes = TryReadFbxMeshes(legacyAsset.m_Original, meshPaths);

            var meshes = new List<MeshDataObject>();
            foreach (var legacyMesh in legacyMeshes)
            {
                meshPaths.TryGetValue(legacyMesh, out string meshPath);
                TryGetFbxMesh(fbxMeshes, meshPath, legacyMesh.m_MeshName, out var fbxMesh);

                var mesh = ConvertMesh(legacyMesh, meshPath, fbxMesh);
                if (mesh != null)
                {
                    meshes.Add(mesh);
                }
            }

            asset.SetMeshes(meshes);
            return asset;
        }

        private static MeshDataObject ConvertMesh(MeshData legacyMesh, string meshPath, FbxMeshSnapshot fbxMesh)
        {
            if (legacyMesh == null)
            {
                return null;
            }

            var mesh = ScriptableObject.CreateInstance<MeshDataObject>();
            mesh.Initialize(
                string.IsNullOrEmpty(meshPath) ? legacyMesh.m_MeshName : meshPath,
                legacyMesh.m_MeshName,
                -1);

            var shapeNames = legacyMesh.GetAllBlendShapeNames();
            var records = new List<BlendShapeRecord>();
            var cache = new List<MappingUnityBlendShapeCache>();
            foreach (var shapeName in shapeNames)
            {
                var legacyBlendShape = legacyMesh.GetBlendShape(shapeName);
                if (legacyBlendShape == null)
                {
                    continue;
                }

                records.Add(new BlendShapeRecord(shapeName, legacyBlendShape.m_FbxBlendShapeData));
                if (legacyBlendShape.m_UnityBlendShapeData != null)
                {
                    cache.Add(new MappingUnityBlendShapeCache(shapeName, legacyBlendShape.m_UnityBlendShapeData));
                }
            }

            mesh.SetBlendShapes(records);
            mesh.SetActiveBlendShapeNames(legacyMesh.m_ShapeNames);

            if (fbxMesh != null && fbxMesh.ControlPointCount > 0)
            {
                mesh.m_FbxControlPointCount = fbxMesh.ControlPointCount;
            }
            else
            {
                mesh.InferFbxControlPointCount();
            }

            var mapping = UnityFbxVertexMappingBuilder.BuildFromLegacy(
                mesh.m_MeshPath,
                legacyMesh.m_VertexCount,
                legacyMesh.m_VerticesHash,
                records,
                cache,
                legacyMesh.m_OriginMesh,
                fbxMesh);
            mapping.m_UnityMesh = legacyMesh.m_OriginMesh;
            mesh.m_Mappings = new[] { mapping };
            return mesh;
        }

        private static Dictionary<MeshData, string> ResolveFbxMeshPaths(GameObject root, IEnumerable<MeshData> legacyMeshes)
        {
            var rendererPathLookup = BuildFbxMeshPathLookup(root);
            var meshPaths = new Dictionary<MeshData, string>();
            foreach (var legacyMesh in legacyMeshes ?? Enumerable.Empty<MeshData>())
            {
                if (legacyMesh == null)
                {
                    continue;
                }

                meshPaths[legacyMesh] = FindFbxMeshPath(rendererPathLookup, legacyMesh.m_MeshName);
            }

            return meshPaths;
        }

        private static Dictionary<string, FbxMeshSnapshot> TryReadFbxMeshes(GameObject fbxAsset, Dictionary<MeshData, string> meshPaths)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var candidates = new List<string>();
            var seenCandidates = new HashSet<string>();
            foreach (var entry in meshPaths ?? new Dictionary<MeshData, string>())
            {
                AddFbxMeshReadCandidate(candidates, seenCandidates, entry.Value);
                AddFbxMeshReadCandidate(candidates, seenCandidates, entry.Key?.m_MeshName);
            }

            Debug.Log(
                $"{LogPrefix} TryReadFbxMeshes start: fbx='{FormatLogTarget(fbxAsset)}', candidates={candidates.Count}");

            var snapshots = BinaryFbxMeshReader.TryReadMeshes(fbxAsset, candidates);

            Debug.Log(
                $"{LogPrefix} TryReadFbxMeshes finished in {stopwatch.Elapsed.TotalMilliseconds:0.###} ms (found={snapshots.Count}/{candidates.Count})");

            return snapshots;
        }

        private static void AddFbxMeshReadCandidate(List<string> candidates, HashSet<string> seenCandidates, string candidate)
        {
            if (!string.IsNullOrEmpty(candidate) && seenCandidates.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        private static string FormatLogTarget(GameObject target)
        {
            return target == null ? "<null>" : target.name;
        }

        private static string FormatLogTarget(string target)
        {
            return string.IsNullOrEmpty(target) ? "<empty>" : target;
        }

        private static bool TryGetFbxMesh(
            Dictionary<string, FbxMeshSnapshot> fbxMeshes,
            string meshPath,
            string meshName,
            out FbxMeshSnapshot snapshot)
        {
            snapshot = null;
            if (fbxMeshes == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(meshPath) && fbxMeshes.TryGetValue(meshPath, out snapshot))
            {
                return true;
            }

            return !string.Equals(meshPath, meshName, System.StringComparison.Ordinal) &&
                   !string.IsNullOrEmpty(meshName) &&
                   fbxMeshes.TryGetValue(meshName, out snapshot);
        }

        private static List<BlendShapePresetObject> ConvertSelections(BlendShapeDataSO legacyAsset, BlendShareObject converted)
        {
            string path = AssetDatabase.GetAssetPath(legacyAsset);
            if (string.IsNullOrEmpty(path))
            {
                return new List<BlendShapePresetObject>();
            }

            var presets = new List<BlendShapePresetObject>();
            foreach (var selection in AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<BlendShapeSelectionSO>())
            {
                var mesh = converted.GetMeshData(selection.m_MeshName);
                if (mesh == null)
                {
                    continue;
                }

                var indexLookup = mesh.BlendShapes
                    .Select((blendShape, index) => new { blendShape, index })
                    .Where(entry => entry.blendShape != null)
                    .ToDictionary(entry => entry.blendShape.m_Name, entry => entry.index);

                var indices = selection.m_BlendShapeNames
                    .Where(indexLookup.ContainsKey)
                    .Select(shapeName => indexLookup[shapeName])
                    .ToList();

                var preset = ScriptableObject.CreateInstance<BlendShapePresetObject>();
                preset.name = selection.DisplayName;
                preset.Set(mesh.m_MeshPath, indices, selection.m_BlendShapeNames);
                presets.Add(preset);
            }

            return presets;
        }

        private static Dictionary<string, string> BuildFbxMeshPathLookup(GameObject root)
        {
            var lookup = new Dictionary<string, string>();
            if (root == null)
            {
                return lookup;
            }

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || string.IsNullOrEmpty(renderer.sharedMesh.name))
                {
                    continue;
                }

                if (!lookup.ContainsKey(renderer.sharedMesh.name))
                {
                    lookup.Add(renderer.sharedMesh.name, GetRelativePath(renderer.transform, root.transform));
                }
            }

            return lookup;
        }

        private static string FindFbxMeshPath(Dictionary<string, string> rendererPathLookup, string meshName)
        {
            if (string.IsNullOrEmpty(meshName))
            {
                return meshName;
            }

            if (rendererPathLookup != null && rendererPathLookup.TryGetValue(meshName, out string meshPath))
            {
                return meshPath;
            }

            return meshName;
        }

        private static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
            {
                return string.Empty;
            }

            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }
    }
}
