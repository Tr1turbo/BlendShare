using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareCore))]
    public sealed class BlendShareCoreEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRootReference"), new GUIContent(Localization.S("ndmf.core.target_root")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Patches"), new GUIContent(Localization.S("common.patches")), true);
            serializedObject.ApplyModifiedProperties();

            var owner = (BlendShareCore)target;
            DrawSummary(owner);
            DrawActions(owner);
        }

        private static void DrawSummary(BlendShareCore owner)
        {
            var meshAppliers = BlendShareComponentSetupService.FindOwnedMeshAppliers(owner);
            var proxies = BlendShareComponentSetupService.FindOwnedBoneProxies(owner);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Localization.S("ndmf.core.bindings"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Localization.S("ndmf.core.target_root"), FormatObject(BlendShareComponentSetupService.ResolveTargetRoot(owner)));
            EditorGUILayout.LabelField(Localization.S("ndmf.core.mesh_appliers"), meshAppliers.Length.ToString());
            EditorGUILayout.LabelField(Localization.S("ndmf.core.bone_proxies"), proxies.Length.ToString());

            foreach (var applier in meshAppliers.Where(applier => applier != null).OrderBy(GetRendererPath))
            {
                string status = applier.EnabledForBuild ? Localization.S("common.enabled") : Localization.S("common.disabled");
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

        private static string GetRendererPath(BlendShareMesh applier)
        {
            if (applier?.TargetRenderer == null)
            {
                return Localization.S("ndmf.mesh.missing_renderer");
            }

            var targetRoot = BlendShareComponentSetupService.ResolveTargetRoot(applier.Owner);
            return MeshNodePath.Normalize(MeshNodePath.GetRelativePath(applier.TargetRenderer.transform, targetRoot));
        }

        private static void DrawActions(BlendShareCore owner)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Localization.S("ndmf.core.rebuild_mesh_bindings")))
                {
                    var result = BlendShareComponentSetupService.RebuildMeshBindings(owner);
                    ReportDiagnostics(result.Diagnostics, owner);
                }

                if (GUILayout.Button(Localization.S("ndmf.core.rebuild_bone_proxies")))
                {
                    var result = BlendShareComponentSetupService.RebuildBoneProxies(owner);
                    ReportDiagnostics(result.Diagnostics, owner);
                }
            }
        }

        private static string FormatObject(Object obj)
        {
            return obj != null ? obj.name : Localization.S("common.none");
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
