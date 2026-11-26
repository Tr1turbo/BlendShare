using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Pipeline.Apply;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.UI.Inspectors;
using UnityEditor;
using UnityEditorInternal;
using System.Runtime.CompilerServices;
using Object = UnityEngine.Object;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Authoring
{
    [CustomEditor(typeof(BlendShareMesh))]
    public sealed class BlendShareMeshEditor : UnityEditor.Editor
    {
        // Horizontal gap separating the path, Transform, and trailing control columns.
        private const float k_BoneColumnSpacing = 4f;

        // Fixed width of each per-bone Revert button and the first part of the shared trailing area.
        private const float k_BoneRestoreButtonWidth = 80f;

        // Tight horizontal gap between a per-bone Revert button and its status label.
        private const float k_BoneStatusSpacing = 4f;

        // Extra width around the measured localized status text to prevent edge clipping.
        private const float k_BoneStatusHorizontalPadding = 4f;

        // Minimum width reserved for the Transform field and required before trailing controls remain visible.
        private const float k_BoneTransformMinimumWidth = 160f;

        // Horizontal inset that visually nests an expanded binding list beneath its proxy row.
        private const float k_BoneListIndent = 16f;

        // Inner padding around the binding rows and the proxy row's right-aligned controls.
        private const float k_BoneListPadding = 2f;

        // Vertical separation between adjacent binding rows inside an expanded proxy group.
        private const float k_BoneListRowSpacing = 2f;

        // Right-side inset that keeps proxy and binding controls away from the inspector edge.
        private const float k_BoneRightGutter = 4f;

        // Minimum source-path column width preserved while the trailing controls are visible.
        private const float k_BonePathMinimumWidth = 90f;

        // Maximum share of the main content area given to paths so the Transform field keeps room.
        private const float k_BonePathMaximumWidthRatio = 0.6f;

        private readonly Dictionary<string, BlendShapeWeightSyncState> m_BlendShapeWeightSyncStates = new();
        private readonly Dictionary<int, bool> m_BoneProxyExpanded = new();
        private static GUIStyle s_BoneStatusStyle;
        private bool m_BonesExpanded = true;
        private bool m_BoneProxyCacheDirty = true;
        private IDisposable m_BoneProxyChangeSubscription;
        private double m_SkipMappingValidationUntil;

        static BlendShareMeshEditor()
        {
            EditorApplication.contextualPropertyMenu -= AddBlendShapeAnimationMenuItems;
            EditorApplication.contextualPropertyMenu += AddBlendShapeAnimationMenuItems;
            BlendShareSkinWeightInfluenceDiagnostics.Changed -= InternalEditorUtility.RepaintAllViews;
            BlendShareSkinWeightInfluenceDiagnostics.Changed += InternalEditorUtility.RepaintAllViews;
        }

        private void OnEnable()
        {
            m_BoneProxyCacheDirty = true;
            m_BoneProxyChangeSubscription = BlendShareEditorChangeEvents.Subscribe(
                BlendShareEditorChangeKind.Hierarchy |
                BlendShareEditorChangeKind.SerializedObject |
                BlendShareEditorChangeKind.Explicit,
                OnEditorChanged,
                typeof(BlendShareMesh),
                typeof(BlendShareBoneProxy),
                typeof(SkinnedMeshRenderer));
        }

        private void OnDisable()
        {
            m_BoneProxyChangeSubscription?.Dispose();
            m_BoneProxyChangeSubscription = null;
        }

        private void OnEditorChanged(BlendShareEditorChange change)
        {
            var applier = target as BlendShareMesh;
            if (applier != null && change.ChangedObjects.Any(changedObject =>
                    changedObject == applier || changedObject == applier.TargetRenderer))
            {
                BlendShareSkinWeightInfluenceDiagnostics.Clear(applier);
            }

            m_BoneProxyCacheDirty = true;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var applier = (BlendShareMesh)target;

            serializedObject.Update();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRendererReference"), new GUIContent(Localization.S("ndmf.mesh.target_renderer")));
            var meshDataProperty = serializedObject.FindProperty("m_MeshAsset");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(meshDataProperty, Localization.G("patch.mesh_data.display_name"));
            bool meshDataChanged = EditorGUI.EndChangeCheck();
            if (meshDataChanged)
            {
                m_SkipMappingValidationUntil = EditorApplication.timeSinceStartup + 0.25d;
                serializedObject.FindProperty("m_MeshId").stringValue =
                    (meshDataProperty.objectReferenceValue as BlendShareMeshAsset)?.MeshId ?? string.Empty;
                serializedObject.FindProperty("m_Patch").objectReferenceValue =
                    BlendShareInspectorUtility.FindOwnerPatch(meshDataProperty.objectReferenceValue);
            }

            var mappingStatus = BlendShareComponentSetupService.GetMappingStatus(applier);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField(Localization.G("patch.mapping.display_name"),
                    new GUIContent(mappingStatus.Label, mappingStatus.Message));
            }

            DrawSkinWeightInfluenceInfo(applier);
            DrawBones(applier);

            var weights = serializedObject.FindProperty(BlendShareMesh.BlendShapeWeightsFieldName);
            var activeNames = BlendShareComponentSetupService.GetActiveShapeNames(applier);
            bool needsSync = !activeNames.SequenceEqual(applier.BlendShapeWeights.Select(weight => weight?.ShapeName));
            // Import settings can change while a saved scene is closed. Prepare the effective list
            // in a separate serialized buffer and commit it only if the user moves a slider.
            using var weightBuffer = needsSync ? new SerializedObject(applier) : null;
            if (needsSync)
            {
                weights = weightBuffer.FindProperty(BlendShareMesh.BlendShapeWeightsFieldName);
                weights.arraySize = activeNames.Count;
                for (int i = 0; i < activeNames.Count; i++)
                {
                    string name = activeNames[i];
                    var entry = weights.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative(BlendShareProxyBlendShapeWeight.ShapeNameFieldName).stringValue = name;
                    if (!applier.TryGetBlendShapeWeight(name, out float value) &&
                        TryGetRendererBlendShape(applier, name, out var renderer, out int index))
                        value = renderer.GetBlendShapeWeight(index);
                    entry.FindPropertyRelative(BlendShareProxyBlendShapeWeight.WeightFieldName).floatValue = value;
                }
            }
            bool changedBlendShapeWeight = DrawBlendShapeWeights(applier, weights,
                BlendShareAnimationHelper.CreateBlendShapeAnimationContext(applier));
            if (changedBlendShapeWeight && needsSync) weightBuffer.ApplyModifiedProperties();
            if (changedBlendShapeWeight)
            {
                m_SkipMappingValidationUntil = EditorApplication.timeSinceStartup + 0.25d;
            }

            if (EditorApplication.timeSinceStartup >= m_SkipMappingValidationUntil &&
                applier.isActiveAndEnabled && applier.TargetRenderer && applier.ResolvedPatch &&
                !mappingStatus.IsReady)
            {
                EditorGUILayout.HelpBox(mappingStatus.Message, MessageType.Error);
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                BlendShareSkinWeightInfluenceDiagnostics.Clear(applier);
                // Mesh-data and renderer edits can change the required cache entry.
                m_BoneProxyCacheDirty = true;
                BlendShareEditorChangeEvents.NotifyChanged(
                    BlendShareEditorChangeKind.Explicit,
                    applier);
            }

            if (meshDataChanged)
            {
                Undo.RecordObject(applier, "Set BlendShare Mesh Data");
                if (applier.SyncActiveBlendShapeWeights())
                {
                    EditorUtility.SetDirty(applier);
                }

                CreateMissingBoneProxies(applier, ResolveProxyTargetRoot(applier));
            }
        }

        private static void DrawSkinWeightInfluenceInfo(BlendShareMesh applier)
        {
            if (BlendShareComponentSetupService.GetSkin(applier) == null ||
                !BlendShareSkinWeightInfluenceDiagnostics.TryGet(applier, out var summary))
            {
                return;
            }

            if (BlendShareSkinWeightSettings.LimitSkinWeightsToFour)
            {
                if (summary.VerticesOverFour <= 0)
                {
                    return;
                }

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    Localization.S("ndmf.mesh.skin_weights_over_four_limit"),
                    MessageType.Info);
                return;
            }

            if (summary.VerticesOverByteLimit <= 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                Localization.S("ndmf.mesh.skin_weights_over_255"),
                MessageType.Warning);
        }

        private void DrawBones(BlendShareMesh applier)
        {
            var skin = BlendShareComponentSetupService.GetSkin(applier);
            if (skin == null)
            {
                return;
            }

            var targetRoot = ResolveProxyTargetRoot(applier);
            if (m_BoneProxyCacheDirty)
            {
                RefreshBoneProxyCache(applier, targetRoot);
                m_BoneProxyCacheDirty = targetRoot == null;
            }
            var entries = GetBoneEntries(applier, skin, targetRoot);
            var proxyGroups = BuildBoneProxyGroups(entries);

            EditorGUILayout.Space();
            m_BonesExpanded = EditorGUILayout.Foldout(
                m_BonesExpanded,
                Localization.G("ndmf.mesh.armature"),
                true);
            if (!m_BonesExpanded)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                if (proxyGroups.Count == 0)
                {
                    EditorGUILayout.LabelField(Localization.S("common.none"), EditorStyles.miniLabel);
                }
                else
                {
                    for (int i = 0; i < proxyGroups.Count; i++)
                    {
                        DrawBoneProxyGroup(applier, proxyGroups[i]);
                    }
                }

                bool hasMissingProxy = entries.Any(entry => !entry.Resolved.IsResolved && entry.CanCreate);
                if (hasMissingProxy)
                {
                    EditorGUILayout.Space(2f);
                    using (new EditorGUI.DisabledScope(targetRoot == null || !applier.isActiveAndEnabled))
                    {
                        if (GUILayout.Button(Localization.S("ndmf.mesh.create_missing_bones")))
                        {
                            CreateMissingBoneProxies(applier, targetRoot);
                        }
                    }
                }
            }
        }

        private static IReadOnlyList<BoneProxyGroup> BuildBoneProxyGroups(
            IReadOnlyList<BoneEntry> entries)
        {
            var groups = new List<BoneProxyGroup>();
            var groupsByProxy = new Dictionary<BlendShareBoneProxy, BoneProxyGroup>();
            foreach (var entry in entries)
            {
                var proxy = entry.Resolved.Proxy;
                if (!entry.Resolved.IsProxy || proxy == null)
                {
                    continue;
                }

                if (!groupsByProxy.TryGetValue(proxy, out var group))
                {
                    group = new BoneProxyGroup(proxy);
                    groupsByProxy[proxy] = group;
                    groups.Add(group);
                }

                // Entries already come from this mesh's resolved per-path cache. Do not expand
                // every raw binding on the component, because some bindings may be shadowed.
                group.Entries.Add(entry);
            }

            return groups;
        }

        private void DrawBoneProxyGroup(
            BlendShareMesh applier,
            BoneProxyGroup group)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();
            const float foldoutWidth = 96f;
            rowRect.xMax -= k_BoneRightGutter;
            int proxyId = group.Proxy.GetInstanceID();
            bool expanded = m_BoneProxyExpanded.TryGetValue(proxyId, out bool storedExpanded) && storedExpanded;
            var foldoutRect = new Rect(rowRect.x, rowRect.y, foldoutWidth, rowRect.height);
            expanded = EditorGUI.Foldout(
                foldoutRect,
                expanded,
                Localization.G("ndmf.mesh.bone_proxy"),
                true);
            m_BoneProxyExpanded[proxyId] = expanded;

            float contentRight = rowRect.xMax - k_BoneListPadding;
            float availableBindingRowWidth = rowRect.width - k_BoneListIndent - k_BoneListPadding * 2f;
            bool showTrailingControls = ShouldShowBoneTrailingControls(availableBindingRowWidth);
            float trailingColumnWidth = showTrailingControls ? GetBoneTrailingColumnWidth() : 0f;
            var buttonRect = new Rect(
                contentRight - trailingColumnWidth,
                rowRect.y,
                trailingColumnWidth,
                rowRect.height);
            var proxyRect = new Rect(
                foldoutRect.xMax,
                rowRect.y,
                Mathf.Max(
                    0f,
                    (showTrailingControls ? buttonRect.x - k_BoneColumnSpacing : contentRight) - foldoutRect.xMax),
                rowRect.height);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.ObjectField(
                    proxyRect,
                    group.Proxy,
                    typeof(BlendShareBoneProxy),
                    true);
            }

            if (showTrailingControls)
            {
                bool hasOverride = group.Entries.Any(entry => entry.HasBindPoseOverride);
                using (new EditorGUI.DisabledScope(!hasOverride))
                {
                    if (GUI.Button(
                            buttonRect,
                            new GUIContent(
                                Localization.S("ndmf.mesh.restore_bindposes"),
                                Localization.S("ndmf.mesh.restore_bindposes_tooltip"))))
                    {
                        RestoreProxyBindPoses(applier, group);
                    }
                }
            }

            if (!expanded)
            {
                return;
            }

            float rowHeight = EditorGUIUtility.singleLineHeight;
            float listHeight = k_BoneListPadding * 2f +
                               group.Entries.Count * rowHeight +
                               Mathf.Max(0, group.Entries.Count - 1) * k_BoneListRowSpacing;
            Rect listRect = EditorGUILayout.GetControlRect(false, listHeight);
            listRect.xMin += k_BoneListIndent;
            listRect.xMax -= k_BoneRightGutter;
            GUI.Box(listRect, GUIContent.none, GUI.skin.box);

            float bindingRowWidth = Mathf.Max(0f, listRect.width - k_BoneListPadding * 2f);
            float pathColumnWidth = GetBonePathColumnWidth(group.Entries, bindingRowWidth);
            float rowY = listRect.y + k_BoneListPadding;
            foreach (var entry in group.Entries)
            {
                DrawBoneProxyBinding(
                    applier,
                    entry,
                    new Rect(
                        listRect.x + k_BoneListPadding,
                        rowY,
                        bindingRowWidth,
                        rowHeight),
                    pathColumnWidth);
                rowY += rowHeight + k_BoneListRowSpacing;
            }
        }

        private static void DrawBoneProxyBinding(
            BlendShareMesh applier,
            BoneEntry entry,
            Rect rowRect,
            float pathColumnWidth)
        {
            string status = entry.HasBindPoseOverride
                ? Localization.S("ndmf.mesh.bind_pose_custom")
                : Localization.S("ndmf.mesh.bind_pose_source");
            bool showTrailingControls = ShouldShowBoneTrailingControls(rowRect.width);
            float statusWidth = showTrailingControls ? GetBoneStatusWidth() : 0f;
            float trailingColumnWidth = showTrailingControls ? GetBoneTrailingColumnWidth() : 0f;
            var trailingRect = new Rect(
                rowRect.xMax - trailingColumnWidth,
                rowRect.y,
                trailingColumnWidth,
                rowRect.height);
            var restoreRect = new Rect(
                trailingRect.x,
                rowRect.y,
                k_BoneRestoreButtonWidth,
                rowRect.height);
            var statusRect = new Rect(
                restoreRect.xMax + k_BoneStatusSpacing,
                rowRect.y,
                statusWidth,
                rowRect.height);
            var contentRect = new Rect(
                rowRect.x,
                rowRect.y,
                Mathf.Max(
                    0f,
                    (showTrailingControls ? trailingRect.x - k_BoneColumnSpacing : rowRect.xMax) - rowRect.x),
                rowRect.height);
            float pathWidth = Mathf.Min(pathColumnWidth, contentRect.width);
            var pathRect = new Rect(contentRect.x, contentRect.y, pathWidth, contentRect.height);
            var transformRect = new Rect(
                pathRect.xMax + k_BoneColumnSpacing,
                contentRect.y,
                Mathf.Max(0f, contentRect.xMax - pathRect.xMax - k_BoneColumnSpacing),
                contentRect.height);

            BlendShareInspectorWidgets.DrawPathLabel(
                pathRect,
                entry.SourceBonePath,
                fadeClippedHead: true);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.ObjectField(
                    transformRect,
                    new GUIContent(string.Empty, Localization.S("ndmf.mesh.bone_transform_tooltip")),
                    entry.Resolved.Transform,
                    typeof(Transform),
                    true);
            }
            if (showTrailingControls)
            {
                using (new EditorGUI.DisabledScope(!entry.HasBindPoseOverride))
                {
                    if (GUI.Button(
                            restoreRect,
                            new GUIContent(
                                Localization.S("ndmf.mesh.restore"),
                                Localization.S("ndmf.mesh.restore_tooltip"))))
                    {
                        RestoreBoneBindPose(applier, entry);
                    }
                }

                GUI.Label(statusRect, status, BoneStatusStyle);
            }
        }

        private static float GetBonePathColumnWidth(
            IEnumerable<BoneEntry> entries,
            float rowWidth)
        {
            float trailingColumnWidth = ShouldShowBoneTrailingControls(rowWidth)
                ? GetBoneTrailingColumnWidth() + k_BoneColumnSpacing
                : 0f;
            float contentWidth = Mathf.Max(
                0f,
                rowWidth - trailingColumnWidth);
            float maximumPathWidth = Mathf.Min(
                contentWidth * k_BonePathMaximumWidthRatio,
                Mathf.Max(0f, contentWidth - k_BoneColumnSpacing - k_BoneTransformMinimumWidth));
            float minimumPathWidth = Mathf.Min(k_BonePathMinimumWidth, maximumPathWidth);
            float preferredPathWidth = (entries ?? Enumerable.Empty<BoneEntry>())
                .Select(entry => BlendShareInspectorWidgets.GetPathLabelWidth(entry.SourceBonePath))
                .DefaultIfEmpty(0f)
                .Max();
            return Mathf.Clamp(preferredPathWidth, minimumPathWidth, maximumPathWidth);
        }

        private static bool ShouldShowBoneTrailingControls(float rowWidth)
        {
            float minimumContentWidth =
                k_BonePathMinimumWidth + k_BoneColumnSpacing + k_BoneTransformMinimumWidth;
            return rowWidth >= minimumContentWidth + k_BoneColumnSpacing + GetBoneTrailingColumnWidth();
        }

        private static float GetBoneTrailingColumnWidth()
        {
            return k_BoneRestoreButtonWidth + k_BoneStatusSpacing + GetBoneStatusWidth();
        }

        private static float GetBoneStatusWidth()
        {
            float sourceWidth = BoneStatusStyle.CalcSize(
                new GUIContent(Localization.S("ndmf.mesh.bind_pose_source"))).x;
            float customWidth = BoneStatusStyle.CalcSize(
                new GUIContent(Localization.S("ndmf.mesh.bind_pose_custom"))).x;
            return Mathf.Ceil(Mathf.Max(sourceWidth, customWidth) + k_BoneStatusHorizontalPadding);
        }

        private static GUIStyle BoneStatusStyle => s_BoneStatusStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset()
        };

        private static void RestoreBoneBindPose(
            BlendShareMesh applier,
            BoneEntry entry)
        {
            Undo.RecordObject(applier, "Restore BlendShare Source Bind Pose");
            if (applier.RemoveBindPoseOverride(entry.SourceBonePath))
            {
                NotifyMeshChanged(applier);
            }
        }

        private static void RestoreProxyBindPoses(
            BlendShareMesh applier,
            BoneProxyGroup group)
        {
            Undo.RecordObject(applier, "Restore BlendShare Source Bind Poses");
            bool changed = false;
            foreach (var entry in group.Entries)
            {
                changed |= applier.RemoveBindPoseOverride(entry.SourceBonePath);
            }

            if (changed)
            {
                NotifyMeshChanged(applier);
            }
        }

        private void CreateMissingBoneProxies(BlendShareMesh applier, Transform targetRoot)
        {
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(Localization.S("ndmf.mesh.create_missing_bones"));

            Transform placementParent = applier.transform.parent;
            if (placementParent == null ||
                (placementParent != targetRoot && !placementParent.IsChildOf(targetRoot)))
            {
                placementParent = targetRoot;
            }

            var result = BlendShareComponentSetupService.RebuildBoneProxies(
                new[] { applier },
                targetRoot,
                placementParent);
            m_BoneProxyCacheDirty = targetRoot == null;
            BlendShareEditorChangeEvents.NotifyChanged(
                BlendShareEditorChangeKind.Explicit,
                applier);
            Undo.CollapseUndoOperations(undoGroup);

            foreach (string diagnostic in result.Diagnostics)
            {
                Debug.LogWarning($"[BlendShare] {diagnostic}", applier);
            }

            Repaint();
            SceneView.RepaintAll();
        }

        private static Transform ResolveProxyTargetRoot(BlendShareMesh applier)
        {
            var renderer = applier.TargetRenderer;
            // NDMF/VRChat roots are authoritative; standalone setups can still infer the root from the mesh path.
            return AvatarHierarchyUtil.FindAvatarInParents(applier.transform) ??
                   AvatarHierarchyUtil.FindAvatarInParents(renderer != null ? renderer.transform : null) ??
                   BlendShareComponentSetupService.ResolveTargetRoot(renderer, applier.MeshAsset);
        }

        private static IReadOnlyList<BoneEntry> GetBoneEntries(
            BlendShareMesh applier,
            SkinHeaders skin,
            Transform targetRoot)
        {
            if (applier.TargetRenderer == null || skin == null)
            {
                return Array.Empty<BoneEntry>();
            }

            var entries = new List<BoneEntry>();

            foreach (string bonePath in skin.BonePaths)
            {
                applier.TryGetCachedBone(bonePath, out var resolved);
                entries.Add(new BoneEntry(
                    bonePath,
                    resolved,
                    applier.CreateMissingBones && targetRoot != null && !targetRoot.Find(bonePath),
                    applier.TryGetBindPoseOverride(bonePath, out _)));
            }

            return entries;
        }

        private static void NotifyMeshChanged(BlendShareMesh applier)
        {
            EditorUtility.SetDirty(applier);
            BlendShareEditorChangeEvents.NotifyChanged(BlendShareEditorChangeKind.Explicit, applier);
            SceneView.RepaintAll();
        }

        private sealed class BoneEntry
        {
            public BoneEntry(
                string sourceBonePath,
                BlendShareResolvedBone resolved,
                bool canCreate,
                bool hasBindPoseOverride)
            {
                SourceBonePath = sourceBonePath;
                Resolved = resolved;
                CanCreate = canCreate;
                HasBindPoseOverride = hasBindPoseOverride;
            }

            public string SourceBonePath { get; }
            public BlendShareResolvedBone Resolved { get; }
            public bool CanCreate { get; }
            public bool HasBindPoseOverride { get; }
        }

        private sealed class BoneProxyGroup
        {
            public BoneProxyGroup(BlendShareBoneProxy proxy)
            {
                Proxy = proxy;
            }

            public BlendShareBoneProxy Proxy { get; }
            public List<BoneEntry> Entries { get; } = new();
        }

        private static void RefreshBoneProxyCache(
            BlendShareMesh applier,
            Transform targetRoot)
        {
            if (applier == null || targetRoot == null)
            {
                return;
            }

            BlendShareComponentSetupService.RefreshBoneProxyCaches(new[] { applier },
                targetRoot,
                targetRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
        }

        /// <inheritdoc />
        public override bool RequiresConstantRepaint()
        {
            return AnimationMode.InAnimationMode();
        }

        private bool DrawBlendShapeWeights(
            BlendShareMesh applier,
            SerializedProperty weightsProperty,
            BlendShareAnimationHelper.BlendShapeAnimationContext animationContext)
        {
            if (weightsProperty == null || !weightsProperty.isArray || weightsProperty.arraySize == 0)
            {
                return false;
            }

            bool changed = false;
            SynchronizeBlendShapeWeights(applier, weightsProperty);

            EditorGUILayout.Space();
            GUIContent label = Localization.G("features.blend-shapes.name");
            Rect rect = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(rect, label, weightsProperty);
            weightsProperty.isExpanded = EditorGUI.Foldout(rect, weightsProperty.isExpanded, label, true);
            EditorGUI.EndProperty();

            if(weightsProperty.isExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    for (int i = 0; i < weightsProperty.arraySize; i++)
                    {
                        var item = weightsProperty.GetArrayElementAtIndex(i);
                        var shapeName = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.ShapeNameFieldName);
                        var weight = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.WeightFieldName);
                        if (shapeName == null || weight == null)
                        {
                            continue;
                        }
                        Rect shapeRect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
                        GUIContent shapeLabel = new GUIContent(shapeName.stringValue);
                        shapeLabel = EditorGUI.BeginProperty(shapeRect, shapeLabel, weight);
                        bool isAnimated = animationContext.TryGetAnimatedBlendShapeValue(
                            shapeName.stringValue,
                            out float animatedValue,
                            out bool isRecording);
                        bool rendererControlsWeight = TryGetRendererBlendShape(
                            applier,
                            shapeName.stringValue,
                            out var targetRenderer,
                            out int rendererBlendShapeIndex);
                        float rendererWeight = rendererControlsWeight
                            ? targetRenderer.GetBlendShapeWeight(rendererBlendShapeIndex)
                            : 0f;

                        float displayValue = rendererControlsWeight
                            ? rendererWeight
                            : isAnimated ? animatedValue : weight.floatValue;

                        var contentRect = EditorGUI.PrefixLabel(shapeRect, shapeLabel);

                        Color previousColor = GUI.color;
                        if (isAnimated)
                        {
                            GUI.color = isRecording
                                ? AnimationMode.recordedPropertyColor
                                : AnimationMode.animatedPropertyColor;
                        }

                        EditorGUI.BeginChangeCheck();

                        float newValue = EditorGUI.Slider(
                            contentRect,
                            displayValue,
                            0f,
                            100f);
                        GUI.color = previousColor;
                        if (EditorGUI.EndChangeCheck())
                        {
                            weight.floatValue = newValue;
                            if (rendererControlsWeight)
                            {
                                Undo.RecordObject(targetRenderer, "Set BlendShape Weight");
                                targetRenderer.SetBlendShapeWeight(rendererBlendShapeIndex, newValue);
                                EditorUtility.SetDirty(targetRenderer);
                                m_BlendShapeWeightSyncStates[shapeName.stringValue] = new BlendShapeWeightSyncState(
                                    targetRenderer,
                                    rendererBlendShapeIndex,
                                    newValue,
                                    newValue);
                            }

                            RecordProxyBlendShapeAnimation(applier, shapeName.stringValue, newValue);
                            SceneView.RepaintAll();
                            changed = true;
                        }
                        EditorGUI.EndProperty();

                    }
                }
            }

            return changed;
        }

        private void SynchronizeBlendShapeWeights(
            BlendShareMesh applier,
            SerializedProperty weightsProperty)
        {
            for (int index = 0; index < weightsProperty.arraySize; index++)
            {
                var item = weightsProperty.GetArrayElementAtIndex(index);
                var shapeName = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.ShapeNameFieldName);
                var weight = item.FindPropertyRelative(BlendShareProxyBlendShapeWeight.WeightFieldName);
                if (shapeName == null || weight == null)
                {
                    continue;
                }

                if (TryGetRendererBlendShape(
                        applier,
                        shapeName.stringValue,
                        out var renderer,
                        out int blendShapeIndex))
                {
                    SynchronizeBlendShapeWeight(
                        shapeName.stringValue,
                        weight,
                        renderer,
                        blendShapeIndex,
                        renderer.GetBlendShapeWeight(blendShapeIndex));
                }
                else
                {
                    m_BlendShapeWeightSyncStates.Remove(shapeName.stringValue);
                }
            }
        }

        private float SynchronizeBlendShapeWeight(
            string shapeName,
            SerializedProperty componentWeight,
            SkinnedMeshRenderer renderer,
            int blendShapeIndex,
            float rendererWeight)
        {
            float serializedWeight = componentWeight.floatValue;
            bool hasPreviousState = m_BlendShapeWeightSyncStates.TryGetValue(shapeName, out var previousState) &&
                                    previousState.Renderer == renderer &&
                                    previousState.BlendShapeIndex == blendShapeIndex;

            if (!AnimationMode.InAnimationMode())
            {
                bool rendererChanged = hasPreviousState &&
                                       !Mathf.Approximately(rendererWeight, previousState.RendererWeight);
                bool componentChanged = hasPreviousState &&
                                        !Mathf.Approximately(serializedWeight, previousState.ComponentWeight);

                if (componentChanged && !rendererChanged)
                {
                    // Prefab Revert and Undo are component-only changes.
                    renderer.SetBlendShapeWeight(blendShapeIndex, serializedWeight);
                    EditorUtility.SetDirty(renderer);
                    rendererWeight = serializedWeight;
                    SceneView.RepaintAll();
                }
                else if (rendererChanged &&
                         !Mathf.Approximately(serializedWeight, rendererWeight))
                {
                    // Preserve explicit renderer edits when both weights changed.
                    componentWeight.floatValue = rendererWeight;
                    serializedWeight = rendererWeight;
                }
            }

            m_BlendShapeWeightSyncStates[shapeName] = new BlendShapeWeightSyncState(
                renderer,
                blendShapeIndex,
                rendererWeight,
                serializedWeight);
            return rendererWeight;
        }

        private readonly struct BlendShapeWeightSyncState
        {
            public BlendShapeWeightSyncState(
                SkinnedMeshRenderer renderer,
                int blendShapeIndex,
                float rendererWeight,
                float componentWeight)
            {
                Renderer = renderer;
                BlendShapeIndex = blendShapeIndex;
                RendererWeight = rendererWeight;
                ComponentWeight = componentWeight;
            }

            public SkinnedMeshRenderer Renderer { get; }
            public int BlendShapeIndex { get; }
            public float RendererWeight { get; }
            public float ComponentWeight { get; }
        }

        private static bool TryGetRendererBlendShape(
            BlendShareMesh applier,
            string shapeName,
            out SkinnedMeshRenderer renderer,
            out int blendShapeIndex)
        {
            renderer = applier != null ? applier.TargetRenderer : null;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            blendShapeIndex = mesh != null && !string.IsNullOrWhiteSpace(shapeName)
                ? mesh.GetBlendShapeIndex(shapeName)
                : -1;
            return blendShapeIndex >= 0;
        }

        private static void RecordProxyBlendShapeAnimation(BlendShareMesh applier, string shapeName, float value)
        {
            if (applier == null || string.IsNullOrWhiteSpace(shapeName))
            {
                return;
            }

            BlendShareAnimationHelper.RecordBlendShapeValue(applier, shapeName, value);
        }

        private static void AddBlendShapeAnimationMenuItems(
            GenericMenu menu,
            SerializedProperty property)
        {
            if (!BlendShareAnimationHelper.IsAnimationPreviewOrRecordingActive() ||
                !TryGetBlendShapeMenuContext(property, out var applier, out string shapeName, out float value))
            {
                return;
            }

            menu.AddSeparator(string.Empty);
            AddBlendShapeAnimationMenuItems(menu, applier, shapeName, value);
        }

        private static bool TryGetBlendShapeMenuContext(
            SerializedProperty property,
            out BlendShareMesh applier,
            out string shapeName,
            out float value)
        {
            applier = null;
            shapeName = null;
            value = 0f;
            if (property == null ||
                property.serializedObject?.targetObject is not BlendShareMesh blendShareMesh ||
                property.propertyType != SerializedPropertyType.Float ||
                !property.propertyPath.EndsWith($".{BlendShareProxyBlendShapeWeight.WeightFieldName}") ||
                !property.propertyPath.Contains($"{BlendShareMesh.BlendShapeWeightsFieldName}.Array.data["))
            {
                return false;
            }

            string shapeNamePath = property.propertyPath.Substring(
                0,
                property.propertyPath.Length - BlendShareProxyBlendShapeWeight.WeightFieldName.Length) +
                BlendShareProxyBlendShapeWeight.ShapeNameFieldName;
            var shapeNameProperty = property.serializedObject.FindProperty(shapeNamePath);
            if (shapeNameProperty == null ||
                string.IsNullOrWhiteSpace(shapeNameProperty.stringValue))
            {
                return false;
            }

            applier = blendShareMesh;
            shapeName = shapeNameProperty.stringValue;
            if (TryGetRendererBlendShape(applier, shapeName, out var renderer, out int blendShapeIndex))
            {
                value = renderer.GetBlendShapeWeight(blendShapeIndex);
            }
            else
            {
                value = BlendShareAnimationHelper.TryGetAnimatedBlendShapeValue(
                    applier,
                    shapeName,
                    out float animatedValue,
                    out _)
                    ? animatedValue
                    : property.floatValue;
            }

            return true;
        }

        private static void AddBlendShapeAnimationMenuItems(
            GenericMenu menu,
            BlendShareMesh applier,
            string shapeName,
            float value)
        {
            if (BlendShareAnimationHelper.CanEditActiveBlendShapeCurve(applier, shapeName))
            {
                menu.AddItem(
                    Localization.G("ndmf.mesh.animation.add_key"),
                    false,
                    () => BlendShareAnimationHelper.AddBlendShapeKey(applier, shapeName, value));
            }
            else
            {
                menu.AddDisabledItem(Localization.G("ndmf.mesh.animation.add_key"));
            }

            if (BlendShareAnimationHelper.HasBlendShapeKeyAtCurrentTime(applier, shapeName))
            {
                menu.AddItem(
                    Localization.G("ndmf.mesh.animation.remove_key"),
                    false,
                    () => BlendShareAnimationHelper.RemoveBlendShapeKey(applier, shapeName));
            }
            else
            {
                menu.AddDisabledItem(Localization.G("ndmf.mesh.animation.remove_key"));
            }

            if (BlendShareAnimationHelper.HasBlendShapeCurve(applier, shapeName))
            {
                menu.AddItem(
                    Localization.G("ndmf.mesh.animation.remove_all_keys"),
                    false,
                    () => BlendShareAnimationHelper.RemoveAllBlendShapeKeys(applier, shapeName));
            }
            else
            {
                menu.AddDisabledItem(Localization.G("ndmf.mesh.animation.remove_all_keys"));
            }
        }
    }
    [Flags]
    internal enum BlendShareEditorChangeKind
    {
        None = 0,
        Hierarchy = 1 << 0,
        SerializedObject = 1 << 1,
        UndoRedo = 1 << 2,
        Project = 1 << 3,
        Explicit = 1 << 4,
        All = Hierarchy | SerializedObject | UndoRedo | Project | Explicit
    }

    internal readonly struct BlendShareEditorChange
    {
        internal BlendShareEditorChange(
            BlendShareEditorChangeKind kinds,
            IReadOnlyList<Object> changedObjects)
        {
            Kinds = kinds;
            ChangedObjects = changedObjects ?? Array.Empty<Object>();
        }

        public BlendShareEditorChangeKind Kinds { get; }
        public IReadOnlyList<Object> ChangedObjects { get; }
    }

    /// <summary>Publishes coalesced, source-level Unity editor change notifications.</summary>
    [InitializeOnLoad]
    internal static class BlendShareEditorChangeEvents
    {
        private const BlendShareEditorChangeKind k_TargetedKinds =
            BlendShareEditorChangeKind.SerializedObject |
            BlendShareEditorChangeKind.Explicit;

        private static readonly List<Subscription> s_Subscriptions = new();
        private static readonly Dictionary<Object, BlendShareEditorChangeKind> s_PendingObjectKinds = new();
        private static BlendShareEditorChangeKind s_PendingKinds;
        private static bool s_PublishScheduled;

        static BlendShareEditorChangeEvents()
        {
            EditorApplication.hierarchyChanged += () => NotifyIfSubscribed(BlendShareEditorChangeKind.Hierarchy);
            EditorApplication.projectChanged += () => NotifyIfSubscribed(BlendShareEditorChangeKind.Project);
            Undo.undoRedoPerformed += () => NotifyIfSubscribed(BlendShareEditorChangeKind.UndoRedo);
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        internal static IDisposable Subscribe(
            BlendShareEditorChangeKind kinds,
            Action<BlendShareEditorChange> callback,
            params Type[] interestedTypes)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var subscription = new Subscription(kinds, interestedTypes, callback);
            s_Subscriptions.Add(subscription);
            return subscription;
        }

        internal static void NotifyChanged(
            BlendShareEditorChangeKind kind,
            params Object[] changedObjects)
        {
            if (kind == BlendShareEditorChangeKind.None)
            {
                return;
            }

            s_PendingKinds |= kind;
            foreach (var changedObject in changedObjects ?? Array.Empty<Object>())
            {
                if (ReferenceEquals(changedObject, null))
                {
                    continue;
                }

                s_PendingObjectKinds.TryGetValue(changedObject, out var objectKinds);
                s_PendingObjectKinds[changedObject] = objectKinds | kind;
            }

            if (s_PublishScheduled)
            {
                return;
            }

            s_PublishScheduled = true;
            EditorApplication.delayCall += Publish;
        }

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] modifications)
        {
            if (!HasSubscribers(BlendShareEditorChangeKind.SerializedObject))
            {
                return modifications;
            }

            var changedObjects = (modifications ?? Array.Empty<UndoPropertyModification>())
                .Select(modification =>
                    modification.currentValue?.target ?? modification.previousValue?.target)
                .Where(target => !ReferenceEquals(target, null))
                .Distinct()
                .ToArray();
            if (changedObjects.Length > 0)
            {
                NotifyChanged(BlendShareEditorChangeKind.SerializedObject, changedObjects);
            }

            return modifications;
        }

        private static void NotifyIfSubscribed(BlendShareEditorChangeKind kind)
        {
            if (HasSubscribers(kind))
            {
                NotifyChanged(kind);
            }
        }

        private static bool HasSubscribers(BlendShareEditorChangeKind kind)
        {
            return s_Subscriptions.Any(subscription => subscription.Includes(kind));
        }

        internal static void Publish()
        {
            s_PublishScheduled = false;
            var kinds = s_PendingKinds;
            var objectKinds = s_PendingObjectKinds.ToArray();
            s_PendingKinds = BlendShareEditorChangeKind.None;
            s_PendingObjectKinds.Clear();
            if (kinds == BlendShareEditorChangeKind.None)
            {
                return;
            }

            foreach (var subscription in s_Subscriptions.ToArray())
            {
                subscription.Publish(kinds, objectKinds);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly BlendShareEditorChangeKind m_Kinds;
            private readonly Type[] m_InterestedTypes;
            private readonly Action<BlendShareEditorChange> m_Callback;
            private bool m_Disposed;

            public Subscription(
                BlendShareEditorChangeKind kinds,
                IEnumerable<Type> interestedTypes,
                Action<BlendShareEditorChange> callback)
            {
                this.m_Kinds = kinds;
                this.m_InterestedTypes = (interestedTypes ?? Array.Empty<Type>())
                    .Where(type => type != null)
                    .Distinct()
                    .ToArray();
                this.m_Callback = callback;
            }

            public void Dispose()
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                s_Subscriptions.Remove(this);
            }

            public bool Includes(BlendShareEditorChangeKind kind)
            {
                return !m_Disposed && (m_Kinds & kind) != 0;
            }

            public void Publish(
                BlendShareEditorChangeKind publishedKinds,
                IReadOnlyList<KeyValuePair<Object, BlendShareEditorChangeKind>> objectKinds)
            {
                if (m_Disposed)
                {
                    return;
                }

                var matchedKinds = publishedKinds & m_Kinds;
                if (matchedKinds == BlendShareEditorChangeKind.None)
                {
                    return;
                }

                var broadKinds = matchedKinds & ~k_TargetedKinds;
                var targetedKinds = matchedKinds & k_TargetedKinds;
                var matchedEntries = (objectKinds ?? Array.Empty<KeyValuePair<Object, BlendShareEditorChangeKind>>())
                    .Where(entry => (entry.Value & targetedKinds) != 0 && MatchesType(entry.Key))
                    .ToArray();
                if (broadKinds == BlendShareEditorChangeKind.None && matchedEntries.Length == 0)
                {
                    return;
                }

                var deliveredKinds = broadKinds | matchedEntries.Aggregate(
                        BlendShareEditorChangeKind.None,
                        (current, entry) => current | (entry.Value & targetedKinds));
                var matchedObjects = matchedEntries.Select(entry => entry.Key).ToArray();
                try
                {
                    m_Callback(new BlendShareEditorChange(deliveredKinds, matchedObjects));
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            private bool MatchesType(Object changedObject)
            {
                return !ReferenceEquals(changedObject, null) &&
                       (m_InterestedTypes.Length == 0 ||
                        m_InterestedTypes.Any(type => type.IsInstanceOfType(changedObject)));
            }
        }
    }
    internal static class BlendShareSkinWeightLimitMenu
    {
        private const string k_LimitSkinWeightsMenuPath =
            "Tools/BlendShare/Limit Skin Weights to 4 Influences";

        [MenuItem(k_LimitSkinWeightsMenuPath, false, 200)]
        private static void ToggleSkinWeightLimit()
        {
            BlendShareSkinWeightSettings.SetLimitSkinWeightsToFour(
                !BlendShareSkinWeightSettings.LimitSkinWeightsToFour);
            InternalEditorUtility.RepaintAllViews();
        }

        [MenuItem(k_LimitSkinWeightsMenuPath, true)]
        private static bool ValidateSkinWeightLimit()
        {
            Menu.SetChecked(k_LimitSkinWeightsMenuPath, BlendShareSkinWeightSettings.LimitSkinWeightsToFour);
            return true;
        }
    }
}
