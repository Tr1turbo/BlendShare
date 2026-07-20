using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
    [DependsOnContext(typeof(AnimatorServicesContext))]
    internal sealed class BlendShareBoneMergePass : nadena.dev.ndmf.Pass<BlendShareBoneMergePass>
    {
        public override string QualifiedName => "com.triturbo.blendshare.merge-bones";
        public override string DisplayName => "Merge BlendShare Bones";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState(_ => new BoneMergeState());
            if (!state.Committed || state.Groups.Count == 0)
            {
                return;
            }

            var animatorServices = context.Extension<AnimatorServicesContext>();
            foreach (var group in state.Groups.Where(group =>
                         CanMergeGroup(context, animatorServices, group)))
            {
                MergeGroup(context, animatorServices, group);
            }
        }

        internal static bool TryPrepare(
            BuildContext context,
            IEnumerable<BlendShareMesh> meshAppliers,
            IEnumerable<BlendShareBoneProxy> availableProxies,
            Transform pathRoot,
            AnimatorServicesContext animatorServices,
            out IReadOnlyList<ResolvedBinding> resolvedBindings,
            out string diagnostic,
            out BlendShareBoneProxy conflictingProxy)
        {
            var state = context.GetState(_ => new BoneMergeState());
            state.Reset();
            if (!TryBuildGroups(
                    meshAppliers,
                    availableProxies,
                    pathRoot,
                    binding => IsAnimated(binding.FinalTransform, animatorServices),
                    out resolvedBindings,
                    out var groups,
                    out var renameGroups,
                    out diagnostic,
                    out conflictingProxy))
            {
                return false;
            }

            AssignUniqueBuildNames(
                resolvedBindings,
                renameGroups,
                pathRoot,
                animatorServices.ObjectPathRemapper);
            state.Groups.AddRange(groups);
            return true;
        }

        internal static bool TryPreparePreview(
            IEnumerable<BlendShareMesh> meshAppliers,
            IEnumerable<BlendShareBoneProxy> availableProxies,
            Transform pathRoot,
            out IReadOnlyList<BlendShareBoneProxy> resolvedProxies,
            out IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            out IReadOnlyList<GameObject> temporaryObjects,
            out string diagnostic,
            out BlendShareBoneProxy conflictingProxy)
        {
            temporaryObjects = Array.Empty<GameObject>();
            if (!TryBuildGroups(
                    meshAppliers,
                    availableProxies,
                    pathRoot,
                    _ => false,
                    out var originalBindings,
                    out _,
                    out var renameGroups,
                    out diagnostic,
                    out conflictingProxy))
            {
                resolvedProxies = originalBindings.Select(binding => binding.Component).Distinct().ToArray();
                boneTransformOverrides = new Dictionary<string, Transform>();
                return false;
            }

            CreatePreviewProxies(
                originalBindings,
                renameGroups,
                pathRoot,
                out resolvedProxies,
                out boneTransformOverrides,
                out temporaryObjects);
            return true;
        }

        internal static void Commit(BuildContext context)
        {
            context.GetState(_ => new BoneMergeState()).Committed = true;
        }

        internal static IReadOnlyDictionary<string, Transform> BuildBoneTransformOverrides(
            IEnumerable<ResolvedBinding> bindings,
            Transform pathRoot)
        {
            return (bindings ?? Array.Empty<ResolvedBinding>())
                .Where(binding => binding.Component != null && binding.FinalTransform != null)
                .GroupBy(binding => GetFinalProxyPath(binding, pathRoot))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(GetBindingOrder, StringComparer.Ordinal)
                        .Last()
                        .FinalTransform);
        }

        internal static void ReleaseTemporaryPreviewObjects(IEnumerable<GameObject> temporaryObjects)
        {
            foreach (var temporaryObject in temporaryObjects ?? Array.Empty<GameObject>())
            {
                if (temporaryObject != null)
                {
                    Object.DestroyImmediate(temporaryObject);
                }
            }
        }

        private static bool TryBuildGroups(
            IEnumerable<BlendShareMesh> meshAppliers,
            IEnumerable<BlendShareBoneProxy> availableProxies,
            Transform pathRoot,
            Func<ResolvedBinding, bool> isAnimated,
            out IReadOnlyList<ResolvedBinding> resolvedBindings,
            out List<BoneMergeGroup> mergeGroups,
            out List<ResolvedBinding[]> renameGroups,
            out string diagnostic,
            out BlendShareBoneProxy conflictingProxy)
        {
            var appliers = (meshAppliers ?? Array.Empty<BlendShareMesh>())
                .Where(applier => applier != null && applier.isActiveAndEnabled)
                .Distinct()
                .ToArray();
            BlendShareMesh.RefreshBoneProxyCaches(appliers, pathRoot, availableProxies);
            var selected = new List<ResolvedBinding>();
            diagnostic = null;
            conflictingProxy = null;

            foreach (var source in GetRequiredBoneProxySources(appliers, pathRoot))
            {
                if (!source.Applier.TryGetCachedBoneProxy(source.Path, out var proxy) ||
                    !proxy.TryGetBinding(source.Path, out var sourceBinding))
                {
                    diagnostic = $"No BlendShare bone proxy represents source bone '{source.Path}'.";
                    resolvedBindings = selected;
                    mergeGroups = new List<BoneMergeGroup>();
                    renameGroups = new List<ResolvedBinding[]>();
                    return false;
                }

                var binding = new ResolvedBinding(
                    proxy,
                    sourceBinding);

                if (!ValidateBinding(binding, pathRoot, out diagnostic))
                {
                    conflictingProxy = binding.Component;
                    resolvedBindings = selected;
                    mergeGroups = new List<BoneMergeGroup>();
                    renameGroups = new List<ResolvedBinding[]>();
                    return false;
                }

                selected.Add(binding);
            }

            var groups = new List<BoneMergeGroup>();
            var groupsToRename = new List<ResolvedBinding[]>();
            foreach (var pathGroup in selected
                         .Distinct()
                         .GroupBy(binding => GetFinalProxyPath(binding, pathRoot)))
            {
                var ordered = pathGroup
                    .OrderBy(GetBindingOrder, StringComparer.Ordinal)
                    .ToArray();
                if (ordered.Length == 1)
                {
                    continue;
                }

                var partitions = BuildMergePartitions(
                    ordered,
                    selected,
                    pathRoot,
                    isAnimated);
                foreach (var partition in partitions.Where(partition => partition.Length > 1))
                {
                    var partitionCanonical = partition[partition.Length - 1];
                    groups.Add(new BoneMergeGroup(
                        partitionCanonical.FinalTransform,
                        partition.Take(partition.Length - 1).Select(binding => binding.FinalTransform)));
                }

                if (partitions.Count > 1)
                {
                    // Once one collider must remain separate, every resulting bone receives a
                    // build-only name. Proxies in the same merge partition share that name.
                    groupsToRename.AddRange(partitions);
                }
            }

            resolvedBindings = selected;
            mergeGroups = groups;
            renameGroups = groupsToRename;
            return true;
        }

        private static List<ResolvedBinding[]> BuildMergePartitions(
            IReadOnlyList<ResolvedBinding> ordered,
            IEnumerable<ResolvedBinding> selectedBindings,
            Transform pathRoot,
            Func<ResolvedBinding, bool> isAnimated)
        {
            var remaining = ordered.ToList();
            var partitions = new List<ResolvedBinding[]>();
            var avatarRoot = pathRoot != null ? pathRoot.gameObject : null;
            while (remaining.Count > 0)
            {
                var canonical = remaining[remaining.Count - 1];
                remaining.RemoveAt(remaining.Count - 1);
                var partition = new List<ResolvedBinding> { canonical };
                bool canonicalCanMerge = IsCleanProxyHost(
                                             canonical,
                                             selectedBindings,
                                             avatarRoot,
                                             false) &&
                                         !isAnimated(canonical);
                if (canonicalCanMerge)
                {
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        var candidate = remaining[i];
                        if (!AreEquivalentBoneProxies(canonical, candidate, pathRoot) ||
                            !IsCleanProxyHost(candidate, selectedBindings, avatarRoot, true) ||
                            isAnimated(candidate))
                        {
                            continue;
                        }

                        partition.Add(candidate);
                        remaining.RemoveAt(i);
                    }
                }

                partitions.Add(partition
                    .OrderBy(GetBindingOrder, StringComparer.Ordinal)
                    .ToArray());
            }

            return partitions;
        }

        private static void AssignUniqueBuildNames(
            IEnumerable<ResolvedBinding> allBindings,
            IEnumerable<ResolvedBinding[]> renameGroups,
            Transform pathRoot,
            ObjectPathRemapper pathRemapper)
        {
            var usedPaths = CollectUsedPaths(pathRoot, allBindings);
            bool renamed = false;
            foreach (var group in renameGroups ?? Array.Empty<ResolvedBinding[]>())
            {
                string uniqueName = CreateUniqueBoneName(group[group.Length - 1], pathRoot, usedPaths);
                foreach (var binding in group)
                {
                    // Preserve the original animation identity before assigning the build-only name.
                    pathRemapper?.RecordObjectTree(binding.FinalTransform);
                    binding.FinalTransform.gameObject.name = uniqueName;
                    renamed = true;
                }
            }

            if (renamed)
            {
                pathRemapper?.ClearCache();
            }
        }

        private static void CreatePreviewProxies(
            IReadOnlyList<ResolvedBinding> originalBindings,
            IEnumerable<ResolvedBinding[]> renameGroups,
            Transform pathRoot,
            out IReadOnlyList<BlendShareBoneProxy> generationProxies,
            out IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            out IReadOnlyList<GameObject> temporaryObjects)
        {
            var proxies = new List<BlendShareBoneProxy>();
            var targets = new Dictionary<ResolvedBinding, Transform>();
            var temporary = new List<GameObject>();
            var usedPaths = CollectUsedPaths(pathRoot, originalBindings);
            var renameByBinding = new Dictionary<ResolvedBinding, string>();
            foreach (var group in renameGroups ?? Array.Empty<ResolvedBinding[]>())
            {
                string uniqueName = CreateUniqueBoneName(group[group.Length - 1], pathRoot, usedPaths);
                foreach (var original in group)
                {
                    renameByBinding[original] = uniqueName;
                }
            }

            foreach (var original in originalBindings ?? Array.Empty<ResolvedBinding>())
            {
                string finalName = renameByBinding.TryGetValue(original, out var uniqueName)
                    ? uniqueName
                    : original.FinalTransform.name;
                var previewProxy = CreatePreviewProxy(original, finalName);
                proxies.Add(previewProxy.Component);
                targets[previewProxy] = original.FinalTransform;
                temporary.Add(previewProxy.Component.gameObject);
            }

            var generationBindings = targets.Keys.ToArray();
            var overrides = generationBindings
                .GroupBy(binding => GetFinalProxyPath(binding, pathRoot))
                .ToDictionary(
                    group => group.Key,
                    group => targets[group
                        .OrderBy(binding => GetHierarchyOrder(targets[binding]), StringComparer.Ordinal)
                        .Last()]);
            generationProxies = proxies;
            boneTransformOverrides = overrides;
            temporaryObjects = temporary;
        }

        private static HashSet<string> CollectUsedPaths(
            Transform pathRoot,
            IEnumerable<ResolvedBinding> bindings)
        {
            var usedPaths = new HashSet<string>((bindings ?? Array.Empty<ResolvedBinding>())
                .Where(binding => binding.Component != null && binding.FinalTransform != null)
                .Select(binding => GetFinalProxyPath(binding, pathRoot)));
            if (pathRoot != null)
            {
                usedPaths.UnionWith(pathRoot.GetComponentsInChildren<Transform>(true)
                    .Where(transform => transform != pathRoot)
                    .Select(transform => MeshNodePath.Normalize(
                        MeshNodePath.GetRelativePath(transform, pathRoot))));
            }

            return usedPaths;
        }

        private static ResolvedBinding CreatePreviewProxy(
            ResolvedBinding source,
            string uniqueName)
        {
            var gameObject = new GameObject(uniqueName)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var proxy = gameObject.AddComponent<BlendShareBoneProxy>();
            proxy.SourceArmature = source.Component.SourceArmature;
            proxy.TargetParent = source.EffectiveParent;
            proxy.UseHierarchyParent = false;
            proxy.RecalculateBindpose = source.Component.RecalculateBindpose;
            proxy.transform.position = source.FinalTransform.position;
            proxy.transform.rotation = source.FinalTransform.rotation;
            proxy.transform.localScale = source.FinalTransform.lossyScale;
            var binding = new BlendShareBoneProxyBinding(source.SourceBonePath, proxy.transform);
            proxy.SetBindings(new[] { binding });
            return new ResolvedBinding(proxy, binding);
        }

        private static string CreateUniqueBoneName(
            ResolvedBinding binding,
            Transform pathRoot,
            ISet<string> usedPaths)
        {
            string baseName = binding.FinalTransform != null ? binding.FinalTransform.name : "BlendShareBone";
            while (true)
            {
                string candidate = $"{baseName} BlendShare {Guid.NewGuid():N}";
                string candidatePath = GetFinalProxyPath(binding, pathRoot, candidate);
                if (usedPaths.Add(candidatePath))
                {
                    return candidate;
                }
            }
        }

        private static IEnumerable<(BlendShareMesh Applier, FbxArmatureObject Armature, string Path)> GetRequiredBoneProxySources(
            IEnumerable<BlendShareMesh> meshAppliers,
            Transform pathRoot)
        {
            var requirements = new List<(
                SourceKey Key,
                BlendShareMesh Applier,
                FbxArmatureObject Armature,
                string Path)>();
            foreach (var applier in meshAppliers ?? Array.Empty<BlendShareMesh>())
            {
                var renderer = applier?.TargetRenderer;
                var skin = applier?.MeshData?.GetFeature<SkinWeightFeatureObject>();
                if (renderer == null || skin?.Armature == null)
                {
                    continue;
                }

                var existingBonePaths = new HashSet<string>((renderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null)
                    .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, pathRoot))));
                foreach (string path in skin.GetNeededBonePathsInArmatureOrder())
                {
                    var bone = skin.Armature.GetBone(path);
                    if (!existingBonePaths.Contains(path) && bone != null && bone.m_CreateIfMissing)
                    {
                        requirements.Add((
                            new SourceKey(skin.Armature, path),
                            applier,
                            skin.Armature,
                            MeshNodePath.Normalize(path)));
                    }
                }
            }

            return requirements
                .GroupBy(requirement => requirement.Key)
                .Select(group =>
                {
                    var first = group.First();
                    return (first.Applier, first.Armature, first.Path);
                });
        }

        private static bool CanMergeGroup(
            BuildContext context,
            AnimatorServicesContext animatorServices,
            BoneMergeGroup group)
        {
            var canonical = group.Canonical;
            if (canonical == null)
            {
                return false;
            }

            foreach (var redundant in group.Redundant)
            {
                if (redundant == null)
                {
                    continue;
                }

                if (!AreEquivalentTransforms(canonical, redundant))
                {
                    return false;
                }

                if (!IsCleanGeneratedBone(canonical) || !IsCleanGeneratedBone(redundant))
                {
                    return false;
                }

                if (IsAnimated(canonical, animatorServices) || IsAnimated(redundant, animatorServices))
                {
                    return false;
                }

                if (HasExternalReference(context.AvatarRootObject, redundant))
                {
                    return false;
                }
            }

            return true;
        }

        private static void MergeGroup(
            BuildContext context,
            AnimatorServicesContext animatorServices,
            BoneMergeGroup group)
        {
            foreach (var redundant in group.Redundant.Where(transform => transform != null))
            {
                ReplaceRendererReferences(context.AvatarRootObject, redundant, group.Canonical);
                animatorServices.ObjectPathRemapper.ReplaceObject(redundant, group.Canonical);
                Object.DestroyImmediate(redundant.gameObject);
            }
        }

        private static bool IsCleanProxyHost(
            ResolvedBinding binding,
            IEnumerable<ResolvedBinding> selectedBindings,
            GameObject avatarRoot,
            bool rejectExternalReferences)
        {
            var finalTransform = binding.FinalTransform;
            if (binding.Component == null || finalTransform == null || finalTransform.childCount != 0 ||
                (selectedBindings ?? Array.Empty<ResolvedBinding>()).Any(candidate =>
                    candidate.FinalTransform != null &&
                    candidate.FinalTransform != finalTransform &&
                    candidate.EffectiveParent == finalTransform))
            {
                return false;
            }

            if (finalTransform.gameObject.GetComponents<Component>().Any(component =>
                    !(component is Transform) && component != binding.Component))
            {
                return false;
            }

            if (rejectExternalReferences && HasExternalReference(avatarRoot, finalTransform))
            {
                return false;
            }

            return true;
        }

        private static bool IsCleanGeneratedBone(Transform bone)
        {
            if (bone.childCount != 0)
            {
                return false;
            }

            if (bone.gameObject.GetComponents<Component>().Any(component => !(component is Transform)))
            {
                return false;
            }

            return true;
        }

        private static bool HasExternalReference(
            GameObject avatarRoot,
            Transform target)
        {
            if (avatarRoot == null || target == null)
            {
                return false;
            }

            foreach (var component in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is SkinnedMeshRenderer ||
                    component is BlendShareBoneProxy)
                {
                    continue;
                }

                var serializedObject = new SerializedObject(component);
                var property = serializedObject.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = property.propertyType != SerializedPropertyType.String;
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    if (property.objectReferenceValue == target ||
                        property.objectReferenceValue == target.gameObject)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsAnimated(Transform transform, AnimatorServicesContext animatorServices)
        {
            if (transform == null || animatorServices == null)
            {
                return false;
            }

            var remapper = animatorServices.ObjectPathRemapper;
            remapper.GetVirtualPathForObject(transform);
            return remapper.GetAllPathsForObject(transform)
                .Any(path => animatorServices.AnimationIndex.GetClipsForObjectPath(path).Any());
        }

        private static void ReplaceRendererReferences(GameObject avatarRoot, Transform oldBone, Transform newBone)
        {
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = renderer.bones ?? Array.Empty<Transform>();
                bool changed = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == oldBone)
                    {
                        bones[i] = newBone;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.bones = bones;
                }

                if (renderer.rootBone == oldBone)
                {
                    renderer.rootBone = newBone;
                }
            }
        }

        private static string GetFinalProxyPath(ResolvedBinding binding, Transform pathRoot)
        {
            return GetFinalProxyPath(
                binding,
                pathRoot,
                binding.FinalTransform != null ? binding.FinalTransform.name : string.Empty);
        }

        private static string GetFinalProxyPath(
            ResolvedBinding binding,
            Transform pathRoot,
            string boneName)
        {
            var parent = ResolveEffectiveProxyParent(binding, pathRoot);
            string parentPath = parent != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(parent, pathRoot))
                : MeshNodePath.Root;
            return parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(boneName)
                : MeshNodePath.Normalize($"{parentPath}/{boneName}");
        }

        private static bool AreEquivalentBoneProxies(
            ResolvedBinding first,
            ResolvedBinding second,
            Transform pathRoot)
        {
            if (first.Component == null || second.Component == null ||
                ResolveEffectiveProxyParent(first, pathRoot) != ResolveEffectiveProxyParent(second, pathRoot) ||
                first.FinalTransform == null || second.FinalTransform == null ||
                first.FinalTransform.name != second.FinalTransform.name ||
                first.Component.RecalculateBindpose != second.Component.RecalculateBindpose)
            {
                return false;
            }

            GetEffectiveProxyTransform(first, out var firstPosition, out var firstRotation, out var firstScale);
            GetEffectiveProxyTransform(second, out var secondPosition, out var secondRotation, out var secondScale);
            return AreEquivalentTransforms(
                firstPosition,
                Quaternion.Euler(firstRotation),
                firstScale,
                secondPosition,
                Quaternion.Euler(secondRotation),
                secondScale);
        }

        private static bool AreEquivalentTransforms(Transform first, Transform second)
        {
            return first != null && second != null &&
                   first.parent == second.parent &&
                   AreEquivalentTransforms(
                       first.localPosition,
                       first.localRotation,
                       first.localScale,
                       second.localPosition,
                       second.localRotation,
                       second.localScale);
        }

        private static bool AreEquivalentTransforms(
            Vector3 firstPosition,
            Quaternion firstRotation,
            Vector3 firstScale,
            Vector3 secondPosition,
            Quaternion secondRotation,
            Vector3 secondScale)
        {
            return Vector3.SqrMagnitude(firstPosition - secondPosition) <= 0.00000001f &&
                   Quaternion.Angle(firstRotation, secondRotation) <= 0.01f &&
                   Vector3.SqrMagnitude(firstScale - secondScale) <= 0.00000001f;
        }

        private static Transform ResolveEffectiveProxyParent(
            ResolvedBinding binding,
            Transform pathRoot)
        {
            var parent = binding.EffectiveParent;
            if (pathRoot != null &&
                (parent == null || (parent != pathRoot && !parent.IsChildOf(pathRoot))))
            {
                return pathRoot;
            }

            return parent;
        }

        private static void GetEffectiveProxyTransform(
            ResolvedBinding binding,
            out Vector3 position,
            out Vector3 rotation,
            out Vector3 scale)
        {
            if (binding.Component != null &&
                binding.Component.TryGetCurrentLocalTransform(binding.Binding, out position, out rotation, out scale))
            {
                return;
            }

            position = binding.Component != null && !binding.Component.HasExplicitBindings
                ? binding.Component.LocalPosition
                : Vector3.zero;
            rotation = binding.Component != null && !binding.Component.HasExplicitBindings
                ? binding.Component.LocalEulerRotation
                : Vector3.zero;
            scale = binding.Component != null && !binding.Component.HasExplicitBindings
                ? binding.Component.LocalScale
                : Vector3.one;
        }

        private static bool ValidateBinding(
            ResolvedBinding binding,
            Transform avatarRoot,
            out string diagnostic)
        {
            var component = binding.Component;
            var finalTransform = binding.FinalTransform;
            var parent = binding.EffectiveParent;
            if (component == null || binding.Binding == null || finalTransform == null)
            {
                diagnostic = "BlendShare bone proxy binding has no final transform.";
                return false;
            }

            if (avatarRoot == null ||
                (finalTransform != avatarRoot && !finalTransform.IsChildOf(avatarRoot)))
            {
                diagnostic = $"BlendShare bone proxy binding '{binding.SourceBonePath}' has a final transform outside the avatar.";
                return false;
            }

            if (parent == null)
            {
                diagnostic = $"BlendShare bone proxy binding '{binding.SourceBonePath}' has no target parent.";
                return false;
            }

            if (parent != avatarRoot && !parent.IsChildOf(avatarRoot))
            {
                diagnostic = $"BlendShare bone proxy binding '{binding.SourceBonePath}' targets a parent outside the avatar.";
                return false;
            }

            if (parent == finalTransform || parent.IsChildOf(finalTransform))
            {
                diagnostic = $"BlendShare bone proxy binding '{binding.SourceBonePath}' creates a self or descendant parent cycle.";
                return false;
            }

            diagnostic = null;
            return true;
        }

        private static string GetHierarchyOrder(Transform transform)
        {
            var indices = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
            }

            return string.Join("/", indices);
        }

        private static string GetBindingOrder(ResolvedBinding binding)
        {
            int bindingIndex = -1;
            if (binding.Component != null)
            {
                for (int i = 0; i < binding.Component.Bindings.Count; i++)
                {
                    if (ReferenceEquals(binding.Component.Bindings[i], binding.Binding))
                    {
                        bindingIndex = i;
                        break;
                    }
                }
            }

            return $"{GetHierarchyOrder(binding.Component != null ? binding.Component.transform : null)}/{bindingIndex:D8}";
        }

        internal readonly struct ResolvedBinding
        {
            public ResolvedBinding(BlendShareBoneProxy component, BlendShareBoneProxyBinding binding)
            {
                Component = component;
                Binding = binding;
            }

            public BlendShareBoneProxy Component { get; }
            public BlendShareBoneProxyBinding Binding { get; }
            public Transform FinalTransform => Binding?.Transform;
            public Transform EffectiveParent => Component?.GetEffectiveParent(Binding);
            public string SourceBonePath => Binding?.SourceBonePath;
        }

        private readonly struct SourceKey : IEquatable<SourceKey>
        {
            private readonly FbxArmatureObject armature;
            private readonly string sourceBonePath;

            public SourceKey(FbxArmatureObject armature, string sourceBonePath)
            {
                this.armature = armature;
                this.sourceBonePath = MeshNodePath.NormalizeOptional(sourceBonePath);
            }

            public bool Equals(SourceKey other)
            {
                return ReferenceEquals(armature, other.armature) && sourceBonePath == other.sourceBonePath;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((armature != null ? armature.GetInstanceID() : 0) * 397) ^
                           (sourceBonePath != null ? sourceBonePath.GetHashCode() : 0);
                }
            }
        }

        private sealed class BoneMergeState
        {
            public readonly List<BoneMergeGroup> Groups = new();
            public bool Committed;

            public void Reset()
            {
                Groups.Clear();
                Committed = false;
            }
        }

        private sealed class BoneMergeGroup
        {
            public Transform Canonical { get; }
            public Transform[] Redundant { get; }

            public BoneMergeGroup(Transform canonical, IEnumerable<Transform> redundant)
            {
                Canonical = canonical;
                Redundant = redundant?.Where(transform => transform != null).ToArray() ?? Array.Empty<Transform>();
            }
        }

    }
}
