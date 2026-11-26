using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.BlendShare.Assets;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.EditorAssets;
using Triturbo.BlendShare.EditorAssets.Mapping;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Primitives;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using LegacyMeshData = Triturbo.BlendShapeShare.BlendShapeData.MeshData;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Triturbo.BlendShare.Legacy.Migration
{
    /// <summary>
    /// Diagnostic severity emitted while migrating a 1.x <see cref="BlendShapeDataSO"/> into a 2.0 <c>.blendshare</c>.
    /// </summary>
    public enum LegacyMigrationSeverity
    {
        Warning = 0,
        Error = 1
    }

    /// <summary>
    /// Structured diagnostic entry produced by <see cref="LegacyPatchMigrator"/>.
    /// </summary>
    public readonly struct LegacyMigrationDiagnostic
    {
        public LegacyMigrationSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }

        public LegacyMigrationDiagnostic(LegacyMigrationSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>
    /// Outcome of upgrading a BlendShare 1.x <see cref="BlendShapeDataSO"/> into a BlendShare 2.0 <c>.blendshare</c> file.
    /// </summary>
    public sealed class LegacyMigrationResult
    {
        public bool Succeeded { get; }
        public string OutputAssetPath { get; }
        public BlendShareAsset ImportedAsset { get; }
        public PatchDocument Document { get; }
        public IReadOnlyList<LegacyMigrationDiagnostic> Diagnostics { get; }

        public LegacyMigrationResult(
            bool succeeded,
            string outputAssetPath,
            BlendShareAsset importedAsset,
            PatchDocument document,
            IReadOnlyList<LegacyMigrationDiagnostic> diagnostics)
        {
            Succeeded = succeeded;
            OutputAssetPath = outputAssetPath ?? string.Empty;
            ImportedAsset = importedAsset;
            Document = document;
            Diagnostics = diagnostics ?? Array.Empty<LegacyMigrationDiagnostic>();
        }
    }

    /// <summary>
    /// One-way upgrader converting BlendShare 1.x <see cref="BlendShapeDataSO"/> (<c>.asset</c>) patches and their
    /// <see cref="BlendShapeSelectionSO"/> sub-assets into canonical BlendShare 2.0 <c>BLSH</c> v1 <c>.blendshare</c>
    /// containers plus <c>.blendshare.meta</c> settings.
    /// </summary>
    public static class LegacyPatchMigrator
    {
        private const string k_DefaultBlendShapeFeatureId = "blend-shapes";
        private const string k_DefaultLegacyDeformerId = "+BlendShare";
        private static readonly char[] s_InvalidSegmentChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Returns true when <paramref name="assetPath"/> is a safe relative Unity asset path (no traversal segments or escaping symlinks/junctions)
        /// residing inside <c>Assets/</c> or an editable embedded/local UPM package rather than an immutable cache or external directory.
        /// </summary>
        public static bool IsWritableAssetLocation(string assetPath)
        {
            return TryResolveValidatedPhysicalPath(assetPath, out _);
        }

        private static bool TryResolveValidatedPhysicalPath(string assetPath, out string canonicalTargetPath)
        {
            canonicalTargetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath))
            {
                return false;
            }

            string normalized = assetPath.Trim().Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (segments.Length < 2)
            {
                return false;
            }

            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i];
                if (seg.Length == 0 || seg == "." || seg == ".." || seg.IndexOfAny(s_InvalidSegmentChars) >= 0)
                {
                    return false;
                }
            }

            string permittedRoot;
            if (string.Equals(segments[0], "Assets", StringComparison.Ordinal))
            {
                permittedRoot = Application.dataPath;
            }
            else if (string.Equals(segments[0], "Packages", StringComparison.Ordinal) && segments.Length >= 3)
            {
                var pkg = PackageInfo.FindForAssetPath(normalized);
                if (pkg == null ||
                    (pkg.source != PackageSource.Embedded && pkg.source != PackageSource.Local) ||
                    string.IsNullOrWhiteSpace(pkg.resolvedPath))
                {
                    return false;
                }

                permittedRoot = pkg.resolvedPath;
            }
            else
            {
                return false;
            }

            if (!TryResolveExistingRealPath(Path.GetFullPath(permittedRoot), out string canonicalRoot))
            {
                return false;
            }

            string rawPhysical = BlendSharePathUtility.ResolvePhysicalPath(normalized);
            if (string.IsNullOrWhiteSpace(rawPhysical))
            {
                return false;
            }

            string fullPhysical = Path.GetFullPath(rawPhysical);
            if (IsLinkOrReparsePoint(fullPhysical))
            {
                return false;
            }

            if (!TryResolveCanonicalDestinationPath(fullPhysical, canonicalRoot, out string resolvedTarget))
            {
                return false;
            }

            canonicalTargetPath = resolvedTarget;
            return true;
        }

        private static bool TryResolveCanonicalDestinationPath(
            string fullPath,
            string canonicalRoot,
            out string canonicalPath)
        {
            canonicalPath = string.Empty;
            var tailSegments = new List<string>();
            string cur = fullPath;

            while (!string.IsNullOrEmpty(cur))
            {
                bool isFile = File.Exists(cur);
                bool isDir = !isFile && Directory.Exists(cur);
                bool exists = isFile || isDir;
                if (!exists && IsLinkOrReparsePoint(cur))
                {
                    // Reject dangling symbolic links or broken directory junctions along the destination path.
                    return false;
                }

                if (exists)
                {
                    if (tailSegments.Count == 0 && isDir)
                    {
                        // Destination .blendshare path cannot overwrite an existing directory.
                        return false;
                    }

                    if (!TryResolveExistingRealPath(cur, out string resolvedExisting))
                    {
                        return false;
                    }

                    string existingAncestorDir = isFile ? Path.GetDirectoryName(resolvedExisting) : resolvedExisting;
                    if (string.IsNullOrEmpty(existingAncestorDir) ||
                        !IsExistingDirectoryWithinOrEqualToRoot(existingAncestorDir, canonicalRoot))
                    {
                        return false;
                    }

                    string combined = resolvedExisting;
                    for (int i = tailSegments.Count - 1; i >= 0; i--)
                    {
                        combined = Path.Combine(combined, tailSegments[i]);
                    }

                    if (!IsStrictSubPathOfOrdinal(combined, canonicalRoot))
                    {
                        return false;
                    }

                    canonicalPath = combined;
                    return true;
                }

                string leaf = Path.GetFileName(cur);
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(leaf) || string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.Ordinal))
                {
                    return false;
                }

                tailSegments.Add(leaf);
                cur = parent;
            }

            return false;
        }

        private static bool IsExistingDirectoryWithinOrEqualToRoot(string existingDir, string canonicalRoot)
        {
            string cur = existingDir;
            while (!string.IsNullOrEmpty(cur))
            {
                if (AreSameExistingDirectoryIdentity(cur, canonicalRoot))
                {
                    return true;
                }

                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cur, StringComparison.Ordinal))
                {
                    break;
                }

                cur = parent;
            }

            return false;
        }

#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
#if UNITY_EDITOR_OSX
        private const string k_PosixLib = "/usr/lib/libSystem.B.dylib";
