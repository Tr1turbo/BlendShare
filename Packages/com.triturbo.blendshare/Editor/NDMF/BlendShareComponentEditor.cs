using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareComponent))]
    public sealed class BlendShareComponentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorWidgets.ShowBlendShareBanner();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRootReference"), new GUIContent("Target Root"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_BlendShares"), new GUIContent("BlendShare Objects"), true);
            serializedObject.ApplyModifiedProperties();

            var owner = (BlendShareComponent)target;
            DrawSummary(owner);
            DrawActions(owner);
        }

        private static void DrawSummary(BlendShareComponent owner)
        {
            var meshAppliers = BlendShareComponentSetupService.FindOwnedMeshAppliers(owner);
            var proxies = BlendShareComponentSetupService.FindOwnedBoneProxies(owner);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bindings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Target Root", FormatObject(BlendShareComponentSetupService.ResolveTargetRoot(owner)));
            EditorGUILayout.LabelField("Mesh Appliers", meshAppliers.Length.ToString());
            EditorGUILayout.LabelField("Bone Proxies", proxies.Length.ToString());

            foreach (var applier in meshAppliers.Where(applier => applier != null).OrderBy(GetRendererPath))
            {
                string status = applier.EnabledForBuild ? "Enabled" : "Disabled";
                EditorGUILayout.LabelField(GetRendererPath(applier), status);
                if (!string.IsNullOrWhiteSpace(applier.DiagnosticMessage))
                {
                    EditorGUILayout.HelpBox(applier.DiagnosticMessage, MessageType.Info);
                }

                if (!BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out string mappingDiagnostic))
                {
                    if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out string cachedDiagnostic))
                    {
                        mappingDiagnostic = cachedDiagnostic;
                    }
                    else if (BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string createdDiagnostic))
                    {
                        continue;
                    }
                    else if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out cachedDiagnostic))
                    {
                        mappingDiagnostic = cachedDiagnostic;
                    }
                    else
                    {
                        mappingDiagnostic = createdDiagnostic;
                    }

                    EditorGUILayout.HelpBox(mappingDiagnostic, MessageType.Error);
                }
            }
        }

        private static string GetRendererPath(BlendShareMeshComponent applier)
        {
            if (applier?.TargetRenderer == null)
            {
                return "<missing renderer>";
            }

            var targetRoot = BlendShareComponentSetupService.ResolveTargetRoot(applier.Owner);
            return MeshNodePath.Normalize(MeshNodePath.GetRelativePath(applier.TargetRenderer.transform, targetRoot));
        }

        private static void DrawActions(BlendShareComponent owner)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Mesh Bindings"))
                {
                    var result = BlendShareComponentSetupService.RebuildMeshBindings(owner);
                    ReportDiagnostics(result.Diagnostics, owner);
                }

                if (GUILayout.Button("Rebuild Bone Proxies"))
                {
                    var result = BlendShareComponentSetupService.RebuildBoneProxies(owner);
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
