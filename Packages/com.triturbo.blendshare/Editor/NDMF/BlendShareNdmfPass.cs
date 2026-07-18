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
            // Later hierarchy entries are applied last and therefore win feature-name collisions.
            var meshAppliers = BlendSharePatchIdUtility.DeduplicateMeshComponents(
                    context.AvatarRootObject.GetComponentsInChildren<BlendShareMesh>(true)
                        .Where(applier => applier != null && applier.isActiveAndEnabled))
                .ToArray();
            if (meshAppliers.Length == 0)
            {
                return;
            }

            AnimatorServicesContext animatorServices;
            ObjectPathRemapper pathRemapper;
            try
            {
                animatorServices = context.Extension<AnimatorServicesContext>();
                pathRemapper = animatorServices.ObjectPathRemapper;
            }
            catch (System.Exception ex)
            {
                ReportFailure(
                    "BlendShare could not access animator services.",
                    $"AnimatorServicesContext is required for proxy bone retargeting: {ex.Message}",
                    context.AvatarRootObject);
                return;
            }

            var validMeshAppliers = new List<BlendShareMesh>();
            bool validationFailed = false;
            foreach (var applier in meshAppliers)
            {
                if (!BlendShareComponentSetupService.ValidateMeshApplierForBuild(applier, out string diagnostic))
                {
                    ReportFailure("BlendShare mesh component is invalid.", diagnostic, applier);
                    validationFailed = true;
                    continue;
                }

                if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out _) &&
                    !BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string mappingDiagnostic))
                {
                    ReportMappingFailure(applier, mappingDiagnostic, context.AvatarRootTransform);
                    validationFailed = true;
                    continue;
                }

                applier.SyncActiveBlendShapeWeights();
                validMeshAppliers.Add(applier);
            }

            if (validationFailed)
            {
                return;
            }

            var availableBoneProxies = context.AvatarRootObject
                .GetComponentsInChildren<BlendShareBoneProxy>(true);
            if (!BlendShareComponentSetupService.TryResolveRequiredBoneProxies(
                    validMeshAppliers,
                    availableBoneProxies,
                    context.AvatarRootTransform,
                    out var usedBoneProxies,
                    out string proxyDiagnostic,
                    out var conflictingProxy))
            {
                ReportFailure("BlendShare bone proxy paths conflict.", proxyDiagnostic, conflictingProxy);
                return;
            }

            var usedBoneProxySet = usedBoneProxies.ToHashSet();
            foreach (var proxy in usedBoneProxies
                         .GroupBy(proxy => BlendShareComponentSetupService.GetFinalProxyPath(
                             proxy,
                             context.AvatarRootTransform))
                         .Select(group => group.First()))
            {
                PlaceProxyInBuildHierarchy(proxy, context.AvatarRootTransform, pathRemapper);
            }

            if (validMeshAppliers.Count > 0)
            {
                // Mesh replacement can reorder blendshape indices, so preserve existing controls by name.
                var originalBlendShapeWeights =
                    BlendShareBlendShapeWeightService.CaptureExistingRendererWeights(validMeshAppliers);
                var generationComponents = validMeshAppliers.Cast<BlendShareComponent>()
                    .Concat(usedBoneProxies)
                    .Distinct()
                    .ToArray();
                var artifact = BlendShareArtifactService.CreateInMemoryArtifact(
                    context.AvatarRootObject,
                    generationComponents,
                    out string generationDiagnostic);
                if (artifact == null)
                {
                    ReportFailure(
                        "BlendShare artifact generation failed.",
                        string.IsNullOrWhiteSpace(generationDiagnostic)
                            ? $"No artifact was generated for avatar '{context.AvatarRootObject.name}'."
                            : generationDiagnostic,
                        context.AvatarRootObject);
                    return;
                }

                var result = BlendShareArtifactService.ApplyArtifact(
                    artifact,
                    context.AvatarRootTransform,
                    new BlendShareArtifactApplyOptions
                    {
                        UseUndo = false,
                        RecordDestructiveMarkers = false,
                        MarkObjectsDirty = false,
                        SaveGeneratedMesh = mesh => context.AssetSaver.SaveAsset(mesh)
                    });
                if (!result.Success)
                {
                    ReportFailure(
                        "BlendShare artifact application failed.",
                        string.Join("\n", result.Diagnostics ?? System.Array.Empty<string>()),
                        context.AvatarRootObject);
                    return;
                }

                foreach (var applier in validMeshAppliers)
                {
                    // Later hierarchy entries win collisions between BlendShare component controls.
                    BlendShareBlendShapeWeightService.ApplyWeightsToRenderer(applier, applier.TargetRenderer);
                }

                foreach (var renderer in validMeshAppliers
                             .Select(applier => applier.TargetRenderer)
                             .Where(renderer => renderer != null)
                             .Distinct())
                {
                    // Existing renderer controls have final authority over same-name patch controls.
                    BlendShareBlendShapeWeightService.ApplyCapturedRendererWeights(
                        originalBlendShapeWeights,
                        renderer);
                }

                BlendShareBlendShapeWeightService.RetargetRendererBlendShapeCurves(validMeshAppliers, animatorServices);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareMesh>(true))
            {
                if (component != null &&
                    IsDedicatedComponentHost(component.gameObject, component))
                {
                    Object.DestroyImmediate(component.gameObject);
                }
                else
                {
                    Object.DestroyImmediate(component);
                }
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareBoneProxy>(true))
            {
                if (component == null)
                {
                    continue;
                }

                if (usedBoneProxySet.Contains(component))
                {
                    // The selected proxy transform is now the build's real bone.
                    Object.DestroyImmediate(component);
                }
                else if (IsDedicatedComponentHost(component.gameObject, component))
                {
                    // Superseded setup groups can contain proxies that were intentionally ignored.
                    Object.DestroyImmediate(component.gameObject);
                }
                else
                {
                    Object.DestroyImmediate(component);
                }
            }

        }

        private static void ReportMappingFailure(
            BlendShareMesh applier,
            string diagnostic,
            Transform avatarRoot)
        {
            var details = BuildMappingFailureDetails(applier, diagnostic, avatarRoot);
            ReportFailure("BlendShare mapping recovery failed.", details, applier);
        }

        private static void ReportFailure(string title, string details, Object context)
        {
            details = string.IsNullOrWhiteSpace(details) ? "No additional diagnostic was provided." : details;
            ErrorReport.ReportError(new BlendShareNdmfError(title, details, context));
            Debug.LogError($"[BlendShare NDMF] {title}\n{details}", context);
        }

        private static string BuildMappingFailureDetails(
            BlendShareMesh applier,
            string diagnostic,
            Transform avatarRoot)
        {
            string rendererPath = applier?.TargetRenderer != null
                ? MeshNodePath.Normalize(MeshNodePath.GetRelativePath(
                    applier.TargetRenderer.transform,
                    avatarRoot))
                : applier != null ? applier.RendererNodePath : MeshNodePath.Root;
            var builder = new StringBuilder();
            builder.AppendLine(string.IsNullOrWhiteSpace(diagnostic)
                ? "BlendShare could not create a compatible Unity vertex mapping."
                : diagnostic);
            builder.AppendLine($"Renderer path: {rendererPath}");

            bool addedPair = false;
            var patch = applier?.Patch;
            if (patch != null && applier?.MeshData != null &&
                (patch.Meshes ?? System.Array.Empty<MeshDataObject>()).Contains(applier.MeshData))
            {
                builder.AppendLine($"BlendShare patch: {patch.name}");
                builder.AppendLine($"Mesh path: {applier.MeshData.m_Path}");
                addedPair = true;
            }

            if (!addedPair)
            {
                builder.AppendLine("BlendShare asset: <not resolved>");
                builder.AppendLine("Mesh path: <not resolved>");
            }

            return builder.ToString().TrimEnd();
        }

        private static bool IsDedicatedComponentHost(GameObject gameObject, Component expectedComponent)
        {
            return gameObject != null && gameObject.GetComponents<Component>().All(component =>
                component is Transform || component == expectedComponent);
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
            BlendShareBoneProxy proxy,
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

    }
}
