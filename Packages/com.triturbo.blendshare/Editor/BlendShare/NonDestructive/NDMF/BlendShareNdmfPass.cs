using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Persistence;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.NonDestructive.NDMF
{
    internal sealed class BlendShareNdmfPass : nadena.dev.ndmf.Pass<BlendShareNdmfPass>
    {
        public override string QualifiedName => "com.triturbo.blendshare.apply";
        public override string DisplayName => "Apply BlendShare";

        protected override void Execute(nadena.dev.ndmf.BuildContext context)
        {
            var appliers = context.AvatarRootObject.GetComponentsInChildren<BlendShareApplierComponent>(true);
            if (appliers.Length == 0)
            {
                return;
            }

            var meshAppliers = context.AvatarRootObject.GetComponentsInChildren<BlendShareMeshApplierComponent>(true);
            var boneProxies = context.AvatarRootObject.GetComponentsInChildren<BlendShareBoneProxyComponent>(true);
            foreach (var proxy in boneProxies)
            {
                PlaceProxyInBuildHierarchy(proxy, context.AvatarRootTransform);
            }

            var validMeshAppliers = new List<BlendShareMeshApplierComponent>();
            foreach (var applier in meshAppliers.Where(applier => applier != null && applier.EnabledForBuild))
            {
                if (!BlendShareApplierSetupService.ValidateMeshApplierForBuild(applier, out string diagnostic))
                {
                    Debug.LogError($"[BlendShare NDMF] {diagnostic}", applier);
                    continue;
                }

                validMeshAppliers.Add(applier);
            }

            var orderedMeshAppliers = validMeshAppliers
                .OrderBy(applier => GetHierarchyOrder(applier.Owner.transform))
                .ThenBy(applier => GetHierarchyOrder(applier.transform))
                .ToArray();

            foreach (var ownerGroup in orderedMeshAppliers.GroupBy(applier => applier.Owner))
            {
                var owner = ownerGroup.Key;
                if (owner == null)
                {
                    continue;
                }

                var targetRoot = owner.TargetRoot != null ? owner.TargetRoot : context.AvatarRootTransform;
                var enabledRendererPaths = new HashSet<string>(ownerGroup
                    .Select(applier => applier.RendererNodePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path)));
                var artifact = BlendShareArtifactService.CreateInMemoryArtifact(
                    targetRoot.gameObject,
                    owner.BlendShares,
                    (share, meshData) => meshData != null && enabledRendererPaths.Contains(MeshNodePath.Normalize(meshData.m_Path)));
                if (artifact == null)
                {
                    Debug.LogError($"[BlendShare NDMF] Failed to generate BlendShare artifact for '{owner.name}'.", owner);
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
                        Debug.LogError($"[BlendShare NDMF] {diagnostic}", owner);
                    }
                }
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareMeshApplierComponent>(true))
            {
                Object.DestroyImmediate(component);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareBoneProxyComponent>(true))
            {
                Object.DestroyImmediate(component);
            }

            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<BlendShareApplierComponent>(true))
            {
                Object.DestroyImmediate(component);
            }
        }

        private static void PlaceProxyInBuildHierarchy(BlendShareBoneProxyComponent proxy, Transform fallbackRoot)
        {
            if (proxy == null)
            {
                return;
            }

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

            proxy.transform.SetParent(parent, false);
            proxy.transform.localPosition = proxy.LocalPosition;
            proxy.transform.localRotation = Quaternion.Euler(proxy.LocalEulerRotation);
            proxy.transform.localScale = proxy.LocalScale;
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
