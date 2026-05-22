using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEditor;
using UnityEngine;
#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using SdkCluster = Autodesk.Fbx.FbxCluster;
using SdkDeformer = Autodesk.Fbx.FbxDeformer;
using SdkSkeleton = Autodesk.Fbx.FbxSkeleton;
#endif

namespace Triturbo.BlendShare.Features.SkinWeights
{
    public sealed class SkinWeightFeatureGenerator : MeshFeatureGenerator<SkinWeightFeatureObject>
    {
        private const float WeightEpsilon = 0.00001f;

        public static readonly SkinWeightFeatureGenerator Instance = new();

        public override int Order => -100;

        protected override MeshFeatureGenerationResult CanApplyToUnityMesh(
            MeshFeatureUnityGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            return MeshFeatureGenerationResult.FailedResult(
                "Skin weight generation is only supported through FBX generation in this version.");
        }

        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            MeshFeatureUnityGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            var canApply = CanApplyToUnityMesh(context, feature);
            if (canApply.Failed)
            {
                return canApply;
            }

            var mapping = (context.MeshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.IsValidFor(context.WorkingMesh));
            if (mapping == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Target mesh does not match any stored Unity vertex mapping.");
            }

            string graphStepKey = feature.m_BoneGraph != null
                ? $"skin-bone-graph::{feature.m_BoneGraph.GetInstanceID()}"
                : null;
            if (feature.m_BoneGraph != null &&
                context.MarkStepOnce(graphStepKey) &&
                !ApplyBoneGraphTransforms(context, feature.m_BoneGraph, out string graphError))
            {
                return MeshFeatureGenerationResult.FailedResult(graphError);
            }

            var mesh = context.WorkingMesh;
            var boneTable = BuildBoneTable(context, feature, mesh, out string boneError);
            if (boneTable == null)
            {
                return MeshFeatureGenerationResult.FailedResult(boneError);
            }

            var boneWeights = BuildUnityBoneWeights(feature, mapping, mesh, boneTable, out string weightError);
            if (boneWeights == null)
            {
                return MeshFeatureGenerationResult.FailedResult(weightError);
            }

            mesh.bindposes = boneTable.BindPoses.ToArray();
            mesh.boneWeights = boneWeights;
            mesh.RecalculateBounds();

            context.TargetRenderer.bones = boneTable.Bones.ToArray();
            context.TargetRenderer.rootBone = ResolveRootBone(context, feature);
            context.WorkingMesh = mesh;

            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(context.TargetRenderer);
            return MeshFeatureGenerationResult.Success();
        }

#if ENABLE_FBX_SDK
        protected override MeshFeatureGenerationResult CanApplyToFbx(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            feature.Sanitize(context.MeshData);
            return MeshFeatureGenerationResult.Success(false);
        }

        protected override MeshFeatureGenerationResult ApplyToFbx(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            FbxMesh targetMesh = context.TargetMesh;
            if (targetMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult($"Can not find mesh at path: {context.MeshData?.m_Path} in FBX file");
            }

            feature.Sanitize(context.MeshData);
            if (feature.WeightedControlPointCount == 0)
            {
                return MeshFeatureGenerationResult.Skipped("No skin weight deltas were stored for this mesh.");
            }

            int controlPointCount = targetMesh.GetControlPointsCount();
            if (!ValidateControlPointRange(feature, controlPointCount, out string rangeError))
            {
                return MeshFeatureGenerationResult.FailedResult(rangeError);
            }

            if (!ApplyBoneGraphNodesToFbx(context, feature.m_BoneGraph, out string graphError))
            {
                return MeshFeatureGenerationResult.FailedResult(graphError);
            }

            if (!TryGetCurrentSkinState(context, out var skinState, out string skinError))
            {
                return MeshFeatureGenerationResult.FailedResult(skinError);
            }

            var deltaWeights = BuildDeltaWeights(feature);
            if (deltaWeights.Count == 0)
            {
                return MeshFeatureGenerationResult.Skipped("No usable skin weight deltas were stored for this mesh.");
            }

            ApplyDeltaWeights(skinState, deltaWeights);
            if (!RebuildFbxSkin(context, feature, skinState, out skinError))
            {
                return MeshFeatureGenerationResult.FailedResult(skinError);
            }

            context.Session?.SetState(BuildSkinStateKey(context), skinState);
            return MeshFeatureGenerationResult.Success();
        }

        protected override MeshFeatureGenerationResult RemoveFromFbx(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            return MeshFeatureGenerationResult.Skipped();
        }

