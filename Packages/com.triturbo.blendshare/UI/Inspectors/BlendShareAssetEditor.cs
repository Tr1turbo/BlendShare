using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Inspector;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Pipeline.Artifacts;
using Triturbo.BlendShare.Pipeline.Persistence;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.UI.ValueDrawers;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    /// <summary>
    /// Shared base inspector for BlendShare 2.0 imported handles (<see cref="BlendShareAsset"/> and
    /// <see cref="BlendShareMeshAsset"/>). Handles settings undo-buffer session routing, read-only / conflict banners,
    /// NotEditable imported control enabling, and manifest-only document loading (zero eager payload decode).
    /// </summary>
    public abstract class BlendShareDocumentEditor : UnityEditor.Editor
    {
        private VisualElement m_Root;
        private VisualElement m_StatusContainer;
        private VisualElement m_ContentContainer;
        private string m_Guid;
        private PatchDocument m_Document;
        private string m_Revision;

        internal bool SuppressFlushOnDisable { get; set; }
        public ILazyPayloadSource PayloadSource { get; set; }
        protected abstract BlendShareAsset Patch { get; }
        protected string AssetGuid => m_Guid;
        protected PatchDocument CachedDocument => m_Document;

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
            if (!SuppressFlushOnDisable && !string.IsNullOrEmpty(m_Guid))
            {
                BlendShareSettingsSession.Flush(m_Guid);
            }
        }

        private void OnSettingsChanged(string guid, long revision)
        {
            if (guid == m_Guid)
            {
                ScheduleRefresh();
            }
        }

        protected void ScheduleRefresh()
        {
            EditorApplication.delayCall -= Refresh;
            EditorApplication.delayCall += Refresh;
        }

        public override VisualElement CreateInspectorGUI()
        {
            m_Root = BlendShareInspectorUi.CreateRoot();
            var root = m_Root;
            // InspectorElement disables custom UITK content on NotEditable imported assets after construction.
            root.RegisterCallback<AttachToPanelEvent>(_ => root.schedule.Execute(() =>
            {
                root.GetFirstAncestorOfType<InspectorElement>()?.SetEnabled(true);
                root.SetEnabled(true);
            }));

            BuildStaticChrome(root);

            m_StatusContainer = new VisualElement();
            m_ContentContainer = new VisualElement();
            root.Add(m_StatusContainer);
            root.Add(m_ContentContainer);

            Refresh();
            Localization.RebuildOnLanguageChange(root, Refresh);
            return root;
        }

        protected virtual void BuildStaticChrome(VisualElement root)
        {
        }

        protected void Refresh()
        {
            if (!this || m_Root == null || m_StatusContainer == null || m_ContentContainer == null)
            {
                return;
            }

            m_StatusContainer.Clear();
            m_ContentContainer.Clear();

            var patch = Patch;
            if (!patch)
            {
                m_ContentContainer.Add(new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning));
                return;
            }

            string path = AssetDatabase.GetAssetPath(patch);
            m_Guid = AssetDatabase.AssetPathToGUID(path);
            m_Root.viewDataKey = m_Guid + "/" + target.name;

            var buffer = BlendShareSettingsSession.GetOrCreateBuffer(patch);
            bool readOnly = BlendShareSettingsSession.IsAssetReadOnly(path, out var reason) || !buffer;
            if (readOnly)
            {
                m_StatusContainer.Add(new HelpBox($"Read-only patch ({reason})", HelpBoxMessageType.Info));
            }

            if (buffer && buffer.HasExternalConflict)
            {
                var conflict = new HelpBox("External changes to .blendshare.meta conflict with pending settings.", HelpBoxMessageType.Warning);
                conflict.Add(BlendShareInspectorUi.SmallButton("Reload From Disk", () => BlendShareSettingsSession.ReloadFromDiskAndDiscardConflict(m_Guid)));
                conflict.Add(BlendShareInspectorUi.SmallButton("Force Overwrite Disk", () => BlendShareSettingsSession.ForceOverwriteDiskFromBuffer(m_Guid)));
                m_StatusContainer.Add(conflict);
            }

            if (buffer && !string.IsNullOrEmpty(buffer.LastCommitError))
            {
                m_StatusContainer.Add(new HelpBox(buffer.LastCommitError, HelpBoxMessageType.Warning));
            }

            if (m_Document == null || m_Revision != patch.SourceRevision)
            {
                var result = BlendShareSourceRepository.Shared.ReadPatchDocument(patch);
                if (!result.Success || result.Value == null)
                {
                    m_ContentContainer.Add(new HelpBox(result.Message, HelpBoxMessageType.Error));
                    return;
                }

                m_Document = result.Value;
                m_Revision = patch.SourceRevision;
            }

            BuildContent(
                m_ContentContainer,
                m_Document,
                mesh => new FeatureInspectorContext(
                    patch,
                    m_Guid,
                    mesh?.MeshId,
                    buffer,
                    readOnly,
                    mesh,
                    m_Document.Armatures,
                    PayloadSource));
        }

        protected abstract void BuildContent(
            VisualElement content,
            PatchDocument document,
            Func<MeshDocument, FeatureInspectorContext> contextFactory);

        protected static Dictionary<string, Mesh> CollectTargetUnityMeshes(GameObject targetFbx)
        {
            var map = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            if (targetFbx == null)
            {
                return map;
            }

            var rootTransform = targetFbx.transform;
            var skinned = targetFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                var smr = skinned[i];
                if (smr == null || smr.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(smr.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, smr.sharedMesh);
            }

            var filters = targetFbx.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(mf.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, mf.sharedMesh);
            }

            return map;
        }

        protected static string GetRelativeTransformPath(Transform transform, Transform root)
        {
            if (transform == null || transform == root)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var curr = transform;
            while (curr != null && curr != root)
            {
                parts.Add(curr.name);
                curr = curr.parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }
    }

    /// <summary>
    /// Verbatim port of the original <c>BlendShareObjectEditor</c> UI wired directly to BlendShare 2.0
    /// <see cref="BlendShareAsset"/>, <see cref="PatchDocument"/>, and <see cref="BlendShareSettingsSession"/>.
    /// </summary>
    [CustomEditor(typeof(BlendShareAsset))]
    public sealed class BlendShareAssetEditor : BlendShareDocumentEditor
    {
        private const string k_GitHubUrl = "https://github.com/Tr1turbo/BlendShare";
        private const string k_XUrl = "https://x.com/TR1TURBO";
        private const string k_BoothUrl = "https://triturbo.booth.pm/";
        private const string k_GitHubIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/GitHub/GitHub_Invertocat_White.png";
        private const string k_XIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/X/logo-white.png";
        private const string k_BoothIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/Booth/logo_icon_white.png";

        private static readonly Dictionary<string, Dictionary<string, MeshFbxCompatibilityStatus>> s_SharedCompatibilityStatusCache =
            new Dictionary<string, Dictionary<string, MeshFbxCompatibilityStatus>>(StringComparer.Ordinal);

        private Dictionary<string, MeshFbxCompatibilityStatus> m_CachedCompatibilityStatuses;
        private string m_CachedCompatibilityKey;
        private string m_PendingCompatibilityKey;
        private BlendShareFbxPatchState m_CachedPatchState;
        private string m_CachedPatchStateKey;
        private bool m_HasCachedPatchState;
        private bool m_ForceApplyUnlocked;

        protected override BlendShareAsset Patch => target as BlendShareAsset;

        protected override void BuildStaticChrome(VisualElement root)
        {
            root.Add(new IMGUIContainer(() =>
            {
                EditorWidgets.ShowBlendShareBanner();
                Localization.DrawLanguageSelection();
            }));
        }

        protected override void BuildContent(
            VisualElement content,
            PatchDocument document,
            Func<MeshDocument, FeatureInspectorContext> contextFactory)
        {
            var rootCtx = contextFactory(null);
            var settings = rootCtx.Buffer?.Settings;
            var targetFbx = settings?.DefaultTargetFbx;
            var compatibilityStatuses = GetCompatibilityStatuses(Patch, document, targetFbx, ScheduleRefresh);
            var unityMeshesByPath = CollectTargetUnityMeshes(targetFbx);

            content.Add(CreateHeader(Patch, targetFbx, rootCtx, compatibilityStatuses));
            content.Add(CreateMeshList(Patch, document, targetFbx, compatibilityStatuses, unityMeshesByPath, contextFactory));
            content.Add(CreateActionsSection(Patch, document, targetFbx, settings));
            content.Add(CreateAdvancedSection(Patch, document, settings, rootCtx));
        }

        private VisualElement CreateHeader(
            BlendShareAsset patch,
            GameObject targetFbx,
            FeatureInspectorContext ctx,
            IReadOnlyDictionary<string, MeshFbxCompatibilityStatus> compatibilityStatuses)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.inspector.title"));

            var targetField = BlendShareInspectorUi.RowField(new ObjectField
            {
                objectType = typeof(GameObject),
                allowSceneObjects = false,
                value = targetFbx
            });
            targetField.SetEnabled(!ctx.IsReadOnly);
            targetField.RegisterValueChangedCallback(evt =>
            {
                var next = evt.newValue as GameObject;
                if (next != null && !EditorWidgets.IsFBXGameObject(next))
                {
                    targetField.SetValueWithoutNotify(evt.previousValue);
                    return;
                }

                m_ForceApplyUnlocked = false;
                ClearCompatibilityCaches();
                ctx.RecordEditAndCommit("Set Default Target FBX", s => s.DefaultTargetFbx = next);
            });

            var statusIcon = CreatePatchCompatibilityIcon(compatibilityStatuses);
            section.Add(BlendShareInspectorUi.LabeledRow(Localization.S("common.target_fbx"), targetField, statusIcon));
            section.Add(CreateFbxPatchMetadata(patch, targetFbx));

            if (targetFbx == null)
            {
                section.Add(new HelpBox(Localization.S("patch.target_missing"), HelpBoxMessageType.Warning));
            }

            if (!BlendShareGenerationService.IsFbxExportAvailable)
            {
                section.Add(new HelpBox(Localization.S("common.fbx_sdk_missing"), HelpBoxMessageType.Warning));
            }

            return section;
        }

        private VisualElement CreateFbxPatchMetadata(BlendShareAsset patch, GameObject targetFbx)
        {
            var state = patch != null && targetFbx != null
                ? GetPatchStateCached(patch, targetFbx)
                : default;
            var records = state.Metadata?.ActiveRecords ?? Array.Empty<BlendShareFbxPatchRecord>();
            if (records.Length == 0)
            {
                return new VisualElement();
            }

            var list = new VisualElement();
            list.style.marginTop = 3;
            list.Add(BlendShareInspectorUi.Row(Localization.S("patch.metadata.applied_patches"), records.Length.ToString()));

            foreach (var record in records.Where(record => record != null))
            {
                string name = string.IsNullOrWhiteSpace(record.BlendShareName) ? record.PatchId : record.BlendShareName;
                list.Add(BlendShareInspectorUi.Row(string.Empty, name));
            }

            return list;
        }

        private VisualElement CreateMeshList(
            BlendShareAsset patch,
            PatchDocument document,
            GameObject targetFbx,
            IReadOnlyDictionary<string, MeshFbxCompatibilityStatus> compatibilityStatuses,
            IReadOnlyDictionary<string, Mesh> unityMeshesByPath,
            Func<MeshDocument, FeatureInspectorContext> contextFactory)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("common.meshes"));
            var meshes = document.Meshes.Where(m => m != null).ToArray();
            if (meshes.Length == 0)
            {
                section.Add(new HelpBox(Localization.S("patch.contents.no_mesh_data"), HelpBoxMessageType.Info));
                return section;
            }

            var handlesByMeshId = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(patch))
                .OfType<BlendShareMeshAsset>()
                .Where(h => h != null && !string.IsNullOrEmpty(h.MeshId))
                .GroupBy(h => h.MeshId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var mesh in meshes)
            {
                handlesByMeshId.TryGetValue(mesh.MeshId, out var meshHandle);
                compatibilityStatuses.TryGetValue(mesh.MeshId, out var compat);
                string normPath = FbxNameUtility.NormalizePath(mesh.NodePath ?? string.Empty);
                unityMeshesByPath.TryGetValue(normPath, out var targetUnityMesh);

                section.Add(BlendShareMeshAssetEditor.CreateCompactEmbeddedInspector(
                    mesh,
                    meshHandle,
                    targetFbx,
                    targetUnityMesh,
                    compat,
                    contextFactory(mesh)));
            }

            return section;
        }

        private VisualElement CreateActionsSection(
            BlendShareAsset patch,
            PatchDocument document,
            GameObject targetFbx,
            BlendShareImportSettings settings)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.actions.title"));
            section.Add(new IMGUIContainer(() => DrawActions(patch, document, targetFbx, settings)));
            return section;
        }

        private void DrawActions(
            BlendShareAsset patch,
            PatchDocument document,
            GameObject targetFbx,
            BlendShareImportSettings settings)
        {
            bool prevEnabled = GUI.enabled;
            GUI.enabled = true;
            try
            {
                bool hasTarget = targetFbx != null;
                bool fbxExportAvailable = BlendShareGenerationService.IsFbxExportAvailable;
                var patchState = hasTarget
                    ? GetPatchStateCached(patch, targetFbx)
                    : default;

                if (!fbxExportAvailable)
                {
                    EditorGUILayout.HelpBox(Localization.S("common.fbx_sdk_missing"), MessageType.Warning);
                }

                using (new EditorGUI.DisabledScope(!hasTarget || !fbxExportAvailable))
                {
                    DrawPatchActions(patch, targetFbx, patchState);
                    DrawCreateFbxControl(patch, targetFbx, patchState, settings);
                }

                bool canGenerateArtifact = hasTarget && document.Meshes.Count > 0;
                if (!hasTarget)
                {
                    EditorGUILayout.HelpBox(Localization.S("asset.mapping.target_missing"), MessageType.Warning);
                }

                using (new EditorGUI.DisabledScope(!canGenerateArtifact))
                {
                    if (GUILayout.Button(Localization.G("asset.create")))
                    {
                        RunCreateArtifact(patch, targetFbx, settings);
                    }
                }
            }
            finally
            {
                GUI.enabled = prevEnabled;
            }
        }

        private void DrawPatchActions(BlendShareAsset patch, GameObject targetFbx, BlendShareFbxPatchState patchState)
        {
            bool applyLocked = patchState.HasPatch && !m_ForceApplyUnlocked;
            bool showRestore = patchState.ActiveRecordCount > 0;
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float spacing = 4f;
            float halfWidth = (row.width - spacing) * 0.5f;
            var applyRect = new Rect(row.x, row.y, halfWidth, row.height);
            var restoreRect = new Rect(applyRect.xMax + spacing, row.y, halfWidth, row.height);

            DrawApplyPatchControl(applyRect, patch, targetFbx, patchState, applyLocked);
            DrawRestoreControl(restoreRect, patch, targetFbx, patchState, showRestore);

            if (applyLocked)
            {
                string lockMessage = patchState.HasExactPatch
                    ? Localization.S("patch.apply.locked_exact")
                    : Localization.S("patch.apply.locked_conflict");
                EditorGUILayout.HelpBox(lockMessage, MessageType.Info);
            }

            if (patchState.HasExactPatch && patchState.ActiveRecordCount > 1 && !patchState.CanRevertPatch)
            {
                EditorGUILayout.HelpBox(Localization.S("patch.revert.missing_record"), MessageType.Warning);
            }
        }

        private void DrawApplyPatchControl(
            Rect rect,
            BlendShareAsset patch,
            GameObject targetFbx,
            BlendShareFbxPatchState patchState,
            bool applyLocked)
        {
            Rect buttonRect = rect;
            if (patchState.HasPatch)
            {
                const float lockWidth = 14f;
                var lockRect = new Rect(rect.x, rect.y + 1f, lockWidth, rect.height - 2f);
                GUIContent lockIcon = m_ForceApplyUnlocked
                    ? EditorGUIUtility.IconContent("Unlocked")
                    : EditorGUIUtility.IconContent("Locked");
                if (GUI.Button(lockRect, lockIcon, GUIStyle.none))
                {
                    m_ForceApplyUnlocked = !m_ForceApplyUnlocked;
                }

                buttonRect = new Rect(lockRect.xMax + 1f, rect.y, rect.width - lockWidth - 1f, rect.height);
            }

            using (new EditorGUI.DisabledScope(applyLocked))
            {
                if (GUI.Button(buttonRect, Localization.S("patch.apply")))
                {
                    RunApplyPatch(patch, targetFbx, patchState);
                }
            }
        }

        private void RunApplyPatch(BlendShareAsset patch, GameObject targetFbx, BlendShareFbxPatchState patchState)
        {
            bool force = patchState.HasPatch && m_ForceApplyUnlocked;
            if (force && !EditorUtility.DisplayDialog(
                    Localization.S("patch.apply.confirm.title"),
                    Localization.S("patch.apply.confirm.message"),
                    Localization.S("common.apply"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            if (BlendShareGenerationService.ApplyPatch(targetFbx, patch, force, out string message))
            {
                m_ForceApplyUnlocked = false;
                ClearPatchStateCache();
                ScheduleRefresh();
            }
            else
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.apply"),
                    message ?? Localization.S("patch.apply.failed"),
                    Localization.S("common.ok"));
            }
        }

        private void DrawCreateFbxControl(
            BlendShareAsset patch,
            GameObject targetFbx,
            BlendShareFbxPatchState patchState,
            BlendShareImportSettings settings)
        {
            if (GUILayout.Button(Localization.S("patch.create_fbx")))
            {
                RunCreateFbx(patch, targetFbx, patchState, settings);
            }
        }

        private void RunCreateFbx(
            BlendShareAsset patch,
            GameObject targetFbx,
            BlendShareFbxPatchState patchState,
            BlendShareImportSettings settings)
        {
            if (patchState.HasPatch &&
                !EditorUtility.DisplayDialog(
                    Localization.S("patch.create_fbx.confirm.title"),
                    Localization.S("patch.create_fbx.confirm.message"),
                    Localization.S("common.generate"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            string defaultName = !string.IsNullOrWhiteSpace(settings?.DefaultGeneratedAssetName)
                ? settings.DefaultGeneratedAssetName
                : patch.name;
            string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("data.save_fbx.title"),
                defaultName,
                "fbx",
                Localization.S("data.save_file.message"),
                folderPath);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (string.Equals(path, AssetDatabase.GetAssetPath(targetFbx), StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.create_fbx"),
                    "Choose an output path different from the target FBX.",
                    Localization.S("common.ok"));
                return;
            }

            if (!BlendShareGenerationService.CreateFbx(targetFbx, new[] { patch }, path, out string message))
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.create_fbx"),
                    message ?? Localization.S("patch.create_fbx.failed"),
                    Localization.S("common.ok"));
            }

            ClearPatchStateCache();
            ScheduleRefresh();
        }

        private void RunCreateArtifact(BlendShareAsset patch, GameObject targetFbx, BlendShareImportSettings settings)
        {
            if (targetFbx == null)
            {
                Localization.DisplayDialog("patch.fbx_missing", Localization.S("common.ok"));
                return;
            }

            string baseName = !string.IsNullOrWhiteSpace(settings?.DefaultGeneratedAssetName)
                ? settings.DefaultGeneratedAssetName
                : patch.name;
            string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("asset.save_title"),
                $"{baseName}_Assets",
                "asset",
                Localization.S("data.save_file.message"),
                folderPath);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var artifact = BlendShareArtifactService.CreateArtifact(targetFbx, new[] { patch }, path, out string error);
            if (artifact != null)
            {
                EditorGUIUtility.PingObject(artifact);
            }
            else if (!string.IsNullOrEmpty(error))
            {
                EditorUtility.DisplayDialog(Localization.S("asset.create"), error, Localization.S("common.ok"));
            }
        }

        private void DrawRestoreControl(
            Rect rect,
            BlendShareAsset patch,
            GameObject targetFbx,
            BlendShareFbxPatchState patchState,
            bool showRestore)
        {
            bool showDropdown = patchState.ActiveRecordCount > 1 && patchState.HasExactPatch;
            Rect restoreButtonRect = rect;
            Rect dropdownRect = default;
            if (showDropdown)
            {
                const float dropdownWidth = 20f;
                restoreButtonRect = new Rect(rect.x, rect.y, rect.width - dropdownWidth, rect.height);
                dropdownRect = new Rect(restoreButtonRect.xMax, rect.y, dropdownWidth, rect.height);
            }

            using (new EditorGUI.DisabledScope(!showRestore))
            {
                GUIStyle restoreStyle = showDropdown ? EditorStyles.miniButtonLeft : GUI.skin.button;
                if (GUI.Button(restoreButtonRect, Localization.S("patch.restore_to_original"), restoreStyle))
                {
                    RunRestoreToOriginal(targetFbx);
                }
            }

            if (showDropdown && GUI.Button(dropdownRect, EditorGUIUtility.IconContent("icon dropdown"), EditorStyles.miniButtonRight))
            {
                var menu = new GenericMenu();
                if (patchState.CanRevertPatch)
                {
                    menu.AddItem(new GUIContent(Localization.S("patch.revert_this")), false, () => RunRevertPatch(patch, targetFbx));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent(Localization.S("patch.revert_this")));
                }

                menu.DropDown(dropdownRect);
            }
        }

        private void RunRevertPatch(BlendShareAsset patch, GameObject targetFbx)
        {
            if (!EditorUtility.DisplayDialog(
                    Localization.S("patch.revert.title"),
                    Localization.S("patch.revert.message"),
                    Localization.S("common.revert"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            if (BlendShareGenerationService.RevertPatch(targetFbx, patch, out string message))
            {
                m_ForceApplyUnlocked = false;
                ClearPatchStateCache();
                ScheduleRefresh();
            }
            else
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.revert.title"),
                    message ?? Localization.S("patch.revert.failed"),
                    Localization.S("common.ok"));
            }
        }

        private void RunRestoreToOriginal(GameObject targetFbx)
        {
            if (!EditorUtility.DisplayDialog(
                    Localization.S("patch.restore.title"),
                    Localization.S("patch.restore.message"),
                    Localization.S("common.restore"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            if (BlendShareGenerationService.RestoreToOriginal(targetFbx, out string message))
            {
                m_ForceApplyUnlocked = false;
                ClearPatchStateCache();
                ScheduleRefresh();
            }
            else
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.restore.title"),
                    message ?? Localization.S("patch.restore.failed"),
                    Localization.S("common.ok"));
            }
        }

        private VisualElement CreateAdvancedSection(
            BlendShareAsset patch,
            PatchDocument document,
            BlendShareImportSettings settings,
            FeatureInspectorContext ctx)
        {
            var foldout = new Foldout { text = Localization.S("patch.advanced"), value = false };

            var defaultName = new TextField(Localization.S("common.default_asset_name"))
            {
                value = settings?.DefaultGeneratedAssetName ?? string.Empty,
                isDelayed = true
            };
            defaultName.SetEnabled(!ctx.IsReadOnly);
            defaultName.RegisterValueChangedCallback(evt =>
                ctx.RecordEditAndCommit("Set Generated Asset Name", s => s.DefaultGeneratedAssetName = evt.newValue));
            foldout.Add(defaultName);

            foldout.Add(BlendShareInspectorUi.Row(Localization.S("common.patch_id"), patch.PatchId));

            var containerFoldout = new Foldout
            {
                text = $"Container (BLSHARE1 v{patch.FormatVersion}, {patch.TotalFileLength:N0} B, {patch.Entries.Count} entries)",
                value = false
            };
            foreach (var entry in patch.Entries)
            {
                containerFoldout.Add(BlendShareInspectorUi.Row($"{entry.EntryIndex} {entry.TypeId}", $"{entry.Slice.Offset:N0} + {entry.Slice.Length:N0}"));
            }
            foldout.Add(containerFoldout);

            foldout.Add(new BlendShareRecordView(document, ctx.PayloadSource, "Raw Patch Document", startExpanded: false));

            foldout.Add(BlendShareInspectorUi.FooterBox(
                Localization.S("patch.created_by"),
                (Localization.S("common.github"), k_GitHubUrl, k_GitHubIconPath, Localization.S("common.github.tooltip")),
                (Localization.S("common.x"), k_XUrl, k_XIconPath, Localization.S("common.x.tooltip")),
                (Localization.S("common.booth"), k_BoothUrl, k_BoothIconPath, Localization.S("common.booth.tooltip"))));
            return foldout;
        }

        private Dictionary<string, MeshFbxCompatibilityStatus> GetCompatibilityStatuses(
            BlendShareAsset patch,
            PatchDocument document,
            GameObject targetFbx,
            Action refresh)
        {
            var meshes = document.Meshes.Where(m => m != null).ToArray();
            string key = CreateCompatibilityCacheKey(patch, targetFbx, meshes);
            if (m_CachedCompatibilityStatuses != null && string.Equals(m_CachedCompatibilityKey, key, StringComparison.Ordinal))
            {
                return m_CachedCompatibilityStatuses;
            }

            if (s_SharedCompatibilityStatusCache.TryGetValue(key, out m_CachedCompatibilityStatuses))
            {
                m_CachedCompatibilityKey = key;
                return m_CachedCompatibilityStatuses;
            }

            m_CachedCompatibilityStatuses = CreatePendingCompatibilityStatuses(meshes);
            m_CachedCompatibilityKey = key;
            ScheduleCompatibilityVerification(targetFbx, meshes, key, refresh);
            return m_CachedCompatibilityStatuses;
        }

        private static Dictionary<string, MeshFbxCompatibilityStatus> CreatePendingCompatibilityStatuses(IEnumerable<MeshDocument> meshes)
        {
            var statuses = new Dictionary<string, MeshFbxCompatibilityStatus>(StringComparer.Ordinal);
            foreach (var mesh in meshes ?? Array.Empty<MeshDocument>())
            {
                if (mesh == null || string.IsNullOrEmpty(mesh.MeshId)) continue;
                statuses[mesh.MeshId] = new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.checking"),
                    Localization.S("patch.compatibility.pending"));
            }

            return statuses;
        }

        private void ScheduleCompatibilityVerification(
            GameObject targetFbx,
            MeshDocument[] meshes,
            string key,
            Action refresh)
        {
            if (string.Equals(m_PendingCompatibilityKey, key, StringComparison.Ordinal))
            {
                return;
            }

            m_PendingCompatibilityKey = key;
            EditorApplication.delayCall += () =>
            {
                if (this == null || target == null || !string.Equals(m_PendingCompatibilityKey, key, StringComparison.Ordinal))
                {
                    return;
                }

                var statuses = BlendShareInspectorUtility.GetFbxCompatibilityStatuses(targetFbx, meshes);
                if (s_SharedCompatibilityStatusCache.Count > 16)
                {
                    s_SharedCompatibilityStatusCache.Clear();
                }

                s_SharedCompatibilityStatusCache[key] = statuses;
                m_CachedCompatibilityStatuses = statuses;
                m_CachedCompatibilityKey = key;
                m_PendingCompatibilityKey = null;
                refresh?.Invoke();
            };
        }

        private static string CreateCompatibilityCacheKey(BlendShareAsset patch, GameObject targetFbx, IReadOnlyList<MeshDocument> meshes)
        {
            var builder = new StringBuilder();
            builder.Append(Localization.CurrentLanguage);
            builder.Append('|');
            builder.Append(patch?.SourceRevision ?? string.Empty);
            builder.Append('|');
            if (targetFbx != null)
            {
                string targetPath = AssetDatabase.GetAssetPath(targetFbx);
                builder.Append(targetPath);
                builder.Append(':');
                builder.Append(AssetDatabase.GetAssetDependencyHash(targetPath));
            }
            builder.Append('|');
            foreach (var mesh in meshes ?? Array.Empty<MeshDocument>())
            {
                var sig = mesh?.Topology;
                builder.Append(mesh?.MeshId ?? string.Empty);
                builder.Append(':');
                builder.Append(mesh?.NodePath ?? string.Empty);
                builder.Append(':');
                builder.Append(sig?.Hash ?? string.Empty);
                builder.Append(':');
                builder.Append(sig?.ControlPointCount ?? -1);
                builder.Append('|');
            }

            return builder.ToString();
        }

        private static VisualElement CreatePatchCompatibilityIcon(
            IReadOnlyDictionary<string, MeshFbxCompatibilityStatus> compatibilityStatuses)
        {
            if (compatibilityStatuses == null || compatibilityStatuses.Count == 0)
            {
                return null;
            }

            var statuses = compatibilityStatuses.Values.ToArray();
            if (statuses.All(status => status.State == MeshFbxCompatibilityState.Verified))
            {
                return BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Verified,
                    Localization.S("common.status.verified"),
                    Localization.S("patch.compatibility.all_verified")));
            }

            if (statuses.Any(status => status.State == MeshFbxCompatibilityState.Incompatible))
            {
                return BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Incompatible,
                    Localization.S("common.status.incompatible"),
                    Localization.S("patch.compatibility.has_incompatible")));
            }

            if (statuses.Any(status => status.State == MeshFbxCompatibilityState.MissingTargetMesh))
            {
                return BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.MissingTargetMesh,
                    Localization.S("common.status.missing_target_mesh"),
                    Localization.S("patch.compatibility.missing_paths")));
            }

            return BlendShareInspectorUi.CompatibilityIcon(new MeshFbxCompatibilityStatus(
                MeshFbxCompatibilityState.Unknown,
                Localization.S("common.status.unknown"),
                Localization.S("patch.compatibility.unknown_checks")));
        }

        private BlendShareFbxPatchState GetPatchStateCached(BlendShareAsset patch, GameObject targetFbx)
        {
            string key = CreatePatchStateKey(patch, targetFbx);
            if (m_HasCachedPatchState && string.Equals(m_CachedPatchStateKey, key, StringComparison.Ordinal))
            {
                return m_CachedPatchState;
            }

            m_CachedPatchState = BlendShareFbxMetadataService.GetPatchState(targetFbx, patch);
            m_CachedPatchStateKey = key;
            m_HasCachedPatchState = true;
            return m_CachedPatchState;
        }

        private static string CreatePatchStateKey(BlendShareAsset patch, GameObject targetFbx)
        {
            string targetPath = targetFbx != null ? AssetDatabase.GetAssetPath(targetFbx) : string.Empty;
            string userData = !string.IsNullOrEmpty(targetPath)
                ? AssetImporter.GetAtPath(targetPath)?.userData ?? string.Empty
                : string.Empty;
            string patchId = patch?.PatchId ?? string.Empty;
            string patchPath = patch != null ? AssetDatabase.GetAssetPath(patch) : string.Empty;
            return $"{(targetFbx != null ? targetFbx.GetInstanceID() : 0)}|{patchId}|{patchPath}|{userData}";
        }

        private void ClearCompatibilityCaches()
        {
            m_CachedCompatibilityStatuses = null;
            m_CachedCompatibilityKey = null;
            m_PendingCompatibilityKey = null;
            ClearPatchStateCache();
        }

        private void ClearPatchStateCache()
        {
            m_CachedPatchStateKey = null;
            m_HasCachedPatchState = false;
        }
    }
}
