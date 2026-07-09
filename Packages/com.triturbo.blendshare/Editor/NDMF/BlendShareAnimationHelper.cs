using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.NDMF
{
    internal static class BlendShareAnimationHelper
    {
        private const string BlendShapePropertyPrefix = "blendShape.";
        private static readonly Dictionary<PreviewWeightKey, PreviewWeightSnapshot> PreviewOriginalWeights = new();
        private static readonly HashSet<PreviewWeightKey> SampledWeights = new();
        private static bool wasAnimationMode;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= UpdateAnimationPreview;
            EditorApplication.update += UpdateAnimationPreview;
            AssemblyReloadEvents.beforeAssemblyReload += RestorePreviewWeights;
        }

        public static EditorCurveBinding CreateBlendShapeBinding(string path, string shapeName)
        {
            return BlendShareAnimationWindowBindingPatch.CreateAuthoringBlendShapeBinding(
                path,
                $"{BlendShapePropertyPrefix}{shapeName}");
        }

        public static bool TryGetAnimatedBlendShapeValue(
            BlendShareMesh applier,
            string shapeName,
            out float value,
            out bool recording)
        {
            var context = CreateBlendShapeAnimationContext(applier);
            return context.TryGetAnimatedBlendShapeValue(shapeName, out value, out recording);
        }

        public static BlendShapeAnimationContext CreateBlendShapeAnimationContext(BlendShareMesh applier)
        {
            if (applier == null ||
                !TryGetActiveAnimationWindowState(out var clip, out var root, out float time, out bool recording) ||
                clip == null ||
                root == null ||
                (!AnimationMode.InAnimationMode() && !recording) ||
                !TryGetBlendShareRecordingPath(applier, root, out string path))
            {
                return BlendShapeAnimationContext.Inactive;
            }

            return new BlendShapeAnimationContext(clip, path, time, recording);
        }

        public sealed class BlendShapeAnimationContext
        {
            public static readonly BlendShapeAnimationContext Inactive = new(null, string.Empty, 0f, false);

            private readonly AnimationClip clip;
            private readonly string path;
            private readonly float time;
            private readonly bool recording;
            private readonly Dictionary<string, AnimationCurve> curvesByShapeName = new();

            public BlendShapeAnimationContext(AnimationClip clip, string path, float time, bool recording)
            {
                this.clip = clip;
                this.path = path ?? string.Empty;
                this.time = time;
                this.recording = recording;
            }

            public bool TryGetAnimatedBlendShapeValue(
                string shapeName,
                out float value,
                out bool isRecording)
            {
                value = 0f;
                isRecording = false;
                if (clip == null || string.IsNullOrWhiteSpace(shapeName))
                {
                    return false;
                }

                isRecording = recording;
                if (!curvesByShapeName.TryGetValue(shapeName, out var curve))
                {
                    var binding = CreateBlendShapeBinding(path, shapeName);
                    curve = AnimationUtility.GetEditorCurve(clip, binding);
                    curvesByShapeName[shapeName] = curve;
                }

                if (curve == null)
                {
                    return false;
                }

                value = curve.Evaluate(time);
                return true;
            }
        }

        public static void RecordBlendShapeValue(BlendShareMesh applier, string shapeName, float value)
        {
            if (!TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out float time, out bool recording) ||
                !recording)
            {
                return;
            }

            SetBlendShapeKey(clip, binding, time, value, "Record BlendShape Key");
            AnimationMode.AddPropertyModification(
                binding,
                new PropertyModification
                {
                    target = applier,
                    propertyPath = binding.propertyName,
                    value = value.ToString(CultureInfo.InvariantCulture)
                },
                true);
        }

        public static void AddBlendShapeKey(BlendShareMesh applier, string shapeName, float value)
        {
            if (!TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out float time, out _))
            {
                return;
            }

            SetBlendShapeKey(clip, binding, time, value, "Add BlendShape Key");
        }

        public static void RemoveBlendShapeKey(BlendShareMesh applier, string shapeName)
        {
            if (!TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out float time, out _))
            {
                return;
            }

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
            {
                return;
            }

            for (int i = 0; i < curve.length; i++)
            {
                if (!Mathf.Approximately(curve.keys[i].time, time))
                {
                    continue;
                }

                Undo.RecordObject(clip, "Remove BlendShape Key");
                curve.RemoveKey(i);
                AnimationUtility.SetEditorCurve(clip, binding, curve.length > 0 ? curve : null);
                EditorUtility.SetDirty(clip);
                return;
            }
        }

        public static void RemoveAllBlendShapeKeys(BlendShareMesh applier, string shapeName)
        {
            if (!TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out _, out _))
            {
                return;
            }

            if (AnimationUtility.GetEditorCurve(clip, binding) == null)
            {
                return;
            }

            Undo.RecordObject(clip, "Remove BlendShape Curve");
            AnimationUtility.SetEditorCurve(clip, binding, null);
            EditorUtility.SetDirty(clip);
        }

        public static bool CanEditActiveBlendShapeCurve(BlendShareMesh applier, string shapeName)
        {
            return TryGetActiveBlendShapeBinding(applier, shapeName, out _, out _, out _, out _);
        }

        public static bool IsAnimationPreviewOrRecordingActive()
        {
            bool hasState = TryGetActiveAnimationWindowState(out _, out _, out _, out bool recording);
            return AnimationMode.InAnimationMode() || (hasState && recording);
        }

        public static bool HasBlendShapeCurve(BlendShareMesh applier, string shapeName)
        {
            return TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out _, out _) &&
                   AnimationUtility.GetEditorCurve(clip, binding) != null;
        }

        public static bool HasBlendShapeKeyAtCurrentTime(BlendShareMesh applier, string shapeName)
        {
            if (!TryGetActiveBlendShapeBinding(applier, shapeName, out var clip, out var binding, out float time, out _))
            {
                return false;
            }

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
            {
                return false;
            }

            return curve.keys.Any(key => Mathf.Approximately(key.time, time));
        }

        public static bool TryGetActiveAnimationWindowState(
            out AnimationClip clip,
            out GameObject root,
            out float time,
            out bool recording)
        {
            clip = null;
            root = null;
            time = 0f;
            recording = false;

            var animationWindow = GetAnimationWindow();
            if (animationWindow == null)
            {
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var state = animationWindow.GetType().GetProperty("state", flags)?.GetValue(animationWindow);
            if (state == null)
            {
                return false;
            }

            var stateType = state.GetType();
            clip = stateType.GetProperty("activeAnimationClip", flags)?.GetValue(state) as AnimationClip;
            root = stateType.GetProperty("activeRootGameObject", flags)?.GetValue(state) as GameObject;
            recording = (bool)(stateType.GetProperty("recording", flags)?.GetValue(state) ?? false);
            time = (float)(stateType.GetProperty("currentTime", flags)?.GetValue(state) ?? 0f);
            return true;
        }

        private static void UpdateAnimationPreview()
        {
            if (!IsAnimationPreviewOrRecordingActive() ||
                !TryGetActiveAnimationWindowState(out var clip, out var root, out float time, out _) ||
                clip == null ||
                root == null)
            {
                if (wasAnimationMode)
                {
                    RestorePreviewWeights();
                }

                wasAnimationMode = false;
                return;
            }

            wasAnimationMode = true;
            SampledWeights.Clear();
            bool changed = false;

            foreach (var applier in root.GetComponentsInChildren<BlendShareMesh>(true))
            {
                if (applier == null)
                {
                    continue;
                }

                applier.SyncActiveBlendShapeWeights();
                string path = GetBlendShareBindingPath(applier, root);
                foreach (var weight in applier.BlendShapeWeights)
                {
                    if (weight == null || string.IsNullOrWhiteSpace(weight.ShapeName))
                    {
                        continue;
                    }

                    var binding = CreateBlendShapeBinding(path, weight.ShapeName);
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null)
                    {
                        continue;
                    }

                    var key = new PreviewWeightKey(applier.GetInstanceID(), weight.ShapeName);
                    SampledWeights.Add(key);
                    if (!PreviewOriginalWeights.ContainsKey(key))
                    {
                        PreviewOriginalWeights.Add(
                            key,
                            new PreviewWeightSnapshot(applier, weight.ShapeName, weight.Weight));
                    }

                    float sampledValue = curve.Evaluate(time);
                    if (!Mathf.Approximately(weight.Weight, sampledValue))
                    {
                        weight.Weight = sampledValue;
                        changed = true;
                    }
                }
            }

            foreach (var key in PreviewOriginalWeights.Keys.ToArray())
            {
                if (SampledWeights.Contains(key))
                {
                    continue;
                }

                changed |= RestorePreviewWeight(key);
                PreviewOriginalWeights.Remove(key);
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        private static void RestorePreviewWeights()
        {
            bool changed = false;
            foreach (var key in PreviewOriginalWeights.Keys.ToArray())
            {
                changed |= RestorePreviewWeight(key);
            }

            PreviewOriginalWeights.Clear();
            SampledWeights.Clear();
            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        private static bool RestorePreviewWeight(PreviewWeightKey key)
        {
            if (!PreviewOriginalWeights.TryGetValue(key, out var snapshot) ||
                snapshot.Applier == null)
            {
                return false;
            }

            var weight = FindBlendShapeWeight(snapshot.Applier, snapshot.ShapeName);
            if (weight == null ||
                Mathf.Approximately(weight.Weight, snapshot.Weight))
            {
                return false;
            }

            weight.Weight = snapshot.Weight;
            return true;
        }

        private static void SetBlendShapeKey(
            AnimationClip clip,
            EditorCurveBinding binding,
            float time,
            float value,
            string undoName)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve();
            for (int i = 0; i < curve.length; i++)
            {
                if (!Mathf.Approximately(curve.keys[i].time, time))
                {
                    continue;
                }

                Undo.RecordObject(clip, undoName);
                curve.MoveKey(i, new Keyframe(time, value));
                SetKeyClampedAuto(curve, i);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                EditorUtility.SetDirty(clip);
                return;
            }

            Undo.RecordObject(clip, undoName);
            int keyIndex = curve.AddKey(time, value);
            SetKeyClampedAuto(curve, keyIndex);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
        }

        private static void SetKeyClampedAuto(AnimationCurve curve, int keyIndex)
        {
            if (curve == null || keyIndex < 0 || keyIndex >= curve.length)
            {
                return;
            }

            AnimationUtility.SetKeyLeftTangentMode(curve, keyIndex, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, keyIndex, AnimationUtility.TangentMode.ClampedAuto);
        }

        private static bool TryGetActiveBlendShapeBinding(
            BlendShareMesh applier,
            string shapeName,
            out AnimationClip clip,
            out EditorCurveBinding binding,
            out float time,
            out bool recording)
        {
            binding = default;
            if (applier == null ||
                string.IsNullOrWhiteSpace(shapeName) ||
                !TryGetActiveAnimationWindowState(out clip, out var root, out time, out recording) ||
                clip == null ||
                root == null ||
                !TryGetBlendShareRecordingPath(applier, root, out string path))
            {
                clip = null;
                time = 0f;
                recording = false;
                return false;
            }

            binding = CreateBlendShapeBinding(path, shapeName);
            return true;
        }

        private static bool TryGetBlendShareRecordingPath(
            BlendShareMesh applier,
            GameObject root,
            out string path)
        {
            path = string.Empty;
            if (applier == null)
            {
                return false;
            }

            if (root == null)
            {
                return true;
            }

            if (!IsSameOrChildOf(applier.transform, root.transform))
            {
                return false;
            }

            path = GetBlendShareBindingPath(applier, root);
            return true;
        }

        private static string GetBlendShareBindingPath(BlendShareMesh applier, GameObject root)
        {
            if (applier == null ||
                root == null ||
                applier.gameObject == root)
            {
                return string.Empty;
            }

            return AnimationUtility.CalculateTransformPath(applier.transform, root.transform);
        }

        private static bool IsSameOrChildOf(Transform targetTransform, Transform rootTransform)
        {
            return targetTransform != null &&
                   rootTransform != null &&
                   (targetTransform == rootTransform || targetTransform.IsChildOf(rootTransform));
        }

        private static BlendShareProxyBlendShapeWeight FindBlendShapeWeight(
            BlendShareMesh applier,
            string shapeName)
        {
            return applier?.BlendShapeWeights.FirstOrDefault(weight =>
                weight != null &&
                weight.ShapeName == shapeName);
        }

        private static EditorWindow GetAnimationWindow()
        {
            var animationWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnimationWindow");
            if (animationWindowType == null)
            {
                return null;
            }

            var focusedWindow = EditorWindow.focusedWindow;
            if (focusedWindow != null && animationWindowType.IsInstanceOfType(focusedWindow))
            {
                return focusedWindow;
            }

            var windows = Resources.FindObjectsOfTypeAll(animationWindowType);
            return windows.Length > 0 ? windows[0] as EditorWindow : null;
        }

        private readonly struct PreviewWeightKey : System.IEquatable<PreviewWeightKey>
        {
            public PreviewWeightKey(int applierId, string shapeName)
            {
                ApplierId = applierId;
                ShapeName = shapeName ?? string.Empty;
            }

            private int ApplierId { get; }
            private string ShapeName { get; }

            public bool Equals(PreviewWeightKey other)
            {
                return ApplierId == other.ApplierId &&
                       ShapeName == other.ShapeName;
            }

            public override bool Equals(object obj)
            {
                return obj is PreviewWeightKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ApplierId * 397) ^ ShapeName.GetHashCode();
                }
            }
        }

        private readonly struct PreviewWeightSnapshot
        {
            public PreviewWeightSnapshot(
                BlendShareMesh applier,
                string shapeName,
                float weight)
            {
                Applier = applier;
                ShapeName = shapeName;
                Weight = weight;
            }

            public BlendShareMesh Applier { get; }
            public string ShapeName { get; }
            public float Weight { get; }
        }
    }
}