        private static bool ValidateControlPointRange(
            SkinWeightFeatureObject feature,
            int controlPointCount,
            out string error)
        {
            error = null;
            foreach (var weights in feature.ControlPointWeights ?? Array.Empty<SkinWeightControlPointData>())
            {
                if (weights == null)
                {
                    continue;
                }

                if (weights.m_ControlPointIndex >= controlPointCount)
                {
                    error = $"Skin weight control point {weights.m_ControlPointIndex} is outside target mesh control point count {controlPointCount}.";
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<int, Dictionary<string, float>> BuildDeltaWeights(
            SkinWeightFeatureObject feature)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            if (feature == null)
            {
                return result;
            }

            foreach (var controlPointWeights in feature.ControlPointWeights ?? Array.Empty<SkinWeightControlPointData>())
            {
                if (controlPointWeights == null)
                {
                    continue;
                }

                foreach (var influence in controlPointWeights.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence == null ||
                        influence.m_BoneIndex < 0 ||
                        feature.m_BonePaths == null ||
                        influence.m_BoneIndex >= feature.m_BonePaths.Length ||
                        Mathf.Abs(influence.m_Weight) <= WeightEpsilon)
                    {
                        continue;
                    }

                    string path = MeshNodePath.Normalize(feature.m_BonePaths[influence.m_BoneIndex]);
                    AddWeight(result, controlPointWeights.m_ControlPointIndex, path, influence.m_Weight);
                }
            }

            return result;
        }

        private static void AddWeight(
            Dictionary<int, Dictionary<string, float>> weightsByControlPoint,
            int controlPointIndex,
            string path,
            float weight)
        {
            if (controlPointIndex < 0)
            {
                return;
            }

            path = MeshNodePath.Normalize(path);
            if (!weightsByControlPoint.TryGetValue(controlPointIndex, out var weightsByPath))
            {
                weightsByPath = new Dictionary<string, float>();
                weightsByControlPoint[controlPointIndex] = weightsByPath;
            }

            weightsByPath.TryGetValue(path, out float existing);
            float updated = existing + weight;
            if (Mathf.Abs(updated) <= WeightEpsilon)
            {
                weightsByPath.Remove(path);
            }
            else
            {
                weightsByPath[path] = updated;
            }
        }

        private static bool ApplyBoneGraphNodesToFbx(
            MeshFeatureFbxGenerationContext context,
            BoneGraphObject boneGraph,
            out string error)
        {
            error = null;
            foreach (var bone in boneGraph?.Bones ?? Array.Empty<BoneNodeData>())
            {
                if (bone == null)
                {
                    continue;
                }

                var node = ResolveOrCreateFbxBone(
                    context,
                    boneGraph,
                    MeshNodePath.Normalize(bone.m_Path),
                    new HashSet<string>(),
                    out error);
                if (node == null)
                {
                    return false;
                }
            }

            return true;
        }

        private static FbxNode ResolveOrCreateFbxBone(
            MeshFeatureFbxGenerationContext context,
            BoneGraphObject boneGraph,
            string path,
            HashSet<string> resolving,
            out string error)
        {
            error = null;
            path = MeshNodePath.Normalize(path);
            var rootNode = GetRootNode(context);
            if (path == MeshNodePath.Root)
            {
                return rootNode;
            }

            var existing = FindFbxNodeByPath(rootNode, path);
            if (existing != null)
            {
                return existing;
            }

            var bone = boneGraph != null ? boneGraph.GetBone(path) : null;
            if (bone == null || !bone.m_CreateIfMissing)
            {
                error = $"Cannot resolve FBX skin bone '{path}'.";
                return null;
            }

            if (!resolving.Add(path))
            {
                error = $"Bone graph contains a parent cycle at '{path}'.";
                return null;
            }

            string parentPath = MeshNodePath.Normalize(bone.m_ParentPath);
            var parent = FindFbxNodeByPath(rootNode, parentPath) ??
                         ResolveOrCreateFbxBone(context, boneGraph, parentPath, resolving, out error);
            resolving.Remove(path);
            if (parent == null)
            {
                return null;
            }

            var manager = context.Node.GetFbxManager();
            var created = FbxNode.Create(manager, MeshNodePath.LeafName(path));
            var skeleton = SdkSkeleton.Create(manager, MeshNodePath.LeafName(path));
            skeleton.SetSkeletonType(parent == rootNode
                ? SdkSkeleton.EType.eRoot
                : SdkSkeleton.EType.eLimbNode);
            created.SetNodeAttribute(skeleton);
            SetLocalTransform(created, bone, context.Session != null ? context.Session.ImportScale : 1f);
            parent.AddChild(created);
            return created;
        }

        private static void SetLocalTransform(FbxNode node, BoneNodeData bone, float importScale)
        {
            importScale = importScale == 0f ? 1f : importScale;
            var localPosition = bone.m_FbxLocalTranslation;
            var localRotation = bone.m_FbxLocalEulerRotation;
            var localScale = bone.m_FbxLocalScale;

            node.LclTranslation.Set(new FbxDouble3(localPosition.x, localPosition.y, localPosition.z));
            node.LclRotation.Set(new FbxDouble3(localRotation.x, localRotation.y, localRotation.z));
            node.LclScaling.Set(new FbxDouble3(localScale.x, localScale.y, localScale.z));
        }

        private static bool TryGetCurrentSkinState(
            MeshFeatureFbxGenerationContext context,
            out FbxSkinRebuildState state,
            out string error)
        {
            error = null;
            state = null;
            string stateKey = BuildSkinStateKey(context);
            if (context.Session != null && context.Session.TryGetState(stateKey, out state))
            {
                return true;
            }

            var readerMesh = GetReaderMesh(context, out error);
            if (readerMesh == null)
            {
                return false;
            }

            var readerSkin = readerMesh.SkinDeformers.FirstOrDefault(skin => skin != null && skin.Clusters.Any(cluster => cluster != null && cluster.WeightCount > 0)) ??
                             readerMesh.SkinDeformers.FirstOrDefault(skin => skin != null);
            if (readerSkin == null)
            {
                error = $"Original FBX mesh '{context.MeshData?.m_Path}' does not contain a skin deformer to rebuild.";
                return false;
            }

            state = FbxSkinRebuildState.FromUfbxSkin(readerSkin, context.TargetMesh.GetControlPointsCount());
            context.Session?.SetState(stateKey, state);
            return true;
        }

        private static string BuildSkinStateKey(MeshFeatureFbxGenerationContext context)
        {
            return $"fbx-skin::{MeshNodePath.Normalize(context?.MeshData?.m_Path)}";
        }

        private static UfbxMesh GetReaderMesh(
            MeshFeatureFbxGenerationContext context,
            out string error)
        {
            error = null;
            var scene = context.Session?.ReaderScene;
            if (scene == null)
            {
                error = "Original FBX skin data was not available. Skin weights require ufbx scene data so the primary skin deformer can be rebuilt.";
                return null;
            }

            var mesh = scene.FindMeshByNodePath(MeshNodePath.Normalize(context.MeshData?.m_Path));
            if (mesh == null)
            {
                error = $"FBX mesh node '{MeshNodePath.Normalize(context.MeshData?.m_Path)}' was not found.";
                return null;
            }

            return mesh;
        }

        private static void ApplyDeltaWeights(
            FbxSkinRebuildState state,
            IReadOnlyDictionary<int, Dictionary<string, float>> deltaWeights)
        {
            foreach (var controlPointPair in deltaWeights)
            {
                if (controlPointPair.Key < 0 || controlPointPair.Key >= state.ControlPointCount)
                {
                    continue;
                }

                var weights = state.GetMutableWeights(controlPointPair.Key);
                foreach (var deltaPair in controlPointPair.Value ?? new Dictionary<string, float>())
                {
                    string path = MeshNodePath.Normalize(deltaPair.Key);
                    weights.TryGetValue(path, out double existing);
                    double updated = existing + deltaPair.Value;
                    if (updated <= WeightEpsilon)
                    {
                        weights.Remove(path);
                    }
                    else
                    {
                        weights[path] = updated;
                    }
                }

                PruneControlPointWeights(weights);
            }
        }

        private static void PruneControlPointWeights(Dictionary<string, double> weights)
        {
            if (weights == null)
            {
                return;
            }

            foreach (string path in weights.Keys.ToArray())
            {
                if (weights[path] <= WeightEpsilon)
                {
                    weights.Remove(path);
                }
            }
        }

        private static bool RebuildFbxSkin(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature,
            FbxSkinRebuildState state,
            out string error)
        {
            error = null;
            var targetMesh = context.TargetMesh;
            var rootNode = GetRootNode(context);

            var clusters = BuildClusterRebuildData(context, feature, state, out error);
            if (clusters == null)
            {
                return false;
            }

            RemoveSkinDeformers(targetMesh);

            string skinName = string.IsNullOrWhiteSpace(state.SkinName) ? "Skin" : state.SkinName;
            var skin = FbxSkin.Create(targetMesh.GetFbxManager(), skinName);
            targetMesh.AddDeformer(skin);
            foreach (var data in clusters)
            {
                var boneNode = FindFbxNodeByPath(rootNode, data.Path);
                if (boneNode == null)
                {
                    boneNode = ResolveOrCreateFbxBone(context, feature.m_BoneGraph, data.Path, new HashSet<string>(), out error);
                }

                if (boneNode == null)
                {
                    return false;
                }

                var cluster = SdkCluster.Create(targetMesh.GetFbxManager(), data.Name);
                cluster.SetLink(boneNode);
                cluster.SetLinkMode(data.LinkMode);
                cluster.SetTransformMatrix(data.TransformMatrix);
                cluster.SetTransformLinkMatrix(data.TransformLinkMatrix);

                foreach (var pair in data.WeightsByControlPoint.OrderBy(pair => pair.Key))
                {
                    if (pair.Value <= WeightEpsilon)
                    {
                        continue;
                    }

                    cluster.AddControlPointIndex(pair.Key, pair.Value);
                }

                skin.AddCluster(cluster);
            }
            return true;
        }

        private static List<FbxClusterRebuildData> BuildClusterRebuildData(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature,
            FbxSkinRebuildState state,
            out string error)
        {
            error = null;
            var result = new List<FbxClusterRebuildData>();
            var usedPaths = new HashSet<string>();
            var meshBindMatrix = context.Node.EvaluateGlobalTransform();

            foreach (var originalCluster in state.Clusters)
            {
                var data = FbxClusterRebuildData.FromOriginalCluster(originalCluster, meshBindMatrix);
                if (FindFbxNodeByPath(GetRootNode(context), data.Path) == null)
                {
                    error = $"Original FBX skin bone '{data.Path}' was not found in the SDK scene.";
                    return null;
                }

                data.SetWeights(state.GetWeightsByControlPoint(data.Path));
                result.Add(data);
                usedPaths.Add(data.Path);
            }

            var additionalPaths = state.GetWeightedPaths()
                .Where(path => !usedPaths.Contains(path))
                .OrderBy(path => path, StringComparer.Ordinal);
            foreach (string path in additionalPaths)
            {
                var boneNode = FindFbxNodeByPath(GetRootNode(context), path);
                if (boneNode == null)
                {
                    boneNode = ResolveOrCreateFbxBone(context, feature.m_BoneGraph, path, new HashSet<string>(), out error);
                }

                if (boneNode == null)
                {
                    return null;
                }

                var matrices = ResolveClusterMatrices(context, feature, path, boneNode);
                var data = FbxClusterRebuildData.CreateNew(path, matrices);
                data.SetWeights(state.GetWeightsByControlPoint(path));
                if (data.WeightsByControlPoint.Count > 0)
                {
                    result.Add(data);
                }
            }

            return result;
        }

        private static ClusterMatrices ResolveClusterMatrices(
            MeshFeatureFbxGenerationContext context,
            SkinWeightFeatureObject feature,
            string path,
            FbxNode boneNode)
        {
            if (feature.TryGetExtraBoneFbxClusterMatrices(
                    path,
                    out var fbxTransformMatrix,
                    out var fbxTransformLinkMatrix))
            {
                return new ClusterMatrices(
                    ToFbxAMatrix(fbxTransformMatrix),
                    ToFbxAMatrix(fbxTransformLinkMatrix));
            }

            var meshMatrix = context.Node.EvaluateGlobalTransform();
            return new ClusterMatrices(meshMatrix, boneNode.EvaluateGlobalTransform());
        }

        private static void RemoveSkinDeformers(FbxMesh targetMesh)
        {
            for (int i = targetMesh.GetDeformerCount(SdkDeformer.EDeformerType.eSkin) - 1; i >= 0; i--)
            {
                var deformer = targetMesh.GetDeformer(i, SdkDeformer.EDeformerType.eSkin);
                if (deformer != null)
                {
                    deformer.Destroy();
                }
            }
        }

        private static FbxNode GetRootNode(MeshFeatureFbxGenerationContext context)
        {
            if (context.RootNode != null)
            {
                return context.RootNode;
            }

            var current = context.Node;
            while (current?.GetParent() != null)
            {
                current = current.GetParent();
            }

            return current;
        }

        private static FbxNode FindFbxNodeByPath(FbxNode rootNode, string path)
        {
            if (rootNode == null)
            {
                return null;
            }

            string normalizedPath = MeshNodePath.Normalize(path);
            if (normalizedPath == MeshNodePath.Root)
            {
                return rootNode;
            }

            var current = rootNode;
            foreach (string part in normalizedPath.Split('/'))
            {
                bool found = false;
                for (int i = 0; i < current.GetChildCount(); i++)
                {
                    var child = current.GetChild(i);
                    if (child.GetName() != part)
                    {
                        continue;
                    }

                    current = child;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return null;
                }
            }

            return current;
        }

        private static FbxAMatrix ToFbxAMatrix(Matrix4x4 matrix)
        {
            var result = new FbxAMatrix();
            for (int row = 0; row < 4; row++)
            {
                result[row] = new FbxDouble4(
                    matrix[row, 0],
                    matrix[row, 1],
                    matrix[row, 2],
                    matrix[row, 3]);
            }

            return result;
        }

        private static FbxAMatrix ToFbxAMatrix(Triturbo.BlendShare.Fbx.FbxMatrix4x4 matrix)
        {
            var result = new FbxAMatrix();
            for (int row = 0; row < 4; row++)
            {
                result[row] = new FbxDouble4(
                    matrix[row, 0],
                    matrix[row, 1],
                    matrix[row, 2],
                    matrix[row, 3]);
            }

            return result;
        }

        private static FbxAMatrix CreateIdentityFbxMatrix()
        {
            var result = new FbxAMatrix();
            result[0] = new FbxDouble4(1d, 0d, 0d, 0d);
            result[1] = new FbxDouble4(0d, 1d, 0d, 0d);
            result[2] = new FbxDouble4(0d, 0d, 1d, 0d);
            result[3] = new FbxDouble4(0d, 0d, 0d, 1d);
            return result;
        }

        private sealed class FbxSkinRebuildState
        {
            private readonly List<Dictionary<string, double>> weightsByControlPoint;

            public string SkinName { get; }
            public int ControlPointCount { get; }
            public IReadOnlyList<FbxOriginalClusterData> Clusters { get; }

            private FbxSkinRebuildState(
                string skinName,
                int controlPointCount,
                IEnumerable<FbxOriginalClusterData> clusters,
                List<Dictionary<string, double>> weightsByControlPoint)
            {
                SkinName = skinName;
                ControlPointCount = controlPointCount;
                Clusters = clusters?.ToArray() ?? Array.Empty<FbxOriginalClusterData>();
                this.weightsByControlPoint = weightsByControlPoint;
            }

            public static FbxSkinRebuildState FromUfbxSkin(
                UfbxSkinDeformer skin,
                int controlPointCount)
            {
                controlPointCount = Mathf.Max(0, controlPointCount);
                var weights = Enumerable.Range(0, controlPointCount)
                    .Select(_ => new Dictionary<string, double>())
                    .ToList();
                var pathsByBoneIndex = (skin.Clusters ?? Array.Empty<UfbxSkinCluster>())
                    .Select((cluster, index) => new { cluster, index })
                    .Where(item => item.cluster?.BoneNode != null)
                    .ToDictionary(
                        item => item.index,
                        item => MeshNodePath.Normalize(item.cluster.BoneNode.Path));

                var clusters = (skin.Clusters ?? Array.Empty<UfbxSkinCluster>())
                    .Select((cluster, index) => new { cluster, index })
                    .Where(item => item.cluster != null)
                    .Select(item =>
                    {
                        string path = item.cluster.BoneNode != null
                            ? MeshNodePath.Normalize(item.cluster.BoneNode.Path)
                            : MeshNodePath.Root;
                        if (path == MeshNodePath.Root &&
                            pathsByBoneIndex.TryGetValue(item.index, out string fallbackPath))
                        {
                            path = fallbackPath;
                        }

                        return path != MeshNodePath.Root
                            ? FbxOriginalClusterData.FromUfbxCluster(item.cluster, path)
                            : null;
                    })
                    .Where(cluster => cluster != null)
                    .ToArray();

                foreach (var cluster in clusters)
                {
                    foreach (var pair in cluster.WeightsByControlPoint)
                    {
                        if (pair.Key < 0 || pair.Key >= controlPointCount || pair.Value <= WeightEpsilon)
                        {
                            continue;
                        }

                        weights[pair.Key].TryGetValue(cluster.Path, out double existing);
                        weights[pair.Key][cluster.Path] = existing + pair.Value;
                    }
                }

                return new FbxSkinRebuildState(skin.Name, controlPointCount, clusters, weights);
            }

            public Dictionary<string, double> GetMutableWeights(int controlPointIndex)
            {
                return weightsByControlPoint[controlPointIndex];
            }

            public Dictionary<int, double> GetWeightsByControlPoint(string path)
            {
                path = MeshNodePath.Normalize(path);
                var result = new Dictionary<int, double>();
                for (int i = 0; i < weightsByControlPoint.Count; i++)
                {
                    if (weightsByControlPoint[i].TryGetValue(path, out double weight) &&
                        weight > WeightEpsilon)
                    {
                        result[i] = weight;
                    }
                }

                return result;
            }

            public IEnumerable<string> GetWeightedPaths()
            {
                return weightsByControlPoint
                    .SelectMany(weights => weights.Keys)
                    .Distinct(StringComparer.Ordinal);
            }
        }

        private sealed class FbxOriginalClusterData
        {
            public string Path { get; }
            public string Name { get; }
            public SdkCluster.ELinkMode LinkMode { get; }
            public FbxAMatrix TransformMatrix { get; }
            public FbxAMatrix TransformLinkMatrix { get; }
            public Dictionary<int, double> WeightsByControlPoint { get; }

            private FbxOriginalClusterData(
                string path,
                string name,
                SdkCluster.ELinkMode linkMode,
                FbxAMatrix transformMatrix,
                FbxAMatrix transformLinkMatrix,
                Dictionary<int, double> weightsByControlPoint)
            {
                Path = MeshNodePath.Normalize(path);
                Name = string.IsNullOrWhiteSpace(name) ? $"{MeshNodePath.LeafName(Path)}Cluster" : name;
                LinkMode = linkMode;
                TransformMatrix = transformMatrix ?? CreateIdentityFbxMatrix();
                TransformLinkMatrix = transformLinkMatrix ?? CreateIdentityFbxMatrix();
                WeightsByControlPoint = weightsByControlPoint ?? new Dictionary<int, double>();
            }

            public static FbxOriginalClusterData FromUfbxCluster(UfbxSkinCluster cluster, string path)
            {
                var weightsByControlPoint = new Dictionary<int, double>();
                var indices = cluster.GetIndices();
                var weights = cluster.GetWeights();
                for (int i = 0; i < indices.Length && i < weights.Length; i++)
                {
                    int controlPointIndex = indices[i];
                    double weight = weights[i];
                    if (controlPointIndex >= 0 && weight > WeightEpsilon)
                    {
                        weightsByControlPoint.TryGetValue(controlPointIndex, out double existing);
                        weightsByControlPoint[controlPointIndex] = existing + weight;
                    }
                }

                return new FbxOriginalClusterData(
                    path,
                    cluster.Name,
                    SdkCluster.ELinkMode.eNormalize,
                    ToFbxAMatrix(cluster.MeshBindWorld),
                    ToFbxAMatrix(cluster.BindToWorld),
                    weightsByControlPoint);
            }
        }

        private sealed class FbxClusterRebuildData
        {
            public string Path { get; private set; }
            public string Name { get; private set; }
            public SdkCluster.ELinkMode LinkMode { get; private set; }
            public FbxAMatrix TransformMatrix { get; private set; }
            public FbxAMatrix TransformLinkMatrix { get; private set; }
            public Dictionary<int, double> WeightsByControlPoint { get; } = new();

            public static FbxClusterRebuildData FromOriginalCluster(FbxOriginalClusterData cluster, FbxAMatrix meshBindMatrix)
            {
                return new FbxClusterRebuildData
                {
                    Path = cluster.Path,
                    Name = cluster.Name,
                    LinkMode = cluster.LinkMode,
                    TransformMatrix = cluster.TransformMatrix ?? meshBindMatrix ?? CreateIdentityFbxMatrix(),
                    TransformLinkMatrix = cluster.TransformLinkMatrix
                };
            }

            public static FbxClusterRebuildData CreateNew(string path, ClusterMatrices matrices)
            {
                path = MeshNodePath.Normalize(path);
                return new FbxClusterRebuildData
                {
                    Path = path,
                    Name = $"{MeshNodePath.LeafName(path)}Cluster",
                    LinkMode = SdkCluster.ELinkMode.eTotalOne,
                    TransformMatrix = matrices.TransformMatrix,
                    TransformLinkMatrix = matrices.TransformLinkMatrix
                };
            }

            public void SetWeights(Dictionary<int, double> weights)
            {
                WeightsByControlPoint.Clear();
                foreach (var pair in weights ?? new Dictionary<int, double>())
                {
                    if (pair.Key >= 0 && pair.Value > WeightEpsilon)
                    {
                        WeightsByControlPoint[pair.Key] = pair.Value;
                    }
                }
            }
        }

        private readonly struct ClusterMatrices
        {
            public FbxAMatrix TransformMatrix { get; }
            public FbxAMatrix TransformLinkMatrix { get; }

            public ClusterMatrices(FbxAMatrix transformMatrix, FbxAMatrix transformLinkMatrix)
            {
                TransformMatrix = transformMatrix;
                TransformLinkMatrix = transformLinkMatrix;
            }
        }
#endif

        private static bool ApplyBoneGraphTransforms(
            MeshFeatureUnityGenerationContext context,
            BoneGraphObject boneGraph,
            out string error)
        {
            error = null;
            foreach (var bone in boneGraph?.Bones ?? Array.Empty<BoneNodeData>())
            {
                if (bone == null)
                {
                    continue;
                }

                var transform = ResolveOrCreateBone(context, boneGraph, MeshNodePath.Normalize(bone.m_Path), new HashSet<string>(), out error);
                if (transform == null)
                {
                    return false;
                }

            }

            return true;
        }

        private static Transform ResolveOrCreateBone(
            MeshFeatureUnityGenerationContext context,
            BoneGraphObject boneGraph,
            string path,
            HashSet<string> resolving,
            out string error)
        {
            error = null;
            path = MeshNodePath.Normalize(path);
            if (path == MeshNodePath.Root)
            {
                return context.TargetRootTransform;
            }

            var existing = context.ResolveTransform(path);
            if (existing != null)
            {
                return existing;
            }

            var bone = boneGraph.GetBone(path);
            if (bone == null || !bone.m_CreateIfMissing)
            {
                error = $"Cannot resolve skin bone '{path}'.";
                return null;
            }

            if (!resolving.Add(path))
            {
                error = $"Bone graph contains a parent cycle at '{path}'.";
                return null;
            }

            string parentPath = MeshNodePath.Normalize(bone.m_ParentPath);
            var parent = context.ResolveTransform(parentPath);
            if (parent == null)
            {
                parent = ResolveOrCreateBone(context, boneGraph, parentPath, resolving, out error);
            }

            resolving.Remove(path);
            if (parent == null)
            {
                return null;
            }

            var created = new GameObject(MeshNodePath.LeafName(path));
            created.transform.SetParent(parent, false);
            created.transform.localPosition = bone.m_FbxLocalTranslation;
            created.transform.localRotation = Quaternion.Euler(bone.m_FbxLocalEulerRotation);
            created.transform.localScale = bone.m_FbxLocalScale;
            context.CacheTransform(path, created.transform);
            EditorUtility.SetDirty(created);
            EditorUtility.SetDirty(parent);
            return created.transform;
        }

        private static BoneTable BuildBoneTable(
            MeshFeatureUnityGenerationContext context,
            SkinWeightFeatureObject feature,
            Mesh mesh,
            out string error)
        {
            error = null;
            var table = new BoneTable();
            var existingBones = context.TargetRenderer.bones ?? Array.Empty<Transform>();
            var existingBindPoses = mesh.bindposes ?? Array.Empty<Matrix4x4>();

            for (int i = 0; i < existingBones.Length; i++)
            {
                var bone = existingBones[i];
                var bindPose = i < existingBindPoses.Length ? existingBindPoses[i] : Matrix4x4.identity;
                if (bone == null)
                {
                    table.AddSynthetic($".__missing_bone_{i}", null, bindPose);
                    continue;
                }

                string path = MeshNodePath.GetRelativePath(bone, context.TargetRootTransform);
                table.Add(path, bone, bindPose);
            }

            foreach (string path in GetMeshNeededBonePaths(feature))
            {
                if (table.HasPath(path))
                {
                    continue;
                }

                var transform = context.ResolveTransform(path);
                if (transform == null)
                {
                    var graphBone = feature.m_BoneGraph != null ? feature.m_BoneGraph.GetBone(path) : null;
                    if (graphBone == null || !graphBone.m_CreateIfMissing)
                    {
                        error = $"Cannot resolve needed skin bone '{path}'.";
                        return null;
                    }

                    transform = ResolveOrCreateBone(context, feature.m_BoneGraph, path, new HashSet<string>(), out error);
                    if (transform == null)
                    {
                        return null;
                    }
                }

                table.Add(path, transform, ResolveExtraBoneBindPose(context, transform));
            }

            return table;
        }

        private static IEnumerable<string> GetMeshNeededBonePaths(SkinWeightFeatureObject feature)
        {
            var indices = new HashSet<int>();
            foreach (var weights in feature.ControlPointWeights ?? Array.Empty<SkinWeightControlPointData>())
            {
                foreach (var influence in weights?.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence != null && Mathf.Abs(influence.m_Weight) > WeightEpsilon)
                    {
                        indices.Add(influence.m_BoneIndex);
                    }
                }
            }

            foreach (int index in indices.OrderBy(index => index))
            {
                if (feature.m_BonePaths != null && index >= 0 && index < feature.m_BonePaths.Length)
                {
                    yield return MeshNodePath.Normalize(feature.m_BonePaths[index]);
                }
            }
        }

