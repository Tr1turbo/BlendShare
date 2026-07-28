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
        private static readonly Comparison<(int index, float weight)> InfluenceComparison =
            CompareInfluences;

        public static readonly SkinWeightFeatureGenerator Instance = new();

        public override int Order => -100;

        private static MeshFeatureGenerationResult ApplyUnityContribution(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature)
        {
            var canApply = ValidateUnityContribution(context, feature);
            if (canApply.Failed)
            {
                return canApply;
            }

            var mapping = context.GetMappingFor(feature, context.WorkingMesh);
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

            var artifactBones = BuildArtifactBones(context, feature, mapping);
            var patch = context.GetHandle(feature).Patch;
            if (context.Session != null &&
                !context.Session.CanAddArmatureBones(artifactBones, patch, out boneError))
            {
                context.Session.Fail(boneError);
                return MeshFeatureGenerationResult.FailedResult(boneError);
            }

            if (!StageUnityBoneWeights(context, feature, mapping, mesh, boneTable, out string weightError))
            {
                return MeshFeatureGenerationResult.FailedResult(weightError);
            }

            StageUnitySkinBinding(
                context,
                feature,
                mapping,
                boneTable,
                ResolveRootBonePath(context, feature),
                artifactBones);
            context.WorkingMesh = mesh;
            return MeshFeatureGenerationResult.Success();
        }

        private static MeshFeatureGenerationResult ValidateUnityContribution(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature)
        {
            var meshData = context?.GetHandle(feature).MeshData;
            if (meshData == null || context.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult(
                    "Skin weight generation requires mesh data and a target mesh.");
            }

            if (context.TargetRenderer == null || context.TargetRootTransform == null)
            {
                return MeshFeatureGenerationResult.FailedResult(
                    "Skin weight generation requires a SkinnedMeshRenderer target hierarchy.");
            }

            feature.Sanitize(meshData);
            return context.GetMappingFor(feature, context.WorkingMesh) != null
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult(
                    "Target mesh does not match any stored Unity vertex mapping.");
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult CanApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            if (context?.WorkingMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Skin weight generation requires a target mesh.");
            }

            if (context.TargetRenderer == null || context.TargetRootTransform == null)
            {
                return MeshFeatureGenerationResult.FailedResult("Skin weight generation requires a SkinnedMeshRenderer target hierarchy.");
            }

            return features.Any(feature => context.GetMappingFor(feature, context.WorkingMesh) != null)
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult("No skin-weight feature has a compatible Unity vertex mapping.");
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult PrepareUnityGeneration(
            UnityMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            if (context?.Session == null)
            {
                return MeshFeatureGenerationResult.Success(false);
            }

            for (int order = 0; order < features.Count; order++)
            {
                var feature = features[order];
                var mapping = context.GetMappingFor(feature, context.OriginalMesh);
                if (mapping == null)
                {
                    continue;
                }

                foreach (string sourcePath in feature.GetNeededBonePathsInArmatureOrder())
                {
                    if (!TryGetBoneProxyData(context, feature, sourcePath, out var proxyData))
                    {
                        continue;
                    }

                    if (!TryResolveBindPose(
                            context,
                            feature,
                            sourcePath,
                            mapping,
                            out var bindPose,
                            out string error))
                    {
                        return MeshFeatureGenerationResult.FailedResult(error);
                    }

                    context.Session.AddBoneBindingUse(new UnityBoneBindingUse(
                        context.MeshKey,
                        context.GetHandle(feature).Order,
                        feature.Armature,
                        sourcePath,
                        proxyData.Proxy,
                        proxyData.Binding,
                        proxyData.FinalTransform,
                        proxyData.FinalBonePath,
                        bindPose));
                }
            }

            return MeshFeatureGenerationResult.Success(false);
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult ApplyToUnityMesh(
            UnityMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            bool modified = false;
            foreach (var feature in features)
            {
                var canApply = ValidateUnityContribution(context, feature);
                if (canApply.Failed)
                {
                    Debug.LogError(
                        $"[BlendShare] Skipped skin-weight feature from '{context.GetHandle(feature).Patch?.name}': {canApply.Message}");
                    DiscardUnitySkinWeights(context.Session, context.MeshKey);
                    continue;
                }

                var result = ApplyUnityContribution(context, feature);
                if (result.Failed)
                {
                    Debug.LogError(
                        $"[BlendShare] Skipped skin-weight feature from '{context.GetHandle(feature).Patch?.name}': {result.Message}");
                    DiscardUnitySkinWeights(context.Session, context.MeshKey);
                    continue;
                }

                CommitUnitySkinWeights(context.Session, context.MeshKey);
                modified |= result.Modified;
            }

            return modified
                ? MeshFeatureGenerationResult.Success()
                : MeshFeatureGenerationResult.Skipped("No valid skin-weight contribution was applied.");
        }

        /// <inheritdoc />
        public override MeshFeatureGenerationResult FinalizeUnityGeneration(UnityMeshGenerationSession session)
        {
            return FinalizeUnitySkinWeights(session, out string error)
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult(error);
        }

        private static void CommitUnitySkinWeights(UnityMeshGenerationSession session, string meshKey)
        {
            if (session != null && session.TryGetState(BuildUnitySkinStateKey(meshKey), out UnitySkinWeightState state))
            {
                state.Commit(session);
            }
        }

        private static void DiscardUnitySkinWeights(UnityMeshGenerationSession session, string meshKey)
        {
            if (session != null && session.TryGetState(BuildUnitySkinStateKey(meshKey), out UnitySkinWeightState state))
            {
                state.Discard();
            }
        }

        internal static bool FinalizeUnitySkinWeights(UnityMeshGenerationSession session, out string error)
        {
            error = null;
            if (session == null)
            {
                return true;
            }

            foreach (var state in session.GetStates<UnitySkinWeightState>())
            {
                if (!state.FinalizeWeights(out error))
                {
                    session.Fail(error);
                    return false;
                }
            }

            return true;
        }

        private static bool StageUnityBoneWeights(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping,
            Mesh mesh,
            BoneTable boneTable,
            out string error)
        {
            error = null;
            if (context.Session == null)
            {
                return BuildUnityBoneWeights(feature, mapping, mesh, boneTable, out error);
            }

            string stateKey = BuildUnitySkinStateKey(context.MeshKey);
            if (!context.Session.TryGetState(stateKey, out UnitySkinWeightState state))
            {
                state = new UnitySkinWeightState(context.MeshKey, mesh, boneTable);
                context.Session.SetState(stateKey, state);
            }

            return state.Stage(
                mesh,
                feature,
                mapping,
                boneTable,
                context.GetComponents<BlendShareMesh>(feature),
                out error);
        }

        private static void StageUnitySkinBinding(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping,
            BoneTable boneTable,
            string rootBonePath,
            UnityArmatureBoneData[] artifactBones)
        {
            if (context.Session == null ||
                !context.Session.TryGetState(BuildUnitySkinStateKey(context.MeshKey), out UnitySkinWeightState state))
            {
                return;
            }

            if (mapping == null)
            {
                context.Session.Fail("A valid FBX-to-Unity space conversion is required for armature generation.");
                return;
            }

            if (!context.Session.RegisterArmatureConversion(
                    feature?.Armature,
                    mapping.SpaceConversion,
                    out string conversionError))
            {
                context.Session.Fail(conversionError);
                return;
            }

            state.StageBinding(
                rootBonePath,
                boneTable,
                artifactBones,
                context.GetHandle(feature).Patch);
        }

        private static string BuildUnitySkinStateKey(string meshKey)
        {
            return $"unity-skin::{MeshNodePath.Normalize(meshKey)}";
        }

#if ENABLE_FBX_SDK
        /// <inheritdoc />
        public override MeshFeatureGenerationResult FinalizeFbxGeneration(FbxGenerationSession session)
        {
            return FinalizeFbxSkinWeights(session, out string error)
                ? MeshFeatureGenerationResult.Success(false)
                : MeshFeatureGenerationResult.FailedResult(error);
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult CanApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            foreach (var feature in features)
            {
                feature.Sanitize(context.GetHandle(feature).MeshData);
            }

            return MeshFeatureGenerationResult.Success(false);
        }

        /// <inheritdoc />
        protected override MeshFeatureGenerationResult ApplyToFbx(
            FbxMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            bool modified = false;
            foreach (var feature in features)
            {
                var result = ApplyFbxContribution(context, feature);
                if (result.Failed)
                {
                    return result;
                }

                modified |= result.Modified;
            }

            return MeshFeatureGenerationResult.Success(modified);
        }

        private static MeshFeatureGenerationResult ApplyFbxContribution(
            FbxMeshFeatureContext context,
            SkinWeightFeatureObject feature)
        {
            FbxMesh targetMesh = context.TargetMesh;
            var handle = context.GetHandle(feature);
            if (targetMesh == null)
            {
                return MeshFeatureGenerationResult.FailedResult(
                    $"Can not find mesh at path: {handle.MeshData?.m_Path} in FBX file");
            }

            feature.Sanitize(handle.MeshData);
            if (feature.ClusterCount == 0)
            {
                return MeshFeatureGenerationResult.Skipped("No skin weight clusters were stored for this mesh.");
            }

            int controlPointCount = targetMesh.GetControlPointsCount();
            if (!ValidateControlPointRange(feature, controlPointCount, out string rangeError))
            {
                return MeshFeatureGenerationResult.FailedResult(rangeError);
            }

            if (!ApplyArmatureNodesToFbx(context, feature.Armature, handle.Patch, out string armatureError))
            {
                return MeshFeatureGenerationResult.FailedResult(armatureError);
            }

            if (!TryGetCurrentSkinState(context, out var skinState, out string skinError))
            {
                return MeshFeatureGenerationResult.FailedResult(skinError);
            }

            if (!skinState.RegisterClusterMatrices(feature, handle.Patch, out skinError))
            {
                return MeshFeatureGenerationResult.FailedResult(skinError);
            }

            var deltaWeights = BuildDeltaWeights(feature);
            if (deltaWeights.Count > 0)
            {
                ApplyDeltaWeights(skinState, deltaWeights);
            }

            skinState.SetFinalContext(context, feature);
            context.Session?.SetState(BuildSkinStateKey(context), skinState);
            return MeshFeatureGenerationResult.Success();
        }

        internal static bool FinalizeFbxSkinWeights(FbxGenerationSession session, out string error)
        {
            error = null;
            if (session == null)
            {
                return true;
            }

            foreach (var state in session.GetStates<FbxSkinRebuildState>())
            {
                state.PruneFinalWeights();
                if (state.FinalContext == null ||
                    !RebuildFbxSkin(state.FinalContext, state.FinalFeature, state, out error))
                {
                    error ??= "Could not finalize accumulated FBX skin weights.";
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        protected override bool RequiresFbxReaderScene(
            FbxMeshFeatureContext context,
            IReadOnlyList<SkinWeightFeatureObject> features)
        {
            return true;
        }

        private static bool ValidateControlPointRange(
            SkinWeightFeatureObject feature,
            int controlPointCount,
            out string error)
        {
            error = null;
            foreach (var cluster in feature.Clusters ?? Array.Empty<SkinWeightClusterData>())
            {
                if (cluster == null)
                {
                    continue;
                }

                foreach (var pair in cluster.GetWeights())
                {
                    int index = pair.Key;
                    if (index >= controlPointCount)
                    {
                        error = $"Skin weight FBX vertex {index} is outside target mesh FBX vertex count {controlPointCount}.";
                        return false;
                    }
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

            foreach (var cluster in feature.Clusters ?? Array.Empty<SkinWeightClusterData>())
            {
                if (cluster == null)
                {
                    continue;
                }

                string path = cluster.BonePath;
                foreach (var pair in cluster.GetWeights())
                {
                    float delta = pair.Value;
                    if (Mathf.Abs(delta) <= WeightEpsilon)
                    {
                        continue;
                    }

                    AddWeight(result, pair.Key, path, delta);
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

        private static bool ApplyArmatureNodesToFbx(
            FbxMeshFeatureContext context,
            FbxArmatureObject armature,
            BlendShareObject patch,
            out string error)
        {
            error = null;
            if (context.Session != null)
            {
                const string stateKey = "fbx-armature-definitions";
                if (!context.Session.TryGetState(stateKey, out FbxArmatureDefinitionState state))
                {
                    state = new FbxArmatureDefinitionState();
                    context.Session.SetState(stateKey, state);
                }

                if (!state.Register(armature?.Bones, patch, out error))
                {
                    return false;
                }
            }

            foreach (var bone in armature?.Bones ?? Array.Empty<FbxArmatureBoneData>())
            {
                if (bone == null)
                {
                    continue;
                }

                var node = ResolveOrCreateFbxBone(
                    context,
                    armature,
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
            FbxMeshFeatureContext context,
            FbxArmatureObject armature,
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

            var bone = armature != null ? armature.GetBone(path) : null;
            if (bone == null || !bone.m_CreateIfMissing)
            {
                error = $"Cannot resolve FBX skin bone '{path}'.";
                return null;
            }

            if (!resolving.Add(path))
            {
                error = $"Armature contains a parent cycle at '{path}'.";
                return null;
            }

            string parentPath = bone.ParentPath;
            var parent = FindFbxNodeByPath(rootNode, parentPath) ??
                         ResolveOrCreateFbxBone(context, armature, parentPath, resolving, out error);
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
            parent.AddChild(created);
            return created;
        }

        private static void SetLocalTransform(FbxNode node, FbxArmatureBoneData bone)
        {
            if (!bone.HasTransformData)
            {
                throw new InvalidOperationException(
                    $"Bone '{bone.Path}' uses an unsupported pre-2.0 armature transform schema. Re-extract the BlendShare patch.");
            }

            node.LclTranslation.Set(new FbxDouble3(bone.m_LclTranslation.x, bone.m_LclTranslation.y, bone.m_LclTranslation.z));
            node.LclRotation.Set(new FbxDouble3(bone.m_LclRotation.x, bone.m_LclRotation.y, bone.m_LclRotation.z));
            node.LclScaling.Set(new FbxDouble3(bone.m_LclScaling.x, bone.m_LclScaling.y, bone.m_LclScaling.z));
            node.SetRotationOrder(FbxNode.EPivotSet.eSourcePivot, ToSdkRotationOrder(bone.m_RotationOrder));
            node.SetTransformationInheritType(ToSdkInheritType(bone.m_InheritMode));
            node.SetRotationActive(bone.m_RotationActive);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_PreRotation));
            node.SetPostRotation(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_PostRotation));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_RotationPivot));
            node.SetScalingPivot(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_ScalingPivot));
            node.SetRotationOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_RotationOffset));
            node.SetScalingOffset(FbxNode.EPivotSet.eSourcePivot, ToFbxVector4(bone.m_ScalingOffset));
        }

        private static FbxVector4 ToFbxVector4(Vector3d value)
        {
            return new FbxVector4(value.x, value.y, value.z);
        }

        private static FbxEuler.EOrder ToSdkRotationOrder(FbxRotationOrder order)
        {
            return order switch
            {
                FbxRotationOrder.XZY => FbxEuler.EOrder.eOrderXZY,
                FbxRotationOrder.YZX => FbxEuler.EOrder.eOrderYZX,
                FbxRotationOrder.YXZ => FbxEuler.EOrder.eOrderYXZ,
                FbxRotationOrder.ZXY => FbxEuler.EOrder.eOrderZXY,
                FbxRotationOrder.ZYX => FbxEuler.EOrder.eOrderZYX,
                FbxRotationOrder.SphericXYZ => FbxEuler.EOrder.eOrderSphericXYZ,
                _ => FbxEuler.EOrder.eOrderXYZ
            };
        }

        private static Autodesk.Fbx.FbxTransform.EInheritType ToSdkInheritType(FbxTransformInheritMode mode)
        {
            return mode switch
            {
                FbxTransformInheritMode.RrSs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrSs,
                FbxTransformInheritMode.Rrs => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRrs,
                _ => Autodesk.Fbx.FbxTransform.EInheritType.eInheritRSrs
            };
        }

        private static bool TryGetCurrentSkinState(
            FbxMeshFeatureContext context,
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
                error = $"Original FBX mesh '{context.MeshKey}' does not contain a skin deformer to rebuild.";
                return false;
            }

            state = FbxSkinRebuildState.FromUfbxSkin(readerSkin, context.TargetMesh.GetControlPointsCount());
            context.Session?.SetState(stateKey, state);
            return true;
        }

        private static string BuildSkinStateKey(FbxMeshFeatureContext context)
        {
            return $"fbx-skin::{MeshNodePath.Normalize(context?.MeshKey)}";
        }

        private static string FormatPatchName(BlendShareObject patch)
        {
            return patch != null ? patch.name : "<unknown>";
        }

        private static UfbxMesh GetReaderMesh(
            FbxMeshFeatureContext context,
            out string error)
        {
            error = null;
            var scene = context.Session?.ReaderScene;
            if (scene == null)
            {
                error = "Original FBX skin data was not available. Skin weights require ufbx scene data so the primary skin deformer can be rebuilt.";
                return null;
            }

            var mesh = scene.FindMeshByNodePath(context.MeshKey);
            if (mesh == null)
            {
                error = $"FBX mesh node '{context.MeshKey}' was not found.";
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
                    weights[path] = existing + deltaPair.Value;
                }
            }
        }

        private static Dictionary<int, Dictionary<string, float>> InvertDeltaWeights(
            IReadOnlyDictionary<int, Dictionary<string, float>> deltaWeights)
        {
            var result = new Dictionary<int, Dictionary<string, float>>();
            foreach (var controlPointPair in deltaWeights ?? new Dictionary<int, Dictionary<string, float>>())
            {
                var inverted = new Dictionary<string, float>();
                foreach (var deltaPair in controlPointPair.Value ?? new Dictionary<string, float>())
                {
                    inverted[deltaPair.Key] = -deltaPair.Value;
                }

                result[controlPointPair.Key] = inverted;
            }

            return result;
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
            FbxMeshFeatureContext context,
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
                    boneNode = ResolveOrCreateFbxBone(context, feature.Armature, data.Path, new HashSet<string>(), out error);
                }

                if (boneNode == null)
                {
                    return false;
                }

                var cluster = SdkCluster.Create(targetMesh.GetFbxManager(), data.Name);
                cluster.SetLink(boneNode);
                cluster.SetLinkMode(SdkCluster.ELinkMode.eNormalize);
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
            FbxMeshFeatureContext context,
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
                state.ApplyStoredClusterMatrices(data);
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
                    boneNode = ResolveOrCreateFbxBone(context, feature.Armature, path, new HashSet<string>(), out error);
                }

                if (boneNode == null)
                {
                    return null;
                }

                var matrices = ResolveClusterMatrices(context, state, path, boneNode);
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
            FbxMeshFeatureContext context,
            FbxSkinRebuildState state,
            string path,
            FbxNode boneNode)
        {
            var meshMatrix = context.Node.EvaluateGlobalTransform();
            var linkMatrix = boneNode.EvaluateGlobalTransform();
            if (state.TryGetClusterMatrices(path, out var storedMatrices))
            {
                return storedMatrices;
            }

            return new ClusterMatrices(meshMatrix, linkMatrix);
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

        private static FbxNode GetRootNode(FbxMeshFeatureContext context)
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

        private sealed class FbxArmatureDefinitionState
        {
            private readonly Dictionary<string, FbxArmatureBoneData> definitionsByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<string, BlendShareObject> sourcesByPath = new(StringComparer.Ordinal);

            public bool Register(
                IEnumerable<FbxArmatureBoneData> bones,
                BlendShareObject patch,
                out string error)
            {
                foreach (var bone in bones ?? Array.Empty<FbxArmatureBoneData>())
                {
                    if (bone == null)
                    {
                        continue;
                    }

                    string path = MeshNodePath.Normalize(bone.m_Path);
                    if (definitionsByPath.TryGetValue(path, out var existing) &&
                        !AreCompatibleBoneDefinitions(existing, bone))
                    {
                        sourcesByPath.TryGetValue(path, out var existingPatch);
                        error = $"Bone path '{path}' has incompatible definitions in patches " +
                                $"'{FormatPatchName(existingPatch)}' and '{FormatPatchName(patch)}'.";
                        return false;
                    }

                    if (!definitionsByPath.ContainsKey(path))
                    {
                        definitionsByPath.Add(path, bone);
                        sourcesByPath[path] = patch;
                    }
                }

                error = null;
                return true;
            }

            private static bool AreCompatibleBoneDefinitions(FbxArmatureBoneData first, FbxArmatureBoneData second)
            {
                return first != null && first.ApproximatelyTransform(second);
            }
        }

        private sealed class FbxSkinRebuildState
        {
            private readonly List<Dictionary<string, double>> weightsByControlPoint;
            private readonly Dictionary<string, ClusterMatrices> clusterMatricesByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Triturbo.BlendShare.Fbx.FbxMatrix4x4> relativeBindPosesByPath = new(StringComparer.Ordinal);
            private readonly Dictionary<string, BlendShareObject> bindPoseSourcesByPath = new(StringComparer.Ordinal);

            public string SkinName { get; }
            public int ControlPointCount { get; }
            public IReadOnlyList<FbxOriginalClusterData> Clusters { get; }
            public FbxMeshFeatureContext FinalContext { get; private set; }
            public SkinWeightFeatureObject FinalFeature { get; private set; }

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

            public bool RegisterClusterMatrices(
                SkinWeightFeatureObject feature,
                BlendShareObject patch,
                out string error)
            {
                error = null;
                foreach (var cluster in feature?.Clusters ?? Array.Empty<SkinWeightClusterData>())
                {
                    if (cluster == null || !cluster.m_HasFbxClusterMatrices)
                    {
                        continue;
                    }

                    string path = cluster.BonePath;
                    if (!cluster.m_FbxTransformLinkMatrix.TryInverse(out var inverseLink))
                    {
                        error = $"Cannot calculate bindpose for bone '{path}' because its FBX TransformLink matrix is singular.";
                        return false;
                    }

                    var relativeBindPose = cluster.m_FbxTransformMatrix * inverseLink;
                    if (relativeBindPosesByPath.TryGetValue(path, out var existing) &&
                        !existing.Approximately(relativeBindPose, 0.0001d))
                    {
                        bindPoseSourcesByPath.TryGetValue(path, out var existingPatch);
                        error = $"Bone path '{path}' has incompatible bindposes in patches " +
                                $"'{FormatPatchName(existingPatch)}' and '{FormatPatchName(patch)}'.";
                        return false;
                    }

                    if (!relativeBindPosesByPath.ContainsKey(path))
                    {
                        relativeBindPosesByPath.Add(path, relativeBindPose);
                        bindPoseSourcesByPath[path] = patch;
                    }

                    clusterMatricesByPath[path] = new ClusterMatrices(
                        ToFbxAMatrix(cluster.m_FbxTransformMatrix),
                        ToFbxAMatrix(cluster.m_FbxTransformLinkMatrix));
                }

                return true;
            }

            public void SetFinalContext(FbxMeshFeatureContext context, SkinWeightFeatureObject feature)
            {
                FinalContext = context;
                FinalFeature = feature;
            }

            public bool TryGetClusterMatrices(string path, out ClusterMatrices matrices)
            {
                return clusterMatricesByPath.TryGetValue(MeshNodePath.Normalize(path), out matrices);
            }

            public void ApplyStoredClusterMatrices(FbxClusterRebuildData data)
            {
                if (data != null && TryGetClusterMatrices(data.Path, out var matrices))
                {
                    data.TransformMatrix = matrices.TransformMatrix;
                    data.TransformLinkMatrix = matrices.TransformLinkMatrix;
                }
            }

            public void PruneFinalWeights()
            {
                foreach (var weights in weightsByControlPoint)
                {
                    PruneControlPointWeights(weights);
                }
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
            public FbxAMatrix TransformMatrix { get; }
            public FbxAMatrix TransformLinkMatrix { get; }
            public Dictionary<int, double> WeightsByControlPoint { get; }

            private FbxOriginalClusterData(
                string path,
                string name,
                FbxAMatrix transformMatrix,
                FbxAMatrix transformLinkMatrix,
                Dictionary<int, double> weightsByControlPoint)
            {
                Path = MeshNodePath.Normalize(path);
                Name = string.IsNullOrWhiteSpace(name) ? $"{MeshNodePath.LeafName(Path)}Cluster" : name;
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
                    ToFbxAMatrix(cluster.MeshBindWorld),
                    ToFbxAMatrix(cluster.BindToWorld),
                    weightsByControlPoint);
            }
        }

        private sealed class FbxClusterRebuildData
        {
            public string Path { get; private set; }
            public string Name { get; private set; }
            public FbxAMatrix TransformMatrix { get; set; }
            public FbxAMatrix TransformLinkMatrix { get; set; }
            public Dictionary<int, double> WeightsByControlPoint { get; } = new();

            public static FbxClusterRebuildData FromOriginalCluster(FbxOriginalClusterData cluster, FbxAMatrix meshBindMatrix)
            {
                return new FbxClusterRebuildData
                {
                    Path = cluster.Path,
                    Name = cluster.Name,
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
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            Mesh mesh,
            UnityVertexMappingObject mapping,
            out string error)
        {
            error = null;
            var table = new BoneTable();
            var existingBindPoses = mesh.bindposes ?? Array.Empty<Matrix4x4>();
            if (context.Session != null &&
                context.Session.TryGetSkinBinding(context.MeshKey, out var previousBinding))
            {
                // The working mesh may already contain bones added by an earlier patch. Preserve
                // its path-to-index table so subsequent patches cannot reinterpret those weights.
                for (int i = 0; i < previousBinding.BonePaths.Length; i++)
                {
                    string path = previousBinding.BonePaths[i];
                    var bindPose = i < existingBindPoses.Length ? existingBindPoses[i] : Matrix4x4.identity;
                    context.Session.BoneTransformsByArtifactPath.TryGetValue(
                        MeshNodePath.Normalize(path),
                        out var previousTransform);
                    table.Add(previousTransform, path, bindPose, i);
                }
            }
            else
            {
                var existingBones = context.TargetRenderer.bones ?? Array.Empty<Transform>();
                for (int i = 0; i < existingBones.Length; i++)
                {
                    var bone = existingBones[i];
                    var bindPose = i < existingBindPoses.Length ? existingBindPoses[i] : Matrix4x4.identity;
                    if (bone == null)
                    {
                        table.AddSynthetic($".__missing_bone_{i}", bindPose, i);
                        continue;
                    }

                    string path = MeshNodePath.GetRelativePath(bone, context.TargetRootTransform);
                    table.Add(bone, path, bindPose, i);
                }
            }

            foreach (string path in feature.GetNeededBonePathsInArmatureOrder())
            {
                string finalPath = path;
                Transform finalTransform = null;
                if (TryGetBoneProxyData(context, feature, path, out var boneProxy))
                {
                    finalPath = boneProxy.FinalBonePath;
                    finalTransform = boneProxy.FinalTransform;
                }
                finalPath = context.Session?.ResolveBoneBindingArtifactPath(
                    boneProxy?.Binding,
                    finalTransform,
                    finalPath) ?? finalPath;

                if (table.TryGetIndex(finalTransform, finalPath, out _))
                {
                    if (finalTransform != null || DefinesBindPose(context, feature, path))
                    {
                        if (!TryResolveBindPose(context, feature, path, mapping, out var updatedBindPose, out error) ||
                            !RegisterBindPose(
                                context,
                                feature,
                                finalPath,
                                updatedBindPose,
                                finalTransform != null,
                                out error))
                        {
                            return null;
                        }

                        table.SetDefinition(finalTransform, finalPath, updatedBindPose);
                    }

                    table.AddAlias(path, finalTransform, finalPath);
                    continue;
                }

                if (!TryResolveBindPose(context, feature, path, mapping, out var bindPose, out error))
                {
                    return null;
                }

                table.Add(finalTransform, finalPath, bindPose);
                table.AddAlias(path, finalTransform, finalPath);
                if (DefinesBindPose(context, feature, path) &&
                    !RegisterBindPose(
                        context,
                        feature,
                        finalPath,
                        bindPose,
                        finalTransform != null,
                        out error))
                {
                    return null;
                }
            }

            return table;
        }

        private static bool DefinesBindPose(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            string sourcePath)
        {
            return context?.GetComponent<BlendShareMesh>(feature)?.TryGetBindPoseOverride(sourcePath, out _) == true ||
                   feature.TryGetBindPoseFbxClusterMatrices(sourcePath, out _, out _);
        }

        private static bool RegisterBindPose(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            string finalPath,
            Matrix4x4 bindPose,
            bool replaceExisting,
            out string error)
        {
            if (context.Session == null ||
                context.Session.RegisterBindPose(
                    context.MeshKey,
                    finalPath,
                    bindPose,
                    context.GetHandle(feature).Patch,
                    replaceExisting,
                    out error))
            {
                error = null;
                return true;
            }

            context.Session.Fail(error);
            return false;
        }

        private static bool TryResolveBindPose(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            string path,
            UnityVertexMappingObject mapping,
            out Matrix4x4 bindPose,
            out string error)
        {
            bindPose = Matrix4x4.identity;
            if (context?.GetComponent<BlendShareMesh>(feature)?.TryGetBindPoseOverride(path, out bindPose) == true)
            {
                error = null;
                return true;
            }

            if (TryResolveStoredBindPose(feature, path, bindPose, mapping, out bindPose, out error))
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
            Matrix4x4 currentBindPose,
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

                bindPose = mapping.SpaceConversion.ConvertMatrix(fbxTransformMatrix * inverseTransformLinkMatrix);
                return true;
            }

            return false;
        }

        private static bool TryResolveBoneLocalToWorld(
            UnityMeshFeatureContext context,
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
                boneProxy.TryGetLocalToWorld(out localToWorld))
            {
                return true;
            }

            var armature = feature?.Armature;
            var armatureBone = armature != null ? armature.GetBone(path) : null;
            if (armatureBone == null)
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
                error = $"Armature contains a parent cycle at '{path}'.";
                return false;
            }

            string parentPath = armatureBone.ParentPath;
            if (!TryResolveBoneLocalToWorld(context, feature, parentPath, mapping, resolving, out var parentLocalToWorld, out error))
            {
                resolving.Remove(path);
                localToWorld = Matrix4x4.identity;
                return false;
            }

            resolving.Remove(path);
            if (!armatureBone.HasTransformData)
            {
                localToWorld = Matrix4x4.identity;
                error = $"Cannot convert armature bone '{path}': missing FBX transform data. Re-extract the BlendShare patch.";
                return false;
            }

            if (mapping == null)
            {
                localToWorld = Matrix4x4.identity;
                error = $"Cannot convert armature bone '{path}': missing FBX-to-Unity conversion.";
                return false;
            }

            if (!mapping.SpaceConversion.TryConvertLocalTransform(
                    armatureBone.EvaluatedNodeToParentMatrix,
                    out UnityLocalTransform localTransform,
                    out error))
            {
                localToWorld = Matrix4x4.identity;
                error = $"Cannot convert armature bone '{path}': {error}";
                return false;
            }

            localToWorld = parentLocalToWorld * localTransform.ToMatrix();
            return true;
        }

        private static bool TryGetBoneProxyData(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            string sourceBonePath,
            out BoneProxyGenerationData data)
        {
            data = null;
            var mesh = context?.GetComponent<BlendShareMesh>(feature);
            if (mesh == null ||
                !mesh.TryGetCachedBone(sourceBonePath, out var resolved) ||
                !resolved.IsProxy ||
                resolved.Proxy.SourceArmature != feature?.Armature)
            {
                return false;
            }
            var proxy = resolved.Proxy;
            var binding = resolved.Binding;

            var finalTransform = proxy?.GetFinalTransform(binding);
            var effectiveParent = proxy?.GetEffectiveParent(binding);
            if (proxy == null || binding == null || finalTransform == null || effectiveParent == null)
            {
                return false;
            }

            string finalPath = proxy.GetFinalPath(binding, context.TargetRootTransform);
            string parentPath = MeshNodePath.ParentPath(finalPath);
            GetProxyLocalTransform(
                proxy,
                binding,
                out var localPosition,
                out var localEulerRotation,
                out var localScale);
            data = new BoneProxyGenerationData(
                finalPath,
                parentPath,
                proxy,
                binding,
                finalTransform,
                effectiveParent,
                localPosition,
                localEulerRotation,
                localScale);
            return true;
        }

        private static UnityArmatureBoneData[] BuildArtifactBones(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping)
        {
            if (context == null || feature?.Armature == null)
            {
                return Array.Empty<UnityArmatureBoneData>();
            }

            return (feature.Armature.Bones ?? Array.Empty<FbxArmatureBoneData>())
                .Where(bone => bone != null)
                .Select(bone => CreateArtifactBoneNode(context, feature, mapping, bone))
                .ToArray();
        }

        private static UnityArmatureBoneData CreateArtifactBoneNode(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            UnityVertexMappingObject mapping,
            FbxArmatureBoneData bone)
        {
            if (TryGetProxyBoneNode(context, feature, bone.m_Path, out var proxyBone))
            {
                return proxyBone;
            }

            if (mapping == null)
            {
                context.Session?.Fail($"Cannot convert armature bone '{bone.Path}': missing FBX-to-Unity conversion.");
                return null;
            }

            if (!bone.HasTransformData)
            {
                context.Session?.Fail($"Cannot convert armature bone '{bone.Path}': missing FBX transform data. Re-extract the BlendShare patch.");
                return null;
            }

            if (!mapping.SpaceConversion.TryConvertLocalTransform(
                    bone.EvaluatedNodeToParentMatrix,
                    out var localTransform,
                    out var diagnostic))
            {
                context.Session?.Fail($"Cannot convert armature bone '{bone.Path}': {diagnostic}");
                return null;
            }

            return new UnityArmatureBoneData(bone.m_Path, localTransform, bone.m_CreateIfMissing);
        }

        private static bool TryGetProxyBoneNode(
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature,
            string sourceBonePath,
            out UnityArmatureBoneData boneNode)
        {
            boneNode = null;
            if (!TryGetBoneProxyData(context, feature, sourceBonePath, out var boneProxy))
            {
                return false;
            }

            var representative = context.Session?.ResolveBoneBindingUse(boneProxy.FinalTransform);
            if (representative != null)
            {
                boneNode = new UnityArmatureBoneData(
                    context.Session.ResolveBoneBindingArtifactPath(
                        representative.Binding,
                        representative.Transform,
                        representative.FinalPath),
                    ToLocalTransform(representative.Transform, context.TargetRootTransform),
                    true);
                return true;
            }

            GetProxyLocalTransform(
                boneProxy.Proxy,
                boneProxy.Binding,
                out var localPosition,
                out var localEulerRotation,
                out var localScale);
            boneNode = new UnityArmatureBoneData(
                boneProxy.FinalBonePath,
                new UnityLocalTransform(localPosition, Quaternion.Euler(localEulerRotation), localScale),
                true);
            return true;
        }

        private static UnityLocalTransform ToLocalTransform(Transform transform, Transform fallbackParent)
        {
            var parent = transform != null ? transform.parent : null;
            if (parent == null)
            {
                parent = fallbackParent;
            }

            if (transform == null || parent == null)
            {
                return new UnityLocalTransform(Vector3.zero, Quaternion.identity, Vector3.one);
            }

            return new UnityLocalTransform(
                parent.InverseTransformPoint(transform.position),
                Quaternion.Inverse(parent.rotation) * transform.rotation,
                DivideScale(transform.lossyScale, parent.lossyScale));
        }

        private static Vector3 DivideScale(Vector3 value, Vector3 divisor)
        {
            return new Vector3(
                Mathf.Approximately(divisor.x, 0f) ? value.x : value.x / divisor.x,
                Mathf.Approximately(divisor.y, 0f) ? value.y : value.y / divisor.y,
                Mathf.Approximately(divisor.z, 0f) ? value.z : value.z / divisor.z);
        }

        private static void GetProxyLocalTransform(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyBinding binding,
            out Vector3 localPosition,
            out Vector3 localEulerRotation,
            out Vector3 localScale)
        {
            if (proxy != null &&
                proxy.TryGetCurrentLocalTransform(binding, out localPosition, out localEulerRotation, out localScale))
            {
                localScale = localScale == Vector3.zero ? Vector3.one : localScale;
                return;
            }

            localPosition = Vector3.zero;
            localEulerRotation = Vector3.zero;
            localScale = Vector3.one;
        }

        private sealed class BoneProxyGenerationData
        {
            public string FinalBonePath { get; }
            public string ParentPath { get; }
            public BlendShareBoneProxy Proxy { get; }
            public BlendShareBoneProxyBinding Binding { get; }
            public Transform FinalTransform { get; }
            public Transform TargetParent { get; }
            public Vector3 LocalPosition { get; }
            public Vector3 LocalEulerRotation { get; }
            public Vector3 LocalScale { get; }
            public BoneProxyGenerationData(
                string finalBonePath,
                string parentPath,
                BlendShareBoneProxy proxy,
                BlendShareBoneProxyBinding binding,
                Transform finalTransform,
                Transform targetParent,
                Vector3 localPosition,
                Vector3 localEulerRotation,
                Vector3 localScale)
            {
                FinalBonePath = MeshNodePath.Normalize(finalBonePath);
                ParentPath = MeshNodePath.Normalize(parentPath);
                Proxy = proxy;
                Binding = binding;
                FinalTransform = finalTransform;
                TargetParent = targetParent;
                LocalPosition = localPosition;
                LocalEulerRotation = localEulerRotation;
                LocalScale = localScale == Vector3.zero ? Vector3.one : localScale;
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
            UnityMeshFeatureContext context,
            SkinWeightFeatureObject feature)
        {
            string rootBonePath = feature.RootBonePath;
            if (rootBonePath != MeshNodePath.Root &&
                TryGetBoneProxyData(context, feature, rootBonePath, out var proxyData))
            {
                return context.Session?.ResolveBoneBindingArtifactPath(
                           proxyData.Binding,
                           proxyData.FinalTransform,
                           proxyData.FinalBonePath) ??
                       proxyData.FinalBonePath;
            }

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
            using var targetWeights = mesh.GetAllBoneWeights();
            using var targetCounts = mesh.GetBonesPerVertex();
            using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
            using var outputCounts = new NativeList<byte>(Allocator.Temp);
            bool limitToFour = BlendShareSkinWeightSettings.LimitSkinWeightsToFour;
            int influenceLimit = limitToFour ? 4 : byte.MaxValue;
            int maximumInputInfluences = 0;
            var selectedWeights = new List<(int index, float weight)>(influenceLimit);
            var deltaIndex = BuildDeltaIndex(feature, boneTable);
            int targetWeightIndex = 0;
            for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
            {
                if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                {
                    error = $"Cannot map Unity vertex {unityIndex} to FBX vertices.";
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
                ApplyIndexedDeltas(aggregate, group, deltaIndex);
                int positiveInfluenceCount = KeepTopWeights(
                    influenceLimit,
                    aggregate,
                    selectedWeights);
                maximumInputInfluences = Math.Max(maximumInputInfluences, positiveInfluenceCount);
                foreach (var weight in selectedWeights)
                {
                    outputWeights.Add(new BoneWeight1 { boneIndex = weight.index, weight = weight.weight });
                }

                outputCounts.Add((byte)selectedWeights.Count);
            }

            using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
            using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
            if (!limitToFour &&
                maximumInputInfluences > 4 &&
                UnitySkinWeightMeshLayoutUtility.RequiresUnlimitedLayoutRebuild(mesh))
            {
                UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
            }

            mesh.SetBoneWeights(outputCountArray, outputWeightArray);
            return true;
        }

        private static Dictionary<int, List<IndexedWeightDelta>> BuildDeltaIndex(
            SkinWeightFeatureObject feature,
            BoneTable boneTable)
        {
            return BuildDeltaIndex(feature, boneTable.TryGetIndex);
        }

        internal static Dictionary<int, List<IndexedWeightDelta>> BuildDeltaIndex(
            SkinWeightFeatureObject feature,
            TryGetBoneIndex tryGetBoneIndex)
        {
            var result = new Dictionary<int, List<IndexedWeightDelta>>();
            if (feature == null || tryGetBoneIndex == null)
            {
                return result;
            }

            foreach (var cluster in feature.Clusters ?? Array.Empty<SkinWeightClusterData>())
            {
                if (cluster == null || cluster.WeightCount == 0)
                {
                    continue;
                }

                if (!tryGetBoneIndex(cluster.BonePath, out int boneIndex))
                {
                    boneIndex = -1;
                }

                foreach (var pair in cluster.GetWeights())
                {
                    int controlPointIndex = pair.Key;
                    float delta = pair.Value;
                    if (controlPointIndex < 0 || Mathf.Abs(delta) <= WeightEpsilon)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(controlPointIndex, out var deltas))
                    {
                        deltas = new List<IndexedWeightDelta>();
                        result[controlPointIndex] = deltas;
                    }

                    deltas.Add(new IndexedWeightDelta(boneIndex, delta));
                }
            }

            return result;
        }

        internal static void ApplyIndexedDeltas(
            Dictionary<int, float> aggregate,
            FbxIndexGroup group,
            IReadOnlyDictionary<int, List<IndexedWeightDelta>> deltaIndex)
        {
            foreach (var pair in CalculateIndexedDeltas(group, deltaIndex))
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

        internal static void AccumulateRawDeltas(
            Dictionary<int, float> aggregate,
            IReadOnlyDictionary<int, float> deltas)
        {
            foreach (var pair in deltas ?? new Dictionary<int, float>())
            {
                aggregate.TryGetValue(pair.Key, out float existing);
                aggregate[pair.Key] = existing + pair.Value;
            }
        }

        private static Dictionary<int, float> CalculateIndexedDeltas(
            FbxIndexGroup group,
            IReadOnlyDictionary<int, List<IndexedWeightDelta>> deltaIndex)
        {
            int contributingControlPoints = 0;
            var deltaByBoneSlot = new Dictionary<int, float>();
            foreach (int controlPointIndex in group.m_Indices ?? Array.Empty<int>())
            {
                if (controlPointIndex < 0 || deltaIndex == null ||
                    !deltaIndex.TryGetValue(controlPointIndex, out var deltas) ||
                    deltas == null || deltas.Count == 0)
                {
                    continue;
                }

                bool contributed = false;
                foreach (var delta in deltas)
                {
                    if (Mathf.Abs(delta.Weight) <= WeightEpsilon)
                    {
                        continue;
                    }

                    contributed = true;
                    if (delta.BoneIndex < 0)
                    {
                        continue;
                    }

                    deltaByBoneSlot.TryGetValue(delta.BoneIndex, out float existing);
                    deltaByBoneSlot[delta.BoneIndex] = existing + delta.Weight;
                }

                if (contributed)
                {
                    contributingControlPoints++;
                }
            }

            if (contributingControlPoints > 1)
            {
                foreach (int boneIndex in deltaByBoneSlot.Keys.ToArray())
                {
                    deltaByBoneSlot[boneIndex] /= contributingControlPoints;
                }
            }

            return deltaByBoneSlot;
        }

        internal readonly struct IndexedWeightDelta
        {
            public readonly int BoneIndex;
            public readonly float Weight;

            public IndexedWeightDelta(int boneIndex, float weight)
            {
                BoneIndex = boneIndex;
                Weight = weight;
            }
        }

        internal delegate bool TryGetBoneIndex(string bonePath, out int boneIndex);

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

        internal static int KeepTopWeights(
            int maximumInfluences,
            IReadOnlyDictionary<int, float> aggregate,
            List<(int index, float weight)> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            if (aggregate != null)
            {
                foreach (var pair in aggregate)
                {
                    if (pair.Key >= 0 && pair.Value > 0f &&
                        !float.IsNaN(pair.Value) && !float.IsInfinity(pair.Value))
                    {
                        output.Add((pair.Key, pair.Value));
                    }
                }
            }

            output.Sort(InfluenceComparison);

            int positiveInfluenceCount = output.Count;
            int limit = Mathf.Clamp(maximumInfluences, 1, byte.MaxValue);
            if (output.Count > limit)
            {
                output.RemoveRange(limit, output.Count - limit);
            }

            if (output.Count == 0)
            {
                output.Add((0, 1f));
                return positiveInfluenceCount;
            }

            float total = 0f;
            for (int index = 0; index < output.Count; index++)
            {
                total += output[index].weight;
            }

            if (!(total > 0f) || float.IsInfinity(total))
            {
                int strongestBoneIndex = output[0].index;
                output.Clear();
                output.Add((strongestBoneIndex, 1f));
                return positiveInfluenceCount;
            }

            for (int index = 0; index < output.Count; index++)
            {
                var weight = output[index];
                output[index] = (weight.index, weight.weight / total);
            }

            return positiveInfluenceCount;
        }

        internal static IReadOnlyList<int> BuildTransformBoneIndexMap(
            IReadOnlyList<Transform> bones)
        {
            var result = new int[bones?.Count ?? 0];
            var indices = new Dictionary<Transform, int>();
            int nextIndex = 0;
            for (int index = 0; index < result.Length; index++)
            {
                var bone = bones[index];
                if (bone == null || !indices.TryGetValue(bone, out int targetIndex))
                {
                    targetIndex = nextIndex++;
                    if (bone != null)
                    {
                        indices[bone] = targetIndex;
                    }
                }

                result[index] = targetIndex;
            }

            return result;
        }

        private static int CompareInfluences(
            (int index, float weight) first,
            (int index, float weight) second)
        {
            int weightOrder = second.weight.CompareTo(first.weight);
            return weightOrder != 0 ? weightOrder : first.index.CompareTo(second.index);
        }

        private sealed class UnitySkinWeightState
        {
            private readonly string meshKey;
            private readonly List<Dictionary<int, float>> weightsByVertex;
            private Dictionary<int, Dictionary<int, float>> pendingDeltas;
            private Mesh pendingMesh;
            private BoneTable pendingBoneTable;
            private string pendingRootBonePath;
            private UnityArmatureBoneData[] pendingArtifactBones;
            private BlendShareObject pendingPatch;
            private BlendShareMesh[] pendingComponents;
            private Mesh mesh;
            private BoneTable boneTable;
            private bool hasCommittedWeights;
            private readonly HashSet<BlendShareMesh> components = new();

            public UnitySkinWeightState(string meshKey, Mesh sourceMesh, BoneTable sourceBoneTable)
            {
                this.meshKey = MeshNodePath.Normalize(meshKey);
                mesh = sourceMesh;
                weightsByVertex = ReadWeights(sourceMesh, sourceBoneTable);
            }

            public bool Stage(
                Mesh targetMesh,
                SkinWeightFeatureObject feature,
                UnityVertexMappingObject mapping,
                BoneTable targetBoneTable,
                IEnumerable<BlendShareMesh> sourceComponents,
                out string error)
            {
                error = null;
                if (targetMesh == null || targetMesh.vertexCount != weightsByVertex.Count)
                {
                    error = "Skin weight patches targeting the same mesh resolved to different Unity vertex counts.";
                    return false;
                }

                var deltaIndex = BuildDeltaIndex(feature, targetBoneTable);
                var staged = new Dictionary<int, Dictionary<int, float>>();
                for (int unityIndex = 0; unityIndex < targetMesh.vertexCount; unityIndex++)
                {
                    if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                    {
                        error = $"Cannot map Unity vertex {unityIndex} to FBX vertices.";
                        return false;
                    }

                    var deltas = CalculateIndexedDeltas(group, deltaIndex);
                    if (deltas.Count > 0)
                    {
                        staged[unityIndex] = deltas;
                    }
                }

                pendingDeltas = staged;
                pendingMesh = targetMesh;
                pendingBoneTable = targetBoneTable;
                pendingComponents = (sourceComponents ?? Enumerable.Empty<BlendShareMesh>())
                    .Where(component => component != null)
                    .Distinct()
                    .ToArray();
                return true;
            }

            public void StageBinding(
                string rootBonePath,
                BoneTable targetBoneTable,
                UnityArmatureBoneData[] artifactBones,
                BlendShareObject patch)
            {
                pendingRootBonePath = rootBonePath;
                pendingBoneTable = targetBoneTable;
                pendingArtifactBones = artifactBones ?? Array.Empty<UnityArmatureBoneData>();
                pendingPatch = patch;
            }

            public void Commit(UnityMeshGenerationSession session)
            {
                if (pendingDeltas == null || pendingBoneTable == null)
                {
                    return;
                }

                foreach (var vertexPair in pendingDeltas)
                {
                    var aggregate = weightsByVertex[vertexPair.Key];
                    AccumulateRawDeltas(aggregate, vertexPair.Value);
                }

                mesh = pendingMesh;
                boneTable = pendingBoneTable;
                hasCommittedWeights = true;
                foreach (var component in pendingComponents ?? Array.Empty<BlendShareMesh>())
                {
                    components.Add(component);
                }
                // Bindpose and path tables may grow between patches even though weights are
                // intentionally written only once after the final patch.
                mesh.bindposes = boneTable.BindPoses.ToArray();
                session.SetSkinBinding(
                    meshKey,
                    pendingRootBonePath,
                    boneTable.BonePaths);
                for (int index = 0; index < boneTable.BonePaths.Count; index++)
                {
                    session.RegisterBoneArtifactTransform(
                        boneTable.BonePaths[index],
                        boneTable.GetTransform(index));
                }
                session.AddArmatureBones(pendingArtifactBones, pendingPatch);
                Discard();
            }

            public void Discard()
            {
                pendingDeltas = null;
                pendingMesh = null;
                pendingBoneTable = null;
                pendingRootBonePath = null;
                pendingArtifactBones = null;
                pendingPatch = null;
                pendingComponents = null;
            }

            public bool FinalizeWeights(out string error)
            {
                error = null;
                if (!hasCommittedWeights)
                {
                    return true;
                }

                if (mesh == null || boneTable == null || mesh.vertexCount != weightsByVertex.Count)
                {
                    error = "Could not finalize accumulated Unity skin weights because the target mesh state changed.";
                    return false;
                }

                using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
                using var outputCounts = new NativeList<byte>(Allocator.Temp);
                bool limitToFour = BlendShareSkinWeightSettings.LimitSkinWeightsToFour;
                int influenceLimit = limitToFour ? 4 : byte.MaxValue;
                var selectedWeights = new List<(int index, float weight)>(influenceLimit);
                int verticesOverFour = 0;
                int verticesOverByteLimit = 0;
                int maximumInputInfluences = 0;
                foreach (var aggregate in weightsByVertex)
                {
                    int positiveInfluenceCount = KeepTopWeights(
                        influenceLimit,
                        aggregate,
                        selectedWeights);
                    maximumInputInfluences = Math.Max(maximumInputInfluences, positiveInfluenceCount);
                    if (positiveInfluenceCount > 4)
                    {
                        verticesOverFour++;
                    }

                    if (positiveInfluenceCount > byte.MaxValue)
                    {
                        verticesOverByteLimit++;
                    }

                    foreach (var weight in selectedWeights)
                    {
                        if (weight.index < 0 || weight.index >= boneTable.BonePaths.Count)
                        {
                            error = $"Accumulated skin weight references missing bone index {weight.index}.";
                            return false;
                        }

                        outputWeights.Add(new BoneWeight1 { boneIndex = weight.index, weight = weight.weight });
                    }

                    outputCounts.Add((byte)selectedWeights.Count);
                }

                LogInfluenceSummary(
                    mesh,
                    !limitToFour,
                    verticesOverFour,
                    verticesOverByteLimit,
                    maximumInputInfluences);
                BlendShareSkinWeightInfluenceDiagnostics.Set(
                    components,
                    new BlendShareSkinWeightInfluenceSummary(
                        verticesOverFour,
                        verticesOverByteLimit,
                        maximumInputInfluences));
                using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
                using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
                // Unity 2022 cannot migrate an imported Float32 skin-weight channel in place
                // when SetBoneWeights selects the unlimited UNorm16/UInt16 layout.
                if (!limitToFour &&
                    maximumInputInfluences > 4 &&
                    UnitySkinWeightMeshLayoutUtility.RequiresUnlimitedLayoutRebuild(mesh))
                {
                    UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
                }

                mesh.bindposes = boneTable.BindPoses.ToArray();
                mesh.SetBoneWeights(outputCountArray, outputWeightArray);
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
                return true;
            }

            private static void LogInfluenceSummary(
                Mesh targetMesh,
                bool preserveAll,
                int verticesOverFour,
                int verticesOverByteLimit,
                int maximumInfluences)
            {
                string meshName = targetMesh != null ? targetMesh.name : "<unknown>";
                if (preserveAll && verticesOverByteLimit > 0)
                {
                    Debug.LogWarning(
                        $"[BlendShare] Mesh '{meshName}' has {verticesOverByteLimit} vertices exceeding Unity's " +
                        $"255 skin-weight influence limit (maximum {maximumInfluences}). BlendShare will preserve " +
                        "the strongest 255 influences per vertex and renormalize them.",
                        targetMesh);
                }

                if (verticesOverFour <= 0)
                {
                    return;
                }

                string behavior = preserveAll
                    ? "preserve all representable influences (up to 255)"
                    : "preserve the strongest 4 influences per vertex and remove the remainder";
                Debug.Log(
                    $"[BlendShare] Mesh '{meshName}' has {verticesOverFour} vertices with more than 4 " +
                    $"skin-weight influences (maximum {maximumInfluences}). BlendShare will {behavior} and renormalize them.",
                    targetMesh);
            }

            private static List<Dictionary<int, float>> ReadWeights(
                Mesh sourceMesh,
                BoneTable sourceBoneTable)
            {
                var result = Enumerable.Range(0, sourceMesh != null ? sourceMesh.vertexCount : 0)
                    .Select(_ => new Dictionary<int, float>())
                    .ToList();
                if (sourceMesh == null)
                {
                    return result;
                }

                using var sourceWeights = sourceMesh.GetAllBoneWeights();
                using var sourceCounts = sourceMesh.GetBonesPerVertex();
                int weightIndex = 0;
                for (int vertexIndex = 0; vertexIndex < result.Count; vertexIndex++)
                {
                    int count = vertexIndex < sourceCounts.Length ? sourceCounts[vertexIndex] : 0;
                    for (int i = 0; i < count; i++)
                    {
                        var weight = sourceWeights[weightIndex + i];
                        int targetIndex = sourceBoneTable != null
                            ? sourceBoneTable.RemapOriginalIndex(weight.boneIndex)
                            : weight.boneIndex;
                        AddInfluence(result[vertexIndex], targetIndex, weight.weight);
                    }

                    weightIndex += count;
                }

                return result;
            }
        }

        internal sealed class BoneTable
        {
            private readonly Dictionary<string, int> indexByPath = new();
            private readonly Dictionary<Transform, int> indexByTransform = new();
            private readonly Dictionary<int, int> originalIndexMap = new();

            public List<string> BonePaths { get; } = new();
            public List<Matrix4x4> BindPoses { get; } = new();
            private readonly List<Transform> transforms = new();

            public bool HasPath(string path)
            {
                return indexByPath.ContainsKey(MeshNodePath.Normalize(path));
            }

            public bool TryGetIndex(string path, out int index)
            {
                return indexByPath.TryGetValue(MeshNodePath.Normalize(path), out index);
            }

            public bool TryGetIndex(Transform transform, string path, out int index)
            {
                return transform != null
                    ? indexByTransform.TryGetValue(transform, out index)
                    : TryGetIndex(path, out index);
            }

            public Matrix4x4 GetBindPose(string path)
            {
                return TryGetIndex(path, out int index) && index >= 0 && index < BindPoses.Count
                    ? BindPoses[index]
                    : Matrix4x4.identity;
            }

            public void SetDefinition(Transform transform, string path, Matrix4x4 bindPose)
            {
                if (TryGetIndex(transform, path, out int index) && index >= 0 && index < BindPoses.Count)
                {
                    BindPoses[index] = bindPose;
                    string normalized = MeshNodePath.Normalize(path);
                    BonePaths[index] = normalized;
                    indexByPath[normalized] = index;
                }
            }

            public int Add(
                Transform transform,
                string path,
                Matrix4x4 bindPose,
                int originalIndex = -1)
            {
                string normalized = MeshNodePath.Normalize(path);
                if ((transform != null && indexByTransform.TryGetValue(transform, out int existing)) ||
                    (transform == null && indexByPath.TryGetValue(normalized, out existing)))
                {
                    SetDefinition(transform, normalized, bindPose);
                    if (originalIndex >= 0)
                    {
                        originalIndexMap[originalIndex] = existing;
                    }
                    return existing;
                }

                int created = AddSynthetic(normalized, bindPose, originalIndex);
                transforms[created] = transform;
                if (transform != null)
                {
                    indexByTransform[transform] = created;
                }
                return created;
            }

            public void AddAlias(string sourcePath, Transform targetTransform, string targetPath)
            {
                string normalizedSource = MeshNodePath.Normalize(sourcePath);
                if (TryGetIndex(targetTransform, targetPath, out int targetIndex))
                {
                    indexByPath[normalizedSource] = targetIndex;
                }
            }

            public int AddSynthetic(string key, Matrix4x4 bindPose, int originalIndex = -1)
            {
                int index = BonePaths.Count;
                indexByPath[key] = index;
                BonePaths.Add(key);
                BindPoses.Add(bindPose);
                transforms.Add(null);
                if (originalIndex >= 0)
                {
                    originalIndexMap[originalIndex] = index;
                }
                return index;
            }

            public Transform GetTransform(int index)
            {
                return index >= 0 && index < transforms.Count ? transforms[index] : null;
            }

            public int RemapOriginalIndex(int index)
            {
                return originalIndexMap.TryGetValue(index, out int remapped) ? remapped : index;
            }
        }
    }
}
