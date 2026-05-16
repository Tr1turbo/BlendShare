using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Migration;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Persistence
{
    public static class BlendShareGenerationService
    {
        /// <summary>
        /// Creates a generated mesh asset by applying BlendShare features to a target mesh container.
        /// </summary>
        /// <param name="targetMeshContainer">FBX asset, generated mesh asset, or other asset containing target meshes.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="path">Asset path where the generated mesh asset should be saved.</param>
        /// <returns>The saved generated mesh asset, or <c>null</c> when generation fails.</returns>
        public static GeneratedMeshAssetSO CreateMeshAsset(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> blendShares,
            string path)
        {
            var shares = blendShares?.Where(share => share != null).Distinct().ToList() ?? new List<BlendShareObject>();
            if (targetMeshContainer == null || shares.Count == 0)
            {
                return null;
            }

            var pipeline = new MeshFeatureGenerationPipeline();
            var appliedBlendShares = GetAppliedBlendShares(targetMeshContainer, shares);
            GameObject targetFbx = GetTargetFbx(targetMeshContainer);

            if (targetFbx != null && !pipeline.CanApplyToUnityMeshes(targetMeshContainer, shares))
            {
#if ENABLE_FBX_SDK
                if (pipeline.CanApplyToFbx(appliedBlendShares))
                {
                    string folder = System.IO.Path.GetDirectoryName(path) ?? Application.dataPath;
                    string tempAssetPath = System.IO.Path.Combine(folder, $"{targetMeshContainer.name}-{System.Guid.NewGuid()}.fbx");
                    if (!CreateFbx(targetFbx, appliedBlendShares, tempAssetPath, true))
                    {
                        Debug.LogError("Failed to create blendshapes fbx.");
                        return null;
                    }

                    var result = GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(
                        targetFbx,
                        appliedBlendShares,
                        tempAssetPath,
                        path);
                    AssetDatabase.MoveAssetToTrash(tempAssetPath);
                    return result;
                }
#endif
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            bool generationSucceeded = pipeline.TryGenerateUnityMeshes(
                targetMeshContainer,
                shares,
                out var generatedMeshes,
                out var generatedObjects);
            if (!generationSucceeded)
            {
                RemoveGeneratedObjects(generatedObjects);
                return null;
            }

            return GeneratedMeshAssetSO.SaveBlendShareMeshesToAsset(
                targetFbx,
                appliedBlendShares,
                generatedMeshes,
                generatedObjects,
                path);
        }

        /// <summary>
        /// Checks whether every mesh feature in the supplied BlendShare assets can apply to the target meshes.
        /// </summary>
        /// <param name="blendShares">BlendShare assets to validate.</param>
        /// <param name="meshes">Target meshes to validate against.</param>
        /// <returns><c>true</c> when every feature can be applied directly to the Unity meshes.</returns>
        public static bool IsAllMeshesValid(IEnumerable<BlendShareObject> blendShares, IEnumerable<Mesh> meshes)
        {
            return new MeshFeatureGenerationPipeline().CanApplyToUnityMeshes(blendShares, meshes);
        }

#if ENABLE_FBX_SDK
        /// <summary>
        /// Creates an FBX file by applying BlendShare features to a source FBX asset.
        /// </summary>
        /// <param name="source">Source FBX asset to import and modify.</param>
        /// <param name="blendShares">BlendShare assets to apply.</param>
        /// <param name="outputPath">Destination FBX asset path, or the source asset path when omitted.</param>
        /// <param name="onlyNecessary">Whether to remove unmodified mesh nodes before export.</param>
        /// <returns><c>true</c> when the FBX file was exported successfully.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false)
        {
            return new MeshFeatureGenerationPipeline().CreateFbx(source, blendShares, outputPath, onlyNecessary);
        }

        /// <summary>
        /// Removes BlendShare-generated blendshapes from a target FBX asset.
        /// </summary>
        /// <param name="share">BlendShare asset describing blendshapes to remove.</param>
        /// <param name="target">Target FBX asset to modify.</param>
        /// <param name="removeInAllDeformer">Whether removal should scan all blendshape deformers.</param>
        /// <returns><c>true</c> when the modified FBX file was exported successfully.</returns>
        public static bool RemoveBlendShapes(
            BlendShareObject share,
            GameObject target,
            bool removeInAllDeformer = true)
        {
            return new MeshFeatureGenerationPipeline().RemoveBlendShapes(share, target, removeInAllDeformer);
        }
#else
        /// <summary>
        /// Stub used when the Autodesk FBX SDK package is not available.
        /// </summary>
        /// <param name="source">Unused source FBX asset.</param>
        /// <param name="blendShares">Unused BlendShare assets.</param>
        /// <param name="outputPath">Unused output path.</param>
        /// <param name="onlyNecessary">Unused pruning flag.</param>
        /// <returns>Always <c>false</c> without FBX SDK support.</returns>
        public static bool CreateFbx(
            GameObject source,
            IEnumerable<BlendShareObject> blendShares,
            string outputPath = null,
            bool onlyNecessary = false)
        {
            return false;
        }

        /// <summary>
        /// Stub used when the Autodesk FBX SDK package is not available.
        /// </summary>
        /// <param name="share">Unused BlendShare asset.</param>
        /// <param name="target">Unused target FBX asset.</param>
        /// <param name="removeInAllDeformer">Unused removal option.</param>
        /// <returns>Always <c>false</c> without FBX SDK support.</returns>
        public static bool RemoveBlendShapes(
            BlendShareObject share,
            GameObject target,
            bool removeInAllDeformer = true)
        {
            return false;
        }
#endif

        private static List<BlendShareObject> GetAppliedBlendShares(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> shares)
        {
            var appliedBlendShares = new List<BlendShareObject>();

            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                if (generatedAsset.m_AppliedBlendShares != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShares.Where(share => share != null));
                }

                if (generatedAsset.m_AppliedBlendShapes != null)
                {
                    appliedBlendShares.AddRange(generatedAsset.m_AppliedBlendShapes
                        .Where(legacy => legacy != null)
                        .Select(BlendShareUpgradeService.UpgradeSideBySide)
                        .Where(share => share != null));
                }
            }

            appliedBlendShares.AddRange(shares ?? Enumerable.Empty<BlendShareObject>());
            return appliedBlendShares
                .Where(share => share != null)
                .Distinct()
                .ToList();
        }

        private static GameObject GetTargetFbx(Object targetMeshContainer)
        {
            if (targetMeshContainer is GeneratedMeshAssetSO generatedAsset)
            {
                return generatedAsset.m_OriginalFbxGo;
            }

            return targetMeshContainer as GameObject;
        }

        private static void RemoveGeneratedObjects(IEnumerable<Object> generatedObjects)
        {
            foreach (var generatedObject in generatedObjects ?? Enumerable.Empty<Object>())
            {
                if (generatedObject == null || AssetDatabase.Contains(generatedObject))
                {
                    continue;
                }

                Object.DestroyImmediate(generatedObject);
            }
        }
    }
}
