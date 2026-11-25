using System;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Assets;
using UnityEditor;

namespace Triturbo.BlendShare.EditorAssets.Settings
{
    public static class EffectiveSettingsResolver
    {
        public static EffectiveSettingsSnapshot Resolve(BlendShareAsset patch) =>
            ResolveByGuid(patch ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(patch)) : null);

        public static EffectiveSettingsSnapshot ResolveByGuid(string guid)
        {
            var buffer = BlendShareSettingsSession.FindBuffer(guid);
            string path = string.IsNullOrEmpty(guid) ? string.Empty : AssetDatabase.GUIDToAssetPath(guid);
            var settings = new BlendShareImportSettings();
            if (buffer)
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(buffer.Settings), settings);
            else if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is BlendShareImporter importer)
                settings = importer.GetSettingsCopy();

            return new EffectiveSettingsSnapshot(settings, BlendShareSettingsSession.GetSettingsRevision(guid),
                buffer && buffer.IsDirty, BlendShareSettingsSession.IsAssetReadOnly(path, out _),
                buffer && buffer.HasExternalConflict);
        }

        public static BlendShareFeatureSettings FindFeatureSettings(BlendShareImportSettings settings,
            string meshId, string featureId)
        {
            var mesh = settings?.Meshes.Find(item => item != null && item.MeshId == meshId);
            return mesh?.Features.Find(item => item != null && item.FeatureId == featureId);
        }

        public static T FindOrCreateFeatureSettings<T>(BlendShareImportSettings settings, string meshId,
            string featureId) where T : BlendShareFeatureSettings, new()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var existing = FindFeatureSettings(settings, meshId, featureId);
            if (existing is T typed) return typed;
            if (existing != null) throw new InvalidOperationException($"Incompatible settings for {meshId}/{featureId}.");

            var mesh = settings.Meshes.Find(item => item != null && item.MeshId == meshId);
            if (mesh == null)
            {
                mesh = new BlendShareMeshImportSettings { MeshId = meshId };
                settings.Meshes.Add(mesh);
            }
            var feature = new T { FeatureId = featureId };
            mesh.Features.Add(feature);
            return feature;
        }
    }
}
