using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

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

            var meshes = new List<MeshDataObject>();
            foreach (var legacyMesh in legacyAsset.m_MeshDataList ?? new List<MeshData>())
            {
                var mesh = ConvertMesh(legacyAsset, legacyMesh);
                if (mesh != null)
                {
                    meshes.Add(mesh);
                }
            }

            asset.SetMeshes(meshes);
            return asset;
        }

        private static MeshDataObject ConvertMesh(BlendShapeDataSO legacyAsset, MeshData legacyMesh)
        {
            if (legacyMesh == null)
            {
                return null;
            }

            var mesh = ScriptableObject.CreateInstance<MeshDataObject>();
            string meshPath = FindFbxMeshPath(legacyAsset.m_Original, legacyMesh.m_MeshName);
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

            FbxMeshSnapshot fbxMesh = TryReadFbxMesh(
                legacyAsset.m_Original,
                mesh.m_MeshPath,
                mesh.m_MeshName);
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

        private static FbxMeshSnapshot TryReadFbxMesh(GameObject fbxAsset, string meshPath, string meshName)
        {
#if ENABLE_FBX_SDK
            if (FbxMeshReader.TryReadMesh(fbxAsset, meshPath, out var snapshot) ||
                FbxMeshReader.TryReadMesh(fbxAsset, meshName, out snapshot))
            {
                return snapshot;
            }
#endif
            return null;
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

        private static string FindFbxMeshPath(GameObject root, string meshName)
        {
            if (root == null || string.IsNullOrEmpty(meshName))
            {
                return meshName;
            }

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null && renderer.sharedMesh.name == meshName)
                {
                    return GetRelativePath(renderer.transform, root.transform);
                }
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
