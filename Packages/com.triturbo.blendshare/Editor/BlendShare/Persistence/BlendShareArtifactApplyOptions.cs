using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Persistence
{
    public sealed class BlendShareArtifactApplyOptions
    {
        public bool UseUndo { get; set; } = true;
        public bool RecordDestructiveMarkers { get; set; } = true;
        public bool MarkObjectsDirty { get; set; } = true;
        public string UndoName { get; set; }
        public Action<Mesh> SaveGeneratedMesh { get; set; }
        public Transform BonePathRoot { get; set; }
        public IReadOnlyDictionary<string, Transform> BoneTransformOverrides { get; set; }
    }

    public sealed class BlendShareArtifactApplyResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<SkinnedMeshRenderer> appliedRenderers = new();
        private readonly List<BlendShareGeneratedBoneRecord> generatedBones = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<SkinnedMeshRenderer> AppliedRenderers => appliedRenderers;
        public IReadOnlyList<BlendShareGeneratedBoneRecord> GeneratedBones => generatedBones;
        public bool Success => diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }

        internal void AddAppliedRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer != null && !appliedRenderers.Contains(renderer))
            {
                appliedRenderers.Add(renderer);
            }
        }

        internal void AddGeneratedBone(string artifactPath, Transform transform, bool created)
        {
            if (transform == null || string.IsNullOrWhiteSpace(artifactPath))
            {
                return;
            }

            if (generatedBones.Exists(record =>
                    record.Transform == transform && record.ArtifactPath == artifactPath))
            {
                return;
            }

            generatedBones.Add(new BlendShareGeneratedBoneRecord(artifactPath, transform, created));
        }
    }

    public sealed class BlendShareGeneratedBoneRecord
    {
        internal BlendShareGeneratedBoneRecord(string artifactPath, Transform transform, bool created)
        {
            ArtifactPath = artifactPath;
            Transform = transform;
            Created = created;
        }

        public string ArtifactPath { get; }
        public Transform Transform { get; }
        public bool Created { get; }
    }
}
