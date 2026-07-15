using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;

namespace Triturbo.BlendShare.Features.BlendShapes
{
    public enum BlendShapeBaseMode
    {
        PreserveSourceRelative,
        RebaseOntoOriginal
    }

    public sealed class BlendShapeExtractionOptions : MeshFeatureExtractionOptions
    {
        public const float DefaultDeltaComparisonTolerance = 0.0001f;

        private readonly Dictionary<string, List<string>> selectedBlendShapeNamesByMesh = new();
        private readonly HashSet<string> disabledMeshKeys = new();
        private readonly Dictionary<string, BlendShapeBaseMode> baseModeByMesh = new();
        private float deltaComparisonTolerance = DefaultDeltaComparisonTolerance;

        public override string FeatureId => BlendShapeFeatureObject.Id;

        public List<string> SelectedBlendShapeNames = new();
        public BlendShapeBaseMode BaseMode = BlendShapeBaseMode.PreserveSourceRelative;
        public float BlendShapeScale = 1f;
        public float DeltaComparisonTolerance
        {
            get => deltaComparisonTolerance;
            set => deltaComparisonTolerance = float.IsNaN(value) || float.IsInfinity(value)
                ? DefaultDeltaComparisonTolerance
                : UnityEngine.Mathf.Max(0f, value);
        }

        public BlendShapeBaseMode GetBaseMode(string path)
        {
            return baseModeByMesh.TryGetValue(MeshFeatureExtractionSession.BuildMeshKey(path), out var mode)
                ? mode
                : BaseMode;
        }

        public void SetBaseMode(string path, BlendShapeBaseMode mode)
        {
            baseModeByMesh[MeshFeatureExtractionSession.BuildMeshKey(path)] = mode;
        }

        public bool HasBaseModeOverride(string path)
        {
            return baseModeByMesh.ContainsKey(MeshFeatureExtractionSession.BuildMeshKey(path));
        }

        public void SetBaseModeOverride(string path, bool enabled)
        {
            string key = MeshFeatureExtractionSession.BuildMeshKey(path);
            if (enabled)
            {
                if (!baseModeByMesh.ContainsKey(key))
                {
                    baseModeByMesh[key] = BaseMode;
                }
            }
            else
            {
                baseModeByMesh.Remove(key);
            }
        }

        /// <inheritdoc />
        public override bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled && HasAnySelectedBlendShapeNames(meshes);
        }

        /// <inheritdoc />
        public override bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            return Enabled &&
                   mesh != null &&
                   IsMeshEnabled(mesh.Path) &&
                   GetSelectedBlendShapeNames(mesh.Path).Count > 0;
        }

        public bool IsMeshEnabled(string path)
        {
            return !disabledMeshKeys.Contains(MeshFeatureExtractionSession.BuildMeshKey(path));
        }

        public void SetMeshEnabled(string path, bool enabled)
        {
            string key = MeshFeatureExtractionSession.BuildMeshKey(path);
            if (enabled)
            {
                disabledMeshKeys.Remove(key);
            }
            else
            {
                disabledMeshKeys.Add(key);
            }
        }

        /// <summary>
        /// Stores the selected blendshape names for one mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <param name="shapeNames">Blendshape names selected for extraction.</param>
        public void SetSelectedBlendShapeNames(
            string path,
            IEnumerable<string> shapeNames)
        {
            selectedBlendShapeNamesByMesh[MeshFeatureExtractionSession.BuildMeshKey(path)] =
                shapeNames?.Where(shapeName => !string.IsNullOrWhiteSpace(shapeName)).Distinct().ToList() ??
                new List<string>();
        }

        /// <summary>
        /// Checks whether this options object has an explicit selection for a mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <returns><c>true</c> when an explicit path-specific selection exists.</returns>
        public bool HasSelectedBlendShapeNames(string path)
        {
            return selectedBlendShapeNamesByMesh.ContainsKey(
                MeshFeatureExtractionSession.BuildMeshKey(path));
        }

        /// <summary>
        /// Gets the selected blendshape names for a mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <returns>Path-specific selections, or the global selection list when none exists.</returns>
        public List<string> GetSelectedBlendShapeNames(string path)
        {
            if (selectedBlendShapeNamesByMesh.TryGetValue(
                    MeshFeatureExtractionSession.BuildMeshKey(path),
                    out var meshSpecific))
            {
                return meshSpecific;
            }

            return SelectedBlendShapeNames?
                .Where(shapeName => !string.IsNullOrWhiteSpace(shapeName))
                .Distinct()
                .ToList() ?? new List<string>();
        }

        /// <summary>
        /// Checks whether any requested mesh has at least one selected blendshape.
        /// </summary>
        /// <param name="meshes">Mesh path requests to inspect.</param>
        /// <returns><c>true</c> when at least one mesh has selected blendshapes.</returns>
        public bool HasAnySelectedBlendShapeNames(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                .Any(mesh => mesh != null &&
                             IsMeshEnabled(mesh.Path) &&
                             GetSelectedBlendShapeNames(mesh.Path).Count > 0);
        }

    }
}
