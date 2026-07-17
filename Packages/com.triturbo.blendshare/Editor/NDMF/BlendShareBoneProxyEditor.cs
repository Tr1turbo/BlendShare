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
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_SourceArmature"), new GUIContent(Localization.S("ndmf.bone_proxy.source_armature")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_SourceBonePath"), new GUIContent(Localization.S("ndmf.bone_proxy.source_bone_path")));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetParentReference"), new GUIContent(Localization.S("ndmf.bone_proxy.target_parent")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RecalculateBindpose"), new GUIContent(Localization.S("ndmf.bone_proxy.recalculate_bindpose")));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.S("ndmf.bone_proxy.final_parent"), proxy.TargetParent != null ? proxy.TargetParent.name : Localization.S("common.none"));
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
            bool hasSourceBindingTransform = TryGetSourceBindingTransform(
                proxy,
                out var sourcePosition,
                out var sourceEulerRotation,
                out var sourceScale,
                out string sourceDiagnostic);
            using (new EditorGUI.DisabledScope(proxy.TargetParent == null))
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

        private static void NotifyPreviewInputChanged(BlendShareBoneProxy proxy)
        {
            EditorUtility.SetDirty(proxy);
            FlushNdmfPreviewInvalidatesIfAvailable();
            ForceResetNdmfPreviewIfAvailable();
            SceneView.RepaintAll();
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
            eulerRotation = bone.m_FbxLocalEulerRotation;
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
                proxies[i]?.SynchronizeParentingTransform();
            }
        }
    }
}
