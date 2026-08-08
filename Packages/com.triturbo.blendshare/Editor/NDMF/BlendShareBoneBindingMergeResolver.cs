using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.SkinWeights;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareBoneBindingMergeResolver : IUnityGenerationPreparation
    {
        private readonly BuildContext context;
        private readonly AnimatorServicesContext animatorServices;
        private readonly Transform avatarRoot;
        private readonly List<Replacement> replacements = new();
        private readonly Dictionary<string, Transform> boneTransformOverrides = new();
        private readonly Dictionary<Transform, Transform> plannedTargets = new();
        private readonly HashSet<Object> externallyReferencedObjects = new();
        private readonly Dictionary<string, Transform> fixedTransformByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkinnedMeshRenderer> rendererByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<Transform, bool> animationByTransform = new();
        private HashSet<Transform> selectedTransforms = new();

        public IReadOnlyDictionary<string, Transform> BoneTransformOverrides => boneTransformOverrides;

        public BlendShareBoneBindingMergeResolver(
            BuildContext context,
            AnimatorServicesContext animatorServices)
        {
            this.context = context;
            this.animatorServices = animatorServices;
            avatarRoot = context?.AvatarRootTransform;
        }

        /// <inheritdoc />
        public UnityGenerationPreparationResult Prepare(UnityMeshGenerationSession session)
        {
            replacements.Clear();
            plannedTargets.Clear();
            animationByTransform.Clear();
            if (session == null || avatarRoot == null)
            {
                return UnityGenerationPreparationResult.Failed(
                    "BlendShare bone merge requires an avatar generation session.");
            }

            var uses = session.BoneBindingUses
                .Where(use => use?.Binding != null && use.Transform != null)
                .OrderBy(use => use.Sequence)
                .ToArray();
            var entries = uses
                .GroupBy(use => use.Transform)
                .Select(group => new Entry(
                    group.Key,
                    group.OrderBy(use => use.Sequence).ToArray(),
                    IsAnimated(group.Key)))
                .OrderBy(entry => entry.Sequence)
                .ToArray();
            selectedTransforms = entries.Select(entry => entry.Transform).ToHashSet();
            RebuildHierarchyIndexes();
            RebuildExternalReferenceIndex();
            var plans = new List<PartitionPlan>();

            foreach (var requestedPathGroup in entries
                         .GroupBy(entry => entry.RequestedPath)
                         .OrderBy(group => PathDepth(group.Key))
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                string requestedPath = requestedPathGroup.Key;
                var ordered = requestedPathGroup.OrderBy(entry => entry.Sequence).ToArray();
                var existing = FindExistingTransform(requestedPath);

                var partitions = BuildPartitions(ordered);
                var resolved = new List<ResolvedPartition>();
                foreach (var partition in partitions)
                {
                    ResolvePartition(partition, existing, resolved);
                }

                bool hasCollision = existing != null || resolved.Count > 1;
                foreach (var partition in resolved)
                {
                    foreach (var entry in partition.Entries)
                    {
                        plannedTargets[entry.Transform] = partition.Target;
                    }

                    plans.Add(new PartitionPlan(
                        partition,
                        requestedPath,
                        partition.Target == existing,
                        hasCollision && partition.Target != existing));
                }
            }

            if (!AllocateFinalPaths(plans, out string diagnostic))
            {
                return UnityGenerationPreparationResult.Failed(diagnostic);
            }

            bool changed = ApplyPlans(session, plans);
            animatorServices?.ObjectPathRemapper.ClearCache();
            return UnityGenerationPreparationResult.Success(changed);
        }

        public void Complete(UnityMeshGenerationSession session)
        {
            boneTransformOverrides.Clear();
            foreach (var pair in session?.BoneTransformsByArtifactPath ??
                                 new Dictionary<string, Transform>())
            {
                boneTransformOverrides[pair.Key] = pair.Value;
            }
        }

        public bool Cleanup(out string diagnostic)
        {
            diagnostic = null;
            animationByTransform.Clear();
            RebuildExternalReferenceIndex();
            foreach (var replacement in replacements
                         .GroupBy(item => item.Redundant)
                         .Select(group => group.Last())
                         .OrderByDescending(item => HierarchyDepth(item.Redundant)))
            {
                if (replacement.Redundant == null || replacement.Target == null)
                {
                    continue;
                }

                if (!CanDeleteGeneratedTransform(replacement.Redundant))
                {
                    diagnostic = $"BlendShare could not finish merging bone '{replacement.Redundant.name}' " +
                                 "because it gained animation, children, components, or external references after generation.";
                    return false;
                }

                if (!ReplaceRendererReferences(
                        replacement.Redundant,
                        replacement.Target,
                        out diagnostic))
                {
                    return false;
                }
                animatorServices?.ObjectPathRemapper.ReplaceObject(
                    replacement.Redundant,
                    replacement.Target);
                Object.DestroyImmediate(replacement.Redundant.gameObject);
            }

            return true;
        }

        private void ResolvePartition(
            IReadOnlyList<Entry> partition,
            Transform existing,
            ICollection<ResolvedPartition> output)
        {
            if (existing != null &&
                partition.All(entry =>
                    IsTransformCompatible(entry, existing) &&
                    AreBindPosesCompatibleWithExisting(entry, existing)))
            {
                var mergeable = partition.Where(CanMergeGeneratedEntry).ToArray();
                if (mergeable.Length > 0)
                {
                    output.Add(new ResolvedPartition(existing, mergeable));
                }

                foreach (var retained in partition.Except(mergeable))
                {
                    output.Add(new ResolvedPartition(retained.Transform, new[] { retained }));
                }
                return;
            }

            var protectedEntries = partition
                .Where(entry => !CanMergeGeneratedEntry(entry))
                .ToArray();
            if (protectedEntries.Length == 1)
            {
                output.Add(new ResolvedPartition(protectedEntries[0].Transform, partition));
                return;
            }

            if (protectedEntries.Length > 1)
            {
                foreach (var retained in protectedEntries)
                {
                    output.Add(new ResolvedPartition(retained.Transform, new[] { retained }));
                }

                var clean = partition.Except(protectedEntries).ToArray();
                if (clean.Length > 0)
                {
                    output.Add(new ResolvedPartition(clean[clean.Length - 1].Transform, clean));
                }
                return;
            }

            output.Add(new ResolvedPartition(
                partition[partition.Count - 1].Transform,
                partition));
        }

        private List<Entry[]> BuildPartitions(IReadOnlyList<Entry> ordered)
        {
            var result = ordered
                .Where(entry => entry.IsAnimated)
                .Select(entry => new[] { entry })
                .ToList();
            var remaining = ordered.Where(entry => !entry.IsAnimated).ToList();
            while (remaining.Count > 0)
            {
                var representative = remaining[remaining.Count - 1];
                remaining.RemoveAt(remaining.Count - 1);
                var partition = new List<Entry> { representative };
                for (int index = remaining.Count - 1; index >= 0; index--)
                {
                    if (ResolveEntryParent(representative) != ResolveEntryParent(remaining[index]) ||
                        !AreEquivalentLocalTransforms(representative, remaining[index]) ||
                        !AreRendererBindPosesCompatible(
                            representative.Transform,
                            remaining[index].Transform) ||
                        !AreBindPosesCompatible(representative, remaining[index]))
                    {
                        continue;
                    }

                    partition.Add(remaining[index]);
                    remaining.RemoveAt(index);
                }

                result.Add(partition.OrderBy(entry => entry.Sequence).ToArray());
            }

            return result;
        }

        private Transform FindExistingTransform(string requestedPath)
        {
            fixedTransformByPath.TryGetValue(MeshNodePath.Normalize(requestedPath), out var transform);
            return transform;
        }

        private bool AreBindPosesCompatibleWithExisting(Entry entry, Transform existing)
        {
            foreach (var pair in entry.BindPosesByMesh)
            {
                if (context.AvatarRootObject == null)
                {
                    return false;
                }

                if (!rendererByPath.TryGetValue(MeshNodePath.Normalize(pair.Key), out var renderer) ||
                    renderer == null)
                {
                    return false;
                }

                var bones = renderer.bones ?? Array.Empty<Transform>();
                int boneIndex = Array.IndexOf(bones, existing);
                var bindposes = renderer.sharedMesh != null
                    ? renderer.sharedMesh.bindposes ?? Array.Empty<Matrix4x4>()
                    : Array.Empty<Matrix4x4>();
                Matrix4x4 existingBindPose = boneIndex >= 0 && boneIndex < bindposes.Length
                    ? bindposes[boneIndex]
                    : existing.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                if (!AreEquivalentMatrices(pair.Value, existingBindPose))
                {
                    return false;
                }
            }

            return true;
        }

        private bool AllocateFinalPaths(
            IReadOnlyCollection<PartitionPlan> plans,
            out string diagnostic)
        {
            diagnostic = null;
            var claimedByPath = BuildFixedPathClaims();
            var finalPathByTransform = new Dictionary<Transform, string>();
            var pending = plans
                .OrderBy(plan => PathDepth(plan.RequestedPath))
                .ThenBy(plan => plan.RequestedPath, StringComparer.Ordinal)
                .ThenBy(plan => plan.Sequence)
                .ToList();

            while (pending.Count > 0)
            {
                bool progressed = false;
                for (int index = 0; index < pending.Count; index++)
                {
                    var plan = pending[index];
                    var parent = ResolvePlannedTarget(plan.PlannedParent);

                    if (parent != null && parent != avatarRoot &&
                        selectedTransforms.Contains(parent) &&
                        !finalPathByTransform.ContainsKey(parent))
                    {
                        continue;
                    }

                    string parentPath = ResolveAllocatedParentPath(parent, finalPathByTransform);
                    if (plan.UsesExistingTarget)
                    {
                        plan.SetAllocation(
                            plan.Target.parent,
                            plan.Target.name,
                            MeshNodePath.Normalize(MeshNodePath.GetRelativePath(plan.Target, avatarRoot)));
                    }
                    else
                    {
                        string originalName = plan.Target.name;
                        string finalName = originalName;
                        if (plan.RequiresRename ||
                            !IsSubtreePathAvailable(
                                claimedByPath,
                                plan,
                                parentPath,
                                originalName))
                        {
                            finalName = AllocateRenamedName(
                                claimedByPath,
                                plan,
                                parentPath,
                                originalName);
                        }

                        plan.SetAllocation(parent ?? avatarRoot, finalName, CombinePath(parentPath, finalName));
                    }

                    if (!TryClaimPath(claimedByPath, plan.FinalPath, plan.Target, out diagnostic) ||
                        !TryClaimRetainedSubtreePaths(
                            claimedByPath,
                            plan,
                            out diagnostic))
                    {
                        return false;
                    }

                    finalPathByTransform[plan.Target] = plan.FinalPath;
                    pending.RemoveAt(index);
                    progressed = true;
                    break;
                }

                if (!progressed)
                {
                    diagnostic = "BlendShare could not order planned bone parents before their children.";
                    return false;
                }
            }

            return true;
        }

        private bool ApplyPlans(
            UnityMeshGenerationSession session,
            IEnumerable<PartitionPlan> plans)
        {
            bool changed = false;
            foreach (var plan in plans
                         .OrderBy(plan => PathDepth(plan.FinalPath))
                         .ThenBy(plan => plan.FinalPath, StringComparer.Ordinal)
                         .ThenBy(plan => plan.Sequence))
            {
                var targetEntry = plan.TargetEntry;
                if (!plan.UsesExistingTarget)
                {
                    bool hierarchyChanged = plan.Target.parent != plan.FinalParent ||
                                            plan.Target.name != plan.FinalName;
                    if (hierarchyChanged)
                    {
                        animatorServices?.ObjectPathRemapper.RecordObjectTree(plan.Target);
                        plan.Target.SetParent(plan.FinalParent, false);
                        plan.Target.name = plan.FinalName;
                        plan.Target.localPosition = targetEntry.LocalPosition;
                        plan.Target.localRotation = targetEntry.LocalRotation;
                        plan.Target.localScale = targetEntry.LocalScale;
                        changed = true;
                    }
                }

                foreach (var entry in plan.Partition.Entries)
                {
                    foreach (var use in entry.Uses)
                    {
                        if (use.Binding.Transform != plan.Target)
                        {
                            use.Binding.Transform = plan.Target;
                            changed = true;
                        }
                        session.SetBoneBindingArtifactPath(use.Binding, plan.FinalPath);
                    }

                    if (entry.Transform != plan.Target)
                    {
                        replacements.Add(new Replacement(entry.Transform, plan.Target));
                    }
                }
            }

            return changed;
        }

        private Dictionary<string, Transform> BuildFixedPathClaims()
        {
            return new Dictionary<string, Transform>(fixedTransformByPath, StringComparer.Ordinal);
        }

        private bool TryClaimRetainedSubtreePaths(
            IDictionary<string, Transform> claimedByPath,
            PartitionPlan plan,
            out string diagnostic)
        {
            diagnostic = null;
            foreach (var descendant in plan.Target.GetComponentsInChildren<Transform>(true))
            {
                if (!IsRetainedWithPlan(descendant, plan.Target))
                {
                    continue;
                }

                string relative = MeshNodePath.Normalize(
                    MeshNodePath.GetRelativePath(descendant, plan.Target));
                string finalPath = plan.FinalPath == MeshNodePath.Root
                    ? relative
                    : MeshNodePath.Normalize($"{plan.FinalPath}/{relative}");
                if (!TryClaimPath(claimedByPath, finalPath, descendant, out diagnostic))
                {
                    return false;
                }
            }

            return true;
        }

        private bool HasSelectedAncestor(Transform transform)
        {
            for (var parent = transform?.parent; parent != null && parent != avatarRoot; parent = parent.parent)
            {
                if (selectedTransforms.Contains(parent))
                {
                    return true;
                }
            }

            return false;
        }

        private string ResolveAllocatedParentPath(
            Transform parent,
            IReadOnlyDictionary<Transform, string> finalPathByTransform)
        {
            if (parent == null || parent == avatarRoot)
            {
                return MeshNodePath.Root;
            }

            return finalPathByTransform.TryGetValue(parent, out string finalPath)
                ? finalPath
                : MeshNodePath.Normalize(MeshNodePath.GetRelativePath(parent, avatarRoot));
        }

        private string AllocateRenamedName(
            IReadOnlyDictionary<string, Transform> claimedByPath,
            PartitionPlan plan,
            string parentPath,
            string originalName)
        {
            for (int counter = 1; counter < int.MaxValue; counter++)
            {
                string candidate = $"{originalName}_BlendShare_{counter}";
                if (IsSubtreePathAvailable(claimedByPath, plan, parentPath, candidate))
                {
                    return candidate;
                }
            }

            return $"{originalName}_BlendShare";
        }

        private bool IsSubtreePathAvailable(
            IReadOnlyDictionary<string, Transform> claimedByPath,
            PartitionPlan plan,
            string parentPath,
            string candidateName)
        {
            string candidateRoot = CombinePath(parentPath, candidateName);
            if (IsClaimedByOther(claimedByPath, candidateRoot, plan.Target))
            {
                return false;
            }

            foreach (var descendant in plan.Target.GetComponentsInChildren<Transform>(true))
            {
                if (!IsRetainedWithPlan(descendant, plan.Target))
                {
                    continue;
                }

                string relative = MeshNodePath.Normalize(
                    MeshNodePath.GetRelativePath(descendant, plan.Target));
                string candidatePath = CombinePath(candidateRoot, relative);
                if (IsClaimedByOther(claimedByPath, candidatePath, descendant))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsRetainedWithPlan(Transform descendant, Transform plannedRoot)
        {
            if (descendant == null || descendant == plannedRoot || selectedTransforms.Contains(descendant))
            {
                return false;
            }

            for (var parent = descendant.parent; parent != null && parent != plannedRoot; parent = parent.parent)
            {
                if (selectedTransforms.Contains(parent))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryClaimPath(
            IDictionary<string, Transform> claimedByPath,
            string path,
            Transform transform,
            out string diagnostic)
        {
            path = MeshNodePath.Normalize(path);
            if (claimedByPath.TryGetValue(path, out var existing) && existing != transform)
            {
                diagnostic = $"BlendShare final bone path '{path}' is occupied by both " +
                             $"'{existing.name}' and '{transform.name}'.";
                return false;
            }

            claimedByPath[path] = transform;
            diagnostic = null;
            return true;
        }

        private static bool IsClaimedByOther(
            IReadOnlyDictionary<string, Transform> claimedByPath,
            string path,
            Transform target)
        {
            return claimedByPath.TryGetValue(MeshNodePath.Normalize(path), out var existing) &&
                   existing != target;
        }

        private static string CombinePath(string parentPath, string name)
        {
            return parentPath == MeshNodePath.Root
                ? MeshNodePath.Normalize(name)
                : MeshNodePath.Normalize($"{parentPath}/{name}");
        }

        private bool CanDeleteGeneratedTransform(Transform transform)
        {
            if (transform == null || transform.childCount != 0 || IsAnimated(transform))
            {
                return false;
            }

            if (transform.gameObject.GetComponents<Component>().Any(component =>
                    !(component is Transform) && !(component is BlendShareBoneProxy)))
            {
                return false;
            }

            return !HasExternalReference(transform);
        }

        private bool CanMergeGeneratedEntry(Entry entry)
        {
            var transform = entry?.Transform;
            if (transform == null || entry.IsAnimated)
            {
                return false;
            }

            if (transform.gameObject.GetComponents<Component>().Any(component =>
                    !(component is Transform) && !(component is BlendShareBoneProxy)))
            {
                return false;
            }

            for (int childIndex = 0; childIndex < transform.childCount; childIndex++)
            {
                if (!selectedTransforms.Contains(transform.GetChild(childIndex)))
                {
                    return false;
                }
            }

            return !HasExternalReference(transform);
        }

        private bool IsAnimated(Transform transform)
        {
            if (transform == null || animatorServices == null)
            {
                return false;
            }

            if (animationByTransform.TryGetValue(transform, out bool cached))
            {
                return cached;
            }

            var remapper = animatorServices.ObjectPathRemapper;
            remapper.GetVirtualPathForObject(transform);
            bool animated = remapper.GetAllPathsForObject(transform)
                .Any(path => animatorServices.AnimationIndex.GetClipsForObjectPath(path).Any());
            animationByTransform[transform] = animated;
            return animated;
        }

        private void RebuildHierarchyIndexes()
        {
            fixedTransformByPath.Clear();
            rendererByPath.Clear();
            foreach (var transform in avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == avatarRoot || selectedTransforms.Contains(transform) ||
                    HasSelectedAncestor(transform))
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(transform, avatarRoot));
                if (!fixedTransformByPath.ContainsKey(path))
                {
                    fixedTransformByPath[path] = transform;
                }
            }

            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = MeshNodePath.Normalize(
                    MeshNodePath.GetRelativePath(renderer.transform, avatarRoot));
                if (!rendererByPath.ContainsKey(path))
                {
                    rendererByPath[path] = renderer;
                }
            }
        }

        private bool HasExternalReference(Transform target)
        {
            return target != null &&
                   (externallyReferencedObjects.Contains(target) ||
                    externallyReferencedObjects.Contains(target.gameObject));
        }

        private void RebuildExternalReferenceIndex()
        {
            externallyReferencedObjects.Clear();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is SkinnedMeshRenderer ||
                    component is BlendShareBoneProxy)
                {
                    continue;
                }

                using var serializedObject = new SerializedObject(component);
                var property = serializedObject.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = property.propertyType != SerializedPropertyType.String;
                    if (property.propertyType == SerializedPropertyType.ObjectReference &&
                        property.objectReferenceValue != null)
                    {
                        externallyReferencedObjects.Add(property.objectReferenceValue);
                    }
                }
            }
        }

        private bool ReplaceRendererReferences(
            Transform oldBone,
            Transform newBone,
            out string diagnostic)
        {
            diagnostic = null;
            foreach (var renderer in context.AvatarRootObject
                         .GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = renderer.bones ?? Array.Empty<Transform>();
                bool hasOldBone = bones.Contains(oldBone);
                if (!hasOldBone)
                {
                    if (renderer.rootBone == oldBone)
                    {
                        renderer.rootBone = newBone;
                    }
                    continue;
                }

                if (!TryCoalesceRendererBoneSlots(renderer, oldBone, newBone, out diagnostic))
                {
                    return false;
                }

                bones = renderer.bones ?? Array.Empty<Transform>();
                for (int index = 0; index < bones.Length; index++)
                {
                    if (bones[index] == oldBone)
                    {
                        bones[index] = newBone;
                    }
                }

                renderer.bones = bones;

                if (renderer.rootBone == oldBone)
                {
                    renderer.rootBone = newBone;
                }
            }

            return true;
        }

        private bool TryCoalesceRendererBoneSlots(
            SkinnedMeshRenderer renderer,
            Transform oldBone,
            Transform newBone,
            out string diagnostic)
        {
            diagnostic = null;
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var oldIndices = Enumerable.Range(0, bones.Length)
                .Where(index => bones[index] == oldBone)
                .ToHashSet();
            var targetIndices = Enumerable.Range(0, bones.Length)
                .Where(index => bones[index] == newBone)
                .ToArray();
            if (oldIndices.Count == 0 || targetIndices.Length == 0)
            {
                return true;
            }

            var sourceMesh = renderer.sharedMesh;
            if (sourceMesh == null)
            {
                diagnostic = $"Renderer '{renderer.name}' has no mesh while merging BlendShare bone slots.";
                return false;
            }

            int targetIndex = targetIndices[0];
            var bindposes = sourceMesh.bindposes ?? Array.Empty<Matrix4x4>();
            if (targetIndex >= bindposes.Length || oldIndices.Any(index =>
                    index >= bindposes.Length ||
                    !AreEquivalentMatrices(bindposes[targetIndex], bindposes[index])))
            {
                diagnostic = $"Renderer '{renderer.name}' has incompatible bindposes for bones " +
                             $"'{oldBone.name}' and '{newBone.name}'.";
                return false;
            }

            var removed = oldIndices
                .Concat(targetIndices.Skip(1))
                .ToHashSet();
            var oldToNew = new int[bones.Length];
            var newBones = new List<Transform>();
            var newBindposes = new List<Matrix4x4>();
            int canonicalNewIndex = -1;
            for (int oldIndex = 0; oldIndex < bones.Length; oldIndex++)
            {
                if (oldIndex == targetIndex)
                {
                    canonicalNewIndex = newBones.Count;
                    oldToNew[oldIndex] = canonicalNewIndex;
                    newBones.Add(newBone);
                    newBindposes.Add(bindposes[oldIndex]);
                    continue;
                }

                if (removed.Contains(oldIndex))
                {
                    continue;
                }

                oldToNew[oldIndex] = newBones.Count;
                newBones.Add(bones[oldIndex]);
                newBindposes.Add(oldIndex < bindposes.Length ? bindposes[oldIndex] : Matrix4x4.identity);
            }

            foreach (int removedIndex in removed)
            {
                oldToNew[removedIndex] = canonicalNewIndex;
            }

            var generatedMesh = Object.Instantiate(sourceMesh);
            generatedMesh.name = sourceMesh.name;
            if (!UnityMeshEditorDataUtility.TryEnableReadability(generatedMesh))
            {
                diagnostic = $"Renderer '{renderer.name}' uses an unreadable mesh that cannot be rewritten for bone merging.";
                Object.DestroyImmediate(generatedMesh);
                return false;
            }
            if (!RewriteBoneWeights(generatedMesh, oldToNew, out diagnostic))
            {
                Object.DestroyImmediate(generatedMesh);
                return false;
            }

            generatedMesh.bindposes = newBindposes.ToArray();
            context.AssetSaver.SaveAsset(generatedMesh);
            renderer.sharedMesh = generatedMesh;
            renderer.bones = newBones.ToArray();
            return true;
        }

        private static bool RewriteBoneWeights(
            Mesh mesh,
            IReadOnlyList<int> oldToNew,
            out string diagnostic)
        {
            diagnostic = null;
            using var sourceWeights = mesh.GetAllBoneWeights();
            using var sourceCounts = mesh.GetBonesPerVertex();
            using var outputWeights = new NativeList<BoneWeight1>(Allocator.Temp);
            using var outputCounts = new NativeList<byte>(Allocator.Temp);
            bool limitToFour = BlendShareSkinWeightSettings.LimitSkinWeightsToFour;
            int influenceLimit = limitToFour ? 4 : byte.MaxValue;
            var selected = new List<(int index, double weight)>(influenceLimit);
            int sourceIndex = 0;
            int maximumInfluences = 0;
            for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
            {
                var aggregate = new Dictionary<int, double>();
                int count = vertex < sourceCounts.Length ? sourceCounts[vertex] : 0;
                for (int influence = 0; influence < count; influence++)
                {
                    var weight = sourceWeights[sourceIndex + influence];
                    if (weight.boneIndex < 0 || weight.boneIndex >= oldToNew.Count)
                    {
                        diagnostic = $"Mesh '{mesh.name}' contains invalid bone index {weight.boneIndex}.";
                        return false;
                    }

                    int targetIndex = oldToNew[weight.boneIndex];
                    aggregate.TryGetValue(targetIndex, out double existing);
                    aggregate[targetIndex] = existing + weight.weight;
                }

                sourceIndex += count;
                maximumInfluences = Math.Max(
                    maximumInfluences,
                    SkinWeightFeatureGenerator.KeepTopWeights(
                        influenceLimit,
                        aggregate,
                        selected));
                foreach (var weight in selected)
                {
                    outputWeights.Add(new BoneWeight1
                    {
                        boneIndex = weight.index,
                        weight = (float)weight.weight
                    });
                }
                outputCounts.Add((byte)selected.Count);
            }

            if (!limitToFour &&
                maximumInfluences > 4 &&
                UnitySkinWeightMeshLayoutUtility.RequiresUnlimitedLayoutRebuild(mesh))
            {
                UnitySkinWeightMeshLayoutUtility.RemoveSkinWeightVertexAttributes(mesh);
            }

            using var outputWeightArray = outputWeights.ToArray(Allocator.Temp);
            using var outputCountArray = outputCounts.ToArray(Allocator.Temp);
            mesh.SetBoneWeights(outputCountArray, outputWeightArray);
            mesh.RecalculateBounds();
            return true;
        }

        private Transform ResolveEntryParent(Entry entry)
        {
            return ResolvePlannedTarget(entry?.EffectiveParent);
        }

        private Transform ResolvePlannedTarget(Transform transform)
        {
            if (transform == null)
            {
                return null;
            }

            var visited = new HashSet<Transform>();
            while (plannedTargets.TryGetValue(transform, out var target) &&
                   target != null && target != transform && visited.Add(transform))
            {
                transform = target;
            }

            return transform;
        }

        private bool IsTransformCompatible(Entry entry, Transform existing)
        {
            return ResolveEntryParent(entry) == existing.parent &&
                   AreEquivalentLocalTransforms(entry, existing) &&
                   AreRendererBindPosesCompatible(entry.Transform, existing);
        }

        private static bool AreRendererBindPosesCompatible(Transform first, Transform second)
        {
            if (first == null || second == null || first.root != second.root)
            {
                return false;
            }

            foreach (var renderer in first.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = renderer.bones ?? Array.Empty<Transform>();
                var firstIndices = Enumerable.Range(0, bones.Length)
                    .Where(index => bones[index] == first)
                    .ToArray();
                var secondIndices = Enumerable.Range(0, bones.Length)
                    .Where(index => bones[index] == second)
                    .ToArray();
                if (firstIndices.Length == 0 || secondIndices.Length == 0)
                {
                    continue;
                }

                var bindposes = renderer.sharedMesh != null
                    ? renderer.sharedMesh.bindposes ?? Array.Empty<Matrix4x4>()
                    : Array.Empty<Matrix4x4>();
                foreach (int firstIndex in firstIndices)
                foreach (int secondIndex in secondIndices)
                {
                    if (firstIndex >= bindposes.Length || secondIndex >= bindposes.Length ||
                        !AreEquivalentMatrices(bindposes[firstIndex], bindposes[secondIndex]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool AreEquivalentLocalTransforms(Entry first, Entry second)
        {
            return Vector3.SqrMagnitude(first.LocalPosition - second.LocalPosition) <= 0.00000001f &&
                   Quaternion.Angle(first.LocalRotation, second.LocalRotation) <= 0.01f &&
                   Vector3.SqrMagnitude(first.LocalScale - second.LocalScale) <= 0.00000001f;
        }

        private static bool AreEquivalentLocalTransforms(Entry entry, Transform existing)
        {
            return Vector3.SqrMagnitude(entry.LocalPosition - existing.localPosition) <= 0.00000001f &&
                   Quaternion.Angle(entry.LocalRotation, existing.localRotation) <= 0.01f &&
                   Vector3.SqrMagnitude(entry.LocalScale - existing.localScale) <= 0.00000001f;
        }

        private static bool AreBindPosesCompatible(Entry first, Entry second)
        {
            foreach (string meshKey in first.BindPosesByMesh.Keys.Intersect(second.BindPosesByMesh.Keys))
            {
                if (!AreEquivalentMatrices(
                        first.BindPosesByMesh[meshKey],
                        second.BindPosesByMesh[meshKey]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreEquivalentMatrices(Matrix4x4 first, Matrix4x4 second)
        {
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                if (Mathf.Abs(first[row, column] - second[row, column]) > 0.0001f)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class Entry
        {
            public Entry(
                Transform transform,
                UnityBoneBindingUse[] uses,
                bool isAnimated)
            {
                Transform = transform;
                Uses = uses;
                Sequence = uses.Max(use => use.Sequence);
                RequestedPath = uses[uses.Length - 1].FinalPath;
                IsAnimated = isAnimated;
                EffectiveParent = RepresentativeUse.Proxy?.GetEffectiveParent(RepresentativeUse.Binding) ??
                                  transform.parent;
                if (RepresentativeUse.Proxy != null &&
                    RepresentativeUse.Proxy.TryGetCurrentLocalTransform(
                        RepresentativeUse.Binding,
                        out var position,
                        out var rotation,
                        out var scale))
                {
                    LocalPosition = position;
                    LocalRotation = Quaternion.Euler(rotation);
                    LocalScale = scale;
                }
                else
                {
                    LocalPosition = transform.localPosition;
                    LocalRotation = transform.localRotation;
                    LocalScale = transform.localScale;
                }
                BindPosesByMesh = uses
                    .GroupBy(use => use.MeshKey)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderBy(use => use.Sequence).Last().BindPose);
            }

            public Transform Transform { get; }
            public UnityBoneBindingUse[] Uses { get; }
            public UnityBoneBindingUse RepresentativeUse => Uses[Uses.Length - 1];
            public int Sequence { get; }
            public string RequestedPath { get; }
            public bool IsAnimated { get; }
            public Transform EffectiveParent { get; }
            public Vector3 LocalPosition { get; }
            public Quaternion LocalRotation { get; }
            public Vector3 LocalScale { get; }
            public IReadOnlyDictionary<string, Matrix4x4> BindPosesByMesh { get; }
        }

        private sealed class ResolvedPartition
        {
            public ResolvedPartition(Transform target, IEnumerable<Entry> entries)
            {
                Target = target;
                Entries = entries.ToArray();
            }

            public Transform Target { get; }
            public Entry[] Entries { get; }
        }

        private sealed class PartitionPlan
        {
            public PartitionPlan(
                ResolvedPartition partition,
                string requestedPath,
                bool usesExistingTarget,
                bool requiresRename)
            {
                Partition = partition;
                RequestedPath = MeshNodePath.Normalize(requestedPath);
                UsesExistingTarget = usesExistingTarget;
                RequiresRename = requiresRename;
                TargetEntry = partition.Entries.FirstOrDefault(entry => entry.Transform == partition.Target) ??
                              partition.Entries[partition.Entries.Length - 1];
            }

            public ResolvedPartition Partition { get; }
            public Transform Target => Partition.Target;
            public Entry TargetEntry { get; }
            public Transform PlannedParent => TargetEntry.EffectiveParent;
            public string RequestedPath { get; }
            public bool UsesExistingTarget { get; }
            public bool RequiresRename { get; }
            public int Sequence => TargetEntry.Sequence;
            public Transform FinalParent { get; private set; }
            public string FinalName { get; private set; }
            public string FinalPath { get; private set; }

            public void SetAllocation(Transform parent, string name, string path)
            {
                FinalParent = parent;
                FinalName = name;
                FinalPath = MeshNodePath.Normalize(path);
            }
        }

        private readonly struct Replacement
        {
            public Replacement(Transform redundant, Transform target)
            {
                Redundant = redundant;
                Target = target;
            }

            public Transform Redundant { get; }
            public Transform Target { get; }
        }

        private static int PathDepth(string path)
        {
            return string.IsNullOrEmpty(path) ? 0 : path.Count(character => character == '/');
        }

        private static int HierarchyDepth(Transform transform)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }
            return depth;
        }
    }
}
