using System;
using System.IO;
using UnityEditor.PackageManager;

namespace Triturbo.BlendShare.EditorAssets
{
    /// <summary>
    /// Shared Unity asset-path to physical filesystem-path translation supporting Assets/, embedded, local, git, and registry packages.
    /// </summary>
    public static class BlendSharePathUtility
    {
        public static string ResolvePhysicalPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(assetPath))
            {
                return assetPath;
            }

            var package = PackageInfo.FindForAssetPath(assetPath);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
            {
                string relative = assetPath.Length > package.assetPath.Length
                    ? assetPath.Substring(package.assetPath.Length).TrimStart('/', '\\')
                    : string.Empty;
                return string.IsNullOrEmpty(relative)
                    ? package.resolvedPath
                    : Path.Combine(package.resolvedPath, relative);
            }

            return Path.GetFullPath(assetPath);
        }
    }
}
