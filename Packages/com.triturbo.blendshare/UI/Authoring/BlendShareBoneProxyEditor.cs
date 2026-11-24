using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.EditorAssets.Binding;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Authoring
{
    [Flags]
    internal enum BlendShareBoneProxyBindingStatus
    {
        Unavailable = 0,
        Source = 1 << 0,
        Override = 1 << 1,
        Modified = 1 << 2,
        Unknown = 1 << 3,
        Shadowed = 1 << 4
    }

    internal readonly struct BlendShareOverriddenBinding
    {
        internal BlendShareOverriddenBinding(string sourceBonePath, string overridingProxyPath)
        {
            SourceBonePath = sourceBonePath;
            OverridingProxyPath = overridingProxyPath;
        }

        internal string SourceBonePath { get; }
        internal string OverridingProxyPath { get; }
    }

    internal readonly struct BlendShareBindingRevertStep
    {
        internal BlendShareBindingRevertStep(
            Transform finalTransform,
            Transform effectiveParent,
            UnityLocalTransform localTransform)
        {
            FinalTransform = finalTransform;
            EffectiveParent = effectiveParent;
            LocalTransform = localTransform;
        }

        internal Transform FinalTransform { get; }
        internal Transform EffectiveParent { get; }
        internal UnityLocalTransform LocalTransform { get; }
    }

    internal sealed class BlendShareBindingRevertPlan
    {
        internal BlendShareBindingRevertPlan(
            IReadOnlyList<BlendShareBindingRevertStep> steps,
            IReadOnlyList<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)> usages)
        {
            Steps = steps;
            Usages = usages;
        }

        internal IReadOnlyList<BlendShareBindingRevertStep> Steps { get; }
        internal IReadOnlyList<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)> Usages { get; }
    }

    [CustomEditor(typeof(BlendShareBoneProxy))]
    public sealed class BlendShareBoneProxyEditor : UnityEditor.Editor
    {
        private const float k_TransformPositionEpsilon = 0.0001f;
        private const float k_TransformRotationEpsilon = 0.01f;
        private const float k_TransformScaleEpsilon = 0.0001f;
        private const float k_BindPoseMatrixEpsilon = 0.0001f;

        private static readonly Lazy<GUIStyle> s_SectionDescriptionStyle = new(() =>
            new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                richText = false
            });

        public override void OnInspectorGUI()
        {
            var proxy = (BlendShareBoneProxy)target;
            serializedObject.Update();
            var availableProxies = proxy.transform.root.GetComponentsInChildren<BlendShareBoneProxy>(true);
            var resolution = BlendShareBoneProxyResolver.Resolve(proxy.Patch, availableProxies);
            var bindingStatuses = GetBindingStatuses(proxy, resolution);
            BlendShareBoneProxyBindingDrawer.SetBindingState(proxy, bindingStatuses, resolution);

            Localization.DrawLanguageSelection();
            EditorGUILayout.Space();

            DrawSectionHeader("ndmf.bone_proxy.parenting");
            EditorGUILayout.BeginVertical("box");
            var useHierarchyParentProperty = serializedObject.FindProperty("m_UseHierarchyParent");
            DrawParentingMode(useHierarchyParentProperty);

            if (!useHierarchyParentProperty.boolValue)
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("m_TargetParentReference"),
                    new GUIContent(Localization.S("ndmf.bone_proxy.target_parent")));
            }
            EditorGUILayout.LabelField(
                Localization.S(useHierarchyParentProperty.boolValue
                    ? "ndmf.bone_proxy.parenting_existing_description"
                    : "ndmf.bone_proxy.parenting_retarget_description"),
                s_SectionDescriptionStyle.Value);
            EditorGUILayout.EndVertical();

            if (serializedObject.ApplyModifiedProperties())
            {
                NotifyPreviewInputChanged(proxy);
            }
            DrawBindingValidation(proxy);

            EditorGUILayout.Space(4f);
            foreach (var conflicts in GetOverriddenBindings(proxy, resolution)
                         .GroupBy(conflict => conflict.OverridingProxyPath, StringComparer.Ordinal))
            {
                string details = string.Join(", ", conflicts.Select(conflict =>
                    $"{conflict.SourceBonePath} → {conflict.OverridingProxyPath}"));
                EditorGUILayout.HelpBox(
                    Localization.SF("ndmf.bone_proxy.bindings_overridden", details),
                    MessageType.Warning);
            }
            //EditorGUILayout.BeginVertical("box");
            DrawSectionHeader("ndmf.bone_proxy.source_and_bindings");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Patch"),
                Localization.G("ndmf.bone_proxy.source_armature"));
            var bindingsProperty = serializedObject.FindProperty("m_Bindings");
            EditorGUILayout.PropertyField(
                bindingsProperty,
                new GUIContent(Localization.S("ndmf.bone_proxy.bindings")),
                true);
            if (serializedObject.ApplyModifiedProperties())
            {
                NotifyPreviewInputChanged(proxy);
            }
            //EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4f);


            DrawSectionHeader("ndmf.bone_proxy.bind_pose_actions");
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                Localization.S("ndmf.bone_proxy.bind_pose_actions_description"),
                s_SectionDescriptionStyle.Value);
            EditorGUILayout.Space(2f);
            bool hasRestorableBinding = bindingStatuses.Values.Any(status =>
                (status & (BlendShareBoneProxyBindingStatus.Override |
                           BlendShareBoneProxyBindingStatus.Modified |
                           BlendShareBoneProxyBindingStatus.Unknown)) != 0);
            BlendShareBindingRevertPlan revertPlan = null;
            string revertDiagnostic = null;
            bool canRevertBinding = hasRestorableBinding &&
                                    TryCreateBindingRevertPlan(
                                        proxy,
                                        out revertPlan,
                                        out revertDiagnostic);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(
                    Localization.S("ndmf.bone_proxy.rebind_bones"),
                    Localization.S("ndmf.bone_proxy.rebind_bones_tooltip"))))
            {
                var changedMeshes = CaptureBindPosesInAllUsingMeshes(proxy);
                NotifyPreviewInputChanged(proxy, changedMeshes);
            }
            bool canLoadEmbeddedPose = hasRestorableBinding && !canRevertBinding &&
                GetUsingMeshes(proxy).Any(usage => BlendShareComponentSetupService.HasEmbeddedMapping(usage.Mesh));
            using (new EditorGUI.DisabledScope(!canRevertBinding && !canLoadEmbeddedPose))
            {
                if (GUILayout.Button(new GUIContent(
                        Localization.S("ndmf.bone_proxy.restore"),
                        Localization.S("ndmf.bone_proxy.restore_tooltip"))))
                {
                    if (revertPlan != null || TryCreateBindingRevertPlan(proxy, out revertPlan, out revertDiagnostic, true))
                    {
                        var changedMeshes = ApplyBindingRevertPlan(revertPlan);
                        NotifyPreviewInputChanged(proxy, changedMeshes);
                    }
                    else
                    {
                        Debug.LogWarning(revertDiagnostic, proxy);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            if (hasRestorableBinding &&
                !canRevertBinding && !canLoadEmbeddedPose &&
                !string.IsNullOrWhiteSpace(revertDiagnostic))
            {
                EditorGUILayout.HelpBox(revertDiagnostic, MessageType.Info);
            }
            EditorGUILayout.EndVertical();
        }

        private static IReadOnlyDictionary<BlendShareBoneProxyBinding, BlendShareBoneProxyBindingStatus>
            GetBindingStatuses(
                BlendShareBoneProxy proxy,
                BlendShareBoneProxyResolution resolution)
        {
            var result = new Dictionary<BlendShareBoneProxyBinding, BlendShareBoneProxyBindingStatus>();
            if (proxy == null)
            {
                return result;
            }

            var usages = GetUsingMeshes(proxy).ToArray();
            var sourceMapping = GetSourceMapping(proxy);
            foreach (var binding in proxy.Bindings.Where(binding => binding != null))
            {
                if (!binding.IsConfigured)
                {
                    result[binding] = BlendShareBoneProxyBindingStatus.Unavailable;
                    continue;
                }

                if (resolution != null &&
                    resolution.TryGetBinding(binding.SourceBonePath, out var effective) &&
                    (effective.Proxy != proxy || !ReferenceEquals(effective.Binding, binding)))
                {
                    result[binding] = BlendShareBoneProxyBindingStatus.Shadowed;
                    continue;
                }

                var overrides = usages
                    .Where(usage => ReferenceEquals(usage.Binding, binding))
                    .Select(usage =>
                    {
                        bool hasOverride = usage.Mesh.TryGetBindPoseOverride(
                            binding.SourceBonePath,
                            out var bindPose);
                        return (Mesh: usage.Mesh, HasOverride: hasOverride, BindPose: bindPose);
                    })
                    .Where(usage => usage.HasOverride)
                    .ToArray();
                if (overrides.Length > 0)
                {
                    bool canCompareEveryOverride = true;
                    bool matchesEveryOverride = true;
                    foreach (var usage in overrides)
                    {
                        var renderer = usage.Mesh.TargetRenderer;
                        if (renderer == null || binding.Transform == null)
                        {
                            canCompareEveryOverride = false;
                            continue;
                        }

                        Matrix4x4 currentBindPose =
                            binding.Transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                        matchesEveryOverride &= Approximately(
                            currentBindPose,
                            usage.BindPose,
                            k_BindPoseMatrixEpsilon);
                    }
                    result[binding] = BlendShareBoneProxyBindingStatus.Override;
                    if (!canCompareEveryOverride)
                    {
                        result[binding] |= BlendShareBoneProxyBindingStatus.Unknown;
                    }
                    else if (!matchesEveryOverride)
                    {
                        result[binding] |= BlendShareBoneProxyBindingStatus.Modified;
                    }
                    continue;
                }

                result[binding] = BlendShareBoneProxyBindingStatus.Source;
                if (!TryMatchesSourcePose(
                        proxy,
                        binding,
                        sourceMapping,
                        out bool matchesSourcePose))
                {
                    result[binding] |= BlendShareBoneProxyBindingStatus.Unknown;
                }
                else if (!matchesSourcePose)
                {
                    result[binding] |= BlendShareBoneProxyBindingStatus.Modified;
                }
            }

            return result;
        }

        private static bool TryMatchesSourcePose(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyBinding binding,
            ResolvedVertexBinding mapping,
            out bool matches)
        {
            matches = false;
            var bone = BlendShareComponentSetupService.GetSkeleton(proxy)?.Bones.Find(item => item.Path == binding?.SourceBonePath);
            if (bone == null ||
                mapping == null ||
                !mapping.TryConvertLocalTransform(
                    bone.LocalBindMatrix,
                    out var sourceTransform,
                    out _) ||
                !proxy.TryGetCurrentLocalTransform(
                    binding,
                    out var currentPosition,
                    out var currentRotation,
                    out var currentScale))
            {
                return false;
            }

            matches = Vector3.Distance(currentPosition, sourceTransform.Position) <= k_TransformPositionEpsilon &&
                      Quaternion.Angle(Quaternion.Euler(currentRotation), sourceTransform.Rotation) <=
                      k_TransformRotationEpsilon &&
                      Vector3.Distance(currentScale, sourceTransform.Scale) <= k_TransformScaleEpsilon;
            return true;
        }

        private static bool Approximately(Matrix4x4 first, Matrix4x4 second, float epsilon)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(first[row, column] - second[row, column]) > epsilon)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void DrawSectionHeader(string localizationKey)
        {
            EditorGUILayout.LabelField(Localization.S(localizationKey), EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);
        }

        private static void DrawParentingMode(SerializedProperty useHierarchyParentProperty)
        {
            Rect row = EditorGUILayout.GetControlRect();
            Rect buttonsRect = EditorGUI.PrefixLabel(
                row,
                new GUIContent(Localization.S("ndmf.bone_proxy.parenting_mode")));
            float halfWidth = buttonsRect.width * 0.5f;
            var retargetRect = new Rect(
                buttonsRect.x,
                buttonsRect.y,
                halfWidth,
                buttonsRect.height);
            var existingRect = new Rect(
                buttonsRect.x + halfWidth,
                buttonsRect.y,
                buttonsRect.width - halfWidth,
                buttonsRect.height);

            bool useExistingParent = useHierarchyParentProperty.boolValue;
            if (GUI.Toggle(
                    retargetRect,
                    !useExistingParent,
                    Localization.S("ndmf.bone_proxy.parenting_retarget"),
                    EditorStyles.miniButtonLeft))
            {
                useExistingParent = false;
            }
            if (GUI.Toggle(
                    existingRect,
                    useExistingParent,
                    Localization.S("ndmf.bone_proxy.parenting_existing"),
                    EditorStyles.miniButtonRight))
            {
                useExistingParent = true;
            }
            if (useExistingParent != useHierarchyParentProperty.boolValue)
                useHierarchyParentProperty.boolValue = useExistingParent;
        }

        private static void DrawBindingValidation(BlendShareBoneProxy proxy)
        {
            if (proxy == null)
            {
                return;
            }

            var duplicatePaths = proxy.Bindings
                .Where(binding => binding?.IsConfigured == true)
                .GroupBy(binding => binding.SourceBonePath)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicatePaths.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    string.Format(Localization.S("ndmf.bone_proxy.duplicate_bindings"), string.Join(", ", duplicatePaths)),
                    MessageType.Warning);
            }

            var avatarRoot = proxy.transform.root;
            foreach (var binding in proxy.Bindings)
            {
                if (binding == null || binding.Transform == null)
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.null_final_transform"), MessageType.Error);
                    continue;
                }

                if (binding.Transform != avatarRoot && !binding.Transform.IsChildOf(avatarRoot))
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.final_transform_outside_avatar"), MessageType.Error);
                }

                var parent = proxy.GetEffectiveParent(binding);
                if (parent == binding.Transform || (parent != null && parent.IsChildOf(binding.Transform)))
                {
                    EditorGUILayout.HelpBox(Localization.S("ndmf.bone_proxy.retarget_cycle"), MessageType.Error);
                }
            }
        }

        internal static void NotifyPreviewInputChanged(
            BlendShareBoneProxy proxy,
            IEnumerable<BlendShareMesh> changedMeshes = null)
        {
            EditorUtility.SetDirty(proxy);
            BlendShareEditorChangeEvents.NotifyChanged(
                BlendShareEditorChangeKind.Explicit,
                new UnityEngine.Object[] { proxy }
                    .Concat(changedMeshes?.Cast<UnityEngine.Object>() ?? Array.Empty<UnityEngine.Object>())
                    .ToArray());
            SceneView.RepaintAll();
        }

        internal static IReadOnlyList<BlendShareOverriddenBinding> GetOverriddenBindings(
            BlendShareBoneProxy proxy)
        {
            var availableProxies = proxy?.transform.root
                .GetComponentsInChildren<BlendShareBoneProxy>(true);
            var resolution = BlendShareBoneProxyResolver.Resolve(proxy?.Patch, availableProxies);
            return GetOverriddenBindings(proxy, resolution);
        }

        private static IReadOnlyList<BlendShareOverriddenBinding> GetOverriddenBindings(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyResolution resolution)
        {
            var result = new List<BlendShareOverriddenBinding>();
            if (proxy == null ||
                !proxy.isActiveAndEnabled ||
                proxy.Patch == null)
            {
                return result;
            }

            var avatarRoot = proxy.transform.root;
            foreach (var binding in proxy.Bindings
                         .Where(binding => binding?.IsConfigured == true)
                         .GroupBy(binding => binding.SourceBonePath, StringComparer.Ordinal)
                         .Select(group => group.Last()))
            {
                if (resolution == null ||
                    !resolution.TryGetBinding(binding.SourceBonePath, out var winner) ||
                    winner.Proxy == null ||
                    (winner.Proxy == proxy && ReferenceEquals(winner.Binding, binding)))
                {
                    continue;
                }

                string overridingProxyPath = MeshNodePath.Normalize(
                    MeshNodePath.GetRelativePath(winner.Proxy.transform, avatarRoot));
                result.Add(new BlendShareOverriddenBinding(
                    binding.SourceBonePath,
                    overridingProxyPath));
            }

            return result;
        }

        private static ResolvedVertexBinding GetFbxToUnityMapping(BlendShareMesh meshApplier)
        {
            return BlendShareComponentSetupService.TryResolveMeshApplierMappingReference(
                meshApplier,
                out var mapping)
                ? mapping
                : null;
        }

        private static ResolvedVertexBinding GetSourceMapping(BlendShareBoneProxy proxy)
        {
            return proxy?.transform.root
                .GetComponentsInChildren<BlendShareMesh>(true)
                .Where(applier =>
                    applier != null && proxy.Patch != null && ReferenceEquals(applier.ResolvedPatch, proxy.Patch))
                .Select(GetFbxToUnityMapping)
                .FirstOrDefault(candidate => candidate != null);
        }

        internal static bool TryCreateBindingRevertPlan(
            BlendShareBoneProxy proxy,
            out BlendShareBindingRevertPlan plan,
            out string diagnostic, bool allowPayloadRead = false)
        {
            var usages = GetUsingMeshes(proxy).ToArray();
            var mapping = usages
                .Select(usage => BlendShareComponentSetupService.TryResolveMeshApplierMappingReference(
                    usage.Mesh, out var resolved, allowPayloadRead) ? resolved : null)
                .FirstOrDefault(candidate => candidate != null);
            return TryCreateBindingRevertPlan(proxy, usages, mapping, out plan, out diagnostic);
        }

        private static bool TryCreateBindingRevertPlan(
            BlendShareBoneProxy proxy,
            IReadOnlyList<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)> usages,
            ResolvedVertexBinding mapping,
            out BlendShareBindingRevertPlan plan,
            out string diagnostic)
        {
            plan = null;
            diagnostic = null;
            if (proxy == null || proxy.Patch == null || proxy.Bindings.Count == 0)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                return false;
            }

            if (mapping == null)
            {
                diagnostic = Localization.S("ndmf.bone_proxy.revert_mapping_missing");
                return false;
            }

            var steps = new List<BlendShareBindingRevertStep>();
            foreach (var binding in proxy.Bindings)
            {
                if (binding == null ||
                    binding.Transform == null ||
                    !binding.IsConfigured ||
                    proxy.GetEffectiveParent(binding) == null)
                {
                    diagnostic = Localization.S("ndmf.bone_proxy.no_source_binding");
                    return false;
                }

                var sourceBone = BlendShareComponentSetupService.GetSkeleton(proxy)?.Bones.Find(bone => bone.Path == binding.SourceBonePath);
                if (sourceBone == null ||
                    !mapping.TryConvertLocalTransform(
                        sourceBone.LocalBindMatrix,
                        out var localTransform,
                        out _))
                {
                    diagnostic = Localization.SF(
                        "ndmf.bone_proxy.revert_source_unavailable",
                        binding.SourceBonePath);
                    return false;
                }

                steps.Add(new BlendShareBindingRevertStep(
                    binding.Transform,
                    proxy.GetEffectiveParent(binding),
                    localTransform));
            }

            plan = new BlendShareBindingRevertPlan(
                steps.OrderBy(step => GetTransformDepth(step.FinalTransform)).ToArray(),
                usages?.Where(usage => usage.Mesh != null && usage.Binding != null).ToArray() ??
                Array.Empty<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)>());
            return true;
        }

        internal static IReadOnlyList<BlendShareMesh> ApplyBindingRevertPlan(
            BlendShareBindingRevertPlan plan)
        {
            if (plan == null)
            {
                return Array.Empty<BlendShareMesh>();
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Revert BlendShare Binding");
            var changedMeshes = new List<BlendShareMesh>();
            var undoObjects = plan.Steps
                .Select(step => (UnityEngine.Object)step.FinalTransform)
                .Concat(plan.Usages.Select(usage => usage.Mesh))
                .Where(value => value != null)
                .Distinct()
                .ToArray();
            if (undoObjects.Length > 0)
            {
                Undo.RecordObjects(undoObjects, "Revert BlendShare Binding");
            }

            try
            {
                foreach (var step in plan.Steps)
                {
                    var finalTransform = step.FinalTransform;
                    var parent = step.EffectiveParent;
                    var localTransform = step.LocalTransform;
                    if (parent == finalTransform.parent)
                    {
                        localTransform.ApplyTo(finalTransform);
                    }
                    else
                    {
                        var worldScale = Vector3.Scale(parent.lossyScale, localTransform.Scale);
                        finalTransform.SetPositionAndRotation(
                            parent.TransformPoint(localTransform.Position),
                            parent.rotation * localTransform.Rotation);
                        finalTransform.localScale = DivideScale(worldScale, finalTransform.parent);
                    }

                    EditorUtility.SetDirty(finalTransform);
                }

                foreach (var usage in plan.Usages)
                {
                    if (usage.Mesh.RemoveBindPoseOverride(usage.Binding.SourceBonePath))
                    {
                        EditorUtility.SetDirty(usage.Mesh);
                        changedMeshes.Add(usage.Mesh);
                    }
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            return changedMeshes.Distinct().ToArray();
        }

        private static IReadOnlyList<BlendShareMesh> CaptureBindPosesInAllUsingMeshes(BlendShareBoneProxy proxy)
        {
            var changed = new List<BlendShareMesh>();
            foreach (var usage in GetUsingMeshes(proxy))
            {
                Undo.RecordObject(usage.Mesh, "Capture BlendShare Bind Poses");
                if (usage.Mesh.CaptureBindPose(usage.Binding.SourceBonePath))
                {
                    EditorUtility.SetDirty(usage.Mesh);
                    changed.Add(usage.Mesh);
                }
            }
            return changed;
        }

        private static IEnumerable<(BlendShareMesh Mesh, BlendShareBoneProxyBinding Binding)> GetUsingMeshes(
            BlendShareBoneProxy proxy)
        {
            if (proxy == null)
            {
                yield break;
            }

            var avatarRoot = proxy.transform.root;
            var meshes = avatarRoot.GetComponentsInChildren<BlendShareMesh>(true)
                .Where(mesh => mesh != null && mesh.isActiveAndEnabled)
                .ToArray();
            BlendShareComponentSetupService.RefreshBoneProxyCaches(
                meshes,
                avatarRoot,
                avatarRoot.GetComponentsInChildren<BlendShareBoneProxy>(true));
            foreach (var mesh in meshes)
            {
                var skin = BlendShareComponentSetupService.GetSkin(mesh);
                foreach (string sourceBonePath in skin?.BonePaths ?? Array.Empty<string>())
                {
                    if (mesh.TryGetCachedBone(sourceBonePath, out var resolved) &&
                        resolved.IsProxy &&
                        resolved.Proxy == proxy &&
                        resolved.Binding != null)
                    {
                        yield return (mesh, resolved.Binding);
                    }
                }
            }
        }

        private static Vector3 DivideScale(Vector3 worldScale, Transform parent)
        {
            var parentScale = parent != null ? parent.lossyScale : Vector3.one;
            return new Vector3(
                SafeDivide(worldScale.x, parentScale.x),
                SafeDivide(worldScale.y, parentScale.y),
                SafeDivide(worldScale.z, parentScale.z));
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) > 0.000001f ? value / divisor : value;
        }

        private static int GetTransformDepth(Transform transform)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }
            return depth;
        }

    }

    [InitializeOnLoad]
    internal static class BlendShareBoneProxyGizmoDrawer
    {
        private const float k_BoneGizmoLineWidth = 3f;
        private const float k_BoneGizmoJointSize = 0.06f;

        private static readonly Color s_BoneGizmoColor = new(0.2f, 0.75f, 1f, 0.8f);
        private static readonly Color s_SelectedBoneGizmoColor = new(1f, 0.8f, 0.1f, 1f);

        static BlendShareBoneProxyGizmoDrawer()
        {
            SceneView.duringSceneGui -= DrawBoneChainGizmos;
            SceneView.duringSceneGui += DrawBoneChainGizmos;
        }

        private static void DrawBoneChainGizmos(SceneView sceneView)
        {
            var selectedTransform = Selection.activeTransform;
            if (selectedTransform == null ||
                !sceneView.drawGizmos ||
                Event.current.type != EventType.Repaint)
            {
                return;
            }

            Color previousColor = Handles.color;
            var previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                var availableProxies = BlendShareBoneProxyEditorUpdater.Proxies;
                var resolutions = BlendShareBoneProxyEditorUpdater.ResolutionsByProxy;
                var selectedProxy = availableProxies.FirstOrDefault(proxy =>
                    IsProxyOrBindingSelected(proxy, selectedTransform));
                foreach (var proxy in availableProxies)
                {
                    if (proxy == null ||
                        !proxy.isActiveAndEnabled ||
                        !ShouldDrawProxy(proxy, selectedTransform, selectedProxy))
                    {
                        continue;
                    }

                    resolutions.TryGetValue(proxy, out var resolution);
                    DrawBoneChain(proxy, selectedProxy, resolution);
                }
            }
            finally
            {
                Handles.color = previousColor;
                Handles.zTest = previousZTest;
            }
        }

        private static bool IsProxyOrBindingSelected(
            BlendShareBoneProxy proxy,
            Transform selectedTransform)
        {
            return proxy != null &&
                   (proxy.transform == selectedTransform ||
                    proxy.Bindings.Any(binding => binding?.Transform == selectedTransform));
        }

        private static bool ShouldDrawProxy(
            BlendShareBoneProxy proxy,
            Transform selectedTransform,
            BlendShareBoneProxy selectedProxy)
        {
            var proxyTransform = proxy.transform;
            if (selectedProxy != null)
            {
                return proxyTransform.parent == selectedProxy.transform.parent;
            }

            return proxyTransform == selectedTransform ||
                   proxyTransform.IsChildOf(selectedTransform) ||
                   (proxyTransform.parent != null && proxyTransform.parent == selectedTransform.parent);
        }

        private static void DrawBoneChain(
            BlendShareBoneProxy proxy,
            BlendShareBoneProxy selectedProxy,
            BlendShareBoneProxyResolution resolution)
        {
            bool isSelectedProxy = proxy == selectedProxy;
            foreach (var binding in proxy.Bindings)
            {
                if (proxy.Patch != null &&
                    (resolution == null || !resolution.IsEffective(proxy, binding)))
                {
                    continue;
                }

                var bone = proxy.GetFinalTransform(binding);
                if (bone == null)
                {
                    continue;
                }

                Handles.color = isSelectedProxy
                    ? s_SelectedBoneGizmoColor
                    : s_BoneGizmoColor;
                var parent = proxy.GetEffectiveParent(binding);
                if (parent != null && parent != bone)
                {
                    Handles.DrawAAPolyLine(k_BoneGizmoLineWidth, parent.position, bone.position);
                }

                float jointSize = HandleUtility.GetHandleSize(bone.position) * k_BoneGizmoJointSize;
                Handles.SphereHandleCap(
                    0,
                    bone.position,
                    Quaternion.identity,
                    jointSize,
                    EventType.Repaint);
            }
        }

    }

    [InitializeOnLoad]
    internal static class BlendShareBoneProxyEditorUpdater
    {
        private static BlendShareBoneProxy[] s_Proxies = System.Array.Empty<BlendShareBoneProxy>();
        private static IReadOnlyDictionary<BlendShareBoneProxy, BlendShareBoneProxyResolution>
            s_ResolutionsByProxy = new Dictionary<BlendShareBoneProxy, BlendShareBoneProxyResolution>();
        private static readonly IDisposable s_EditorChangeSubscription;

        internal static IReadOnlyList<BlendShareBoneProxy> Proxies => s_Proxies;
        internal static IReadOnlyDictionary<BlendShareBoneProxy, BlendShareBoneProxyResolution>
            ResolutionsByProxy => s_ResolutionsByProxy;

        static BlendShareBoneProxyEditorUpdater()
        {
            EditorApplication.update += UpdateProxies;
            s_EditorChangeSubscription = BlendShareEditorChangeEvents.Subscribe(
                BlendShareEditorChangeKind.Hierarchy | BlendShareEditorChangeKind.Explicit,
                OnEditorChanged,
                typeof(BlendShareBoneProxy));
            EditorApplication.delayCall += RefreshProxies;
        }

        private static void OnEditorChanged(BlendShareEditorChange change)
        {
            RefreshProxies();
        }

        private static void RefreshProxies()
        {
            s_Proxies = Resources.FindObjectsOfTypeAll<BlendShareBoneProxy>()
                .Where(proxy => proxy != null && !EditorUtility.IsPersistent(proxy))
                .ToArray();
            var resolved = new Dictionary<BlendShareBoneProxy, BlendShareBoneProxyResolution>();
            foreach (var group in s_Proxies
                         .Where(proxy => proxy.isActiveAndEnabled && proxy.Patch != null)
                         .GroupBy(proxy => (proxy.transform.root, proxy.Patch)))
            {
                var patchProxies = group.ToArray();
                var resolution = BlendShareBoneProxyResolver.Resolve(
                    group.Key.Patch,
                    patchProxies);
                foreach (var proxy in patchProxies)
                {
                    resolved[proxy] = resolution;
                }
            }
            s_ResolutionsByProxy = resolved;
        }

        private static void UpdateProxies()
        {
            for (int i = 0; i < s_Proxies.Length; i++)
            {
                var proxy = s_Proxies[i];
                if (proxy != null)
                {
                    proxy.SynchronizeParentingTransform();
                }
            }
        }
    }
}
