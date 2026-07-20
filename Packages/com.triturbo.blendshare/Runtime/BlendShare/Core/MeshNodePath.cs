using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Mesh identity helpers. BlendShare stores FBX node paths only;
    /// an empty/root path is represented by ".".
    /// </summary>
    public static class MeshNodePath
    {
        public const string Root = ".";

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == Root)
            {
                return Root;
            }

            string normalized = string.Join(
                "/",
                path.Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0 && part != Root));

            return string.IsNullOrEmpty(normalized) ? Root : normalized;
        }

        /// <summary>Normalizes an optional node path, returning an empty string only when it is unset or whitespace.</summary>
        public static string NormalizeOptional(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Normalize(path);
        }

        public static string ToFbxPath(string path)
        {
            string normalized = Normalize(path);
            return normalized == Root ? string.Empty : normalized;
        }

        public static string LeafName(string path)
        {
            string normalized = Normalize(path);
            if (normalized == Root)
            {
                return Root;
            }

            int separator = normalized.LastIndexOf('/');
            return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
        }

        public static string ParentPath(string path)
        {
            string normalized = Normalize(path);
            if (normalized == Root)
            {
                return Root;
            }

            int separator = normalized.LastIndexOf('/');
            return separator >= 0 ? normalized.Substring(0, separator) : Root;
        }

        public static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
            {
                return Root;
            }

            var parts = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return current == root
                ? Normalize(string.Join("/", parts))
                : Root;
        }

        public static Transform FindRelativeTransform(Transform root, string path)
        {
            if (root == null)
            {
                return null;
            }

            string normalized = Normalize(path);
            return normalized == Root ? root : root.Find(normalized);
        }
    }
}
