using System.Collections.Generic;
using System.Linq;
using System.Text;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Persistence;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NDMF
{
    [DependsOnContext(typeof(AnimatorServicesContext))]
    internal sealed class BlendShareNdmfPass : nadena.dev.ndmf.Pass<BlendShareNdmfPass>
    {
        public override string QualifiedName => "com.triturbo.blendshare.apply";
        public override string DisplayName => "Apply BlendShare";

        protected override void Execute(nadena.dev.ndmf.BuildContext context)
        {
            var appliers = context.AvatarRootObject.GetComponentsInChildren<BlendShareComponent>(true);
            if (appliers.Length == 0)
            {
                return;
            }

            var meshAppliers = context.AvatarRootObject.GetComponentsInChildren<BlendShareMeshComponent>(true);
            var boneProxies = context.AvatarRootObject.GetComponentsInChildren<BlendShareBoneProxyComponent>(true);
            AnimatorServicesContext animatorServices;
            ObjectPathRemapper pathRemapper;
            try
            {
                animatorServices = context.Extension<AnimatorServicesContext>();
                pathRemapper = animatorServices.ObjectPathRemapper;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BlendShare NDMF] AnimatorServicesContext is required for BlendShare proxy bone retargeting: {ex.Message}", context.AvatarRootObject);
                return;
            }

            foreach (var proxy in boneProxies.Where(proxy => proxy != null && proxy.Owner != null && proxy.Owner.enabled))
            {
                PlaceProxyInBuildHierarchy(proxy, context.AvatarRootTransform, pathRemapper);
            }

            var validMeshAppliers = new List<BlendShareMeshComponent>();
            foreach (var applier in meshAppliers.Where(applier => applier != null && applier.EnabledForBuild && applier.Owner != null && applier.Owner.enabled))
            {
                if (!BlendShareComponentSetupService.ValidateMeshApplierForBuild(applier, out string diagnostic))
                {
                    Debug.LogError($"[BlendShare NDMF] {diagnostic}", applier);
                    continue;
                }

                if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out _) &&
                    !BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string mappingDiagnostic))
                {
                    ReportMappingFailure(applier, mappingDiagnostic);
                    continue;
                }

                applier.SyncActiveBlendShapeWeights();
                BlendShareComponentSetupService.PrepareMeshApplierGenerationMappings(applier);
                validMeshAppliers.Add(applier);
            }

            if (context.ErrorReport.Errors.Any(error => error.TheError is BlendShareNdmfError))
            {
                return;
            }

            var orderedMeshAppliers = validMeshAppliers
                .OrderBy(applier => GetHierarchyOrder(applier.Owner.transform))
                .ThenBy(applier => GetHierarchyOrder(applier.transform))
                .ToArray();

            foreach (var rootGroup in orderedMeshAppliers.GroupBy(applier =>
                         BlendShareComponentSetupService.ResolveTargetRoot(applier.Owner) ?? context.AvatarRootTransform))
            {
                var targetRoot = rootGroup.Key;
                if (targetRoot == null)
                {
                    continue;
                }

                var rootAppliers = rootGroup.ToArray();
                var owners = rootAppliers
                    .Select(applier => applier.Owner)
                    .Where(owner => owner != null)
                    .Distinct()
                    .ToArray();
                var generationComponents = owners.Cast<BlendShareGenerationComponent>()
                    .Concat(rootAppliers)
                    .Concat(boneProxies.Where(proxy => proxy != null && owners.Contains(proxy.Owner)))
                    .Distinct()
                    .ToArray();
                var artifact = BlendShareArtifactService.CreateInMemoryArtifact(
                    targetRoot.gameObject,
                    generationComponents);
                if (artifact == null)
                {
                    Debug.LogError($"[BlendShare NDMF] Failed to generate BlendShare artifact for '{targetRoot.name}'.", targetRoot);
                    continue;
                }

                var result = BlendShareArtifactService.ApplyArtifact(
                    artifact,
                    targetRoot,
                    new BlendShareArtifactApplyOptions
                    {
                        UseUndo = false,
                        RecordDestructiveMarkers = false,
                        MarkObjectsDirty = false,
                        SaveGeneratedMesh = mesh => context.AssetSaver.SaveAsset(mesh)
                    });
                if (!result.Success)
                {
                    foreach (string diagnostic in result.Diagnostics)
                    {
                        Debug.LogError($"[BlendShare NDMF] {diagnostic}", targetRoot);
                    }
                    continue;
                }

                foreach (var applier in rootAppliers)
                {
                    BlendShareBlendShapeWeightService.ApplyWeightsToRenderer(applier, applier.TargetRenderer);
                }

                BlendShareBlendShapeWeightService.RetargetRendererBlendShapeCurves(rootAppliers, animatorServices);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareMeshComponent>(true))
            {
                Object.DestroyImmediate(component);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareBoneProxyComponent>(true))
            {
                Object.DestroyImmediate(component);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareComponent>(true))
            {
                Object.DestroyImmediate(component);
            }
        }

        private static void ReportMappingFailure(BlendShareMeshComponent applier, string diagnostic)
        {
            var details = BuildMappingFailureDetails(applier, diagnostic);
            ErrorReport.ReportError(new BlendShareNdmfError(
                "BlendShare mapping recovery failed.",
                details,
                applier));
            Debug.LogError($"[BlendShare NDMF] {details}", applier);
        }

        private static string BuildMappingFailureDetails(BlendShareMeshComponent applier, string diagnostic)
        {
            string rendererPath = applier?.TargetRenderer != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(
                    applier.TargetRenderer.transform,
                    BlendShareComponentSetupService.ResolveTargetRoot(applier.Owner)))
                : applier != null ? applier.RendererNodePath : MeshNodePath.Root;
            var builder = new StringBuilder();
            builder.AppendLine(string.IsNullOrWhiteSpace(diagnostic)
                ? "BlendShare could not create a compatible Unity vertex mapping."
                : diagnostic);
            builder.AppendLine($"Renderer path: {rendererPath}");

            bool addedPair = false;
            foreach (var share in applier?.Owner?.BlendShares ?? System.Array.Empty<BlendShareObject>())
            {
                if (share == null || applier?.MeshData == null ||
                    !(share.Meshes ?? System.Array.Empty<MeshDataObject>()).Contains(applier.MeshData))
                {
                    continue;
                }

                builder.AppendLine($"BlendShare asset: {share.name}");
                builder.AppendLine($"Mesh path: {applier.MeshData.m_Path}");
                addedPair = true;
                break;
            }

            if (!addedPair)
            {
                builder.AppendLine("BlendShare asset: <not resolved>");
                builder.AppendLine("Mesh path: <not resolved>");
            }

            return builder.ToString().TrimEnd();
        }

        private sealed class BlendShareNdmfError : IError
        {
            private readonly string title;
            private readonly string details;
            private readonly List<ObjectReference> references = new();

            public BlendShareNdmfError(string title, string details, Object context)
            {
                this.title = title;
                this.details = details;
                if (context != null)
                {
                    references.Add(ObjectRegistry.GetReference(context));
                }
            }

            public ErrorSeverity Severity => ErrorSeverity.Error;

            public UnityEngine.UIElements.VisualElement CreateVisualElement(ErrorReport report)
            {
                var container = new UnityEngine.UIElements.VisualElement();
                container.Add(new UnityEngine.UIElements.Label(title));
                container.Add(new UnityEngine.UIElements.Label(details));
                return container;
            }

            public string ToMessage()
            {
                return $"{title}\n\n{details}";
            }

            public void AddReference(ObjectReference obj)
            {
                if (obj != null && !references.Contains(obj))
                {
                    references.Add(obj);
                }
            }
        }

        private static void PlaceProxyInBuildHierarchy(
            BlendShareBoneProxyComponent proxy,
            Transform fallbackRoot,
            ObjectPathRemapper pathRemapper)
        {
            if (proxy == null)
            {
                return;
            }

            pathRemapper?.RecordObjectTree(proxy.transform);
            pathRemapper?.GetVirtualPathForObject(proxy.gameObject);

            var parent = proxy.TargetParent != null
                ? proxy.TargetParent
                : proxy.transform.parent;
            if (parent == null)
            {
                parent = fallbackRoot;
            }
            else if (fallbackRoot != null && parent != fallbackRoot && !parent.IsChildOf(fallbackRoot))
            {
                parent = fallbackRoot;
            }

            proxy.transform.SetParent(parent, true);
            pathRemapper?.ClearCache();
        }

        private static string GetHierarchyOrder(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var indices = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                indices.Push(current.GetSiblingIndex().ToString("D8"));
                current = current.parent;
            }

            return string.Join("/", indices);
        }
    }
}
