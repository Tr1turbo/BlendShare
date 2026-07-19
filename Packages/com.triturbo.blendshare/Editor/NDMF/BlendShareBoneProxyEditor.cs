using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
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
            if (bindingsProperty.arraySize == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("m_SourceBonePath"), new GUIContent(Localization.S("ndmf.bone_proxy.source_bone_path")));
                }
                EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.legacy_binding"), MessageType.Info);
            }
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("m_UseHierarchyParent"),
                new GUIContent(Localization.S("ndmf.bone_proxy.use_hierarchy_parent")));
            using (new EditorGUI.DisabledScope(proxy.UseHierarchyParent))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetParentReference"), new GUIContent(Localization.S("ndmf.bone_proxy.target_parent")));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RecalculateBindpose"), new GUIContent(Localization.S("ndmf.bone_proxy.recalculate_bindpose")));
            serializedObject.ApplyModifiedProperties();
            DrawBindingValidation(proxy);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.S("ndmf.bone_proxy.final_parent"), proxy.EffectiveParent != null ? proxy.EffectiveParent.name : Localization.S("common.none"));
            EditorGUILayout.LabelField(Localization.S("ndmf.bone_proxy.final_bone_name"), proxy.name);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.binding_local_position"), proxy.LocalPosition);
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.binding_local_rotation"), proxy.LocalEulerRotation);
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.binding_local_scale"), proxy.LocalScale);
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.parenting_local_position"), proxy.ParentingLocalPosition);
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.parenting_local_rotation"), proxy.ParentingLocalEulerRotation);
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.parenting_local_scale"), proxy.ParentingLocalScale);
            }

            if (proxy.TryGetBindPoseWorldTransform(out Vector3 bindPosition, out _, out _))
            {
                EditorGUILayout.Vector3Field(Localization.S("ndmf.bone_proxy.bind_world_position"), bindPosition);
            }

            EditorGUILayout.Space();
            if (proxy.HasExplicitBindings)
            {
                bool canResetChain = CanResetExplicitBindingsFromSource(proxy, out string resetDiagnostic);
                using (new EditorGUI.DisabledScope(!canResetChain))
                {
                    if (GUILayout.Button(Localization.S("ndmf.bone_proxy.reset_chain_from_source")))
                    {
                        ResetExplicitBindingsFromSource(proxy);
                        NotifyPreviewInputChanged(proxy);
                    }
                }

                if (!canResetChain && !string.IsNullOrWhiteSpace(resetDiagnostic))
                {
                    EditorGUILayout.HelpBox(resetDiagnostic, MessageType.Info);
                }

                return;
            }

            bool hasSourceBindingTransform = TryGetSourceBindingTransform(
                proxy,
                out var sourcePosition,
                out var sourceEulerRotation,
                out var sourceScale,
                out string sourceDiagnostic);
            using (new EditorGUI.DisabledScope(proxy.EffectiveParent == null))
            {
                if (GUILayout.Button(Localization.S("ndmf.bone_proxy.update_bindpose")))
                {
                    Undo.RecordObject(proxy, "Update BlendShare Bone Proxy Bindpose");
                    if (proxy.CaptureBindingTransformFromCurrentTransform())
                    {
                        NotifyPreviewInputChanged(proxy);
                    }
                }

                using (new EditorGUI.DisabledScope(!hasSourceBindingTransform))
                {
                    if (GUILayout.Button(Localization.S("ndmf.bone_proxy.restore_from_source")))
                    {
                        Undo.RecordObject(proxy, "Restore BlendShare Bone Proxy Source Bindpose");
                        proxy.LocalPosition = sourcePosition;
                        proxy.LocalEulerRotation = sourceEulerRotation;
                        proxy.LocalScale = sourceScale;
                        proxy.RecalculateBindpose = false;
                        NotifyPreviewInputChanged(proxy);
                    }
                }

                if (GUILayout.Button(Localization.S("ndmf.bone_proxy.reset_to_bind_pose")))
                {
                    Undo.RecordObject(proxy.transform, "Reset BlendShare Bone Proxy Transform");
                    proxy.ResetTransformToBindPose();
                    EditorUtility.SetDirty(proxy.transform);
                }
            }

            if (!hasSourceBindingTransform && !string.IsNullOrWhiteSpace(sourceDiagnostic))
            {
                EditorGUILayout.HelpBox(sourceDiagnostic, MessageType.Info);
            }
        }

        private static void DrawBindingValidation(BlendShareBoneProxy proxy)
        {
            if (proxy == null)
            {
                return;
            }

            var duplicatePaths = proxy.Bindings
                .Where(binding => binding != null && !string.IsNullOrWhiteSpace(binding.SourceBonePath))
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

                string finalPath = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(binding.Transform, avatarRoot));
                EditorGUILayout.LabelField(
                    string.IsNullOrWhiteSpace(binding.SourceBonePath) ? Localization.S("common.none") : binding.SourceBonePath,
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

        private static void NotifyPreviewInputChanged(BlendShareBoneProxy proxy)
        {
            EditorUtility.SetDirty(proxy);
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
                !proxy.Bindings.Any(binding => binding != null && !string.IsNullOrWhiteSpace(binding.SourceBonePath)))
            {
                return false;
            }

            var avatarRoot = proxy.transform.root;
            var lookup = BlendShareBoneProxyLookup.Create(
                avatarRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
            var overriddenBinding = proxy.Bindings.LastOrDefault(binding =>
                binding != null && !string.IsNullOrWhiteSpace(binding.SourceBonePath));
            if (overriddenBinding == null ||
                !lookup.TryGet(proxy.SourceArmature, overriddenBinding.SourceBonePath, out var overridingProxy) ||
                overridingProxy == proxy)
            {
                return false;
            }

            overridingProxyPath = MeshNodePath.Normalize(
                MeshNodePath.GetRelativePath(overridingProxy.transform, avatarRoot));
            return true;
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

        private static bool TryGetSourceBindingTransform(
            BlendShareBoneProxy proxy,
            out Vector3 position,
            out Vector3 eulerRotation,
            out Vector3 scale,
            out string diagnostic)
        {
            position = default;
            eulerRotation = default;
            scale = Vector3.one;
            diagnostic = null;
            if (proxy?.SourceArmature == null || string.IsNullOrWhiteSpace(proxy.SourceBonePath))
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                return false;
            }

            var bone = proxy.SourceArmature.GetBone(proxy.SourceBonePath);
            if (bone == null)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                return false;
            }

            var mapping = proxy.transform.root
                .GetComponentsInChildren<BlendShareMesh>(true)
                .Where(applier => applier?.MeshData?.GetFeature<SkinWeightFeatureObject>()?.Armature == proxy.SourceArmature)
                .Select(GetFbxToUnityMapping)
                .FirstOrDefault(candidate => candidate != null);
            position = mapping != null
                ? mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation)
                : bone.m_FbxLocalTranslation;
            eulerRotation = mapping != null
                ? mapping.ConvertFbxRotationToUnityEuler(bone.GetFbxLocalRotation())
                : bone.GetFbxLocalRotation().eulerAngles;
            scale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;
            return true;
        }

        private static UnityVertexMappingObject GetFbxToUnityMapping(BlendShareMesh meshApplier)
        {
            return BlendShareComponentSetupService.TryResolveMeshApplierMappingReference(
                meshApplier,
                out var mapping)
                ? mapping
                : null;
        }

        private static bool CanResetExplicitBindingsFromSource(
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
                    string.IsNullOrWhiteSpace(binding.SourceBonePath) ||
                    proxy.SourceArmature.GetBone(binding.SourceBonePath) == null ||
                    proxy.GetEffectiveParent(binding) == null)
                {
                    diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                    return false;
                }
            }

            return true;
        }

        private static void ResetExplicitBindingsFromSource(BlendShareBoneProxy proxy)
        {
            var mapping = proxy.transform.root
                .GetComponentsInChildren<BlendShareMesh>(true)
                .Where(applier => applier?.MeshData?.GetFeature<SkinWeightFeatureObject>()?.Armature == proxy.SourceArmature)
                .Select(GetFbxToUnityMapping)
                .FirstOrDefault(candidate => candidate != null);
            foreach (var binding in proxy.Bindings)
            {
                var bone = proxy.SourceArmature.GetBone(binding.SourceBonePath);
                var finalTransform = binding.Transform;
                var parent = proxy.GetEffectiveParent(binding);
                var localPosition = mapping != null
                    ? mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation)
                    : bone.m_FbxLocalTranslation;
                var localRotation = mapping != null
                    ? mapping.ConvertFbxRotationToUnity(bone.GetFbxLocalRotation())
                    : bone.GetFbxLocalRotation();
                var localScale = bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale;

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
            proxy.RecalculateBindpose = false;
            EditorUtility.SetDirty(proxy);
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

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Active)]
        private static void DrawBindPoseOffsetGizmo(BlendShareBoneProxy proxy, GizmoType gizmoType)
        {
            if (proxy == null ||
                proxy.IsTransformAtBindPosition() ||
                !proxy.TryGetBindPoseWorldTransform(out Vector3 bindPosition, out _, out _))
            {
                return;
            }

            Color previousColor = Handles.color;
            Handles.color = new Color(0.25f, 0.7f, 1f, 0.9f);
            Handles.DrawDottedLine(proxy.transform.position, bindPosition, 4f);
            Handles.SphereHandleCap(
                0,
                bindPosition,
                Quaternion.identity,
                HandleUtility.GetHandleSize(bindPosition) * 0.06f,
                EventType.Repaint);
            Handles.color = previousColor;
        }
    }

    [InitializeOnLoad]
    internal static class BlendShareBoneProxyEditorUpdater
    {
        private static BlendShareBoneProxy[] proxies = System.Array.Empty<BlendShareBoneProxy>();

        static BlendShareBoneProxyEditorUpdater()
        {
            EditorApplication.update += UpdateProxies;
            EditorApplication.hierarchyChanged += RefreshProxies;
            EditorApplication.delayCall += RefreshProxies;
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
