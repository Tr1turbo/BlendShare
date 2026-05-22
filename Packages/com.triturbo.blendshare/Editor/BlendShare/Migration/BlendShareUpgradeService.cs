using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;
using Triturbo.Fbx;
using Triturbo.Fbx.Ufbx;
using Triturbo.Fbx.Unity;

namespace Triturbo.BlendShare.Migration
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
            UfbxScene originalScene = null;
            var sceneResult = FbxUnityAssetReader.ReadScene(legacyAsset.m_Original);
            if (sceneResult.Success)
            {
                originalScene = sceneResult.Value;
            }

            try
            {
                float importScale = FbxUnityAssetReader.GetImportScale(legacyAsset.m_Original);
                var meshes = new List<MeshDataObject>();
                foreach (var legacyMesh in legacyMeshes)
                {
                    meshPaths.TryGetValue(legacyMesh, out string meshPath);

                    var mesh = ConvertMesh(legacyMesh, meshPath, originalScene, importScale);
                    if (mesh != null)
                    {
                        meshes.Add(mesh);
                    }
                }

                asset.SetMeshes(meshes);
                return asset;
            }
            finally
            {
                originalScene?.Dispose();
            }
        }

        private static MeshDataObject ConvertMesh(
            MeshData legacyMesh,
            string meshPath,
            UfbxScene originalScene,
            float importScale)
        {
            if (legacyMesh == null)
            {
                return null;
            }

            var mesh = ScriptableObject.CreateInstance<MeshDataObject>();
            if (string.IsNullOrEmpty(meshPath))
            {
                return null;
            }

            mesh.Initialize(meshPath, -1);

            var shapeNames = legacyMesh.GetAllBlendShapeNames();
            var records = new List<BlendShapeRecord>();
            foreach (var shapeName in shapeNames)
            {
                var legacyBlendShape = legacyMesh.GetBlendShape(shapeName);
                if (legacyBlendShape == null)
                {
                    continue;
                }

                records.Add(new BlendShapeRecord(shapeName, legacyBlendShape.m_FbxBlendShapeData));
            }

            var blendShapeFeature = ScriptableObject.CreateInstance<BlendShapeFeatureObject>();
            blendShapeFeature.SetBlendShapes(records);
            blendShapeFeature.SetActiveBlendShapeNames(legacyMesh.m_ShapeNames);
            mesh.AddFeature(blendShapeFeature);

            var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(
                mesh.m_Path,
                legacyMesh.m_OriginMesh,
                originalScene,
                importScale,
                out UfbxMesh fbxMesh);

            if (fbxMesh != null && fbxMesh.ControlPointCount > 0)
            {
                mesh.m_FbxControlPointCount = fbxMesh.ControlPointCount;
            }
            else
            {
                mesh.m_FbxControlPointCount = blendShapeFeature.InferFbxControlPointCount();
            }

            mesh.m_Mappings = new[] { mapping };
            return mesh;
        }

        private static Dictionary<MeshData, string> ResolveFbxMeshPaths(GameObject root, IEnumerable<MeshData> legacyMeshes)
        {
            var nodeNamePathLookup = BuildUniqueNodeNamePathLookup(root);
            var meshPaths = new Dictionary<MeshData, string>();
            foreach (var legacyMesh in legacyMeshes ?? Enumerable.Empty<MeshData>())
            {
                if (legacyMesh == null)
                {
                    continue;
                }

                if (legacyMesh.HasNodePath)
                {
                    meshPaths[legacyMesh] = legacyMesh.NodePath;
                    continue;
                }

                string nodeName = MeshNodePath.LeafName(legacyMesh.m_MeshName);
                if (nodeNamePathLookup.TryGetValue(nodeName, out string path))
                {
                    meshPaths[legacyMesh] = path;
                }
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
            var meshPaths = ResolveFbxMeshPaths(legacyAsset.m_Original, legacyAsset.m_MeshDataList);
            foreach (var selection in AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<BlendShapeSelectionSO>())
            {
                var legacyMesh = legacyAsset.m_MeshDataList?
                    .FirstOrDefault(meshData => meshData != null && meshData.m_MeshName == selection.m_MeshName);
                if (legacyMesh == null || !meshPaths.TryGetValue(legacyMesh, out string meshPath))
                {
                    continue;
                }

                var mesh = converted.GetMeshData(meshPath);
                if (mesh == null)
                {
                    continue;
                }

                var blendShapeFeature = mesh.GetFeature<BlendShapeFeatureObject>();
                if (blendShapeFeature == null)
                {
                    continue;
                }

                var indexLookup = blendShapeFeature.BlendShapes
                    .Select((blendShape, index) => new { blendShape, index })
                    .Where(entry => entry.blendShape != null)
                    .ToDictionary(entry => entry.blendShape.m_Name, entry => entry.index);

                var indices = selection.m_BlendShapeNames
                    .Where(indexLookup.ContainsKey)
                    .Select(shapeName => indexLookup[shapeName])
                    .ToList();

                var preset = ScriptableObject.CreateInstance<BlendShapePresetObject>();
                preset.name = selection.DisplayName;
                preset.Set(mesh.m_Path, indices, selection.m_BlendShapeNames);
                presets.Add(preset);
            }

            return presets;
        }

        private static Dictionary<string, string> BuildUniqueNodeNamePathLookup(GameObject root)
        {
            var pathsByNodeName = new Dictionary<string, List<string>>();
            if (root == null)
            {
                return new Dictionary<string, string>();
            }

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null)
                {
                    continue;
                }

                string path = MeshNodePath.GetRelativePath(renderer.transform, root.transform);
                string nodeName = MeshNodePath.LeafName(path);
                if (nodeName == MeshNodePath.Root)
                {
                    continue;
                }

                if (!pathsByNodeName.TryGetValue(nodeName, out var paths))
                {
                    paths = new List<string>();
                    pathsByNodeName[nodeName] = paths;
                }

                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }
            }

            return pathsByNodeName
                .Where(entry => entry.Value.Count == 1)
                .ToDictionary(entry => entry.Key, entry => entry.Value[0]);
        }
    }
}
