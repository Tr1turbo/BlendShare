using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Pipeline.Export;
using Triturbo.BlendShare.Primitives;

namespace Triturbo.BlendShare.FbxExport
{
    public sealed class BlendShapeFbxWriter : IFeatureFbxWriter
    {
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int ExecutionOrder => 100;

        public void RegisterTransactionInputs(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
        }

        private static IReadOnlyList<(FbxFeatureWriteInput Input, BlendShapeEntry Shape)> ResolveWinners(
            IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var ordered = new List<(FbxFeatureWriteInput Input, BlendShapeEntry Shape)>();
            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var feature = input.Feature as BlendShapeFeature;
                if (feature?.Shapes == null || feature.Shapes.Count == 0)
                {
                    continue;
                }

                var meshSettings = input.PatchItem.EffectiveSettings?.Meshes?.Find(
                    m => m != null && string.Equals(m.MeshId, input.MeshHeader.MeshId, StringComparison.Ordinal));
                var settings = meshSettings?.Features?.Find(
                    f => f != null && string.Equals(f.FeatureId, feature.FeatureId, StringComparison.Ordinal)) as BlendShapeFeatureSettings;

                var ids = new List<string>(feature.Shapes.Count);
                var byId = new Dictionary<string, BlendShapeEntry>(feature.Shapes.Count, StringComparer.Ordinal);
                for (int s = 0; s < feature.Shapes.Count; s++)
                {
                    var shape = feature.Shapes[s];
                    if (shape == null || string.IsNullOrEmpty(shape.ShapeId))
                    {
                        continue;
                    }

                    ids.Add(shape.ShapeId);
                    byId[shape.ShapeId] = shape;
                }

                var effectiveIds = settings != null ? settings.ResolveEffectiveShapeIds(ids) : ids;
                for (int k = 0; k < effectiveIds.Count; k++)
                {
                    if (byId.TryGetValue(effectiveIds[k], out var shape))
                    {
                        ordered.Add((input, shape));
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var winners = new List<(FbxFeatureWriteInput Input, BlendShapeEntry Shape)>(ordered.Count);
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var item = ordered[i];
                if (seen.Add(item.Shape.Name ?? string.Empty))
                {
                    winners.Add(item);
                }
            }

            winners.Reverse();
            return winners;
        }

        private static FbxFeatureWriteResult Fail(string code, string message, string patchId = null, string meshId = null) =>
            FbxFeatureWriteResult.Failed(new FbxExportDiagnostic(
                FbxExportDiagnosticSeverity.Error,
                code,
                message,
                patchId: patchId,
                meshId: meshId,
                featureTypeId: BlendShapeFeature.FeatureTypeId));

        private static bool IsValidBuffer(IndexedBuffer<Vector3d> buffer, int controlPointCount)
        {
            if (buffer == null || buffer.Domain != IndexDomain.ControlPoint || buffer.DomainCount != controlPointCount)
            {
                return false;
            }

            for (int i = 0; i < buffer.Count; i++)
            {
                var delta = buffer.Values[i];
                if (double.IsNaN(delta.x) || double.IsInfinity(delta.x) ||
                    double.IsNaN(delta.y) || double.IsInfinity(delta.y) ||
                    double.IsNaN(delta.z) || double.IsInfinity(delta.z))
                {
                    return false;
                }
            }

            return true;
        }

        public FbxFeatureWriteResult Preflight(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            if (context?.TargetMesh == null)
            {
                return Fail("FBX_EXPORT_NO_TARGET_MESH", "Target FBX mesh is null.");
            }

            int count = context.TargetMesh.GetControlPointsCount();
            var winners = ResolveWinners(inputs);
            for (int i = 0; i < winners.Count; i++)
            {
                var (input, shape) = winners[i];
                if (string.IsNullOrWhiteSpace(shape.Name) || shape.Frames == null || shape.Frames.Count == 0)
                {
                    return Fail(
                        "FBX_EXPORT_INVALID_SHAPE",
                        "A blend shape requires a non-empty name and at least one frame.",
                        input.PatchItem.PatchId,
                        input.MeshHeader.MeshId);
                }

                double previous = 0d;
                for (int f = 0; f < shape.Frames.Count; f++)
                {
                    var frame = shape.Frames[f];
                    if (frame == null ||
                        double.IsNaN(frame.Weight) ||
                        double.IsInfinity(frame.Weight) ||
                        !(frame.Weight > previous && frame.Weight <= 100d))
                    {
                        return Fail(
                            "FBX_EXPORT_INVALID_WEIGHT",
                            $"Invalid frame weight for '{shape.Name}'.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }

                    previous = frame.Weight;
                    if (!frame.Payload.HasPayload)
                    {
                        return Fail(
                            "FBX_EXPORT_MISSING_PAYLOAD",
                            $"Blend shape '{shape.Name}' frame '{frame.FrameId}' has no payload.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }

                    using var lease = frame.Payload.Acquire(input.PayloadSource);
                    var payload = lease.Value;
                    if (!IsValidBuffer(payload?.DeltaPositions, count) ||
                        (payload.DeltaNormals != null && !IsValidBuffer(payload.DeltaNormals, count)))
                    {
                        return Fail(
                            "FBX_EXPORT_INVALID_DELTAS",
                            $"Invalid control point deltas for '{shape.Name}'.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }
                }
            }

            return FbxFeatureWriteResult.Succeeded(modifiedMesh: false);
        }

        public FbxFeatureWriteResult Write(FbxExportMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            var mesh = context.TargetMesh;
            var owners = new Dictionary<string, FbxBlendShape>(StringComparer.Ordinal);
            var existingChannelsByName = new Dictionary<string, List<FbxBlendShapeChannel>>(StringComparer.Ordinal);
            var patchIds = new HashSet<string>(inputs.Select(i => i.PatchItem.PatchId), StringComparer.Ordinal);
            bool modified = false;

            for (int i = mesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) - 1; i >= 0; i--)
            {
                var owner = mesh.GetBlendShapeDeformer(i);
                if (owner == null)
                {
                    continue;
                }

                string ownerName = owner.GetName() ?? string.Empty;
                if (patchIds.Contains(ownerName))
                {
                    owner.Destroy();
                    modified = true;
                    continue;
                }

                int channelCount = owner.GetBlendShapeChannelCount();
                for (int j = 0; j < channelCount; j++)
                {
                    var channel = owner.GetBlendShapeChannel(j);
                    if (channel == null)
                    {
                        continue;
                    }

                    string channelName = channel.GetName() ?? string.Empty;
                    if (!existingChannelsByName.TryGetValue(channelName, out var list))
                    {
                        list = new List<FbxBlendShapeChannel>();
                        existingChannelsByName.Add(channelName, list);
                    }

                    list.Add(channel);
                }
            }

            int cpCount = mesh.GetControlPointsCount();
            var baseControlPoints = new FbxVector4[cpCount];
            for (int cp = 0; cp < cpCount; cp++)
            {
                baseControlPoints[cp] = mesh.GetControlPointAt(cp);
            }

            var winners = ResolveWinners(inputs);
            for (int w = 0; w < winners.Count; w++)
            {
                var (input, entry) = winners[w];
                List<FbxBlendShapeChannel> targetChannels;
                if (existingChannelsByName.TryGetValue(entry.Name, out var existingList) && existingList.Count > 0)
                {
                    targetChannels = existingList;
                }
                else
                {
                    string patchId = input.PatchItem.PatchId;
                    if (!owners.TryGetValue(patchId, out var owner))
                    {
                        owner = FbxBlendShape.Create(context.SdkScene, patchId);
                        mesh.AddDeformer(owner);
                        owners.Add(patchId, owner);
                    }

                    var createdChannel = FbxBlendShapeChannel.Create(context.SdkScene, entry.Name);
                    owner.AddBlendShapeChannel(createdChannel);
                    targetChannels = new List<FbxBlendShapeChannel> { createdChannel };
                }

                for (int c = 0; c < targetChannels.Count; c++)
                {
                    var channel = targetChannels[c];
                    for (int s = channel.GetTargetShapeCount() - 1; s >= 0; s--)
                    {
                        channel.RemoveTargetShape(channel.GetTargetShape(s));
                    }

                    for (int f = 0; f < entry.Frames.Count; f++)
                    {
                        var frame = entry.Frames[f];
                        using var lease = frame.Payload.Acquire(input.PayloadSource);
                        var payload = lease.Value;

                        var shape = FbxShape.Create(context.SdkScene, entry.Name);
                        shape.InitControlPoints(cpCount);
                        for (int cp = 0; cp < cpCount; cp++)
                        {
                            shape.SetControlPointAt(baseControlPoints[cp], cp);
                        }

                        var posBuf = payload.DeltaPositions;
                        for (int k = 0; k < posBuf.Count; k++)
                        {
                            int cp = posBuf.Indices[k];
                            var p = baseControlPoints[cp];
                            var d = input.PatchSpace.PositionDeltaToRaw(posBuf.Values[k]);
                            shape.SetControlPointAt(new FbxVector4(p.X + d.x, p.Y + d.y, p.Z + d.z, p.W), cp);
                        }

                        if (payload.DeltaNormals != null && payload.DeltaNormals.Count > 0)
                        {
                            if (!TryWriteNormals(context, input.PatchSpace, shape, payload.DeltaNormals, out string normalError))
                            {
                                return Fail(
                                    "FBX_EXPORT_NORMAL_WRITE_FAILED",
                                    normalError,
                                    input.PatchItem.PatchId,
                                    input.MeshHeader.MeshId);
                            }
                        }

                        if (!channel.AddTargetShape(shape, frame.Weight))
                        {
                            return Fail(
                                "FBX_EXPORT_ADD_TARGET_FAILED",
                                $"Cannot add target shape '{entry.Name}'.",
                                input.PatchItem.PatchId,
                                input.MeshHeader.MeshId);
                        }
                    }
                }

                modified = true;
            }

            return modified ? FbxFeatureWriteResult.Succeeded() : FbxFeatureWriteResult.Skipped();
        }

        private static bool TryWriteNormals(
            FbxExportMeshContext context,
            FbxExportSpace space,
            FbxShape shape,
            IndexedBuffer<Vector3d> deltas,
            out string error)
        {
            error = null;
            var mesh = context.TargetMesh;
            var source = mesh.GetLayer(0)?.GetNormals();
            if (source == null)
            {
                context.Diagnostics.Add(new FbxExportDiagnostic(
                    FbxExportDiagnosticSeverity.Warning,
                    "FBX_EXPORT_MISSING_BASE_NORMALS",
                    $"Skipped normal offsets for '{shape.GetName()}' because the target mesh lacks layer-0 normals.",
                    meshId: context.TargetNodePath,
                    featureTypeId: BlendShapeFeature.FeatureTypeId));
                return true;
            }

            var denseDeltas = deltas.ToDenseArray();
            if (shape.GetLayerCount() == 0)
            {
                shape.CreateLayer();
            }

            var output = FbxLayerElementNormal.Create(shape, string.Empty);
            var mode = source.GetMappingMode();
            output.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            shape.GetLayer(0).SetNormals(output);
            var array = output.GetDirectArray();

            void Append(int cp, FbxVector4 normal)
            {
                var raw = new Vector3d(normal.X, normal.Y, normal.Z);
                var storedDelta = denseDeltas[cp];
                if (storedDelta.sqrMagnitude <= 0d)
                {
                    array.Add(normal);
                    return;
                }

                var deltaRaw = space.TransformNormalDeltaToRaw(raw, storedDelta);
                array.Add(new FbxVector4(raw.x + deltaRaw.x, raw.y + deltaRaw.y, raw.z + deltaRaw.z, 0d));
            }

            if (mode == FbxLayerElement.EMappingMode.eByControlPoint)
            {
                output.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
                var direct = source.GetDirectArray();
                var indices = source.GetIndexArray();
                bool isDirect = source.GetReferenceMode() == FbxLayerElement.EReferenceMode.eDirect;
                int cpCount = mesh.GetControlPointsCount();
                for (int cp = 0; cp < cpCount; cp++)
                {
                    int normalIdx = isDirect ? cp : indices.GetAt(cp);
                    Append(cp, direct.GetAt(normalIdx));
                }

                return true;
            }

            output.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            int polyCount = mesh.GetPolygonCount();
            for (int p = 0; p < polyCount; p++)
            {
                int polySize = mesh.GetPolygonSize(p);
                for (int v = 0; v < polySize; v++)
                {
                    if (!mesh.GetPolygonVertexNormal(p, v, out var normal))
                    {
                        error = $"Missing base polygon-vertex normal at polygon {p}, corner {v} on '{context.TargetNodePath}'.";
                        return false;
                    }

                    Append(mesh.GetPolygonVertex(p, v), normal);
                }
            }

            return true;
        }

        public FbxFeatureWriteResult Verify(FbxExportVerifyMeshContext context, IReadOnlyList<FbxFeatureWriteInput> inputs)
        {
            int count = context.VerifiedMesh.ControlPointCount;
            var channels = context.VerifiedMesh.BlendDeformers
                .SelectMany(d => d.Channels)
                .ToLookup(c => c.Name, StringComparer.Ordinal);

            var winners = ResolveWinners(inputs);
            for (int w = 0; w < winners.Count; w++)
            {
                var (input, entry) = winners[w];
                var matches = channels[entry.Name].ToList();
                if (matches.Count == 0 || matches.Any(m => m.KeyframeCount != entry.Frames.Count))
                {
                    return Fail(
                        "FBX_EXPORT_VERIFY_CHANNEL_MISMATCH",
                        $"BlendShape channel '{entry.Name}' count or keyframe count mismatch.",
                        input.PatchItem.PatchId,
                        input.MeshHeader.MeshId);
                }

                var verifiedChannel = matches[0];
                for (int i = 0; i < entry.Frames.Count; i++)
                {
                    var frame = entry.Frames[i];
                    var target = verifiedChannel.Keyframes[i];
                    if (!(Math.Abs(target.TargetWeight * 100d - frame.Weight) <= 1e-3d))
                    {
                        return Fail(
                            "FBX_EXPORT_VERIFY_WEIGHT_MISMATCH",
                            $"BlendShape '{entry.Name}' frame {i} weight mismatch: expected {frame.Weight}, got {target.TargetWeight * 100d}.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }

                    if (!context.VerifyNumerics)
                    {
                        continue;
                    }

                    using var lease = frame.Payload.Acquire(input.PayloadSource);
                    var payload = lease.Value;
                    var shape = target.Shape;
                    bool verifyNormals = payload.DeltaNormals != null &&
                                         payload.DeltaNormals.Count > 0 &&
                                         context.OriginalReaderMesh.Normals.Exists;
                    if (verifyNormals && !shape.HasNormalOffsets)
                    {
                        return Fail(
                            "FBX_EXPORT_VERIFY_NORMAL_MISMATCH",
                            $"BlendShape '{entry.Name}' frame {i} is missing normal offsets in exported FBX.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }

                    var indices = new int[shape.OffsetCount];
                    var positions = new Vector3d[indices.Length];
                    var normals = verifyNormals ? new Vector3d[indices.Length] : Array.Empty<Vector3d>();
                    if (indices.Length > 0 &&
                        (!shape.TryCopyOffsets(indices, positions, normals, default, out int written) || written != indices.Length))
                    {
                        return Fail(
                            "FBX_EXPORT_VERIFY_POSITION_MISMATCH",
                            $"Failed copying exported offsets for '{entry.Name}'.",
                            input.PatchItem.PatchId,
                            input.MeshHeader.MeshId);
                    }

                    var expected = new Vector3d[count];
                    var sourcePositions = payload.DeltaPositions;
                    for (int j = 0; j < sourcePositions.Count; j++)
                        expected[sourcePositions.Indices[j]] = input.PatchSpace.PositionDeltaToRaw(sourcePositions.Values[j]);

                    var expectedNormals = verifyNormals ? new Vector3d[count] : null;
                    if (verifyNormals)
                    {
                        var baseNormals = context.OriginalReaderMesh.GetFirstCornerNormals();
                        var sourceNormals = payload.DeltaNormals;
                        for (int j = 0; j < sourceNormals.Count; j++)
                        {
                            int ncp = sourceNormals.Indices[j];
                            Vector3d baseNrm = (uint)ncp < (uint)baseNormals.Length ? baseNormals[ncp] : Vector3d.zero;
                            expectedNormals[ncp] = input.PatchSpace.TransformNormalDeltaToRaw(baseNrm, sourceNormals.Values[j]);
                        }
                    }
                    for (int j = 0; j < indices.Length; j++)
                    {
                        int cp = indices[j];
                        if ((uint)cp >= (uint)count)
                        {
                            return Fail(
                                "FBX_EXPORT_VERIFY_POSITION_MISMATCH",
                                $"Out-of-range offset control point {cp} in '{entry.Name}'.",
                                input.PatchItem.PatchId,
                                input.MeshHeader.MeshId);
                        }

                        expected[cp] -= positions[j];
                        if (verifyNormals)
                        {
                            expectedNormals[cp] -= normals[j];
                        }
                    }

                    for (int cp = 0; cp < count; cp++)
                    {
                        double errorMeters = expected[cp].magnitude * context.OriginalReaderScene.Metadata.UnitMeters;
                        if (!(errorMeters <= context.NumericVerificationToleranceMeters))
                        {
                            return Fail(
                                "FBX_EXPORT_VERIFY_POSITION_MISMATCH",
                                $"Position delta mismatch on '{entry.Name}' CP {cp}: err={errorMeters:E3}m > tol={context.NumericVerificationToleranceMeters:E3}m.",
                                input.PatchItem.PatchId,
                                input.MeshHeader.MeshId);
                        }
                    }

                    if (verifyNormals)
                    {
                        const double normalTol = 2e-3d;
                        for (int cp = 0; cp < count; cp++)
                        {
                            if (!(expectedNormals[cp].magnitude <= normalTol))
                            {
                                return Fail(
                                    "FBX_EXPORT_VERIFY_NORMAL_MISMATCH",
                                    $"Normal delta mismatch on '{entry.Name}' CP {cp}: err={expectedNormals[cp].magnitude:E3} > tol={normalTol:E3}.",
                                    input.PatchItem.PatchId,
                                    input.MeshHeader.MeshId);
                            }
                        }
                    }
                }
            }

            return FbxFeatureWriteResult.Succeeded(modifiedMesh: false);
        }
    }
}