        private static Matrix4x4 ResolveExtraBoneBindPose(
            MeshFeatureUnityGenerationContext context,
            Transform bone)
        {
            if (bone == null || context?.TargetRenderer == null)
            {
                return Matrix4x4.identity;
            }

            // Do not use the reader-derived bind pose here. It depends on the same raw
            // cluster Transform that can disagree with SDK/global node evaluation.
            return bone.worldToLocalMatrix * context.TargetRenderer.transform.localToWorldMatrix;
        }

        private static Transform ResolveRootBone(
            MeshFeatureUnityGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            string rootBonePath = MeshNodePath.Normalize(feature.m_RootBonePath);
            var rootBone = context.ResolveTransform(rootBonePath);
            return rootBone != null ? rootBone : context.TargetRootTransform;
        }

        private static BoneWeight[] BuildUnityBoneWeights(
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping,
            Mesh mesh,
            BoneTable boneTable,
            out string error)
        {
            error = null;
            var deltaWeightsByControlPoint = feature.ControlPointWeights
                .Where(weights => weights != null)
                .ToDictionary(weights => weights.m_ControlPointIndex, weights => weights);

            var targetWeights = mesh.boneWeights ?? Array.Empty<BoneWeight>();
            var unityWeights = new BoneWeight[mesh.vertexCount];
            for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
            {
                if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                {
                    error = $"Cannot map Unity vertex {unityIndex} to FBX control points.";
                    return null;
                }

                var aggregate = FromBoneWeight(unityIndex < targetWeights.Length ? targetWeights[unityIndex] : default);
                ApplyDeltas(aggregate, group, deltaWeightsByControlPoint, feature, boneTable);
                unityWeights[unityIndex] = ToBoneWeight(aggregate);
            }

            return unityWeights;
        }

