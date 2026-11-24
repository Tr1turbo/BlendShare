using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Primitives;
using Triturbo.BlendShare.Runtime.Assets;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    /// <summary>
    /// Applies one or more <see cref="SkinWeightFeature"/> patches to a Unity Mesh using signed delta
    /// weight accumulation across all inputs before finalization, full 3-stage bindpose resolution
    /// (override -> stored cluster matrices -> recursive armature/hierarchy localToWorld),
    /// deduplicated bone table preservation, rootBone resolution, and unlimited (&gt;4) skin-weight
    /// vertex layout rebuild support.
    /// </summary>
    public sealed class SkinWeightUnityApplier : IFeatureUnityApplier
    {
        private static readonly Comparison<(int Index, double Weight)> s_InfluenceComparison = CompareInfluences;

        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;

        public FeatureApplyResult Apply(UnityMeshApplyContext context, IReadOnlyList<FeatureApplyInput> inputs)
        {
            var mesh = context.WorkingMesh;
            var boneTable = SeedBoneTable(context.WorkingBones, mesh.bindposes, context.TargetRootTransform);
            var weightsByVertex = ReadInitialWeights(mesh, boneTable);
            var records = new List<AppliedBoneBinding>();
            var armatureBonesByPath = new Dictionary<string, (string PatchId, GeneratedArmatureBoneRecord Record)>(StringComparer.Ordinal);
            var createdRoots = new List<Transform>();

            Transform outputRootBone = context.IncomingRootBone;
            string outputRootBonePath = outputRootBone && context.TargetRootTransform
                ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(outputRootBone, context.TargetRootTransform))
                : string.Empty;

            bool anyWeightOrBindposeModified = false;
            bool anyArmatureModified = false;

            try
            {
                foreach (var input in inputs)
                {
                    var feature = (SkinWeightFeature)input.Feature;
                    var binding = input.VertexBinding;
                    var map = binding.VertexMap;
                    var space = binding.SpaceContract.StoredToUnityMeshLocal;
                    if (map.SourceDomain != IndexDomain.UnityVertex || map.TargetDomain != IndexDomain.ControlPoint ||
                        map.SourceCount != mesh.vertexCount ||
                        binding.SpaceContract.StoredSpace == null || !binding.SpaceContract.StoredSpace.IsValid ||
                        (input.MeshHeader?.SpaceProvenance != null && !binding.SpaceContract.StoredSpace.MatchesSpace(input.MeshHeader.SpaceProvenance)) ||
                        !FbxAffineSpaceTransform.TryCreate(space, out _))
                    {
                        throw new InvalidOperationException("Invalid skin vertex binding.");
                    }

                    var armature = input.Armatures?.FirstOrDefault(a => a != null && a.ArmatureId == feature.ArmatureId);
                    if (armature != null)
                    {
                        var expectedSpace = input.MeshHeader?.SpaceProvenance ?? binding.SpaceContract.StoredSpace;
                        var armatureSpace = armature.SpaceProvenance;
                        if (armatureSpace == null || !armatureSpace.IsValid ||
                            expectedSpace == null || !expectedSpace.IsValid ||
                            !armatureSpace.MatchesSpace(expectedSpace))
                        {
                            throw new InvalidOperationException($"Armature '{feature.ArmatureId}' and mesh use incompatible stored spaces.");
                        }

                        CollectArtifactArmatureBones(
                            context,
                            input,
                            feature,
                            armature,
                            space,
                            armatureBonesByPath);
                        if (armature.Bones != null && armature.Bones.Count > 0)
                        {
                            anyArmatureModified = true;
                        }
                    }

                    var neededPaths = feature.GetNeededBonePathsInArmatureOrder(armature);
                    var slotBySourcePath = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (int n = 0; n < neededPaths.Count; n++)
                    {
                        string sourcePath = neededPaths[n];
                        var overrideKey = new BoneOverrideKey(input.PatchId, feature.ArmatureId, sourcePath);

                        Transform resolvedBone = null;
                        bool isProxyOverride = false;
                        if (context.BoneTransformOverrides != null &&
                            context.BoneTransformOverrides.TryGetValue(overrideKey, out var overrideTransform) &&
                            overrideTransform != null)
                        {
                            resolvedBone = overrideTransform;
                            isProxyOverride = true;
                        }

                        if (!resolvedBone && context.TargetRootTransform)
                        {
                            resolvedBone = context.TargetRootTransform.Find(sourcePath);
                        }

                        if (!resolvedBone && boneTable.TryGetTransform(sourcePath, out var tableTransform))
                        {
                            resolvedBone = tableTransform;
                        }

                        if (!resolvedBone && context.CreateMissingBones && input.CreateMissingBones && context.TargetRootTransform)
                        {
                            string firstMissing = FindTopmostMissingPath(context.TargetRootTransform, sourcePath);
                            if (!ArmatureBoneResolver.EnsureBoneHierarchy(
                                    context.TargetRootTransform,
                                    armature,
                                    sourcePath,
                                    space,
                                    out resolvedBone,
                                    out var createError))
                            {
                                throw new InvalidOperationException(createError);
                            }

                            var topmostCreated = context.TargetRootTransform.Find(firstMissing);
                            if (topmostCreated && !createdRoots.Contains(topmostCreated))
                            {
                                createdRoots.Add(topmostCreated);
                            }
                        }

                        string targetPath = resolvedBone && context.TargetRootTransform
                            ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(resolvedBone, context.TargetRootTransform))
                            : sourcePath;
                        bool isNew = resolvedBone && createdRoots.Any(t => t && (resolvedBone == t || resolvedBone.IsChildOf(t)));

                        bool hasBindPoseOverride = TryGetBindPoseOverride(input, sourcePath, out var authoredBindPose);
                        bool hasClusterMatrices = feature.TryGetBindPoseFbxClusterMatrices(sourcePath, out _, out _);
                        bool definesBindPose = hasBindPoseOverride || hasClusterMatrices;

                        int slotIndex;
                        Matrix4x4 resolvedBindPose;

                        if (boneTable.TryGetIndex(resolvedBone, targetPath, out slotIndex))
                        {
                            resolvedBindPose = boneTable.BindPoses[slotIndex];
                            if (isProxyOverride || definesBindPose)
                            {
                                if (!TryResolveBindPose(
                                        context,
                                        input,
                                        feature,
                                        armature,
                                        sourcePath,
                                        resolvedBone,
                                        hasBindPoseOverride,
                                        authoredBindPose,
                                        space,
                                        out resolvedBindPose,
                                        out var bindError))
                                {
                                    throw new InvalidOperationException(bindError);
                                }

                                boneTable.SetDefinition(slotIndex, resolvedBone, targetPath, resolvedBindPose);
                            }

                            boneTable.AddAlias(sourcePath, slotIndex);
                        }
                        else
                        {
                            if (!TryResolveBindPose(
                                    context,
                                    input,
                                    feature,
                                    armature,
                                    sourcePath,
                                    resolvedBone,
                                    hasBindPoseOverride,
                                    authoredBindPose,
                                    space,
                                    out resolvedBindPose,
                                    out var bindError))
                            {
                                throw new InvalidOperationException(bindError);
                            }

                            slotIndex = boneTable.Add(resolvedBone, targetPath, resolvedBindPose);
                            boneTable.AddAlias(sourcePath, slotIndex);
                        }

                        slotBySourcePath[sourcePath] = slotIndex;
                        records.Add(new AppliedBoneBinding(
                            input.PatchId,
                            feature.ArmatureId,
                            sourcePath,
                            targetPath,
                            resolvedBone,
                            resolvedBindPose,
                            slotIndex,
                            isNew));
                        anyWeightOrBindposeModified = true;
                    }

                    // Resolve optional feature.RootBonePath override.
                    string featureRootBonePath = FbxNameUtility.NormalizePath(feature.RootBonePath ?? string.Empty);
                    if (!string.IsNullOrEmpty(featureRootBonePath))
                    {
                        var rootKey = new BoneOverrideKey(input.PatchId, feature.ArmatureId, featureRootBonePath);
                        Transform resolvedRoot = null;
                        if (context.BoneTransformOverrides != null &&
                            context.BoneTransformOverrides.TryGetValue(rootKey, out var overrideRoot) &&
                            overrideRoot != null)
                        {
                            resolvedRoot = overrideRoot;
                        }
                        else if (boneTable.TryGetTransform(featureRootBonePath, out var tableRoot) && tableRoot != null)
                        {
                            resolvedRoot = tableRoot;
                        }
                        else if (context.TargetRootTransform)
                        {
                            resolvedRoot = context.TargetRootTransform.Find(featureRootBonePath);
                        }

                        if (resolvedRoot != null)
                        {
                            outputRootBone = resolvedRoot;
                            outputRootBonePath = context.TargetRootTransform
                                ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(resolvedRoot, context.TargetRootTransform))
                                : featureRootBonePath;
                        }
                        else
                        {
                            outputRootBonePath = featureRootBonePath;
                        }
                    }

                    // Build control-point delta index and accumulate signed deltas across all Unity vertices.
                    var deltaIndex = BuildDeltaIndex(feature, input.PayloadSource, slotBySourcePath, map.TargetCount);
                    if (deltaIndex.Count > 0)
                    {
                        for (int v = 0; v < weightsByVertex.Length; v++)
                        {
                            var row = map.GetRow(v);
                            var deltas = CalculateIndexedDeltas(row, deltaIndex);
                            if (deltas == null || deltas.Count == 0)
                            {
                                continue;
                            }

                            var aggregate = weightsByVertex[v];
                            foreach (var pair in deltas)
                            {
                                aggregate.TryGetValue(pair.Key, out double existing);
                                aggregate[pair.Key] = existing + pair.Value;
                            }
                        }
                    }
                }

                if (!anyWeightOrBindposeModified && !anyArmatureModified)
                {
                    return FeatureApplyResult.Skipped();
                }

                // Finalize accumulated weights ONCE after all patches have contributed signed deltas.
                if (anyWeightOrBindposeModified)
                {
                    int maxBonesPerVertex = Mathf.Clamp(context.MaxBonesPerVertex, 1, byte.MaxValue);
                    bool limitToFour = maxBonesPerVertex <= 4;
                    var selectedWeights = new List<(int Index, double Weight)>(maxBonesPerVertex);
                    using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
                    using var outputCounts = new NativeList<byte>(Allocator.Temp);

                    int verticesOverFour = 0;
                    int verticesOverByteLimit = 0;
                    int maxInfluences = 0;

                    for (int v = 0; v < weightsByVertex.Length; v++)
                    {
                        int positiveCount = KeepTopWeights(maxBonesPerVertex, weightsByVertex[v], boneTable.BonePaths.Count, selectedWeights);
                        if (positiveCount > maxInfluences) maxInfluences = positiveCount;
                        if (positiveCount > 4) verticesOverFour++;
                        if (positiveCount > byte.MaxValue) verticesOverByteLimit++;

                        for (int i = 0; i < selectedWeights.Count; i++)
                        {
                            var entry = selectedWeights[i];
                            outputWeights.Add(new BoneWeight1 { boneIndex = entry.Index, weight = (float)entry.Weight });
                        }

                        outputCounts.Add((byte)selectedWeights.Count);
                    }

                    if (!limitToFour &&
                        maxInfluences > 4 &&
                        UnitySkinWeightMeshLayoutUtility.RequiresUnlimitedLayoutRebuild(mesh))
                    {
                        UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
                    }

                    mesh.bindposes = boneTable.BindPoses.ToArray();
                    using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
                    using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
                    mesh.SetBoneWeights(outputCountArray, outputWeightArray);
                    context.WorkingBones = boneTable.Transforms.ToArray();
                    context.AppliedBoneBindings.AddRange(records);
                    context.SkinWeightInfluenceSummary = new BlendShareSkinWeightInfluenceSummary(
                        verticesOverFour,
                        verticesOverByteLimit,
                        maxInfluences);
                }

                context.OutputRootBone = outputRootBone;
                context.OutputRootBonePath = outputRootBonePath;
                foreach (var entry in armatureBonesByPath.Values
                             .OrderBy(item => CountPathDepth(item.Record.Path))
                             .ThenBy(item => item.Record.Path, StringComparer.Ordinal))
                {
                    context.AppliedArmatureBones.Add(entry.Record);
                }

                return FeatureApplyResult.Applied();
            }
            catch (Exception ex)
            {
                foreach (var transform in createdRoots)
                {
                    if (transform) UnityEngine.Object.DestroyImmediate(transform.gameObject);
                }

                return FeatureApplyResult.Failed(new ApplyDiagnostic(
                    ApplyDiagnosticSeverity.Error,
                    "SKIN_APPLY_FAILED",
                    ex.Message));
            }
        }

        private static BoneTable SeedBoneTable(Transform[] existingBones, Matrix4x4[] existingBindPoses, Transform targetRoot)
        {
            var table = new BoneTable();
            var bones = existingBones ?? Array.Empty<Transform>();
            var poses = existingBindPoses ?? Array.Empty<Matrix4x4>();
            int count = Math.Max(bones.Length, poses.Length);

            for (int i = 0; i < count; i++)
            {
                var bone = i < bones.Length ? bones[i] : null;
                var pose = i < poses.Length ? poses[i] : Matrix4x4.identity;
                if (bone == null)
                {
                    table.AddSynthetic($".__missing_bone_{i}", pose, i);
                    continue;
                }

                string path = targetRoot
                    ? FbxNameUtility.NormalizePath(AnimationUtility.CalculateTransformPath(bone, targetRoot))
                    : bone.name;
                table.Add(bone, path, pose, i);
            }

            return table;
        }

        private static Dictionary<int, double>[] ReadInitialWeights(Mesh mesh, BoneTable boneTable)
        {
            var result = new Dictionary<int, double>[mesh.vertexCount];
            for (int v = 0; v < result.Length; v++)
            {
                result[v] = new Dictionary<int, double>();
            }

            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int offset = 0;
            for (int v = 0; v < result.Length; v++)
            {
                int count = v < counts.Length ? counts[v] : 0;
                var map = result[v];
                for (int i = 0; i < count; i++)
                {
                    var bw = weights[offset++];
                    if (bw.boneIndex < 0 || bw.weight <= 0f) continue;
                    int slot = boneTable.RemapOriginalIndex(bw.boneIndex);
                    map.TryGetValue(slot, out double existing);
                    map[slot] = existing + bw.weight;
                }
            }

            return result;
        }

        private static void CollectArtifactArmatureBones(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            SkinWeightFeature feature,
            ArmatureDocument armature,
            FbxMatrix4x4 storedToUnityMeshLocal,
            Dictionary<string, (string PatchId, GeneratedArmatureBoneRecord Record)> target)
        {
            if (armature?.Bones == null || armature.Bones.Count == 0)
            {
                return;
            }

            for (int i = 0; i < armature.Bones.Count; i++)
            {
                var bone = armature.Bones[i];
                if (bone == null || string.IsNullOrWhiteSpace(bone.Path))
                {
                    continue;
                }

                string sourcePath = FbxNameUtility.NormalizePath(bone.Path);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    continue;
                }

                var overrideKey = new BoneOverrideKey(input.PatchId, feature.ArmatureId, sourcePath);
                if (context.BoneTransformOverrides != null &&
                    context.BoneTransformOverrides.TryGetValue(overrideKey, out var overrideTransform) &&
                    overrideTransform != null)
                {
                    // Proxy-overridden bones are managed directly by BlendShareBoneProxy & merge resolver.
                    continue;
                }

                if (!bone.HasTransformData)
                {
                    throw new InvalidOperationException($"Armature bone '{sourcePath}' is missing local bind transform data.");
                }

                string decomposeError = null;
                if (!ArmatureBindTransformMath.ConvertCanonicalMatrixToUnity(
                        bone.LocalBindMatrix,
                        storedToUnityMeshLocal,
                        out var localUnity) ||
                    !ArmatureBindTransformMath.TryDecomposeUnityLocalTransform(
                        localUnity,
                        out var position,
                        out var rotation,
                        out var scale,
                        out decomposeError))
                {
                    throw new InvalidOperationException($"Cannot convert armature bone '{sourcePath}': {decomposeError ?? "invalid local bind matrix"}");
                }

                var record = new GeneratedArmatureBoneRecord(sourcePath, position, rotation, scale, bone.CreateIfMissing);
                if (target.TryGetValue(sourcePath, out var existing))
                {
                    if (!existing.Record.ApproximatelyMatches(record))
                    {
                        throw new InvalidOperationException(
                            $"Conflicting armature bone transforms for '{sourcePath}' between patch '{existing.PatchId}' and patch '{input.PatchId}'.");
                    }

                    if (record.CreateIfMissing && !existing.Record.CreateIfMissing)
                    {
                        target[sourcePath] = (input.PatchId, record);
                    }
                }
                else
                {
                    target[sourcePath] = (input.PatchId, record);
                }
            }
        }

        private static bool TryGetBindPoseOverride(FeatureApplyInput input, string sourcePath, out Matrix4x4 bindPose)
        {
            bindPose = Matrix4x4.identity;
            return input.BindPoseOverrides != null &&
                   input.BindPoseOverrides.TryGetValue(sourcePath, out bindPose);
        }

        private static bool TryResolveBindPose(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            SkinWeightFeature feature,
            ArmatureDocument armature,
            string sourcePath,
            Transform resolvedBone,
            bool hasBindPoseOverride,
            Matrix4x4 authoredBindPose,
            FbxMatrix4x4 storedToUnityMeshLocal,
            out Matrix4x4 bindPose,
            out string error)
        {
            error = null;
            if (hasBindPoseOverride)
            {
                bindPose = authoredBindPose;
                return true;
            }

            if (feature.TryGetBindPoseFbxClusterMatrices(sourcePath, out var transformMatrix, out var transformLinkMatrix))
            {
                if (!ArmatureBindTransformMath.ComputeCanonicalBindPose(transformMatrix, transformLinkMatrix, out var canonical) ||
                    !ArmatureBindTransformMath.ConvertCanonicalBindPoseToUnity(canonical, storedToUnityMeshLocal, out bindPose))
                {
                    bindPose = Matrix4x4.identity;
                    error = $"Invalid bind matrices for bone '{sourcePath}'.";
                    return false;
                }

                return true;
            }

            if (TryResolveBoneLocalToWorld(
                    context,
                    input,
                    feature,
                    armature,
                    sourcePath,
                    resolvedBone,
                    storedToUnityMeshLocal,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var boneLocalToWorld,
                    out error))
            {
                var rendererLocalToWorld = context.MeshRendererTransform
                    ? context.MeshRendererTransform.localToWorldMatrix
                    : Matrix4x4.identity;
                bindPose = boneLocalToWorld.inverse * rendererLocalToWorld;
                return true;
            }

            bindPose = Matrix4x4.identity;
            return false;
        }

        private static bool TryResolveBoneLocalToWorld(
            UnityMeshApplyContext context,
            FeatureApplyInput input,
            SkinWeightFeature feature,
            ArmatureDocument armature,
            string path,
            Transform directTransform,
            FbxMatrix4x4 storedToUnityMeshLocal,
            HashSet<string> resolving,
            out Matrix4x4 localToWorld,
            out string error)
        {
            error = null;
            path = FbxNameUtility.NormalizePath(path ?? string.Empty);
            if (string.IsNullOrEmpty(path))
            {
                localToWorld = context.TargetRootTransform ? context.TargetRootTransform.localToWorldMatrix : Matrix4x4.identity;
                return true;
            }

            var overrideKey = new BoneOverrideKey(input.PatchId, feature.ArmatureId, path);
            if (context.BoneTransformOverrides != null &&
                context.BoneTransformOverrides.TryGetValue(overrideKey, out var overrideTransform) &&
                overrideTransform != null)
            {
                localToWorld = overrideTransform.localToWorldMatrix;
                return true;
            }

            var armatureBone = armature?.GetBone(path);
            if (armatureBone == null)
            {
                var existing = directTransform != null
                    ? directTransform
                    : (context.TargetRootTransform ? context.TargetRootTransform.Find(path) : null);
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

            string parentPath = !string.IsNullOrEmpty(armatureBone.ParentPath)
                ? FbxNameUtility.NormalizePath(armatureBone.ParentPath)
                : GetParentPath(path);
            if (!TryResolveBoneLocalToWorld(
                    context,
                    input,
                    feature,
                    armature,
                    parentPath,
                    directTransform: null,
                    storedToUnityMeshLocal,
                    resolving,
                    out var parentLocalToWorld,
                    out error))
            {
                resolving.Remove(path);
                localToWorld = Matrix4x4.identity;
                return false;
            }

            resolving.Remove(path);
            if (!armatureBone.HasTransformData ||
                !ArmatureBindTransformMath.ConvertCanonicalMatrixToUnity(armatureBone.LocalBindMatrix, storedToUnityMeshLocal, out var localUnity) ||
                !ArmatureBindTransformMath.TryDecomposeUnityLocalTransform(localUnity, out var pos, out var rot, out var scale, out error))
            {
                localToWorld = Matrix4x4.identity;
                error = $"Cannot convert armature bone '{path}': {error ?? "missing or invalid transform data"}";
                return false;
            }

            localToWorld = parentLocalToWorld * Matrix4x4.TRS(pos, rot, scale);
            return true;
        }

        private static Dictionary<int, List<(int BoneIndex, double Delta)>> BuildDeltaIndex(
            SkinWeightFeature feature,
            Model.Serialization.ILazyPayloadSource payloadSource,
            Dictionary<string, int> slotBySourcePath,
            int controlPointCount)
        {
            var result = new Dictionary<int, List<(int BoneIndex, double Delta)>>();
            if (feature?.Clusters == null)
            {
                return result;
            }

            for (int c = 0; c < feature.Clusters.Count; c++)
            {
                var cluster = feature.Clusters[c];
                if (cluster == null || cluster.Payload == null || !cluster.Payload.HasPayload)
                {
                    continue;
                }

                string path = FbxNameUtility.NormalizePath(cluster.BonePath);
                int boneIndex = slotBySourcePath.TryGetValue(path, out int slot) ? slot : -1;

                using var lease = cluster.Payload.Acquire(payloadSource);
                var buffer = lease.Value?.DeltaWeights;
                if (buffer == null || buffer.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < buffer.Count; i++)
                {
                    int cp = buffer.Indices[i];
                    double delta = buffer.Values[i];
                    if ((uint)cp >= (uint)controlPointCount)
                    {
                        continue;
                    }

                    if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < -1d || delta > 1d)
                    {
                        throw new InvalidOperationException(
                            $"Skin weight delta out of [-1, 1] for bone '{path}' at control point {cp}: {delta}.");
                    }

                    if (delta == 0d)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(cp, out var list))
                    {
                        list = new List<(int, double)>();
                        result[cp] = list;
                    }

                    list.Add((boneIndex, delta));
                }
            }

            return result;
        }

        private static Dictionary<int, double> CalculateIndexedDeltas(
            ReadOnlySpan<int> controlPoints,
            Dictionary<int, List<(int BoneIndex, double Delta)>> deltaIndex)
        {
            int contributingControlPoints = 0;
            Dictionary<int, double> deltaBySlot = null;

            for (int i = 0; i < controlPoints.Length; i++)
            {
                int cp = controlPoints[i];
                if (cp < 0 || !deltaIndex.TryGetValue(cp, out var deltas) || deltas == null || deltas.Count == 0)
                {
                    continue;
                }

                contributingControlPoints++;
                deltaBySlot ??= new Dictionary<int, double>();
                for (int d = 0; d < deltas.Count; d++)
                {
                    var item = deltas[d];
                    if (item.BoneIndex < 0) continue;
                    deltaBySlot.TryGetValue(item.BoneIndex, out double existing);
                    deltaBySlot[item.BoneIndex] = existing + item.Delta;
                }
            }

            if (deltaBySlot != null && contributingControlPoints > 1)
            {
                var keys = deltaBySlot.Keys.ToArray();
                for (int i = 0; i < keys.Length; i++)
                {
                    deltaBySlot[keys[i]] /= contributingControlPoints;
                }
            }

            return deltaBySlot;
        }

        private static int KeepTopWeights(
            int maxInfluences,
            Dictionary<int, double> aggregate,
            int boneCount,
            List<(int Index, double Weight)> output)
        {
            output.Clear();
            if (aggregate != null)
            {
                foreach (var pair in aggregate)
                {
                    if (pair.Key >= 0 && pair.Key < boneCount &&
                        pair.Value > 0d && !double.IsNaN(pair.Value) && !double.IsInfinity(pair.Value))
                    {
                        output.Add((pair.Key, pair.Value));
                    }
                }
            }

            output.Sort(s_InfluenceComparison);
            int positiveCount = output.Count;
            int limit = Mathf.Clamp(maxInfluences, 1, byte.MaxValue);
            if (output.Count > limit)
            {
                output.RemoveRange(limit, output.Count - limit);
            }

            if (output.Count == 0)
            {
                if (boneCount > 0)
                {
                    output.Add((0, 1d));
                }

                return positiveCount;
            }

            double total = 0d;
            for (int i = 0; i < output.Count; i++)
            {
                total += output[i].Weight;
            }

            if (!(total > 0d) || double.IsInfinity(total))
            {
                int strongest = output[0].Index;
                output.Clear();
                output.Add((strongest, 1d));
                return positiveCount;
            }

            for (int i = 0; i < output.Count; i++)
            {
                output[i] = (output[i].Index, output[i].Weight / total);
            }

            return positiveCount;
        }

        private static int CompareInfluences((int Index, double Weight) left, (int Index, double Weight) right)
        {
            int weightCmp = right.Weight.CompareTo(left.Weight);
            return weightCmp != 0 ? weightCmp : left.Index.CompareTo(right.Index);
        }

        private static string FindTopmostMissingPath(Transform root, string path)
        {
            string current = path;
            while (current.Contains("/"))
            {
                string parent = current.Substring(0, current.LastIndexOf('/'));
                if (root.Find(parent) != null)
                {
                    break;
                }

                current = parent;
            }

            return current;
        }

        private static string GetParentPath(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath)) return string.Empty;
            int slash = normalizedPath.LastIndexOf('/');
            return slash > 0 ? normalizedPath.Substring(0, slash) : string.Empty;
        }

        private static int CountPathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/') depth++;
            }

            return depth;
        }

        private sealed class BoneTable
        {
            private readonly Dictionary<string, int> m_IndexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<Transform, int> m_IndexByTransform = new Dictionary<Transform, int>();
            private readonly Dictionary<int, int> m_OriginalIndexMap = new Dictionary<int, int>();

            public List<string> BonePaths { get; } = new List<string>();
            public List<Matrix4x4> BindPoses { get; } = new List<Matrix4x4>();
            public List<Transform> Transforms { get; } = new List<Transform>();

            public bool TryGetIndex(Transform transform, string path, out int index)
            {
                if (transform != null && m_IndexByTransform.TryGetValue(transform, out index))
                {
                    return true;
                }

                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                return m_IndexByPath.TryGetValue(normalized, out index);
            }

            public bool TryGetTransform(string path, out Transform transform)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if (m_IndexByPath.TryGetValue(normalized, out int index) && index >= 0 && index < Transforms.Count)
                {
                    transform = Transforms[index];
                    return transform != null;
                }

                transform = null;
                return false;
            }

            public void SetDefinition(int index, Transform transform, string path, Matrix4x4 bindPose)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                BindPoses[index] = bindPose;
                BonePaths[index] = normalized;
                if (transform != null)
                {
                    Transforms[index] = transform;
                    m_IndexByTransform[transform] = index;
                }

                if (!string.IsNullOrEmpty(normalized))
                {
                    m_IndexByPath[normalized] = index;
                }
            }

            public int Add(Transform transform, string path, Matrix4x4 bindPose, int originalIndex = -1)
            {
                string normalized = FbxNameUtility.NormalizePath(path ?? string.Empty);
                if ((transform != null && m_IndexByTransform.TryGetValue(transform, out int existing)) ||
                    (transform == null && !string.IsNullOrEmpty(normalized) && m_IndexByPath.TryGetValue(normalized, out existing)))
                {
                    SetDefinition(existing, transform, normalized, bindPose);
                    if (originalIndex >= 0) m_OriginalIndexMap[originalIndex] = existing;
                    return existing;
                }

                int created = AddSynthetic(normalized, bindPose, originalIndex);
                Transforms[created] = transform;
                if (transform != null) m_IndexByTransform[transform] = created;
                return created;
            }

            public int AddSynthetic(string key, Matrix4x4 bindPose, int originalIndex = -1)
            {
                int index = BonePaths.Count;
                if (!string.IsNullOrEmpty(key)) m_IndexByPath[key] = index;
                BonePaths.Add(key ?? string.Empty);
                BindPoses.Add(bindPose);
                Transforms.Add(null);
                if (originalIndex >= 0) m_OriginalIndexMap[originalIndex] = index;
                return index;
            }

            public void AddAlias(string sourcePath, int targetIndex)
            {
                string normalized = FbxNameUtility.NormalizePath(sourcePath ?? string.Empty);
                if (!string.IsNullOrEmpty(normalized))
                {
                    m_IndexByPath[normalized] = targetIndex;
                }
            }

            public int RemapOriginalIndex(int index)
            {
                return m_OriginalIndexMap.TryGetValue(index, out int remapped) ? remapped : index;
            }
        }
    }
}
