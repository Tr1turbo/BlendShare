using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Fbx;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Authoring
{
    internal static class BlendShareComponentSetupService
    {
        private const string k_BoneProxyContainerName = "Armature";

        public static CreateBlendShareSetupResult CreateSetup(Transform targetRoot, BlendShareAsset patch)
        {
            return CreateSetup(targetRoot, patch, null, null);
        }

        public static CreateBlendShareSetupResult CreateSetup(
            Transform targetRoot,
            BlendShareAsset patch,
            BlendShareMeshAsset selectedMesh,
            SkinnedMeshRenderer targetRenderer)
        {
            var result = new CreateBlendShareSetupResult();
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            if (patch == null)
            {
                result.AddDiagnostic("BlendShare patch is missing.");
                return result;
            }

            var document = ReadDocument(patch);
            if (document == null)
            {
                result.AddDiagnostic("Cannot read the BlendShare patch header.");
                return result;
            }

            if (selectedMesh != null && (selectedMesh.Root != patch || !document.Meshes.Any(mesh => mesh.MeshId == selectedMesh.MeshId)))
            {
                result.AddDiagnostic("Mesh data is not part of the selected BlendShare patch.");
                return result;
            }

            string groupName = string.IsNullOrWhiteSpace(patch.PatchId) ? patch.name : patch.PatchId;
            var placementObject = new GameObject(CreateUniqueChildName(targetRoot, groupName));
            Undo.RegisterCreatedObjectUndo(placementObject, "Create BlendShare Setup");
            placementObject.transform.SetParent(targetRoot, false);

            var renderersByPath = targetRoot
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(renderer => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var handles = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(patch))
                .OfType<BlendShareMeshAsset>().ToDictionary(handle => handle.MeshId);
            var desiredMeshes = document.Meshes.Where(mesh => selectedMesh == null || mesh.MeshId == selectedMesh.MeshId)
                .GroupBy(mesh => MeshNodePath.Normalize(mesh.NodePath)).Select(group => group.First());

            foreach (var meshData in desiredMeshes)
            {
                string path = MeshNodePath.Normalize(meshData.NodePath);
                var renderer = targetRenderer;
                if (renderer == null && !renderersByPath.TryGetValue(path, out renderer))
                {
                    result.AddDiagnostic($"Renderer path '{path}' was not found under '{targetRoot.name}'.");
                    continue;
                }

                if (renderer == null ||
                    (renderer.transform != targetRoot && !renderer.transform.IsChildOf(targetRoot)))
                {
                    result.AddDiagnostic($"Target renderer '{renderer?.name ?? "<missing>"}' is not under '{targetRoot.name}'.");
                    continue;
                }

                var applier = CreateMeshApplier(placementObject.transform, path, renderer.name);

                Undo.RecordObject(applier, "Create BlendShare Mesh Binding");
                applier.Patch = patch;
                applier.TargetRenderer = renderer;
                if (!GetTargetFbx(applier) && renderer.sharedMesh)
                {
                    string assetPath = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                    if (assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        applier.TargetFbxOverride = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                }
                applier.MeshId = meshData.MeshId;
                handles.TryGetValue(meshData.MeshId, out var handle);
                applier.MeshAsset = handle;
                applier.RendererNodePath = path;
                applier.SyncActiveBlendShapeWeights();
                EditorUtility.SetDirty(applier);
                result.AddMeshApplier(applier);
            }

            if (result.MeshAppliers.Count == 0)
            {
                Undo.DestroyObjectImmediate(placementObject);
                return result;
            }

            var proxyResult = RebuildBoneProxies(result.MeshAppliers, targetRoot, placementObject.transform);
            foreach (string diagnostic in proxyResult.Diagnostics)
            {
                result.AddDiagnostic(diagnostic);
            }
            foreach (var proxy in proxyResult.BoneProxies)
            {
                result.AddBoneProxy(proxy);
            }

            return result;
        }

        public static Transform ResolveTargetRoot(SkinnedMeshRenderer renderer, BlendShareMeshAsset meshData)
        {
            var header = GetMesh(meshData);
            if (renderer == null || header == null)
            {
                return null;
            }

            string expectedPath = MeshNodePath.Normalize(header.NodePath);
            for (Transform candidate = renderer.transform; candidate != null; candidate = candidate.parent)
            {
                if (MeshNodePath.Normalize(MeshNodePath.GetRelativePath(renderer.transform, candidate)) == expectedPath)
                {
                    return candidate;
                }
            }

            return null;
        }

        public static RebuildBoneProxiesResult RebuildBoneProxies(
            IEnumerable<BlendShareMesh> meshAppliers,
            Transform targetRoot,
            Transform placementParent = null)
        {
            var result = new RebuildBoneProxiesResult();
            if (targetRoot == null)
            {
                result.AddDiagnostic("Target root is missing.");
                return result;
            }

            var appliers = (meshAppliers ?? Array.Empty<BlendShareMesh>())
                .Where(applier => applier != null && applier.isActiveAndEnabled)
                .Distinct()
                .ToArray();
            var transformsByPath = targetRoot
                .GetComponentsInChildren<Transform>(true)
                .GroupBy(transform => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(transform, targetRoot)))
                .ToDictionary(group => group.Key, group => group.First());
            var existingProxies = targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true)
                .Where(proxy => proxy != null)
                .ToList();
            var existingBindingsBySource = new Dictionary<(BlendShareAsset, string), ResolvedBinding>();
            bool proxyResolutionFailed = false;
            foreach (var patch in appliers
                         .Select(applier => applier.ResolvedPatch)
                         .Where(patch => patch != null)
                         .Distinct())
            {
                var resolution = BlendShareBoneProxyResolver.Resolve(patch, existingProxies);
                if (!resolution.Success)
                {
                    proxyResolutionFailed = true;
                    foreach (string diagnostic in resolution.Diagnostics)
                    {
                        result.AddDiagnostic(diagnostic);
                    }
                    continue;
                }

                foreach (var pair in resolution.BindingsByPath)
                {
                    existingBindingsBySource[(patch, pair.Key)] =
                        new ResolvedBinding(pair.Value.Proxy, pair.Value.Binding);
                }
            }
            if (proxyResolutionFailed)
            {
                return result;
            }

            var usedProxies = new HashSet<BlendShareBoneProxy>();
            var proxiesByBonePath = new Dictionary<(BlendShareAsset, string), ResolvedBinding>();
            var boneProxyPlacementParent = placementParent != null ? placementParent : targetRoot;
            Transform boneProxyContainer = FindBoneProxyContainer(boneProxyPlacementParent);
            MoveBoneProxyContainerFirst(boneProxyContainer);

            foreach (var meshApplier in appliers)
            {
                var renderer = meshApplier.TargetRenderer;
                if (renderer == null)
                {
                    continue;
                }

                var skin = GetSkin(meshApplier);
                if (skin?.Skeleton == null)
                {
                    continue;
                }

                var patch = meshApplier.ResolvedPatch;
                if (patch == null)
                {
                    continue;
                }

                ResolvedVertexBinding mapping = null;

                foreach (string bonePath in skin.BonePaths)
                {
                    var bone = skin.Skeleton.Bones.Find(item => item.Path == bonePath);
                    if (bone == null || !bone.CreateIfMissing || !meshApplier.CreateMissingBones || transformsByPath.ContainsKey(bonePath))
                    {
                        continue;
                    }

                    var parent = ResolveProxyParent(bone, skin.Skeleton, patch, targetRoot, transformsByPath, proxiesByBonePath);
                    bool useHierarchyParent = parent != null && proxiesByBonePath.Values.Any(candidate =>
                        candidate.FinalTransform == parent);
                    if (mapping == null && !TryResolveMeshApplierMappingReference(meshApplier, out mapping, true))
                    {
                        result.AddDiagnostic($"Cannot create proxy for bone '{bonePath}': missing FBX-to-Unity conversion.");
                        continue;
                    }

                    if (!mapping.TryConvertLocalTransform(
                            bone.LocalBindMatrix,
                            out var localTransform,
                            out string transformDiagnostic))
                    {
                        result.AddDiagnostic($"Cannot create proxy for bone '{bonePath}': {transformDiagnostic}");
                        continue;
                    }

                    var localPosition = localTransform.Position;
                    var localEulerRotation = localTransform.Rotation.eulerAngles;
                    var localScale = localTransform.Scale;
                    var sourceKey = (patch, bonePath);
                    bool createdProxy = false;
                    BlendShareBoneProxy proxy;
                    BlendShareBoneProxyBinding binding;
                    Transform finalTransform;
                    if (proxiesByBonePath.TryGetValue(sourceKey, out var resolved) ||
                        existingBindingsBySource.TryGetValue(sourceKey, out resolved))
                    {
                        proxy = resolved.Component;
                        binding = resolved.Binding;
                        finalTransform = resolved.FinalTransform;
                    }
                    else
                    {
                        var authoringParent = useHierarchyParent
                            ? parent
                            : (boneProxyContainer ??= GetOrCreateBoneProxyContainer(
                                boneProxyPlacementParent));
                        proxy = CreateBoneProxy(
                            authoringParent,
                            patch,
                            bonePath,
                            useHierarchyParent ? null : parent);
                        binding = proxy.Bindings[0];
                        finalTransform = proxy.transform;
                        createdProxy = true;
                    }

                    if (proxy == null || binding == null || finalTransform == null)
                    {
                        result.AddDiagnostic($"Bone proxy binding '{bonePath}' has no final transform.");
                        continue;
                    }

                    usedProxies.Add(proxy);
                    Undo.RecordObject(proxy, "Rebuild BlendShare Bone Proxy");
                    Undo.RecordObject(finalTransform, "Rebuild BlendShare Bone Proxy");
                    if (useHierarchyParent && finalTransform.parent != parent)
                    {
                        Undo.SetTransformParent(finalTransform, parent, "Rebuild BlendShare Bone Proxy Hierarchy");
                    }
                    proxy.Patch = patch;
                    proxy.UpdateBinding(binding, bonePath, finalTransform);
                    if (proxy.IsRootBinding(binding))
                    {
                        proxy.UseHierarchyParent = useHierarchyParent;
                        proxy.TargetParent = useHierarchyParent ? null : parent;
                    }

                    if (createdProxy)
                    {
                        ApplyBindingLocalTransform(
                            finalTransform,
                            parent,
                            localPosition,
                            Quaternion.Euler(localEulerRotation),
                            localScale);
                    }
                    EditorUtility.SetDirty(proxy);
                    EditorUtility.SetDirty(finalTransform);
                    result.AddBoneProxy(proxy);
                    proxiesByBonePath[sourceKey] = new ResolvedBinding(proxy, binding);
                }
            }

            ConsolidateGeneratedProxySubtrees(proxiesByBonePath.Values, usedProxies, result);
            RefreshBoneProxyCaches(
                appliers,
                targetRoot,
                targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
            return result;
        }

        private static void ConsolidateGeneratedProxySubtrees(
            IEnumerable<ResolvedBinding> resolvedBindings,
            ISet<BlendShareBoneProxy> usedProxies,
            RebuildBoneProxiesResult result)
        {
            var bindings = (resolvedBindings ?? Array.Empty<ResolvedBinding>())
                .Where(resolved => resolved.Component != null && resolved.Binding != null && resolved.FinalTransform != null)
                .ToArray();
            foreach (var patchGroup in bindings.GroupBy(resolved => resolved.Component.Patch))
            {
                var finalTransforms = patchGroup.Select(resolved => resolved.FinalTransform).ToHashSet();
                foreach (var root in patchGroup.Where(resolved => !finalTransforms.Contains(resolved.FinalTransform.parent)))
                {
                    var subtree = patchGroup
                        .Where(resolved => resolved.FinalTransform == root.FinalTransform ||
                                           resolved.FinalTransform.IsChildOf(root.FinalTransform))
                        .OrderBy(resolved => GetHierarchyOrder(resolved.FinalTransform), StringComparer.Ordinal)
                        .ToArray();
                    if (subtree.Length <= 1 || subtree.Any(resolved =>
                            !usedProxies.Contains(resolved.Component) || !IsConvertibleFlatProxy(resolved)))
                    {
                        continue;
                    }

                    var holder = root.Component;
                    Undo.RecordObject(holder, "Consolidate BlendShare Bone Proxy Subtree");
                    holder.SetBindings(subtree.Select(resolved =>
                        new BlendShareBoneProxyBinding(resolved.SourceBonePath, resolved.FinalTransform)));
                    EditorUtility.SetDirty(holder);
                    foreach (var descendant in subtree.Select(resolved => resolved.Component).Distinct())
                    {
                        if (descendant == holder)
                        {
                            continue;
                        }

                        usedProxies.Remove(descendant);
                        result.RemoveBoneProxy(descendant);
                        Undo.DestroyObjectImmediate(descendant);
                    }

                    result.AddBoneProxy(holder);
                }
            }
        }

        private static bool IsConvertibleFlatProxy(ResolvedBinding resolved)
        {
            var component = resolved.Component;
            return component != null &&
                   component.Bindings.Count == 1 &&
                   resolved.FinalTransform == component.transform &&
                   component.gameObject.GetComponents<Component>().All(candidate =>
                       candidate is Transform || candidate == component);
        }

        // A source revision identifies immutable headers. Import settings have their own revision.
        private sealed class HeaderCache
        {
            internal string Revision;
            internal PatchDocument Document;
        }

        private static readonly ConditionalWeakTable<BlendShareAsset, HeaderCache> s_Headers = new();

        private static PatchDocument ReadDocument(BlendShareAsset patch)
        {
            if (!patch) return null;
            var cache = s_Headers.GetOrCreateValue(patch);
            if (cache.Document == null || cache.Revision != patch.SourceRevision)
            {
                cache.Document = BlendShareSourceRepository.Shared.ReadPatchDocument(patch).Value;
                cache.Revision = patch.SourceRevision;
            }
            return cache.Document;
        }

        internal static MeshDocument GetMesh(BlendShareMeshAsset handle) =>
            handle ? ReadDocument(handle.Root)?.Meshes.Find(mesh => mesh.MeshId == handle.MeshId) : null;

        private static MeshDocument GetMesh(BlendShareMesh mesh) =>
            mesh ? ReadDocument(mesh.ResolvedPatch)?.Meshes.Find(item => item.MeshId == mesh.ResolvedMeshId) : null;

        internal static SkeletonDocument GetSkeleton(BlendShareBoneProxy proxy) =>
            proxy ? ReadDocument(proxy.Patch)?.Skeleton : null;

        internal static SkinHeaders GetSkin(BlendShareMesh mesh)
        {
            var feature = GetMesh(mesh)?.Features.OfType<SkinWeightFeature>().FirstOrDefault();
            if (feature == null) return null;
            var skeleton = ReadDocument(mesh.ResolvedPatch)?.Skeleton;
            return skeleton == null ? null : new SkinHeaders(feature, skeleton);
        }

        internal static bool SyncActiveBlendShapeWeights(this BlendShareMesh mesh) =>
            mesh.SyncActiveBlendShapeWeights(GetActiveShapeNames(mesh));

        internal static IReadOnlyList<string> GetActiveShapeNames(BlendShareMesh mesh)
        {
            var header = GetMesh(mesh);
            var settings = EffectiveSettingsResolver.Resolve(mesh.ResolvedPatch).Settings;
            var names = new List<string>();
            foreach (var feature in header?.Features.OfType<BlendShapeFeature>() ?? Enumerable.Empty<BlendShapeFeature>())
            {
                var selection = EffectiveSettingsResolver.FindFeatureSettings(settings, header.MeshId, feature.FeatureId)
                    as BlendShapeFeatureSettings;
                var namesById = feature.Shapes.ToDictionary(shape => shape.ShapeId, shape => shape.Name);
                var ids = feature.Shapes.Select(shape => shape.ShapeId).ToList();
                names.AddRange((selection?.ResolveEffectiveShapeIds(ids) ?? ids)
                    .Select(id => namesById[id]));
            }
            return names.Distinct(StringComparer.Ordinal).ToArray();
        }

        internal static void RefreshBoneProxyCaches(IEnumerable<BlendShareMesh> meshes, Transform targetRoot,
            IEnumerable<BlendShareBoneProxy> proxies)
        {
            foreach (var group in meshes.GroupBy(mesh => mesh != null ? mesh.ResolvedPatch : null))
            {
                var resolution = BlendShareBoneProxyResolver.Resolve(group.Key, proxies);
                foreach (var mesh in group) mesh.SetBoneCache(resolution.BindingsByPath);
            }
        }

        private static GameObject GetTargetFbx(BlendShareMesh mesh)
        {
            if (mesh.TargetFbxOverride) return mesh.TargetFbxOverride;
            return EffectiveSettingsResolver.Resolve(mesh.ResolvedPatch).Settings.DefaultTargetFbx;
        }

        internal static bool HasEmbeddedMapping(BlendShareMesh mesh) =>
            GetMesh(mesh)?.OriginBinding?.HasPayload == true;

        internal static MeshMappingStatus GetMappingStatus(BlendShareMesh mesh)
        {
            var header = GetMesh(mesh);
            var fbx = GetTargetFbx(mesh);
            var cache = GetMappingCache(mesh, header, fbx);
            var language = Triturbo.BlendShapeShare.Localization.CurrentLanguage;
            if (!cache.Status.HasValue || cache.Language != language)
            {
                cache.Status = BlendShareInspectorUtility.GetMappingStatus(fbx, header);
                cache.Language = language;
            }
            return cache.Status.Value;
        }

        private sealed class MappingCache
        {
            internal MeshDocument Header;
            internal Mesh Target;
            internal Mesh Reference;
            internal GameObject Fbx;
            internal ResolvedVertexBinding Binding;
            internal bool TriedHeadersOnly;
            internal MeshMappingStatus? Status;
            internal string Language;
        }

        private static ConditionalWeakTable<BlendShareMesh, MappingCache> s_Mappings = new();

        [InitializeOnLoadMethod]
        private static void InvalidateMappingsOnAssetChange() =>
            EditorApplication.projectChanged += () => s_Mappings = new();

        internal static bool TryResolveMeshApplierMappingReference(BlendShareMesh mesh,
            out ResolvedVertexBinding binding, bool allowPayloadRead = false)
        {
            binding = null;
            var header = GetMesh(mesh);
            var target = mesh && mesh.TargetRenderer ? mesh.TargetRenderer.sharedMesh : null;
            if (header == null || !target) return false;
            var fbx = GetTargetFbx(mesh);
            var cache = GetMappingCache(mesh, header, fbx);
            if (cache.Binding == null && (allowPayloadRead || (fbx && !cache.TriedHeadersOnly)))
            {
                // Passive display can use the FBX mapping path, but must not open an OriginBinding payload.
                var input = allowPayloadRead ? header : new MeshDocument
                {
                    MeshId = header.MeshId, NodePath = header.NodePath, Topology = header.Topology
                };
                var result = BindingService.Shared.Resolve(new BindingResolveRequest
                {
                    MeshHeader = input,
                    SpaceProvenance = BlendShareAssetTocMapper.GetSpaceProvenance(mesh.ResolvedPatch),
                    TargetUnityMesh = target, ReferenceUnityMesh = mesh.ReferenceMesh,
                    TargetFbxAsset = fbx, PayloadSource = BlendShareSourceRepository.Shared.PayloadStore
                });
                cache.Binding = result.Binding;
                cache.TriedHeadersOnly = true;
            }
            binding = cache.Binding;
            return binding != null;
        }

        private static MappingCache GetMappingCache(BlendShareMesh mesh, MeshDocument header, GameObject fbx)
        {
            var target = mesh.TargetRenderer ? mesh.TargetRenderer.sharedMesh : null;
            var cache = s_Mappings.GetOrCreateValue(mesh);
            if (cache.Header != header || cache.Target != target || cache.Fbx != fbx || cache.Reference != mesh.ReferenceMesh)
            {
                cache.Header = header;
                cache.Target = target;
                cache.Reference = mesh.ReferenceMesh;
                cache.Fbx = fbx;
                cache.Status = null;
                cache.Binding = null;
                cache.TriedHeadersOnly = false;
            }
            return cache;
        }

        internal static bool TryConvertLocalTransform(this ResolvedVertexBinding binding, FbxMatrix4x4 local,
            out UnityLocalTransform transform, out string diagnostic)
        {
            transform = default;
            diagnostic = null;
            if (!ArmatureBindTransformMath.ConvertCanonicalMatrixToUnity(local, binding.SpaceContract.StoredToUnityMeshLocal,
                    out var matrix) || !ArmatureBindTransformMath.TryDecomposeUnityLocalTransform(matrix,
                    out var position, out var rotation, out var scale, out diagnostic))
            {
                diagnostic = "Cannot decompose the source bone transform.";
                return false;
            }
            transform = new UnityLocalTransform(position, rotation, scale);
            return true;
        }

        private static BlendShareMesh CreateMeshApplier(
            Transform placementParent,
            string rendererPath,
            string rendererName)
        {
            string desiredName = rendererPath == MeshNodePath.Root
                ? rendererName
                : MeshNodePath.LeafName(rendererPath);
            var host = new GameObject(CreateUniqueChildName(placementParent, desiredName));
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Mesh Binding");
            host.transform.SetParent(placementParent, false);
            return Undo.AddComponent<BlendShareMesh>(host);
        }

        private static BlendShareBoneProxy CreateBoneProxy(
            Transform placementParent,
            BlendShareAsset sourcePatch,
            string sourceBonePath,
            Transform parent)
        {
            string desiredName = MeshNodePath.LeafName(sourceBonePath);
            var host = new GameObject(string.IsNullOrWhiteSpace(desiredName) ? "BlendShareBone" : desiredName);
            Undo.RegisterCreatedObjectUndo(host, "Create BlendShare Bone Proxy");
            host.transform.SetParent(placementParent, false);
            var proxy = Undo.AddComponent<BlendShareBoneProxy>(host);
            InitializeSingleBindingProxy(proxy, sourcePatch, sourceBonePath);
            proxy.TargetParent = parent;
            return proxy;
        }

        private static void InitializeSingleBindingProxy(
            BlendShareBoneProxy proxy,
            BlendShareAsset sourcePatch,
            string sourceBonePath)
        {
            proxy.Patch = sourcePatch;
            proxy.SetBindings(new[]
            {
                new BlendShareBoneProxyBinding(sourceBonePath, proxy.transform)
            });
        }

        private static void ApplyBindingLocalTransform(
            Transform finalTransform,
            Transform effectiveParent,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale)
        {
            if (finalTransform == null || effectiveParent == null)
            {
                return;
            }

            if (finalTransform.parent == effectiveParent)
            {
                finalTransform.localPosition = localPosition;
                finalTransform.localRotation = localRotation;
                finalTransform.localScale = localScale;
                return;
            }

            var worldScale = Vector3.Scale(effectiveParent.lossyScale, localScale);
            finalTransform.SetPositionAndRotation(
                effectiveParent.TransformPoint(localPosition),
                effectiveParent.rotation * localRotation);
            finalTransform.localScale = DivideScale(worldScale, finalTransform.parent);
        }

        private static Vector3 DivideScale(Vector3 worldScale, Transform parent)
        {
            var parentScale = parent != null ? parent.lossyScale : Vector3.one;
            return new Vector3(
                SafeDivide(worldScale.x, parentScale.x),
                SafeDivide(worldScale.y, parentScale.y),
                SafeDivide(worldScale.z, parentScale.z));
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) > 0.000001f ? value / divisor : value;
        }

        private static Transform GetOrCreateBoneProxyContainer(Transform placementParent)
        {
            var existing = FindBoneProxyContainer(placementParent);
            if (existing != null)
            {
                MoveBoneProxyContainerFirst(existing);
                return existing;
            }

            var container = new GameObject(k_BoneProxyContainerName);
            Undo.RegisterCreatedObjectUndo(container, "Create BlendShare Bone Proxy Container");
            container.transform.SetParent(placementParent, false);
            container.transform.SetAsFirstSibling();
            return container.transform;
        }

        private static Transform FindBoneProxyContainer(Transform placementParent)
        {
            return Enumerable.Range(0, placementParent.childCount)
                .Select(placementParent.GetChild)
                .FirstOrDefault(child =>
                    child.name == k_BoneProxyContainerName &&
                    child.gameObject.GetComponents<Component>().All(component => component is Transform) &&
                    (child.childCount == 0 || child.GetComponentInChildren<BlendShareBoneProxy>(true) != null));
        }

        private static void MoveBoneProxyContainerFirst(Transform container)
        {
            if (container != null && container.GetSiblingIndex() != 0)
            {
                Undo.RecordObject(container, "Move BlendShare Bone Proxy Container");
                container.SetAsFirstSibling();
                EditorUtility.SetDirty(container);
            }
        }

        private static Transform ResolveProxyParent(
            SkeletonBoneRecord bone,
            SkeletonDocument skeleton,
            BlendShareAsset patch,
            Transform targetRoot,
            IReadOnlyDictionary<string, Transform> transformsByPath,
            IReadOnlyDictionary<(BlendShareAsset, string), ResolvedBinding> proxiesByBonePath)
        {
            string parentPath = bone?.ParentPath ?? MeshNodePath.Root;
            var visited = new HashSet<string>();
            while (!string.IsNullOrEmpty(parentPath) && parentPath != MeshNodePath.Root && visited.Add(parentPath))
            {
                if (proxiesByBonePath.TryGetValue(
                        (patch, parentPath),
                        out var proxyParent) && proxyParent.FinalTransform != null)
                {
                    return proxyParent.FinalTransform;
                }

                if (transformsByPath.TryGetValue(parentPath, out var parent) && parent != null)
                {
                    return parent;
                }

                var parentBone = skeleton.Bones.Find(item => item.Path == parentPath);
                if (parentBone == null)
                {
                    break;
                }

                parentPath = parentBone.ParentPath;
            }

            return targetRoot;
        }

        private readonly struct ResolvedBinding
        {
            public ResolvedBinding(BlendShareBoneProxy component, BlendShareBoneProxyBinding binding)
            {
                Component = component;
                Binding = binding;
            }

            public BlendShareBoneProxy Component { get; }
            public BlendShareBoneProxyBinding Binding { get; }
            public Transform FinalTransform => Binding?.Transform;
            public string SourceBonePath => Binding?.SourceBonePath;
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

        private static string CreateUniqueChildName(Transform parent, string desiredName)
        {
            desiredName = string.IsNullOrWhiteSpace(desiredName) || desiredName == MeshNodePath.Root
                ? "BlendShareBone"
                : desiredName;
            if (parent == null || parent.Find(desiredName) == null)
            {
                return desiredName;
            }

            string baseName = $"{desiredName} BlendShare";
            if (parent.Find(baseName) == null)
            {
                return baseName;
            }

            for (int i = 1; i < 10000; i++)
            {
                string candidate = $"{baseName} {i}";
                if (parent.Find(candidate) == null)
                {
                    return candidate;
                }
            }

            return $"{baseName} {Guid.NewGuid():N}";
        }
    }

    internal readonly struct UnityLocalTransform
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public void ApplyTo(Transform target)
        {
            target.localPosition = Position;
            target.localRotation = Rotation;
            target.localScale = Scale;
        }

        public UnityLocalTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    internal sealed class SkinHeaders
    {
        internal SkeletonDocument Skeleton { get; }
        internal string[] BonePaths { get; }

        internal SkinHeaders(SkinWeightFeature feature, SkeletonDocument skeleton)
        {
            Skeleton = skeleton;
            var weighted = feature.Clusters.Select(cluster => cluster.BonePath).Distinct().ToArray();
            var needed = new HashSet<string>(weighted, StringComparer.Ordinal);
            foreach (var bone in skeleton.Bones)
            {
                if (bone != null && bone.CreateIfMissing && !string.IsNullOrEmpty(bone.Path))
                {
                    needed.Add(bone.Path);
                }
            }

            BonePaths = skeleton.Bones.OrderBy(bone => bone.Path.Count(character => character == '/'))
                .Select(bone => bone.Path).Where(needed.Remove).Concat(weighted.Where(needed.Contains)).ToArray();
        }
    }

    internal sealed class CreateBlendShareSetupResult
    {
        private readonly List<string> m_Diagnostics = new();
        private readonly List<BlendShareMesh> m_MeshAppliers = new();
        private readonly List<BlendShareBoneProxy> m_BoneProxies = new();

        public IReadOnlyList<string> Diagnostics => m_Diagnostics;
        public IReadOnlyList<BlendShareMesh> MeshAppliers => m_MeshAppliers;
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies => m_BoneProxies;
        public bool Success => m_Diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                m_Diagnostics.Add(diagnostic);
            }
        }

        internal void AddMeshApplier(BlendShareMesh applier)
        {
            if (applier != null && !m_MeshAppliers.Contains(applier))
            {
                m_MeshAppliers.Add(applier);
            }
        }

        internal void AddBoneProxy(BlendShareBoneProxy proxy)
        {
            if (proxy != null && !m_BoneProxies.Contains(proxy))
            {
                m_BoneProxies.Add(proxy);
            }
        }

    }

    internal sealed class RebuildBoneProxiesResult
    {
        private readonly List<string> m_Diagnostics = new();
        private readonly List<BlendShareBoneProxy> m_BoneProxies = new();

        public IReadOnlyList<string> Diagnostics => m_Diagnostics;
        public IReadOnlyList<BlendShareBoneProxy> BoneProxies => m_BoneProxies;
        public bool Success => m_Diagnostics.Count == 0;

        internal void AddDiagnostic(string diagnostic)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                m_Diagnostics.Add(diagnostic);
            }
        }

        internal void AddBoneProxy(BlendShareBoneProxy proxy)
        {
            if (proxy != null && !m_BoneProxies.Contains(proxy))
            {
                m_BoneProxies.Add(proxy);
            }
        }

        internal void RemoveBoneProxy(BlendShareBoneProxy proxy)
        {
            m_BoneProxies.Remove(proxy);
        }
    }
    /// <summary>
    /// Mesh identity helpers. BlendShare stores FBX node paths only;
    /// an empty/root path is represented by ".".
    /// </summary>
    internal static class MeshNodePath
    {
        public const string Root = ".";

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == Root)
            {
                return Root;
            }

            string normalized = string.Join(
                "/",
                path.Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0 && part != Root));

            return string.IsNullOrEmpty(normalized) ? Root : normalized;
        }

        /// <summary>Normalizes an optional node path, returning an empty string only when it is unset or whitespace.</summary>
        public static string NormalizeOptional(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Normalize(path);
        }

        public static string LeafName(string path)
        {
            string normalized = Normalize(path);
            if (normalized == Root)
            {
                return Root;
            }

            int separator = normalized.LastIndexOf('/');
            return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
        }

        public static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
            {
                return Root;
            }

            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return current == root
                ? Normalize(string.Join("/", parts))
                : Root;
        }

    }
}
