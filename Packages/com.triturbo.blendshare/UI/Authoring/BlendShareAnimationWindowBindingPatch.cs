using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Triturbo.BlendShare.Components;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Authoring
{
    internal static class BlendShareAnimationWindowBindingPatch
    {
        private const string k_HarmonyId = "com.triturbo.blendshare.animation-window-bindings";
        private const string k_RendererBlendShapePropertyPrefix = "blendShape.";
        private static FieldInfo s_NodeCurvesField;

        internal static EditorCurveBinding CreateAuthoringBlendShapeBinding(string path, string propertyName)
        {
            return EditorCurveBinding.FloatCurve(path, typeof(BlendShareMesh), propertyName);
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            var harmonyType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("HarmonyLib.Harmony")).FirstOrDefault(type => type != null);
            var methodType = harmonyType?.Assembly.GetType("HarmonyLib.HarmonyMethod");
            if (methodType == null) return;
            if (!TryResolvePatchTargets(out var targets, out string diagnostic))
            {
                Debug.LogWarning($"[BlendShare] AnimationWindow integration is disabled: {diagnostic}");
                return;
            }

            object harmony = null;
            bool installedPostfix = false;
            try
            {
                harmony = Activator.CreateInstance(harmonyType, k_HarmonyId);
                PatchPostfix(harmony, methodType, targets.GetAnimatableBindings, nameof(GetAnimatableBindingsPostfix));
                installedPostfix = true;
                PatchPostfix(harmony, methodType, targets.GetAnimatedObject, nameof(GetAnimatedObjectPostfix));
                PatchPostfix(harmony, methodType, targets.GetFloatValue, nameof(GetFloatValuePostfix));
                PatchPostfix(harmony, methodType, targets.GetCurrentValue, nameof(GetCurrentValuePostfix));
                PatchPostfix(harmony, methodType, targets.PropertyIsAnimatable, nameof(PropertyIsAnimatablePostfix));
                PatchPostfix(harmony, methodType, targets.IsNodeLeftOverCurve, nameof(IsNodeLeftOverCurvePostfix));
                PatchPostfix(harmony, methodType, targets.IsNodePhantom, nameof(IsNodePhantomPostfix));

                s_NodeCurvesField = targets.NodeCurvesField;
                AssemblyReloadEvents.beforeAssemblyReload += () => Unpatch(harmony);
            }
            catch (System.Exception exception)
            {
                if (installedPostfix) Unpatch(harmony);
                s_NodeCurvesField = null;
                Debug.LogWarning($"[BlendShare] AnimationWindow integration is disabled: {exception.GetBaseException().Message}");
            }
        }

        private static void Unpatch(object harmony)
        {
            try
            {
                harmony.GetType().GetMethod("UnpatchAll", new[] { typeof(string) })
                    ?.Invoke(harmony, new object[] { k_HarmonyId });
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BlendShare] Could not remove AnimationWindow postfixes: {exception.GetBaseException().Message}");
            }
        }

        private const BindingFlags k_Members = BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static;

        private static void PatchPostfix(object harmony, Type methodType, MethodInfo target, string postfixName)
        {
            var patch = harmony.GetType().GetMethods().Single(method => method.Name == "Patch" &&
                method.GetParameters().Any(parameter => parameter.Name == "postfix" && parameter.ParameterType == methodType));
            var parameters = patch.GetParameters();
            var args = new object[parameters.Length];
            args[0] = target;
            var postfix = typeof(BlendShareAnimationWindowBindingPatch).GetMethod(postfixName, k_Members);
            args[Array.FindIndex(parameters, parameter => parameter.Name == "postfix")] =
                Activator.CreateInstance(methodType, postfix);
            patch.Invoke(harmony, args);
        }

        private static MethodInfo FindMethod(Type type, string name, Type[] parameters = null) =>
            parameters == null ? type.GetMethod(name, k_Members) : type.GetMethod(name, k_Members, null, parameters, null);

        private static bool TryResolvePatchTargets(out PatchTargets targets, out string diagnostic)
        {
            targets = null;
            diagnostic = null;

            try
            {
                var editorAssembly = typeof(EditorWindow).Assembly;
                var nodeType = editorAssembly.GetType("UnityEditorInternal.AnimationWindowHierarchyNode");
                var animationWindowUtility = editorAssembly.GetType("UnityEditorInternal.AnimationWindowUtility");
                if (nodeType == null)
                {
                    diagnostic = "UnityEditorInternal.AnimationWindowHierarchyNode was not found.";
                    return false;
                }

                if (animationWindowUtility == null)
                {
                    diagnostic = "UnityEditorInternal.AnimationWindowUtility was not found.";
                    return false;
                }

                targets = new PatchTargets
                {
                    NodeCurvesField = nodeType.GetField("curves", k_Members),
                    GetAnimatableBindings = FindMethod(
                        typeof(AnimationUtility),
                        nameof(AnimationUtility.GetAnimatableBindings),
                        new[] { typeof(GameObject), typeof(GameObject) }),
                    GetAnimatedObject = FindMethod(
                        typeof(AnimationUtility),
                        nameof(AnimationUtility.GetAnimatedObject),
                        new[] { typeof(GameObject), typeof(EditorCurveBinding) }),
                    GetFloatValue = FindMethod(
                        typeof(AnimationUtility),
                        nameof(AnimationUtility.GetFloatValue),
                        new[] { typeof(GameObject), typeof(EditorCurveBinding), typeof(float).MakeByRefType() }),
                    GetCurrentValue = FindMethod(
                        animationWindowUtility,
                        "GetCurrentValue",
                        new[] { typeof(GameObject), typeof(EditorCurveBinding) }),
                    PropertyIsAnimatable = FindMethod(
                        animationWindowUtility,
                        "PropertyIsAnimatable",
                        new[] { typeof(UnityEngine.Object), typeof(string), typeof(UnityEngine.Object) }),
                    IsNodeLeftOverCurve = FindMethod(animationWindowUtility, "IsNodeLeftOverCurve"),
                    IsNodePhantom = FindMethod(animationWindowUtility, "IsNodePhantom")
                };

                return targets.IsComplete(out diagnostic);
            }
            catch (System.Exception exception)
            {
                targets = null;
                diagnostic = exception.Message;
                return false;
            }
        }

        private sealed class PatchTargets
        {
            public FieldInfo NodeCurvesField;
            public MethodInfo GetAnimatableBindings;
            public MethodInfo GetAnimatedObject;
            public MethodInfo GetFloatValue;
            public MethodInfo GetCurrentValue;
            public MethodInfo PropertyIsAnimatable;
            public MethodInfo IsNodeLeftOverCurve;
            public MethodInfo IsNodePhantom;

            public bool IsComplete(out string diagnostic)
            {
                diagnostic = null;
                if (NodeCurvesField == null) diagnostic = "AnimationWindowHierarchyNode.curves was not found.";
                else if (GetAnimatableBindings == null) diagnostic = "AnimationUtility.GetAnimatableBindings was not found.";
                else if (GetAnimatedObject == null) diagnostic = "AnimationUtility.GetAnimatedObject was not found.";
                else if (GetFloatValue == null) diagnostic = "AnimationUtility.GetFloatValue was not found.";
                else if (GetCurrentValue == null) diagnostic = "AnimationWindowUtility.GetCurrentValue was not found.";
                else if (PropertyIsAnimatable == null) diagnostic = "AnimationWindowUtility.PropertyIsAnimatable was not found.";
                else if (IsNodeLeftOverCurve == null) diagnostic = "AnimationWindowUtility.IsNodeLeftOverCurve was not found.";
                else if (IsNodePhantom == null) diagnostic = "AnimationWindowUtility.IsNodePhantom was not found.";

                return diagnostic == null;
            }
        }

        // Populate AnimationWindow's "Add Property" menu.
        // BlendShare placeholder curves use BlendShareMesh/blendShape.*
        private static void GetAnimatableBindingsPostfix(
            GameObject targetObject,
            GameObject root,
            ref EditorCurveBinding[] __result)
        {
            if (targetObject == null)
            {
                return;
            }

            var appliers = targetObject.GetComponents<BlendShareMesh>();
            if (appliers.Length == 0)
            {
                return;
            }

            var bindings = __result != null
                ? new List<EditorCurveBinding>(__result)
                : new List<EditorCurveBinding>();
            var existing = new HashSet<string>(bindings.Select(GetBindingKey));

            foreach (var applier in appliers)
            {
                if (applier == null)
                {
                    continue;
                }

                string path = GetBindingPath(applier.gameObject, root);
                foreach (var weight in applier.BlendShapeWeights)
                {
                    if (weight == null || string.IsNullOrWhiteSpace(weight.ShapeName))
                    {
                        continue;
                    }

                    var binding = CreateAuthoringBlendShapeBinding(
                        path,
                        $"{k_RendererBlendShapePropertyPrefix}{weight.ShapeName}");
                    if (existing.Add(GetBindingKey(binding)))
                    {
                        bindings.Add(binding);
                    }
                }
            }

            __result = bindings.ToArray();
        }

        // Resolve BlendShare placeholder curves to the real component
        // that owns the named blendshape weight.
        private static void GetAnimatedObjectPostfix(
            GameObject root,
            EditorCurveBinding binding,
            ref UnityEngine.Object __result)
        {
            if (__result != null || root == null || !IsBlendShareBlendShapeBinding(binding))
            {
                return;
            }

            var target = ResolveBindingGameObject(root, binding.path);
            if (target == null)
            {
                return;
            }

            foreach (var applier in target.GetComponents<BlendShareMesh>())
            {
                if (applier == null)
                {
                    continue;
                }

                if (applier.BlendShapeWeights.Any(weight =>
                        weight != null &&
                        binding.propertyName == $"{k_RendererBlendShapePropertyPrefix}{weight.ShapeName}"))
                {
                    __result = applier;
                    return;
                }
            }
        }

        // Feed AnimationWindow's numeric field.
        // Prefer the active clip's evaluated curve value; falling back to the stored
        // BlendShare weight causes a visible one-frame/default-value lag while scrubbing.
        private static void GetFloatValuePostfix(
            GameObject root,
            EditorCurveBinding binding,
            ref float data,
            ref bool __result)
        {
            if (!TryGetBlendShareBlendShapeValue(root, binding, out float weight))
            {
                return;
            }

            data = weight;
            __result = true;
        }

        // Feed AnimationWindow's internal row/value drawing.
        // This mirrors GetFloatValuePostfix for the internal API path used by the
        // hierarchy and dope sheet.
        private static void GetCurrentValuePostfix(
            GameObject rootGameObject,
            EditorCurveBinding curveBinding,
            ref object __result)
        {
            if (!TryGetBlendShareBlendShapeValue(rootGameObject, curveBinding, out float weight))
            {
                return;
            }

            __result = weight;
        }

        // Tell AnimationWindow that virtual blendShape.* properties are valid.
        // Unity's default check only accepts real serialized properties/components.
        private static void PropertyIsAnimatablePostfix(
            UnityEngine.Object targetObject,
            string propertyPath,
            ref bool __result)
        {
            if (__result ||
                targetObject is not BlendShareMesh applier ||
                !IsBlendShapeProperty(propertyPath))
            {
                return;
            }

            __result = TryGetBlendShareBlendShapeWeight(applier, propertyPath, out _);
        }

        // Suppress the "GameObject or Component is missing" styling for
        // BlendShare virtual curves. Unity calls these "leftover" curves when their
        // target cannot be found through normal serialized-property lookup.
        private static void IsNodeLeftOverCurvePostfix(object node, ref bool __result)
        {
            if (__result && IsBlendShareBlendShapeNode(node))
            {
                __result = false;
            }
        }

        // Keep BlendShare virtual curves out of Unity's phantom styling path.
        // Phantom rows are also presented as invalid/missing in the AnimationWindow UI.
        private static void IsNodePhantomPostfix(object node, ref bool __result)
        {
            if (__result && IsBlendShareBlendShapeNode(node))
            {
                __result = false;
            }
        }

        private static string GetBindingPath(GameObject targetObject, GameObject root)
        {
            if (targetObject == null ||
                root == null ||
                targetObject == root ||
                !targetObject.transform.IsChildOf(root.transform))
            {
                return string.Empty;
            }

            return AnimationUtility.CalculateTransformPath(targetObject.transform, root.transform);
        }

        private static string GetBindingKey(EditorCurveBinding binding)
        {
            return $"{binding.path}\n{binding.type?.AssemblyQualifiedName}\n{binding.propertyName}";
        }

        private static bool IsBlendShareBlendShapeBinding(EditorCurveBinding binding)
        {
            return binding.type == typeof(BlendShareMesh) &&
                   IsBlendShapeProperty(binding.propertyName);
        }

        private static GameObject ResolveBindingGameObject(GameObject root, string path)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(path))
            {
                return root;
            }

            var transform = root.transform.Find(path);
            return transform != null ? transform.gameObject : null;
        }

        private static bool IsBlendShapeProperty(string propertyName)
        {
            return propertyName != null &&
                   propertyName.StartsWith(k_RendererBlendShapePropertyPrefix);
        }

        private static bool TryGetBlendShareBlendShapeWeight(
            GameObject root,
            EditorCurveBinding binding,
            out float weight)
        {
            weight = 0f;
            if (root == null || !IsBlendShareBlendShapeBinding(binding))
            {
                return false;
            }

            var target = ResolveBindingGameObject(root, binding.path);
            if (target == null)
            {
                return false;
            }

            foreach (var applier in target.GetComponents<BlendShareMesh>())
            {
                if (TryGetBlendShareBlendShapeWeight(applier, binding.propertyName, out weight))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetBlendShareBlendShapeValue(
            GameObject root,
            EditorCurveBinding binding,
            out float weight)
        {
            if (TryEvaluateActiveAnimationWindowCurve(binding, out weight))
            {
                return true;
            }

            return TryGetBlendShareBlendShapeWeight(root, binding, out weight);
        }

        private static bool TryEvaluateActiveAnimationWindowCurve(
            EditorCurveBinding binding,
            out float value)
        {
            value = 0f;
            if (!IsBlendShareBlendShapeBinding(binding) ||
                !TryGetActiveAnimationWindowState(out var clip, out float time) ||
                clip == null)
            {
                return false;
            }

            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
            {
                return false;
            }

            value = curve.Evaluate(time);
            return true;
        }

        private static bool TryGetActiveAnimationWindowState(out AnimationClip clip, out float time)
        {
            clip = null;
            time = 0f;

            var animationWindow = GetAnimationWindow();
            if (animationWindow == null)
            {
                return false;
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var state = animationWindow.GetType().GetProperty("state", flags)?.GetValue(animationWindow);
                if (state == null)
                {
                    return false;
                }

                var stateType = state.GetType();
                var clipProperty = stateType.GetProperty("activeAnimationClip", flags);
                var timeProperty = stateType.GetProperty("currentTime", flags);
                if (clipProperty == null || timeProperty == null)
                {
                    return false;
                }

                clip = clipProperty.GetValue(state) as AnimationClip;
                time = (float)(timeProperty.GetValue(state) ?? 0f);
                return true;
            }
            catch (System.Exception)
            {
                clip = null;
                time = 0f;
                return false;
            }
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

        private static bool TryGetBlendShareBlendShapeWeight(
            BlendShareMesh applier,
            string propertyName,
            out float weight)
        {
            weight = 0f;
            if (applier == null || !IsBlendShapeProperty(propertyName))
            {
                return false;
            }

            string shapeName = propertyName.Substring(k_RendererBlendShapePropertyPrefix.Length);
            return applier.TryGetBlendShapeWeight(shapeName, out weight);
        }

        private static bool IsBlendShareBlendShapeNode(object node)
        {
            if (node == null)
            {
                return false;
            }

            foreach (var curve in GetNodeCurves(node))
            {
                if (TryGetCurveBinding(curve, out var binding) &&
                    TryGetCurveRootGameObject(curve, out var root) &&
                    TryGetBlendShareBlendShapeWeight(root, binding, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<object> GetNodeCurves(object node)
        {
            System.Array curves;
            try
            {
                curves = s_NodeCurvesField?.GetValue(node) as System.Array;
            }
            catch (System.Exception)
            {
                yield break;
            }

            if (curves == null)
            {
                yield break;
            }

            foreach (var curve in curves)
            {
                if (curve != null)
                {
                    yield return curve;
                }
            }
        }

        private static bool TryGetCurveBinding(object curve, out EditorCurveBinding binding)
        {
            binding = default;
            try
            {
                var bindingProperty = curve != null ? curve.GetType().GetProperty("binding", k_Members) : null;
                var value = bindingProperty?.GetValue(curve);
                if (value is not EditorCurveBinding curveBinding)
                {
                    return false;
                }

                binding = curveBinding;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static bool TryGetCurveRootGameObject(object curve, out GameObject root)
        {
            root = null;
            try
            {
                var rootProperty = curve != null ? curve.GetType().GetProperty("rootGameObject", k_Members) : null;
                var value = rootProperty?.GetValue(curve);
                if (value is not GameObject rootGameObject)
                {
                    return false;
                }

                root = rootGameObject;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
