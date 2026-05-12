using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Triturbo.FBX;

namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class BlendShareUpgradeService
    {
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

            var meshes = new List<MeshDataObject>();
            foreach (var legacyMesh in legacyMeshes)
            {
                meshPaths.TryGetValue(legacyMesh, out string meshPath);

                var mesh = ConvertMesh(legacyMesh, meshPath, legacyAsset.m_Original);
                if (mesh != null)
                {
                    meshes.Add(mesh);
                }
            }

            asset.SetMeshes(meshes);
            return asset;
        }

        private static MeshDataObject ConvertMesh(MeshData legacyMesh, string meshPath, GameObject originalFbx)
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

            var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(
                mesh.m_MeshPath,
                legacyMesh.m_OriginMesh,
                originalFbx,
                out FbxMeshGeometry fbxMesh);

            if (fbxMesh != null && fbxMesh.ControlPointCount > 0)
            {
                mesh.m_FbxControlPointCount = fbxMesh.ControlPointCount;
            }
            else
            {
                mesh.InferFbxControlPointCount();
            }

            if (!mapping.m_IsValid)
            {
                mapping.SetLegacyCache(cache);
            }

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
