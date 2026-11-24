using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.Pipeline.Apply;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
    internal sealed class BlendShareBoneBindingMergeResolver
    {
        private readonly BuildContext m_Context;
        private readonly AnimatorServicesContext m_AnimatorServices;
        private readonly Transform m_AvatarRoot;
        private readonly List<Replacement> m_Replacements = new();
        private readonly Dictionary<Transform, Transform> m_PlannedTargets = new();
        private readonly HashSet<Object> m_ExternallyReferencedObjects = new();
        private readonly Dictionary<string, Transform> m_FixedTransformByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkinnedMeshRenderer> m_RendererByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<Transform, bool> m_AnimationByTransform = new();
        private HashSet<Transform> m_SelectedTransforms = new();

        public BlendShareBoneBindingMergeResolver(
            BuildContext context,
            AnimatorServicesContext animatorServices)
        {
            m_Context = context;
            m_AnimatorServices = animatorServices;
            m_AvatarRoot = context.AvatarRootTransform;
        }

        public bool Prepare(IReadOnlyList<AvatarBoneBindingUse> uses, out string diagnostic)
        {
            diagnostic = null;
            if (uses.Count == 0) return true;
            var entries = uses
                .Select((use, sequence) => new BindingUse(use, sequence, m_AvatarRoot))
                .GroupBy(use => use.Transform)
                .Select(group => new Entry(
                    group.Key,
                    group.OrderBy(use => use.Sequence).ToArray(),
                    IsAnimated(group.Key)))
                .OrderBy(entry => entry.Sequence)
                .ToArray();
            m_SelectedTransforms = entries.Select(entry => entry.Transform).ToHashSet();
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
                        m_PlannedTargets[entry.Transform] = partition.Target;
                    }

                    plans.Add(new PartitionPlan(
                        partition,
                        requestedPath,
                        partition.Target == existing,
                        hasCollision && partition.Target != existing));
                }
            }

            if (!AllocateFinalPaths(plans, out diagnostic))
            {
                return false;
            }

            ApplyPlans(plans);
            m_AnimatorServices.ObjectPathRemapper.ClearCache();
            return true;
        }

        public bool Cleanup(out string diagnostic)
        {
            diagnostic = null;
            m_AnimationByTransform.Clear();
            RebuildExternalReferenceIndex();
            foreach (var replacement in m_Replacements
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
                m_AnimatorServices.ObjectPathRemapper.ReplaceObject(
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
            m_FixedTransformByPath.TryGetValue(requestedPath, out var transform);
            return transform;
        }

        private bool AreBindPosesCompatibleWithExisting(Entry entry, Transform existing)
        {
            foreach (var pair in entry.BindPosesByMesh)
            {
                if (m_Context.AvatarRootObject == null)
                {
                    return false;
                }

                if (!m_RendererByPath.TryGetValue(pair.Key, out var renderer) ||
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
            var claimedByPath = new Dictionary<string, Transform>(m_FixedTransformByPath, StringComparer.Ordinal);
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
                    var parent = ResolvePlannedTarget(plan.PlannedParent) ?? m_AvatarRoot;

                    if (parent != null && parent != m_AvatarRoot &&
                        m_SelectedTransforms.Contains(parent) &&
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
                            AvatarHierarchyUtil.RelativePath(m_AvatarRoot, plan.Target));
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

                        plan.SetAllocation(parent ?? m_AvatarRoot, finalName, CombinePath(parentPath, finalName));
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

        private void ApplyPlans(IEnumerable<PartitionPlan> plans)
        {
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
                        m_AnimatorServices.ObjectPathRemapper.RecordObjectTree(plan.Target);
                        plan.Target.SetParent(plan.FinalParent, false);
                        plan.Target.name = plan.FinalName;
                        plan.Target.localPosition = targetEntry.LocalPosition;
                        plan.Target.localRotation = targetEntry.LocalRotation;
                        plan.Target.localScale = targetEntry.LocalScale;
                    }
                }

                foreach (var entry in plan.Partition.Entries)
                {
                    foreach (var use in entry.Uses)
                    {
                        use.Binding.Transform = plan.Target;
                    }

                    if (entry.Transform != plan.Target)
                    {
                        m_Replacements.Add(new Replacement(entry.Transform, plan.Target));
                    }
                }
            }
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

                string relative = AvatarHierarchyUtil.RelativePath(plan.Target, descendant);
                string finalPath = plan.FinalPath == string.Empty
                    ? relative
                    : $"{plan.FinalPath}/{relative}";
                if (!TryClaimPath(claimedByPath, finalPath, descendant, out diagnostic))
                {
                    return false;
                }
            }

            return true;
        }

        private bool HasSelectedAncestor(Transform transform)
        {
            for (var parent = transform?.parent; parent != null && parent != m_AvatarRoot; parent = parent.parent)
            {
                if (m_SelectedTransforms.Contains(parent))
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
            if (parent == null || parent == m_AvatarRoot)
            {
                return string.Empty;
            }

            return finalPathByTransform.TryGetValue(parent, out string finalPath)
                ? finalPath
                : AvatarHierarchyUtil.RelativePath(m_AvatarRoot, parent);
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

                string relative = AvatarHierarchyUtil.RelativePath(plan.Target, descendant);
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
            if (descendant == null || descendant == plannedRoot || m_SelectedTransforms.Contains(descendant))
            {
                return false;
            }

            for (var parent = descendant.parent; parent != null && parent != plannedRoot; parent = parent.parent)
            {
                if (m_SelectedTransforms.Contains(parent))
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
            return claimedByPath.TryGetValue(path, out var existing) &&
                   existing != target;
        }

        private static string CombinePath(string parentPath, string name)
        {
            return parentPath == string.Empty
                ? name
                : $"{parentPath}/{name}";
        }

        public bool CanRemoveHost(Transform host) => !IsAnimated(host) && !HasExternalReference(host);

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
                if (!m_SelectedTransforms.Contains(transform.GetChild(childIndex)))
                {
                    return false;
                }
            }

            return !HasExternalReference(transform);
        }

        private bool IsAnimated(Transform transform)
        {
            if (transform == null || m_AnimatorServices == null)
            {
                return false;
            }

            if (m_AnimationByTransform.TryGetValue(transform, out bool cached))
            {
                return cached;
            }

            var remapper = m_AnimatorServices.ObjectPathRemapper;
            remapper.GetVirtualPathForObject(transform);
            bool animated = remapper.GetAllPathsForObject(transform)
                .Any(path => m_AnimatorServices.AnimationIndex.GetClipsForObjectPath(path).Any());
            m_AnimationByTransform[transform] = animated;
            return animated;
        }

        private void RebuildHierarchyIndexes()
        {
            m_FixedTransformByPath.Clear();
            m_RendererByPath.Clear();
            foreach (var transform in m_AvatarRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == m_AvatarRoot || m_SelectedTransforms.Contains(transform) ||
                    HasSelectedAncestor(transform))
                {
                    continue;
                }

                string path = AvatarHierarchyUtil.RelativePath(m_AvatarRoot, transform);
                if (!m_FixedTransformByPath.ContainsKey(path))
                {
                    m_FixedTransformByPath[path] = transform;
                }
            }

            foreach (var renderer in m_Context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = AvatarHierarchyUtil.RelativePath(m_AvatarRoot, renderer.transform);
                if (!m_RendererByPath.ContainsKey(path))
                {
                    m_RendererByPath[path] = renderer;
                }
            }
        }

        private bool HasExternalReference(Transform target)
        {
            return target != null &&
                   (m_ExternallyReferencedObjects.Contains(target) ||
                    m_ExternallyReferencedObjects.Contains(target.gameObject));
        }

        private void RebuildExternalReferenceIndex()
        {
            m_ExternallyReferencedObjects.Clear();
            foreach (var component in m_Context.AvatarRootObject.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is BlendShareBoneProxy)
                {
                    continue;
                }

                using var serializedObject = new SerializedObject(component);
                var property = serializedObject.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    // Bones and rootBone can be retargeted. A renderer's probe anchor
                    // and other serialized references still protect their target.
                    if (component is SkinnedMeshRenderer && (property.propertyPath == "m_Bones" ||
                        property.propertyPath == "m_RootBone"))
                    {
                        enterChildren = false;
                        continue;
                    }
                    enterChildren = property.propertyType != SerializedPropertyType.String;
                    if (property.propertyType == SerializedPropertyType.ObjectReference &&
                        property.objectReferenceValue != null)
                    {
                        m_ExternallyReferencedObjects.Add(property.objectReferenceValue);
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
            foreach (var renderer in m_Context.AvatarRootObject
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
            if (!UnityMeshReaderUtility.TryEnableReadability(generatedMesh))
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
            ObjectRegistry.RegisterReplacedObject(sourceMesh, generatedMesh);
            m_Context.AssetSaver.SaveAsset(generatedMesh);
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
            // These are views owned by the mesh. Copy the coalesced rows before updating it.
            var sourceWeights = mesh.GetAllBoneWeights();
            var sourceCounts = mesh.GetBonesPerVertex();
            if (sourceCounts.Length == 0) return true;
            var outputWeights = new List<BoneWeight1>(sourceWeights.Length);
            var counts = new byte[mesh.vertexCount];
            var aggregate = new Dictionary<int, float>();
            int offset = 0;
            for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
            {
                aggregate.Clear();
                for (int influence = 0; influence < sourceCounts[vertex]; influence++)
                {
                    var weight = sourceWeights[offset++];
                    if ((uint)weight.boneIndex >= (uint)oldToNew.Count)
                    {
                        diagnostic = $"Mesh '{mesh.name}' contains invalid bone index {weight.boneIndex}.";
                        return false;
                    }
                    int index = oldToNew[weight.boneIndex];
                    aggregate.TryGetValue(index, out var previous);
                    aggregate[index] = previous + weight.weight;
                }

                foreach (var pair in aggregate.OrderByDescending(p => p.Value).ThenBy(p => p.Key))
                    outputWeights.Add(new BoneWeight1 { boneIndex = pair.Key, weight = pair.Value });
                counts[vertex] = checked((byte)aggregate.Count);
            }
            using var outputWeightArray = new NativeArray<BoneWeight1>(outputWeights.ToArray(), Allocator.Temp);
            using var outputCountArray = new NativeArray<byte>(counts, Allocator.Temp);
            mesh.SetBoneWeights(outputCountArray, outputWeightArray);
            return true;
        }

        private Transform ResolveEntryParent(Entry entry)
        {
            return ResolvePlannedTarget(entry?.EffectiveParent) ?? m_AvatarRoot;
        }

        private Transform ResolvePlannedTarget(Transform transform)
        {
            if (transform == null)
            {
                return null;
            }

            var visited = new HashSet<Transform>();
            while (m_PlannedTargets.TryGetValue(transform, out var target) &&
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

        private readonly struct BindingUse
        {
            public Transform Transform { get; }
            public BlendShareBoneProxy Proxy { get; }
            public BlendShareBoneProxyBinding Binding { get; }
            public string MeshKey { get; }
            public string FinalPath { get; }
            public Matrix4x4 BindPose { get; }
            public int Sequence { get; }

            public BindingUse(AvatarBoneBindingUse use, int sequence, Transform root)
            {
                Transform = use.Bone.Transform;
                Proxy = use.Bone.Proxy;
                Binding = use.Bone.Binding;
                MeshKey = AvatarHierarchyUtil.RelativePath(root, use.Renderer);
                FinalPath = Proxy.GetFinalPath(Binding, root);
                BindPose = use.BindPose;
                Sequence = sequence;
            }
        }

        private sealed class Entry
        {
            public Entry(
                Transform transform,
                BindingUse[] uses,
                bool isAnimated)
            {
                Transform = transform;
                Uses = uses;
                Sequence = uses.Max(use => use.Sequence);
                RequestedPath = uses[uses.Length - 1].FinalPath;
                IsAnimated = isAnimated;
                EffectiveParent = RepresentativeUse.Proxy.GetEffectiveParent(RepresentativeUse.Binding);
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
            public BindingUse[] Uses { get; }
            public BindingUse RepresentativeUse => Uses[Uses.Length - 1];
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
                RequestedPath = requestedPath;
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
                FinalPath = path;
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
