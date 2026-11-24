using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Unity;
using UnityEngine;

namespace Triturbo.BlendShare.Features.SkinWeights
{
    internal enum SkinWeightSelectionMode
    {
        Simple,
        Advanced
    }

    internal sealed class SkinWeightResolvedSelection
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> weightBonesByMesh;
        private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> bindposeBonesByMesh;

        internal IReadOnlyCollection<string> TransformBonePaths { get; }
        internal IReadOnlyCollection<string> NewBonePaths { get; }

        internal SkinWeightResolvedSelection(
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> weightBonesByMesh,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> bindposeBonesByMesh,
            IReadOnlyCollection<string> transformBonePaths,
            IReadOnlyCollection<string> newBonePaths)
        {
            this.weightBonesByMesh = weightBonesByMesh ??
                                     new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            this.bindposeBonesByMesh = bindposeBonesByMesh ??
                                       new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            TransformBonePaths = transformBonePaths ?? Array.Empty<string>();
            NewBonePaths = newBonePaths ?? Array.Empty<string>();
        }

        internal IReadOnlyCollection<string> GetWeightBonePaths(string meshPath)
        {
            return weightBonesByMesh.TryGetValue(
                MeshFeatureExtractionSession.BuildMeshKey(meshPath),
                out var paths)
                ? paths
                : Array.Empty<string>();
        }

