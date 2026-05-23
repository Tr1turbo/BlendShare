using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.NonDestructive;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareApplierComponent))]
    public sealed class BlendShareApplierComponentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorWidgets.ShowBlendShareBanner();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRootReference"), new GUIContent("Target Root"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_BlendShares"), new GUIContent("BlendShare Objects"), true);
            serializedObject.ApplyModifiedProperties();

            var owner = (BlendShareApplierComponent)target;
            DrawSummary(owner);
            DrawActions(owner);
        }

        private static void DrawSummary(BlendShareApplierComponent owner)
        {
            var meshAppliers = BlendShareApplierSetupService.FindOwnedMeshAppliers(owner);
            var proxies = BlendShareApplierSetupService.FindOwnedBoneProxies(owner);
            int staleCount = meshAppliers.Count(applier => applier != null && applier.IsStale);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bindings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Target Root", FormatObject(BlendShareApplierSetupService.ResolveTargetRoot(owner)));
            EditorGUILayout.LabelField("Mesh Appliers", meshAppliers.Length.ToString());
            EditorGUILayout.LabelField("Stale Mesh Appliers", staleCount.ToString());
            EditorGUILayout.LabelField("Bone Proxies", proxies.Length.ToString());

            foreach (var applier in meshAppliers.Where(applier => applier != null).OrderBy(applier => applier.RendererNodePath))
            {
                string status = applier.IsStale ? "Stale" : applier.EnabledForBuild ? "Enabled" : "Disabled";
                EditorGUILayout.LabelField(applier.RendererNodePath, status);
                if (!string.IsNullOrWhiteSpace(applier.DiagnosticMessage))
                {
                    EditorGUILayout.HelpBox(applier.DiagnosticMessage, applier.IsStale ? MessageType.Warning : MessageType.Info);
                }
            }
        }

        private static void DrawActions(BlendShareApplierComponent owner)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Mesh Bindings"))
                {
                    var result = BlendShareApplierSetupService.RebuildMeshBindings(owner);
                    ReportDiagnostics(result.Diagnostics, owner);
                }

                if (GUILayout.Button("Rebuild Bone Proxies"))
                {
                    var result = BlendShareApplierSetupService.RebuildBoneProxies(owner);
                    ReportDiagnostics(result.Diagnostics, owner);
                }
            }
        }

        private static string FormatObject(Object obj)
        {
            return obj != null ? obj.name : "None";
        }

        private static void ReportDiagnostics(System.Collections.Generic.IReadOnlyList<string> diagnostics, Object context)
        {
            foreach (string diagnostic in diagnostics)
            {
                Debug.LogWarning($"[BlendShare] {diagnostic}", context);
            }
        }
    }
}
