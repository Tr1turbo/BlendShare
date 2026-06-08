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
                string parent = GetParentPath(path);
                while (parent != MeshNodePath.Root)
                {
                    if (comparison.TryGetBone(parent, out var parentBone) &&
                        parentBone.RequiresCreateBone)
                    {
                        selectedNewBonePaths.Add(parent);
                        selectedTransformBonePaths.Add(parent);
                    }

                    parent = GetParentPath(parent);
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

        private static string GetParentPath(string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            if (normalized == MeshNodePath.Root)
            {
                return MeshNodePath.Root;
            }

            int separator = normalized.LastIndexOf('/');
            return separator > 0 ? normalized.Substring(0, separator) : MeshNodePath.Root;
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
            var selectedWeightBonePaths = options.GetSelectedWeightBonePaths(context.Path);
            var deltaWeights = BuildSelectedDeltaWeights(
                sourceWeights,
                originWeights,
                selectedWeightBonePaths);

            var bonePaths = deltaWeights
                .SelectMany(weights => weights.Value.Keys)
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var localBoneIndexByPath = bonePaths
                .Select((path, index) => new { path, index })
                .ToDictionary(item => item.path, item => item.index);

            var bindPoses = ExtractSelectedBindPoses(
                sourceSkin,
                originSkin,
                options.GetSelectedBindposeBonePaths(context.Path),
                sourceBonesByIndex,
                originBonesByIndex);
            var boneGraph = BuildSelectedBoneGraph(
                context.Session,
                options.GetSelectedBoneTransformPaths(),
                options.GetSelectedNewBonePaths());

            var controlPointWeights = deltaWeights
                .Select(pair => new SkinWeightControlPointData
                {
                    m_ControlPointIndex = pair.Key,
                    m_Influences = pair.Value
                        .Where(weight => localBoneIndexByPath.ContainsKey(weight.Key))
                        .Select(weight => new SkinWeightInfluenceData
                        {
                            m_BoneIndex = localBoneIndexByPath[weight.Key],
                            m_Weight = weight.Value
                        })
                        .OrderByDescending(influence => Mathf.Abs(influence.m_Weight))
                        .ToArray()
                })
                .Where(weights => weights.m_Influences.Length > 0)
                .ToList();

            if (controlPointWeights.Count == 0 &&
                bindPoses.Count == 0 &&
                boneGraph == null)
            {
                return MeshFeatureExtractionResult.Skipped("No selected skin weight work was found for this mesh.");
            }

            feature = ScriptableObject.CreateInstance<SkinWeightFeatureObject>();
            feature.SetSkinning(
                boneGraph,
                GetRootBonePath(context),
                sourceMesh.ControlPointCount,
                bonePaths,
                controlPointWeights,
                bindPoses);
            return MeshFeatureExtractionResult.Success();
        }

        private static Dictionary<int, Dictionary<string, float>> BuildSelectedDeltaWeights(
            IReadOnlyDictionary<string, Dictionary<int, float>> sourceWeights,
            IReadOnlyDictionary<string, Dictionary<int, float>> originWeights,
            IEnumerable<string> selectedBonePaths)
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

        private static List<SkinWeightBindPoseData> ExtractSelectedBindPoses(
            UfbxSkinDeformer sourceSkin,
            UfbxSkinDeformer originSkin,
            IEnumerable<string> selectedBonePaths,
            IReadOnlyDictionary<int, string> sourceBonesByIndex,
            IReadOnlyDictionary<int, string> originBonesByIndex)
        {
            var result = new List<SkinWeightBindPoseData>();
            var sourceBindPoses = SkinWeightFbxComparison.BuildBindPosesByPath(sourceSkin, sourceBonesByIndex);
            var originBindPoses = SkinWeightFbxComparison.BuildBindPosesByPath(originSkin, originBonesByIndex);
            foreach (string bonePath in (selectedBonePaths ?? Array.Empty<string>())
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                if (sourceBindPoses.TryGetValue(bonePath, out var sourceBindPose))
                {
                    originBindPoses.TryGetValue(bonePath, out var originBindPose);
                    var relativeBindPose = CreateRelativeBindPose(sourceBindPose, originBindPose);
                    if (relativeBindPose != null)
                    {
                        result.Add(relativeBindPose);
                    }
                }
            }

            return result;
        }

        private static SkinWeightBindPoseData CreateRelativeBindPose(
            SkinWeightBindPoseData source,
            SkinWeightBindPoseData origin)
        {
            var originTransformMatrix = origin?.m_FbxTransformMatrix ?? Triturbo.BlendShare.Fbx.FbxMatrix4x4.Identity;
            var originTransformLinkMatrix = origin?.m_FbxTransformLinkMatrix ?? Triturbo.BlendShare.Fbx.FbxMatrix4x4.Identity;
            if (!originTransformMatrix.TryInverse(out var inverseOriginTransformMatrix) ||
                !originTransformLinkMatrix.TryInverse(out var inverseOriginTransformLinkMatrix))
            {
                return null;
            }

            return new SkinWeightBindPoseData(
                source.m_Path,
                source.m_FbxTransformMatrix * inverseOriginTransformMatrix,
                source.m_FbxTransformLinkMatrix * inverseOriginTransformLinkMatrix);
        }

        private static BoneGraphObject BuildSelectedBoneGraph(
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

            var graph = ScriptableObject.CreateInstance<BoneGraphObject>();
            graph.name = "BoneGraph";
            foreach (string bonePath in transformPaths.Concat(createPaths)
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                AddBoneGraphPatch(graph, session, bonePath, createPaths.Contains(bonePath));
            }

            return graph.BoneCount > 0 ? graph : null;
        }

        private static void AddBoneGraphPatch(
            BoneGraphObject graph,
            MeshFeatureExtractionSession session,
            string bonePath,
            bool createIfMissing)
        {
            string normalizedPath = MeshNodePath.Normalize(bonePath);
            if (graph == null ||
                session == null ||
                normalizedPath == MeshNodePath.Root ||
                graph.HasBone(normalizedPath))
            {
                return;
            }

            string parentPath = GetParentPath(normalizedPath);
            if (createIfMissing && parentPath != MeshNodePath.Root && !session.OriginHasTransform(parentPath))
            {
                AddBoneGraphPatch(graph, session, parentPath, true);
            }

            graph.GetOrAddBone(CreateBoneNode(session, normalizedPath, parentPath, createIfMissing));
        }

        private static BoneNodeData CreateBoneNode(
            MeshFeatureExtractionSession session,
            string path,
            string parentPath,
            bool createIfMissing)
        {
            var node = new BoneNodeData
            {
                m_Path = MeshNodePath.Normalize(path),
                m_ParentPath = MeshNodePath.Normalize(parentPath),
                m_CreateIfMissing = createIfMissing
            };

            var sourceNode = session?.GetSourceNode(path);
            if (sourceNode != null)
            {
                var originNode = session.GetOriginNode(path);
                node.m_FbxLocalTranslation = sourceNode.LclTranslation.ToVector3() -
                                             (originNode != null ? originNode.LclTranslation.ToVector3() : Vector3.zero);
                node.m_FbxLocalEulerRotation = sourceNode.LclRotation.ToVector3() -
                                               (originNode != null ? originNode.LclRotation.ToVector3() : Vector3.zero);
                node.m_FbxLocalScale = DivideScale(
                    sourceNode.LclScale.ToVector3(),
                    originNode != null ? originNode.LclScale.ToVector3() : Vector3.one);
                return node;
            }

            node.m_FbxLocalTranslation = Vector3.zero;
            node.m_FbxLocalEulerRotation = Vector3.zero;
            node.m_FbxLocalScale = Vector3.one;
            return node;
        }

        private static Vector3 DivideScale(Vector3 source, Vector3 origin)
        {
            return new Vector3(
                Mathf.Approximately(origin.x, 0f) ? source.x : source.x / origin.x,
                Mathf.Approximately(origin.y, 0f) ? source.y : source.y / origin.y,
                Mathf.Approximately(origin.z, 0f) ? source.z : source.z / origin.z);
        }

        private static string GetParentPath(string path)
        {
            string normalized = MeshNodePath.Normalize(path);
            if (normalized == MeshNodePath.Root)
            {
                return MeshNodePath.Root;
            }

            int separator = normalized.LastIndexOf('/');
            return separator > 0 ? normalized.Substring(0, separator) : MeshNodePath.Root;
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