        private static void ApplyDeltas(
            Dictionary<int, float> aggregate,
            FbxIndexGroup group,
            IReadOnlyDictionary<int, SkinWeightControlPointData> deltaWeightsByControlPoint,
            SkinWeightFeatureObject feature,
            BoneTable boneTable)
        {
            int contributingControlPoints = 0;
            var deltaByBoneSlot = new Dictionary<int, float>();
            foreach (int controlPointIndex in group.m_Indices ?? Array.Empty<int>())
            {
                if (!deltaWeightsByControlPoint.TryGetValue(controlPointIndex, out var weights))
                {
                    continue;
                }

                contributingControlPoints++;
                foreach (var influence in weights.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence == null ||
                        influence.m_BoneIndex < 0 ||
                        feature.m_BonePaths == null ||
                        influence.m_BoneIndex >= feature.m_BonePaths.Length ||
                        Mathf.Abs(influence.m_Weight) <= WeightEpsilon)
                    {
                        continue;
                    }

                    string path = MeshNodePath.Normalize(feature.m_BonePaths[influence.m_BoneIndex]);
                    if (!boneTable.TryGetIndex(path, out int targetBoneIndex))
                    {
                        continue;
                    }

                    deltaByBoneSlot.TryGetValue(targetBoneIndex, out float existing);
                    deltaByBoneSlot[targetBoneIndex] = existing + influence.m_Weight;
                }
            }

