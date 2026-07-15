using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightExtractionOptions : MeshFeatureExtractionOptions
    {
        private readonly Dictionary<string, HashSet<string>> selectedWeightBonePathsByMesh = new();
        private readonly Dictionary<string, HashSet<string>> selectedBindposeBonePathsByMesh = new();
        private readonly HashSet<string> selectedTransformBonePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> selectedNewBonePaths = new(StringComparer.Ordinal);
        private bool defaultsInitialized;

        public override string FeatureId => SkinWeightFeatureObject.Id;

        public SkinWeightExtractionOptions()
        {
            Enabled = false;
        }

        public override bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled &&
                   (selectedTransformBonePaths.Count > 0 ||
                    selectedNewBonePaths.Count > 0 ||
                    (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                    .Any(request => ShouldExtractMesh(request)));
        }

        public override bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            return Enabled &&
                   mesh != null &&
                   (selectedTransformBonePaths.Count > 0 ||
                    selectedNewBonePaths.Count > 0 ||
                    GetSelectedWeightBonePaths(mesh.Path).Count > 0 ||
                    GetSelectedBindposeBonePaths(mesh.Path).Count > 0);
        }

        public bool IsWeightClusterSelected(string meshPath, string bonePath)
        {
            return TryGetSelectedSet(selectedWeightBonePathsByMesh, meshPath, out var selected) &&
                   selected.Contains(NormalizeBonePath(bonePath));
        }

        public void SetWeightClusterSelected(string meshPath, string bonePath, bool selected)
        {
            SetMeshBoneSelection(selectedWeightBonePathsByMesh, meshPath, bonePath, selected);
        }

        public IReadOnlyCollection<string> GetSelectedWeightBonePaths(string meshPath)
        {
            return TryGetSelectedSet(selectedWeightBonePathsByMesh, meshPath, out var selected)
                ? selected.ToArray()
                : Array.Empty<string>();
        }

        public bool IsBindposeSelected(string meshPath, string bonePath)
        {
            return TryGetSelectedSet(selectedBindposeBonePathsByMesh, meshPath, out var selected) &&
                   selected.Contains(NormalizeBonePath(bonePath));
        }

        public void SetBindposeSelected(string meshPath, string bonePath, bool selected)
        {
            SetMeshBoneSelection(selectedBindposeBonePathsByMesh, meshPath, bonePath, selected);
        }

        public IReadOnlyCollection<string> GetSelectedBindposeBonePaths(string meshPath)
        {
            return TryGetSelectedSet(selectedBindposeBonePathsByMesh, meshPath, out var selected)
                ? selected.ToArray()
                : Array.Empty<string>();
        }

        public bool IsBoneTransformSelected(string bonePath)
        {
            return selectedTransformBonePaths.Contains(NormalizeBonePath(bonePath));
        }

        public void SetBoneTransformSelected(string bonePath, bool selected)
        {
            string normalized = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedTransformBonePaths.Add(normalized);
            }
            else
            {
                selectedTransformBonePaths.Remove(normalized);
            }
        }

        public IReadOnlyCollection<string> GetSelectedBoneTransformPaths()
        {
            return selectedTransformBonePaths.ToArray();
        }

        public bool IsNewBoneSelected(string bonePath)
        {
            return selectedNewBonePaths.Contains(NormalizeBonePath(bonePath));
        }

        public void SetNewBoneSelected(string bonePath, bool selected)
        {
            string normalized = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedNewBonePaths.Add(normalized);
            }
            else
            {
                selectedNewBonePaths.Remove(normalized);
            }
        }

        public IReadOnlyCollection<string> GetSelectedNewBonePaths()
        {
            return selectedNewBonePaths.ToArray();
        }

        internal void EnsureDefaultSelection(SkinWeightFbxComparison comparison)
        {
            if (defaultsInitialized || comparison == null)
            {
                return;
            }

            defaultsInitialized = true;
            foreach (var bone in comparison.Bones ?? Array.Empty<SkinWeightBoneComparison>())
            {
                bool selectedWeightForBone = false;
                foreach (var cluster in bone.WeightClusters ?? Array.Empty<SkinWeightMeshClusterComparison>())
                {
                    if (cluster.Status != SkinWeightComparisonStatus.New &&
                        cluster.Status != SkinWeightComparisonStatus.Changed)
                    {
                        continue;
                    }

                    SetWeightClusterSelected(cluster.MeshPath, bone.BonePath, true);
                    selectedWeightForBone = true;
                }

                foreach (var bindPose in bone.Bindposes ?? Array.Empty<SkinWeightBindposeComparison>())
                {
                    if ((bindPose.Status == SkinWeightComparisonStatus.New ||
                         bindPose.Status == SkinWeightComparisonStatus.Changed) &&
                        IsWeightClusterSelected(bindPose.MeshPath, bone.BonePath))
                    {
                        SetBindposeSelected(bindPose.MeshPath, bone.BonePath, true);
                    }
                }

                if (selectedWeightForBone && bone.RequiresCreateBone)
                {
                    SetNewBoneSelected(bone.BonePath, true);
                    SetBoneTransformSelected(bone.BonePath, true);
                }
                else if (bone.Transform?.Status == SkinWeightComparisonStatus.Changed)
                {
                    SetBoneTransformSelected(bone.BonePath, true);
                }
            }

            EnsureRequiredParentSelections(comparison);
        }

        internal void EnsureRequiredParentSelections(SkinWeightFbxComparison comparison)
        {
            if (comparison == null)
            {
                return;
            }

            foreach (string path in selectedNewBonePaths.ToArray())
            {
                string parent = MeshNodePath.ParentPath(path);
                while (parent != MeshNodePath.Root)
                {
                    if (comparison.TryGetBone(parent, out var parentBone) &&
                        parentBone.RequiresCreateBone)
                    {
                        selectedNewBonePaths.Add(parent);
                        selectedTransformBonePaths.Add(parent);
                    }

                    parent = MeshNodePath.ParentPath(parent);
                }
            }
        }

        private static bool TryGetSelectedSet(
            Dictionary<string, HashSet<string>> selectionsByMesh,
            string meshPath,
            out HashSet<string> selected)
        {
            return selectionsByMesh.TryGetValue(
                MeshFeatureExtractionSession.BuildMeshKey(meshPath),
                out selected);
        }

        private static void SetMeshBoneSelection(
            Dictionary<string, HashSet<string>> selectionsByMesh,
            string meshPath,
            string bonePath,
            bool selected)
        {
            string meshKey = MeshFeatureExtractionSession.BuildMeshKey(meshPath);
            if (!selectionsByMesh.TryGetValue(meshKey, out var selectedBones))
            {
                selectedBones = new HashSet<string>(StringComparer.Ordinal);
                selectionsByMesh[meshKey] = selectedBones;
            }

            string normalizedBonePath = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedBones.Add(normalizedBonePath);
            }
            else
            {
                selectedBones.Remove(normalizedBonePath);
                if (selectedBones.Count == 0)
                {
                    selectionsByMesh.Remove(meshKey);
                }
            }
        }

        private static string NormalizeBonePath(string bonePath)
        {
            return MeshNodePath.Normalize(bonePath);
        }

    }

    public sealed class SkinWeightFeatureExtractor
        : MeshFeatureExtractor<SkinWeightFeatureObject, SkinWeightExtractionOptions>
    {
        private const float WeightEpsilon = 0.00001f;

        public static readonly SkinWeightFeatureExtractor Instance = new();

        public override string FeatureId => SkinWeightFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            SkinWeightExtractionOptions options,
            out SkinWeightFeatureObject feature)
        {
            feature = null;

            var sourceMesh = context.GetSourceFbxMesh();
            var sourceSkin = SkinWeightExtractionUtility.GetPrimarySourceSkinDeformer(context);
            if (sourceMesh == null)
            {
                return MeshFeatureExtractionResult.Skipped("No source mesh was found.");
            }

            var originSkin = SkinWeightExtractionUtility.GetPrimaryOriginSkinDeformer(context);
            var sourceBonesByIndex = SkinWeightFbxComparison.BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = SkinWeightFbxComparison.BuildBonePathsByIndex(originSkin);
            var sourceWeights = SkinWeightFbxComparison.BuildWeightsByPath(sourceSkin, sourceBonesByIndex);
            var originWeights = SkinWeightFbxComparison.BuildWeightsByPath(originSkin, originBonesByIndex);
            int controlPointCount = context.GetOriginFbxMesh()?.ControlPointCount ?? 0;
            var selectedWeightBonePaths = options.GetSelectedWeightBonePaths(context.Path);
            var deltaWeights = BuildSelectedDeltaWeights(
                sourceWeights,
                originWeights,
                selectedWeightBonePaths,
                controlPointCount);

            var bindPoses = ExtractSelectedBindPoses(
                sourceSkin,
                options.GetSelectedBindposeBonePaths(context.Path),
                sourceBonesByIndex);
            var armature = BuildSelectedArmature(
                context.Session,
                options.GetSelectedBoneTransformPaths(),
                options.GetSelectedNewBonePaths());

            var clusters = BuildClusters(deltaWeights, bindPoses);

            if (clusters.Count == 0 &&
                armature == null)
            {
                return MeshFeatureExtractionResult.Skipped("No selected skin weight work was found for this mesh.");
            }

            feature = ScriptableObject.CreateInstance<SkinWeightFeatureObject>();
            feature.SetSkinning(
                armature,
                GetRootBonePath(context),
                clusters);
            return MeshFeatureExtractionResult.Success();
        }

        private static Dictionary<int, Dictionary<string, float>> BuildSelectedDeltaWeights(
            IReadOnlyDictionary<string, Dictionary<int, float>> sourceWeights,
            IReadOnlyDictionary<string, Dictionary<int, float>> originWeights,
            IEnumerable<string> selectedBonePaths,
            int controlPointCount)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            foreach (string bonePath in (selectedBonePaths ?? Array.Empty<string>())
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                sourceWeights.TryGetValue(bonePath, out var sourceByControlPoint);
                originWeights.TryGetValue(bonePath, out var originByControlPoint);
                foreach (int controlPointIndex in (sourceByControlPoint?.Keys ?? Enumerable.Empty<int>())
                             .Concat(originByControlPoint?.Keys ?? Enumerable.Empty<int>())
                             .Where(index => index >= 0 && index < controlPointCount)
                             .Distinct())
                {
                    float source = sourceByControlPoint != null &&
                                   sourceByControlPoint.TryGetValue(controlPointIndex, out float sourceWeight)
                        ? sourceWeight
                        : 0f;
                    float origin = originByControlPoint != null &&
                                   originByControlPoint.TryGetValue(controlPointIndex, out float originWeight)
                        ? originWeight
                        : 0f;
                    float delta = source - origin;
                    if (Mathf.Abs(delta) <= WeightEpsilon)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(controlPointIndex, out var deltasByPath))
                    {
                        deltasByPath = new Dictionary<string, float>();
                        result[controlPointIndex] = deltasByPath;
                    }

                    deltasByPath[bonePath] = delta;
                }
            }

            return result;
        }

        private static List<SkinWeightClusterData> ExtractSelectedBindPoses(
            UfbxSkinDeformer sourceSkin,
            IEnumerable<string> selectedBonePaths,
            IReadOnlyDictionary<int, string> sourceBonesByIndex)
        {
            var result = new List<SkinWeightClusterData>();
            var sourceBindPoses = SkinWeightFbxComparison.BuildBindPosesByPath(sourceSkin, sourceBonesByIndex);
            foreach (string bonePath in (selectedBonePaths ?? Array.Empty<string>())
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                if (sourceBindPoses.TryGetValue(bonePath, out var sourceBindPose))
                {
                    result.Add(sourceBindPose);
                }
            }

            return result;
        }

        private static List<SkinWeightClusterData> BuildClusters(
            IReadOnlyDictionary<int, Dictionary<string, float>> deltaWeights,
            IEnumerable<SkinWeightClusterData> bindPoses)
        {
            var weightsByPath = new Dictionary<string, Dictionary<int, float>>(StringComparer.Ordinal);
            foreach (var controlPointPair in deltaWeights ?? new Dictionary<int, Dictionary<string, float>>())
            {
                foreach (var weightPair in controlPointPair.Value ?? new Dictionary<string, float>())
                {
                    string path = MeshNodePath.Normalize(weightPair.Key);
                    if (path == MeshNodePath.Root || Mathf.Abs(weightPair.Value) <= WeightEpsilon)
                    {
                        continue;
                    }

                    if (!weightsByPath.TryGetValue(path, out var weightsByIndex))
                    {
                        weightsByIndex = new Dictionary<int, float>();
                        weightsByPath[path] = weightsByIndex;
                    }

                    weightsByIndex.TryGetValue(controlPointPair.Key, out float existing);
                    weightsByIndex[controlPointPair.Key] = existing + weightPair.Value;
                }
            }

            var clustersByPath = weightsByPath.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var entries = pair.Value
                        .Where(weight => Mathf.Abs(weight.Value) > WeightEpsilon)
                        .OrderBy(weight => weight.Key)
                        .ToArray();
                    return new SkinWeightClusterData
                    {
                        m_BonePath = pair.Key,
                    }.WithWeights(
                        entries.Select(weight => weight.Key),
                        entries.Select(weight => weight.Value));
                },
                StringComparer.Ordinal);

            foreach (var bindPose in bindPoses ?? Array.Empty<SkinWeightClusterData>())
            {
                if (bindPose == null)
                {
                    continue;
                }

                string path = bindPose.BonePath;
                if (path == MeshNodePath.Root)
                {
                    continue;
                }

                if (!clustersByPath.TryGetValue(path, out var cluster))
                {
                    cluster = new SkinWeightClusterData { m_BonePath = path };
                    clustersByPath[path] = cluster;
                }

                if (bindPose.m_HasFbxClusterMatrices)
                {
                    cluster.SetFbxClusterMatrices(
                        bindPose.m_FbxTransformMatrix,
                        bindPose.m_FbxTransformLinkMatrix);
                }
            }

            return clustersByPath.Values
                .OrderBy(cluster => cluster.BonePath, StringComparer.Ordinal)
                .ToList();
        }

        private static ArmatureObject BuildSelectedArmature(
            MeshFeatureExtractionSession session,
            IEnumerable<string> selectedTransformBonePaths,
            IEnumerable<string> selectedNewBonePaths)
        {
            if (session == null)
            {
                return null;
            }

            var createPaths = new HashSet<string>(
                (selectedNewBonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Where(path => path != MeshNodePath.Root),
                StringComparer.Ordinal);
            var transformPaths = new HashSet<string>(
                (selectedTransformBonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Where(path => path != MeshNodePath.Root),
                StringComparer.Ordinal);
            foreach (string path in createPaths)
            {
                transformPaths.Add(path);
            }

            if (transformPaths.Count == 0 && createPaths.Count == 0)
            {
                return null;
            }

            var armature = ScriptableObject.CreateInstance<ArmatureObject>();
            armature.name = "Armature";
            foreach (string bonePath in transformPaths.Concat(createPaths)
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                AddArmaturePatch(armature, session, bonePath, createPaths.Contains(bonePath));
            }

            return armature.BoneCount > 0 ? armature : null;
        }

        private static void AddArmaturePatch(
            ArmatureObject armature,
            MeshFeatureExtractionSession session,
            string bonePath,
            bool createIfMissing)
        {
            string normalizedPath = MeshNodePath.Normalize(bonePath);
            if (armature == null ||
                session == null ||
                normalizedPath == MeshNodePath.Root ||
                armature.HasBone(normalizedPath))
            {
                return;
            }

            string parentPath = MeshNodePath.ParentPath(normalizedPath);
            if (createIfMissing && parentPath != MeshNodePath.Root && !session.OriginHasTransform(parentPath))
            {
                AddArmaturePatch(armature, session, parentPath, true);
            }

            armature.GetOrAddBone(CreateBoneNode(session, normalizedPath, createIfMissing));
        }

        private static ArmatureBoneData CreateBoneNode(
            MeshFeatureExtractionSession session,
            string path,
            bool createIfMissing)
        {
            var node = new ArmatureBoneData
            {
                m_Path = MeshNodePath.Normalize(path),
                m_CreateIfMissing = createIfMissing
            };

            var sourceNode = session?.GetSourceNode(path);
            if (sourceNode != null)
            {
                node.m_FbxLocalTranslation = sourceNode.LclTranslation.ToVector3();
                node.m_FbxLocalEulerRotation = sourceNode.LclRotation.ToVector3();
                node.m_FbxLocalScale = sourceNode.LclScale.ToVector3();
                return node;
            }

            node.m_FbxLocalTranslation = Vector3.zero;
            node.m_FbxLocalEulerRotation = Vector3.zero;
            node.m_FbxLocalScale = Vector3.one;
            return node;
        }

        private static string GetRootBonePath(MeshFeatureExtractionContext context)
        {
            var sourceRenderer = SkinWeightExtractionUtility.GetSourceRenderer(context);
            string rootBonePath = SkinWeightExtractionUtility.GetTransformPath(
                sourceRenderer != null ? sourceRenderer.rootBone : null,
                context.Session.SourceFbxGo.transform);
            return MeshNodePath.Normalize(rootBonePath);
        }
    }
}
