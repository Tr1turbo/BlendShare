using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Editor;
using Triturbo.BlendShare.Features.SkinWeights;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    [CustomEditor(typeof(BlendShareMesh))]
    public sealed class BlendShareMeshEditor : UnityEditor.Editor
    {
        private readonly Dictionary<string, BlendShapeWeightSyncState> blendShapeWeightSyncStates = new();
        private bool boneProxiesExpanded = true;
        private bool boneProxyCacheDirty = true;
        private IDisposable boneProxyChangeSubscription;
        private double skipMappingValidationUntil;

        static BlendShareMeshEditor()
        {
            EditorApplication.contextualPropertyMenu -= AddBlendShapeAnimationMenuItems;
            EditorApplication.contextualPropertyMenu += AddBlendShapeAnimationMenuItems;
        }

        private void OnEnable()
        {
            boneProxyCacheDirty = true;
            boneProxyChangeSubscription = BlendShareEditorChangeEvents.Subscribe(
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
            boneProxyChangeSubscription?.Dispose();
            boneProxyChangeSubscription = null;
        }

        private void OnEditorChanged(BlendShareEditorChange change)
        {
            boneProxyCacheDirty = true;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var applier = (BlendShareMesh)target;
            if (applier.SyncActiveBlendShapeWeights())
            {
                EditorUtility.SetDirty(applier);
            }

            serializedObject.Update();

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_TargetRendererReference"), new GUIContent(Localization.S("ndmf.mesh.target_renderer")));
            var meshDataProperty = serializedObject.FindProperty("m_MeshData");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(meshDataProperty);
            bool meshDataChanged = EditorGUI.EndChangeCheck();
            if (meshDataChanged)
            {
                skipMappingValidationUntil = EditorApplication.timeSinceStartup + 0.25d;
                serializedObject.FindProperty("m_Patch").objectReferenceValue =
                    BlendShareInspectorUtility.FindOwnerPatch(meshDataProperty.objectReferenceValue);
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("m_Mapping"),
                    new GUIContent(Localization.S("patch.mapping.display_name")));
            }

            DrawBoneProxies(applier);

            bool changedBlendShapeWeight = DrawBlendShapeWeights(
                applier,
                serializedObject.FindProperty(BlendShareMesh.BlendShapeWeightsFieldName),
                BlendShareAnimationHelper.CreateBlendShapeAnimationContext(applier));
            if (changedBlendShapeWeight)
            {
                skipMappingValidationUntil = EditorApplication.timeSinceStartup + 0.25d;
            }

            if (EditorApplication.timeSinceStartup >= skipMappingValidationUntil &&
                !BlendShareComponentSetupService.ValidateMeshApplierMapping(applier, out string mappingDiagnostic))
            {
                bool showMappingError = true;
                if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out string cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else if (BlendShareComponentSetupService.EnsureMeshApplierMappingCache(applier, out string createdDiagnostic))
                {
                    showMappingError = false;
                }
                else if (BlendShareComponentSetupService.TryGetCachedInvalidMappingDiagnostic(applier, out cachedDiagnostic))
                {
                    mappingDiagnostic = cachedDiagnostic;
                }
                else
                {
                    mappingDiagnostic = createdDiagnostic;
                }

                if (showMappingError)
                {
                    EditorGUILayout.HelpBox(mappingDiagnostic, MessageType.Error);
                }
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                // Mesh-data and renderer edits can change the required cache entry.
                boneProxyCacheDirty = true;
                BlendShareEditorChangeEvents.NotifyChanged(
                    BlendShareEditorChangeKind.Explicit,
                    applier);
                BlendShareComponentSetupService.TryResolveMeshApplierMappingReference(applier, out _);
            }

            if (meshDataChanged)
            {
                if (applier.SyncActiveBlendShapeWeights())
                {
                    EditorUtility.SetDirty(applier);
                }

                CreateMissingBoneProxies(applier, ResolveProxyTargetRoot(applier));
            }
        }

        private void DrawBoneProxies(BlendShareMesh applier)
        {
            var skin = applier.MeshData?.GetFeature<SkinWeightFeatureObject>();
            if (skin == null)
            {
                return;
            }

            var targetRoot = ResolveProxyTargetRoot(applier);
            if (boneProxyCacheDirty)
            {
                RefreshBoneProxyCache(applier, targetRoot);
                boneProxyCacheDirty = targetRoot == null;
            }
            var entries = GetBoneProxyEntries(applier, skin, targetRoot);

            EditorGUILayout.Space();
            boneProxiesExpanded = EditorGUILayout.Foldout(
                boneProxiesExpanded,
                Localization.G("ndmf.mesh.bone_proxies"),
                true);
            if (!boneProxiesExpanded)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                if (entries.Count == 0)
                {
                    EditorGUILayout.LabelField(Localization.S("common.none"), EditorStyles.miniLabel);
                }
                else
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        foreach (var entry in entries)
                        {
                            EditorGUILayout.ObjectField(
                                new GUIContent(MeshNodePath.LeafName(entry.BonePath), entry.BonePath),
                                entry.Proxy,
                                typeof(BlendShareBoneProxy),
                                true);
                        }
                    }
                }

                bool hasMissingProxy = entries.Any(entry => entry.Proxy == null && entry.CanCreate);
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
            boneProxyCacheDirty = targetRoot == null;
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
                   BlendShareComponentSetupService.ResolveTargetRoot(renderer, applier.MeshData);
        }

        private static IReadOnlyList<(string BonePath, BlendShareBoneProxy Proxy, bool CanCreate)> GetBoneProxyEntries(
            BlendShareMesh applier,
            SkinWeightFeatureObject skin,
            Transform targetRoot)
        {
            if (applier.TargetRenderer == null || skin?.Armature == null)
            {
                return Array.Empty<(string, BlendShareBoneProxy, bool)>();
            }

            var existingBonePaths = targetRoot != null
                ? new HashSet<string>((applier.TargetRenderer.bones ?? Array.Empty<Transform>())
                    .Where(bone => bone != null)
                    .Select(bone => MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, targetRoot))))
                : new HashSet<string>();
            var entries = new List<(string BonePath, BlendShareBoneProxy Proxy, bool CanCreate)>();

            foreach (string bonePath in skin.GetNeededBonePathsInArmatureOrder())
            {
                if (existingBonePaths.Contains(bonePath))
                {
                    continue;
                }

                applier.TryGetCachedBoneProxy(bonePath, out var proxy);
                entries.Add((
                    bonePath,
                    proxy,
                    skin.Armature.GetBone(bonePath)?.m_CreateIfMissing == true));
            }

            return entries;
        }

        private static void RefreshBoneProxyCache(
            BlendShareMesh applier,
            Transform targetRoot)
        {
            if (applier == null || targetRoot == null)
            {
                return;
            }

            applier.RefreshBoneProxyCache(
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
                                blendShapeWeightSyncStates[shapeName.stringValue] = new BlendShapeWeightSyncState(
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
                    blendShapeWeightSyncStates.Remove(shapeName.stringValue);
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
            bool hasPreviousState = blendShapeWeightSyncStates.TryGetValue(shapeName, out var previousState) &&
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
                else if ((!hasPreviousState || rendererChanged) &&
                         !Mathf.Approximately(serializedWeight, rendererWeight))
                {
                    // Renderer wins on first observation and simultaneous changes.
                    componentWeight.floatValue = rendererWeight;
                    serializedWeight = rendererWeight;
                }
            }

            blendShapeWeightSyncStates[shapeName] = new BlendShapeWeightSyncState(
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
}
