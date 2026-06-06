using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Fbx.Ufbx;
using Unity.Collections;
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
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            if (context.MeshData == null || context.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Skin weight generation requires mesh data and a target mesh.");
            }

            if (context.TargetRenderer == null || context.TargetRootTransform == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Skin weight generation requires a SkinnedMeshRenderer target hierarchy.");
            }

            feature.Sanitize(context.MeshData);
            return context.GetMappingFor(context.WorkingMesh) != null
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult("Target mesh does not match any stored Unity vertex mapping.");
        }

        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            var canApply = CanApplyToUnityMesh(context, feature);
            if (canApply.Failed)
            {
                return canApply;
            }

            var mapping = context.GetMappingFor(context.WorkingMesh);
            if (mapping == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Target mesh does not match any stored Unity vertex mapping.");
            }

            var mesh = context.WorkingMesh;
            var boneTable = BuildBoneTable(context, feature, mesh, mapping, out string boneError);
            if (boneTable == null)
            {
                return MeshFeatureGenerationResult.FailedResult(boneError);
            }

            if (!BuildUnityBoneWeights(feature, mapping, mesh, boneTable, out string weightError))
            {
                return MeshFeatureGenerationResult.FailedResult(weightError);
            }

            mesh.bindposes = boneTable.BindPoses.ToArray();
            mesh.RecalculateBounds();

            context.Session?.SetSkinBinding(
                context.MeshKey,
                ResolveRootBonePath(context, feature),
                boneTable.BonePaths);
            RegisterArmatureBones(context, feature, mapping);
            context.WorkingMesh = mesh;

            EditorUtility.SetDirty(mesh);
            return MeshFeatureGenerationResult.Success();
        }

