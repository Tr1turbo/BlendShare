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
    }

    public sealed class BlendShareArtifactApplyResult
    {
        private readonly List<string> diagnostics = new();
        private readonly List<SkinnedMeshRenderer> appliedRenderers = new();

        public IReadOnlyList<string> Diagnostics => diagnostics;
        public IReadOnlyList<SkinnedMeshRenderer> AppliedRenderers => appliedRenderers;
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
    }
}
