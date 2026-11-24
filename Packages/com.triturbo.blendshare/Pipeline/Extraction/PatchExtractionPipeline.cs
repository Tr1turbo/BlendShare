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
    /// Coordinates BlendShare 2.0 feature extraction into a <see cref="PatchDocument"/> in native Origin FBX
    /// local space (preserving Origin FBX axes and unit scale) and optional atomic <c>BLSHARE1</c> v5 file writing.
    /// Zero <c>Autodesk.Fbx</c> dependency.
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
                    "Origin FBX scene had invalid/unknown coordinate axes or unit scale; fell back to default meter (+X/+Y/+Z) space."));
            }

            CanonicalMeshSpace.SanitizeMetadata(
                request.SourceScene.Metadata,
                out var sourceAxes,
                out double sourceUnitMeters,
                out bool sourceSpaceFallback);
            if (sourceSpaceFallback)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    "PIPE_EXT_SOURCE_SPACE_FALLBACK",
                    "Source FBX scene had invalid/unknown coordinate axes or unit scale; fell back to default meter (+X/+Y/+Z) space."));
            }

            FbxSceneMetadata effectiveOriginMetadata = originSpaceFallback
                ? new FbxSceneMetadata(
                    request.OriginScene.Metadata.FileVersion,
                    originAxes.Up,
                    originUnitMeters,
                    request.OriginScene.Metadata.OriginalUnitMeters,
                    request.OriginScene.Metadata.FramesPerSecond,
                    originAxes)
                : request.OriginScene.Metadata;

            // Scene/world basis transform W: Source raw world -> Origin raw world.
            // Kept strictly separate from per-mesh alignment offsets so shared armatures stay coherent across meshes.
            FbxMatrix4x4 sourceWorldToOriginRaw = CanonicalMeshSpace.BuildSpaceToSpace(
                sourceAxes,
                sourceUnitMeters,
                originAxes,
                originUnitMeters,
                out _,
                out bool isExactSameSpace);
            if (!FbxAffineSpaceTransform.TryCreate(sourceWorldToOriginRaw, out var sourceWorldToStored))
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    "PIPE_EXT_SINGULAR_SOURCE_SPACE",
                    "Source-to-origin coordinate space transform is singular."));
                return new PatchExtractionResult(false, null, diagnostics);
            }

            var extractors = FeatureExtractorRegistry.GetAllOrdered();
            var optionsSet = request.FeatureOptions ?? new ExtractionOptionsSet();
            var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
            var extractedMeshes = new List<MeshDocument>(request.Meshes.Count);
            var armatureAccumulators = new Dictionary<string, ArmatureAccumulator>(StringComparer.Ordinal);
            var armatureOrder = new List<string>();
            bool hasError = false;
            double translationBindTolerance = 1e-4d / originUnitMeters;

            // Collect armature bone references and authoritative cluster binds across ALL meshes first,
            // then finalize ArmatureDocuments deterministically after the mesh loop so mesh order never affects
            // bone bind selection or local/global consistency.
            Action<string, string, IReadOnlyList<IFbxNode>, IReadOnlyDictionary<string, FbxMatrix4x4>, FbxAffineSpaceTransform> registerArmature =
                (armId, rootPath, boneNodes, clusterBindWorldByPath, worldToStored) =>
                {
                    if (string.IsNullOrWhiteSpace(armId) || boneNodes == null) return;
                    if (!armatureAccumulators.TryGetValue(armId, out var acc))
                    {
                        acc = new ArmatureAccumulator(armId);
                        armatureAccumulators.Add(armId, acc);
                        armatureOrder.Add(armId);
                    }

                    var chain = new List<IFbxNode>();
                    for (int i = 0; i < boneNodes.Count; i++)
                    {
                        chain.Clear();
                        var curr = boneNodes[i];
                        while (curr != null && !curr.IsRoot)
                        {
                            chain.Add(curr);
                            curr = curr.Parent;
                        }

                        for (int k = chain.Count - 1; k >= 0; k--)
                        {
                            var node = chain[k];
                            string path = FbxNameUtility.NormalizePath(node.Path);
                            if (string.IsNullOrEmpty(path)) continue;

                            string parentPath = node.Parent != null && !node.Parent.IsRoot
                                ? FbxNameUtility.NormalizePath(node.Parent.Path)
                                : string.Empty;
                            int depth = chain.Count - 1 - k;

                            if (!acc.NodesByPath.ContainsKey(path))
                            {
                                acc.NodesByPath.Add(path, new ArmatureNodeCandidate(path, parentPath, depth, node, worldToStored));
                            }

                            if (clusterBindWorldByPath != null &&
                                clusterBindWorldByPath.TryGetValue(path, out var clusterGlobal))
                            {
                                acc.WeightedPaths.Add(path);
                                if (acc.ClusterBindByPath.TryGetValue(path, out var existingClusterBind))
                                {
                                    if (!MatricesApproximatelyEqual(
                                            existingClusterBind,
                                            clusterGlobal,
                                            linearTolerance: 1e-4d,
                                            translationTolerance: translationBindTolerance))
                                    {
                                        diagnostics.Add(new ExtractionDiagnostic(
                                            ExtractionDiagnosticSeverity.Error,
                                            "PIPE_EXT_ARMATURE_BIND_MISMATCH",
                                            $"Conflicting authoritative skin cluster bind poses for bone '{path}' in armature '{armId}'."));
                                        hasError = true;
                                    }
                                }
                                else
                                {
                                    acc.ClusterBindByPath.Add(path, clusterGlobal);
                                }
                            }
                        }
                    }
                };

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

                // Per-mesh local alignment transform L: Source mesh-local -> Origin raw mesh-local.
                // Use exact IEEE-754 identity checks so tiny non-zero offsets and NaN matrices are never dropped.
                FbxMatrix4x4 sourceMeshTotalMatrix;
                if (!FbxAffineSpaceTransform.IsExactIdentity(meshReq.SourceToOriginRawOffset))
                {
                    if (!FbxAffineSpaceTransform.TryCreate(meshReq.SourceToOriginRawOffset, out _))
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Error,
                            "PIPE_EXT_SINGULAR_SOURCE_OFFSET",
                            $"Source-to-origin raw alignment offset for mesh '{meshReq.NodePath}' is singular, non-finite, or non-affine.",
                            meshReq.MeshId));
                        hasError = true;
                        continue;
                    }

                    sourceMeshTotalMatrix = isExactSameSpace
                        ? meshReq.SourceToOriginRawOffset
                        : sourceWorldToOriginRaw * meshReq.SourceToOriginRawOffset;
                }
                else
                {
                    sourceMeshTotalMatrix = sourceWorldToOriginRaw;
                }

                if (!FbxAffineSpaceTransform.TryCreate(sourceMeshTotalMatrix, out var sourceMeshToStored))
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        "PIPE_EXT_SINGULAR_SOURCE_OFFSET",
                        $"Source-to-origin alignment transform for mesh '{meshReq.NodePath}' is singular or non-affine.",
                        meshReq.MeshId));
                    hasError = true;
                    continue;
                }

                IReadOnlyList<int[]> weldGroups = meshReq.WeldGroupsOverride ??
                                                  (request.WeldGroupResolver != null ? request.WeldGroupResolver(rawOriginMesh) : null);

                // Origin mesh stays natively untouched (Identity transform).
                var normalizedOriginMesh = SourceMeshNormalizer.Normalize(rawOriginMesh, FbxAffineSpaceTransform.Identity, weldGroups: null);
                var normalizedSourceMesh = SourceMeshNormalizer.Normalize(rawSourceMesh, sourceMeshToStored, weldGroups);

                var provenance = MeshSpaceProvenance.FromMetadata(originAxes, originUnitMeters);

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
                    request.EncodeOptions,
                    registerArmature,
                    sourceWorldToStored,
                    sourceMeshToStored);

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
                    var originBinding = request.OriginBindingBuilder(meshReq, rawOriginMesh, effectiveOriginMetadata);
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

            // Finalize ArmatureDocuments deterministically after all meshes have contributed their cluster binds.
            var finalizedArmatures = new List<ArmatureDocument>(armatureOrder.Count);
            armatureOrder.Sort(StringComparer.Ordinal);
            for (int a = 0; a < armatureOrder.Count; a++)
            {
                var acc = armatureAccumulators[armatureOrder[a]];
                var armDoc = new ArmatureDocument
                {
                    ArmatureId = acc.ArmatureId,
                    Name = acc.ArmatureId,
                    RootBonePath = string.Empty,
                    SpaceProvenance = MeshSpaceProvenance.FromMetadata(originAxes, originUnitMeters)
                };

                var candidates = new List<ArmatureNodeCandidate>(acc.NodesByPath.Values);
                candidates.Sort((x, y) =>
                {
                    int cmp = x.Depth.CompareTo(y.Depth);
                    return cmp != 0 ? cmp : string.CompareOrdinal(x.Path, y.Path);
                });

                var boneMap = new Dictionary<string, ArmatureBoneRecord>(candidates.Count, StringComparer.Ordinal);
                for (int i = 0; i < candidates.Count; i++)
                {
                    var cand = candidates[i];
                    FbxMatrix4x4 globalStored;
                    if (acc.ClusterBindByPath.TryGetValue(cand.Path, out var clusterGlobal))
                    {
                        globalStored = clusterGlobal;
                    }
                    else
                    {
                        globalStored = cand.WorldToStored.IsIdentity
                            ? cand.Node.NodeToWorldMatrix
                            : cand.WorldToStored.InverseOffset * cand.Node.NodeToWorldMatrix * cand.WorldToStored.Offset;
                    }

                    FbxMatrix4x4 localStored;
                    if (!string.IsNullOrEmpty(cand.ParentPath) &&
                        boneMap.TryGetValue(cand.ParentPath, out var parentRecord))
                    {
                        if (!parentRecord.GlobalBindMatrix.TryInverse(out var parentInv))
                        {
                            diagnostics.Add(new ExtractionDiagnostic(
                                ExtractionDiagnosticSeverity.Error,
                                "PIPE_EXT_ARMATURE_SINGULAR_PARENT_BIND",
                                $"Parent bone '{cand.ParentPath}' in armature '{acc.ArmatureId}' has a singular global bind matrix."));
                            hasError = true;
                            continue;
                        }

                        localStored = globalStored * parentInv;
                        if (!MatricesApproximatelyEqual(
                                localStored * parentRecord.GlobalBindMatrix,
                                globalStored,
                                linearTolerance: 1e-4d,
                                translationTolerance: translationBindTolerance))
                        {
                            diagnostics.Add(new ExtractionDiagnostic(
                                ExtractionDiagnosticSeverity.Error,
                                "PIPE_EXT_ARMATURE_LOCAL_INCONSISTENT",
                                $"Derived local bind matrix for bone '{cand.Path}' in armature '{acc.ArmatureId}' is not consistent with parent '{cand.ParentPath}'."));
                            hasError = true;
                            continue;
                        }
                    }
                    else
                    {
                        // Top-level bone in the serialized armature has no serialized parent (scene root is identity),
                        // so its local bind matrix equals its global bind matrix.
                        localStored = globalStored;
                    }

                    var record = new ArmatureBoneRecord
                    {
                        Path = cand.Path,
                        ParentPath = cand.ParentPath,
                        LocalBindMatrix = localStored,
                        GlobalBindMatrix = globalStored
                    };
                    boneMap.Add(cand.Path, record);
                    armDoc.Bones.Add(record);
                }

                var weightedList = new List<string>(acc.WeightedPaths);
                weightedList.Sort(StringComparer.Ordinal);
                armDoc.RootBonePath = SkinWeightFeatureExtractor.ComputeLowestCommonAncestorPath(weightedList);
                finalizedArmatures.Add(armDoc);
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
                Meshes = extractedMeshes,
                Armatures = finalizedArmatures
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

        private sealed class ArmatureAccumulator
        {
            public string ArmatureId { get; }
            public Dictionary<string, ArmatureNodeCandidate> NodesByPath { get; } =
                new Dictionary<string, ArmatureNodeCandidate>(StringComparer.Ordinal);
            public Dictionary<string, FbxMatrix4x4> ClusterBindByPath { get; } =
                new Dictionary<string, FbxMatrix4x4>(StringComparer.Ordinal);
            public HashSet<string> WeightedPaths { get; } =
                new HashSet<string>(StringComparer.Ordinal);

            public ArmatureAccumulator(string armatureId)
            {
                ArmatureId = armatureId;
            }
        }

        private readonly struct ArmatureNodeCandidate
        {
            public string Path { get; }
            public string ParentPath { get; }
            public int Depth { get; }
            public IFbxNode Node { get; }
            public FbxAffineSpaceTransform WorldToStored { get; }

            public ArmatureNodeCandidate(
                string path,
                string parentPath,
                int depth,
                IFbxNode node,
                FbxAffineSpaceTransform worldToStored)
            {
                Path = path;
                ParentPath = parentPath;
                Depth = depth;
                Node = node;
                WorldToStored = worldToStored;
            }
        }

        private static bool MatricesApproximatelyEqual(
            FbxMatrix4x4 a,
            FbxMatrix4x4 b,
            double linearTolerance,
            double translationTolerance)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double tol = (r == 3 && c < 3) ? translationTolerance : linearTolerance;
                    if (Math.Abs(a[r, c] - b[r, c]) > tol)
                    {
                        return false;
                    }
                }
            }

            return true;
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
