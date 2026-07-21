using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Editor;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareBoneProxy))]
    public sealed class BlendShareBoneProxyEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var proxy = (BlendShareBoneProxy)target;
            serializedObject.Update();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.S("ndmf.bone_proxy.title"), EditorStyles.boldLabel);
            if (TryGetOverridingProxy(proxy, out string overridingProxyPath))
            {
                EditorGUILayout.HelpBox(
                    string.Format(
                        Localization.S("ndmf.bone_proxy.duplicate_ignored"),
                        overridingProxyPath),
                    MessageType.Warning);
            }

            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("m_SourceArmature"),
                new GUIContent(Localization.S("ndmf.bone_proxy.source_armature")));
            var bindingsProperty = serializedObject.FindProperty("m_Bindings");
            EditorGUILayout.PropertyField(
                bindingsProperty,
                new GUIContent(Localization.S("ndmf.bone_proxy.bindings")),
                true);
            var useHierarchyParentProperty = serializedObject.FindProperty("m_UseHierarchyParent");
            EditorGUILayout.PropertyField(
                useHierarchyParentProperty,
                new GUIContent(Localization.S("ndmf.bone_proxy.use_hierarchy_parent")));
            using (new EditorGUI.DisabledScope(useHierarchyParentProperty.boolValue))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetParentReference"), new GUIContent(Localization.S("ndmf.bone_proxy.target_parent")));
            }
            if (serializedObject.ApplyModifiedProperties())
            {
                NotifyPreviewInputChanged(proxy);
            }
            DrawBindingValidation(proxy);

            EditorGUILayout.Space();
            if (GUILayout.Button(Localization.S("ndmf.bone_proxy.capture_bindposes_all_meshes")))
            {
                var changedMeshes = CaptureBindPosesInAllUsingMeshes(proxy);
                NotifyPreviewInputChanged(proxy, changedMeshes);
            }

            bool canResetChain = CanResetBindingsFromSource(proxy, out string resetDiagnostic);
            using (new EditorGUI.DisabledScope(!canResetChain))
            {
                if (GUILayout.Button(Localization.S("ndmf.bone_proxy.restore_chain_and_bindposes")))
                {
                    ResetBindingsFromSource(proxy);
                    var changedMeshes = RemoveBindPoseOverridesFromAllUsingMeshes(proxy);
                    NotifyPreviewInputChanged(proxy, changedMeshes);
                }
            }

            if (!canResetChain && !string.IsNullOrWhiteSpace(resetDiagnostic))
            {
                EditorGUILayout.HelpBox(resetDiagnostic, MessageType.Info);
            }
        }

        private static void DrawBindingValidation(BlendShareBoneProxy proxy)
        {
            if (proxy == null)
            {
                return;
            }

            var duplicatePaths = proxy.Bindings
                .Where(binding => binding?.IsConfigured == true)
                .GroupBy(binding => binding.SourceBonePath)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicatePaths.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    string.Format(Localization.S("ndmf.bone_proxy.duplicate_bindings"), string.Join(", ", duplicatePaths)),
                    MessageType.Warning);
            }

            var avatarRoot = proxy.transform.root;
            foreach (var binding in proxy.Bindings)
            {
                if (binding == null || binding.Transform == null)
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.null_final_transform"), MessageType.Error);
                    continue;
                }

                string finalPath = proxy.GetFinalPath(binding, avatarRoot);
                EditorGUILayout.LabelField(
                    binding.IsConfigured ? binding.SourceBonePath : Localization.S("common.none"),
                    finalPath);
                if (binding.Transform != avatarRoot && !binding.Transform.IsChildOf(avatarRoot))
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.final_transform_outside_avatar"), MessageType.Error);
                }

                var parent = proxy.GetEffectiveParent(binding);
                if (parent == binding.Transform || (parent != null && parent.IsChildOf(binding.Transform)))
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.retarget_cycle"), MessageType.Error);
                }
            }
        }

        private static void NotifyPreviewInputChanged(
            BlendShareBoneProxy proxy,
            IEnumerable<BlendShareMesh> changedMeshes = null)
        {
            EditorUtility.SetDirty(proxy);
            BlendShareEditorChangeEvents.NotifyChanged(
                BlendShareEditorChangeKind.Explicit,
                new UnityEngine.Object[] { proxy }
                    .Concat(changedMeshes?.Cast<UnityEngine.Object>() ?? Array.Empty<UnityEngine.Object>())
                    .ToArray());
            FlushNdmfPreviewInvalidatesIfAvailable();
            ForceResetNdmfPreviewIfAvailable();
            SceneView.RepaintAll();
        }

        private static bool TryGetOverridingProxy(
            BlendShareBoneProxy proxy,
            out string overridingProxyPath)
        {
            overridingProxyPath = null;
            if (proxy == null ||
                !proxy.isActiveAndEnabled ||
                proxy.SourceArmature == null ||
                !proxy.Bindings.Any(binding => binding?.IsConfigured == true))
            {
                return false;
            }

            var avatarRoot = proxy.transform.root;
            var overriddenBinding = proxy.Bindings.LastOrDefault(binding =>
                binding?.IsConfigured == true);
            var overridingProxy = avatarRoot.GetComponentsInChildren<BlendShareBoneProxy>(true)
                .Where(candidate => candidate != null &&
                                    candidate.isActiveAndEnabled &&
                                    candidate.SourceArmature == proxy.SourceArmature &&
                                    candidate.TryGetBinding(overriddenBinding?.SourceBonePath, out _))
                .OrderBy(candidate => GetHierarchyOrder(candidate.transform), StringComparer.Ordinal)
                .LastOrDefault();
            if (overriddenBinding == null ||
                overridingProxy == null ||
                overridingProxy == proxy)
            {
                return false;
            }

            overridingProxyPath = MeshNodePath.Normalize(
                MeshNodePath.GetRelativePath(overridingProxy.transform, avatarRoot));
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

        private static void FlushNdmfPreviewInvalidatesIfAvailable()
        {
            var computeContextType = System.Type.GetType("nadena.dev.ndmf.preview.ComputeContext, nadena.dev.ndmf");
            var flushInvalidates = computeContextType?.GetMethod(
                "FlushInvalidates",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null,
                System.Type.EmptyTypes,
                null);
            flushInvalidates?.Invoke(null, null);
        }

        private static void ForceResetNdmfPreviewIfAvailable()
        {
            var previewType = System.Type.GetType("nadena.dev.ndmf.preview.NDMFPreview, nadena.dev.ndmf");
            var forceResetPreview = previewType?.GetMethod(
                "ForceResetPreview",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                System.Type.EmptyTypes,
                null);
            forceResetPreview?.Invoke(null, null);
        }

        private static UnityVertexMappingObject GetFbxToUnityMapping(BlendShareMesh meshApplier)
        {
            return BlendShareComponentSetupService.TryResolveMeshApplierMappingReference(
                meshApplier,
                out var mapping)
                ? mapping
                : null;
        }

        private static bool CanResetBindingsFromSource(
            BlendShareBoneProxy proxy,
            out string diagnostic)
        {
            diagnostic = null;
            if (proxy?.SourceArmature == null)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                return false;
            }

            foreach (var binding in proxy.Bindings)
            {
                if (binding == null ||
                    binding.Transform == null ||
                    !binding.IsConfigured ||
                    proxy.SourceArmature.GetBone(binding.SourceBonePath) == null ||
                    proxy.GetEffectiveParent(binding) == null)
                {
                    diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                    return false;
                }
            }

            return true;
        }

        private static void ResetBindingsFromSource(BlendShareBoneProxy proxy)
        {
            var mapping = proxy.transform.root
                .GetComponentsInChildren<BlendShareMesh>(true)
                .Where(applier => applier?.MeshData?.GetFeature<SkinWeightFeatureObject>()?.Armature == proxy.SourceArmature)
                .Select(GetFbxToUnityMapping)
                .FirstOrDefault(candidate => candidate != null);
            foreach (var binding in proxy.Bindings
                         .Where(binding => binding?.Transform != null)
                         .OrderBy(binding => GetTransformDepth(binding.Transform)))
            {
                var bone = proxy.SourceArmature.GetBone(binding.SourceBonePath);
                var finalTransform = binding.Transform;
                var parent = proxy.GetEffectiveParent(binding);
                if (mapping == null ||
                    !bone.HasTransformData ||
                    !mapping.SpaceConversion.TryConvertLocalTransform(
                        bone.EvaluatedNodeToParentMatrix,
                        out var localTransform,
                        out _))
                {
                    continue;
                }

                var localPosition = localTransform.Position;
                var localRotation = localTransform.Rotation;
                var localScale = localTransform.Scale;

                Undo.RecordObject(finalTransform, "Reset BlendShare Bone Proxy Chain");
                if (parent == finalTransform.parent)
                {
                    finalTransform.localPosition = localPosition;
                    finalTransform.localRotation = localRotation;
                    finalTransform.localScale = localScale;
                }
                else
                {
                    var worldScale = Vector3.Scale(parent.lossyScale, localScale);
                    finalTransform.SetPositionAndRotation(
                        parent.TransformPoint(localPosition),
                        parent.rotation * localRotation);
                    finalTransform.localScale = DivideScale(worldScale, finalTransform.parent);
                }

                EditorUtility.SetDirty(finalTransform);
            }

            Undo.RecordObject(proxy, "Reset BlendShare Bone Proxy Chain");
            EditorUtility.SetDirty(proxy);
        }

        private static IReadOnlyList<BlendShareMesh> CaptureBindPosesInAllUsingMeshes(BlendShareBoneProxy proxy)
        {
            var changed = new List<BlendShareMesh>();
            foreach (var usage in GetUsingMeshes(proxy))
            {
                Undo.RecordObject(usage.Mesh, "Capture BlendShare Bind Poses");
                if (usage.Mesh.CaptureBindPose(usage.Binding.SourceBonePath))
                {
                    EditorUtility.SetDirty(usage.Mesh);
                    changed.Add(usage.Mesh);
                }
            }
            return changed;
        }

        private static IReadOnlyList<BlendShareMesh> RemoveBindPoseOverridesFromAllUsingMeshes(BlendShareBoneProxy proxy)
        {
            var changed = new List<BlendShareMesh>();
            foreach (var usage in GetUsingMeshes(proxy))
            {
                Undo.RecordObject(usage.Mesh, "Restore BlendShare Source Bind Poses");
                if (usage.Mesh.RemoveBindPoseOverride(usage.Binding.SourceBonePath))
                {
                    EditorUtility.SetDirty(usage.Mesh);
                    changed.Add(usage.Mesh);
                }
            }
            return changed;
        }

        private static IEnumerable<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)> GetUsingMeshes(
            BlendShareBoneProxy proxy)
        {
            if (proxy == null)
            {
                yield break;
            }

            var avatarRoot = proxy.transform.root;
            var meshes = avatarRoot.GetComponentsInChildren<BlendShareMesh>(true)
                .Where(mesh => mesh != null && mesh.isActiveAndEnabled)
                .ToArray();
            BlendShareMesh.RefreshBoneProxyCaches(
                meshes,
                avatarRoot,
                avatarRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
            foreach (var mesh in meshes)
            {
                var skin = mesh.MeshData?.GetFeature<SkinWeightFeatureObject>();
                foreach (string sourceBonePath in skin?.GetNeededBonePathsInArmatureOrder() ?? Array.Empty<string>())
                {
                    if (mesh.TryGetCachedBone(sourceBonePath, out var resolved) &&
                        resolved.IsProxy &&
                        resolved.Proxy == proxy &&
                        resolved.Binding != null)
                    {
                        yield return (mesh, resolved.Binding);
                    }
                }
            }
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

        private static int GetTransformDepth(Transform transform)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }
            return depth;
        }

    }

    [InitializeOnLoad]
    internal static class BlendShareBoneProxyEditorUpdater
    {
        private static BlendShareBoneProxy[] proxies = System.Array.Empty<BlendShareBoneProxy>();
        private static readonly IDisposable EditorChangeSubscription;

        static BlendShareBoneProxyEditorUpdater()
        {
            EditorApplication.update += UpdateProxies;
            EditorChangeSubscription = BlendShareEditorChangeEvents.Subscribe(
                BlendShareEditorChangeKind.Hierarchy | BlendShareEditorChangeKind.Explicit,
                OnEditorChanged,
                typeof(BlendShareBoneProxy));
            EditorApplication.delayCall += RefreshProxies;
        }

        private static void OnEditorChanged(BlendShareEditorChange change)
        {
            RefreshProxies();
        }

        private static void RefreshProxies()
        {
            proxies = Resources.FindObjectsOfTypeAll<BlendShareBoneProxy>()
                .Where(proxy => proxy != null && !EditorUtility.IsPersistent(proxy))
                .ToArray();
        }

        private static void UpdateProxies()
        {
            for (int i = 0; i < proxies.Length; i++)
            {
                var proxy = proxies[i];
                if (proxy != null)
                {
                    proxy.SynchronizeParentingTransform();
                }
            }
        }
    }
}
