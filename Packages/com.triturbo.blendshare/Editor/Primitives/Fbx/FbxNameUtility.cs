using System;
using System.Linq;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Utilities for cleaning FBX object names and paths.</summary>
    public static class FbxNameUtility
    {
        /// <summary>Strips the FBX class prefix and null-terminated suffix from a name.</summary>
        public static string CleanObjectName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            int classSeparator = value.IndexOf("::", StringComparison.Ordinal);
            if (classSeparator >= 0)
            {
                value = value.Substring(classSeparator + 2);
            }

            int nullSeparator = value.IndexOf('\0');
            if (nullSeparator >= 0)
            {
                value = value.Substring(0, nullSeparator);
            }

            return value;
        }

        /// <summary>Cleans each hierarchy segment and joins nonempty segments with slashes.</summary>
        public static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(
                "/",
                value.Replace('\\', '/')
                    .Trim()
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => CleanObjectName(s)?.Trim() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s) && s != "."));
        }

        /// <summary>Returns the normalized parent hierarchy path, or empty string for root-level or empty paths.</summary>
        public static string GetParentPath(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            int lastSlash = normalized.LastIndexOf('/');
            return lastSlash < 0 ? string.Empty : normalized.Substring(0, lastSlash);
        }

        /// <summary>Returns the normalized leaf node name from a hierarchy path.</summary>
        public static string GetNodeName(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            int lastSlash = normalized.LastIndexOf('/');
            return lastSlash < 0 ? normalized : normalized.Substring(lastSlash + 1);
        }

        /// <summary>Returns the slash-separated hierarchy depth (0 for top-level nodes, -1 for empty).</summary>
        public static int GetPathDepth(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return -1;
            }

            int depth = 0;
            for (int i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] == '/')
                {
                    depth++;
                }
            }

            return depth;
        }

        /// <summary>Deterministic parent-before-child hierarchy path comparison (depth ascending, then ordinal).</summary>
        public static int CompareHierarchyOrder(string leftPath, string rightPath)
        {
            string left = NormalizePath(leftPath);
            string right = NormalizePath(rightPath);
            int depthDiff = GetPathDepth(left).CompareTo(GetPathDepth(right));
            return depthDiff != 0 ? depthDiff : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
