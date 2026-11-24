using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Triturbo.BlendShare.EditorAssets
{
    /// <summary>
    /// Unity 2022.3 ScriptedImporter for BLSHARE1 v5 .blendshare files.
    /// - Produces only 1 ultra-thin BlendShareAsset (TOC pointer cache) + N thin BlendShareMeshAsset handles.
    /// - Accelerator-safe (AllowCaching = true, deterministic artifact contents, zero paths/InstanceIDs/timestamps).
    /// - Stores project-local Unity Settings in .blendshare.meta via m_Settings.
    /// </summary>
    [ScriptedImporter(2, "blendshare", AllowCaching = true)]
    public sealed class BlendShareImporter : ScriptedImporter
    {
        private const string k_PatchIconPath = "Packages/com.triturbo.blendshare/Icons/BlendShapesDataIcon.png";
        private const string k_MeshIconPath = "Packages/com.triturbo.blendshare/Icons/MeshAssetsIcon.png";

        [SerializeField]
        private BlendShareImportSettings m_Settings = new BlendShareImportSettings();

        public BlendShareImportSettings GetSettingsCopy()
        {
            var copy = new BlendShareImportSettings();
            if (m_Settings != null)
            {
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(m_Settings), copy);
            }
            return copy;
        }

        public void SetSettingsCopy(BlendShareImportSettings settings)
        {
            var copy = new BlendShareImportSettings();
            if (settings != null)
            {
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(settings), copy);
            }
            m_Settings = copy;
        }

        public string ComputeSettingsJson()
        {
            return EditorJsonUtility.ToJson(m_Settings ?? new BlendShareImportSettings());
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string physicalPath = BlendSharePathUtility.ResolvePhysicalPath(ctx.assetPath);
            if (string.IsNullOrEmpty(physicalPath) || !File.Exists(physicalPath))
            {
                ctx.LogImportError($"BlendShare source file not found at '{ctx.assetPath}' (resolved '{physicalPath}').");
                return;
            }

            var reader = new BlendShareContainerReader();
            ContainerToc toc;
            var meshNamesById = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var tocResult = reader.ReadToc(stream);
                if (!tocResult.Success)
                {
                    ctx.LogImportError($"Failed to import BlendShare v5 container '{ctx.assetPath}': {tocResult.Status} - {tocResult.Message}");
                    return;
                }

                toc = tocResult.Value;
                for (int i = 0; i < toc.Meshes.Count; i++)
                {
                    string meshId = toc.Meshes[i].MeshId;
                    var headerResult = reader.ReadMeshHeader(stream, toc, meshId);
                    if (!headerResult.Success || headerResult.Value == null)
                    {
                        ctx.LogImportError($"Failed to validate mesh header '{meshId}' in '{ctx.assetPath}': {headerResult.Status} - {headerResult.Message}");
                        return;
                    }

                    meshNamesById[meshId] = string.IsNullOrWhiteSpace(headerResult.Value.Name)
                        ? meshId
                        : headerResult.Value.Name;
                }
            }

            var rootIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(k_PatchIconPath);
            var meshIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(k_MeshIconPath);

            var rootAsset = ScriptableObject.CreateInstance<BlendShareAsset>();
            rootAsset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            BlendShareAssetTocMapper.PopulateAsset(rootAsset, toc, meshNamesById);

            if (rootIcon != null) ctx.AddObjectToAsset("blendshare-root", rootAsset, rootIcon);
            else ctx.AddObjectToAsset("blendshare-root", rootAsset);
            ctx.SetMainObject(rootAsset);

            for (int i = 0; i < rootAsset.Meshes.Count; i++)
            {
                var meshToc = rootAsset.Meshes[i];
                var meshHandle = ScriptableObject.CreateInstance<BlendShareMeshAsset>();
                meshHandle.name = string.IsNullOrWhiteSpace(meshToc.Name) ? meshToc.MeshId : meshToc.Name;
                meshHandle.InitializeHandle(rootAsset, meshToc.MeshId);

                string stableId = "blendshare-mesh:" + meshToc.MeshId;
                if (meshIcon != null) ctx.AddObjectToAsset(stableId, meshHandle, meshIcon);
                else ctx.AddObjectToAsset(stableId, meshHandle);
            }
        }
    }
}
