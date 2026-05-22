using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
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

            foreach (var group in meshAppliers
                         .Where(applier => applier != null && applier.EnabledForBuild && applier.Owner != null && applier.TargetRenderer != null)
                         .OrderBy(applier => GetHierarchyOrder(applier.transform))
                         .GroupBy(applier => applier.TargetRenderer))
            {
                var renderer = group.Key;
                var ordered = group.ToArray();
                var owners = ordered.Select(applier => applier.Owner).Distinct().ToArray();
                var request = new BlendShareSkinBindingProcessor.Request
                {
                    Renderer = renderer,
                    TargetRoot = owners.FirstOrDefault(owner => owner != null && owner.TargetRoot != null)?.TargetRoot ?? context.AvatarRootTransform,
                    MeshAppliers = ordered,
                    BoneProxies = boneProxies.Where(proxy => proxy != null && owners.Contains(proxy.Owner)).ToArray(),
                    CreateBone = (path, bone) => CreateBuildBone(context.AvatarRootTransform, path, bone)
                };

                var result = BlendShareSkinBindingProcessor.Process(request);
                if (!result.Success)
                {
                    foreach (string diagnostic in result.Diagnostics)
                    {
                        Debug.LogError($"[BlendShare NDMF] {diagnostic}", renderer);
                    }

                    continue;
                }

                context.AssetSaver.SaveAsset(result.Mesh);
                renderer.sharedMesh = result.Mesh;
                renderer.rootBone = result.RootBone;
                renderer.bones = result.Bones;
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

        private static Transform CreateBuildBone(Transform targetRoot, string path, BoneNodeData bone)
        {
            string normalized = MeshNodePath.Normalize(path);
            string parentPath = MeshNodePath.Normalize(bone.m_ParentPath);
            var parent = MeshNodePath.FindRelativeTransform(targetRoot, parentPath) ?? targetRoot;
            var created = new GameObject(MeshNodePath.LeafName(normalized));
            created.transform.SetParent(parent, false);
            created.transform.localPosition = bone.m_FbxLocalTranslation;
            created.transform.localRotation = Quaternion.Euler(bone.m_FbxLocalEulerRotation);
            created.transform.localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
            return created.transform;
        }

        private static void PlaceProxyInBuildHierarchy(BlendShareBoneProxyComponent proxy, Transform fallbackRoot)
        {
            if (proxy == null)
            {
                return;
            }

            var parent = proxy.TargetParent != null
                ? proxy.TargetParent
                : MeshNodePath.FindRelativeTransform(fallbackRoot, proxy.TargetParentPath);
            if (parent == null)
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
