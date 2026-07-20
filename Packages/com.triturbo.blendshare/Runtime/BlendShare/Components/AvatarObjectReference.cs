// Derived from Modular Avatar (https://github.com/bdunderscore/modular-avatar)
// Original work Copyright (c) 2022 bd_
// Distributed under the MIT License; see THIRD_PARTY_NOTICES.md

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

#if UNITY_EDITOR
using Triturbo.BlendShare.Editor;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif
#endif

namespace Triturbo.BlendShare.Components
{
    public static class AvatarHierarchyUtil
    {
        private static readonly string[] s_AvatarRootTypeNames =
        {
            "nadena.dev.ndmf.runtime.components.NDMFAvatarRoot, nadena.dev.ndmf.runtime",
            "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRC.SDK3A",
        };

        private static Type[] s_AvatarRootTypes;

        public static string RelativePath(GameObject root, GameObject child)
        {
            return RelativePath(
                root != null ? root.transform : null,
                child != null ? child.transform : null
            );
        }

        public static string RelativePath(Transform root, Transform child)
        {
            if (root == child)
            {
                return string.Empty;
            }

            List<string> pathSegments = new List<string>();
            while (child != root && child != null)
            {
                pathSegments.Add(child.gameObject.name);
                child = child.parent;
            }

            if (child == null && root != null)
            {
                return null;
            }

            pathSegments.Reverse();
            return string.Join("/", pathSegments);
        }

        public static string AvatarRootPath(GameObject child)
        {
            if (child == null)
            {
                return null;
            }

            Transform avatarRoot = FindAvatarInParents(child.transform);
            if (avatarRoot == null)
            {
                return null;
            }

            return RelativePath(avatarRoot.gameObject, child);
        }

        public static bool IsAvatarRoot(Transform target)
        {
            return target != null &&
                   TryGetAvatarRootComponent(target, out _) &&
                   GetAvatarRootInThisAndParents(target.parent) == null;
        }

        public static Transform FindAvatarInParents(Transform target)
        {
            Component avatarRoot = GetAvatarRootInThisAndParents(target);
            return avatarRoot != null ? avatarRoot.transform : null;
        }

        private static Component GetAvatarRootInThisAndParents(Transform target)
        {
            Component candidate = null;
            while (target != null)
            {
                if (TryGetAvatarRootComponent(target, out Component component))
                {
                    candidate = component;
                }

                target = target.parent;
            }

            return candidate;
        }

        private static bool TryGetAvatarRootComponent(Transform target, out Component component)
        {
            foreach (Type rootType in GetAvatarRootTypes())
            {
                if (target.TryGetComponent(rootType, out component))
                {
                    return true;
                }
            }

            component = null;
            return false;
        }

        private static Type[] GetAvatarRootTypes()
        {
            if (s_AvatarRootTypes != null)
            {
                return s_AvatarRootTypes;
            }

            List<Type> resolvedTypes = new List<Type>(s_AvatarRootTypeNames.Length);
            foreach (string typeName in s_AvatarRootTypeNames)
            {
                Type resolvedType = ResolveType(typeName);
                if (resolvedType != null)
                {
                    resolvedTypes.Add(resolvedType);
                }
            }

            s_AvatarRootTypes = resolvedTypes.ToArray();
            return s_AvatarRootTypes;
        }

        private static Type ResolveType(string typeName)
        {
            Type resolvedType = Type.GetType(typeName, false);
            if (resolvedType != null)
            {
                return resolvedType;
            }

            int assemblySeparatorIndex = typeName.IndexOf(',');
            string fullTypeName = assemblySeparatorIndex >= 0
                ? typeName.Substring(0, assemblySeparatorIndex)
                : typeName;

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resolvedType = assembly.GetType(fullTypeName, false);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            return null;
        }
    }

    [Serializable]
    public class AvatarObjectReference
    {
        private static long s_HierarchyChangedSequence = long.MinValue;
#if UNITY_EDITOR
        private static IDisposable s_EditorChangeSubscription;
#endif

        public const string AvatarRoot = "$$$AVATAR_ROOT$$$";

        public string referencePath;

#if UNITY_EDITOR
        public readonly struct InspectorState
        {
            public Object CurrentObject { get; }
            public bool ShowMissingPath { get; }

            public InspectorState(Object currentObject, bool showMissingPath)
            {
                CurrentObject = currentObject;
                ShowMissingPath = showMissingPath;
            }
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            s_EditorChangeSubscription?.Dispose();
            s_EditorChangeSubscription = BlendShareEditorChangeEvents.Subscribe(
                BlendShareEditorChangeKind.Hierarchy,
                OnEditorChanged);
        }

        private static void OnEditorChanged(BlendShareEditorChange change)
        {
            if ((change.Kinds & BlendShareEditorChangeKind.Hierarchy) != 0)
            {
                s_HierarchyChangedSequence++;
            }
        }

