using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShapeShare.BlendShapeData
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

                foreach (var mesh in meshArray)
                {
                    mesh.name = GetMeshSubAssetName(mesh);
                    AddHiddenSubAsset(mesh, asset);

                    foreach (var mapping in mesh.m_Mappings ?? System.Array.Empty<UnityMeshVerticesMappingObject>())
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

        private static void RemoveOldSubAssets(string path, BlendShareObject mainAsset)
        {
            foreach (var subAsset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (subAsset == null || subAsset == mainAsset)
                {
                    continue;
                }

                if (subAsset is MeshDataObject ||
                    subAsset is UnityMeshVerticesMappingObject ||
                    subAsset is BlendShapePresetObject)
                {
                    Object.DestroyImmediate(subAsset, true);
                }
            }
        }

        private static string GetMeshSubAssetName(MeshDataObject mesh)
        {
            return $"Mesh_{SanitizeName(string.IsNullOrEmpty(mesh.m_MeshPath) ? mesh.m_MeshName : mesh.m_MeshPath)}";
        }

        private static string GetMappingSubAssetName(MeshDataObject mesh, UnityMeshVerticesMappingObject mapping)
        {
            string source = string.IsNullOrEmpty(mapping.m_UnityRendererPath) ? mesh.m_MeshPath : mapping.m_UnityRendererPath;
            return $"Mapping_{SanitizeName(source)}_{mapping.m_UnityVerticesHash}";
        }

        private static string GetPresetSubAssetName(BlendShapePresetObject preset)
        {
            return $"Preset_{SanitizeName(preset.m_MeshPath)}_{SanitizeName(preset.DisplayName)}";
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
