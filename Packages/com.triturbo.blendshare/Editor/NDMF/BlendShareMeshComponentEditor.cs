using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareMeshComponent))]
    public sealed class BlendShareMeshComponentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("BlendShare Mesh Applier", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Owner"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRendererReference"), new GUIContent("Target Renderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MeshData"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_EnabledForBuild"));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_DiagnosticMessage"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_BoneProxyBindings"), true);

            var applier = (BlendShareMeshComponent)target;
            if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out string mappingDiagnostic))
            {
                bool showMappingError = true;
                if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out string cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else if (BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string createdDiagnostic))
                {
                    showMappingError = false;
                }
                else if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else
                {
                    mappingDiagnostic = createdDiagnostic;
                }

                if (showMappingError)
                {
                    EditorGUILayout.HelpBox(mappingDiagnostic, MessageType.Error);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
