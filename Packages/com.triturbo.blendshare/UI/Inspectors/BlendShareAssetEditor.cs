using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Pipeline.Artifacts;
using Triturbo.BlendShare.Pipeline.Persistence;
using Triturbo.BlendShare.Runtime.Assets;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Ui = Triturbo.BlendShare.Inspector.BlendShareInspectorUi;

namespace Triturbo.BlendShare.UI.Inspectors
{
    // Both imported handles use the same settings buffer, conflict resolution and manifest reader.
    public abstract class BlendShareDocumentEditor : UnityEditor.Editor
    {
        private VisualElement m_Root;
        private string m_Guid;
        private PatchDocument m_Document;
        private string m_Revision;
        private string m_CompatibilityKey;
        private Dictionary<string, MeshFbxCompatibilityStatus> m_Statuses;
        internal bool SuppressFlushOnDisable { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        protected abstract BlendShareAsset Patch { get; }

        protected virtual void OnEnable()
        {
            BlendShareSettingsSession.SettingsChanged += OnSettingsChanged;
            EditorApplication.projectChanged += ScheduleRefresh;
        }

        protected virtual void OnDisable()
        {
            BlendShareSettingsSession.SettingsChanged -= OnSettingsChanged;
            EditorApplication.projectChanged -= ScheduleRefresh;
            EditorApplication.delayCall -= Refresh;
            if (!SuppressFlushOnDisable && !string.IsNullOrEmpty(m_Guid)) BlendShareSettingsSession.Flush(m_Guid);
        }

        private void OnSettingsChanged(string guid, long revision)
        {
            if (guid == m_Guid) ScheduleRefresh();
        }

        protected void ScheduleRefresh()
        {
            EditorApplication.delayCall -= Refresh;
            EditorApplication.delayCall += Refresh;
        }

        public override VisualElement CreateInspectorGUI()
        {
            m_Root = Ui.CreateRoot();
            var root = m_Root;
            // InspectorElement disables custom UITK content on NotEditable imported assets after construction.
            root.RegisterCallback<AttachToPanelEvent>(_ => root.schedule.Execute(() =>
            {
                root.GetFirstAncestorOfType<InspectorElement>()?.SetEnabled(true);
                root.SetEnabled(true);
            }));
            Refresh();
            Localization.RebuildOnLanguageChange(root, Refresh);
            return root;
        }

        private void Refresh()
        {
            if (!this || m_Root == null) return;
            m_Root.Clear();
            var patch = Patch;
            if (!patch)
            {
                m_Root.Add(new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning));
                return;
            }
            string path = AssetDatabase.GetAssetPath(patch);
            m_Guid = AssetDatabase.AssetPathToGUID(path);
            m_Root.viewDataKey = m_Guid + "/" + target.name;
            var buffer = BlendShareSettingsSession.GetOrCreateBuffer(patch);
            bool readOnly = BlendShareSettingsSession.IsAssetReadOnly(path, out var reason) || !buffer;
            if (readOnly) m_Root.Add(new HelpBox($"Read-only patch ({reason})", HelpBoxMessageType.Info));
            if (buffer && buffer.HasExternalConflict)
            {
                var conflict = new HelpBox("External changes to .blendshare.meta conflict with pending settings.", HelpBoxMessageType.Warning);
                conflict.Add(Ui.SmallButton("Reload From Disk", () => BlendShareSettingsSession.ReloadFromDiskAndDiscardConflict(m_Guid)));
                conflict.Add(Ui.SmallButton("Force Overwrite Disk", () => BlendShareSettingsSession.ForceOverwriteDiskFromBuffer(m_Guid)));
                m_Root.Add(conflict);
            }
            if (buffer && !string.IsNullOrEmpty(buffer.LastCommitError))
                m_Root.Add(new HelpBox(buffer.LastCommitError, HelpBoxMessageType.Warning));
            if (m_Document == null || m_Revision != patch.SourceRevision)
            {
                var result = BlendShareSourceRepository.Shared.ReadPatchDocument(patch);
                if (!result.Success)
                {
                    m_Root.Add(new HelpBox(result.Message, HelpBoxMessageType.Error));
                    return;
                }
                m_Document = result.Value;
                m_Revision = patch.SourceRevision;
            }
            Build(m_Root, m_Document, mesh => new FeatureInspectorContext(patch, m_Guid, mesh?.MeshId, buffer,
                readOnly, mesh, m_Document.Armatures, PayloadSource));
        }

