using System.Collections.Generic;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Features.BlendShapes;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Persistence;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Inspector
{
    [CustomEditor(typeof(BlendShareObject))]
    public class BlendShareObjectEditor : UnityEditor.Editor
    {
        private const int UninitializedTargetLookupId = int.MinValue;
        private const int MaxTargetLookupRetryCount = 3;
        private const string GitHubUrl = "https://github.com/Tr1turbo/BlendShare";
        private const string XUrl = "https://x.com/TR1TURBO";
        private const string BoothUrl = "https://triturbo.booth.pm/";
        private const string GitHubIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/GitHub/GitHub_Invertocat_White.png";
        private const string XIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/X/logo-white.png";
        private const string BoothIconPath = "Packages/com.triturbo.blendshare/Icons/ThirdPartyLogos/Booth/logo_icon_white.png";

        private static readonly Dictionary<string, Dictionary<MeshDataObject, MeshFbxCompatibilityStatus>> sharedCompatibilityStatusCache = new();

        private readonly Dictionary<MeshDataObject, bool> meshFoldouts = new();
        private Dictionary<MeshDataObject, MeshFbxCompatibilityStatus> cachedCompatibilityStatuses;
        private string cachedCompatibilityKey;
        private string pendingCompatibilityKey;
        private readonly Dictionary<string, MeshMappingStatus> cachedMappingStatuses = new();
        private readonly Dictionary<string, string> cachedUnityVertexHashes = new();
        private UnityMeshTargetLookup cachedTargetLookup;
        private int cachedTargetLookupTargetId = UninitializedTargetLookupId;
        private int targetLookupRetryCount;
        private bool targetLookupRetryScheduled;
        private ArtifactMappingStatus cachedArtifactMappingStatus;
        private string cachedArtifactMappingStatusKey;
        private bool hasCachedArtifactMappingStatus;
        private BlendShareFbxPatchState cachedPatchState;
        private string cachedPatchStateKey;
        private bool hasCachedPatchState;
        private bool forceApplyUnlocked;

        public override VisualElement CreateInspectorGUI()
        {
            var patch = (BlendShareObject)target;
            serializedObject.Update();

            var root = BlendShareInspectorUi.CreateRoot();
            root.Add(new IMGUIContainer(() =>
            {
                EditorWidgets.ShowBlendShareBanner();
                Localization.DrawLanguageSelection();
            }));

            var content = new VisualElement();
            root.Add(content);
            int renderedTargetId = GetTargetId(patch);

            void RebuildContent()
            {
                if (target == null)
                {
                    return;
                }

                serializedObject.Update();
                content.Clear();
                var compatibilityStatuses = GetCompatibilityStatuses(patch, RebuildContent);
                var targetLookup = GetTargetLookup(patch);
                ScheduleTargetLookupRetry(patch, content, RebuildContent);
                content.Add(CreateHeader(patch, compatibilityStatuses, RefreshWhenTargetChanges));
                content.Add(CreateContentSummary(patch, compatibilityStatuses, targetLookup));
                content.Add(CreateMeshList(patch, targetLookup, () =>
                {
                    ClearCompatibilityCaches();
                    RebuildContent();
                }));
                content.Add(CreateActionsSection(patch));
                content.Add(CreateAdvancedSection());
                renderedTargetId = GetTargetId(patch);
            }

            void RefreshWhenTargetChanges()
            {
                serializedObject.Update();
                int currentTargetId = GetTargetId(patch);
                if (currentTargetId == renderedTargetId)
                {
                    return;
                }

                ClearCompatibilityCaches();
                RebuildContent();
            }

            RebuildContent();
            Localization.RebuildOnLanguageChange(root, RebuildContent);
            return root;
        }

        private VisualElement CreateHeader(
            BlendShareObject patch,
            IReadOnlyDictionary<MeshDataObject, MeshFbxCompatibilityStatus> compatibilityStatuses,
            System.Action refresh)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.inspector.title"));

            var targetField = new PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_Target)), string.Empty);
            targetField.Bind(serializedObject);
            targetField.RegisterCallback<SerializedPropertyChangeEvent>(_ => targetField.schedule.Execute(() => refresh?.Invoke()));
            var statusIcon = CreatePatchCompatibilityIcon(compatibilityStatuses);
            section.Add(BlendShareInspectorUi.LabeledRow(Localization.S("common.target_fbx"), targetField, statusIcon));
            section.Add(CreateFbxPatchMetadata(patch));

            if (patch.m_Target == null)
            {
                section.Add(new HelpBox(Localization.S("patch.target_missing"), HelpBoxMessageType.Warning));
            }

#if !ENABLE_FBX_SDK
            section.Add(new HelpBox(Localization.S("common.fbx_sdk_missing"), HelpBoxMessageType.Warning));
