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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
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
            if (proxy == null || proxy.Owner == null)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_owner");
                return false;
            }

            var matches = new System.Collections.Generic.List<(Vector3 Position, Vector3 Rotation, Vector3 Scale)>();
            foreach (var meshApplier in BlendShareComponentSetupService.FindOwnedMeshAppliers(proxy.Owner))
            {
                if (meshApplier == null || meshApplier.MeshData == null)
                {
                    continue;
                }

                var mapping = GetFbxToUnityMapping(meshApplier);
                foreach (var binding in meshApplier.BoneProxyBindings)
                {
                    if (binding?.Proxy != proxy || binding.Armature == null)
                    {
                        continue;
                    }

                    var bone = binding.Armature.GetBone(binding.SourceBonePath);
                    if (bone == null)
                    {
                        continue;
                    }

                    matches.Add((
                        mapping != null ? mapping.ConvertFbxVectorToUnity(bone.m_FbxLocalTranslation) : bone.m_FbxLocalTranslation,
                        bone.m_FbxLocalEulerRotation,
                        bone.m_FbxLocalScale == Vector3.zero ? Vector3.one : bone.m_FbxLocalScale));
                }
            }

            if (matches.Count == 0)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                return false;
            }

            var first = matches[0];
            for (int i = 1; i < matches.Count; i++)
            {
                if (Vector3.Distance(first.Position, matches[i].Position) > 0.0001f ||
                    Vector3.Distance(first.Rotation, matches[i].Rotation) > 0.0001f ||
                    Vector3.Distance(first.Scale, matches[i].Scale) > 0.0001f)
                {
                    diagnostic = Localization.S("ndmf.bone_proxy.shared_different_transforms");
                    return false;
                }
            }

            position = first.Position;
            eulerRotation = first.Rotation;
            scale = first.Scale;
            return true;
        }

        private static UnityVertexMappingObject GetFbxToUnityMapping(BlendShareMesh meshApplier)
        {
            var meshData = meshApplier?.MeshData;
            var targetMesh = meshApplier?.TargetRenderer != null ? meshApplier.TargetRenderer.sharedMesh : null;
            var mapping = targetMesh != null
                ? System.Linq.Enumerable.FirstOrDefault(meshData?.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>(), item => item != null && item.IsCompatibleWith(meshData, targetMesh))
                : System.Linq.Enumerable.FirstOrDefault(meshData?.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>(), item => item != null && item.m_IsValid);
            if (mapping == null && meshApplier != null && targetMesh != null)
            {
                var sourceFbx = BlendShareComponentSetupService.ResolveSourceFbx(meshApplier.Owner, targetMesh);
                BlendShareVertexMappingCacheService.TryGet(
                    sourceFbx,
                    meshData,
                    targetMesh,
                    out mapping);
            }

            return mapping;
        }

        private static BlendShareObject FindBlendShareForMeshData(BlendShareCore owner, MeshDataObject meshData)
        {
            return (owner?.Patches ?? System.Array.Empty<BlendShareObject>())
                .FirstOrDefault(patch => patch != null && (patch.Meshes ?? System.Array.Empty<MeshDataObject>()).Contains(meshData));
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
}
