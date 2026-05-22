using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BoneGraph;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareObject))]
    public class BlendShareObjectEditor : UnityEditor.Editor
    {
        private readonly Dictionary<MeshDataObject, bool> meshFoldouts = new();

        public override void OnInspectorGUI()
        {
            var blendShare = (BlendShareObject)target;
            serializedObject.Update();

            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawMetadata();
            EditorGUILayout.Space();
            DrawSharedBoneGraphs(blendShare);
            EditorGUILayout.Space();
            DrawMeshes(blendShare);
            EditorGUILayout.Space();
            DrawActions(blendShare);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawMetadata()
        {
            EditorGUILayout.LabelField("BlendShare Object", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_Original)), Localization.G("data.original_fbx"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_DefaultGeneratedAssetName)), Localization.G("data.hidden_settings.default_asset_name"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_Applied)), Localization.G("data.hidden_settings.applied"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_DeformerID)), Localization.G("data.hidden_settings.deformer_id"));
        }

        private void DrawMeshes(BlendShareObject blendShare)
        {
            EditorGUILayout.LabelField("Meshes", EditorStyles.boldLabel);
            foreach (var mesh in blendShare.Meshes)
            {
                if (mesh == null)
                {
                    continue;
                }

                if (!meshFoldouts.ContainsKey(mesh))
                {
                    meshFoldouts[mesh] = false;
                }

                var blendShapeFeature = mesh.GetFeature<BlendShapeFeatureObject>();
                int activeBlendShapeCount = blendShapeFeature?.ActiveBlendShapeIndices.Count ?? 0;
                int blendShapeCount = blendShapeFeature?.BlendShapes.Count ?? 0;

                meshFoldouts[mesh] = EditorGUILayout.Foldout(
                    meshFoldouts[mesh],
                    $"{mesh.m_Path} ({activeBlendShapeCount}/{blendShapeCount})",
                    true);

                if (!meshFoldouts[mesh])
                {
                    continue;
                }

                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Path", mesh.m_Path);
                EditorGUILayout.LabelField("FBX Control Points", mesh.m_FbxControlPointCount.ToString());
                DrawMappings(mesh);
                DrawSkinWeightSummary(mesh.GetFeature<SkinWeightFeatureObject>());
                DrawMeshPresets(blendShare, mesh, blendShapeFeature);
                DrawBlendShapeToggles(blendShapeFeature);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawSharedBoneGraphs(BlendShareObject blendShare)
        {
            var boneGraphs = GetSharedBoneGraphs(blendShare);
            if (boneGraphs.Count == 0)
            {
                return;
            }

            foreach (var boneGraph in boneGraphs)
            {
                DrawBoneGraphSummary(boneGraph);
            }
        }

        private void DrawBoneGraphSummary(BoneGraphObject boneGraph)
        {
            EditorGUILayout.LabelField(Localization.G("data.bone_graph.title"), EditorStyles.boldLabel);
            var bones = boneGraph?.Bones ?? System.Array.Empty<BoneNodeData>();
            int createdCount = bones.Count(bone => bone != null && bone.m_CreateIfMissing);
            EditorGUILayout.LabelField(Localization.S("data.bone_graph.created_count"), createdCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.bone_count"), bones.Count.ToString());
            EditorGUI.indentLevel++;
            foreach (var bone in bones)
            {
                if (bone == null)
                {
                    continue;
                }

                EditorGUILayout.LabelField(bone.m_Path);
            }
            EditorGUI.indentLevel--;
        }

        private static List<BoneGraphObject> GetSharedBoneGraphs(BlendShareObject blendShare)
        {
            return (blendShare?.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .Select(mesh => mesh.GetFeature<SkinWeightFeatureObject>()?.m_BoneGraph)
                .Where(graph => graph != null)
                .Distinct()
                .ToList();
        }

        private void DrawSkinWeightSummary(SkinWeightFeatureObject skinWeightFeature)
        {
            if (skinWeightFeature == null)
            {
                return;
            }

            EditorGUILayout.LabelField(Localization.G("data.skin_weights.title"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.bone_count"), skinWeightFeature.BoneSlotCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.weighted_control_points"), skinWeightFeature.WeightedControlPointCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.root_bone"), skinWeightFeature.m_RootBonePath);
        }

        private void DrawMappings(MeshDataObject mesh)
        {
            var mappings = mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>();
            EditorGUILayout.LabelField("Unity Mappings", mappings.Length.ToString());
            EditorGUI.indentLevel++;
            foreach (var mapping in mappings)
            {
                if (mapping == null)
                {
                    continue;
                }

                EditorGUILayout.ObjectField("Unity Mesh", mapping.m_UnityMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField("Unity Vertices", mapping.m_UnityVertexCount.ToString());
                EditorGUILayout.LabelField("Mapping Valid", mapping.m_IsValid.ToString());
                if (!mapping.m_IsValid && !string.IsNullOrEmpty(mapping.m_InvalidReason))
                {
                    EditorGUILayout.HelpBox(mapping.m_InvalidReason, MessageType.Warning);
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawMeshPresets(BlendShareObject blendShare, MeshDataObject mesh, BlendShapeFeatureObject blendShapeFeature)
        {
            var presets = BlendShareAssetService.LoadPresets(blendShare)
                .Where(preset => preset.m_Path == mesh.m_Path)
                .ToList();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(blendShapeFeature == null);
                if (GUILayout.Button("Save Preset"))
                {
                    string presetName = $"{mesh.m_Path} Preset";
                    var preset = blendShapeFeature.CreatePreset(mesh, presetName);
                    AssetDatabase.AddObjectToAsset(preset, blendShare);
                    preset.hideFlags = HideFlags.HideInHierarchy;
                    EditorUtility.SetDirty(preset);
                    AssetDatabase.SaveAssets();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(blendShapeFeature == null || presets.Count == 0);
                if (GUILayout.Button("Apply First Preset"))
                {
                    Undo.RecordObject(blendShapeFeature, "Apply BlendShape Preset");
                    blendShapeFeature.ApplyPreset(mesh, presets[0]);
                    EditorUtility.SetDirty(blendShapeFeature);
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawBlendShapeToggles(BlendShapeFeatureObject blendShapeFeature)
        {
            if (blendShapeFeature == null)
            {
                EditorGUILayout.HelpBox("No BlendShape feature found for this mesh.", MessageType.Info);
                return;
            }

            var active = new HashSet<int>(blendShapeFeature.ActiveBlendShapeIndices);
            bool changed = false;

            for (int i = 0; i < blendShapeFeature.BlendShapes.Count; i++)
            {
                var blendShape = blendShapeFeature.BlendShapes[i];
                bool enabled = active.Contains(i);
                bool updated = EditorGUILayout.ToggleLeft(blendShape.m_Name, enabled);
                if (updated == enabled)
                {
                    continue;
                }

                changed = true;
                if (updated)
                {
                    active.Add(i);
                }
                else
                {
                    active.Remove(i);
                }
            }

            if (changed)
            {
                Undo.RecordObject(blendShapeFeature, "Update BlendShape Selection");
                blendShapeFeature.SetActiveBlendShapeIndices(blendShapeFeature.ActiveBlendShapeIndices.Where(active.Contains)
                    .Concat(Enumerable.Range(0, blendShapeFeature.BlendShapes.Count).Where(index => active.Contains(index) && !blendShapeFeature.ActiveBlendShapeIndices.Contains(index))));
                EditorUtility.SetDirty(blendShapeFeature);
            }
        }

        private void DrawActions(BlendShareObject blendShare)
        {
            bool hasOriginal = blendShare.m_Original != null;

#if !ENABLE_FBX_SDK
            EditorGUILayout.HelpBox(Localization.S("data.fbx_sdk_missing"), MessageType.Warning);
#endif

            using (new EditorGUI.DisabledScope(!hasOriginal))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(Localization.G("data.apply_blendshapes")))
                    {
                        blendShare.m_Applied = true;
                        BlendShareGenerationService.CreateFbx(blendShare.m_Original, new[] { blendShare });
                        EditorUtility.SetDirty(blendShare);
                    }

                    if (GUILayout.Button(Localization.G("data.remove_blendshapes")))
                    {
                        blendShare.m_Applied = false;
                        BlendShareGenerationService.RemoveBlendShapes(blendShare, blendShare.m_Original);
                        EditorUtility.SetDirty(blendShare);
                    }
                }

                if (GUILayout.Button(Localization.G("data.apply_blendshapes_as_new_fbx")))
                {
                    string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(blendShare));
                    string path = EditorUtility.SaveFilePanelInProject(
                        Localization.S("data.save_fbx.title"),
                        blendShare.DefaultFbxName,
                        "fbx",
                        Localization.S("data.save_file.message"),
                        folderPath);
                    if (!string.IsNullOrEmpty(path))
                    {
                        BlendShareGenerationService.CreateFbx(blendShare.m_Original, new[] { blendShare }, path);
                    }
                }
            }

            if (GUILayout.Button(Localization.G("data.create_meshes")))
            {
                if (blendShare.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }

                string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(blendShare));
                string path = EditorUtility.SaveFilePanelInProject(
                    Localization.S("data.save_mesh.title"),
                    blendShare.DefaultMeshAssetName,
                    "asset",
                    Localization.S("data.save_file.message"),
                    folderPath);
                if (!string.IsNullOrEmpty(path))
                {
                    BlendShareGenerationService.CreateMeshAsset(blendShare.m_Original, new[] { blendShare }, path);
                }
            }

            if (GUILayout.Button(Localization.G("data.create_artifact")))
            {
                if (blendShare.m_Original == null)
                {
                    Localization.DisplayDialog("data.dialog.fbx_missing", Localization.S("data.dialog.ok"));
                    return;
                }

                string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(blendShare));
                string path = EditorUtility.SaveFilePanelInProject(
                    Localization.S("data.save_artifact.title"),
                    $"{blendShare.DefaultMeshAssetName}_Artifact",
                    "asset",
                    Localization.S("data.save_file.message"),
                    folderPath);
                if (!string.IsNullOrEmpty(path))
                {
                    BlendShareArtifactService.CreateArtifact(blendShare.m_Original, new[] { blendShare }, path);
                }
            }

            if (GUILayout.Button(Localization.G("data.open_advanced_generator")))
            {
                var window = EditorWindow.GetWindow<BlendShapeMeshGeneratorWindow>("BlendShare");
                window.blendShapeList.Add(blendShare);
                window.TargetMeshContainer = blendShare.m_Original;
                window.Focus();
            }
        }
    }
}
