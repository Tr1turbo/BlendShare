using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Hashing;
using CurrentFbxBlendShapeData = Triturbo.BlendShare.Features.BlendShapes.FbxBlendShapeData;
using CurrentFbxBlendShapeFrame = Triturbo.BlendShare.Features.BlendShapes.FbxBlendShapeFrame;
using LegacyFbxBlendShapeData = Triturbo.BlendShapeShare.BlendShapeData.FbxBlendShapeData;
using LegacyFbxBlendShapeFrame = Triturbo.BlendShapeShare.BlendShapeData.FbxBlendShapeFrame;
//
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
            ConvertSelections(legacyAsset, converted);
            return BlendShareAssetService.Save(converted, path, converted.Meshes);
        }

        public static BlendShareObject ConvertLegacy(BlendShapeDataSO legacyAsset)
        {
            if (legacyAsset == null)
            {
                return null;
            }

            var asset = ScriptableObject.CreateInstance<BlendShareObject>();
            asset.name = legacyAsset.name;
            asset.m_Target = legacyAsset.m_Original;
            asset.m_DefaultGeneratedAssetName = legacyAsset.m_DefaultGeneratedAssetName;
            asset.m_Applied = legacyAsset.m_Applied;
            asset.m_PatchId = legacyAsset.m_DeformerID;

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
                bool bakeAxisConversion = FbxUnityAssetReader.GetBakeAxisConversion(legacyAsset.m_Original);
                Matrix4x4 importerSpaceTransform = FbxUnityAssetReader.GetImporterSpaceTransform(legacyAsset.m_Original);
                var meshes = new List<MeshDataObject>();
                foreach (var legacyMesh in legacyMeshes)
                {
                    meshPaths.TryGetValue(legacyMesh, out string meshPath);

                    var mesh = ConvertMesh(legacyMesh, meshPath, originalScene, importScale, bakeAxisConversion, importerSpaceTransform);
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
            float importScale,
            bool bakeAxisConversion,
            Matrix4x4 importerSpaceTransform)
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
            var records = new List<CurrentFbxBlendShapeData>();
            foreach (var shapeName in shapeNames)
            {
                var legacyBlendShape = legacyMesh.GetBlendShape(shapeName);
                if (legacyBlendShape == null)
                {
                    continue;
                }

                var data = ConvertLegacyBlendShapeData(legacyBlendShape.m_FbxBlendShapeData);
                if (data == null)
                {
                    continue;
                }

                data.m_Name = shapeName;
                records.Add(data);
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
                bakeAxisConversion,
                importerSpaceTransform,
                out UfbxMesh fbxMesh);

            if (fbxMesh != null && fbxMesh.ControlPointCount > 0)
            {
                mesh.m_FbxTopologySignature = FbxTopologyHash.Calculate(fbxMesh);
            }
            else
            {
                int inferredCount = blendShapeFeature.InferFbxControlPointCount();
                mesh.m_FbxTopologySignature = new FbxTopologySignature(string.Empty, inferredCount, -1, false);
            }

            mesh.m_Mappings = new[] { mapping };
            return mesh;
        }

        private static CurrentFbxBlendShapeData ConvertLegacyBlendShapeData(LegacyFbxBlendShapeData legacyData)
        {
            var legacyFrames = legacyData?.m_Frames;
            if (legacyFrames == null)
            {
                return null;
            }

            var frames = new CurrentFbxBlendShapeFrame[legacyFrames.Length];
            for (int frameIndex = 0; frameIndex < legacyFrames.Length; frameIndex++)
            {
                float frameWeight = 100f * (frameIndex + 1) / legacyFrames.Length;
                frames[frameIndex] = ConvertLegacyBlendShapeFrame(legacyFrames[frameIndex], frameWeight);
            }

            return new CurrentFbxBlendShapeData(frames);
        }

        private static CurrentFbxBlendShapeFrame ConvertLegacyBlendShapeFrame(LegacyFbxBlendShapeFrame legacyFrame, float frameWeight)
        {
            var frame = new CurrentFbxBlendShapeFrame(frameWeight);
            if (legacyFrame == null)
            {
                return frame;
            }

            legacyFrame.MigrateLegacyVectors();
            int count = System.Math.Min(
                legacyFrame.m_PointsIndices?.Count ?? 0,
                legacyFrame.m_DeltaControlPointsList?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                var legacyDelta = legacyFrame.m_DeltaControlPointsList[i];
                var delta = legacyDelta == null
                    ? Triturbo.BlendShare.Fbx.Vector3d.zero
                    : new Triturbo.BlendShare.Fbx.Vector3d(legacyDelta.m_X, legacyDelta.m_Y, legacyDelta.m_Z);
                if (!delta.IsZero())
                {
                    frame.SetDeltaPositionAt(legacyFrame.m_PointsIndices[i], delta);
                }
            }

            return frame;
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

                string nodeName = MeshNodePath.LeafName(legacyMesh.m_MeshName);
                if (nodeNamePathLookup.TryGetValue(nodeName, out string path))
                {
                    meshPaths[legacyMesh] = path;
                }
            }

            return meshPaths;
        }

        private static void ConvertSelections(BlendShapeDataSO legacyAsset, BlendShareObject converted)
        {
            string path = AssetDatabase.GetAssetPath(legacyAsset);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

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

                var originalActive = blendShapeFeature.ActiveBlendShapeIndices.ToArray();
                string originalSelectionSetId = blendShapeFeature.ActiveSelectionSetId;
                blendShapeFeature.SetWorkingSelection(indices, string.Empty);
                blendShapeFeature.SaveSelectionSet(selection.DisplayName);
                blendShapeFeature.SetWorkingSelection(originalActive, originalSelectionSetId);
            }
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
