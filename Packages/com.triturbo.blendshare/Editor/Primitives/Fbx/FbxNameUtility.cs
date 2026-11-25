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
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return string.Join(
                "/",
                value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(CleanObjectName));
        }
    }
}