        protected abstract void Build(VisualElement root, PatchDocument document, Func<MeshDocument, FeatureInspectorContext> context);

        protected void AddCompatibility(VisualElement parent, GameObject fbx, MeshDocument mesh)
        {
            var holder = new VisualElement();
            holder.Add(Ui.CompatibilityIcon(new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown,
                Localization.S("common.status.checking"), Localization.S("patch.compatibility.pending"))));
            parent.Add(holder);
            holder.schedule.Execute(() =>
            {
                string path = fbx ? AssetDatabase.GetAssetPath(fbx) : "";
                string key = Localization.CurrentLanguage + "/" + m_Revision + "/" + path +
                    (fbx ? AssetDatabase.GetAssetDependencyHash(path).ToString() : "");
                if (m_CompatibilityKey != key)
                {
                    m_Statuses = BlendShareInspectorUtility.GetFbxCompatibilityStatuses(fbx, m_Document.Meshes);
                    m_CompatibilityKey = key;
                }
                holder.Clear();
                if (m_Statuses.TryGetValue(mesh.MeshId, out var status))
                    holder.Add(Ui.CompatibilityIcon(status));
            });
        }

        protected static void AddFeatures(VisualElement parent, MeshDocument mesh, FeatureInspectorContext context, bool compact)
        {
            foreach (var feature in mesh.Features.Where(f => f != null)
                         .OrderBy(f => FeatureInspectorRegistry.Resolve(f).DisplayOrder))
            {
                var drawer = FeatureInspectorRegistry.Resolve(feature);
                parent.Add(compact ? drawer.CreateCompactBadgeElement(feature, context) : drawer.CreateInspectorElement(feature, context));
            }
        }
    }

    [CustomEditor(typeof(BlendShareAsset))]
    public sealed class BlendShareAssetEditor : BlendShareDocumentEditor
    {
        private bool m_Force;
        protected override BlendShareAsset Patch => target as BlendShareAsset;

        protected override void Build(VisualElement root, PatchDocument document, Func<MeshDocument, FeatureInspectorContext> context)
        {
            var ctx = context(null);
            var settings = ctx.Buffer?.Settings;
            var fbx = settings?.DefaultTargetFbx;
            root.Add(new IMGUIContainer(EditorWidgets.ShowBlendShareBanner));
            root.Add(Localization.CreateLanguageDropdown());
            var targetSection = Ui.Section(Localization.S("patch.inspector.title"));
            root.Add(targetSection);
            var field = new ObjectField(Localization.S("common.target_fbx"))
            {
                objectType = typeof(GameObject), allowSceneObjects = false, value = fbx
            };
            field.SetEnabled(!ctx.IsReadOnly);
            field.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue && !EditorWidgets.IsFBXGameObject(evt.newValue))
                {
                    field.SetValueWithoutNotify(evt.previousValue);
                    return;
                }
                m_Force = false;
                ctx.RecordEditAndCommit("Set Default Target FBX", s => s.DefaultTargetFbx = evt.newValue as GameObject);
            });
            targetSection.Add(field);
            if (!fbx) targetSection.Add(new HelpBox(Localization.S("patch.target_missing"), HelpBoxMessageType.Info));
            var state = BlendShareFbxMetadataService.GetPatchState(fbx, Patch);
            if (fbx)
            {
                var history = new Foldout { text = Localization.S("patch.metadata.applied_patches") + $" ({state.ActiveRecordCount})", value = false };
                string backupPath = BlendShareFbxMetadataService.ResolveBackupPath(state.Metadata);
                string status = string.IsNullOrEmpty(backupPath) ? Localization.S("common.none") :
                    File.Exists(BlendSharePathUtility.ResolvePhysicalPath(backupPath)) ? backupPath :
                    backupPath + " · " + Localization.S("common.status.not_found");
                history.Add(Ui.Row(Localization.S("backup.path"), status));
                foreach (var record in state.Metadata.ActiveRecords)
                    if (record != null) history.Add(Ui.Row(record.BlendShareName, record.AppliedAtUtc));
                targetSection.Add(history);
            }

            var meshes = Ui.Section(Localization.S("common.meshes"));
            var handles = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(Patch)).OfType<BlendShareMeshAsset>()
                .ToDictionary(handle => handle.MeshId);
            foreach (var mesh in document.Meshes)
            {
                var card = Ui.Box();
                var heading = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                var title = Ui.Header(mesh.Name, mesh.NodePath);
                heading.Add(title);
                AddCompatibility(heading, fbx, mesh);
                if (handles.TryGetValue(mesh.MeshId, out var handle))
                {
                    Ui.RegisterClickAction(card, () => EditorGUIUtility.PingObject(handle));
                    Ui.RegisterDoubleClickAction(card, () => Selection.activeObject = handle);
                    heading.Add(Ui.InlineButton(Localization.S("common.select"), () => Selection.activeObject = handle));
                }
                card.Add(heading);
                var badges = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
                AddFeatures(badges, mesh, context(mesh), true);
                card.Add(badges);
                meshes.Add(card);
            }
            if (document.Meshes.Count == 0) meshes.Add(new HelpBox(Localization.S("patch.contents.no_mesh_data"), HelpBoxMessageType.Info));
            root.Add(meshes);
            root.Add(Actions(fbx, state, settings?.DefaultGeneratedAssetName));
            var advanced = ctx.Advanced(document);
            var name = new TextField(Localization.S("common.default_asset_name")) { value = settings?.DefaultGeneratedAssetName, isDelayed = true };
            name.RegisterValueChangedCallback(evt => ctx.RecordEditAndCommit("Set Generated Asset Name", s => s.DefaultGeneratedAssetName = evt.newValue));
            name.SetEnabled(!ctx.IsReadOnly);
            advanced.Insert(0, name);
            advanced.Add(Ui.Row(Localization.S("common.patch_id"), Patch.PatchId));
            advanced.Add(Ui.Row("Format", $"BLSHARE1 v{Patch.FormatVersion}"));
            advanced.Add(Ui.Row("Source Revision", Patch.SourceRevision));
            var entries = new Foldout { text = $"Container ({Patch.TotalFileLength:N0} bytes, {Patch.Entries.Count} entries)", value = false };
            foreach (var entry in Patch.Entries) entries.Add(Ui.Row($"{entry.EntryIndex} {entry.TypeId}", $"{entry.Slice.Offset:N0} + {entry.Slice.Length:N0}"));
            advanced.Add(entries);
            const string logos = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/";
            advanced.Add(Ui.FooterBox(Localization.S("patch.created_by"),
                ("GitHub", "https://github.com/Tr1turbo/BlendShare", logos + "GitHub/GitHub_Invertocat_White.png", Localization.S("common.github.tooltip")),
                ("X", "https://x.com/TR1TURBO", logos + "X/logo-white.png", Localization.S("common.x.tooltip")),
                ("Booth", "https://triturbo.booth.pm/", logos + "Booth/logo_icon_white.png", Localization.S("common.booth.tooltip"))));
            root.Add(advanced);
        }

        private VisualElement Actions(GameObject fbx, BlendShareFbxPatchState state, string defaultName)
        {
            var section = Ui.Section(Localization.S("patch.actions.title"));
            bool available = BlendShareGenerationService.IsFbxExportAvailable;
            if (!available) section.Add(new HelpBox(Localization.S("common.fbx_sdk_missing"), HelpBoxMessageType.Info));
            bool canWrite = fbx && available;
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var apply = Ui.SmallButton(Localization.S("patch.apply"), () =>
            {
                if (state.HasPatch && !Confirm("patch.apply.confirm", "patch.apply")) return;
                Finish(BlendShareGenerationService.ApplyPatch(fbx, Patch, state.HasPatch && m_Force, out var message), message);
            });
            apply.style.flexGrow = 1;
            apply.SetEnabled(canWrite && (!state.HasPatch || m_Force));
            row.Add(apply);
            if (state.HasPatch)
            {
                var toggle = new Button(() => { m_Force = !m_Force; ScheduleRefresh(); });
                toggle.Add(Ui.IconElement(m_Force ? "Unlocked" : "Locked"));
                toggle.tooltip = Localization.S("patch.apply.locked_exact");
                row.Add(toggle);
            }
            var restore = Ui.SmallButton(Localization.S("patch.restore_to_original"), () =>
            {
                if (Confirm("patch.restore", "common.restore"))
                    Finish(BlendShareGenerationService.RestoreToOriginal(fbx, out var message), message);
            });
            restore.SetEnabled(canWrite && state.ActiveRecordCount > 0);
            row.Add(restore);
            var menu = new ToolbarMenu();
            menu.menu.AppendAction(Localization.S("patch.revert_this"), _ =>
            {
                if (Confirm("patch.revert", "common.revert"))
                    Finish(BlendShareGenerationService.RevertPatch(fbx, Patch, out var message), message);
            }, state.CanRevertPatch ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.SetEnabled(canWrite && state.HasExactPatch);
            row.Add(menu);
            section.Add(row);
            var create = Ui.SmallButton(Localization.S("patch.create_fbx"), () => CreateOutput(true));
            create.SetEnabled(canWrite);
            section.Add(create);
            var artifact = Ui.SmallButton(Localization.S("asset.create"), () => CreateOutput(false));
            artifact.SetEnabled(fbx);
            section.Add(artifact);
            return section;

            void CreateOutput(bool export)
            {
                if (export && state.HasPatch && !Confirm("patch.create_fbx.confirm", "patch.create_fbx")) return;
                string title = Localization.S(export ? "data.save_fbx.title" : "asset.save_title");
                string name = string.IsNullOrWhiteSpace(defaultName) ? Patch.name : defaultName;
                string path = EditorUtility.SaveFilePanelInProject(title, name, export ? "fbx" : "asset", title,
                    Path.GetDirectoryName(AssetDatabase.GetAssetPath(Patch)));
                if (string.IsNullOrEmpty(path)) return;
                if (export)
                {
                    if (string.Equals(path, AssetDatabase.GetAssetPath(fbx), StringComparison.OrdinalIgnoreCase))
                    {
                        Finish(false, "Choose an output path different from the target FBX.");
                        return;
                    }
                    Finish(BlendShareGenerationService.CreateFbx(fbx, new[] { Patch }, path, out var message), message);
                }
                else
                {
                    var result = BlendShareArtifactService.CreateArtifact(fbx, new[] { Patch }, path, out var error);
                    if (result) EditorGUIUtility.PingObject(result);
                    Finish(result, error);
                }
            }
        }

        private static bool Confirm(string key, string action) => EditorUtility.DisplayDialog(Localization.S(key + ".title"),
            Localization.S(key + ".message"), Localization.S(action), Localization.S("common.cancel"));

        private void Finish(bool success, string message)
        {
            if (!success) EditorUtility.DisplayDialog("BlendShare", message, Localization.S("common.ok"));
            else m_Force = false;
            ScheduleRefresh();
        }
    }
}
