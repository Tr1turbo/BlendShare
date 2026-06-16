using System;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(MeshDataObject))]
    public sealed class MeshDataObjectEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var mesh = (MeshDataObject)target;
            var patch = BlendShareInspectorUtility.FindOwnerPatch(mesh);
            var root = BlendShareInspectorUi.CreateRoot();

            root.Add(CreateEmbeddedInspector(mesh, patch, () => Rebuild(root, mesh, patch), true));
            Localization.RebuildOnLanguageChange(root, () => Rebuild(root, mesh, patch));
            return root;
        }

        private void Rebuild(VisualElement root, MeshDataObject mesh, BlendShareObject patch)
        {
            root.Clear();
            root.Add(CreateEmbeddedInspector(mesh, patch, () => Rebuild(root, mesh, patch), true));
        }

        internal static VisualElement CreateEmbeddedInspector(MeshDataObject mesh, BlendShareObject patch, Action refresh, bool showParentPatch)
        {
            return CreateEmbeddedInspector(mesh, new BlendShareEmbeddedEditorContext(mesh, mesh, patch, refresh), showParentPatch);
        }

        internal static VisualElement CreateEmbeddedInspector(MeshDataObject mesh, BlendShareEmbeddedEditorContext context, bool showParentPatch)
        {
            var root = new VisualElement();
            root.Add(CreateSummary(mesh, context, showParentPatch));
            root.Add(CreateFeatureSections(mesh, context));
            root.Add(CreateAdvancedSection(mesh, context));
            return root;
        }

        internal static VisualElement CreateCompactEmbeddedInspector(MeshDataObject mesh, BlendShareObject patch, Action refresh)
        {
            return CreateCompactEmbeddedInspector(mesh, new BlendShareEmbeddedEditorContext(mesh, mesh, patch, refresh));
        }

        internal static VisualElement CreateCompactEmbeddedInspector(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var root = BlendShareInspectorUi.Box();
            BlendShareInspectorUi.RegisterClickAction(root, () => EditorGUIUtility.PingObject(mesh));
            BlendShareInspectorUi.RegisterDoubleClickAction(root, () =>
            {
                EditorApplication.delayCall += () =>
                {
                    Selection.activeObject = mesh;
                    EditorGUIUtility.PingObject(mesh);
                };
            });

            var compatibility = BlendShareInspectorUtility.GetFbxCompatibilityStatus(context.OwnerPatch, mesh);
            var mapping = GetMappingStatus(mesh, context, out var targetMesh);

            root.Add(BlendShareInspectorUi.LabeledRow(
                Localization.S("common.path"),
                CreateTextWithStatusRow(mesh.m_Path, BlendShareInspectorUi.CompatibilityIcon(compatibility))));
            root.Add(CreateUnityMeshRow(mesh, context, mapping, targetMesh));

            root.Add(CreateCompactFeatureElements(mesh, context));

            if (compatibility.State == MeshFbxCompatibilityState.Incompatible || compatibility.State == MeshFbxCompatibilityState.MissingTargetMesh)
            {
                BlendShareInspectorUi.AddHelpBox(root, compatibility.Message, HelpBoxMessageType.Warning);
            }

            if (!mapping.IsReady)
            {
                BlendShareInspectorUi.AddHelpBox(root, mapping.Message, HelpBoxMessageType.Warning);
            }

            return root;
        }

        private static VisualElement CreateCompactFeatureElements(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var features = (mesh.Features ?? Array.Empty<MeshFeatureObject>())
                .Where(feature => feature != null)
                .ToArray();
            if (features.Length == 0)
            {
                return new VisualElement();
            }

            var section = new VisualElement();
            section.style.flexDirection = FlexDirection.Row;
            section.style.flexWrap = Wrap.Wrap;
            section.style.alignItems = Align.FlexStart;
            section.style.marginTop = 4;

            foreach (var feature in features)
            {
                var editor = MeshFeatureEditorRegistry.GetEditor(feature);
                var childContext = new MeshFeatureEditorContext(feature, mesh, context.OwnerPatch, context.Refresh);
                section.Add(editor?.CreateCompactElement(childContext) ?? new BlendShareFeatureBadge(GetFeatureLabel(feature)));
            }

            return section;
        }

        private static string GetFeatureLabel(MeshFeatureObject feature)
        {
            return string.Equals(feature?.FeatureId, "uv", StringComparison.Ordinal) ? "UV" : feature?.FeatureId ?? "Feature";
        }

        private static VisualElement CreateUnityMeshRow(
            MeshDataObject mesh,
            BlendShareEmbeddedEditorContext context,
            MeshMappingStatus mapping,
            Mesh targetMesh)
        {
            if (targetMesh != null)
            {
                return BlendShareInspectorUi.LabeledRow(Localization.S("patch.mapping.unity_mesh"), CreateUnityMeshReferenceRow(mesh, context, mapping, targetMesh));
            }

            string message = context.FbxGo != null
                ? context.GetUnityMeshResolutionError(mesh) ?? Localization.S("patch.mapping.not_resolved")
                : Localization.S("patch.mapping.target_missing");
            return BlendShareInspectorUi.Row(Localization.S("patch.mapping.unity_mesh"), message);
        }

        private static VisualElement CreateUnityMeshReferenceRow(
            MeshDataObject mesh,
            BlendShareEmbeddedEditorContext context,
            MeshMappingStatus mapping,
            Mesh targetMesh)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.style.minWidth = 0;
            var meshField = new ObjectField
            {
                objectType = typeof(Mesh),
                allowSceneObjects = false,
                value = targetMesh
            };
            meshField.SetEnabled(false);
            meshField.style.flexGrow = 1;
            meshField.style.flexShrink = 1;
            meshField.style.minWidth = 120;
            row.Add(meshField);

            var compatibleMapping = FindCompatibleMapping(mesh, targetMesh);
            if (compatibleMapping != null)
            {
                var icon = BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Verified,
                    Localization.S("common.status.verified"),
                    Localization.SF("patch.mapping.verified", targetMesh.name, compatibleMapping.UnityVerticesHashShort)));
                icon.style.flexShrink = 0;
                row.Add(icon);
                return row;
            }

            var button = BlendShareInspectorUi.SmallButton(Localization.S("patch.mapping.create"), () => CreateMapping(mesh, context, targetMesh));
            button.SetEnabled(mapping.CanCreateMappings && context.FbxGo != null && context.OwnerPatch != null);
            button.style.marginLeft = 6;
            button.style.flexShrink = 0;
            row.Add(button);
            return row;
        }

        private static VisualElement CreateTextWithStatusRow(string value, VisualElement statusIcon)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.style.minWidth = 0;

            var label = BlendShareInspectorUi.ValueLabel(value);
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.minWidth = 120;
            row.Add(label);

            if (statusIcon != null)
            {
                statusIcon.style.flexShrink = 0;
                row.Add(statusIcon);
            }

            return row;
        }

        private static VisualElement CreateSummary(MeshDataObject mesh, BlendShareEmbeddedEditorContext context, bool showParentPatch)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.mesh_data.summary"));
            var compatibility = BlendShareInspectorUtility.GetFbxCompatibilityStatus(context.OwnerPatch, mesh);
            var mapping = GetMappingStatus(mesh, context, out var targetMesh);

            section.Add(BlendShareInspectorUi.LabeledRow(
                Localization.S("features.skin-weights.mesh"),
                CreateTextWithStatusRow(mesh.m_Path, BlendShareInspectorUi.CompatibilityIcon(compatibility))));
            section.Add(CreateUnityMeshRow(mesh, context, mapping, targetMesh));

            if (showParentPatch)
            {
                var parentRow = new VisualElement();
                parentRow.style.flexDirection = FlexDirection.Row;
                parentRow.style.alignItems = Align.Center;
                parentRow.style.flexShrink = 1;

                var label = BlendShareInspectorUi.ValueLabel(context.OwnerPatch != null ? context.OwnerPatch.name : Localization.S("common.status.not_found"));
                label.style.minWidth = 100;
                label.style.flexGrow = 1;
                label.style.flexShrink = 1;

                parentRow.Add(label);
                if (context.OwnerPatch != null)
                {
                    parentRow.Add(BlendShareInspectorUi.InlineButton(Localization.S("common.select"), () => Selection.activeObject = context.OwnerPatch));
                }

                section.Add(BlendShareInspectorUi.LabeledRow(Localization.S("patch.mesh_data.parent_patch"), parentRow));
            }

            section.Add(BlendShareInspectorUi.BadgeRow(BlendShareInspectorUi.Badge(Localization.SF("patch.mapping.badge", mapping.Label), mapping.Kind)));

            if (compatibility.State != MeshFbxCompatibilityState.Verified)
            {
                BlendShareInspectorUi.AddHelpBox(section, compatibility.Message, HelpBoxMessageType.Info);
            }

            if (!mapping.IsReady)
            {
                BlendShareInspectorUi.AddHelpBox(section, mapping.Message, HelpBoxMessageType.Warning);
            }

            return section;
        }

        private static VisualElement CreateFeatureSections(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var section = new VisualElement();
            var features = (mesh.Features ?? Array.Empty<MeshFeatureObject>())
                .Where(feature => feature != null)
                .ToArray();

            if (features.Length == 0)
            {
                section.Add(new HelpBox(Localization.S("patch.mesh_data.no_data"), HelpBoxMessageType.Info));
                return section;
            }

            foreach (var feature in features)
            {
                var editor = MeshFeatureEditorRegistry.GetEmbeddedEditor(feature);
                if (editor != null)
                {
                    section.Add(editor.CreateEmbeddedInspector(CreateChildContext(context, feature, mesh)));
                }
                else
                {
                    var featureSection = BlendShareInspectorUi.Section(feature.FeatureId);
                    featureSection.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.data_id"), feature.FeatureId));
                    featureSection.Add(new HelpBox(Localization.S("patch.mesh_data.no_data_editor"), HelpBoxMessageType.Info));
                    section.Add(featureSection);
                }
            }

            return section;
        }

        private static VisualElement CreateMappingSections(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var mappings = (mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .Where(mapping => mapping != null)
                .ToArray();
            if (mappings.Length == 0)
            {
                return new VisualElement();
            }

            var section = BlendShareInspectorUi.Section(Localization.S("patch.mapping.title"));
            context.TryGetUnityMesh(mesh, out var targetMesh);
            foreach (var mapping in mappings)
            {
                var editor = MeshFeatureEditorRegistry.GetEmbeddedEditor(mapping);
                if (editor != null)
                {
                    var mappingElement = editor.CreateEmbeddedInspector(CreateChildContext(context, mapping, mesh));
                    if (targetMesh != null && mapping.IsCompatibleWith(mesh, targetMesh))
                    {
                        AddActiveBadge(mappingElement);
                    }

                    section.Add(mappingElement);
                }
                else
                {
                    section.Add(new HelpBox(Localization.S("patch.mapping.no_editor"), HelpBoxMessageType.Info));
                }
            }

            return section;
        }

        private static void AddActiveBadge(VisualElement element)
        {
            if (element == null)
            {
                return;
            }

            element.style.position = Position.Relative;
            var badge = BlendShareInspectorUi.Badge(Localization.S("common.status.active"), StatusKind.Success);
            badge.style.position = Position.Absolute;
            badge.style.top = 5;
            badge.style.right = 6;
            element.Add(badge);
        }

        private static VisualElement CreateAdvancedSection(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var foldout = new Foldout { text = Localization.S("common.advanced") };
            foldout.Add(CreateTopologyFingerprintDrawer(mesh));
            foldout.Add(CreateMappingSections(mesh, context));
            foldout.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.data"), CountValid(mesh.Features).ToString()));
            foldout.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.count"), CountValid(mesh.m_Mappings).ToString()));
            return foldout;
        }

        private static MeshMappingStatus GetMappingStatus(
            MeshDataObject mesh,
            BlendShareEmbeddedEditorContext context,
            out Mesh targetMesh)
        {
            targetMesh = null;
            if (mesh == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mesh_data.missing"), false, StatusKind.Neutral);
            }

            if (context.FbxGo == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.missing_target"), Localization.S("patch.mapping.target_missing"), false, StatusKind.Warning);
            }

            if (context.UnityMeshLookup == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mapping.target_unreadable"), false, StatusKind.Neutral);
            }

            if (!context.TryGetUnityMesh(mesh, out targetMesh))
            {
                return new MeshMappingStatus(Localization.S("common.status.missing"), context.GetUnityMeshResolutionError(mesh), false, StatusKind.Warning);
            }

            var resolvedTargetMesh = targetMesh;
            var mappings = mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>();
            bool hasMapping = mappings.Any(mapping => mapping != null);
            bool hasValidMapping = mappings.Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, resolvedTargetMesh));
            if (hasValidMapping)
            {
                return new MeshMappingStatus(Localization.S("common.status.ready"), targetMesh.name, false, StatusKind.Success);
            }

            return new MeshMappingStatus(
                hasMapping ? Localization.S("common.status.invalid") : Localization.S("common.status.missing"),
                hasMapping ? Localization.S("patch.mapping.no_stored_match") : Localization.S("patch.mapping.no_mapping"),
                true,
                StatusKind.Warning);
        }

        private static UnityVertexMappingObject FindCompatibleMapping(MeshDataObject mesh, Mesh targetMesh)
        {
            return (mesh?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh));
        }

        private static void CreateMapping(MeshDataObject mesh, BlendShareEmbeddedEditorContext context, Mesh targetMesh)
        {
            if (mesh == null || context.OwnerPatch == null || context.FbxGo == null || targetMesh == null)
            {
                return;
            }

            var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(mesh.m_Path, targetMesh, context.FbxGo);
            if (mapping == null || !mapping.m_IsValid)
            {
                string message = mapping != null ? mapping.m_Report : Localization.S("patch.mapping.generation_failed");
                if (mapping != null)
                {
                    UnityEngine.Object.DestroyImmediate(mapping);
                }

                EditorUtility.DisplayDialog(Localization.S("patch.mapping.create"), message, Localization.S("common.ok"));
                return;
            }

            Undo.RecordObject(mesh, "Create Unity Mapping");
            mesh.m_Mappings = (mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                .Where(existing => existing != null)
                .Concat(new[] { mapping })
                .ToArray();
            BlendShareAssetService.SaveMappings(context.OwnerPatch);
            context.Refresh?.Invoke();
        }

        private static BlendShareEmbeddedEditorContext CreateChildContext(
            BlendShareEmbeddedEditorContext context,
            UnityEngine.Object embeddedObject,
            MeshDataObject ownerMesh)
        {
            return new BlendShareEmbeddedEditorContext(
                embeddedObject,
                ownerMesh,
                context.OwnerPatch,
                context.Refresh,
                context.FbxGo,
                context.UnityMeshLookup);
        }

        private static VisualElement CreateTopologyFingerprintDrawer(MeshDataObject mesh)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.mesh_data.topology_fingerprint"));
            var signature = mesh?.m_FbxTopologySignature;
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.hash"), signature != null ? BlendShareInspectorUtility.ShortHash(signature.Hash) : "-"));
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.control_points"), signature != null ? signature.ControlPointCount.ToString() : "-"));
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.faces"), signature != null ? signature.FaceCount.ToString() : "-"));
            return section;
        }

        private static int CountValid<T>(System.Collections.Generic.IEnumerable<T> items) where T : UnityEngine.Object
        {
            return (items ?? Array.Empty<T>()).Count(item => item != null);
        }
    }

    public sealed class MeshDataObjectEmbeddedEditor : IBlendShareEmbeddedEditor
    {
        public Type TargetType => typeof(MeshDataObject);
        public string DisplayName => Localization.S("patch.mesh_data.display_name");

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var mesh = context.EmbeddedObject as MeshDataObject ?? context.OwnerMeshData;
            if (mesh == null)
            {
                return new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning);
            }

            return MeshDataObjectEditor.CreateEmbeddedInspector(mesh, context, false);
        }
    }
}
