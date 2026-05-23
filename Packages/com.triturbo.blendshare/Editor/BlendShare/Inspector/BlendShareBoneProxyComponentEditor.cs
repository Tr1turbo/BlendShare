using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareBoneProxyComponent))]
    public sealed class BlendShareBoneProxyComponentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var proxy = (BlendShareBoneProxyComponent)target;
            serializedObject.Update();

            EditorGUILayout.LabelField("BlendShare Bone Proxy", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetParentReference"), new GUIContent("Target Parent"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_LocalPosition"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_LocalEulerRotation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_LocalScale"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Final Parent", proxy.TargetParent != null ? proxy.TargetParent.name : "<none>");
            EditorGUILayout.LabelField("Final Bone Name", proxy.name);
            if (proxy.TryGetBindPoseWorldTransform(out Vector3 bindPosition, out _, out _))
            {
                EditorGUILayout.Vector3Field("Bind World Position", bindPosition);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(proxy.TargetParent == null))
            {
                if (GUILayout.Button("Reset Transform To Bind Pose"))
                {
                    Undo.RecordObject(proxy.transform, "Reset BlendShare Bone Proxy Transform");
                    proxy.ResetTransformToBindPose();
                    EditorUtility.SetDirty(proxy.transform);
                }
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Active)]
        private static void DrawBindPoseOffsetGizmo(BlendShareBoneProxyComponent proxy, GizmoType gizmoType)
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
