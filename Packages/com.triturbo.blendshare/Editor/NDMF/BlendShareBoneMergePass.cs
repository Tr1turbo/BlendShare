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
            out IReadOnlyList<BlendShareBoneProxy> resolvedProxies,
            out string diagnostic,
            out BlendShareBoneProxy conflictingProxy)
        {
            var state = context.GetState(_ => new BoneMergeState());
            state.Reset();
            if (!TryBuildGroups(
                    meshAppliers,
                    availableProxies,
                    pathRoot,
                    proxy => IsAnimated(proxy.transform, animatorServices),
                    out resolvedProxies,
                    out var groups,
                    out var renameGroups,
                    out diagnostic,
                    out conflictingProxy))
            {
                return false;
            }

            AssignUniqueBuildNames(
                resolvedProxies,
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
                    out var originalProxies,
                    out _,
                    out var renameGroups,
                    out diagnostic,
                    out conflictingProxy))
            {
                resolvedProxies = originalProxies;
                boneTransformOverrides = new Dictionary<string, Transform>();
                return false;
            }

            CreatePreviewProxies(
                originalProxies,
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
            IEnumerable<BlendShareBoneProxy> proxies,
            Transform pathRoot)
        {
            return (proxies ?? Array.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null)
                .Distinct()
                .GroupBy(proxy => GetFinalProxyPath(proxy, pathRoot))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
                        .Last()
                        .transform);
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
            Func<BlendShareBoneProxy, bool> isAnimated,
            out IReadOnlyList<BlendShareBoneProxy> resolvedProxies,
            out List<BoneMergeGroup> mergeGroups,
            out List<BlendShareBoneProxy[]> renameGroups,
            out string diagnostic,
            out BlendShareBoneProxy conflictingProxy)
        {
            var appliers = (meshAppliers ?? Array.Empty<BlendShareMesh>())
                .Where(applier => applier != null && applier.isActiveAndEnabled)
                .Distinct()
                .ToArray();
            var proxyLookup = BlendShareBoneProxyLookup.Create(availableProxies);
            var selected = new List<BlendShareBoneProxy>();
            diagnostic = null;
            conflictingProxy = null;

            foreach (var source in GetRequiredBoneProxySources(appliers, pathRoot))
            {
                if (!proxyLookup.TryGet(source.Armature, source.Path, out var proxy))
                {
                    diagnostic = $"No BlendShare bone proxy represents source bone '{source.Path}'.";
                    resolvedProxies = selected;
                    mergeGroups = new List<BoneMergeGroup>();
                    renameGroups = new List<BlendShareBoneProxy[]>();
                    return false;
                }

                if (proxy.TargetParent == null)
                {
                    diagnostic = $"BlendShare bone proxy '{proxy.name}' has no target parent.";
                    conflictingProxy = proxy;
                    resolvedProxies = selected;
                    mergeGroups = new List<BoneMergeGroup>();
                    renameGroups = new List<BlendShareBoneProxy[]>();
                    return false;
                }

                selected.Add(proxy);
            }

            var groups = new List<BoneMergeGroup>();
            var groupsToRename = new List<BlendShareBoneProxy[]>();
            foreach (var pathGroup in selected
                         .Distinct()
                         .GroupBy(proxy => GetFinalProxyPath(proxy, pathRoot)))
            {
                var ordered = pathGroup
                    .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
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
                        partitionCanonical.transform,
                        partition.Take(partition.Length - 1).Select(proxy => proxy.transform)));
                }

                if (partitions.Count > 1)
                {
                    // Once one collider must remain separate, every resulting bone receives a
                    // build-only name. Proxies in the same merge partition share that name.
                    groupsToRename.AddRange(partitions);
                }
            }

            resolvedProxies = selected;
            mergeGroups = groups;
            renameGroups = groupsToRename;
            return true;
        }

        private static List<BlendShareBoneProxy[]> BuildMergePartitions(
            IReadOnlyList<BlendShareBoneProxy> ordered,
            IEnumerable<BlendShareBoneProxy> selectedProxies,
            Transform pathRoot,
            Func<BlendShareBoneProxy, bool> isAnimated)
        {
            var remaining = ordered.ToList();
            var partitions = new List<BlendShareBoneProxy[]>();
            var avatarRoot = pathRoot != null ? pathRoot.gameObject : null;
            while (remaining.Count > 0)
            {
                var canonical = remaining[remaining.Count - 1];
                remaining.RemoveAt(remaining.Count - 1);
                var partition = new List<BlendShareBoneProxy> { canonical };
                bool canonicalCanMerge = IsCleanProxyHost(
                                             canonical,
                                             selectedProxies,
                                             avatarRoot,
                                             false) &&
                                         !isAnimated(canonical);
                if (canonicalCanMerge)
                {
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        var candidate = remaining[i];
                        if (!AreEquivalentBoneProxies(canonical, candidate, pathRoot) ||
                            !IsCleanProxyHost(candidate, selectedProxies, avatarRoot, true) ||
                            isAnimated(candidate))
                        {
                            continue;
                        }

                        partition.Add(candidate);
                        remaining.RemoveAt(i);
                    }
                }

                partitions.Add(partition
                    .OrderBy(proxy => GetHierarchyOrder(proxy.transform), StringComparer.Ordinal)
                    .ToArray());
            }

            return partitions;
        }

        private static void AssignUniqueBuildNames(
            IEnumerable<BlendShareBoneProxy> allProxies,
            IEnumerable<BlendShareBoneProxy[]> renameGroups,
            Transform pathRoot,
            ObjectPathRemapper pathRemapper)
        {
            var usedPaths = CollectUsedPaths(pathRoot, allProxies);
            bool renamed = false;
            foreach (var group in renameGroups ?? Array.Empty<BlendShareBoneProxy[]>())
            {
                string uniqueName = CreateUniqueBoneName(group[group.Length - 1], pathRoot, usedPaths);
                foreach (var proxy in group)
                {
                    // Preserve the original animation identity before assigning the build-only name.
                    pathRemapper?.RecordObjectTree(proxy.transform);
                    proxy.gameObject.name = uniqueName;
                    renamed = true;
                }
            }

            if (renamed)
            {
                pathRemapper?.ClearCache();
            }
        }

        private static void CreatePreviewProxies(
            IReadOnlyList<BlendShareBoneProxy> originalProxies,
            IEnumerable<BlendShareBoneProxy[]> renameGroups,
            Transform pathRoot,
            out IReadOnlyList<BlendShareBoneProxy> generationProxies,
            out IReadOnlyDictionary<string, Transform> boneTransformOverrides,
            out IReadOnlyList<GameObject> temporaryObjects)
        {
            var proxies = (originalProxies ?? Array.Empty<BlendShareBoneProxy>()).ToList();
            var targets = proxies.ToDictionary(proxy => proxy, proxy => proxy.transform);
            var temporary = new List<GameObject>();
            var usedPaths = CollectUsedPaths(pathRoot, proxies);

            foreach (var group in renameGroups ?? Array.Empty<BlendShareBoneProxy[]>())
            {
                string uniqueName = CreateUniqueBoneName(group[group.Length - 1], pathRoot, usedPaths);
                foreach (var original in group)
                {
                    var previewProxy = CreatePreviewProxy(original, uniqueName);
                    int index = proxies.IndexOf(original);
                    if (index >= 0)
                    {
                        proxies[index] = previewProxy;
                    }

                    targets.Remove(original);
                    targets[previewProxy] = original.transform;
                    temporary.Add(previewProxy.gameObject);
                }
            }

            var overrides = proxies
                .GroupBy(proxy => GetFinalProxyPath(proxy, pathRoot))
                .ToDictionary(
                    group => group.Key,
                    group => targets[group
                        .OrderBy(proxy => GetHierarchyOrder(targets[proxy]), StringComparer.Ordinal)
                        .Last()]);
            generationProxies = proxies;
            boneTransformOverrides = overrides;
            temporaryObjects = temporary;
        }

        private static HashSet<string> CollectUsedPaths(
            Transform pathRoot,
            IEnumerable<BlendShareBoneProxy> proxies)
        {
            var usedPaths = new HashSet<string>((proxies ?? Array.Empty<BlendShareBoneProxy>())
                .Where(proxy => proxy != null)
                .Select(proxy => GetFinalProxyPath(proxy, pathRoot)));
            if (pathRoot != null)
            {
                usedPaths.UnionWith(pathRoot.GetComponentsInChildren<Transform>(true)
                    .Where(transform => transform != pathRoot)
                    .Select(transform => MeshNodePath.Normalize(
                        MeshNodePath.GetRelativePath(transform, pathRoot))));
            }

            return usedPaths;
        }

        private static BlendShareBoneProxy CreatePreviewProxy(
            BlendShareBoneProxy source,
            string uniqueName)
        {
            var gameObject = new GameObject(uniqueName)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var proxy = gameObject.AddComponent<BlendShareBoneProxy>();
            proxy.SourceArmature = source.SourceArmature;
            proxy.SourceBonePath = source.SourceBonePath;
            proxy.TargetParent = source.TargetParent;
            proxy.LocalPosition = source.LocalPosition;
            proxy.LocalEulerRotation = source.LocalEulerRotation;
            proxy.LocalScale = source.LocalScale;
            proxy.RecalculateBindpose = source.RecalculateBindpose;
            proxy.transform.position = source.transform.position;
            proxy.transform.rotation = source.transform.rotation;
            proxy.transform.localScale = source.transform.lossyScale;
            return proxy;
        }

        private static string CreateUniqueBoneName(
            BlendShareBoneProxy proxy,
            Transform pathRoot,
            ISet<string> usedPaths)
        {
            string baseName = proxy != null ? proxy.name : "BlendShareBone";
            while (true)
            {
                string candidate = $"{baseName} BlendShare {Guid.NewGuid():N}";
                string candidatePath = GetFinalProxyPath(proxy, pathRoot, candidate);
                if (usedPaths.Add(candidatePath))
                {
                    return candidate;
                }
            }
        }

        private static IEnumerable<(ArmatureObject Armature, string Path)> GetRequiredBoneProxySources(
            IEnumerable<BlendShareMesh> meshAppliers,
            Transform pathRoot)
        {
            var requirements = new List<(BlendShareBoneProxyLookup.SourceKey Key, ArmatureObject Armature, string Path)>();
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
                            new BlendShareBoneProxyLookup.SourceKey(skin.Armature, path),
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
                    return (first.Armature, first.Path);
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
            BlendShareBoneProxy proxy,
            IEnumerable<BlendShareBoneProxy> selectedProxies,
            GameObject avatarRoot,
            bool rejectExternalReferences)
        {
            if (proxy.transform.childCount != 0 ||
                (selectedProxies ?? Array.Empty<BlendShareBoneProxy>()).Any(candidate =>
                    candidate != null && candidate != proxy && candidate.TargetParent == proxy.transform))
            {
                return false;
            }

            if (proxy.gameObject.GetComponents<Component>().Any(component =>
                    !(component is Transform) && component != proxy))
            {
                return false;
            }

            if (rejectExternalReferences && HasExternalReference(avatarRoot, proxy.transform))
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

        private static string GetFinalProxyPath(BlendShareBoneProxy proxy, Transform pathRoot)
        {
            return GetFinalProxyPath(proxy, pathRoot, proxy != null ? proxy.name : string.Empty);
        }

        private static string GetFinalProxyPath(
            BlendShareBoneProxy proxy,
            Transform pathRoot,
            string boneName)
        {
            var parent = ResolveEffectiveProxyParent(proxy, pathRoot);
            string parentPath = parent != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(parent, pathRoot))
                : MeshNodePath.Root;
            return parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(boneName)
                : MeshNodePath.Normalize($"{parentPath}/{boneName}");
        }

        private static bool AreEquivalentBoneProxies(
            BlendShareBoneProxy first,
            BlendShareBoneProxy second,
            Transform pathRoot)
        {
            if (first == null || second == null ||
                ResolveEffectiveProxyParent(first, pathRoot) != ResolveEffectiveProxyParent(second, pathRoot) ||
                first.name != second.name ||
                first.RecalculateBindpose != second.RecalculateBindpose)
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

        private static Transform ResolveEffectiveProxyParent(BlendShareBoneProxy proxy, Transform pathRoot)
        {
            var parent = proxy != null ? proxy.TargetParent : null;
            if (pathRoot != null &&
                (parent == null || (parent != pathRoot && !parent.IsChildOf(pathRoot))))
            {
                return pathRoot;
            }

            return parent;
        }

        private static void GetEffectiveProxyTransform(
            BlendShareBoneProxy proxy,
            out Vector3 position,
            out Vector3 rotation,
            out Vector3 scale)
        {
            if (proxy != null && proxy.TryGetCurrentLocalTransform(out position, out rotation, out scale))
            {
                return;
            }

            position = proxy != null ? proxy.LocalPosition : Vector3.zero;
            rotation = proxy != null ? proxy.LocalEulerRotation : Vector3.zero;
            scale = proxy != null ? proxy.LocalScale : Vector3.one;
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