#if ENABLE_FBX_SDK
        protected override MeshFeatureGenerationResult CanApplyToFbx(
            FbxGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            feature.Sanitize(context.MeshData);
            return MeshFeatureGenerationResult.Success(false);
        }

        protected override MeshFeatureGenerationResult ApplyToFbx(
            FbxGenerationContext context,
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
            FbxGenerationContext context,
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
            FbxGenerationContext context,
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

                SetLocalTransform(node, bone);
            }

            return true;
        }

        private static FbxNode ResolveOrCreateFbxBone(
            FbxGenerationContext context,
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
            SetLocalTransform(created, bone);
            parent.AddChild(created);
            return created;
        }

        private static void SetLocalTransform(FbxNode node, BoneNodeData bone)
        {
            var localPosition = bone.m_FbxLocalTranslation;
            var localRotation = bone.m_FbxLocalEulerRotation;
            var localScale = bone.m_FbxLocalScale;

            node.LclTranslation.Set(new FbxDouble3(localPosition.x, localPosition.y, localPosition.z));
            node.LclRotation.Set(new FbxDouble3(localRotation.x, localRotation.y, localRotation.z));
            node.LclScaling.Set(new FbxDouble3(localScale.x, localScale.y, localScale.z));
        }

        private static bool TryGetCurrentSkinState(
            FbxGenerationContext context,
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

        private static string BuildSkinStateKey(FbxGenerationContext context)
        {
            return $"fbx-skin::{MeshNodePath.Normalize(context?.MeshData?.m_Path)}";
        }

        private static UfbxMesh GetReaderMesh(
            FbxGenerationContext context,
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
            FbxGenerationContext context,
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
            FbxGenerationContext context,
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
            FbxGenerationContext context,
            SkinWeightFeatureObject feature,
            string path,
            FbxNode boneNode)
        {
            if (feature.TryGetBindPoseFbxClusterMatrices(
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

        private static FbxNode GetRootNode(FbxGenerationContext context)
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

        private static BoneTable BuildBoneTable(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            Mesh mesh,
            UnityVertexMappingObject mapping,
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
                    table.AddSynthetic($".__missing_bone_{i}", bindPose);
                    continue;
                }

                string path = MeshNodePath.GetRelativePath(bone, context.TargetRootTransform);
                if (TryResolveStoredBindPose(feature, path, mapping, out var storedBindPose, out error))
                {
                    bindPose = storedBindPose;
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    return null;
                }

                table.Add(path, bindPose);
            }

            foreach (string path in GetMeshNeededBonePathsInGraphOrder(feature))
            {
                string finalPath = path;
                if (TryGetBoneProxyData(context, feature, path, out var boneProxy))
                {
                    finalPath = boneProxy.FinalBonePath;
                }

                if (table.HasPath(finalPath))
                {
                    table.AddAlias(path, finalPath);
                    continue;
                }

                if (!TryResolveBindPose(context, feature, path, mapping, out var bindPose, out error))
                {
                    return null;
                }

                table.Add(finalPath, bindPose);
                table.AddAlias(path, finalPath);
            }

            return table;
        }

        private static IEnumerable<string> GetMeshNeededBonePathsInGraphOrder(SkinWeightFeatureObject feature)
        {
            var needed = new HashSet<string>();
            foreach (var weights in feature.ControlPointWeights ?? Array.Empty<SkinWeightControlPointData>())
            {
                foreach (var influence in weights?.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence == null ||
                        Mathf.Abs(influence.m_Weight) <= WeightEpsilon ||
                        influence.m_BoneIndex < 0 ||
                        feature.m_BonePaths == null ||
                        influence.m_BoneIndex >= feature.m_BonePaths.Length)
                    {
                        continue;
                    }

                    needed.Add(MeshNodePath.Normalize(feature.m_BonePaths[influence.m_BoneIndex]));
                }
            }

            foreach (var bone in feature.m_BoneGraph?.Bones ?? Array.Empty<BoneNodeData>())
            {
                string path = MeshNodePath.Normalize(bone?.m_Path);
                if (needed.Remove(path))
                {
                    yield return path;
                }
            }

            foreach (string path in needed.OrderBy(path => Array.IndexOf(feature.m_BonePaths, path)))
            {
                yield return path;
            }
        }

        private static bool TryResolveBindPose(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            string path,
            UnityVertexMappingObject mapping,
            out Matrix4x4 bindPose,
            out string error)
        {
            bindPose = Matrix4x4.identity;
            if (TryGetBoneProxyData(context, feature, path, out var boneProxy) &&
                boneProxy.RecalculateBindpose &&
                boneProxy.TryGetLocalToWorld(out var overrideLocalToWorld))
            {
                bindPose = overrideLocalToWorld.inverse * context.TargetRenderer.transform.localToWorldMatrix;
                error = null;
                return true;
            }

            if (TryResolveStoredBindPose(feature, path, mapping, out bindPose, out error))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(error))
            {
                return false;
            }

            if (!TryResolveBoneLocalToWorld(context, feature, path, mapping, new HashSet<string>(), out var localToWorld, out error))
            {
                return false;
            }

            bindPose = localToWorld.inverse * context.TargetRenderer.transform.localToWorldMatrix;
            return true;
        }

        private static bool TryResolveStoredBindPose(
            SkinWeightFeatureObject feature,
            string path,
            UnityVertexMappingObject mapping,
            out Matrix4x4 bindPose,
            out string error)
        {
            bindPose = Matrix4x4.identity;
            error = null;
            if (feature != null &&
                feature.TryGetBindPoseFbxClusterMatrices(path, out var fbxTransformMatrix, out var fbxTransformLinkMatrix))
            {
                if (!fbxTransformLinkMatrix.TryInverse(out var inverseTransformLinkMatrix))
                {
                    error = $"Cannot calculate bindpose for bone '{path}' because its FBX TransformLink matrix is singular.";
                    return false;
                }

                bindPose = mapping.ConvertFbxMatrixToUnity(fbxTransformMatrix * inverseTransformLinkMatrix);
                return true;
            }

            return false;
        }

        private static bool TryResolveBoneLocalToWorld(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            string path,
            UnityVertexMappingObject mapping,
            HashSet<string> resolving,
            out Matrix4x4 localToWorld,
            out string error)
        {
            error = null;
            path = MeshNodePath.Normalize(path);
            if (path == MeshNodePath.Root)
            {
                localToWorld = context.TargetRootTransform != null
                    ? context.TargetRootTransform.localToWorldMatrix
                    : Matrix4x4.identity;
                return true;
            }

            if (TryGetBoneProxyData(context, feature, path, out var boneProxy) &&
                boneProxy.RecalculateBindpose &&
                boneProxy.TryGetLocalToWorld(out localToWorld))
            {
                return true;
            }

            var boneGraph = feature?.m_BoneGraph;
            var graphBone = boneGraph != null ? boneGraph.GetBone(path) : null;
            if (graphBone == null)
            {
                var existing = context.ResolveTransform(path);
                if (existing != null)
                {
                    localToWorld = existing.localToWorldMatrix;
                    return true;
                }

                localToWorld = Matrix4x4.identity;
                error = $"Cannot resolve needed skin bone '{path}'.";
                return false;
            }

            if (!resolving.Add(path))
            {
                localToWorld = Matrix4x4.identity;
                error = $"Bone graph contains a parent cycle at '{path}'.";
                return false;
            }

            string parentPath = MeshNodePath.Normalize(graphBone.m_ParentPath);
            if (!TryResolveBoneLocalToWorld(context, feature, parentPath, mapping, resolving, out var parentLocalToWorld, out error))
            {
                resolving.Remove(path);
                localToWorld = Matrix4x4.identity;
                return false;
            }

            resolving.Remove(path);
            var scale = graphBone.m_FbxLocalScale == Vector3.zero ? Vector3.one : graphBone.m_FbxLocalScale;
            localToWorld = parentLocalToWorld * Matrix4x4.TRS(
                mapping != null ? mapping.ConvertFbxVectorToUnity(graphBone.m_FbxLocalTranslation) : graphBone.m_FbxLocalTranslation,
                Quaternion.Euler(graphBone.m_FbxLocalEulerRotation),
                scale);
            return true;
        }

        private static bool TryGetBoneProxyData(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            string sourceBonePath,
            out BoneProxyGenerationData data)
        {
            data = null;
            var meshComponent = context.GetComponent<BlendShareMesh>();
            if (meshComponent == null ||
                feature?.m_BoneGraph == null ||
                !meshComponent.TryGetBoneProxyBinding(feature.m_BoneGraph, sourceBonePath, out var binding) ||
                binding?.Proxy == null ||
                binding.Proxy.TargetParent == null)
            {
                return false;
            }

            var proxy = binding.Proxy;
            string parentPath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(proxy.TargetParent, context.TargetRootTransform));
            string finalPath = parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(proxy.name)
                : MeshNodePath.Normalize($"{parentPath}/{proxy.name}");
            data = new BoneProxyGenerationData(
                finalPath,
                parentPath,
                proxy,
                proxy.TargetParent,
                proxy.LocalPosition,
                proxy.LocalEulerRotation,
                proxy.LocalScale,
                proxy.RecalculateBindpose);
            return true;
        }

        private static void RegisterArmatureBones(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping)
        {
            if (context?.Session == null || feature?.m_BoneGraph == null)
            {
                return;
            }

            context.Session.AddArmatureBones((feature.m_BoneGraph.Bones ?? Array.Empty<BoneNodeData>())
                .Where(bone => bone != null)
                .Select(bone => CreateArtifactBoneNode(context, feature, mapping, bone)));
        }

        private static BoneNodeData CreateArtifactBoneNode(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping,
            BoneNodeData bone)
        {
            if (TryGetProxyBoneNode(context, feature, bone.m_Path, out var proxyBone))
            {
                return proxyBone;
            }

            return new BoneNodeData(
                bone.m_Path,
                bone.m_ParentPath,
                mapping != null ? mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation) : bone.m_FbxLocalTranslation,
                bone.m_FbxLocalEulerRotation,
                bone.m_FbxLocalScale,
                bone.m_CreateIfMissing);
        }

        private static bool TryGetProxyBoneNode(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature,
            string sourceBonePath,
            out BoneNodeData boneNode)
        {
            boneNode = null;
            if (!TryGetBoneProxyData(context, feature, sourceBonePath, out var boneProxy))
            {
                return false;
            }

            GetProxyLocalTransform(
                boneProxy.Proxy,
                out var localPosition,
                out var localEulerRotation,
                out var localScale);
            boneNode = new BoneNodeData(
                boneProxy.FinalBonePath,
                boneProxy.ParentPath,
                localPosition,
                localEulerRotation,
                localScale,
                true);
            return true;
        }

        private static void GetProxyLocalTransform(
            BlendShareBoneProxy proxy,
            out Vector3 localPosition,
            out Vector3 localEulerRotation,
            out Vector3 localScale)
        {
            if (proxy != null &&
                proxy.TryGetCurrentLocalTransform(out localPosition, out localEulerRotation, out localScale))
            {
                localScale = localScale == Vector3.zero ? Vector3.one : localScale;
                return;
            }

            localPosition = proxy != null ? proxy.LocalPosition : Vector3.zero;
            localEulerRotation = proxy != null ? proxy.LocalEulerRotation : Vector3.zero;
            localScale = proxy != null ? proxy.LocalScale : Vector3.one;
        }

        private sealed class BoneProxyGenerationData
        {
            public string FinalBonePath { get; }
            public string ParentPath { get; }
            public BlendShareBoneProxy Proxy { get; }
            public Transform TargetParent { get; }
            public Vector3 LocalPosition { get; }
            public Vector3 LocalEulerRotation { get; }
            public Vector3 LocalScale { get; }
            public bool RecalculateBindpose { get; }

            public BoneProxyGenerationData(
                string finalBonePath,
                string parentPath,
                BlendShareBoneProxy proxy,
                Transform targetParent,
                Vector3 localPosition,
                Vector3 localEulerRotation,
                Vector3 localScale,
                bool recalculateBindpose)
            {
                FinalBonePath = MeshNodePath.Normalize(finalBonePath);
                ParentPath = MeshNodePath.Normalize(parentPath);
                Proxy = proxy;
                TargetParent = targetParent;
                LocalPosition = localPosition;
                LocalEulerRotation = localEulerRotation;
                LocalScale = localScale == Vector3.zero ? Vector3.one : localScale;
                RecalculateBindpose = recalculateBindpose;
            }

            public bool TryGetLocalToWorld(out Matrix4x4 localToWorld)
            {
                if (TargetParent == null)
                {
                    localToWorld = Matrix4x4.identity;
                    return false;
                }

                localToWorld = TargetParent.localToWorldMatrix * Matrix4x4.TRS(
                    LocalPosition,
                    Quaternion.Euler(LocalEulerRotation),
                    LocalScale);
                return true;
            }
        }

        private static string ResolveRootBonePath(
            UnityMeshGenerationContext context,
            SkinWeightFeatureObject feature)
        {
            string rootBonePath = MeshNodePath.Normalize(feature.m_RootBonePath);
            if (rootBonePath != MeshNodePath.Root)
            {
                return rootBonePath;
            }

            return context.TargetRenderer.rootBone != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(context.TargetRenderer.rootBone, context.TargetRootTransform))
                : MeshNodePath.Root;
        }

        private static bool BuildUnityBoneWeights(
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

            using var targetWeights = mesh.GetAllBoneWeights();
            using var targetCounts = mesh.GetBonesPerVertex();
            using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
            using var outputCounts = new NativeList<byte>(Allocator.Temp);
            int targetWeightIndex = 0;
            for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
            {
                if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                {
                    error = $"Cannot map Unity vertex {unityIndex} to FBX control points.";
                    return false;
                }

                var aggregate = new Dictionary<int, float>();
                int targetCount = unityIndex < targetCounts.Length ? targetCounts[unityIndex] : 0;
                for (int i = 0; i < targetCount; i++)
                {
                    var weight = targetWeights[targetWeightIndex + i];
                    AddInfluence(aggregate, weight.boneIndex, weight.weight);
                }

                targetWeightIndex += targetCount;
                ApplyDeltas(aggregate, group, deltaWeightsByControlPoint, feature, boneTable);
                var normalized = NormalizeWeights(aggregate);
                foreach (var weight in normalized)
                {
                    outputWeights.Add(new BoneWeight1 { boneIndex = weight.index, weight = weight.weight });
                }

                outputCounts.Add((byte)normalized.Count);
            }

            using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
            using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
            mesh.SetBoneWeights(outputCountArray, outputWeightArray);
            return true;
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

        private static List<(int index, float weight)> NormalizeWeights(Dictionary<int, float> aggregate)
        {
            var weights = (aggregate ?? new Dictionary<int, float>())
                .Where(pair => pair.Value > WeightEpsilon)
                .OrderByDescending(pair => pair.Value)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();
            if (weights.Count == 0)
            {
                weights.Add((0, 1f));
            }

            float total = weights.Sum(pair => pair.Value);
            if (total <= WeightEpsilon)
            {
                return new List<(int index, float weight)> { (weights[0].Key, 1f) };
            }

            return weights.Select(pair => (pair.Key, pair.Value / total)).ToList();
        }

        private sealed class BoneTable
        {
            private readonly Dictionary<string, int> indexByPath = new();

            public List<string> BonePaths { get; } = new();
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

            public int Add(string path, Matrix4x4 bindPose)
            {
                string normalized = MeshNodePath.Normalize(path);
                if (indexByPath.TryGetValue(normalized, out int existing))
                {
                    return existing;
                }

                return AddSynthetic(normalized, bindPose);
            }

            public void AddAlias(string sourcePath, string targetPath)
            {
                string normalizedSource = MeshNodePath.Normalize(sourcePath);
                string normalizedTarget = MeshNodePath.Normalize(targetPath);
                if (indexByPath.TryGetValue(normalizedTarget, out int targetIndex))
                {
                    indexByPath[normalizedSource] = targetIndex;
                }
            }

            public int AddSynthetic(string key, Matrix4x4 bindPose)
            {
                int index = BonePaths.Count;
                indexByPath[key] = index;
                BonePaths.Add(key);
                BindPoses.Add(bindPose);
                return index;
            }
        }
    }
}
