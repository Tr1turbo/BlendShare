// Derived from Modular Avatar (https://github.com/bdunderscore/modular-avatar)
// Original work Copyright (c) 2022 bd_
// Distributed under the MIT License; see THIRD_PARTY_NOTICES.md

using System;
using System.Collections.Generic;
using UnityEngine;

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
                child != null ? child.transform : null);
        }

        public static string RelativePath(Transform root, Transform child)
        {
            if (root == child)
            {
                return string.Empty;
            }

            var pathSegments = new List<string>();
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

            var resolvedTypes = new List<Type>(s_AvatarRootTypeNames.Length);
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

    internal static class RuntimeNodePathUtility
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('\\', '/').Trim();
            while (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            while (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            while (normalized.Contains("//"))
            {
                normalized = normalized.Replace("//", "/");
            }

            return normalized;
        }

        public static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || target == root)
            {
                return string.Empty;
            }

            return Normalize(AvatarHierarchyUtil.RelativePath(root, target));
        }
    }

    /// <summary>
    /// Pure runtime avatar-relative object reference. Editor-only <c>SerializedProperty</c> inspection lives in
    /// <c>AvatarObjectReferenceDrawer</c> inside <c>UI.Editor</c> so <c>Runtime</c> stays 100% Player-isolated.
    /// </summary>
    [Serializable]
    public class AvatarObjectReference
    {
        private static long s_HierarchyChangedSequence = long.MinValue;

        public const string AvatarRoot = "$$$AVATAR_ROOT$$$";
        public const string ReferencePathFieldName = "referencePath";
        public const string TargetObjectFieldName = "targetObject";

        public string referencePath;

        /// <summary>
        /// Called by Editor hierarchy listeners to invalidate cached resolutions without referencing UnityEditor in Runtime.
        /// </summary>
        public static void NotifyHierarchyChanged()
        {
            s_HierarchyChangedSequence++;
        }

        protected static long HierarchyChangedSequence => s_HierarchyChangedSequence;

        public static Transform ResolveReferenceTransform(Transform avatarRoot, string path)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(path))
            {
                return null;
            }

            return path == AvatarRoot
                ? avatarRoot
                : avatarRoot.Find(path);
        }

        public static string GetReferencePath(GameObject target)
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

        public static bool IsValidTarget(Component candidate, Transform avatarRoot)
        {
            return candidate != null &&
                   avatarRoot != null &&
                   (candidate.transform == avatarRoot || candidate.transform.IsChildOf(avatarRoot));
        }
    }

    [Serializable]
    public class AvatarObjectReference<T> : AvatarObjectReference where T : Component
    {
        [SerializeField] private T targetObject;

        [NonSerialized] private long m_CachedSequence = long.MinValue;
        [NonSerialized] private bool m_HasCachedResult;
        [NonSerialized] private string m_CachedPath;
        [NonSerialized] private T m_CachedSourceTarget;
        [NonSerialized] private T m_CachedResolvedTarget;

        public AvatarObjectReference()
        {
        }

        public AvatarObjectReference(T target)
        {
            Set(target);
        }

        public T DirectTarget => targetObject;
        public bool IsConfigured => !string.IsNullOrEmpty(referencePath) || targetObject != null;

        public T Get(Component container)
        {
            if (m_HasCachedResult &&
                m_CachedSequence == HierarchyChangedSequence &&
                m_CachedPath == referencePath &&
                ReferenceEquals(m_CachedSourceTarget, targetObject))
            {
                return m_CachedResolvedTarget;
            }

            m_HasCachedResult = true;
            m_CachedSequence = HierarchyChangedSequence;
            m_CachedPath = referencePath;
            m_CachedSourceTarget = targetObject;

            if (container == null)
            {
                m_CachedResolvedTarget = null;
                return null;
            }

            Transform avatarRoot = AvatarHierarchyUtil.FindAvatarInParents(container.transform);
            if (avatarRoot == null)
            {
                m_CachedResolvedTarget = targetObject;
                return m_CachedResolvedTarget;
            }

            if (IsValidTarget(targetObject, avatarRoot))
            {
                m_CachedResolvedTarget = targetObject;
                return m_CachedResolvedTarget;
            }

            if (!string.IsNullOrEmpty(referencePath))
            {
                Transform targetTransform = ResolveReferenceTransform(avatarRoot, referencePath);
                if (targetTransform != null)
                {
                    m_CachedResolvedTarget = targetTransform.GetComponent<T>();
                    if (m_CachedResolvedTarget != null)
                    {
                        return m_CachedResolvedTarget;
                    }
                }
            }

            m_CachedResolvedTarget = null;
            return m_CachedResolvedTarget;
        }

        public void Set(T target)
        {
            referencePath = GetReferencePath(target != null ? target.gameObject : null);
            targetObject = target;
            m_CachedResolvedTarget = target;
            m_CachedSourceTarget = target;
            m_CachedPath = referencePath;
            m_CachedSequence = HierarchyChangedSequence;
            m_HasCachedResult = true;
        }
    }
}
