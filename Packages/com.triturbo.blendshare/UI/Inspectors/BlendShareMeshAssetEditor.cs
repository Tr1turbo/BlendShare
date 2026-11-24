using System;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.UI.ValueDrawers;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Verbatim port of the original <c>MeshDataObjectEditor</c> UI wired directly to BlendShare 2.0
    /// <see cref="BlendShareMeshAsset"/> and <see cref="MeshDocument"/>.
    /// </summary>
    [CustomEditor(typeof(BlendShareMeshAsset))]
    public sealed class BlendShareMeshAssetEditor : BlendShareDocumentEditor
    {
        protected override BlendShareAsset Patch => (target as BlendShareMeshAsset)?.Root;

        protected override void BuildStaticChrome(VisualElement root)
        {
            root.Add(new IMGUIContainer(Localization.DrawLanguageSelection));
        }

        protected override void BuildContent(
            VisualElement content,
            PatchDocument document,
            Func<MeshDocument, FeatureInspectorContext> contextFactory)
        {
            var meshHandle = (BlendShareMeshAsset)target;
            var mesh = document.Meshes.FirstOrDefault(m => m != null && string.Equals(m.MeshId, meshHandle.MeshId, StringComparison.Ordinal));
            if (mesh == null)
            {
                content.Add(new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning));
                return;
            }

            var ctx = contextFactory(mesh);
            var targetFbx = ctx.Buffer?.Settings?.DefaultTargetFbx;
            var unityMeshes = CollectTargetUnityMeshes(targetFbx);
            string normPath = FbxNameUtility.NormalizePath(mesh.NodePath ?? string.Empty);
            unityMeshes.TryGetValue(normPath, out var targetUnityMesh);
            var compatibility = BlendShareInspectorUtility.GetFbxCompatibilityStatus(targetFbx, mesh);
            var mapping = BlendShareInspectorUtility.GetMappingStatus(targetFbx, mesh);

            content.Add(CreateSummary(mesh, meshHandle, targetFbx, targetUnityMesh, compatibility, mapping));
            content.Add(CreateFeatureSections(mesh, ctx));
            content.Add(CreateAdvancedSection(mesh, ctx));
        }

        internal static VisualElement CreateCompactEmbeddedInspector(
            MeshDocument mesh,
            BlendShareMeshAsset meshHandle,
            GameObject targetFbx,
            Mesh targetUnityMesh,
            MeshFbxCompatibilityStatus compatibility,
            FeatureInspectorContext context)
        {
            var root = BlendShareInspectorUi.Box();
            if (meshHandle != null)
            {
                BlendShareInspectorUi.RegisterClickAction(root, () => EditorGUIUtility.PingObject(meshHandle));
                BlendShareInspectorUi.RegisterDoubleClickAction(root, () =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        Selection.activeObject = meshHandle;
                        EditorGUIUtility.PingObject(meshHandle);
                    };
                });
            }

            var mapping = BlendShareInspectorUtility.GetMappingStatus(targetFbx, mesh);
            root.Add(BlendShareInspectorUi.LabeledRow(
                Localization.S("common.path"),
                CreateTextWithStatusRow(mesh.NodePath, BlendShareInspectorUi.CompatibilityIcon(compatibility))));
            root.Add(CreateUnityMeshRow(mesh, targetFbx, targetUnityMesh, mapping));
            root.Add(CreateCompactFeatureElements(mesh, context));

            if (compatibility.State == MeshFbxCompatibilityState.Incompatible ||
                compatibility.State == MeshFbxCompatibilityState.MissingTargetMesh)
            {
                BlendShareInspectorUi.AddHelpBox(root, compatibility.Message, HelpBoxMessageType.Warning);
            }

            if (!mapping.IsReady && targetFbx != null)
            {
                BlendShareInspectorUi.AddHelpBox(root, mapping.Message, HelpBoxMessageType.Warning);
            }

            return root;
        }

        private static VisualElement CreateCompactFeatureElements(MeshDocument mesh, FeatureInspectorContext context)
        {
            var features = mesh.Features
                .Where(f => f != null)
                .OrderBy(f => FeatureInspectorRegistry.Resolve(f).DisplayOrder)
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
                var drawer = FeatureInspectorRegistry.Resolve(feature);
                section.Add(drawer.CreateCompactBadgeElement(feature, context));
            }

            return section;
        }

        private static VisualElement CreateSummary(
            MeshDocument mesh,
            BlendShareMeshAsset meshHandle,
            GameObject targetFbx,
            Mesh targetUnityMesh,
            MeshFbxCompatibilityStatus compatibility,
            MeshMappingStatus mapping)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.mesh_data.summary"));

            section.Add(BlendShareInspectorUi.LabeledRow(
                Localization.S("features.skin-weights.mesh"),
                CreateTextWithStatusRow(mesh.NodePath, BlendShareInspectorUi.CompatibilityIcon(compatibility))));
            section.Add(CreateUnityMeshRow(mesh, targetFbx, targetUnityMesh, mapping));

            var parentRow = new VisualElement();
            parentRow.style.flexDirection = FlexDirection.Row;
            parentRow.style.alignItems = Align.Center;
            parentRow.style.flexShrink = 1;

            var ownerPatch = meshHandle != null ? meshHandle.Root : null;
            var label = BlendShareInspectorUi.ValueLabel(ownerPatch != null ? ownerPatch.name : Localization.S("common.status.not_found"));
            label.style.minWidth = 100;
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            parentRow.Add(label);
            if (ownerPatch != null)
            {
                parentRow.Add(BlendShareInspectorUi.InlineButton(Localization.S("common.select"), () => Selection.activeObject = ownerPatch));
            }

            section.Add(BlendShareInspectorUi.LabeledRow(Localization.S("patch.mesh_data.parent_patch"), parentRow));

            if (compatibility.State != MeshFbxCompatibilityState.Verified)
            {
                BlendShareInspectorUi.AddHelpBox(section, compatibility.Message, HelpBoxMessageType.Info);
            }

            if (!mapping.IsReady && targetFbx != null)
            {
                BlendShareInspectorUi.AddHelpBox(section, mapping.Message, HelpBoxMessageType.Warning);
            }

            return section;
        }

        private static VisualElement CreateUnityMeshRow(
            MeshDocument mesh,
            GameObject targetFbx,
            Mesh targetUnityMesh,
            MeshMappingStatus mapping)
        {
            if (targetUnityMesh != null)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                row.style.minWidth = 0;

                var meshField = BlendShareInspectorUi.RowField(new ObjectField
                {
                    objectType = typeof(Mesh),
                    allowSceneObjects = false,
                    value = targetUnityMesh
                });
                meshField.SetEnabled(false);
                meshField.style.minWidth = 120;
                row.Add(meshField);

                if (mapping.IsReady)
                {
                    var icon = BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                        MeshFbxCompatibilityState.Verified,
                        Localization.S("common.status.verified"),
                        mapping.Message));
                    icon.style.flexShrink = 0;
                    row.Add(icon);
                }

                return BlendShareInspectorUi.LabeledRow(Localization.S("patch.mapping.unity_mesh"), row);
            }

            string message = targetFbx != null
                ? Localization.S("patch.mapping.not_resolved")
                : Localization.S("patch.mapping.target_missing");
            return BlendShareInspectorUi.Row(Localization.S("patch.mapping.unity_mesh"), message);
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

        private static VisualElement CreateFeatureSections(MeshDocument mesh, FeatureInspectorContext ctx)
        {
            var section = new VisualElement();
            var features = mesh.Features
                .Where(f => f != null)
                .OrderBy(f => FeatureInspectorRegistry.Resolve(f).DisplayOrder)
                .ToArray();

            if (features.Length == 0)
            {
                section.Add(new HelpBox(Localization.S("patch.mesh_data.no_data"), HelpBoxMessageType.Info));
                return section;
            }

            foreach (var feature in features)
            {
                var drawer = FeatureInspectorRegistry.Resolve(feature);
                section.Add(drawer.CreateInspectorElement(feature, ctx));
            }

            return section;
        }

        private static VisualElement CreateAdvancedSection(MeshDocument mesh, FeatureInspectorContext ctx)
        {
            var foldout = new Foldout { text = Localization.S("common.advanced"), value = false };
            foldout.Add(CreateTopologyFingerprintDrawer(mesh));

            double unitMeters = mesh.SpaceProvenance?.OriginRawMetersPerUnit ?? 1d;
            foldout.Add(new BlendShareRecordView(
                mesh.SpaceProvenance ?? new MeshSpaceProvenance(),
                ctx.PayloadSource,
                "Space Provenance",
                startExpanded: false,
                positionUnitMeters: unitMeters));
            foldout.Add(new BlendShareRecordView(
                mesh.OriginBinding,
                ctx.PayloadSource,
                "Origin Binding",
                startExpanded: false,
                positionUnitMeters: unitMeters));
            foldout.Add(new BlendShareRecordView(
                mesh,
                ctx.PayloadSource,
                "Raw Mesh Record",
                startExpanded: false,
                positionUnitMeters: unitMeters));
            return foldout;
        }

        private static VisualElement CreateTopologyFingerprintDrawer(MeshDocument mesh)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.mesh_data.topology_fingerprint"));
            var signature = mesh?.Topology;
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.hash"), signature != null ? BlendShareInspectorUtility.ShortHash(signature.Hash) : "-"));
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.control_points"), signature != null ? signature.ControlPointCount.ToString() : "-"));
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mesh_data.faces"), signature != null ? signature.FaceCount.ToString() : "-"));
            return section;
        }
    }
}