        public static GameObject Get(SerializedProperty property)
        {
            if (property?.serializedObject == null)
            {
                return null;
            }

            UnityEngine.Object rootObject = property.serializedObject.targetObject;
            Transform hostTransform =
                (rootObject as Component)?.transform ??
                (rootObject as GameObject)?.transform;

            Transform avatarRoot = AvatarHierarchyUtil.FindAvatarInParents(hostTransform);
            if (avatarRoot == null)
            {
                return null;
            }

            SerializedProperty referencePathProperty = property.FindPropertyRelative("referencePath");
            SerializedProperty targetObjectProperty = property.FindPropertyRelative("targetObject");
            string path = referencePathProperty != null ? referencePathProperty.stringValue : string.Empty;
            Component directTarget = targetObjectProperty != null
                ? targetObjectProperty.objectReferenceValue as Component
                : null;

            if (IsValidTarget(directTarget, avatarRoot))
            {
                return directTarget.gameObject;
            }

            Transform resolvedTransform = ResolveReferenceTransform(avatarRoot, path);
            if (resolvedTransform != null)
            {
                return resolvedTransform.gameObject;
            }

            return null;
        }

        public static InspectorState GetInspectorState(
            SerializedProperty property,
            Type acceptedType,
            bool allowPathRepair)
        {
            if (property?.serializedObject == null || acceptedType == null)
            {
                return default;
            }

            SerializedProperty referencePathProperty = property.FindPropertyRelative("referencePath");
            SerializedProperty targetObjectProperty = property.FindPropertyRelative("targetObject");
            if (referencePathProperty == null || targetObjectProperty == null)
            {
                return default;
            }

            Transform avatarRoot = FindContainingAvatarTransform(property);
            Object currentObject = ResolveInspectorObject(
                avatarRoot,
                referencePathProperty,
                targetObjectProperty,
                acceptedType,
                allowPathRepair
            );

            return new InspectorState(
                currentObject,
                currentObject == null && !string.IsNullOrEmpty(referencePathProperty.stringValue)
            );
        }

        public static Transform FindContainingAvatarTransform(SerializedProperty property)
        {
            if (property?.serializedObject == null)
            {
                return null;
            }

            Transform sharedAvatarRoot = null;
            foreach (Object target in property.serializedObject.targetObjects)
            {
                Transform targetTransform =
                    (target as Component)?.transform ??
                    (target as GameObject)?.transform;

                Transform avatarRoot = AvatarHierarchyUtil.FindAvatarInParents(targetTransform);
                if (avatarRoot == null)
                {
                    return null;
                }

                if (sharedAvatarRoot == null)
                {
                    sharedAvatarRoot = avatarRoot;
                    continue;
                }

                if (sharedAvatarRoot != avatarRoot)
                {
                    return null;
                }
            }

            return sharedAvatarRoot;
        }

        public static bool IsInPrefabAsset(SerializedProperty property)
        {
            if (property?.serializedObject == null)
            {
                return false;
            }

            foreach (Object target in property.serializedObject.targetObjects)
            {
                GameObject gameObject =
                    (target as Component)?.gameObject ??
                    target as GameObject;

                if (gameObject == null || !PrefabUtility.IsPartOfPrefabAsset(gameObject))
                {
                    return false;
                }
            }

            return property.serializedObject.targetObjects.Length > 0;
        }

        public static bool IsInPrefabMode(SerializedProperty property)
        {
            if (property?.serializedObject == null)
            {
                return false;
            }

            PrefabStage currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            foreach (Object target in property.serializedObject.targetObjects)
            {
                GameObject gameObject =
                    (target as Component)?.gameObject ??
                    target as GameObject;

                if (gameObject == null)
                {
                    return false;
                }

                if (PrefabUtility.IsPartOfPrefabAsset(gameObject))
                {
                    continue;
                }

                if (currentPrefabStage != null && PrefabStageUtility.GetPrefabStage(gameObject) == currentPrefabStage)
                {
                    continue;
                }

                return false;
            }

            return property.serializedObject.targetObjects.Length > 0;
        }

        private static Object ResolveInspectorObject(
            Transform avatarRoot,
            SerializedProperty referencePathProperty,
            SerializedProperty targetObjectProperty,
            Type acceptedType,
            bool allowPathRepair)
        {
            string referencePath = referencePathProperty?.stringValue ?? string.Empty;
            Object pathTarget = ResolvePathTarget(avatarRoot, referencePath, acceptedType);
            Object directTarget = GetTypedTarget(targetObjectProperty, acceptedType);
            bool directTargetValid = IsTargetUnderAvatarRoot(directTarget, avatarRoot);

            if (directTargetValid)
            {
                if (allowPathRepair)
                {
                    string normalizedPath = GetRelativePath(avatarRoot, directTarget);
                    if (!string.Equals(referencePath, normalizedPath, StringComparison.Ordinal))
                    {
                        referencePathProperty.stringValue = normalizedPath;
                    }
                }

                return directTarget;
            }

            if (pathTarget != null)
            {
                targetObjectProperty.objectReferenceValue = pathTarget;
                return pathTarget;
            }

            if (!string.IsNullOrEmpty(referencePath))
            {
                return null;
            }

            return directTarget;
        }

