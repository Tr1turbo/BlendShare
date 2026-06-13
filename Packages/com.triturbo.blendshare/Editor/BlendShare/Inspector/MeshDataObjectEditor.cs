using System;
using System.Linq;
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
                "Path",
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
                return BlendShareInspectorUi.LabeledRow("Unity Mesh", CreateUnityMeshReferenceRow(mesh, context, mapping, targetMesh));
            }

            string message = context.FbxGo != null
                ? context.GetUnityMeshResolutionError(mesh) ?? "Not resolved from current FBX"
                : "Original FBX is not assigned";
            return BlendShareInspectorUi.Row("Unity Mesh", message);
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
                    "Verified",
                    $"Mapping verified for {targetMesh.name}. Vertex hash: {compatibleMapping.UnityVerticesHashShort}"));
                icon.style.flexShrink = 0;
                row.Add(icon);
                return row;
            }

            var button = BlendShareInspectorUi.SmallButton("Create Mapping", () => CreateMapping(mesh, context, targetMesh));
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
            var section = BlendShareInspectorUi.Section("Mesh Summary");
            var compatibility = BlendShareInspectorUtility.GetFbxCompatibilityStatus(context.OwnerPatch, mesh);
            var mapping = GetMappingStatus(mesh, context, out var targetMesh);

            section.Add(BlendShareInspectorUi.LabeledRow(
                "Mesh",
                CreateTextWithStatusRow(mesh.m_Path, BlendShareInspectorUi.CompatibilityIcon(compatibility))));
            section.Add(CreateUnityMeshRow(mesh, context, mapping, targetMesh));

            if (showParentPatch)
            {
                var parentRow = new VisualElement();
                parentRow.style.flexDirection = FlexDirection.Row;
                parentRow.style.alignItems = Align.Center;
                parentRow.style.flexShrink = 1;

                var label = BlendShareInspectorUi.ValueLabel(context.OwnerPatch != null ? context.OwnerPatch.name : "Not found");
                label.style.minWidth = 100;
                label.style.flexGrow = 1;
                label.style.flexShrink = 1;

                parentRow.Add(label);
                if (context.OwnerPatch != null)
                {
                    parentRow.Add(BlendShareInspectorUi.InlineButton("Select", () => Selection.activeObject = context.OwnerPatch));
                }

                section.Add(BlendShareInspectorUi.LabeledRow("Parent Patch", parentRow));
            }

            section.Add(BlendShareInspectorUi.BadgeRow(BlendShareInspectorUi.Badge($"Mapping: {mapping.Label}", mapping.Kind)));

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
                section.Add(new HelpBox("This mesh does not contain extracted features.", HelpBoxMessageType.Info));
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
                    featureSection.Add(BlendShareInspectorUi.Row("Feature ID", feature.FeatureId));
                    featureSection.Add(new HelpBox("No custom feature editor is registered for this feature.", HelpBoxMessageType.Info));
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

            var section = BlendShareInspectorUi.Section("Unity Mappings");
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
                    section.Add(new HelpBox("No custom embedded editor is registered for this mapping.", HelpBoxMessageType.Info));
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
            var badge = BlendShareInspectorUi.Badge("Active", StatusKind.Success);
            badge.style.position = Position.Absolute;
            badge.style.top = 5;
            badge.style.right = 6;
            element.Add(badge);
        }

        private static VisualElement CreateAdvancedSection(MeshDataObject mesh, BlendShareEmbeddedEditorContext context)
        {
            var foldout = new Foldout { text = "Advanced" };
            foldout.Add(CreateTopologyFingerprintDrawer(mesh));
            foldout.Add(CreateMappingSections(mesh, context));
            foldout.Add(BlendShareInspectorUi.Row("Features", CountValid(mesh.Features).ToString()));
            foldout.Add(BlendShareInspectorUi.Row("Mappings", CountValid(mesh.m_Mappings).ToString()));
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
                return new MeshMappingStatus("Unknown", "Mesh data is missing.", false, StatusKind.Neutral);
            }

            if (context.FbxGo == null)
            {
                return new MeshMappingStatus("Missing Target", "Original FBX is not assigned.", false, StatusKind.Warning);
            }

            if (context.UnityMeshLookup == null)
            {
                return new MeshMappingStatus("Unknown", "Unity mesh target cannot be read.", false, StatusKind.Neutral);
            }

            if (!context.TryGetUnityMesh(mesh, out targetMesh))
            {
                return new MeshMappingStatus("Missing", context.GetUnityMeshResolutionError(mesh), false, StatusKind.Warning);
            }

            var resolvedTargetMesh = targetMesh;
            var mappings = mesh.m_Mappings ?? Array.Empty<UnityVertexMappingObject>();
            bool hasMapping = mappings.Any(mapping => mapping != null);
            bool hasValidMapping = mappings.Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, resolvedTargetMesh));
            if (hasValidMapping)
            {
                return new MeshMappingStatus("Ready", targetMesh.name, false, StatusKind.Success);
            }

            return new MeshMappingStatus(
                hasMapping ? "Invalid" : "Missing",
                hasMapping ? "No stored mapping matches the current Unity mesh import." : "No Unity mapping is stored for this mesh.",
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
                string message = mapping != null ? mapping.m_Report : "mapping generation failed";
                if (mapping != null)
                {
                    UnityEngine.Object.DestroyImmediate(mapping);
                }

                EditorUtility.DisplayDialog("Create Unity Mapping", message, "OK");
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
            var section = BlendShareInspectorUi.Section("Topology Fingerprint");
            var signature = mesh?.m_FbxTopologySignature;
            section.Add(BlendShareInspectorUi.Row("Hash", signature != null ? BlendShareInspectorUtility.ShortHash(signature.Hash) : "-"));
            section.Add(BlendShareInspectorUi.Row("Control Points", signature != null ? signature.ControlPointCount.ToString() : "-"));
            section.Add(BlendShareInspectorUi.Row("Faces", signature != null ? signature.FaceCount.ToString() : "-"));
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
        public string DisplayName => "Mesh Data";

        public VisualElement CreateEmbeddedInspector(BlendShareEmbeddedEditorContext context)
        {
            var mesh = context.EmbeddedObject as MeshDataObject ?? context.OwnerMeshData;
            if (mesh == null)
            {
                return new HelpBox("Mesh data is missing.", HelpBoxMessageType.Warning);
            }

            return MeshDataObjectEditor.CreateEmbeddedInspector(mesh, context, false);
        }
    }
}
