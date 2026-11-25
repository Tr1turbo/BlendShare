#if UNITY_EDITOR
using Triturbo.BlendShare.Fbx.Ufbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Fbx.Unity
{
    /// <summary>
    /// Immutable snapshot of Unity <see cref="ModelImporter"/> settings relevant to FBX welding and inverse mapping.
    /// </summary>
    public readonly struct FbxUnityImportSettings
    {
        /// <summary>Default import settings used when no <see cref="ModelImporter"/> is available.</summary>
        public static readonly FbxUnityImportSettings Default = new(
            fileScale: 1f,
            globalScale: 1f,
            useFileScale: true,
            bakeAxisConversion: false,
            weldVertices: true,
            importBlendShapes: true,
            hasImporter: false);

        /// <summary>Whether the values came from an actual <see cref="ModelImporter"/>.</summary>
        public bool HasImporter { get; }

        /// <summary><see cref="ModelImporter.fileScale"/>.</summary>
        public float FileScale { get; }

        /// <summary><see cref="ModelImporter.globalScale"/>.</summary>
        public float GlobalScale { get; }

        /// <summary><see cref="ModelImporter.useFileScale"/>.</summary>
        public bool UseFileScale { get; }

        /// <summary>Effective vertex import scale (<c>GlobalScale * (UseFileScale ? FileScale : 1)</c>).</summary>
        public float EffectiveScale => GlobalScale * (UseFileScale ? FileScale : 1f);

        /// <summary><see cref="ModelImporter.bakeAxisConversion"/>.</summary>
        public bool BakeAxisConversion { get; }

        /// <summary><see cref="ModelImporter.weldVertices"/>.</summary>
        public bool WeldVertices { get; }

        /// <summary><see cref="ModelImporter.importBlendShapes"/>.</summary>
        public bool ImportBlendShapes { get; }

        /// <summary>Creates an import-settings snapshot.</summary>
        public FbxUnityImportSettings(
            float fileScale,
            float globalScale,
            bool useFileScale,
            bool bakeAxisConversion,
            bool weldVertices,
            bool importBlendShapes,
            bool hasImporter = true)
        {
            FileScale = fileScale;
            GlobalScale = globalScale;
            UseFileScale = useFileScale;
            BakeAxisConversion = bakeAxisConversion;
            WeldVertices = weldVertices;
            ImportBlendShapes = importBlendShapes;
            HasImporter = hasImporter;
        }
    }

    /// <summary>Unity Editor helpers for reading FBX assets and their ModelImporter space settings.</summary>
    public static class FbxUnityAssetReader
    {
        /// <summary>Reads all relevant <see cref="ModelImporter"/> settings for the asset at <paramref name="assetPath"/> once.</summary>
        public static FbxUnityImportSettings GetImportSettings(string assetPath)
        {
            var importer = !string.IsNullOrEmpty(assetPath)
                ? AssetImporter.GetAtPath(assetPath) as ModelImporter
                : null;
            if (importer == null)
            {
                return FbxUnityImportSettings.Default;
            }

            return new FbxUnityImportSettings(
                fileScale: importer.fileScale,
                globalScale: importer.globalScale,
                useFileScale: importer.useFileScale,
                bakeAxisConversion: importer.bakeAxisConversion,
                weldVertices: importer.weldVertices,
                importBlendShapes: importer.importBlendShapes,
                hasImporter: true);
        }

        /// <summary>Reads all relevant <see cref="ModelImporter"/> settings for the asset backing <paramref name="fbxGo"/> once.</summary>
        public static FbxUnityImportSettings GetImportSettings(GameObject fbxGo)
        {
            return fbxGo != null
                ? GetImportSettings(AssetDatabase.GetAssetPath(fbxGo))
                : FbxUnityImportSettings.Default;
        }
        /// <summary>Loads an FBX scene from a Unity imported FBX <see cref="GameObject"/> asset.</summary>
        public static FbxReadResult<UfbxScene> ReadScene(GameObject fbxGo, UfbxLoadOptions? options = null)
        {
            if (fbxGo == null)
            {
                return FbxReadResult<UfbxScene>.Failed(FbxReadStatus.InvalidArgument, "FBX asset is null.");
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxGo);
            return UfbxScene.TryLoad(assetPath, options);
        }

        /// <summary>Finds a mesh attached to the node at the given hierarchy path.</summary>
        public static FbxReadResult<IFbxMesh> FindMesh(IFbxScene scene, string nodePath)
        {
            if (scene == null)
            {
                return FbxReadResult<IFbxMesh>.Failed(FbxReadStatus.InvalidArgument, "FBX scene is null.");
            }

            string normalizedPath = FbxNameUtility.NormalizePath(nodePath);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return FbxReadResult<IFbxMesh>.Failed(
                    FbxReadStatus.InvalidArgument,
                    "FBX node path is empty.");
            }

            var mesh = scene.FindMeshByNodePath(normalizedPath);
            return mesh != null
                ? FbxReadResult<IFbxMesh>.Succeeded(mesh)
                : FbxReadResult<IFbxMesh>.Failed(FbxReadStatus.NodeNotFound, $"FBX mesh node '{normalizedPath}' was not found.");
        }

        /// <summary>Reads <see cref="ModelImporter.fileScale"/> for the asset at <paramref name="assetPath"/>.</summary>
        public static float GetImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null ? importer.fileScale : 1f;
        }

        /// <summary>Reads <see cref="ModelImporter.fileScale"/> for the asset backing <paramref name="fbxGo"/>.</summary>
        public static float GetImportScale(GameObject fbxGo)
        {
            return fbxGo != null ? GetImportScale(AssetDatabase.GetAssetPath(fbxGo)) : 1f;
        }

        /// <summary>
        /// Reads the effective vertex import scale (<c>globalScale * (useFileScale ? fileScale : 1)</c>)
        /// for the asset at <paramref name="assetPath"/>.
        /// </summary>
        public static float GetEffectiveImportScale(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                return 1f;
            }

            float fileFactor = importer.useFileScale ? importer.fileScale : 1f;
            return importer.globalScale * fileFactor;
        }

        /// <summary>
        /// Reads the effective vertex import scale for the asset backing <paramref name="fbxGo"/>.
        /// </summary>
        public static float GetEffectiveImportScale(GameObject fbxGo)
        {
            return fbxGo != null ? GetEffectiveImportScale(AssetDatabase.GetAssetPath(fbxGo)) : 1f;
        }

        /// <summary>Reads <see cref="ModelImporter.bakeAxisConversion"/> for the asset at <paramref name="assetPath"/>.</summary>
        public static bool GetBakeAxisConversion(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer != null && importer.bakeAxisConversion;
        }

        /// <summary>Reads <see cref="ModelImporter.bakeAxisConversion"/> for the asset backing <paramref name="fbxGo"/>.</summary>
        public static bool GetBakeAxisConversion(GameObject fbxGo)
        {
            return fbxGo != null && GetBakeAxisConversion(AssetDatabase.GetAssetPath(fbxGo));
        }

        /// <summary>Reads <see cref="ModelImporter.weldVertices"/> for the asset at <paramref name="assetPath"/> (defaults to <c>true</c>).</summary>
        public static bool GetWeldVertices(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer == null || importer.weldVertices;
        }

        /// <summary>Reads <see cref="ModelImporter.weldVertices"/> for the asset backing <paramref name="fbxGo"/> (defaults to <c>true</c>).</summary>
        public static bool GetWeldVertices(GameObject fbxGo)
        {
            return fbxGo == null || GetWeldVertices(AssetDatabase.GetAssetPath(fbxGo));
        }

        /// <summary>Reads <see cref="ModelImporter.importBlendShapes"/> for the asset at <paramref name="assetPath"/> (defaults to <c>true</c>).</summary>
        public static bool GetImportBlendShapes(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            return importer == null || importer.importBlendShapes;
        }

        /// <summary>Reads <see cref="ModelImporter.importBlendShapes"/> for the asset backing <paramref name="fbxGo"/> (defaults to <c>true</c>).</summary>
        public static bool GetImportBlendShapes(GameObject fbxGo)
        {
            return fbxGo == null || GetImportBlendShapes(AssetDatabase.GetAssetPath(fbxGo));
        }

        /// <summary>Returns the FBX-to-Unity importer scale/axis matrix for the asset at <paramref name="assetPath"/>.</summary>
        public static Matrix4x4 GetImporterSpaceTransform(string assetPath)
        {
            float scale = GetImportScale(assetPath);
            bool bakeAxisConversion = GetBakeAxisConversion(assetPath);
            return bakeAxisConversion
                ? Matrix4x4.Scale(new Vector3(scale, scale, -scale))
                : Matrix4x4.Scale(new Vector3(-scale, scale, scale));
        }

        /// <summary>Returns the FBX-to-Unity importer scale/axis matrix for the asset backing <paramref name="fbxGo"/>.</summary>
        public static Matrix4x4 GetImporterSpaceTransform(GameObject fbxGo)
        {
            return fbxGo != null ? GetImporterSpaceTransform(AssetDatabase.GetAssetPath(fbxGo)) : Matrix4x4.identity;
        }
    }
}
#endif
