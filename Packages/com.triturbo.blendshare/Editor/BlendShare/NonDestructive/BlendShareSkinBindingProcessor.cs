using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.SkinWeights;
using Unity.Collections;
using UnityEngine;

namespace Triturbo.BlendShare.NonDestructive
{
    public static class BlendShareSkinBindingProcessor
    {
        private const float WeightEpsilon = 0.00001f;

        public sealed class Request
        {
            public SkinnedMeshRenderer Renderer;
            public Transform TargetRoot;
            public IReadOnlyList<BlendShareMeshApplierComponent> MeshAppliers = Array.Empty<BlendShareMeshApplierComponent>();
            public IReadOnlyList<BlendShareBoneProxyComponent> BoneProxies = Array.Empty<BlendShareBoneProxyComponent>();
            public Func<string, BoneNodeData, Transform> CreateBone;
        }

        public sealed class Result
        {
            public Mesh Mesh;
            public Transform RootBone;
            public Transform[] Bones = Array.Empty<Transform>();
            public string[] Diagnostics = Array.Empty<string>();
            public bool Success => Diagnostics == null || Diagnostics.Length == 0;
        }

        public static Result Process(Request request)
        {
            var diagnostics = new List<string>();
            if (request?.Renderer == null || request.Renderer.sharedMesh == null)
            {
                return Failed("BlendShare processing requires a SkinnedMeshRenderer with a shared mesh.");
            }

            var targetRoot = request.TargetRoot != null ? request.TargetRoot : request.Renderer.transform.root;
            var mesh = UnityEngine.Object.Instantiate(request.Renderer.sharedMesh);
            mesh.name = $"{request.Renderer.sharedMesh.name}_BlendShare";

            var bones = new List<Transform>(request.Renderer.bones ?? Array.Empty<Transform>());
            var bindposes = new List<Matrix4x4>(mesh.bindposes ?? Array.Empty<Matrix4x4>());
            while (bindposes.Count < bones.Count)
            {
                bindposes.Add(Matrix4x4.identity);
            }

            var boneIndexByPath = BuildExistingBoneIndex(bones, targetRoot);
            var proxiesByPath = (request.BoneProxies ?? Array.Empty<BlendShareBoneProxyComponent>())
                .Where(proxy => proxy != null && proxy.Owner != null)
                .GroupBy(proxy => MeshNodePath.Normalize(proxy.BonePath))
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var applier in request.MeshAppliers ?? Array.Empty<BlendShareMeshApplierComponent>())
            {
                if (applier == null || !applier.EnabledForBuild || applier.MeshData == null)
                {
                    continue;
                }

                var meshData = applier.MeshData;
                if (!meshData.IsValidTarget(mesh))
                {
                    diagnostics.Add($"Mesh '{mesh.name}' does not match BlendShare mesh data '{meshData.m_Path}'.");
                    continue;
                }

                ApplyBlendShapes(meshData, mesh, diagnostics);
                ApplySkinWeights(
                    meshData,
                    mesh,
                    targetRoot,
                    request,
                    bones,
                    bindposes,
                    boneIndexByPath,
                    proxiesByPath,
                    diagnostics);
            }

            if (diagnostics.Count > 0)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                return new Result { Diagnostics = diagnostics.ToArray() };
            }

