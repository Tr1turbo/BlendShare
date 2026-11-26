using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Assets;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Artifacts
{
    public sealed class ArtifactMeshBuildInput
    {
        public string MeshId { get; set; }
        public string NodePath { get; set; }
        public UnityMeshApplyResult ApplyResult { get; set; }
        public Transform TargetRootTransform { get; set; }
    }

    public static class BlendShareArtifactBuilder
    {
        public static BlendShareGeneratedArtifact CreateInMemory(UnityEngine.Object targetSource,
            IReadOnlyList<string> appliedPatchIds, IReadOnlyList<ArtifactMeshBuildInput> meshResults)
        {
            if (meshResults == null) throw new ArgumentNullException(nameof(meshResults));
            var skeletonBonesByPath = new Dictionary<string, GeneratedSkeletonBoneRecord>(StringComparer.Ordinal);
            var entries = meshResults.Select(input =>
            {
                if (input == null) throw new ArgumentException("Null ArtifactMeshBuildInput.");
                var result = input.ApplyResult;
                if (result == null || !result.Succeeded || !result.OutputMesh) throw new ArgumentException("Missing successful mesh result.");
                var paths = (result.OutputBones ?? Array.Empty<Transform>()).Select(b => b ? AnimationUtility.CalculateTransformPath(b, input.TargetRootTransform) : "").ToArray();
                var bindings = (result.AppliedBoneBindings ?? Array.Empty<AppliedBoneBinding>()).Select(b => new GeneratedBoneBindingRecord(b.PatchId,
                    b.SourceBonePath, b.TargetBonePath, b.BindPose, b.BoneIndex, b.IsNewBone)).ToArray();
                if (result.AppliedSkeletonBones != null)
                {
                    foreach (var bone in result.AppliedSkeletonBones)
                    {
                        if (bone == null || string.IsNullOrEmpty(bone.Path)) continue;
                        if (skeletonBonesByPath.TryGetValue(bone.Path, out var existing))
                        {
                            if (!existing.ApproximatelyMatches(bone))
                            {
                                throw new InvalidOperationException(
                                    $"Conflicting skeleton bone transforms for '{bone.Path}' across meshes.");
                            }
                        }
                        else
                        {
                            skeletonBonesByPath[bone.Path] = bone;
                        }
                    }
                }

                return new GeneratedMeshEntry(input.MeshId, input.NodePath, result.OutputMesh, paths, bindings, result.OutputRootBonePath ?? string.Empty);
            }).ToArray();

            var orderedSkeletonBones = skeletonBonesByPath.Values
                .OrderBy(b => b.Path.Count(c => c == '/'))
                .ThenBy(b => b.Path, StringComparer.Ordinal)
                .ToArray();

            var artifact = ScriptableObject.CreateInstance<BlendShareGeneratedArtifact>();
            artifact.Initialize(targetSource, (appliedPatchIds ?? Array.Empty<string>()).ToArray(), entries, orderedSkeletonBones);
            return artifact;
        }

        public static BlendShareGeneratedArtifact SaveToAsset(BlendShareGeneratedArtifact inMemoryArtifact, string assetPath)
        {
            if (inMemoryArtifact == null) throw new ArgumentNullException(nameof(inMemoryArtifact));
            if (string.IsNullOrWhiteSpace(assetPath)) throw new ArgumentException("Artifact asset path must be non-empty.", nameof(assetPath));
            if (File.Exists(assetPath)) throw new IOException("Artifact already exists at " + assetPath);
            var artifact = ScriptableObject.CreateInstance<BlendShareGeneratedArtifact>();
            var clones = new List<Mesh>();
            try
            {
                AssetDatabase.CreateAsset(artifact, assetPath);
                if (!AssetDatabase.Contains(artifact)) throw new IOException("Could not create " + assetPath);
                var entries = new List<GeneratedMeshEntry>();
                foreach (var entry in inMemoryArtifact.Meshes)
                {
                    var mesh = UnityEngine.Object.Instantiate(entry.Mesh);
                    clones.Add(mesh);
                    mesh.name = entry.Mesh.name;
                    mesh.hideFlags = HideFlags.None;
                    AssetDatabase.AddObjectToAsset(mesh, artifact);
                    entries.Add(new GeneratedMeshEntry(entry.MeshId, entry.NodePath, mesh,
                        entry.BonePaths.ToArray(), entry.BoneBindings.ToArray(), entry.RootBonePath));
                }
                var skeletonBones = (inMemoryArtifact.SkeletonBones ?? Array.Empty<GeneratedSkeletonBoneRecord>())
                    .Where(b => b != null)
                    .Select(b => new GeneratedSkeletonBoneRecord(b.Path, b.LocalPosition, b.LocalRotation, b.LocalScale))
                    .ToArray();
                artifact.Initialize(inMemoryArtifact.TargetSource, inMemoryArtifact.AppliedPatchIds.ToArray(), entries.ToArray(), skeletonBones);
                EditorUtility.SetDirty(artifact);
                AssetDatabase.SaveAssets();
                return artifact;
            }
            catch
            {
                if (AssetDatabase.Contains(artifact)) AssetDatabase.DeleteAsset(assetPath);
                foreach (var mesh in clones) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                if (artifact) UnityEngine.Object.DestroyImmediate(artifact);
                throw;
            }
        }
    }
}
