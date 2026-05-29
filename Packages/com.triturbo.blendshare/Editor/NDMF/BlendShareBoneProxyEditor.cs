using System.Linq;
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

            EditorGUILayout.LabelField("BlendShare Bone Proxy", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetParentReference"), new GUIContent("Target Parent"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_RecalculateBindpose"), new GUIContent("Recalculate Bindpose"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Final Parent", proxy.TargetParent != null ? proxy.TargetParent.name : "<none>");
            EditorGUILayout.LabelField("Final Bone Name", proxy.name);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Vector3Field("Binding Local Position", proxy.LocalPosition);
                EditorGUILayout.Vector3Field("Binding Local Rotation", proxy.LocalEulerRotation);
                EditorGUILayout.Vector3Field("Binding Local Scale", proxy.LocalScale);
                EditorGUILayout.Vector3Field("Parenting Local Position", proxy.ParentingLocalPosition);
                EditorGUILayout.Vector3Field("Parenting Local Rotation", proxy.ParentingLocalEulerRotation);
                EditorGUILayout.Vector3Field("Parenting Local Scale", proxy.ParentingLocalScale);
            }

            if (proxy.TryGetBindPoseWorldTransform(out Vector3 bindPosition, out _, out _))
            {
                EditorGUILayout.Vector3Field("Bind World Position", bindPosition);
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
                if (GUILayout.Button("Update Bindpose From Current Transform"))
                {
                    Undo.RecordObject(proxy, "Update BlendShare Bone Proxy Bindpose");
                    if (proxy.CaptureBindingTransformFromCurrentTransform())
                    {
                        NotifyPreviewInputChanged(proxy);
                    }
                }

                using (new EditorGUI.DisabledScope(!hasSourceBindingTransform))
                {
                    if (GUILayout.Button("Restore Binding Transform From Source"))
                    {
                        Undo.RecordObject(proxy, "Restore BlendShare Bone Proxy Source Bindpose");
                        proxy.LocalPosition = sourcePosition;
                        proxy.LocalEulerRotation = sourceEulerRotation;
                        proxy.LocalScale = sourceScale;
                        proxy.RecalculateBindpose = false;
                        NotifyPreviewInputChanged(proxy);
                    }
                }

                if (GUILayout.Button("Reset Transform To Bind Pose"))
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
                diagnostic = "No owning BlendShare applier was found.";
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
                    if (binding?.Proxy != proxy || binding.BoneGraph == null)
                    {
                        continue;
                    }

                    var bone = binding.BoneGraph.GetBone(binding.SourceBonePath);
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
                diagnostic = "No source bone binding was found for this proxy.";
                return false;
            }

            var first = matches[0];
            for (int i = 1; i < matches.Count; i++)
            {
                if (Vector3.Distance(first.Position, matches[i].Position) > 0.0001f ||
                    Vector3.Distance(first.Rotation, matches[i].Rotation) > 0.0001f ||
                    Vector3.Distance(first.Scale, matches[i].Scale) > 0.0001f)
                {
                    diagnostic = "This proxy is shared by source bones with different binding transforms.";
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
            return (owner?.BlendShares ?? System.Array.Empty<BlendShareObject>())
                .FirstOrDefault(share => share != null && (share.Meshes ?? System.Array.Empty<MeshDataObject>()).Contains(meshData));
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