            mesh.bindposes = bindposes.Take(bones.Count).ToArray();
            mesh.RecalculateBounds();
            return new Result
            {
                Mesh = mesh,
                RootBone = request.Renderer.rootBone != null ? request.Renderer.rootBone : targetRoot,
                Bones = bones.ToArray(),
                Diagnostics = Array.Empty<string>()
            };
        }

        private static Result Failed(string diagnostic)
        {
            return new Result { Diagnostics = new[] { diagnostic } };
        }

        private static Dictionary<string, int> BuildExistingBoneIndex(IReadOnlyList<Transform> bones, Transform targetRoot)
        {
            var result = new Dictionary<string, int>();
            for (int i = 0; i < (bones?.Count ?? 0); i++)
            {
                var bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot));
                if (!result.ContainsKey(path))
                {
                    result.Add(path, i);
                }
            }

            return result;
        }

        private static void ApplyBlendShapes(MeshDataObject meshData, Mesh mesh, List<string> diagnostics)
        {
            var feature = meshData.GetFeature<BlendShapeFeatureObject>();
            if (feature == null)
            {
                return;
            }

            var mapping = (meshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.IsValidFor(mesh));
            if (mapping == null)
            {
                diagnostics.Add($"No Unity vertex mapping matches mesh '{mesh.name}'.");
                return;
            }

            var active = feature.GetActiveBlendShapes().Where(shape => shape != null).ToArray();
            var activeNames = new HashSet<string>(active.Select(shape => shape.m_Name));
            var existing = new List<(string name, float weight, Vector3[] vertices, Vector3[] normals, Vector3[] tangents)>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (!activeNames.Contains(name))
                {
                    for (int frameIndex = 0; frameIndex < mesh.GetBlendShapeFrameCount(i); frameIndex++)
                    {
                        var vertices = new Vector3[mesh.vertexCount];
                        var normals = new Vector3[mesh.vertexCount];
                        var tangents = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(i, frameIndex, vertices, normals, tangents);
                        existing.Add((name, mesh.GetBlendShapeFrameWeight(i, frameIndex), vertices, normals, tangents));
                    }
                }
            }

            mesh.ClearBlendShapes();
            foreach (var shape in existing)
            {
                mesh.AddBlendShapeFrame(shape.name, shape.weight, shape.vertices, shape.normals, shape.tangents);
            }

            foreach (var shape in active)
            {
                var frames = shape.m_FbxBlendShapeData?.m_Frames;
                if (frames == null || frames.Length == 0)
                {
                    continue;
                }

                for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
                {
                    var deltaVertices = new Vector3[mesh.vertexCount];
                    var deltaNormals = new Vector3[mesh.vertexCount];
                    var deltaTangents = new Vector3[mesh.vertexCount];
                    for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
                    {
                        if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                        {
                            diagnostics.Add($"Cannot map Unity vertex {unityIndex} for blendshape '{shape.m_Name}'.");
                            return;
                        }

                        deltaVertices[unityIndex] = GetDelta(frames[frameIndex], group) * mapping.FbxToUnityScale;
                    }

                    float weight = 100f * (frameIndex + 1) / frames.Length;
                    mesh.AddBlendShapeFrame(shape.m_Name, weight, deltaVertices, deltaNormals, deltaTangents);
                }
            }
        }

        private static Vector3 GetDelta(FbxBlendShapeFrame frame, FbxIndexGroup group)
        {
            foreach (int index in group.m_Indices ?? Array.Empty<int>())
            {
                var delta = frame.GetDeltaControlPointAt(index);
                if (!delta.IsZero())
                {
                    return new Vector3((float)delta.x, (float)delta.y, (float)delta.z);
                }
            }

            return Vector3.zero;
        }

        private static void ApplySkinWeights(
            MeshDataObject meshData,
            Mesh mesh,
            Transform targetRoot,
            Request request,
            List<Transform> bones,
            List<Matrix4x4> bindposes,
            Dictionary<string, int> boneIndexByPath,
            IReadOnlyDictionary<string, BlendShareBoneProxyComponent> proxiesByPath,
            List<string> diagnostics)
        {
            var feature = meshData.GetFeature<SkinWeightFeatureObject>();
            if (feature == null || feature.WeightedControlPointCount == 0)
            {
                return;
            }

            var mapping = (meshData.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(candidate => candidate != null && candidate.IsValidFor(mesh));
            if (mapping == null)
            {
                diagnostics.Add($"No Unity vertex mapping matches mesh '{mesh.name}'.");
                return;
            }

            foreach (string path in GetNeededBonePathsInGraphOrder(feature))
            {
                EnsureBone(path, feature, targetRoot, request, bones, bindposes, boneIndexByPath, proxiesByPath, diagnostics);
            }

            if (diagnostics.Count > 0)
            {
                return;
            }

            var deltasByControlPoint = feature.ControlPointWeights
                .Where(weights => weights != null)
                .ToDictionary(weights => weights.m_ControlPointIndex, weights => weights);
            var existingWeights = mesh.GetAllBoneWeights();
            var existingCounts = mesh.GetBonesPerVertex();
            using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
            using var outputCounts = new NativeList<byte>(Allocator.Temp);
            int existingIndex = 0;
            for (int unityIndex = 0; unityIndex < mesh.vertexCount; unityIndex++)
            {
                var aggregate = new Dictionary<int, float>();
                int existingCount = unityIndex < existingCounts.Length ? existingCounts[unityIndex] : 0;
                for (int i = 0; i < existingCount; i++)
                {
                    var weight = existingWeights[existingIndex + i];
                    if (weight.weight > WeightEpsilon)
                    {
                        aggregate[weight.boneIndex] = weight.weight;
                    }
                }

                existingIndex += existingCount;
                if (!mapping.TryGetFbxGroup(unityIndex, out var group))
                {
                    diagnostics.Add($"Cannot map Unity vertex {unityIndex} for skin weights.");
                    break;
                }

                ApplyWeightDeltas(aggregate, group, deltasByControlPoint, feature, boneIndexByPath);
                var normalized = NormalizeWeights(aggregate);
                foreach (var weight in normalized)
                {
                    outputWeights.Add(new BoneWeight1 { boneIndex = weight.index, weight = weight.weight });
                }

                outputCounts.Add((byte)normalized.Count);
            }

            existingWeights.Dispose();
            existingCounts.Dispose();
            if (diagnostics.Count > 0)
            {
                return;
            }

            mesh.SetBoneWeights(outputCounts.AsArray(), outputWeights.AsArray());
        }

        private static IEnumerable<string> GetNeededBonePathsInGraphOrder(SkinWeightFeatureObject feature)
        {
            var needed = new HashSet<string>();
            foreach (var weights in feature.ControlPointWeights ?? Array.Empty<SkinWeightControlPointData>())
            {
                foreach (var influence in weights?.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence == null || influence.m_BoneIndex < 0 || feature.m_BonePaths == null || influence.m_BoneIndex >= feature.m_BonePaths.Length)
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

        private static void EnsureBone(
            string path,
            SkinWeightFeatureObject feature,
            Transform targetRoot,
            Request request,
            List<Transform> bones,
            List<Matrix4x4> bindposes,
            Dictionary<string, int> boneIndexByPath,
            IReadOnlyDictionary<string, BlendShareBoneProxyComponent> proxiesByPath,
            List<string> diagnostics)
        {
            if (boneIndexByPath.ContainsKey(path))
            {
                return;
            }

            Transform bone = null;
            if (proxiesByPath.TryGetValue(path, out var proxy) && proxy != null)
            {
                bone = proxy.transform;
            }

            var boneData = feature.m_BoneGraph != null ? feature.m_BoneGraph.GetBone(path) : null;
            if (bone == null)
            {
                bone = MeshNodePath.FindRelativeTransform(targetRoot, path);
            }

            if (bone == null && request.CreateBone != null && boneData != null && boneData.m_CreateIfMissing)
            {
                bone = request.CreateBone(path, boneData);
            }

            if (bone == null)
            {
                diagnostics.Add($"Cannot resolve BlendShare bone '{path}'.");
                return;
            }

            int index = bones.Count;
            bones.Add(bone);
            bindposes.Add(bone.worldToLocalMatrix * request.Renderer.transform.localToWorldMatrix);
            boneIndexByPath[path] = index;
        }

        private static void ApplyWeightDeltas(
            Dictionary<int, float> aggregate,
            FbxIndexGroup group,
            IReadOnlyDictionary<int, SkinWeightControlPointData> deltasByControlPoint,
            SkinWeightFeatureObject feature,
            IReadOnlyDictionary<string, int> boneIndexByPath)
        {
            int contributing = 0;
            var deltasByBoneIndex = new Dictionary<int, float>();
            foreach (int controlPoint in group.m_Indices ?? Array.Empty<int>())
            {
                if (!deltasByControlPoint.TryGetValue(controlPoint, out var weights))
                {
                    continue;
                }

                contributing++;
                foreach (var influence in weights.m_Influences ?? Array.Empty<SkinWeightInfluenceData>())
                {
                    if (influence == null || Mathf.Abs(influence.m_Weight) <= WeightEpsilon || influence.m_BoneIndex < 0 || feature.m_BonePaths == null || influence.m_BoneIndex >= feature.m_BonePaths.Length)
                    {
                        continue;
                    }

                    string path = MeshNodePath.Normalize(feature.m_BonePaths[influence.m_BoneIndex]);
                    if (!boneIndexByPath.TryGetValue(path, out int finalIndex))
                    {
                        continue;
                    }

                    deltasByBoneIndex.TryGetValue(finalIndex, out float existing);
                    deltasByBoneIndex[finalIndex] = existing + influence.m_Weight;
                }
            }

            if (contributing > 1)
            {
                foreach (int key in deltasByBoneIndex.Keys.ToArray())
                {
                    deltasByBoneIndex[key] /= contributing;
                }
            }

            foreach (var pair in deltasByBoneIndex)
            {
                aggregate.TryGetValue(pair.Key, out float existing);
                float updated = Mathf.Max(0f, existing + pair.Value);
                if (updated <= WeightEpsilon)
                {
                    aggregate.Remove(pair.Key);
                }
                else
                {
                    aggregate[pair.Key] = updated;
                }
            }
        }

        private static List<(int index, float weight)> NormalizeWeights(Dictionary<int, float> aggregate)
        {
            var weights = aggregate
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
    }
}