        internal IReadOnlyCollection<string> GetBindposeBonePaths(string meshPath)
        {
            return bindposeBonesByMesh.TryGetValue(
                MeshFeatureExtractionSession.BuildMeshKey(meshPath),
                out var paths)
                ? paths
                : Array.Empty<string>();
        }
    }

    public sealed class SkinWeightExtractionOptions : MeshFeatureExtractionOptions
    {
        private readonly Dictionary<string, HashSet<string>> selectedWeightBonePathsByMesh = new();
        private readonly Dictionary<string, HashSet<string>> selectedBindposeBonePathsByMesh = new();
        private readonly HashSet<string> selectedTransformBonePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> forcedTransformBonePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> selectedNewBonePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> selectedSimpleMeshKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> selectedSimpleBonePaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> disabledMeshKeys = new(StringComparer.Ordinal);
        private string armatureOnlyHostMeshKey;
        private bool sharedArmatureInitialized;
        private MeshFeatureExtractionSession sharedArmatureSession;
        private FbxArmatureObject sharedArmature;

        public override string FeatureId => SkinWeightFeatureObject.Id;
        internal SkinWeightSelectionMode SelectionMode { get; set; } = SkinWeightSelectionMode.Simple;
        internal bool DefaultsInitialized { get; set; }

        public SkinWeightExtractionOptions()
        {
            Enabled = false;
        }

        public override bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled &&
                   (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                   .Any(request => ShouldExtractMesh(request));
        }

        public override bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            if (!Enabled || mesh == null)
            {
                return false;
            }

            string meshKey = MeshFeatureExtractionSession.BuildMeshKey(mesh.Path);
            return (IsMeshEnabled(mesh.Path) &&
                    (GetSelectedWeightBonePaths(mesh.Path).Count > 0 ||
                     GetSelectedBindposeBonePaths(mesh.Path).Count > 0)) ||
                   meshKey == armatureOnlyHostMeshKey;
        }

        internal bool IsMeshEnabled(string meshPath)
        {
            return !disabledMeshKeys.Contains(MeshFeatureExtractionSession.BuildMeshKey(meshPath));
        }

        internal void SetMeshEnabled(string meshPath, bool enabled)
        {
            SetSelection(
                disabledMeshKeys,
                MeshFeatureExtractionSession.BuildMeshKey(meshPath),
                !enabled);
        }

        internal void ClearMeshEnabledOverrides()
        {
            disabledMeshKeys.Clear();
        }

        internal FbxArmatureObject GetOrCreateSharedArmature(
            MeshFeatureExtractionSession session,
            Func<FbxArmatureObject> createArmature)
        {
            if (!sharedArmatureInitialized || !ReferenceEquals(sharedArmatureSession, session))
            {
                // Every skin feature extracted from one FBX pair must reference the same armature subasset.
                sharedArmatureInitialized = true;
                sharedArmatureSession = session;
                sharedArmature = createArmature?.Invoke();
            }

            return sharedArmature;
        }

        public bool IsWeightClusterSelected(string meshPath, string bonePath)
        {
            return TryGetSelectedSet(selectedWeightBonePathsByMesh, meshPath, out var selected) &&
                   selected.Contains(NormalizeBonePath(bonePath));
        }

        public void SetWeightClusterSelected(string meshPath, string bonePath, bool selected)
        {
            SetMeshBoneSelection(selectedWeightBonePathsByMesh, meshPath, bonePath, selected);
        }

        public IReadOnlyCollection<string> GetSelectedWeightBonePaths(string meshPath)
        {
            return TryGetSelectedSet(selectedWeightBonePathsByMesh, meshPath, out var selected)
                ? selected.ToArray()
                : Array.Empty<string>();
        }

        public bool IsBindposeSelected(string meshPath, string bonePath)
        {
            return TryGetSelectedSet(selectedBindposeBonePathsByMesh, meshPath, out var selected) &&
                   selected.Contains(NormalizeBonePath(bonePath));
        }

        public void SetBindposeSelected(string meshPath, string bonePath, bool selected)
        {
            SetMeshBoneSelection(selectedBindposeBonePathsByMesh, meshPath, bonePath, selected);
        }

        public IReadOnlyCollection<string> GetSelectedBindposeBonePaths(string meshPath)
        {
            return TryGetSelectedSet(selectedBindposeBonePathsByMesh, meshPath, out var selected)
                ? selected.ToArray()
                : Array.Empty<string>();
        }

        public bool IsBoneTransformSelected(string bonePath)
        {
            string normalized = NormalizeBonePath(bonePath);
            return selectedTransformBonePaths.Contains(normalized) ||
                   forcedTransformBonePaths.Contains(normalized);
        }

        public void SetBoneTransformSelected(string bonePath, bool selected)
        {
            string normalized = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedTransformBonePaths.Add(normalized);
            }
            else
            {
                selectedTransformBonePaths.Remove(normalized);
            }
        }

        public IReadOnlyCollection<string> GetSelectedBoneTransformPaths()
        {
            return selectedTransformBonePaths
                .Concat(forcedTransformBonePaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public bool IsNewBoneSelected(string bonePath)
        {
            return selectedNewBonePaths.Contains(NormalizeBonePath(bonePath));
        }

        public void SetNewBoneSelected(string bonePath, bool selected)
        {
            string normalized = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedNewBonePaths.Add(normalized);
            }
            else
            {
                selectedNewBonePaths.Remove(normalized);
            }
        }

        public IReadOnlyCollection<string> GetSelectedNewBonePaths()
        {
            return selectedNewBonePaths.ToArray();
        }

        internal bool IsSimpleMeshSelected(string meshPath)
        {
            return selectedSimpleMeshKeys.Contains(MeshFeatureExtractionSession.BuildMeshKey(meshPath));
        }

        internal void SetSimpleMeshSelected(string meshPath, bool selected)
        {
            SetSelection(selectedSimpleMeshKeys, MeshFeatureExtractionSession.BuildMeshKey(meshPath), selected);
        }

        internal IReadOnlyCollection<string> GetSelectedSimpleMeshKeys()
        {
            return selectedSimpleMeshKeys.ToArray();
        }

        internal bool IsSimpleBoneSelected(string bonePath)
        {
            return selectedSimpleBonePaths.Contains(NormalizeBonePath(bonePath));
        }

        internal void SetSimpleBoneSelected(string bonePath, bool selected)
        {
            SetSelection(selectedSimpleBonePaths, NormalizeBonePath(bonePath), selected);
        }

        internal IReadOnlyCollection<string> GetSelectedSimpleBonePaths()
        {
            return selectedSimpleBonePaths.ToArray();
        }

        internal void ClearSimpleAxes()
        {
            selectedSimpleMeshKeys.Clear();
            selectedSimpleBonePaths.Clear();
        }

        internal void ClearMeshSelections()
        {
            selectedWeightBonePathsByMesh.Clear();
            selectedBindposeBonePathsByMesh.Clear();
        }

        internal void ClearArmatureSelections()
        {
            selectedTransformBonePaths.Clear();
            forcedTransformBonePaths.Clear();
            selectedNewBonePaths.Clear();
            armatureOnlyHostMeshKey = null;
        }

        internal bool IsBoneTransformForced(string bonePath)
        {
            return forcedTransformBonePaths.Contains(NormalizeBonePath(bonePath));
        }

        internal void SetBoneTransformForced(string bonePath, bool selected)
        {
            SetSelection(forcedTransformBonePaths, NormalizeBonePath(bonePath), selected);
        }

        internal IReadOnlyCollection<string> GetExplicitBoneTransformPaths()
        {
            return selectedTransformBonePaths.ToArray();
        }

        internal void ClearExplicitBoneTransformSelections()
        {
            selectedTransformBonePaths.Clear();
        }

        internal void ClearAutomaticArmatureSelections()
        {
            forcedTransformBonePaths.Clear();
            selectedNewBonePaths.Clear();
            armatureOnlyHostMeshKey = null;
        }

        internal bool HasMeshScopedSelections()
        {
            return selectedWeightBonePathsByMesh.Keys
                       .Concat(selectedBindposeBonePathsByMesh.Keys)
                       .Distinct(StringComparer.Ordinal)
                       .Any(meshKey => !disabledMeshKeys.Contains(meshKey));
        }

        internal void SetArmatureOnlyHostMesh(string meshPath)
        {
            armatureOnlyHostMeshKey = string.IsNullOrWhiteSpace(meshPath)
                ? null
                : MeshFeatureExtractionSession.BuildMeshKey(meshPath);
        }

        internal SkinWeightResolvedSelection ResolveSelection()
        {
            return new SkinWeightResolvedSelection(
                CopyEnabledSelections(selectedWeightBonePathsByMesh, disabledMeshKeys),
                CopyEnabledSelections(selectedBindposeBonePathsByMesh, disabledMeshKeys),
                GetSelectedBoneTransformPaths().OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                selectedNewBonePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }

        internal IEnumerable<(string MeshKey, string BonePath)> GetSelectedMeshBonePairs()
        {
            return selectedWeightBonePathsByMesh
                .SelectMany(pair => pair.Value.Select(path => (pair.Key, path)))
                .Concat(selectedBindposeBonePathsByMesh
                    .SelectMany(pair => pair.Value.Select(path => (pair.Key, path))))
                .Distinct();
        }

        internal void EnsureRequiredParentSelections(SkinWeightFbxComparison comparison)
        {
            if (comparison == null)
            {
                return;
            }

            foreach (string path in selectedNewBonePaths.ToArray())
            {
                string parent = MeshNodePath.ParentPath(path);
                while (parent != MeshNodePath.Root)
                {
                    if (comparison.TryGetBone(parent, out var parentBone) &&
                        parentBone.RequiresCreateBone)
                    {
                        selectedNewBonePaths.Add(parent);
                        forcedTransformBonePaths.Add(parent);
                    }

                    parent = MeshNodePath.ParentPath(parent);
                }
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> CopyEnabledSelections(
            IReadOnlyDictionary<string, HashSet<string>> selections,
            HashSet<string> disabledMeshKeys)
        {
            return selections
                .Where(pair => !disabledMeshKeys.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyCollection<string>)pair.Value
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);
        }

        private static void SetSelection(HashSet<string> selections, string key, bool selected)
        {
            if (selected)
            {
                selections.Add(key);
            }
            else
            {
                selections.Remove(key);
            }
        }

        private static bool TryGetSelectedSet(
            Dictionary<string, HashSet<string>> selectionsByMesh,
            string meshPath,
            out HashSet<string> selected)
        {
            return selectionsByMesh.TryGetValue(
                MeshFeatureExtractionSession.BuildMeshKey(meshPath),
                out selected);
        }

        private static void SetMeshBoneSelection(
            Dictionary<string, HashSet<string>> selectionsByMesh,
            string meshPath,
            string bonePath,
            bool selected)
        {
            string meshKey = MeshFeatureExtractionSession.BuildMeshKey(meshPath);
            if (!selectionsByMesh.TryGetValue(meshKey, out var selectedBones))
            {
                selectedBones = new HashSet<string>(StringComparer.Ordinal);
                selectionsByMesh[meshKey] = selectedBones;
            }

            string normalizedBonePath = NormalizeBonePath(bonePath);
            if (selected)
            {
                selectedBones.Add(normalizedBonePath);
            }
            else
            {
                selectedBones.Remove(normalizedBonePath);
                if (selectedBones.Count == 0)
                {
                    selectionsByMesh.Remove(meshKey);
                }
            }
        }

        private static string NormalizeBonePath(string bonePath)
        {
            return MeshNodePath.Normalize(bonePath);
        }
    }

    internal sealed class SkinWeightSelectionController
    {
        private readonly SkinWeightExtractionOptions options;
        private readonly SkinWeightFbxComparison comparison;

        internal SkinWeightSelectionMode Mode => options.SelectionMode;

        internal SkinWeightSelectionController(
            SkinWeightExtractionOptions options,
            SkinWeightFbxComparison comparison)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.comparison = comparison ?? SkinWeightFbxComparison.Empty;
        }

        internal void EnsureDefaultSelection()
        {
            if (options.DefaultsInitialized)
            {
                return;
            }

            options.DefaultsInitialized = true;
            RestoreAutomaticSelection();
        }

        internal void SwitchToAdvanced()
        {
            options.SelectionMode = SkinWeightSelectionMode.Advanced;
            foreach (var mesh in comparison.Meshes)
            {
                options.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
            }
            RebuildArmatureDependencies();
        }

        internal int GetSimpleExpansionCount()
        {
            var selectedPairs = options.GetSelectedMeshBonePairs().ToArray();
            var meshKeys = new HashSet<string>(
                selectedPairs
                    .Select(pair => pair.MeshKey)
                    .Where(meshKey => comparison.Meshes.Any(mesh =>
                        MeshFeatureExtractionSession.BuildMeshKey(mesh.MeshPath) == meshKey &&
                        options.IsMeshEnabled(mesh.MeshPath))),
                StringComparer.Ordinal);
            var bonePaths = new HashSet<string>(
                selectedPairs.Select(pair => pair.BonePath)
                    .Concat(options.GetExplicitBoneTransformPaths()),
                StringComparer.Ordinal);
            int additional = 0;
            foreach (var mesh in comparison.Meshes)
            {
                string meshKey = MeshFeatureExtractionSession.BuildMeshKey(mesh.MeshPath);
                if (!meshKeys.Contains(meshKey))
                {
                    continue;
                }

                foreach (var pair in mesh.Bones.Where(pair => bonePaths.Contains(pair.BonePath)))
                {
                    if (!options.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) &&
                        !options.IsBindposeSelected(pair.MeshPath, pair.BonePath))
                    {
                        additional++;
                    }
                }
            }

            return additional;
        }

        internal void SwitchToSimple()
        {
            var selectedPairs = options.GetSelectedMeshBonePairs().ToArray();
            options.ClearSimpleAxes();
            foreach (string meshKey in selectedPairs.Select(pair => pair.MeshKey).Distinct(StringComparer.Ordinal))
            {
                var mesh = comparison.Meshes.FirstOrDefault(candidate =>
                    MeshFeatureExtractionSession.BuildMeshKey(candidate.MeshPath) == meshKey);
                if (mesh != null && options.IsMeshEnabled(mesh.MeshPath))
                {
                    options.SetSimpleMeshSelected(mesh.MeshPath, true);
                }
            }

            foreach (string bonePath in selectedPairs.Select(pair => pair.BonePath)
                         .Concat(options.GetExplicitBoneTransformPaths())
                         .Distinct(StringComparer.Ordinal))
            {
                options.SetSimpleBoneSelected(bonePath, true);
            }

            options.SelectionMode = SkinWeightSelectionMode.Simple;
            ApplySimpleCartesianSelection();
        }

        internal bool IsSimpleMeshSelected(string meshPath)
        {
            return options.IsSimpleMeshSelected(meshPath) && options.IsMeshEnabled(meshPath);
        }

        internal void SetSimpleMeshSelected(string meshPath, bool selected)
        {
            options.SetSimpleMeshSelected(meshPath, selected);
            ApplySimpleCartesianSelection();
        }

        internal bool IsSimpleBoneSelected(string bonePath)
        {
            return options.IsSimpleBoneSelected(bonePath);
        }

        internal bool IsWeightSelected(SkinWeightMeshBoneComparison pair)
        {
            return pair != null && options.IsWeightClusterSelected(pair.MeshPath, pair.BonePath);
        }

        internal bool IsBindposeSelected(SkinWeightMeshBoneComparison pair)
        {
            return pair != null && options.IsBindposeSelected(pair.MeshPath, pair.BonePath);
        }

        internal bool IsAdvancedMeshEnabled(SkinWeightMeshComparison mesh)
        {
            return mesh != null && options.IsMeshEnabled(mesh.MeshPath);
        }

        internal bool IsTransformSelected(string bonePath)
        {
            return options.IsBoneTransformSelected(bonePath);
        }

        internal bool IsTransformForced(string bonePath)
        {
            return options.IsBoneTransformForced(bonePath);
        }

        internal void SetAdvancedTransformSelected(string bonePath, bool selected)
        {
            if (IsTransformForced(bonePath))
            {
                return;
            }

            options.SetBoneTransformSelected(bonePath, selected);
            RebuildArmatureDependencies();
        }

        internal void SetAdvancedTransformsEnabled(bool selected)
        {
            options.ClearExplicitBoneTransformSelections();
            if (selected)
            {
                foreach (var bone in comparison.Bones.Where(bone => bone.SourceBoneExists))
                {
                    options.SetBoneTransformSelected(bone.BonePath, true);
                }
            }

            RebuildArmatureDependencies();
        }

        internal void RestoreAdvancedTransforms()
        {
            options.ClearExplicitBoneTransformSelections();
            foreach (var bone in comparison.Bones.Where(bone =>
                         bone.SourceBoneExists &&
                         bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
            {
                options.SetBoneTransformSelected(bone.BonePath, true);
            }

            RebuildArmatureDependencies();
        }

        internal void DisableChangedAdvancedTransforms()
        {
            foreach (var bone in comparison.Bones.Where(bone =>
                         bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
            {
                options.SetBoneTransformSelected(bone.BonePath, false);
            }

            RebuildArmatureDependencies();
        }

        internal void SetSimpleBoneSelected(string bonePath, bool selected)
        {
            options.SetSimpleBoneSelected(bonePath, selected);
            ApplySimpleCartesianSelection();
        }

        internal void SetAdvancedWeightSelected(SkinWeightMeshBoneComparison pair, bool selected)
        {
            if (pair?.Weight == null)
            {
                return;
            }

            options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, selected);
            RebuildArmatureDependencies();
        }

        internal void SetAdvancedBindposeSelected(SkinWeightMeshBoneComparison pair, bool selected)
        {
            if (pair?.Bindpose == null || !pair.Bindpose.SourceExists)
            {
                return;
            }

            options.SetBindposeSelected(pair.MeshPath, pair.BonePath, selected);
            RebuildArmatureDependencies();
        }

        internal void SetAdvancedMeshEnabled(SkinWeightMeshComparison mesh, bool selected)
        {
            if (mesh == null)
            {
                return;
            }

            options.SetMeshEnabled(mesh.MeshPath, selected);
            RebuildArmatureDependencies();
        }

        internal void SetAdvancedMeshDataEnabled(SkinWeightMeshComparison mesh, bool selected)
        {
            if (mesh == null)
            {
                return;
            }

            foreach (var pair in mesh.Bones)
            {
                options.SetWeightClusterSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Weight != null);
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Bindpose?.SourceExists == true);
            }

            RebuildArmatureDependencies();
        }

        internal void SetAdvancedMeshesEnabled(bool selected)
        {
            foreach (var mesh in comparison.Meshes)
            {
                options.SetMeshEnabled(mesh.MeshPath, selected);
            }

            RebuildArmatureDependencies();
        }

        internal void SetAdvancedWeightsEnabled(bool selected)
        {
            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                options.SetWeightClusterSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Weight != null);
            }

            RebuildArmatureDependencies();
        }

        internal void SetAdvancedBindposesEnabled(bool selected)
        {
            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Bindpose?.SourceExists == true);
            }

            RebuildArmatureDependencies();
        }

        internal void SetAdvancedMeshWeightsEnabled(SkinWeightMeshComparison mesh, bool selected)
        {
            if (mesh == null)
            {
                return;
            }

            foreach (var pair in mesh.Bones)
            {
                options.SetWeightClusterSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Weight != null);
            }

            RebuildArmatureDependencies();
        }

        internal void SetAdvancedMeshBindposesEnabled(SkinWeightMeshComparison mesh, bool selected)
        {
            if (mesh == null)
            {
                return;
            }

            foreach (var pair in mesh.Bones)
            {
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    selected && pair.Bindpose?.SourceExists == true);
            }

            RebuildArmatureDependencies();
        }

        internal void RestoreAdvancedMeshes()
        {
            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                options.SetWeightClusterSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    ShouldAutomaticallySelectWeight(pair));
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    ShouldAutomaticallySelectBindpose(pair));
            }
            foreach (var mesh in comparison.Meshes)
            {
                options.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
            }

            RebuildArmatureDependencies();
        }

        internal void DisableChangedAdvancedMeshes()
        {
            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                {
                    options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, false);
                }

                if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                {
                    options.SetBindposeSelected(pair.MeshPath, pair.BonePath, false);
                }
            }

            RebuildArmatureDependencies();
        }

        internal void RestoreAdvancedMesh(SkinWeightMeshComparison mesh)
        {
            if (mesh == null)
            {
                return;
            }

            foreach (var pair in mesh.Bones)
            {
                bool selectWeight = ShouldAutomaticallySelectWeight(pair);
                bool selectBindpose = ShouldAutomaticallySelectBindpose(pair);
                options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, selectWeight);
                options.SetBindposeSelected(pair.MeshPath, pair.BonePath, selectBindpose);
            }

            options.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
            RebuildArmatureDependencies();
        }

        internal void DisableChangedAdvancedMesh(SkinWeightMeshComparison mesh)
        {
            if (mesh == null)
            {
                return;
            }

            foreach (var pair in mesh.Bones)
            {
                if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                {
                    SetAdvancedWeightSelected(pair, false);
                }

                if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                {
                    SetAdvancedBindposeSelected(pair, false);
                }
            }
            RebuildArmatureDependencies();
        }

        internal void SelectAll()
        {
            ClearAll();
            if (Mode == SkinWeightSelectionMode.Simple)
            {
                foreach (var mesh in comparison.Meshes)
                {
                    options.SetSimpleMeshSelected(mesh.MeshPath, true);
                }

                foreach (var bone in comparison.RelevantBones)
                {
                    options.SetSimpleBoneSelected(bone.BonePath, true);
                }

                ApplySimpleCartesianSelection();
                return;
            }

            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, pair.Weight != null);
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    pair.Bindpose?.SourceExists == true);
            }
            foreach (var bone in comparison.Bones.Where(bone => bone.SourceBoneExists))
            {
                options.SetBoneTransformSelected(bone.BonePath, true);
            }
            RebuildArmatureDependencies();
        }

        internal void ClearAll()
        {
            options.ClearSimpleAxes();
            options.ClearMeshSelections();
            options.ClearArmatureSelections();
            options.ClearMeshEnabledOverrides();
        }

        internal void RestoreAutomaticSelection()
        {
            ClearAll();
            var automaticPairs = comparison.Meshes
                .SelectMany(mesh => mesh.Bones)
                .Where(ShouldAutomaticallySelectPair)
                .ToArray();
            var automaticMeshKeys = new HashSet<string>(
                automaticPairs.Select(pair => MeshFeatureExtractionSession.BuildMeshKey(pair.MeshPath)),
                StringComparer.Ordinal);
            var automaticBonePaths = new HashSet<string>(
                automaticPairs.Select(pair => pair.BonePath),
                StringComparer.Ordinal);

            foreach (var mesh in comparison.Meshes.Where(mesh =>
                         automaticMeshKeys.Contains(MeshFeatureExtractionSession.BuildMeshKey(mesh.MeshPath))))
            {
                options.SetSimpleMeshSelected(mesh.MeshPath, true);
            }

            foreach (string bonePath in automaticBonePaths)
            {
                options.SetSimpleBoneSelected(bonePath, true);
            }

            foreach (var pair in automaticPairs)
            {
                options.SetWeightClusterSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    ShouldAutomaticallySelectWeight(pair));
                options.SetBindposeSelected(
                    pair.MeshPath,
                    pair.BonePath,
                    ShouldAutomaticallySelectBindpose(pair));
            }

            foreach (var bone in comparison.Bones.Where(bone =>
                         bone.SourceBoneExists &&
                         bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
            {
                options.SetSimpleBoneSelected(bone.BonePath, true);
                options.SetBoneTransformSelected(bone.BonePath, true);
            }

            if (Mode == SkinWeightSelectionMode.Advanced)
            {
                foreach (var mesh in comparison.Meshes)
                {
                    options.SetMeshEnabled(mesh.MeshPath, HasSelectedMeshData(mesh));
                }
            }

            RebuildArmatureDependencies();
        }

        internal void DisableChanged()
        {
            if (Mode == SkinWeightSelectionMode.Simple)
            {
                foreach (var bone in comparison.RelevantBones.Where(bone => bone.Meshes.Any(pair =>
                             pair.Weight?.Status == SkinWeightComparisonStatus.Changed ||
                             pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)))
                {
                    options.SetSimpleBoneSelected(bone.BonePath, false);
                }

                ApplySimpleCartesianSelection();
            }
            else
            {
                foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
                {
                    if (pair.Weight?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, false);
                    }

                    if (pair.Bindpose?.Status == SkinWeightComparisonStatus.Changed)
                    {
                        options.SetBindposeSelected(pair.MeshPath, pair.BonePath, false);
                    }
                }

                foreach (var bone in comparison.Bones.Where(bone =>
                             bone.Transform?.Status == SkinWeightComparisonStatus.Changed))
                {
                    options.SetBoneTransformSelected(bone.BonePath, false);
                }
            }

            RebuildArmatureDependencies();
        }

        internal bool IsRequiredParent(string bonePath)
        {
            string normalized = MeshNodePath.Normalize(bonePath);
            foreach (string selected in options.GetSelectedNewBonePaths())
            {
                if (selected == normalized)
                {
                    continue;
                }

                string parent = MeshNodePath.ParentPath(selected);
                while (parent != MeshNodePath.Root)
                {
                    if (parent == normalized &&
                        comparison.TryGetBone(parent, out var parentBone) &&
                        parentBone.RequiresCreateBone)
                    {
                        return true;
                    }

                    parent = MeshNodePath.ParentPath(parent);
                }
            }

            return false;
        }

        private void ApplySimpleCartesianSelection()
        {
            options.ClearMeshSelections();
            options.ClearExplicitBoneTransformSelections();
            options.ClearAutomaticArmatureSelections();
            options.ClearMeshEnabledOverrides();
            var selectedMeshKeys = new HashSet<string>(
                options.GetSelectedSimpleMeshKeys(),
                StringComparer.Ordinal);
            var selectedBones = new HashSet<string>(
                options.GetSelectedSimpleBonePaths(),
                StringComparer.Ordinal);
            foreach (string bonePath in selectedBones)
            {
                if (comparison.TryGetBone(bonePath, out var bone) && bone.SourceBoneExists)
                {
                    options.SetBoneTransformSelected(bonePath, true);
                }
            }

            foreach (var mesh in comparison.Meshes)
            {
                if (!selectedMeshKeys.Contains(MeshFeatureExtractionSession.BuildMeshKey(mesh.MeshPath)))
                {
                    continue;
                }

                foreach (var pair in mesh.Bones.Where(pair => selectedBones.Contains(pair.BonePath)))
                {
                    if (pair.Weight != null)
                    {
                        options.SetWeightClusterSelected(pair.MeshPath, pair.BonePath, true);
                    }

                    if (ShouldAutomaticallySelectBindpose(pair))
                    {
                        options.SetBindposeSelected(pair.MeshPath, pair.BonePath, true);
                    }

                }
            }

            RebuildArmatureDependencies();
        }

        private void AddAutomaticArmatureDependencies(string bonePath, bool forceTransform)
        {
            if (!comparison.TryGetBone(bonePath, out var bone))
            {
                return;
            }

            if (forceTransform && bone.SourceBoneExists)
            {
                options.SetBoneTransformForced(bone.BonePath, true);
            }

            if (bone.RequiresCreateBone)
            {
                options.SetNewBoneSelected(bone.BonePath, true);
                options.EnsureRequiredParentSelections(comparison);
            }
        }

        private void RebuildArmatureDependencies()
        {
            options.ClearAutomaticArmatureSelections();
            foreach (string bonePath in options.GetExplicitBoneTransformPaths())
            {
                AddAutomaticArmatureDependencies(bonePath, false);
            }

            foreach (var pair in comparison.Meshes.SelectMany(mesh => mesh.Bones))
            {
                if (!options.IsMeshEnabled(pair.MeshPath))
                {
                    continue;
                }

                if (options.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) ||
                    options.IsBindposeSelected(pair.MeshPath, pair.BonePath))
                {
                    AddAutomaticArmatureDependencies(pair.BonePath, true);
                }
            }

            if (!options.HasMeshScopedSelections() &&
                options.GetSelectedBoneTransformPaths().Count > 0 &&
                (comparison.Meshes.FirstOrDefault(mesh => options.IsMeshEnabled(mesh.MeshPath)) ??
                 comparison.Meshes.FirstOrDefault()) is { } hostMesh)
            {
                options.SetArmatureOnlyHostMesh(hostMesh.MeshPath);
            }
        }

        private static bool IsNewOrChanged(SkinWeightComparisonStatus? status)
        {
            return status == SkinWeightComparisonStatus.New ||
                   status == SkinWeightComparisonStatus.Changed;
        }

        private static bool ShouldAutomaticallySelectWeight(SkinWeightMeshBoneComparison pair)
        {
            return pair?.Weight != null && IsNewOrChanged(pair.Weight.Status);
        }

        private static bool ShouldAutomaticallySelectBindpose(SkinWeightMeshBoneComparison pair)
        {
            return pair?.Bindpose?.SourceExists == true && IsNewOrChanged(pair.Bindpose.Status);
        }

        private static bool ShouldAutomaticallySelectPair(SkinWeightMeshBoneComparison pair)
        {
            return ShouldAutomaticallySelectWeight(pair) || ShouldAutomaticallySelectBindpose(pair);
        }

        private bool HasSelectedMeshData(SkinWeightMeshComparison mesh)
        {
            return mesh != null && mesh.Bones.Any(pair =>
                options.IsWeightClusterSelected(pair.MeshPath, pair.BonePath) ||
                options.IsBindposeSelected(pair.MeshPath, pair.BonePath));
        }
    }

    public sealed class SkinWeightFeatureExtractor
        : MeshFeatureExtractor<SkinWeightFeatureObject, SkinWeightExtractionOptions>
    {
        public static readonly SkinWeightFeatureExtractor Instance = new();

        public override string FeatureId => SkinWeightFeatureObject.Id;

        protected override MeshFeatureExtractionResult TryExtract(
            MeshFeatureExtractionContext context,
            SkinWeightExtractionOptions options,
            out SkinWeightFeatureObject feature)
        {
            feature = null;
            if (!options.ShouldExtractMesh(new MeshFeatureExtractionMeshRequest(context.Path)))
            {
                return MeshFeatureExtractionResult.Skipped("No skin weight work was selected for this mesh.");
            }

            var sourceMesh = context.GetSourceFbxMesh();
            var sourceSkin = SkinWeightExtractionUtility.GetPrimarySourceSkinDeformer(context);
            if (sourceMesh == null)
            {
                return MeshFeatureExtractionResult.Skipped("No source mesh was found.");
            }

            var originSkin = SkinWeightExtractionUtility.GetPrimaryOriginSkinDeformer(context);
            var sourceBonesByIndex = SkinWeightFbxComparison.BuildBonePathsByIndex(sourceSkin);
            var originBonesByIndex = SkinWeightFbxComparison.BuildBonePathsByIndex(originSkin);
            var sourceWeights = SkinWeightFbxComparison.BuildWeightsByPath(sourceSkin, sourceBonesByIndex);
            var originWeights = SkinWeightFbxComparison.BuildWeightsByPath(originSkin, originBonesByIndex);
            int controlPointCount = context.GetOriginFbxMesh()?.ControlPointCount ?? 0;
            var selection = options.ResolveSelection();
            var selectedWeightBonePaths = selection.GetWeightBonePaths(context.Path);
            var deltaWeights = BuildSelectedDeltaWeights(
                sourceWeights,
                originWeights,
                selectedWeightBonePaths,
                controlPointCount);

            var bindPoses = ExtractSelectedBindPoses(
                sourceSkin,
                selection.GetBindposeBonePaths(context.Path),
                sourceBonesByIndex);
            var armature = options.GetOrCreateSharedArmature(
                context.Session,
                () => BuildSelectedArmature(
                    context.Session,
                    selection.TransformBonePaths,
                    selection.NewBonePaths));

            var clusters = BuildClusters(deltaWeights, bindPoses);

            feature = ScriptableObject.CreateInstance<SkinWeightFeatureObject>();
            feature.SetSkinning(
                armature,
                GetRootBonePath(context),
                clusters);
            return MeshFeatureExtractionResult.Success();
        }

        private static Dictionary<int, Dictionary<string, double>> BuildSelectedDeltaWeights(
            IReadOnlyDictionary<string, Dictionary<int, double>> sourceWeights,
            IReadOnlyDictionary<string, Dictionary<int, double>> originWeights,
            IEnumerable<string> selectedBonePaths,
            int controlPointCount)
        {
            var result = new Dictionary<int, Dictionary<string, double>>();
            foreach (string bonePath in (selectedBonePaths ?? Array.Empty<string>())
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                sourceWeights.TryGetValue(bonePath, out var sourceByControlPoint);
                originWeights.TryGetValue(bonePath, out var originByControlPoint);
                foreach (int controlPointIndex in (sourceByControlPoint?.Keys ?? Enumerable.Empty<int>())
                             .Concat(originByControlPoint?.Keys ?? Enumerable.Empty<int>())
                             .Where(index => index >= 0 && index < controlPointCount)
                             .Distinct())
                {
                    double source = sourceByControlPoint != null &&
                                   sourceByControlPoint.TryGetValue(controlPointIndex, out double sourceWeight)
                        ? sourceWeight
                        : 0d;
                    double origin = originByControlPoint != null &&
                                   originByControlPoint.TryGetValue(controlPointIndex, out double originWeight)
                        ? originWeight
                        : 0d;
                    double delta = source - origin;
                    if (!result.TryGetValue(controlPointIndex, out var deltasByPath))
                    {
                        deltasByPath = new Dictionary<string, double>();
                        result[controlPointIndex] = deltasByPath;
                    }

                    deltasByPath[bonePath] = delta;
                }
            }

            return result;
        }

        private static List<SkinWeightClusterData> ExtractSelectedBindPoses(
            IFbxSkinDeformer sourceSkin,
            IEnumerable<string> selectedBonePaths,
            IReadOnlyDictionary<int, string> sourceBonesByIndex)
        {
            var result = new List<SkinWeightClusterData>();
            var sourceBindPoses = SkinWeightFbxComparison.BuildBindPosesByPath(sourceSkin, sourceBonesByIndex);
            foreach (string bonePath in (selectedBonePaths ?? Array.Empty<string>())
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                if (sourceBindPoses.TryGetValue(bonePath, out var sourceBindPose))
                {
                    result.Add(sourceBindPose);
                }
            }

            return result;
        }

        private static List<SkinWeightClusterData> BuildClusters(
            IReadOnlyDictionary<int, Dictionary<string, double>> deltaWeights,
            IEnumerable<SkinWeightClusterData> bindPoses)
        {
            var weightsByPath = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
            foreach (var controlPointPair in deltaWeights ?? new Dictionary<int, Dictionary<string, double>>())
            {
                foreach (var weightPair in controlPointPair.Value ?? new Dictionary<string, double>())
                {
                    string path = MeshNodePath.Normalize(weightPair.Key);
                    if (path == MeshNodePath.Root)
                    {
                        continue;
                    }

                    if (!weightsByPath.TryGetValue(path, out var weightsByIndex))
                    {
                        weightsByIndex = new Dictionary<int, double>();
                        weightsByPath[path] = weightsByIndex;
                    }

                    weightsByIndex.TryGetValue(controlPointPair.Key, out double existing);
                    weightsByIndex[controlPointPair.Key] = existing + weightPair.Value;
                }
            }

            var clustersByPath = weightsByPath
                .Select(pair =>
                {
                    var entries = pair.Value
                        .OrderBy(weight => weight.Key)
                        .ToArray();
                    return new SkinWeightClusterData
                    {
                        m_BonePath = pair.Key,
                    }.WithWeights(
                        entries.Select(weight => weight.Key),
                        entries.Select(weight => weight.Value));
                })
                .Where(cluster => cluster.WeightCount > 0)
                .ToDictionary(cluster => cluster.BonePath, StringComparer.Ordinal);

            foreach (var bindPose in bindPoses ?? Array.Empty<SkinWeightClusterData>())
            {
                if (bindPose == null)
                {
                    continue;
                }

                string path = bindPose.BonePath;
                if (path == MeshNodePath.Root)
                {
                    continue;
                }

                if (!clustersByPath.TryGetValue(path, out var cluster))
                {
                    cluster = new SkinWeightClusterData { m_BonePath = path };
                    clustersByPath[path] = cluster;
                }

                if (bindPose.m_HasFbxClusterMatrices)
                {
                    cluster.SetFbxClusterMatrices(
                        bindPose.m_FbxTransformMatrix,
                        bindPose.m_FbxTransformLinkMatrix);
                }
            }

            return clustersByPath.Values
                .OrderBy(cluster => cluster.BonePath, StringComparer.Ordinal)
                .ToList();
        }

        private static FbxArmatureObject BuildSelectedArmature(
            MeshFeatureExtractionSession session,
            IEnumerable<string> selectedTransformBonePaths,
            IEnumerable<string> selectedNewBonePaths)
        {
            if (session == null)
            {
                return null;
            }

            var createPaths = new HashSet<string>(
                (selectedNewBonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Where(path => path != MeshNodePath.Root),
                StringComparer.Ordinal);
            var transformPaths = new HashSet<string>(
                (selectedTransformBonePaths ?? Array.Empty<string>())
                .Select(MeshNodePath.Normalize)
                .Where(path => path != MeshNodePath.Root),
                StringComparer.Ordinal);
            foreach (string path in createPaths)
            {
                transformPaths.Add(path);
            }

            if (transformPaths.Count == 0 && createPaths.Count == 0)
            {
                return null;
            }

            var armature = ScriptableObject.CreateInstance<FbxArmatureObject>();
            armature.name = "Armature";
            foreach (string bonePath in transformPaths.Concat(createPaths)
                         .Select(MeshNodePath.Normalize)
                         .Distinct(StringComparer.Ordinal))
            {
                AddArmaturePatch(armature, session, bonePath, createPaths.Contains(bonePath));
            }

            return armature.BoneCount > 0 ? armature : null;
        }

        private static void AddArmaturePatch(
            FbxArmatureObject armature,
            MeshFeatureExtractionSession session,
            string bonePath,
            bool createIfMissing)
        {
            string normalizedPath = MeshNodePath.Normalize(bonePath);
            if (armature == null ||
                session == null ||
                normalizedPath == MeshNodePath.Root ||
                armature.HasBone(normalizedPath))
            {
                return;
            }

            string parentPath = MeshNodePath.ParentPath(normalizedPath);
            if (createIfMissing && parentPath != MeshNodePath.Root && !session.OriginHasTransform(parentPath))
            {
                AddArmaturePatch(armature, session, parentPath, true);
            }

            armature.GetOrAddBone(CreateBoneNode(session, normalizedPath, createIfMissing));
        }

        private static FbxArmatureBoneData CreateBoneNode(
            MeshFeatureExtractionSession session,
            string path,
            bool createIfMissing)
        {
            var node = new FbxArmatureBoneData
            {
                m_Path = MeshNodePath.Normalize(path),
                m_CreateIfMissing = createIfMissing
            };

            var sourceNode = session?.GetSourceNode(path);
            if (sourceNode == null)
            {
                throw new InvalidOperationException(
                    $"Bone '{node.m_Path}' was not found in the source FBX. Re-open the extraction session and select a valid source model.");
            }

            var rawTransform = sourceNode.RawTransform;
            node.m_HasTransformData = true;
            node.m_LclTranslation = rawTransform.LclTranslation;
            node.m_LclRotation = rawTransform.LclRotation;
            node.m_LclScaling = rawTransform.LclScaling;
            node.m_RotationOrder = rawTransform.RotationOrder;
            node.m_InheritMode = rawTransform.OriginalInheritMode;
            node.m_RotationActive = rawTransform.RotationActive;
            node.m_PreRotation = rawTransform.PreRotation;
            node.m_PostRotation = rawTransform.PostRotation;
            node.m_RotationPivot = rawTransform.RotationPivot;
            node.m_ScalingPivot = rawTransform.ScalingPivot;
            node.m_RotationOffset = rawTransform.RotationOffset;
            node.m_ScalingOffset = rawTransform.ScalingOffset;
            node.m_EvaluatedNodeToParentMatrix = sourceNode.NodeToParentMatrix;
            return node;
        }

        private static string GetRootBonePath(MeshFeatureExtractionContext context)
        {
            var sourceRenderer = SkinWeightExtractionUtility.GetSourceRenderer(context);
            string rootBonePath = SkinWeightExtractionUtility.GetTransformPath(
                sourceRenderer != null ? sourceRenderer.rootBone : null,
                context.Session.SourceFbxGo.transform);
            return MeshNodePath.Normalize(rootBonePath);
        }
    }
}
