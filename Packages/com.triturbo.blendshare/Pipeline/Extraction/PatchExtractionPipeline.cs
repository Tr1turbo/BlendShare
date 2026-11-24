using System;
using System.Collections.Generic;
using System.IO;
using Triturbo.BlendShare.Container.Format;
using Triturbo.BlendShare.Container.Serialization;
using Triturbo.BlendShare.EditorAssets.Binding;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;
using Triturbo.BlendShare.Fbx.Unity;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Extraction
{
    /// <summary>
    /// Coordinates BlendShare 2.0 feature extraction into a <see cref="PatchDocument"/> in <c>CanonicalMeshLocalV1</c>
    /// and optional atomic <c>BLSHARE1</c> v5 file writing. Zero <c>Autodesk.Fbx</c> dependency.
    /// </summary>
    public sealed class PatchExtractionPipeline
    {
        public PatchExtractionResult Extract(PatchExtractionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var diagnostics = new List<ExtractionDiagnostic>();
            if (request.SourceScene == null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_NO_SOURCE_SCENE",
                    "Source FBX scene is required."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            if (request.OriginScene == null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_NO_ORIGIN_SCENE",
                    "Origin FBX scene is required."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            if (request.Meshes == null || request.Meshes.Count == 0)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_NO_MESHES",
                    "At least one mesh extraction request is required."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            CanonicalMeshSpace.SanitizeMetadata(
                request.OriginScene.Metadata,
                out var originAxes,
                out double originUnitMeters,
                out bool originSpaceFallback);
            if (originSpaceFallback)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    "PIPE_EXT_ORIGIN_SPACE_FALLBACK",
                    "Origin FBX scene had invalid/unknown coordinate axes or unit scale; fell back to CanonicalMeshLocalV1 defaults."));
            }

            FbxMatrix4x4 originRawToCanonical = CanonicalMeshSpace.BuildRawToCanonical(
                originAxes,
                originUnitMeters,
                out _);
            if (!FbxAffineSpaceTransform.TryCreate(originRawToCanonical, out var originSpaceTransform))
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_SINGULAR_ORIGIN_SPACE",
                    "Origin FBX raw-to-canonical space transform is singular."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            FbxMatrix4x4 sourceRawToCanonical = CanonicalMeshSpace.BuildRawToCanonical(
                request.SourceScene.Metadata,
                out bool sourceSpaceFallback);
            if (sourceSpaceFallback)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    "PIPE_EXT_SOURCE_SPACE_FALLBACK",
                    "Source FBX scene had invalid/unknown coordinate axes or unit scale; fell back to CanonicalMeshLocalV1 defaults."));
            }

            var extractors = FeatureExtractorRegistry.GetAllOrdered();
            var optionsSet = request.FeatureOptions ?? new ExtractionOptionsSet();
            var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
            var extractedMeshes = new List<MeshDocument>(request.Meshes.Count);
            bool hasError = false;

            for (int m = 0; m < request.Meshes.Count; m++)
            {
                var meshReq = request.Meshes[m];
                if (meshReq == null)
                {
                    continue;
                }

                if (!seenMeshIds.Add(meshReq.MeshId))
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_DUP_MESH_ID",
                        $"Duplicate MeshId '{meshReq.MeshId}' in extraction request.",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                var rawOriginMesh = request.OriginScene.FindMeshByNodePath(meshReq.NodePath);
                if (rawOriginMesh == null)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_ORIGIN_MESH_NOT_FOUND",
                        $"Origin FBX mesh not found at node path '{meshReq.NodePath}'.",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                var rawSourceMesh = request.SourceScene.FindMeshByNodePath(meshReq.NodePath);
                if (rawSourceMesh == null)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_SOURCE_MESH_NOT_FOUND",
                        $"Source FBX mesh not found at node path '{meshReq.NodePath}'.",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                var compatibility = FbxTopologyUtility.EvaluateCompatibility(rawOriginMesh, rawSourceMesh);
                if (!compatibility.IsCompatible)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_TOPOLOGY_INCOMPATIBLE",
                        $"Mesh '{meshReq.NodePath}' topology is incompatible ({compatibility.State}: origin={compatibility.OriginControlPointCount}, source={compatibility.SourceControlPointCount}).",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                if (compatibility.HasWarning)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Warning,
                        "PIPE_EXT_TOPOLOGY_EXTENDED",
                        $"Mesh '{meshReq.NodePath}' is compatible with warnings ({compatibility.State}: origin={compatibility.OriginControlPointCount}, source={compatibility.SourceControlPointCount}).",
                        meshReq.MeshId));
                }

                FbxMatrix4x4 sourceTotalMatrix = sourceRawToCanonical * meshReq.SourceToOriginCanonicalOffset;
                if (!FbxAffineSpaceTransform.TryCreate(sourceTotalMatrix, out var sourceSpaceTransform))
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_SINGULAR_SOURCE_OFFSET",
                        $"Source-to-canonical alignment transform for mesh '{meshReq.NodePath}' is singular or non-affine.",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                IReadOnlyList<int[]> weldGroups = meshReq.WeldGroupsOverride ??
                                                  (request.WeldGroupResolver != null ? request.WeldGroupResolver(rawOriginMesh) : null);

                var normalizedOriginMesh = SourceMeshNormalizer.Normalize(rawOriginMesh, originSpaceTransform, weldGroups: null);
                var normalizedSourceMesh = SourceMeshNormalizer.Normalize(rawSourceMesh, sourceSpaceTransform, weldGroups);

                var provenance = new MeshSpaceProvenance
                {
                    FrameVersion = MeshSpaceProvenance.CanonicalMeshLocalV1,
                    OriginRawMetersPerUnit = originUnitMeters,
                    OriginRightAxis = originAxes.Right,
                    OriginUpAxis = originAxes.Up,
                    OriginFrontAxis = originAxes.Front,
                    OriginRawToCanonical = originRawToCanonical
                };

                var context = new MeshExtractionContext(
                    meshReq.MeshId,
                    meshReq.NodePath,
                    meshReq.DisplayName,
                    request.SourceScene,
                    request.OriginScene,
                    rawSourceMesh,
                    rawOriginMesh,
                    normalizedSourceMesh,
                    normalizedOriginMesh,
                    compatibility,
                    provenance,
                    request.EncodeOptions);

                var meshDoc = new MeshDocument
                {
                    MeshId = meshReq.MeshId,
                    Name = meshReq.DisplayName,
                    NodePath = meshReq.NodePath,
                    Topology = new TopologyDigestRecord
                    {
                        Hash = compatibility.OriginDigest.Hash,
                        ControlPointCount = compatibility.OriginDigest.ControlPointCount,
                        FaceCount = compatibility.OriginDigest.FaceCount,
                        IsValid = compatibility.OriginDigest.IsValid
                    },
                    SpaceProvenance = provenance
                };

                if (request.OriginBindingBuilder != null)
                {
                    var originBinding = request.OriginBindingBuilder(meshReq, rawOriginMesh, request.OriginScene.Metadata);
                    if (originBinding != null)
                    {
                        meshDoc.OriginBinding = LazyPayload<OriginBindingDocument>.FromValue(originBinding);
                    }
                }

                var seenFeatureIds = new HashSet<string>(StringComparer.Ordinal);
                for (int e = 0; e < extractors.Count; e++)
                {
                    var extractor = extractors[e];
                    if (!optionsSet.TryGet(extractor.FeatureTypeId, out var featureOptions) || featureOptions == null)
                    {
                        continue;
                    }

                    if (!featureOptions.ShouldExtractMesh(meshReq.NodePath))
                    {
                        continue;
                    }

                    var featureResult = extractor.Extract(context, featureOptions);
                    if (featureResult.Diagnostics != null)
                    {
                        for (int d = 0; d < featureResult.Diagnostics.Count; d++)
                        {
                            diagnostics.Add(featureResult.Diagnostics[d]);
                        }
                    }

                    if (featureResult.Status == FeatureExtractionStatus.Failed)
                    {
                        hasError = true;
                        continue;
                    }

                    if (featureResult.Status != FeatureExtractionStatus.Succeeded || featureResult.Feature == null)
                    {
                        continue;
                    }

                    var feature = featureResult.Feature;
                    if (string.IsNullOrWhiteSpace(feature.FeatureId) || !seenFeatureIds.Add(feature.FeatureId))
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Error,
                            "PIPE_EXT_INVALID_FEATURE_ID",
                            $"Extractor '{extractor.FeatureTypeId}' produced empty or duplicate FeatureId '{feature.FeatureId}'.",
                            meshReq.MeshId,
                            extractor.FeatureTypeId));
                        hasError = true;
                        continue;
                    }

                    var valResult = new FeatureValidationResult();
                    if (FeatureValidatorRegistry.Default.TryGetValidator(feature.TypeId, out var validator))
                    {
                        validator.Validate(
                            feature,
                            new FeatureValidationContext(
                                meshReq.MeshId,
                                rawOriginMesh.ControlPointCount,
                                rawOriginMesh.FaceCornerCount,
                                payloadSource: null,
                                validatePayloads: true),
                            valResult);
                    }

                    for (int v = 0; v < valResult.Issues.Count; v++)
                    {
                        var issue = valResult.Issues[v];
                        diagnostics.Add(new ExtractionDiagnostic(
                            issue.Severity == FeatureValidationSeverity.Error
                                ? ExtractionDiagnosticSeverity.Error
                                : ExtractionDiagnosticSeverity.Warning,
                            issue.Code,
                            issue.Message,
                            meshReq.MeshId,
                            extractor.FeatureTypeId));
                    }

                    if (!valResult.IsValid)
                    {
                        hasError = true;
                        continue;
                    }

                    meshDoc.Features.Add(feature);
                }

                if (meshDoc.Features.Count > 0)
                {
                    extractedMeshes.Add(meshDoc);
                }
            }

            if (hasError)
            {
                return new PatchExtractionResult(false, null, diagnostics);
            }

            if (extractedMeshes.Count == 0)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_NO_FEATURES_EXTRACTED",
                    "Extraction produced zero mesh features."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            string patchId = string.IsNullOrWhiteSpace(request.PatchId)
                ? Guid.NewGuid().ToString("N")
                : request.PatchId.Trim();
            string displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? patchId
                : request.DisplayName.Trim();

            var doc = new PatchDocument
            {
                PatchId = patchId,
                DisplayName = displayName,
                Meshes = extractedMeshes
            };

            return new PatchExtractionResult(true, doc, diagnostics);
        }

        /// <summary>
        /// Extracts a <see cref="PatchDocument"/> and atomically writes a <c>BLSHARE1</c> v5 file to <paramref name="outputFilePath"/>.
        /// </summary>
        public PatchExtractionResult ExtractAndWriteFile(
            PatchExtractionRequest request,
            string outputFilePath,
            out ContainerToc writtenToc)
        {
            writtenToc = null;
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("Output file path must be non-empty.", nameof(outputFilePath));
            }

            var extraction = Extract(request);
            if (!extraction.Succeeded || extraction.Document == null)
            {
                return extraction;
            }

            string tempPath = null;
            try
            {
                string fullOutputPath = Path.GetFullPath(outputFilePath);
                string directory = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                tempPath = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var containerWriter = new BlendShareContainerWriter();
                ContainerToc localToc;
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    localToc = containerWriter.Write(
                        fs,
                        extraction.Document,
                        request.EncodeOptions,
                        rawCopySource: null,
                        payloadSource: null);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(fullOutputPath))
                {
                    File.Replace(tempPath, fullOutputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullOutputPath);
                }

                writtenToc = localToc;
                return extraction;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    TryDeleteQuietly(tempPath);
                }

                var diags = new List<ExtractionDiagnostic>(extraction.Diagnostics);
                diags.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_WRITE_FAILED",
                    $"Failed to write BLSHARE1 v5 container '{outputFilePath}': {ex.Message}"));
                return new PatchExtractionResult(false, null, diags);
            }
        }

        /// <summary>
        /// Convenience entry point loading Unity FBX assets via <see cref="FbxUnityAssetReader"/> and extracting.
        /// Defaults to <see cref="FbxUnityWeldPredictor"/> when <paramref name="weldGroupResolver"/> is null
        /// and embeds <see cref="OriginBindingDocument"/> when <paramref name="embedOriginBinding"/> is true.
        /// </summary>
        public PatchExtractionResult ExtractFromFbxAssets(
            GameObject sourceFbx,
            GameObject originFbx,
            IReadOnlyList<MeshExtractionRequest> meshes,
            ExtractionOptionsSet featureOptions,
            PayloadEncodeOptions encodeOptions,
            string patchId = null,
            string displayName = null,
            Func<IFbxMesh, IReadOnlyList<int[]>> weldGroupResolver = null,
            bool embedOriginBinding = true)
        {
            var diagnostics = new List<ExtractionDiagnostic>();
            var srcRead = FbxUnityAssetReader.ReadScene(sourceFbx, UfbxLoadOptions.Default);
            if (!srcRead.Success || srcRead.Value == null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_LOAD_SOURCE_FAILED",
                    $"Failed to load source FBX: {srcRead.Message}"));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            using (var sourceScene = srcRead.Value)
            {
                var orgRead = FbxUnityAssetReader.ReadScene(originFbx, UfbxLoadOptions.Default);
                if (!orgRead.Success || orgRead.Value == null)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_LOAD_ORIGIN_FAILED",
                        $"Failed to load origin FBX: {orgRead.Message}"));
                    return new PatchExtractionResult(false, null, diagnostics);
                }

                using (var originScene = orgRead.Value)
                {
                    var originImportSettings = FbxUnityAssetReader.GetImportSettings(originFbx);
                    return Extract(new PatchExtractionRequest
                    {
                        PatchId = patchId ?? string.Empty,
                        DisplayName = displayName ?? (sourceFbx != null ? sourceFbx.name : string.Empty),
                        SourceScene = sourceScene,
                        OriginScene = originScene,
                        Meshes = meshes,
                        FeatureOptions = featureOptions,
                        EncodeOptions = encodeOptions,
                        WeldGroupResolver = weldGroupResolver ?? BuildDefaultWeldGroupResolver(originImportSettings),
                        OriginBindingBuilder = embedOriginBinding ? BuildDefaultOriginBindingBuilder(originFbx, originImportSettings) : null
                    });
                }
            }
        }

        /// <summary>
        /// Convenience entry point loading Unity FBX assets, writing a <c>.blendshare</c> file, and refreshing AssetDatabase if inside Assets/.
        /// </summary>
        public PatchExtractionResult ExtractFromFbxAssetsAndWriteFile(
            GameObject sourceFbx,
            GameObject originFbx,
            IReadOnlyList<MeshExtractionRequest> meshes,
            ExtractionOptionsSet featureOptions,
            PayloadEncodeOptions encodeOptions,
            string outputFilePath,
            out ContainerToc writtenToc,
            string patchId = null,
            string displayName = null,
            Func<IFbxMesh, IReadOnlyList<int[]>> weldGroupResolver = null,
            bool importUnityAsset = true,
            bool embedOriginBinding = true)
        {
            writtenToc = null;
            var diagnostics = new List<ExtractionDiagnostic>();
            var srcRead = FbxUnityAssetReader.ReadScene(sourceFbx, UfbxLoadOptions.Default);
            if (!srcRead.Success || srcRead.Value == null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_LOAD_SOURCE_FAILED",
                    $"Failed to load source FBX: {srcRead.Message}"));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            using (var sourceScene = srcRead.Value)
            {
                var orgRead = FbxUnityAssetReader.ReadScene(originFbx, UfbxLoadOptions.Default);
                if (!orgRead.Success || orgRead.Value == null)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_LOAD_ORIGIN_FAILED",
                        $"Failed to load origin FBX: {orgRead.Message}"));
                    return new PatchExtractionResult(false, null, diagnostics);
                }

                using (var originScene = orgRead.Value)
                {
                    var originImportSettings = FbxUnityAssetReader.GetImportSettings(originFbx);
                    var result = ExtractAndWriteFile(
                        new PatchExtractionRequest
                        {
                            PatchId = patchId ?? string.Empty,
                            DisplayName = displayName ?? (sourceFbx != null ? sourceFbx.name : string.Empty),
                            SourceScene = sourceScene,
                            OriginScene = originScene,
                            Meshes = meshes,
                            FeatureOptions = featureOptions,
                            EncodeOptions = encodeOptions,
                            WeldGroupResolver = weldGroupResolver ?? BuildDefaultWeldGroupResolver(originImportSettings),
                            OriginBindingBuilder = embedOriginBinding ? BuildDefaultOriginBindingBuilder(originFbx, originImportSettings) : null
                        },
                        outputFilePath,
                        out writtenToc);

                    if (result.Succeeded && importUnityAsset && TryToUnityAssetPath(outputFilePath, out string unityAssetPath))
                    {
                        AssetDatabase.ImportAsset(unityAssetPath, ImportAssetOptions.ForceSynchronousImport);
                    }

                    return result;
                }
            }
        }

        private static Func<IFbxMesh, IReadOnlyList<int[]>> BuildDefaultWeldGroupResolver(FbxUnityImportSettings settings)
        {
            var weldOptions = FbxUnityWeldOptions.FromImportSettings(settings);
            if (!weldOptions.WeldVertices)
            {
                return null;
            }

            return rawOriginMesh =>
            {
                if (rawOriginMesh == null || rawOriginMesh.ControlPointCount <= 0)
                {
                    return null;
                }

                var prediction = FbxUnityWeldPredictor.Predict(rawOriginMesh, weldOptions);
                return prediction.HasMultiMemberGroups ? prediction.MultiMemberGroups : null;
            };
        }

        private static Func<MeshExtractionRequest, IFbxMesh, FbxSceneMetadata, OriginBindingDocument> BuildDefaultOriginBindingBuilder(
            GameObject originFbx,
            FbxUnityImportSettings importSettings)
        {
            if (originFbx == null)
            {
                return null;
            }

            var meshByPath = CollectUnityMeshesByPath(originFbx);
            var hints = FbxInverseMapHints.FromImportSettings(importSettings);

            return (meshReq, rawOriginMesh, originMetadata) =>
            {
                if (rawOriginMesh == null || !meshByPath.TryGetValue(meshReq.NodePath, out var unityMesh) || unityMesh == null)
                {
                    return null;
                }

                if (!UnityMeshReaderUtility.TryReadMeshSnapshot(unityMesh, out var snapshot))
                {
                    return null;
                }

                var mapped = FbxUnityInverseMapper.Map(rawOriginMesh, snapshot, hints);
                if (!mapped.Succeeded)
                {
                    return null;
                }

                return OriginBindingCodec.Build(
                    snapshot,
                    mapped,
                    rawOriginMesh.ControlPointCount,
                    originMetadata,
                    meshReq.NodePath);
            };
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
            var curr = target;
            while (curr != null && curr != root)
            {
                parts.Add(curr.name);
                curr = curr.parent;
            }

            parts.Reverse();
            return FbxNameUtility.NormalizePath(string.Join("/", parts));
        }

        internal static bool TryToUnityAssetPath(string filePath, out string assetPath)
        {
            assetPath = null;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                string fullFile = Path.GetFullPath(filePath);
                string fullAssets = Path.GetFullPath(Application.dataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string prefix = fullAssets + Path.DirectorySeparatorChar;

                StringComparison comparison = Application.platform == RuntimePlatform.LinuxEditor
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                if (!fullFile.StartsWith(prefix, comparison))
                {
                    return false;
                }

                string relative = fullFile.Substring(prefix.Length).Replace('\\', '/');
                if (string.IsNullOrEmpty(relative))
                {
                    return false;
                }

                assetPath = "Assets/" + relative;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }
}