#endif

            return section;
        }

        private VisualElement CreateFbxPatchMetadata(BlendShareObject patch)
        {
            var state = patch != null && patch.m_Target != null
                ? GetPatchStateCached(patch)
                : default;
            var records = state.Metadata?.activeRecords ?? System.Array.Empty<BlendShareFbxPatchRecord>();
            if (records.Length == 0)
            {
                return new VisualElement();
            }

            var list = new VisualElement();
            list.style.marginTop = 3;
            list.Add(BlendShareInspectorUi.Row(Localization.S("patch.metadata.applied_patches"), records.Length.ToString()));

            foreach (var record in records.Where(record => record != null))
            {
                string name = string.IsNullOrWhiteSpace(record.blendShareName) ? record.patchId : record.blendShareName;
                list.Add(BlendShareInspectorUi.Row("", name));
            }

            return list;
        }

        private VisualElement CreateContentSummary(
            BlendShareObject patch,
            IReadOnlyDictionary<MeshDataObject, MeshFbxCompatibilityStatus> compatibilityStatuses,
            UnityMeshTargetLookup targetLookup)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.contents.title"));
            var meshes = (patch.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .ToArray();
            int readyMappings = 0;
            int requiredMappings = 0;
            int verifiedMeshes = 0;
            long estimatedVideoMemoryBytes = 0;

            foreach (var mesh in meshes)
            {
                var mappingStatus = GetMappingStatusCached(patch, mesh, targetLookup);
                if (mappingStatus.IsReady)
                {
                    readyMappings++;
                }

                if (mappingStatus.Kind != StatusKind.Neutral)
                {
                    requiredMappings++;
                }

                if (GetCompatibilityStatus(compatibilityStatuses, mesh).State == MeshFbxCompatibilityState.Verified)
                {
                    verifiedMeshes++;
                }

                estimatedVideoMemoryBytes += EstimateVideoMemoryBytes(mesh, targetLookup);
            }

            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.compatibility.title"), Localization.SF("patch.compatibility.summary", verifiedMeshes, meshes.Length)));
            section.Add(BlendShareInspectorUi.Row(Localization.S("patch.mapping.title"), Localization.SF("patch.mapping.summary", readyMappings, requiredMappings)));
            //section.Add(BlendShareInspectorUi.Row("VRAM", FormatBytes(estimatedGraphicsMemoryBytes)));
            return section;
        }

        private static long EstimateVideoMemoryBytes(MeshDataObject mesh, UnityMeshTargetLookup targetLookup)
        {
            if (mesh == null || targetLookup == null || !targetLookup.TryGetMesh(mesh, out var unityMesh) || unityMesh == null)
            {
                return 0;
            }

            long bytes = 0;
            int unityVertexCount = unityMesh.vertexCount;
            foreach (var feature in mesh.Features ?? System.Array.Empty<MeshFeatureObject>())
            {
                var editor = MeshFeatureEditorRegistry.GetEditor(feature);
                if (editor == null)
                {
                    continue;
                }

                // bytes += editor.EstimateVideoMemoryBytes(
                //     new MeshFeatureEditorContext(feature, mesh, null, null),
                //     unityVertexCount);
            }

            return bytes;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "-";
            }

            const double kib = 1024d;
            double mib = bytes / (kib * kib);
            return mib >= 1d ? $"~{mib:0.##} MiB" : $"~{bytes / kib:0.##} KiB";
        }

        private VisualElement CreateMeshList(BlendShareObject patch, UnityMeshTargetLookup targetLookup, System.Action refresh)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("common.meshes"));
            var meshes = (patch.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .ToArray();

            if (meshes.Length == 0)
            {
                section.Add(new HelpBox(Localization.S("patch.contents.no_mesh_data"), HelpBoxMessageType.Info));
                return section;
            }

            foreach (var mesh in meshes)
            {
                section.Add(CreateMeshRow(patch, mesh, targetLookup, refresh));
            }

            return section;
        }

        private static VisualElement CreateMeshRow(
            BlendShareObject patch,
            MeshDataObject mesh,
            UnityMeshTargetLookup targetLookup,
            System.Action refresh)
        {
            var editor = MeshFeatureEditorRegistry.GetEmbeddedEditor(mesh);
            if (editor != null)
            {
                var context = new BlendShareEmbeddedEditorContext(mesh, mesh, patch, refresh, patch != null ? patch.m_Target : null, targetLookup, false);
                return MeshDataObjectEditor.CreateCompactEmbeddedInspector(mesh, context);
            }

            var row = BlendShareInspectorUi.Box();
            row.Add(new HelpBox(Localization.S("patch.mesh_data.no_embedded_editor"), HelpBoxMessageType.Info));
            return row;
        }

        private Dictionary<MeshDataObject, MeshFbxCompatibilityStatus> GetCompatibilityStatuses(BlendShareObject patch, System.Action refresh)
        {
            var meshes = (patch?.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .ToArray();
            string key = CreateCompatibilityCacheKey(patch, meshes);
            if (cachedCompatibilityStatuses != null && string.Equals(cachedCompatibilityKey, key, System.StringComparison.Ordinal))
            {
                return cachedCompatibilityStatuses;
            }

            if (sharedCompatibilityStatusCache.TryGetValue(key, out cachedCompatibilityStatuses))
            {
                cachedCompatibilityKey = key;
                return cachedCompatibilityStatuses;
            }

            cachedCompatibilityStatuses = CreatePendingCompatibilityStatuses(meshes);
            cachedCompatibilityKey = key;
            ScheduleCompatibilityVerification(patch, meshes, key, refresh);
            return cachedCompatibilityStatuses;
        }

        private Dictionary<MeshDataObject, MeshFbxCompatibilityStatus> CreatePendingCompatibilityStatuses(IEnumerable<MeshDataObject> meshes)
        {
            var statuses = new Dictionary<MeshDataObject, MeshFbxCompatibilityStatus>();
            foreach (var mesh in meshes ?? System.Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                statuses[mesh] = new MeshFbxCompatibilityStatus(
                    MeshFbxCompatibilityState.Unknown,
                    Localization.S("common.status.checking"),
                    Localization.S("patch.compatibility.pending"));
            }

            return statuses;
        }

        private void ScheduleCompatibilityVerification(
            BlendShareObject patch,
            MeshDataObject[] meshes,
            string key,
            System.Action refresh)
        {
            if (string.Equals(pendingCompatibilityKey, key, System.StringComparison.Ordinal))
            {
                return;
            }

            pendingCompatibilityKey = key;
            EditorApplication.delayCall += () =>
            {
                if (this == null || target == null || !string.Equals(pendingCompatibilityKey, key, System.StringComparison.Ordinal))
                {
                    return;
                }

                var statuses = BlendShareInspectorUtility.GetFbxCompatibilityStatuses(patch, meshes);
                if (sharedCompatibilityStatusCache.Count > 16)
                {
                    sharedCompatibilityStatusCache.Clear();
                }

                sharedCompatibilityStatusCache[key] = statuses;
                cachedCompatibilityStatuses = statuses;
                cachedCompatibilityKey = key;
                pendingCompatibilityKey = null;
                refresh?.Invoke();
            };
        }

        private static string CreateCompatibilityCacheKey(BlendShareObject patch, IReadOnlyList<MeshDataObject> meshes)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(patch != null && patch.m_Target != null ? AssetDatabase.GetAssetPath(patch.m_Target) : string.Empty);
            builder.Append('|');
            foreach (var mesh in meshes ?? System.Array.Empty<MeshDataObject>())
            {
                var signature = mesh?.m_FbxTopologySignature;
                builder.Append(mesh != null ? mesh.GetInstanceID() : 0);
                builder.Append(':');
                builder.Append(mesh?.m_Path ?? string.Empty);
                builder.Append(':');
                builder.Append(signature?.Hash ?? string.Empty);
                builder.Append(':');
                builder.Append(signature?.ControlPointCount ?? -1);
                builder.Append(':');
                builder.Append(signature?.FaceCount ?? -1);
                builder.Append(':');
                builder.Append(signature?.IsValid ?? false);
                builder.Append('|');
            }

            return builder.ToString();
        }

        private UnityMeshTargetLookup GetTargetLookup(BlendShareObject patch)
        {
            int targetId = GetTargetId(patch);
            if (cachedTargetLookupTargetId == targetId && (targetId == 0 || cachedTargetLookup != null))
            {
                return cachedTargetLookup;
            }

            cachedTargetLookup = patch != null && patch.m_Target != null ? UnityMeshTargetLookup.Create(patch.m_Target) : null;
            cachedTargetLookupTargetId = targetId;
            if (cachedTargetLookup != null || targetId == 0)
            {
                targetLookupRetryCount = 0;
            }

            ClearMappingStatusCaches();
            return cachedTargetLookup;
        }

        private void ScheduleTargetLookupRetry(BlendShareObject patch, VisualElement scheduler, System.Action rebuild)
        {
            if (patch == null || patch.m_Target == null || scheduler == null || cachedTargetLookup != null || targetLookupRetryScheduled || targetLookupRetryCount >= MaxTargetLookupRetryCount)
            {
                return;
            }

            targetLookupRetryScheduled = true;
            targetLookupRetryCount++;
            scheduler.schedule.Execute(() =>
            {
                targetLookupRetryScheduled = false;
                if (target != null)
                {
                    rebuild?.Invoke();
                }
            }).StartingIn(100);
        }

        private void ClearCompatibilityCaches()
        {
            cachedCompatibilityStatuses = null;
            cachedCompatibilityKey = null;
            pendingCompatibilityKey = null;
            cachedMappingStatuses.Clear();
            cachedUnityVertexHashes.Clear();
            cachedArtifactMappingStatusKey = null;
            hasCachedArtifactMappingStatus = false;
            cachedPatchStateKey = null;
            hasCachedPatchState = false;
            cachedTargetLookup = null;
            cachedTargetLookupTargetId = UninitializedTargetLookupId;
            targetLookupRetryCount = 0;
        }

        private void ClearMappingStatusCaches()
        {
            cachedMappingStatuses.Clear();
            cachedUnityVertexHashes.Clear();
            cachedArtifactMappingStatusKey = null;
            hasCachedArtifactMappingStatus = false;
        }

        private void ClearPatchStateCache()
        {
            cachedPatchStateKey = null;
            hasCachedPatchState = false;
        }

        private static int GetTargetId(BlendShareObject patch)
        {
            return patch != null && patch.m_Target != null ? patch.m_Target.GetInstanceID() : 0;
        }

        private static MeshFbxCompatibilityStatus GetCompatibilityStatus(
            IReadOnlyDictionary<MeshDataObject, MeshFbxCompatibilityStatus> statuses,
            MeshDataObject mesh)
        {
            return statuses != null && mesh != null && statuses.TryGetValue(mesh, out var status)
                ? status
                : new MeshFbxCompatibilityStatus(MeshFbxCompatibilityState.Unknown, Localization.S("common.status.unknown"), Localization.S("patch.compatibility.not_evaluated"));
        }

        private static VisualElement CreatePatchCompatibilityIcon(
            IReadOnlyDictionary<MeshDataObject, MeshFbxCompatibilityStatus> compatibilityStatuses)
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

        private MeshMappingStatus GetMappingStatusCached(
            BlendShareObject patch,
            MeshDataObject mesh,
            UnityMeshTargetLookup targetLookup)
        {
            string key = CreateMappingStatusCacheKey(patch, mesh, targetLookup);
            if (cachedMappingStatuses.TryGetValue(key, out var status))
            {
                return status;
            }

            status = GetMappingStatus(patch, mesh, targetLookup);
            cachedMappingStatuses[key] = status;
            return status;
        }

        private static string CreateMappingStatusCacheKey(
            BlendShareObject patch,
            MeshDataObject mesh,
            UnityMeshTargetLookup targetLookup)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(GetTargetId(patch));
            builder.Append('|');
            builder.Append(mesh != null ? mesh.GetInstanceID() : 0);
            builder.Append('|');
            if (targetLookup != null && targetLookup.TryGetMesh(mesh, out var targetMesh))
            {
                builder.Append(targetMesh.GetInstanceID());
                builder.Append(':');
                builder.Append(targetMesh.vertexCount);
            }

            foreach (var mapping in mesh?.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
            {
                builder.Append('|');
                builder.Append(mapping != null ? mapping.GetInstanceID() : 0);
                builder.Append(':');
                builder.Append(mapping != null && mapping.m_IsValid);
                builder.Append(':');
                builder.Append(mapping?.m_UnityVertexHash ?? string.Empty);
                builder.Append(':');
                builder.Append(mapping?.m_UnityVertexCount ?? -1);
                builder.Append(':');
                builder.Append(mapping != null && mapping.m_UnityMesh != null ? mapping.m_UnityMesh.GetInstanceID() : 0);
            }

            return builder.ToString();
        }

        private MeshMappingStatus GetMappingStatus(
            BlendShareObject patch,
            MeshDataObject mesh,
            UnityMeshTargetLookup targetLookup)
        {
            if (mesh == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mesh_data.missing"), false, StatusKind.Neutral);
            }

            if (patch == null || patch.m_Target == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.missing_target"), Localization.S("patch.mapping.target_missing"), false, StatusKind.Warning);
            }

            if (targetLookup == null)
            {
                return new MeshMappingStatus(Localization.S("common.status.unknown"), Localization.S("patch.mapping.target_unreadable"), false, StatusKind.Neutral);
            }

            if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
            {
                return new MeshMappingStatus(Localization.S("common.status.missing"), targetLookup.GetResolutionError(mesh), false, StatusKind.Warning);
            }

            var mappings = mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>();
            bool hasMapping = mappings.Any(mapping => mapping != null);
            bool hasValidMapping = mappings.Any(mapping => IsCompatibleMapping(mapping, mesh, targetMesh));
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

        private VisualElement CreateActionsSection(BlendShareObject patch)
        {
            var section = BlendShareInspectorUi.Section(Localization.S("patch.actions.title"));
            section.Add(new IMGUIContainer(() => DrawActions(patch)));
            return section;
        }

        private VisualElement CreateAdvancedSection()
        {
            var foldout = new Foldout { text = Localization.S("patch.advanced") };
            var defaultName = new PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_DefaultGeneratedAssetName)), Localization.S("common.default_asset_name"));
            defaultName.Bind(serializedObject);
            foldout.Add(defaultName);
            var patchId = new PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_PatchId)), Localization.S("common.patch_id"));
            patchId.Bind(serializedObject);
            foldout.Add(patchId);
            foldout.Add(new IMGUIContainer(() =>
            {
                var patch = (BlendShareObject)target;
                DrawSharedArmatures(patch);
            }));
            foldout.Add(BlendShareInspectorUi.FooterBox(
                Localization.S("patch.created_by"),
                (Localization.S("common.github"), GitHubUrl, GitHubIconPath, Localization.S("common.github.tooltip")),
                (Localization.S("common.x"), XUrl, XIconPath, Localization.S("common.x.tooltip")),
                (Localization.S("common.booth"), BoothUrl, BoothIconPath, Localization.S("common.booth.tooltip"))));
            return foldout;
        }

        public override void OnInspectorGUI()
        {
            var patch = (BlendShareObject)target;
            serializedObject.Update();

            EditorWidgets.ShowBlendShareBanner();
            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawMetadata();
            EditorGUILayout.Space();
            DrawSharedArmatures(patch);
            EditorGUILayout.Space();
            DrawMeshes(patch);
            EditorGUILayout.Space();
            DrawActions(patch);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawMetadata()
        {
            EditorGUILayout.LabelField(Localization.S("patch.inspector.title"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_Target)), Localization.G("common.target_fbx"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_DefaultGeneratedAssetName)), Localization.G("data.hidden_settings.default_asset_name"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShareObject.m_PatchId)), Localization.G("data.hidden_settings.patch_id"));
        }

        private void DrawMeshes(BlendShareObject patch)
        {
            EditorGUILayout.LabelField("Meshes", EditorStyles.boldLabel);
            foreach (var mesh in patch.Meshes)
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
                EditorGUILayout.LabelField(Localization.S("common.path"), mesh.m_Path);
                EditorGUILayout.LabelField(Localization.S("patch.mesh_data.fbx_control_points"), mesh.FbxControlPointCount.ToString());
                EditorGUILayout.LabelField(Localization.S("patch.mesh_data.fbx_topology_hash"), ShortHash(mesh.m_FbxTopologySignature?.Hash));
                DrawMappings(mesh);
                DrawSkinWeightSummary(mesh.GetFeature<SkinWeightFeatureObject>());
                DrawBlendShapeToggles(blendShapeFeature);
                EditorGUI.indentLevel--;
            }
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return "-";
            }

            return hash.Length <= 8 ? hash : hash.Substring(0, 8);
        }

        private void DrawSharedArmatures(BlendShareObject patch)
        {
            var armatures = GetSharedArmatures(patch);
            if (armatures.Count == 0)
            {
                return;
            }

            foreach (var armature in armatures)
            {
                DrawArmatureSummary(armature);
            }
        }

        private void DrawArmatureSummary(ArmatureObject armature)
        {
            EditorGUILayout.LabelField(Localization.G("data.armature.title"), EditorStyles.boldLabel);
            var bones = armature?.Bones ?? System.Array.Empty<ArmatureBoneData>();
            int createdCount = bones.Count(bone => bone != null && bone.m_CreateIfMissing);
            EditorGUILayout.LabelField(Localization.S("data.armature.created_count"), createdCount.ToString());
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

        private static List<ArmatureObject> GetSharedArmatures(BlendShareObject patch)
        {
            return (patch?.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .Select(mesh => mesh.GetFeature<SkinWeightFeatureObject>()?.Armature)
                .Where(armature => armature != null)
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
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.bone_count"), skinWeightFeature.ClusterCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.weighted_control_points"), skinWeightFeature.WeightedControlPointCount.ToString());
            EditorGUILayout.LabelField(Localization.S("data.skin_weights.root_bone"), skinWeightFeature.RootBonePath);
        }

        private void DrawMappings(MeshDataObject mesh)
        {
            var mappings = mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>();
            EditorGUILayout.LabelField(Localization.S("patch.mapping.title"), mappings.Length.ToString());
            EditorGUI.indentLevel++;
            foreach (var mapping in mappings)
            {
                if (mapping == null)
                {
                    continue;
                }

                EditorGUILayout.ObjectField(Localization.S("patch.mapping.unity_mesh"), mapping.m_UnityMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField(Localization.S("patch.mapping.unity_vertices"), mapping.m_UnityVertexCount.ToString());
                EditorGUILayout.LabelField(Localization.S("patch.mapping.valid"), mapping.m_IsValid.ToString());
                if (!mapping.m_IsValid && !string.IsNullOrEmpty(mapping.m_Report))
                {
                    EditorGUILayout.HelpBox(mapping.m_Report, MessageType.Warning);
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawBlendShapeToggles(BlendShapeFeatureObject blendShapeFeature)
        {
            if (blendShapeFeature == null)
            {
                EditorGUILayout.HelpBox(Localization.S("features.blend-shapes.no_feature_for_mesh"), MessageType.Info);
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

        private void DrawActions(BlendShareObject patch)
        {
            bool hasTarget = patch.m_Target != null;
            var patchState = hasTarget
                ? GetPatchStateCached(patch)
                : default;
            var artifactMappingStatus = GetArtifactMappingStatus(patch);

#if !ENABLE_FBX_SDK
            EditorGUILayout.HelpBox(Localization.S("common.fbx_sdk_missing"), MessageType.Warning);
#endif

            using (new EditorGUI.DisabledScope(!hasTarget))
            {
                DrawPatchActions(patch, patchState);
                DrawCreateFbxControl(patch, patchState);
            }

            if (!artifactMappingStatus.CanGenerateArtifact)
            {
                EditorGUILayout.HelpBox(artifactMappingStatus.Message, MessageType.Warning);
            }

            if (artifactMappingStatus.CanCreateMappings)
            {
                if (GUILayout.Button(Localization.G("patch.mapping.create")))
                {
                    using var progress = BlendShareEditorProgress.Create(Localization.S("patch.mapping.create"));
                    try
                    {
                        CreateMappingsForTarget(patch, progress);
                    }
                    catch (BlendShareOperationCanceledException)
                    {
                        EditorUtility.DisplayDialog(Localization.S("patch.mapping.create"), BlendShareProgressUtility.CanceledMessage, Localization.S("common.ok"));
                    }
                    ClearMappingStatusCaches();
                    artifactMappingStatus = GetArtifactMappingStatus(patch);
                }
            }

            using (new EditorGUI.DisabledScope(!artifactMappingStatus.CanGenerateArtifact))
            {
                if (GUILayout.Button(Localization.G("asset.create")))
                {
                    if (patch.m_Target == null)
                    {
                        Localization.DisplayDialog("patch.fbx_missing", Localization.S("common.ok"));
                        return;
                    }

                    string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
                    string path = EditorUtility.SaveFilePanelInProject(
                        Localization.S("asset.save_title"),
                        $"{patch.DefaultMeshAssetName}_Assets",
                        "asset",
                        Localization.S("data.save_file.message"),
                        folderPath);
                    if (!string.IsNullOrEmpty(path))
                    {
                        using var progress = BlendShareEditorProgress.Create(Localization.S("asset.create"));
                        try
                        {
                            BlendShareArtifactService.CreateArtifact(patch.m_Target, new[] { patch }, path, progress);
                        }
                        catch (BlendShareOperationCanceledException)
                        {
                            EditorUtility.DisplayDialog(Localization.S("asset.create"), BlendShareProgressUtility.CanceledMessage, Localization.S("common.ok"));
                        }
                    }
                }
            }

        }
        private void DrawPatchActions(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            bool applyLocked = patchState.HasPatch && !forceApplyUnlocked;
            bool showRestore = patchState.ActiveRecordCount > 0;
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float spacing = 4f;
            float halfWidth = (row.width - spacing) * 0.5f;
            var applyRect = new Rect(row.x, row.y, halfWidth, row.height);
            var restoreRect = new Rect(applyRect.xMax + spacing, row.y, halfWidth, row.height);

            DrawApplyPatchControl(applyRect, patch, patchState, applyLocked);
            DrawRestoreControl(restoreRect, patch, patchState, showRestore);

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
            BlendShareObject patch,
            BlendShareFbxPatchState patchState,
            bool applyLocked)
        {
            Rect buttonRect = rect;
            if (patchState.HasPatch)
            {
                const float lockWidth = 14f;
                var lockRect = new Rect(rect.x, rect.y + 1f, lockWidth, rect.height - 2f);
                GUIContent lockIcon = forceApplyUnlocked
                    ? EditorGUIUtility.IconContent("Unlocked")
                    : EditorGUIUtility.IconContent("Locked");
                if (GUI.Button(lockRect, lockIcon, GUIStyle.none))
                {
                    forceApplyUnlocked = !forceApplyUnlocked;
                }

                buttonRect = new Rect(lockRect.xMax + 1f, rect.y, rect.width - lockWidth - 1f, rect.height);
            }

            using (new EditorGUI.DisabledScope(applyLocked))
            {
                if (GUI.Button(buttonRect, Localization.S("patch.apply")))
                {
                    RunApplyPatch(patch, patchState);
                }
            }
        }

        private void RunApplyPatch(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            bool force = patchState.HasPatch && forceApplyUnlocked;
            if (force && !EditorUtility.DisplayDialog(
                    Localization.S("patch.apply.confirm.title"),
                    Localization.S("patch.apply.confirm.message"),
                    Localization.S("common.apply"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create(Localization.S("patch.apply"));
            if (BlendShareGenerationService.ApplyPatch(patch.m_Target, patch, force, progress, out string message))
            {
                forceApplyUnlocked = false;
                ClearPatchStateCache();
            }
            else
            {
                EditorUtility.DisplayDialog(Localization.S("patch.apply"), message ?? Localization.S("patch.apply.failed"), Localization.S("common.ok"));
            }
        }

        private void DrawCreateFbxControl(BlendShareObject patch, BlendShareFbxPatchState patchState)
        {
            if (GUILayout.Button(Localization.S("patch.create_fbx")))
            {
                RunCreateFbx(patch, patchState);
            }
        }

        private void RunCreateFbx(BlendShareObject patch, BlendShareFbxPatchState patchState)
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

            string folderPath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(patch));
            string path = EditorUtility.SaveFilePanelInProject(
                Localization.S("data.save_fbx.title"),
                patch.DefaultFbxName,
                "fbx",
                Localization.S("data.save_file.message"),
                folderPath);
            if (!string.IsNullOrEmpty(path))
            {
                using var progress = BlendShareEditorProgress.Create(Localization.S("patch.create_fbx"));
                try
                {
                    bool created = BlendShareGenerationService.CreateFbx(patch.m_Target, new[] { patch }, path, progress: progress);
                    if (!created)
                    {
                        EditorUtility.DisplayDialog(Localization.S("patch.create_fbx"), Localization.S("patch.create_fbx.failed"), Localization.S("common.ok"));
                    }

                    ClearPatchStateCache();
                }
                catch (BlendShareOperationCanceledException)
                {
                    EditorUtility.DisplayDialog(Localization.S("patch.create_fbx"), BlendShareProgressUtility.CanceledMessage, Localization.S("common.ok"));
                }
            }
        }

        private void DrawRestoreControl(
            Rect rect,
            BlendShareObject patch,
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
                    RunRestoreToOriginal(patch);
                }
            }

            if (showDropdown && GUI.Button(dropdownRect, EditorGUIUtility.IconContent("icon dropdown"), EditorStyles.miniButtonRight))
            {
                var menu = new GenericMenu();
                if (patchState.CanRevertPatch)
                {
                    menu.AddItem(new GUIContent(Localization.S("patch.revert_this")), false, () => RunRevertPatch(patch));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent(Localization.S("patch.revert_this")));
                }

                menu.DropDown(dropdownRect);
            }
        }

        private void RunRevertPatch(BlendShareObject patch)
        {
            if (!EditorUtility.DisplayDialog(
                    Localization.S("patch.revert.title"),
                    Localization.S("patch.revert.message"),
                    Localization.S("common.revert"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create(Localization.S("patch.revert.title"));
            if (BlendShareGenerationService.RevertPatch(patch.m_Target, patch, progress, out string message))
            {
                forceApplyUnlocked = false;
                ClearPatchStateCache();
            }
            else
            {
                EditorUtility.DisplayDialog(Localization.S("patch.revert.title"), message ?? Localization.S("patch.revert.failed"), Localization.S("common.ok"));
            }
        }

        private void RunRestoreToOriginal(BlendShareObject patch)
        {
            if (!EditorUtility.DisplayDialog(
                    Localization.S("patch.restore.title"),
                    Localization.S("patch.restore.message"),
                    Localization.S("common.restore"),
                    Localization.S("common.cancel")))
            {
                return;
            }

            using var progress = BlendShareEditorProgress.Create(Localization.S("patch.restore.title"));
            if (BlendShareGenerationService.RestoreToOriginal(patch.m_Target, progress, out string message))
            {
                forceApplyUnlocked = false;
                ClearPatchStateCache();
            }
            else
            {
                EditorUtility.DisplayDialog(Localization.S("patch.restore.title"), message ?? Localization.S("patch.restore.failed"), Localization.S("common.ok"));
            }
        }

        private ArtifactMappingStatus GetArtifactMappingStatus(BlendShareObject patch)
        {
            string key = CreateArtifactMappingStatusKey(patch);
            if (hasCachedArtifactMappingStatus && string.Equals(cachedArtifactMappingStatusKey, key, System.StringComparison.Ordinal))
            {
                return cachedArtifactMappingStatus;
            }

            cachedArtifactMappingStatus = CalculateArtifactMappingStatus(patch);
            cachedArtifactMappingStatusKey = key;
            hasCachedArtifactMappingStatus = true;
            return cachedArtifactMappingStatus;
        }

        private BlendShareFbxPatchState GetPatchStateCached(BlendShareObject patch)
        {
            string key = CreatePatchStateKey(patch);
            if (hasCachedPatchState && string.Equals(cachedPatchStateKey, key, System.StringComparison.Ordinal))
            {
                return cachedPatchState;
            }

            cachedPatchState = BlendShareFbxMetadataService.GetPatchState(patch.m_Target, patch);
            cachedPatchStateKey = key;
            hasCachedPatchState = true;
            return cachedPatchState;
        }

        private static string CreatePatchStateKey(BlendShareObject patch)
        {
            string targetPath = patch != null && patch.m_Target != null
                ? AssetDatabase.GetAssetPath(patch.m_Target)
                : string.Empty;
            string userData = !string.IsNullOrEmpty(targetPath)
                ? AssetImporter.GetAtPath(targetPath)?.userData ?? string.Empty
                : string.Empty;
            string patchId = patch?.m_PatchId ?? string.Empty;
            string patchPath = patch != null ? AssetDatabase.GetAssetPath(patch) : string.Empty;

            return $"{GetTargetId(patch)}|{patchId}|{patchPath}|{userData}";
        }

        private static string CreateArtifactMappingStatusKey(BlendShareObject patch)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(GetTargetId(patch));
            builder.Append('|');
            foreach (var mesh in patch?.Meshes ?? System.Array.Empty<MeshDataObject>())
            {
                builder.Append(mesh != null ? mesh.GetInstanceID() : 0);
                builder.Append(':');
                builder.Append(mesh?.FbxControlPointCount ?? -1);
                foreach (var mapping in mesh?.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                {
                    builder.Append(':');
                    builder.Append(mapping != null ? mapping.GetInstanceID() : 0);
                    builder.Append(',');
                    builder.Append(mapping != null && mapping.m_IsValid);
                    builder.Append(',');
                    builder.Append(mapping?.m_UnityVertexHash ?? string.Empty);
                    builder.Append(',');
                    builder.Append(mapping?.m_UnityVertexCount ?? -1);
                    builder.Append(',');
                    builder.Append(mapping != null && mapping.m_UnityMesh != null ? mapping.m_UnityMesh.GetInstanceID() : 0);
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private ArtifactMappingStatus CalculateArtifactMappingStatus(BlendShareObject patch)
        {
            if (patch == null || patch.m_Target == null)
            {
                return ArtifactMappingStatus.Blocked(Localization.S("asset.mapping.target_missing"), false);
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Target);
            if (targetLookup == null)
            {
                return ArtifactMappingStatus.Blocked(Localization.S("asset.mapping.target_unreadable"), false);
            }

            int invalidCount = 0;
            int missingMeshCount = 0;
            foreach (var mesh in patch.Meshes ?? System.Array.Empty<MeshDataObject>())
            {
                if (mesh == null)
                {
                    continue;
                }

                if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
                {
                    missingMeshCount++;
                    continue;
                }

                bool hasValidMapping = (mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                    .Any(mapping => IsCompatibleMapping(mapping, mesh, targetMesh));
                if (!hasValidMapping)
                {
                    invalidCount++;
                }
            }

            if (missingMeshCount > 0)
            {
                return ArtifactMappingStatus.Blocked(
                    string.Format(Localization.S("asset.mapping.mesh_missing"), missingMeshCount),
                    false);
            }

            if (invalidCount > 0)
            {
                return ArtifactMappingStatus.Blocked(
                    string.Format(Localization.S("asset.mapping.invalid"), invalidCount),
                    true);
            }

            return ArtifactMappingStatus.Ready();
        }

        private bool IsCompatibleMapping(UnityVertexMappingObject mapping, MeshDataObject meshData, Mesh targetMesh)
        {
            if (mapping == null || targetMesh == null || !mapping.m_IsValid)
            {
                return false;
            }

            if (!MappingMatchesUnityMesh(mapping, targetMesh))
            {
                return false;
            }

            return MappingMatchesFbxControlPointCount(mapping, meshData?.FbxControlPointCount ?? -1);
        }

        private bool MappingMatchesUnityMesh(UnityVertexMappingObject mapping, Mesh targetMesh)
        {
            if (mapping.m_UnityVertexCount != targetMesh.vertexCount)
            {
                return false;
            }

            if (mapping.m_UnityMesh == targetMesh)
            {
                return true;
            }

            return !string.IsNullOrEmpty(mapping.m_UnityVertexHash) && mapping.m_UnityVertexHash == GetUnityVertexHash(targetMesh);
        }

        private string GetUnityVertexHash(Mesh mesh)
        {
            string key = $"{mesh.GetInstanceID()}:{mesh.vertexCount}";
            if (cachedUnityVertexHashes.TryGetValue(key, out var hash))
            {
                return hash;
            }

            hash = UnityMeshEditorDataUtility.TryCalculatePositionHash(mesh, out string calculatedHash)
                ? calculatedHash
                : string.Empty;
            cachedUnityVertexHashes[key] = hash;
            return hash;
        }

        private static bool MappingMatchesFbxControlPointCount(UnityVertexMappingObject mapping, int fbxControlPointCount)
        {
            if (fbxControlPointCount <= 0)
            {
                return true;
            }

            if (mapping.m_IndexGroups != null)
            {
                return mapping.m_IndexGroups.All(group =>
                    group.m_Indices == null ||
                    group.m_Indices.All(index => index < 0 || index < fbxControlPointCount));
            }

            return mapping.m_Indices == null || mapping.m_Indices.All(index => index < 0 || index < fbxControlPointCount);
        }

        private static void CreateMappingsForTarget(BlendShareObject patch, IBlendShareProgress progress)
        {
            if (patch == null || patch.m_Target == null)
            {
                Localization.DisplayDialog("patch.fbx_missing", Localization.S("common.ok"));
                return;
            }

            var targetLookup = UnityMeshTargetLookup.Create(patch.m_Target);
            if (targetLookup == null)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.mapping.create.failed.title"),
                    Localization.S("asset.mapping.target_unreadable"),
                    Localization.S("common.ok"));
                return;
            }

            var createdMappings = new List<(MeshDataObject Mesh, UnityVertexMappingObject Mapping)>();
            var failures = new List<string>();
            var meshes = (patch.Meshes ?? System.Array.Empty<MeshDataObject>())
                .Where(mesh => mesh != null)
                .ToArray();
            try
            {
                for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                {
                    var mesh = meshes[meshIndex];
                    BlendShareProgressUtility.Report(
                        progress,
                        Localization.S("patch.mapping.create"),
                        Localization.SF("patch.mapping.progress", mesh.m_Path),
                        meshes.Length > 0 ? (float)meshIndex / meshes.Length : 0f,
                        true);

                    if (!targetLookup.TryGetMesh(mesh, out var targetMesh))
                    {
                        failures.Add($"{mesh.m_Path}: {targetLookup.GetResolutionError(mesh)}");
                        continue;
                    }

                    if ((mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                        .Any(mapping => mapping != null && mapping.IsCompatibleWith(mesh, targetMesh)))
                    {
                        continue;
                    }

                    var mapping = UnityFbxVertexMappingBuilder.BuildFromFbx(mesh.m_Path, targetMesh, patch.m_Target);
                    if (mapping == null || !mapping.m_IsValid)
                    {
                        failures.Add($"{mesh.m_Path}: {mapping?.m_Report ?? Localization.S("patch.mapping.generation_failed")}");
                        continue;
                    }

                    createdMappings.Add((mesh, mapping));
                }
            }
            catch (BlendShareOperationCanceledException)
            {
                foreach (var createdMapping in createdMappings)
                {
                    if (createdMapping.Mapping != null && !AssetDatabase.Contains(createdMapping.Mapping))
                    {
                        UnityEngine.Object.DestroyImmediate(createdMapping.Mapping);
                    }
                }

                throw;
            }

            if (createdMappings.Count > 0)
            {
                BlendShareProgressUtility.Report(progress, Localization.S("patch.mapping.create"), Localization.S("patch.mapping.saving"), 0.95f, false);
                foreach (var createdMapping in createdMappings)
                {
                    createdMapping.Mesh.m_Mappings = (createdMapping.Mesh.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                        .Where(existing => existing != null)
                        .Concat(new[] { createdMapping.Mapping })
                        .ToArray();
                }

                BlendShareAssetService.SaveMappings(patch);
            }

            if (failures.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("patch.mapping.create.failed.title"),
                    string.Join("\n", failures),
                    Localization.S("common.ok"));
                return;
            }

            EditorUtility.DisplayDialog(
                Localization.S("patch.mapping.create.success.title"),
                string.Format(Localization.S("patch.mapping.create.success.message"), createdMappings.Count),
                Localization.S("common.ok"));
        }

        private readonly struct ArtifactMappingStatus
        {
            public bool CanGenerateArtifact { get; }
            public bool CanCreateMappings { get; }
            public string Message { get; }

            private ArtifactMappingStatus(bool canGenerateArtifact, bool canCreateMappings, string message)
            {
                CanGenerateArtifact = canGenerateArtifact;
                CanCreateMappings = canCreateMappings;
                Message = message;
            }

            public static ArtifactMappingStatus Ready()
            {
                return new ArtifactMappingStatus(true, false, string.Empty);
            }

            public static ArtifactMappingStatus Blocked(string message, bool canCreateMappings)
            {
                return new ArtifactMappingStatus(false, canCreateMappings, message);
            }
        }
    }
}