            if (contributingControlPoints > 1)
            {
                foreach (int boneIndex in deltaByBoneSlot.Keys.ToArray())
                {
                    deltaByBoneSlot[boneIndex] /= contributingControlPoints;
                }
            }

            foreach (var pair in deltaByBoneSlot)
            {
                aggregate.TryGetValue(pair.Key, out float existing);
                float updated = existing + pair.Value;
                if (Mathf.Abs(updated) <= WeightEpsilon)
                {
                    aggregate.Remove(pair.Key);
                }
                else
                {
                    aggregate[pair.Key] = Mathf.Max(0f, updated);
                }
            }
        }

        private static Dictionary<int, float> FromBoneWeight(BoneWeight boneWeight)
        {
            var result = new Dictionary<int, float>();
            AddInfluence(result, boneWeight.boneIndex0, boneWeight.weight0);
            AddInfluence(result, boneWeight.boneIndex1, boneWeight.weight1);
            AddInfluence(result, boneWeight.boneIndex2, boneWeight.weight2);
            AddInfluence(result, boneWeight.boneIndex3, boneWeight.weight3);
            return result;
        }

        private static void AddInfluence(Dictionary<int, float> weights, int boneIndex, float weight)
        {
            if (boneIndex < 0 || weight <= WeightEpsilon)
            {
                return;
            }

            weights.TryGetValue(boneIndex, out float existing);
            weights[boneIndex] = existing + weight;
        }

        private static BoneWeight ToBoneWeight(Dictionary<int, float> aggregate)
        {
            var top = (aggregate ?? new Dictionary<int, float>())
                .Where(pair => pair.Value > WeightEpsilon)
                .OrderByDescending(pair => pair.Value)
                .Take(4)
                .ToArray();

            float total = top.Sum(pair => pair.Value);
            if (total <= WeightEpsilon)
            {
                return default;
            }

            var boneWeight = new BoneWeight();
            for (int i = 0; i < top.Length; i++)
            {
                SetInfluence(ref boneWeight, i, top[i].Key, top[i].Value / total);
            }

            return boneWeight;
        }

        private static void SetInfluence(ref BoneWeight boneWeight, int slot, int boneIndex, float weight)
        {
            switch (slot)
            {
                case 0:
                    boneWeight.boneIndex0 = boneIndex;
                    boneWeight.weight0 = weight;
                    break;
                case 1:
                    boneWeight.boneIndex1 = boneIndex;
                    boneWeight.weight1 = weight;
                    break;
                case 2:
                    boneWeight.boneIndex2 = boneIndex;
                    boneWeight.weight2 = weight;
                    break;
                case 3:
                    boneWeight.boneIndex3 = boneIndex;
                    boneWeight.weight3 = weight;
                    break;
            }
        }

        private sealed class BoneTable
        {
            private readonly Dictionary<string, int> indexByPath = new();

            public List<Transform> Bones { get; } = new();
            public List<Matrix4x4> BindPoses { get; } = new();

            public bool HasPath(string path)
            {
                return indexByPath.ContainsKey(MeshNodePath.Normalize(path));
            }

            public bool TryGetIndex(string path, out int index)
            {
                return indexByPath.TryGetValue(MeshNodePath.Normalize(path), out index);
            }

            public Matrix4x4 GetBindPose(string path)
            {
                return TryGetIndex(path, out int index) && index >= 0 && index < BindPoses.Count
                    ? BindPoses[index]
                    : Matrix4x4.identity;
            }

            public void SetBindPose(string path, Matrix4x4 bindPose)
            {
                if (TryGetIndex(path, out int index) && index >= 0 && index < BindPoses.Count)
                {
                    BindPoses[index] = bindPose;
                }
            }

            public int Add(string path, Transform bone, Matrix4x4 bindPose)
            {
                string normalized = MeshNodePath.Normalize(path);
                if (indexByPath.TryGetValue(normalized, out int existing))
                {
                    return existing;
                }

                return AddSynthetic(normalized, bone, bindPose);
            }

            public int AddSynthetic(string key, Transform bone, Matrix4x4 bindPose)
            {
                int index = Bones.Count;
                indexByPath[key] = index;
                Bones.Add(bone);
                BindPoses.Add(bindPose);
                return index;
            }
        }
    }
}
