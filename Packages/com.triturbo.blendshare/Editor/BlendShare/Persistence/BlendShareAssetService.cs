using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Persistence
{
    public static class BlendShareAssetService
    {
        public static BlendShareObject Save(
            BlendShareObject source,
            string path,
            IEnumerable<MeshDataObject> meshes,
            IEnumerable<BlendShapePresetObject> presets = null)
        {
            if (source == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets");

            var meshArray = meshes?.Where(mesh => mesh != null).ToArray() ?? System.Array.Empty<MeshDataObject>();
            var presetArray = presets?.Where(preset => preset != null).ToArray() ?? System.Array.Empty<BlendShapePresetObject>();
            var boneGraphs = CollectBoneGraphs(meshArray);
            var existing = AssetDatabase.LoadAssetAtPath<BlendShareObject>(path);
            BlendShareObject asset = existing != null ? existing : source;

            try
            {
                AssetDatabase.StartAssetEditing();
                AssetDatabase.DisallowAutoRefresh();

                if (existing == null)
                {
                    AssetDatabase.CreateAsset(asset, path);
                }
                else if (existing != source)
                {
                    EditorUtility.CopySerialized(source, existing);
                    asset = existing;
                }

                RemoveOldSubAssets(path, asset);

                for (int i = 0; i < boneGraphs.Length; i++)
                {
                    boneGraphs[i].name = GetBoneGraphSubAssetName(i);
                    AddHiddenSubAsset(boneGraphs[i], asset);
                }

                foreach (var mesh in meshArray)
                {
                    mesh.name = GetMeshSubAssetName(mesh);
                    AddHiddenSubAsset(mesh, asset);

                    foreach (var feature in mesh.Features)
                    {
                        if (feature == null)
                        {
                            continue;
                        }

                        feature.name = GetFeatureSubAssetName(mesh, feature);
                        AddHiddenSubAsset(feature, asset);
                    }

                    foreach (var mapping in mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                    {
                        if (mapping == null)
                        {
                            continue;
                        }

                        mapping.name = GetMappingSubAssetName(mesh, mapping);
                        AddHiddenSubAsset(mapping, asset);
                    }
                }

                foreach (var preset in presetArray)
                {
                    preset.name = GetPresetSubAssetName(preset);
                    AddHiddenSubAsset(preset, asset);
                }

                asset.SetMeshes(meshArray);
                EditorUtility.SetDirty(asset);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return asset;
        }

        public static List<BlendShapePresetObject> LoadPresets(BlendShareObject asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path))
            {
                return new List<BlendShapePresetObject>();
            }

            return AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<BlendShapePresetObject>()
                .OrderBy(preset => preset.DisplayName)
                .ToList();
        }

        public static void SaveMappings(BlendShareObject asset)
        {
            if (asset == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                AssetDatabase.DisallowAutoRefresh();

                foreach (var mesh in asset.Meshes ?? System.Array.Empty<MeshDataObject>())
                {
                    if (mesh == null)
                    {
                        continue;
                    }

                    foreach (var mapping in mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                    {
                        if (mapping == null)
                        {
                            continue;
                        }

                        mapping.name = GetMappingSubAssetName(mesh, mapping);
                        AddHiddenSubAsset(mapping, asset);
                    }

                    EditorUtility.SetDirty(mesh);
                }

                EditorUtility.SetDirty(asset);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        public static string GetUniqueSideBySidePath(Object sourceAsset, string suffix = "_v2")
        {
            string sourcePath = AssetDatabase.GetAssetPath(sourceAsset);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return null;
            }

            string directory = Path.GetDirectoryName(sourcePath);
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            return AssetDatabase.GenerateUniqueAssetPath(Path.Combine(directory ?? "Assets", $"{fileName}{suffix}.asset"));
        }

        private static void AddHiddenSubAsset(Object subAsset, Object mainAsset)
        {
            if (subAsset == null || mainAsset == null)
            {
                return;
            }

            subAsset.hideFlags = HideFlags.None;
            if (!AssetDatabase.Contains(subAsset))
            {
                AssetDatabase.AddObjectToAsset(subAsset, mainAsset);
            }
            EditorUtility.SetDirty(subAsset);
        }

        private static BoneGraphObject[] CollectBoneGraphs(IEnumerable<MeshDataObject> meshes)
        {
            return (meshes ?? Enumerable.Empty<MeshDataObject>())
                .SelectMany(mesh => mesh?.Features ?? System.Array.Empty<MeshFeatureObject>())
                .OfType<SkinWeightFeatureObject>()
                .Select(feature => feature.m_BoneGraph)
                .Where(graph => graph != null)
                .Distinct()
                .ToArray();
        }

        private static void RemoveOldSubAssets(string path, BlendShareObject mainAsset)
        {
            foreach (var subAsset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (subAsset == null || subAsset == mainAsset)
                {
                    continue;
                }

                if (subAsset is MeshDataObject ||
                    subAsset is MeshFeatureObject ||
                    subAsset is BoneGraphObject ||
                    subAsset is UnityVertexMappingObject ||
                    subAsset is BlendShapePresetObject)
                {
                    Object.DestroyImmediate(subAsset, true);
                }
            }
        }

        private static string GetBoneGraphSubAssetName(int index)
        {
            return index == 0 ? "BoneGraph" : $"BoneGraph_{index}";
        }

        private static string GetMeshSubAssetName(MeshDataObject mesh)
        {
            return $"Mesh_{SanitizeName(mesh.m_Path)}";
        }

        private static string GetMappingSubAssetName(MeshDataObject mesh, UnityVertexMappingObject mapping)
        {
            return $"Mapping_{SanitizeName(mesh.m_Path)}_{mapping.UnityVerticesHashShort}";
        }

        private static string GetFeatureSubAssetName(MeshDataObject mesh, MeshFeatureObject feature)
        {
            return $"Feature::{feature.FeatureId}@{mesh.m_Path}";
        }

        private static string GetPresetSubAssetName(BlendShapePresetObject preset)
        {
            return $"Preset_{SanitizeName(preset.m_Path)}_{SanitizeName(preset.DisplayName)}";
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Unnamed";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name.Replace('/', '_').Replace('\\', '_');
        }
    }
}
