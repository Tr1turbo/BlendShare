using System;
using System.Linq;

namespace Triturbo.BlendShare.Fbx
{
    public static class FbxNameUtility
    {
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
