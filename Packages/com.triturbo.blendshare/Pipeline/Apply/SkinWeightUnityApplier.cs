using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Primitives;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public sealed class SkinWeightUnityApplier : IFeatureUnityApplier
    {
        public string FeatureTypeId => SkinWeightFeature.FeatureTypeId;
        public int ExecutionOrder => 50;

        public FeatureApplyResult Apply(UnityMeshApplyContext context, IReadOnlyList<FeatureApplyInput> inputs)
        {
            var mesh = context.WorkingMesh;
            var bones = new List<Transform>(context.WorkingBones ?? Array.Empty<Transform>());
            var poses = mesh.bindposes.ToList();
            while (bones.Count < poses.Count) bones.Add(null);
            while (poses.Count < bones.Count) poses.Add(Matrix4x4.identity);
            var records = new List<AppliedBoneBinding>();
            var rows = new BoneWeight1[mesh.vertexCount][];
            var created = new List<Transform>();
            try
            {
                var counts = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                int offset = 0;
                for (int v = 0; v < rows.Length; v++)
                {
                    rows[v] = new BoneWeight1[counts.Length == 0 ? 0 : counts[v]];
                    for (int i = 0; i < rows[v].Length; i++) rows[v][i] = weights[offset++];
                }

                foreach (var input in inputs)
                {
                    var feature = (SkinWeightFeature)input.Feature;
                    var binding = input.VertexBinding;
                    var map = binding.VertexMap;
                    var space = binding.SpaceContract.CanonicalToUnityMeshLocal;
                    if (map.SourceDomain != IndexDomain.UnityVertex || map.TargetDomain != IndexDomain.ControlPoint ||
                        map.SourceCount != mesh.vertexCount || binding.SpaceContract.FrameVersion != MeshSpaceProvenance.CanonicalMeshLocalV1 ||
                        !FbxAffineSpaceTransform.TryCreate(space, out _)) throw new InvalidOperationException("Invalid skin vertex binding.");

                    var buffers = new List<IndexedBuffer<double>>();
                    foreach (var cluster in feature.Clusters)
                    {
                        using var lease = cluster.Payload.Acquire(input.PayloadSource);
                        buffers.Add(lease.Value.DeltaWeights);
                    }
                    var normalized = SkinWeightNormalization.RenormalizePerVertex(buffers, map.TargetCount,
                        ZeroWeightSumPolicy.KeepUnweighted, minWeightThreshold: 0d);
                    var points = new List<(int BoneIndex, double Weight)>[map.TargetCount];
                    for (int c = 0; c < normalized.Length; c++)
                    {
                        var cluster = feature.Clusters[c];
                        var path = FbxNameUtility.NormalizePath(cluster.BonePath);
                        Transform root = context.TargetRootTransform;
                        if (context.ArmatureRootOverrides != null &&
                            context.ArmatureRootOverrides.TryGetValue((input.PatchId ?? string.Empty, feature.ArmatureId ?? string.Empty), out var overrideRoot) &&
                            overrideRoot != null)
                        {
                            root = overrideRoot;
                        }
                        ArmatureBoneResolver.ResolveBoneTransform(root, context.BoneTransformOverrides, input.PatchId,
                            feature.ArmatureId, path, out var bone, out var targetPath, out var isNew);
                        if (!bone)
                            bone = bones.FirstOrDefault(b => b && AnimationUtility.CalculateTransformPath(b, root) == path);
                        if (!bone && context.CreateMissingBones && input.CreateMissingBones)
                        {
                            var armature = input.Armatures?.FirstOrDefault(a => a.ArmatureId == feature.ArmatureId);
                            var firstMissing = path;
                            while (firstMissing.Contains("/"))
                            {
                                var parent = firstMissing.Substring(0, firstMissing.LastIndexOf('/'));
                                if (root && root.Find(parent)) break;
                                firstMissing = parent;
                            }
                            if (!ArmatureBoneResolver.EnsureBoneHierarchy(root, armature, path, space, out bone, out var error))
                                throw new InvalidOperationException(error);
                            created.Add(root.Find(firstMissing));
                        }
                        if (bone) targetPath = AnimationUtility.CalculateTransformPath(bone, root);
                        isNew = bone && created.Any(t => bone == t || bone.IsChildOf(t));
                        int index = bone ? bones.IndexOf(bone) : records.Find(b => b.PatchId == input.PatchId &&
                            b.ArmatureId == feature.ArmatureId && b.SourceBonePath == path)?.BoneIndex ?? -1;
                        if (index < 0)
                        {
                            index = bones.Count;
                            bones.Add(bone);
                            poses.Add(Matrix4x4.identity);
                        }
                        var pose = poses[index];
                        if (cluster.HasClusterMatrices)
                        {
                            if (!ArmatureBindTransformMath.ComputeCanonicalBindPose(cluster.TransformMatrix, cluster.TransformLinkMatrix, out var canonical) ||
                                !ArmatureBindTransformMath.ConvertCanonicalBindPoseToUnity(canonical, space, out pose))
                                throw new InvalidOperationException("Invalid bind matrices for " + path);
                        }
                        else if (bone && context.MeshRendererTransform)
                            pose = bone.worldToLocalMatrix * context.MeshRendererTransform.localToWorldMatrix;

                        poses[index] = pose;
                        records.Add(new AppliedBoneBinding(input.PatchId, feature.ArmatureId, path, targetPath, bone, pose, index, isNew));
                        var buffer = normalized[c];
                        for (int i = 0; i < buffer.Count; i++)
                        {
                            int cp = buffer.Indices[i];
                            (points[cp] ??= new List<(int, double)>()).Add((index, buffer.Values[i]));
                        }
                    }

                    for (int v = 0; v < rows.Length; v++)
                    {
                        var sums = new Dictionary<int, double>();
                        var row = map.GetRow(v);
                        foreach (int cp in row)
                        {
                            if (points[cp] == null) continue;
                            foreach (var influence in points[cp])
                            {
                                sums.TryGetValue(influence.BoneIndex, out var sum);
                                sums[influence.BoneIndex] = sum + influence.Weight / (double)row.Length;
                            }
                        }
                        if (sums.Count == 0) continue;
                        var top = sums.OrderByDescending(p => p.Value).ThenBy(p => p.Key)
                            .Take(Mathf.Clamp(context.MaxBonesPerVertex, 1, 255)).ToArray();
                        double total = top.Sum(p => p.Value);
                        if (total <= 0d) continue;
                        rows[v] = top.Select(p => new BoneWeight1 { boneIndex = p.Key, weight = (float)(p.Value / total) }).ToArray();
                    }
                }
                if (records.Count == 0) return FeatureApplyResult.Skipped();
                using var finalCounts = new NativeArray<byte>(rows.Select(r => checked((byte)r.Length)).ToArray(), Allocator.Temp);
                using var finalWeights = new NativeArray<BoneWeight1>(rows.SelectMany(r => r).ToArray(), Allocator.Temp);
                mesh.SetBoneWeights(finalCounts, finalWeights);
                mesh.bindposes = poses.ToArray();
                context.WorkingBones = bones.ToArray();
                context.AppliedBoneBindings.AddRange(records);
                return FeatureApplyResult.Applied();
            }
            catch (Exception ex)
            {
                foreach (var transform in created) if (transform) UnityEngine.Object.DestroyImmediate(transform.gameObject);
                return FeatureApplyResult.Failed(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error, "SKIN_APPLY_FAILED", ex.Message));
            }
        }
    }
}