        private static Object ResolvePathTarget(Transform avatarRoot, string referencePath, Type acceptedType)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(referencePath))
            {
                return null;
            }

            Transform targetTransform = referencePath == AvatarRoot
                ? avatarRoot
                : avatarRoot.Find(referencePath);

            if (targetTransform == null)
            {
                return null;
            }

            if (acceptedType == typeof(GameObject))
            {
                return targetTransform.gameObject;
            }

            return targetTransform.GetComponent(acceptedType);
        }

        private static Object GetTypedTarget(SerializedProperty targetObjectProperty, Type acceptedType)
        {
            Object directTarget = targetObjectProperty?.objectReferenceValue;
            return directTarget != null && acceptedType.IsInstanceOfType(directTarget)
                ? directTarget
                : null;
        }
#endif

        protected static long HierarchyChangedSequence => s_HierarchyChangedSequence;

        protected static Transform ResolveReferenceTransform(Transform avatarRoot, string path)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            return path == AvatarRoot
                ? avatarRoot
                : avatarRoot.Find(path);
        }

        protected static string GetReferencePath(GameObject target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            if (AvatarHierarchyUtil.IsAvatarRoot(target.transform))
            {
                return AvatarRoot;
            }

            return AvatarHierarchyUtil.AvatarRootPath(target) ?? string.Empty;
        }

        protected static bool IsValidTarget(Component candidate, Transform avatarRoot)
        {
            return candidate != null &&
                   avatarRoot != null &&
                   (candidate.transform == avatarRoot || candidate.transform.IsChildOf(avatarRoot));
        }

#if UNITY_EDITOR
        private static bool IsTargetUnderAvatarRoot(Object targetObject, Transform avatarRoot)
        {
            if (targetObject == null || avatarRoot == null)
            {
                return false;
            }

            GameObject targetGameObject = ExtractGameObject(targetObject);
            return targetGameObject != null &&
                   (targetGameObject.transform == avatarRoot || targetGameObject.transform.IsChildOf(avatarRoot));
        }

        private static string GetRelativePath(Transform avatarRoot, Object targetObject)
        {
            GameObject targetGameObject = ExtractGameObject(targetObject);
            if (avatarRoot == null || targetGameObject == null)
            {
                return string.Empty;
            }

            return targetGameObject.transform == avatarRoot
                ? AvatarRoot
                : AvatarHierarchyUtil.RelativePath(avatarRoot, targetGameObject.transform) ?? string.Empty;
        }

        private static GameObject ExtractGameObject(Object targetObject)
        {
            if (targetObject is GameObject gameObject)
            {
                return gameObject;
            }

            if (targetObject is Component component)
            {
                return component.gameObject;
            }

            return null;
        }
#endif
    }

    [Serializable]
    public class AvatarObjectReference<T> : AvatarObjectReference where T : Component
    {
        [SerializeField] internal T targetObject;

        private long _cachedSequence = long.MinValue;
        private bool _hasCachedResult;
        private string _cachedPath;
        private T _cachedSourceTarget;
        private T _cachedResolvedTarget;

        public AvatarObjectReference()
        {
        }

        public AvatarObjectReference(T target)
        {
            Set(target);
        }

        public bool IsConfigured => !string.IsNullOrEmpty(referencePath) || targetObject != null;

        public T Get(Component container)
        {
            if (_hasCachedResult &&
                _cachedSequence == HierarchyChangedSequence &&
                _cachedPath == referencePath &&
                ReferenceEquals(_cachedSourceTarget, targetObject))
            {
                return _cachedResolvedTarget;
            }

            _hasCachedResult = true;
            _cachedSequence = HierarchyChangedSequence;
            _cachedPath = referencePath;
            _cachedSourceTarget = targetObject;

            if (container == null)
            {
                _cachedResolvedTarget = null;
                return null;
            }

            Transform avatarRoot = AvatarHierarchyUtil.FindAvatarInParents(container.transform);
            if (avatarRoot == null)
            {
                _cachedResolvedTarget = targetObject;
                return _cachedResolvedTarget;
            }

            if (IsValidTarget(targetObject, avatarRoot))
            {
                _cachedResolvedTarget = targetObject;
                return _cachedResolvedTarget;
            }

            if (!string.IsNullOrEmpty(referencePath))
            {
                Transform targetTransform = ResolveReferenceTransform(avatarRoot, referencePath);
                if (targetTransform != null)
                {
                    _cachedResolvedTarget = targetTransform.GetComponent<T>();
                    if (_cachedResolvedTarget != null)
                    {
                        return _cachedResolvedTarget;
                    }
                }
            }

            _cachedResolvedTarget = null;
            return _cachedResolvedTarget;
        }

        public void Set(T target)
        {
            referencePath = GetReferencePath(target != null ? target.gameObject : null);
            targetObject = target;
            _cachedResolvedTarget = target;
            _cachedSourceTarget = target;
            _cachedPath = referencePath;
            _cachedSequence = HierarchyChangedSequence;
            _hasCachedResult = true;
        }
    }
}