#else
        private const string k_PosixLib = "libc";
#endif

        [DllImport(k_PosixLib, EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr PosixRealPath(byte[] path, IntPtr resolvedPath);

        [DllImport(k_PosixLib, EntryPoint = "readlink", SetLastError = true)]
        private static extern IntPtr PosixReadLink(byte[] path, byte[] buffer, UIntPtr bufferSize);

        [DllImport(k_PosixLib, EntryPoint = "free")]
        private static extern void PosixFree(IntPtr ptr);

        private static bool IsLinkOrReparsePoint(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            byte[] utf8 = ToNullTerminatedUtf8(path);
            byte[] scratch = new byte[16];
            return PosixReadLink(utf8, scratch, (UIntPtr)scratch.Length).ToInt64() >= 0;
        }

        private static bool TryResolveExistingRealPath(string existingPath, out string realPath)
        {
            realPath = string.Empty;
            if (string.IsNullOrEmpty(existingPath))
            {
                return false;
            }

            IntPtr ptr = PosixRealPath(ToNullTerminatedUtf8(existingPath), IntPtr.Zero);
            if (ptr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                realPath = ReadNullTerminatedUtf8(ptr);
                return !string.IsNullOrEmpty(realPath);
            }
            finally
            {
                PosixFree(ptr);
            }
        }

        private static byte[] ToNullTerminatedUtf8(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            byte[] bytes = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
            return bytes;
        }

        private static string ReadNullTerminatedUtf8(IntPtr ptr)
        {
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0)
            {
                len++;
            }

            if (len == 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool AreSameExistingDirectoryIdentity(string dirA, string dirB)
        {
            return TryResolveExistingRealPath(dirA, out string realA) &&
                   TryResolveExistingRealPath(dirB, out string realB) &&
                   string.Equals(realA, realB, StringComparison.Ordinal);
        }
#elif UNITY_EDITOR_WIN
        private const uint k_FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint k_OpenExisting = 3;
        private const uint k_FileFlagBackupSemantics = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32FileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr Win32CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint Win32GetFinalPathNameByHandle(
            IntPtr hFile,
            StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        private static extern bool Win32GetFileInformationByHandle(
            IntPtr hFile,
            out Win32FileInformation lpFileInformation);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        private static extern bool Win32CloseHandle(IntPtr hObject);

        private static bool IsLinkOrReparsePoint(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                var attrs = File.GetAttributes(path);
                return attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveExistingRealPath(string existingPath, out string realPath)
        {
            realPath = string.Empty;
            if (string.IsNullOrEmpty(existingPath))
            {
                return false;
            }

            IntPtr handle = Win32CreateFile(
                existingPath,
                0u,
                k_FileShareAll,
                IntPtr.Zero,
                k_OpenExisting,
                k_FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                var sb = new StringBuilder(512);
                uint len = Win32GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0u);
                if (len >= sb.Capacity)
                {
                    sb = new StringBuilder((int)len + 1);
                    len = Win32GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0u);
                }

                if (len == 0)
                {
                    return false;
                }

                string raw = sb.ToString(0, (int)len);
                if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    raw = @"\\" + raw.Substring(8);
                }
                else if (raw.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                {
                    raw = raw.Substring(4);
                }

                realPath = Path.GetFullPath(raw);
                return true;
            }
            finally
            {
                Win32CloseHandle(handle);
            }
        }

        private static bool AreSameExistingDirectoryIdentity(string dirA, string dirB)
        {
            return TryGetWin32FileIdentity(dirA, out var idA) &&
                   TryGetWin32FileIdentity(dirB, out var idB) &&
                   idA.VolumeSerialNumber == idB.VolumeSerialNumber &&
                   idA.FileIndexHigh == idB.FileIndexHigh &&
                   idA.FileIndexLow == idB.FileIndexLow;
        }

        private static bool TryGetWin32FileIdentity(string path, out Win32FileInformation info)
        {
            info = default;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            IntPtr handle = Win32CreateFile(
                path,
                0u,
                k_FileShareAll,
                IntPtr.Zero,
                k_OpenExisting,
                k_FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                return Win32GetFileInformationByHandle(handle, out info);
            }
            finally
            {
                Win32CloseHandle(handle);
            }
        }
#else
        private static bool IsLinkOrReparsePoint(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                return attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveExistingRealPath(string existingPath, out string realPath)
        {
            realPath = string.Empty;
            if (string.IsNullOrEmpty(existingPath) || IsLinkOrReparsePoint(existingPath))
            {
                return false;
            }

            realPath = Path.GetFullPath(existingPath);
            return true;
        }

        private static bool AreSameExistingDirectoryIdentity(string dirA, string dirB)
        {
            return TryResolveExistingRealPath(dirA, out string realA) &&
                   TryResolveExistingRealPath(dirB, out string realB) &&
                   string.Equals(realA, realB, StringComparison.Ordinal);
        }
#endif

        /// <summary>
        /// Checks strict containment on canonical paths resolved from the filesystem (which already carry exact on-disk casing),
        /// using ordinal comparison so distinct case-sensitive roots or directories differing only by case never collide.
        /// </summary>
        private static bool IsStrictSubPathOfOrdinal(string candidateCanonicalPath, string rootCanonicalDir)
        {
            if (string.IsNullOrEmpty(candidateCanonicalPath) || string.IsNullOrEmpty(rootCanonicalDir))
            {
                return false;
            }

            string trimmedRoot = rootCanonicalDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootWithSep = trimmedRoot + Path.DirectorySeparatorChar;
            return candidateCanonicalPath.Length > rootWithSep.Length &&
                   candidateCanonicalPath.StartsWith(rootWithSep, StringComparison.Ordinal);
        }

        /// <summary>
        /// Computes the default <c>.blendshare</c> Unity asset path for a legacy <see cref="BlendShapeDataSO"/>.
        /// Falls back to <c>Assets/</c> when the source lives in an immutable UPM package.
        /// </summary>
        public static string GetDefaultOutputAssetPath(BlendShapeDataSO legacyAsset, bool uniqueIfExists = false)
        {
            if (legacyAsset == null)
            {
                return string.Empty;
            }

            string legacyPath = AssetDatabase.GetAssetPath(legacyAsset);
            if (string.IsNullOrWhiteSpace(legacyPath))
            {
                return string.Empty;
            }

            string baseName = Path.GetFileNameWithoutExtension(legacyPath);
            string dir = IsWritableAssetLocation(legacyPath)
                ? (Path.GetDirectoryName(legacyPath)?.Replace('\\', '/') ?? "Assets")
                : "Assets";
            string candidate = $"{dir}/{baseName}.blendshare";
            return uniqueIfExists ? AssetDatabase.GenerateUniqueAssetPath(candidate) : candidate;
        }

        /// <summary>
        /// Upgrades <paramref name="legacyAsset"/> into a <c>.blendshare</c> file at <paramref name="outputAssetPath"/>
        /// (or its default writable path when omitted), imports it into Unity, and persists all working selections,
        /// ordering overrides, and selection presets into <c>.blendshare.meta</c>.
        /// </summary>
        public static LegacyMigrationResult MigrateToBlendShare(
            BlendShapeDataSO legacyAsset,
            GameObject targetBaseFbx = null,
            string outputAssetPath = null,
            PayloadEncodeOptions? encodeOptions = null)
        {
            var diagnostics = new List<LegacyMigrationDiagnostic>();
            if (legacyAsset == null)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_NULL_ASSET",
                    "Legacy 1.x BlendShapeDataSO asset is null."));
                return new LegacyMigrationResult(false, string.Empty, null, null, diagnostics);
            }

            GameObject effectiveFbx = targetBaseFbx != null ? targetBaseFbx : legacyAsset.Original;
            if (effectiveFbx == null)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_MISSING_FBX",
                    $"Legacy asset '{legacyAsset.name}' has no assigned Original FBX."));
                return new LegacyMigrationResult(false, string.Empty, null, null, diagnostics);
            }

            string resolvedAssetPath = string.IsNullOrWhiteSpace(outputAssetPath)
                ? GetDefaultOutputAssetPath(legacyAsset)
                : outputAssetPath.Trim().Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(resolvedAssetPath) &&
                !resolvedAssetPath.EndsWith(".blendshare", StringComparison.OrdinalIgnoreCase))
            {
                resolvedAssetPath += ".blendshare";
            }

            if (string.IsNullOrWhiteSpace(resolvedAssetPath) || !IsWritableAssetLocation(resolvedAssetPath))
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_IMMUTABLE_OUTPUT_PATH",
                    $"Output path '{resolvedAssetPath}' is missing, outside editable project roots, or belongs to an immutable package."));
                return new LegacyMigrationResult(false, resolvedAssetPath ?? string.Empty, null, null, diagnostics);
            }

            string fbxAssetPath = AssetDatabase.GetAssetPath(effectiveFbx);
            string fbxPhysicalPath = BlendSharePathUtility.ResolvePhysicalPath(fbxAssetPath);
            var sceneRead = !string.IsNullOrWhiteSpace(fbxPhysicalPath)
                ? UfbxScene.TryLoad(fbxPhysicalPath, UfbxLoadOptions.Default)
                : FbxUnityAssetReader.ReadScene(effectiveFbx, UfbxLoadOptions.Default);
            if (!sceneRead.Success || sceneRead.Value == null)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_FBX_READ_FAILED",
                    $"Failed to read base FBX '{effectiveFbx.name}': {sceneRead.Message}"));
                return new LegacyMigrationResult(false, resolvedAssetPath, null, null, diagnostics);
            }

            PatchDocument document;
            Dictionary<string, MeshSettingsMigrationSeed> meshSettingsSeeds;
            using (var scene = sceneRead.Value)
            {
                var importSettings = FbxUnityAssetReader.GetImportSettings(effectiveFbx);
                if (!TryBuildPatchDocument(
                        legacyAsset,
                        effectiveFbx,
                        scene,
                        importSettings,
                        diagnostics,
                        out document,
                        out meshSettingsSeeds))
                {
                    return new LegacyMigrationResult(false, resolvedAssetPath, null, null, diagnostics);
                }
            }

            // Re-validate canonical destination containment immediately before creating directories or writing
            if (!TryResolveValidatedPhysicalPath(resolvedAssetPath, out string physicalOutputPath))
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_IMMUTABLE_OUTPUT_PATH",
                    $"Output path '{resolvedAssetPath}' resolved outside editable project roots or traversed a symbolic link/junction."));
                return new LegacyMigrationResult(false, resolvedAssetPath, null, null, diagnostics);
            }

            string tempPath = null;
            try
            {
                string directory = Path.GetDirectoryName(physicalOutputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                tempPath = physicalOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var writer = new BlendShareContainerWriter();
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    writer.Write(fs, document, encodeOptions ?? PayloadEncodeOptions.Default);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(physicalOutputPath))
                {
                    File.Replace(tempPath, physicalOutputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, physicalOutputPath);
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* ignore cleanup failure */ }
                }

                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_WRITE_FAILED",
                    $"Failed to write '{resolvedAssetPath}': {ex.Message}"));
                return new LegacyMigrationResult(false, resolvedAssetPath, null, null, diagnostics);
            }

            AssetDatabase.ImportAsset(resolvedAssetPath, ImportAssetOptions.ForceSynchronousImport);
            var imported = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(resolvedAssetPath);
            if (imported == null)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_IMPORT_FAILED",
                    $"Wrote '{resolvedAssetPath}', but Unity failed to import it as a BlendShareAsset."));
                return new LegacyMigrationResult(false, resolvedAssetPath, null, document, diagnostics);
            }

            if (!TryApplyMigratedImportSettings(
                    legacyAsset,
                    effectiveFbx,
                    resolvedAssetPath,
                    imported,
                    meshSettingsSeeds,
                    diagnostics))
            {
                imported = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(resolvedAssetPath);
                return new LegacyMigrationResult(false, resolvedAssetPath, imported, document, diagnostics);
            }

            imported = AssetDatabase.LoadAssetAtPath<BlendShareAsset>(resolvedAssetPath);
            return new LegacyMigrationResult(true, resolvedAssetPath, imported, document, diagnostics);
        }

        private static bool TryBuildPatchDocument(
            BlendShapeDataSO legacyAsset,
            GameObject targetFbx,
            UfbxScene scene,
            FbxUnityImportSettings importSettings,
            List<LegacyMigrationDiagnostic> diagnostics,
            out PatchDocument document,
            out Dictionary<string, MeshSettingsMigrationSeed> meshSettingsSeeds)
        {
            document = null;
            meshSettingsSeeds = new Dictionary<string, MeshSettingsMigrationSeed>(StringComparer.Ordinal);

            var legacyMeshes = legacyAsset.MeshDataList;
            if (legacyMeshes == null || legacyMeshes.Count == 0)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_EMPTY_MESH_LIST",
                    $"Legacy asset '{legacyAsset.name}' contains no mesh entries."));
                return false;
            }

            CanonicalMeshSpace.SanitizeMetadata(
                scene.Metadata,
                out var axes,
                out double unitMeters,
                out bool spaceFallback);
            if (spaceFallback)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Warning,
                    "LEG_MIG_SPACE_FALLBACK",
                    $"Base FBX '{targetFbx.name}' had unspecified axes or unit scale; defaulted to meters (+X/+Y/+Z)."));
            }

            FbxSceneMetadata effectiveMetadata = spaceFallback
                ? new FbxSceneMetadata(
                    scene.Metadata.FileVersion,
                    axes.Up,
                    unitMeters,
                    scene.Metadata.OriginalUnitMeters,
                    scene.Metadata.FramesPerSecond,
                    axes)
                : scene.Metadata;

            var unityMeshesByPath = CollectUnityMeshesByPath(targetFbx);
            var groupedByNodePath = new Dictionary<string, (IFbxMesh FbxMesh, List<LegacyMeshData> Entries)>(StringComparer.Ordinal);
            var orderedNodePaths = new List<string>();
            bool hasError = false;

            for (int i = 0; i < legacyMeshes.Count; i++)
            {
                var legacyMesh = legacyMeshes[i];
                if (legacyMesh == null)
                {
                    continue;
                }

                int maxControlPointIndex = ComputeMaxReferencedControlPointIndex(legacyMesh);
                if (!TryResolveFbxMesh(
                        scene,
                        unityMeshesByPath,
                        legacyMesh,
                        maxControlPointIndex,
                        out string nodePath,
                        out var fbxMesh,
                        out string failureCode,
                        out string failureMessage))
                {
                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Error,
                        failureCode,
                        $"Mesh '{legacyMesh.MeshName}' in '{legacyAsset.name}': {failureMessage}"));
                    hasError = true;
                    continue;
                }

                if (!groupedByNodePath.TryGetValue(nodePath, out var bucket))
                {
                    bucket = (fbxMesh, new List<LegacyMeshData>());
                    groupedByNodePath.Add(nodePath, bucket);
                    orderedNodePaths.Add(nodePath);
                }

                bucket.Entries.Add(legacyMesh);
            }

            if (hasError)
            {
                return false;
            }

            var selectionsByLegacyMeshName = LoadLegacySelectionSubAssets(legacyAsset);
            var convertedMeshes = new List<MeshDocument>(orderedNodePaths.Count);

            for (int m = 0; m < orderedNodePaths.Count; m++)
            {
                string nodePath = orderedNodePaths[m];
                var (fbxMesh, entries) = groupedByNodePath[nodePath];
                int domainCount = fbxMesh.ControlPointCount;
                var digest = FbxTopologyUtility.ComputeDigest(fbxMesh);

                if (!TryBuildBlendShapeFeatureForMesh(
                        nodePath,
                        domainCount,
                        entries,
                        selectionsByLegacyMeshName,
                        diagnostics,
                        out var feature,
                        out var settingsSeed))
                {
                    hasError = true;
                    continue;
                }

                if (feature == null || feature.Shapes.Count == 0)
                {
                    continue;
                }

                var meshNode = scene.FindNodeByPath(nodePath) ?? fbxMesh.PrimaryNode;
                double sigmaMax = meshNode != null
                    ? FbxLinearAlgebra3d.ComputeMaxSingularValue(meshNode.GeometryToWorldMatrix)
                    : 0d;
                double effectiveMeshUnitMeters = sigmaMax > 1e-12d && !double.IsNaN(sigmaMax) && !double.IsInfinity(sigmaMax)
                    ? unitMeters * sigmaMax
                    : unitMeters;
                var sketch = GeometrySketch.TryBuild(fbxMesh.GetControlPoints(), fbxMesh.GetFaceCornerControlPoints());

                var meshDoc = new MeshDocument
                {
                    MeshId = nodePath,
                    Name = ExtractLeafName(nodePath),
                    NodePath = nodePath,
                    Topology = new TopologyDigestRecord
                    {
                        Hash = digest.Hash,
                        ControlPointCount = digest.ControlPointCount,
                        FaceCount = digest.FaceCount,
                        IsValid = digest.IsValid
                    },
                    Sketch = sketch,
                    EffectivePositionUnitMeters = effectiveMeshUnitMeters
                };

                if (TryBuildEmbeddedMapping(nodePath, fbxMesh, effectiveMetadata, importSettings, unityMeshesByPath, diagnostics, out var mappingEntry) &&
                    VertexMappingEntry.TryValidateEntries(new[] { mappingEntry }, meshDoc.Topology, out _))
                {
                    meshDoc.Mappings.Add(mappingEntry);
                }

                meshDoc.Features.Add(feature);
                convertedMeshes.Add(meshDoc);
                meshSettingsSeeds[nodePath] = settingsSeed ?? new MeshSettingsMigrationSeed();
            }

            if (hasError)
            {
                return false;
            }

            if (convertedMeshes.Count == 0)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_NO_SHAPES",
                    $"Legacy asset '{legacyAsset.name}' produced zero valid blendshape features."));
                return false;
            }

            string rawDeformerId = legacyAsset.DeformerId?.Trim() ?? string.Empty;
            string patchId = string.IsNullOrEmpty(rawDeformerId) ||
                             string.Equals(rawDeformerId, k_DefaultLegacyDeformerId, StringComparison.Ordinal)
                ? $"{k_DefaultLegacyDeformerId}-{legacyAsset.name}"
                : rawDeformerId;

            document = new PatchDocument
            {
                PatchId = patchId,
                SpaceProvenance = PatchSpaceProvenance.FromMetadata(axes, unitMeters),
                Skeleton = null,
                Meshes = convertedMeshes
            };
            return true;
        }

        private static bool TryBuildBlendShapeFeatureForMesh(
            string nodePath,
            int domainCount,
            List<LegacyMeshData> entries,
            Dictionary<string, List<BlendShapeSelectionSO>> selectionsByLegacyMeshName,
            List<LegacyMigrationDiagnostic> diagnostics,
            out BlendShapeFeature feature,
            out MeshSettingsMigrationSeed settingsSeed)
        {
            feature = null;
            settingsSeed = new MeshSettingsMigrationSeed();

            // Preserve the authored m_BlendShapes order inside the .blendshare container, while capturing
            // working mutes/reorders from m_ShapeNames in .blendshare.meta DisabledShapeIds + OrderOverrides.
            var wrappersByName = new Dictionary<string, BlendShapeWrapper>(StringComparer.Ordinal);
            var shapesWithUnityWeightFallback = new HashSet<string>(StringComparer.Ordinal);
            var authoredNames = new List<string>();
            var enabledOrderedNames = new List<string>();
            var enabledSet = new HashSet<string>(StringComparer.Ordinal);
            var legacyMeshNames = new HashSet<string>(StringComparer.Ordinal);

            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                if (!string.IsNullOrEmpty(entry.MeshName))
                {
                    legacyMeshNames.Add(entry.MeshName);
                }

                var rawShapes = entry.RawBlendShapes;
                for (int i = 0; i < rawShapes.Count; i++)
                {
                    var wrapper = rawShapes[i];
                    if (wrapper == null || string.IsNullOrWhiteSpace(wrapper.ShapeName))
                    {
                        continue;
                    }

                    if (wrappersByName.TryGetValue(wrapper.ShapeName, out var existingWrapper))
                    {
                        if (!AreBlendShapeWrappersEquivalent(existingWrapper, wrapper))
                        {
                            diagnostics.Add(new LegacyMigrationDiagnostic(
                                LegacyMigrationSeverity.Error,
                                "LEG_MIG_CONFLICTING_DUPLICATE_SHAPE",
                                $"Duplicate mesh entries for '{nodePath}' define conflicting blendshape payloads for channel '{wrapper.ShapeName}'."));
                            return false;
                        }
                    }
                    else
                    {
                        wrappersByName.Add(wrapper.ShapeName, wrapper);
                        authoredNames.Add(wrapper.ShapeName);
                    }

                    if (HasUnusableUnityFrameWeights(wrapper))
                    {
                        shapesWithUnityWeightFallback.Add(wrapper.ShapeName);
                    }
                }

                var activeNames = entry.ShapeNames;
                for (int i = 0; i < activeNames.Count; i++)
                {
                    string active = activeNames[i];
                    if (!string.IsNullOrWhiteSpace(active) && wrappersByName.ContainsKey(active) && enabledSet.Add(active))
                    {
                        enabledOrderedNames.Add(active);
                    }
                }
            }

            var shapes = new List<BlendShapeEntry>(authoredNames.Count);
            var shapeIdByName = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenShapeIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < authoredNames.Count; i++)
            {
                string shapeName = authoredNames[i];
                if (!wrappersByName.TryGetValue(shapeName, out var wrapper) ||
                    wrapper.FbxBlendShapeData == null ||
                    wrapper.FbxBlendShapeData.Frames.Count == 0)
                {
                    if (wrapper?.UnityBlendShapeData != null && wrapper.UnityBlendShapeData.Frames.Count > 0)
                    {
                        diagnostics.Add(new LegacyMigrationDiagnostic(
                            LegacyMigrationSeverity.Error,
                            "LEG_MIG_UNITY_ONLY_CHANNEL",
                            $"BlendShape '{shapeName}' on mesh '{nodePath}' contains only legacy Unity vertex deltas without FBX control-point keyframes and cannot be upgraded losslessly."));
                        return false;
                    }

                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Warning,
                        "LEG_MIG_EMPTY_SHAPE",
                        $"Skipped blendshape '{shapeName}' on mesh '{nodePath}' because it has zero keyframes."));
                    continue;
                }

                var legacyFrames = wrapper.FbxBlendShapeData.Frames;
                int frameCount = legacyFrames.Count;
                double[] effectiveWeights = ResolveEffectiveFrameWeights(
                    wrapper.FbxBlendShapeData,
                    wrapper.UnityBlendShapeData,
                    out _);
                if (shapesWithUnityWeightFallback.Contains(shapeName))
                {
                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Warning,
                        "LEG_MIG_UNITY_WEIGHTS_FALLBACK",
                        $"BlendShape '{shapeName}' on mesh '{nodePath}' had mismatched or non-increasing legacy Unity frame weights; defaulted to evenly spaced FBX weights."));
                }

                if (HasNonZeroUnityShadingDeltas(wrapper.UnityBlendShapeData, out bool hasNonZeroNormals, out bool hasNonZeroTangents))
                {
                    string omittedKinds = hasNonZeroNormals && hasNonZeroTangents
                        ? "normal and tangent"
                        : (hasNonZeroNormals ? "normal" : "tangent");
                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Warning,
                        "LEG_MIG_UNITY_SHADING_OMITTED",
                        $"BlendShape '{shapeName}' on mesh '{nodePath}' contained legacy Unity split-vertex {omittedKinds} deltas, which cannot be represented as FBX control-point deltas and were omitted."));
                }

                var frames = new List<BlendShapeFrame>(frameCount);
                var seenFrameIds = new HashSet<string>(StringComparer.Ordinal);

                for (int f = 0; f < frameCount; f++)
                {
                    var legacyFrame = legacyFrames[f];
                    if (legacyFrame == null)
                    {
                        continue;
                    }

                    if (!TryBuildFramePayload(nodePath, shapeName, f, domainCount, legacyFrame, diagnostics, out var payload))
                    {
                        return false;
                    }

                    double weight = effectiveWeights[f];
                    string frameId = MakeStableFrameId(weight, seenFrameIds);
                    frames.Add(new BlendShapeFrame
                    {
                        FrameId = frameId,
                        Weight = weight,
                        Payload = LazyPayload<BlendShapeFramePayload>.FromValue(payload)
                    });
                }

                if (frames.Count == 0)
                {
                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Warning,
                        "LEG_MIG_EMPTY_SHAPE",
                        $"Skipped blendshape '{shapeName}' on mesh '{nodePath}' because all keyframes were null."));
                    continue;
                }

                string shapeId = MakeUniqueId(shapeName, seenShapeIds);
                shapeIdByName[shapeName] = shapeId;
                shapes.Add(new BlendShapeEntry
                {
                    ShapeId = shapeId,
                    Name = shapeName,
                    Frames = frames
                });

                if (!enabledSet.Contains(shapeName))
                {
                    settingsSeed.DisabledShapeIds.Add(shapeId);
                }
            }

            // Detect working order override relative to the authored container order of enabled shapes
            var enabledIdsInAuthoredOrder = new List<string>(shapes.Count);
            for (int i = 0; i < shapes.Count; i++)
            {
                string id = shapes[i].ShapeId;
                if (!settingsSeed.DisabledShapeIds.Contains(id))
                {
                    enabledIdsInAuthoredOrder.Add(id);
                }
            }

            var enabledIdsInWorkingOrder = new List<string>(enabledOrderedNames.Count);
            for (int i = 0; i < enabledOrderedNames.Count; i++)
            {
                if (shapeIdByName.TryGetValue(enabledOrderedNames[i], out string mappedId))
                {
                    enabledIdsInWorkingOrder.Add(mappedId);
                }
            }

            if (!SequenceEqualsOrdinal(enabledIdsInAuthoredOrder, enabledIdsInWorkingOrder))
            {
                settingsSeed.OrderOverrides.AddRange(enabledIdsInWorkingOrder);
            }

            // Convert 1.x BlendShapeSelectionSO sub-assets into 2.0 BlendShapeSelectionSets
            var seenSetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string legacyMeshName in legacyMeshNames)
            {
                if (!selectionsByLegacyMeshName.TryGetValue(legacyMeshName, out var presets))
                {
                    continue;
                }

                for (int p = 0; p < presets.Count; p++)
                {
                    var preset = presets[p];
                    if (preset == null)
                    {
                        continue;
                    }

                    string displayName = string.IsNullOrWhiteSpace(preset.DisplayName)
                        ? $"Selection {p + 1}"
                        : preset.DisplayName.Trim();
                    string setId = MakeUniqueId(displayName, seenSetIds);

                    var presetActiveIds = new List<string>();
                    var presetActiveSet = new HashSet<string>(StringComparer.Ordinal);
                    var rawPresetNames = preset.BlendShapeNames;
                    for (int n = 0; n < rawPresetNames.Count; n++)
                    {
                        string rawName = rawPresetNames[n];
                        if (!string.IsNullOrWhiteSpace(rawName) &&
                            shapeIdByName.TryGetValue(rawName, out string mappedId) &&
                            presetActiveSet.Add(mappedId))
                        {
                            presetActiveIds.Add(mappedId);
                        }
                    }

                    var setRecord = new BlendShapeSelectionSet
                    {
                        SetId = setId,
                        DisplayName = displayName
                    };

                    for (int s = 0; s < shapes.Count; s++)
                    {
                        string id = shapes[s].ShapeId;
                        if (!presetActiveSet.Contains(id))
                        {
                            setRecord.DisabledShapeIds.Add(id);
                        }
                    }

                    setRecord.OrderOverrides.AddRange(presetActiveIds);
                    settingsSeed.SelectionSets.Add(setRecord);
                }
            }

            if (shapes.Count == 0)
            {
                return true;
            }

            feature = new BlendShapeFeature
            {
                FeatureId = k_DefaultBlendShapeFeatureId,
                Required = true,
                Shapes = shapes
            };
            return true;
        }

        private static bool HasUnusableUnityFrameWeights(BlendShapeWrapper wrapper)
        {
            int fbxCount = wrapper?.FbxBlendShapeData?.Frames.Count ?? 0;
            int unityCount = wrapper?.UnityBlendShapeData?.Frames.Count ?? 0;
            return fbxCount > 0 && unityCount > 0 &&
                   !TryGetValidUnityFrameWeights(wrapper.UnityBlendShapeData, fbxCount, out _);
        }

        private static double[] ResolveEffectiveFrameWeights(
            FbxBlendShapeData fbxData,
            UnityBlendShapeData unityData,
            out bool usedCustomUnityWeights)
        {
            usedCustomUnityWeights = false;
            int frameCount = fbxData?.Frames.Count ?? 0;
            if (frameCount <= 0)
            {
                return Array.Empty<double>();
            }

            if (TryGetValidUnityFrameWeights(unityData, frameCount, out var customWeights))
            {
                usedCustomUnityWeights = true;
                return customWeights;
            }

            var evenlySpaced = new double[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                evenlySpaced[f] = 100d * (f + 1) / frameCount;
            }

            return evenlySpaced;
        }

        private static bool TryGetValidUnityFrameWeights(
            UnityBlendShapeData unityData,
            int expectedFrameCount,
            out double[] weights)
        {
            weights = null;
            if (unityData == null || expectedFrameCount <= 0 || unityData.Frames.Count != expectedFrameCount)
            {
                return false;
            }

            var result = new double[expectedFrameCount];
            float prev = float.NegativeInfinity;
            for (int i = 0; i < expectedFrameCount; i++)
            {
                var frame = unityData.Frames[i];
                if (frame == null)
                {
                    return false;
                }

                float w = frame.FrameWeight;
                if (float.IsNaN(w) || float.IsInfinity(w) || !(w > prev))
                {
                    return false;
                }

                result[i] = w;
                prev = w;
            }

            weights = result;
            return true;
        }

        private static bool HasNonZeroUnityShadingDeltas(
            UnityBlendShapeData unityData,
            out bool hasNonZeroNormals,
            out bool hasNonZeroTangents)
        {
            hasNonZeroNormals = false;
            hasNonZeroTangents = false;
            if (unityData == null)
            {
                return false;
            }

            var frames = unityData.Frames;
            for (int f = 0; f < frames.Count; f++)
            {
                var frame = frames[f];
                if (frame == null)
                {
                    continue;
                }

                if (!hasNonZeroNormals && HasAnyNonZeroComponent(frame.DeltaNormals))
                {
                    hasNonZeroNormals = true;
                }

                if (!hasNonZeroTangents && HasAnyNonZeroComponent(frame.DeltaTangents))
                {
                    hasNonZeroTangents = true;
                }

                if (hasNonZeroNormals && hasNonZeroTangents)
                {
                    return true;
                }
            }

            return hasNonZeroNormals || hasNonZeroTangents;
        }

        private static bool HasAnyNonZeroComponent(IReadOnlyList<Vector3> vectors)
        {
            if (vectors == null)
            {
                return false;
            }

            for (int i = 0; i < vectors.Count; i++)
            {
                var v = vectors[i];
                if (v.x != 0f || v.y != 0f || v.z != 0f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AreBlendShapeWrappersEquivalent(BlendShapeWrapper a, BlendShapeWrapper b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null)
            {
                return false;
            }

            if (!AreFbxBlendShapePayloadsEquivalent(a.FbxBlendShapeData, b.FbxBlendShapeData))
            {
                return false;
            }

            int fbxFrameCount = a.FbxBlendShapeData?.Frames.Count ?? 0;
            if (fbxFrameCount == 0)
            {
                bool aHasUnity = (a.UnityBlendShapeData?.Frames.Count ?? 0) > 0;
                bool bHasUnity = (b.UnityBlendShapeData?.Frames.Count ?? 0) > 0;
                return aHasUnity == bHasUnity && !aHasUnity;
            }

            double[] aw = ResolveEffectiveFrameWeights(a.FbxBlendShapeData, a.UnityBlendShapeData, out _);
            double[] bw = ResolveEffectiveFrameWeights(b.FbxBlendShapeData, b.UnityBlendShapeData, out _);
            if (aw.Length != bw.Length)
            {
                return false;
            }

            for (int i = 0; i < aw.Length; i++)
            {
                if (!aw[i].Equals(bw[i]))
                {
                    return false;
                }
            }

            HasNonZeroUnityShadingDeltas(a.UnityBlendShapeData, out bool aNormals, out bool aTangents);
            HasNonZeroUnityShadingDeltas(b.UnityBlendShapeData, out bool bNormals, out bool bTangents);
            return aNormals == bNormals && aTangents == bTangents;
        }

        private static bool AreFbxBlendShapePayloadsEquivalent(FbxBlendShapeData a, FbxBlendShapeData b)
        {
            var aFrames = a?.Frames ?? Array.Empty<FbxBlendShapeFrame>();
            var bFrames = b?.Frames ?? Array.Empty<FbxBlendShapeFrame>();
            if (aFrames.Count != bFrames.Count)
            {
                return false;
            }

            for (int f = 0; f < aFrames.Count; f++)
            {
                var fa = aFrames[f];
                var fb = bFrames[f];
                if (ReferenceEquals(fa, fb))
                {
                    continue;
                }

                if (fa == null || fb == null ||
                    fa.PointIndices.Count != fb.PointIndices.Count ||
                    fa.DeltaControlPoints.Count != fb.DeltaControlPoints.Count)
                {
                    return false;
                }

                for (int i = 0; i < fa.PointIndices.Count; i++)
                {
                    if (fa.PointIndices[i] != fb.PointIndices[i])
                    {
                        return false;
                    }
                }

                for (int i = 0; i < fa.DeltaControlPoints.Count; i++)
                {
                    var va = fa.DeltaControlPoints[i];
                    var vb = fb.DeltaControlPoints[i];
                    if (va == null || vb == null)
                    {
                        if (!ReferenceEquals(va, vb)) return false;
                        continue;
                    }

                    if (!va.X.Equals(vb.X) || !va.Y.Equals(vb.Y) || !va.Z.Equals(vb.Z))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SequenceEqualsOrdinal(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryBuildFramePayload(
            string nodePath,
            string shapeName,
            int frameIndex,
            int domainCount,
            FbxBlendShapeFrame legacyFrame,
            List<LegacyMigrationDiagnostic> diagnostics,
            out BlendShapeFramePayload payload)
        {
            payload = null;
            var rawIndices = legacyFrame.PointIndices;
            var rawDeltas = legacyFrame.DeltaControlPoints;
            int pairCount = Math.Min(rawIndices.Count, rawDeltas.Count);
            if (rawIndices.Count != rawDeltas.Count)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Warning,
                    "LEG_MIG_FRAME_LENGTH_MISMATCH",
                    $"BlendShape '{shapeName}' frame {frameIndex} on '{nodePath}' had mismatched index/delta counts ({rawIndices.Count} vs {rawDeltas.Count}); truncated to {pairCount}."));
            }

            if (pairCount == 0)
            {
                payload = new BlendShapeFramePayload
                {
                    DeltaPositions = new IndexedBuffer<Vector3d>(
                        IndexDomain.ControlPoint,
                        domainCount,
                        Array.Empty<int>(),
                        Array.Empty<Vector3d>()),
                    DeltaNormals = null
                };
                return true;
            }

            var sparseByIndex = new SortedDictionary<int, Vector3d>();
            int droppedNonFinite = 0;
            for (int i = 0; i < pairCount; i++)
            {
                int cpIndex = rawIndices[i];
                if (cpIndex < 0 || cpIndex >= domainCount)
                {
                    diagnostics.Add(new LegacyMigrationDiagnostic(
                        LegacyMigrationSeverity.Error,
                        "LEG_MIG_INDEX_OUT_OF_RANGE",
                        $"BlendShape '{shapeName}' frame {frameIndex} on '{nodePath}' referenced control point {cpIndex} outside mesh domain [0, {domainCount})."));
                    return false;
                }

                var raw = rawDeltas[i];
                if (raw == null)
                {
                    continue;
                }

                var delta = new Vector3d(raw.X, raw.Y, raw.Z);
                if (double.IsNaN(delta.x) || double.IsInfinity(delta.x) ||
                    double.IsNaN(delta.y) || double.IsInfinity(delta.y) ||
                    double.IsNaN(delta.z) || double.IsInfinity(delta.z))
                {
                    droppedNonFinite++;
                    continue;
                }

                if (delta.x == 0d && delta.y == 0d && delta.z == 0d)
                {
                    sparseByIndex.Remove(cpIndex);
                    continue;
                }

                sparseByIndex[cpIndex] = delta;
            }

            if (droppedNonFinite > 0)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Warning,
                    "LEG_MIG_NONFINITE_DELTA",
                    $"Filtered {droppedNonFinite} non-finite position delta(s) in blendshape '{shapeName}' frame {frameIndex} on '{nodePath}'."));
            }

            int count = sparseByIndex.Count;
            int[] indices = count == 0 ? Array.Empty<int>() : new int[count];
            Vector3d[] values = count == 0 ? Array.Empty<Vector3d>() : new Vector3d[count];
            int cursor = 0;
            foreach (var kv in sparseByIndex)
            {
                indices[cursor] = kv.Key;
                values[cursor] = kv.Value;
                cursor++;
            }

            payload = new BlendShapeFramePayload
            {
                DeltaPositions = new IndexedBuffer<Vector3d>(
                    IndexDomain.ControlPoint,
                    domainCount,
                    indices,
                    values),
                DeltaNormals = null
            };
            return true;
        }

        private static bool TryBuildEmbeddedMapping(
            string nodePath,
            IFbxMesh fbxMesh,
            FbxSceneMetadata sceneMetadata,
            FbxUnityImportSettings importSettings,
            Dictionary<string, Mesh> unityMeshesByPath,
            List<LegacyMigrationDiagnostic> diagnostics,
            out VertexMappingEntry entry)
        {
            entry = null;
            if (!unityMeshesByPath.TryGetValue(nodePath, out var unityMesh) || unityMesh == null)
            {
                return false;
            }

            if (!UnityMeshReaderUtility.TryReadMeshSnapshot(unityMesh, out var snapshot))
            {
                return false;
            }

            var hints = FbxInverseMapHints.FromImportSettings(importSettings);
            var mapped = FbxUnityInverseMapper.Map(fbxMesh, snapshot, hints);
            if (!mapped.Succeeded || mapped.IsBlendShapeSensitive || mapped.ExceededLayoutDfsBudget)
            {
                return false;
            }

            try
            {
                entry = VertexMappingCodec.Build(
                    snapshot,
                    mapped,
                    fbxMesh.ControlPointCount,
                    sceneMetadata,
                    nodePath);
                return entry != null;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryResolveFbxMesh(
            UfbxScene scene,
            Dictionary<string, Mesh> unityMeshesByPath,
            LegacyMeshData legacyMesh,
            int maxControlPointIndex,
            out string resolvedNodePath,
            out IFbxMesh resolvedMesh,
            out string failureCode,
            out string failureMessage)
        {
            resolvedNodePath = string.Empty;
            resolvedMesh = null;
            failureCode = "LEG_MIG_MESH_NOT_FOUND";
            failureMessage = $"Mesh '{legacyMesh.MeshName}' was not found in the assigned FBX.";

            string rawName = legacyMesh.MeshName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(rawName))
            {
                return false;
            }

            string normalized = FbxNameUtility.NormalizePath(rawName);
            bool isExplicitPath = normalized.IndexOf('/') >= 0;
            string leafQuery = ExtractLeafName(normalized);
            var candidates = new List<(string NodePath, IFbxMesh Mesh)>();
            var allNodes = scene.Nodes;
            for (int i = 0; i < allNodes.Count; i++)
            {
                var node = allNodes[i];
                var candidate = node?.Mesh;
                if (node == null || node.IsRoot || candidate == null || candidate.ControlPointCount <= maxControlPointIndex)
                {
                    continue;
                }

                string candPath = FbxNameUtility.NormalizePath(node.Path);
                if (isExplicitPath)
                {
                    if (string.Equals(candPath, normalized, StringComparison.Ordinal))
                    {
                        candidates.Add((candPath, candidate));
                    }
                    continue;
                }

                string candLeaf = ExtractLeafName(candPath);
                if (string.Equals(candLeaf, leafQuery, StringComparison.Ordinal) ||
                    string.Equals(node.Name, rawName, StringComparison.Ordinal) ||
                    string.Equals(candidate.Name, rawName, StringComparison.Ordinal))
                {
                    candidates.Add((candPath, candidate));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            if (candidates.Count == 1)
            {
                resolvedNodePath = candidates[0].NodePath;
                resolvedMesh = candidates[0].Mesh;
                return true;
            }

            // Disambiguate multiple same-name FBX nodes first by serialized Unity Mesh reference,
            // then by 1.0.4's exact Unity vertex count + structural vertex-array hash fingerprint.
            if (legacyMesh.ReferenceUnityMesh != null)
            {
                var refMatches = candidates
                    .Where(c => unityMeshesByPath.TryGetValue(c.NodePath, out var unityMesh) && unityMesh == legacyMesh.ReferenceUnityMesh)
                    .ToList();
                if (refMatches.Count == 1)
                {
                    resolvedNodePath = refMatches[0].NodePath;
                    resolvedMesh = refMatches[0].Mesh;
                    return true;
                }
            }

            if (legacyMesh.VertexCount > 0 && legacyMesh.VerticesHash != 0)
            {
                var fingerprintMatches = candidates
                    .Where(c => unityMeshesByPath.TryGetValue(c.NodePath, out var unityMesh) &&
                                unityMesh != null &&
                                unityMesh.vertexCount == legacyMesh.VertexCount &&
                                ComputeLegacyVerticesHash(unityMesh) == legacyMesh.VerticesHash)
                    .ToList();
                if (fingerprintMatches.Count == 1)
                {
                    resolvedNodePath = fingerprintMatches[0].NodePath;
                    resolvedMesh = fingerprintMatches[0].Mesh;
                    return true;
                }
            }

            failureCode = "LEG_MIG_AMBIGUOUS_MESH";
            failureMessage =
                $"Ambiguous FBX mesh match for '{rawName}'; multiple candidate nodes matched: {string.Join(", ", candidates.Select(c => "'" + c.NodePath + "'"))}.";
            return false;
        }

        private static int ComputeLegacyVerticesHash(Mesh mesh)
        {
            return ((IStructuralEquatable)mesh.vertices).GetHashCode(EqualityComparer<Vector3>.Default);
        }

        private static int ComputeMaxReferencedControlPointIndex(LegacyMeshData legacyMesh)
        {
            int maxIndex = -1;
            var shapes = legacyMesh.RawBlendShapes;
            for (int s = 0; s < shapes.Count; s++)
            {
                var fbx = shapes[s]?.FbxBlendShapeData;
                if (fbx == null) continue;
                var frames = fbx.Frames;
                for (int f = 0; f < frames.Count; f++)
                {
                    var indices = frames[f]?.PointIndices;
                    if (indices == null) continue;
                    for (int i = 0; i < indices.Count; i++)
                    {
                        if (indices[i] > maxIndex)
                        {
                            maxIndex = indices[i];
                        }
                    }
                }
            }

            return maxIndex;
        }

        private static Dictionary<string, List<BlendShapeSelectionSO>> LoadLegacySelectionSubAssets(BlendShapeDataSO legacyAsset)
        {
            var result = new Dictionary<string, List<BlendShapeSelectionSO>>(StringComparer.Ordinal);
            string assetPath = AssetDatabase.GetAssetPath(legacyAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                return result;
            }

            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
            for (int i = 0; i < subAssets.Length; i++)
            {
                if (subAssets[i] is BlendShapeSelectionSO selection)
                {
                    string key = selection.MeshName;
                    if (!result.TryGetValue(key, out var list))
                    {
                        list = new List<BlendShapeSelectionSO>();
                        result[key] = list;
                    }

                    list.Add(selection);
                }
            }

            foreach (var kv in result)
            {
                kv.Value.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
            }

            return result;
        }

        private static bool TryApplyMigratedImportSettings(
            BlendShapeDataSO legacyAsset,
            GameObject targetFbx,
            string outputAssetPath,
            BlendShareAsset importedAsset,
            Dictionary<string, MeshSettingsMigrationSeed> meshSettingsSeeds,
            List<LegacyMigrationDiagnostic> diagnostics)
        {
            string guid = AssetDatabase.AssetPathToGUID(outputAssetPath);
            if (string.IsNullOrEmpty(guid))
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_SETTINGS_SAVE_FAILED",
                    $"Could not resolve AssetDatabase GUID for '{outputAssetPath}' to persist migrated settings."));
                return false;
            }

            // Sync session baseline with the newly imported asset on disk and replace all per-mesh settings
            // so retrying an upgrade never retains obsolete mutes, order overrides, or presets from a prior run.
            BlendShareSettingsSession.ReloadFromDiskAndDiscardConflict(guid);
            var buffer = BlendShareSettingsSession.GetOrCreateBuffer(importedAsset);
            if (buffer?.Settings == null)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_SETTINGS_SAVE_FAILED",
                    $"Could not initialize settings session buffer for '{outputAssetPath}'."));
                return false;
            }

            buffer.Settings.DefaultTargetFbx = targetFbx;
            buffer.Settings.DefaultGeneratedAssetName = legacyAsset.DefaultGeneratedAssetName;
            buffer.Settings.Meshes.Clear();

            foreach (var kv in meshSettingsSeeds)
            {
                string meshId = kv.Key;
                var seed = kv.Value;
                if (seed == null ||
                    (seed.DisabledShapeIds.Count == 0 &&
                     seed.OrderOverrides.Count == 0 &&
                     seed.SelectionSets.Count == 0))
                {
                    continue;
                }

                var bsSettings = new BlendShapeFeatureSettings
                {
                    FeatureId = k_DefaultBlendShapeFeatureId,
                    HasWorkingOverride = seed.DisabledShapeIds.Count > 0 || seed.OrderOverrides.Count > 0
                };
                bsSettings.DisabledShapeIds.AddRange(seed.DisabledShapeIds);
                bsSettings.OrderOverrides.AddRange(seed.OrderOverrides);
                bsSettings.SelectionSets.AddRange(seed.SelectionSets);

                var meshSettings = new BlendShareMeshImportSettings { MeshId = meshId };
                meshSettings.Features.Add(bsSettings);
                buffer.Settings.Meshes.Add(meshSettings);
            }

            BlendShareSettingsSession.NotifyBufferMutated(buffer);
            var commit = BlendShareSettingsSession.Flush(guid);
            if (!commit.Success)
            {
                diagnostics.Add(new LegacyMigrationDiagnostic(
                    LegacyMigrationSeverity.Error,
                    "LEG_MIG_SETTINGS_SAVE_FAILED",
                    $"Failed to persist migrated settings to '{outputAssetPath}.meta' ({commit.Status}): {commit.Message}"));
                return false;
            }

            return true;
        }

        private static Dictionary<string, Mesh> CollectUnityMeshesByPath(GameObject root)
        {
            var map = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            if (root == null)
            {
                return map;
            }

            var rootTransform = root.transform;
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                var smr = skinned[i];
                if (smr == null || smr.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(smr.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, smr.sharedMesh);
            }

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                string path = GetRelativeTransformPath(mf.transform, rootTransform);
                if (!map.ContainsKey(path)) map.Add(path, mf.sharedMesh);
            }

            return map;
        }

        private static string GetRelativeTransformPath(Transform target, Transform root)
        {
            if (target == null || target == root)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            for (var cur = target; cur != null && cur != root; cur = cur.parent)
            {
                parts.Add(cur.name);
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }

        private static string ExtractLeafName(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return string.Empty;
            }

            int slash = normalizedPath.LastIndexOf('/');
            return slash >= 0 ? normalizedPath.Substring(slash + 1) : normalizedPath;
        }

        private static string MakeUniqueId(string candidateBase, HashSet<string> seenIds)
        {
            if (seenIds.Add(candidateBase))
            {
                return candidateBase;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{candidateBase}_{suffix}";
                if (seenIds.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string MakeStableFrameId(double weightPercent, HashSet<string> seenFrameIds)
        {
            double normalized = weightPercent == 0d ? 0d : weightPercent;
            string baseId = "w" + normalized.ToString("0.######", CultureInfo.InvariantCulture);
            if (seenFrameIds.Add(baseId))
            {
                return baseId;
            }

            string exactId = "w" + normalized.ToString("G17", CultureInfo.InvariantCulture);
            if (seenFrameIds.Add(exactId))
            {
                return exactId;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{exactId}_{suffix}";
                if (seenFrameIds.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private sealed class MeshSettingsMigrationSeed
        {
            public List<string> DisabledShapeIds { get; } = new List<string>();
            public List<string> OrderOverrides { get; } = new List<string>();
            public List<BlendShapeSelectionSet> SelectionSets { get; } = new List<BlendShapeSelectionSet>();
        }
    }
}
