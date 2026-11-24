using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.EditorAssets.Settings;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Mapping;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Settings;
using Triturbo.BlendShare.Primitives;
using UnityEngine;

namespace Triturbo.BlendShare.Pipeline.Apply
{
    public sealed class BlendShapeUnityApplier : IFeatureUnityApplier
    {
        public string FeatureTypeId => BlendShapeFeature.FeatureTypeId;
        public int ExecutionOrder => 100;

        private sealed class Frame
        {
            public string Name;
            public float Weight;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector3[] Tangents;
        }

        public FeatureApplyResult Apply(UnityMeshApplyContext context, IReadOnlyList<FeatureApplyInput> inputs)
        {
            FeatureApplyInput current = null;
            try
            {
                var mesh = context.WorkingMesh;
                var candidates = new List<(FeatureApplyInput Input, BlendShapeEntry Shape)>();
                var winners = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var input in inputs)
                {
                    current = input;
                    var feature = (BlendShapeFeature)input.Feature;
                    if (feature.Shapes == null) continue;
                    var settings = EffectiveSettingsResolver.FindFeatureSettings(input.EffectiveSettings, input.MeshId,
                        feature.FeatureId) as BlendShapeFeatureSettings;
                    var byId = feature.Shapes.ToDictionary(s => s.ShapeId);
                    var ids = feature.Shapes.Select(s => s.ShapeId).ToList();
                    foreach (var id in settings?.ResolveEffectiveShapeIds(ids) ?? ids)
                    {
                        var shape = byId[id];
                        winners[shape.Name] = candidates.Count;
                        candidates.Add((input, shape));
                    }
                }
                if (winners.Count == 0 && context.PreserveExistingBlendShapes) return FeatureApplyResult.Skipped();

                // Pre-validate winning shapes' bindings, space contracts, and Unity float frame weights before mutating mesh.
                var winningList = new List<(FeatureApplyInput Input, BlendShapeEntry Shape, FbxMatrix4x4 NormalMatrix)>(winners.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    var (input, shape) = candidates[i];
                    if (winners[shape.Name] != i) continue;
                    current = input;
                    var binding = input.VertexBinding;
                    var map = binding.VertexMap;
                    var matrix = binding.SpaceContract.CanonicalToUnityMeshLocal;
                    if (map.SourceDomain != IndexDomain.UnityVertex || map.TargetDomain != IndexDomain.ControlPoint ||
                        map.SourceCount != mesh.vertexCount || binding.SpaceContract.FrameVersion != MeshSpaceProvenance.CanonicalMeshLocalV1 ||
                        !UnitySpaceContractMath.TryBuildNormalDeltaMatrix(matrix, out var normalMatrix))
                        throw new InvalidOperationException("Invalid vertex map or space contract.");

                    float previous = float.NegativeInfinity;
                    foreach (var source in shape.Frames)
                    {
                        float weight = (float)source.Weight;
                        if (!(weight > previous) || float.IsInfinity(weight))
                            throw new InvalidOperationException("Invalid Unity frame weight.");
                        previous = weight;
                    }

                    winningList.Add((input, shape, normalMatrix));
                }

                // Determine whether existing blendshapes need rebuilding (only on name collision or when clearing base shapes).
                bool needRebuild = !context.PreserveExistingBlendShapes;
                if (!needRebuild)
                {
                    for (int s = 0; s < mesh.blendShapeCount; s++)
                    {
                        if (winners.ContainsKey(mesh.GetBlendShapeName(s)))
                        {
                            needRebuild = true;
                            break;
                        }
                    }
                }

                if (needRebuild)
                {
                    var existing = new List<Frame>();
                    if (context.PreserveExistingBlendShapes)
                    {
                        for (int s = 0; s < mesh.blendShapeCount; s++)
                        {
                            string name = mesh.GetBlendShapeName(s);
                            if (winners.ContainsKey(name)) continue;
                            for (int f = 0; f < mesh.GetBlendShapeFrameCount(s); f++)
                            {
                                var frame = new Frame
                                {
                                    Name = name,
                                    Weight = mesh.GetBlendShapeFrameWeight(s, f),
                                    Positions = new Vector3[mesh.vertexCount],
                                    Normals = new Vector3[mesh.vertexCount],
                                    Tangents = new Vector3[mesh.vertexCount]
                                };
                                mesh.GetBlendShapeFrameVertices(s, f, frame.Positions, frame.Normals, frame.Tangents);
                                existing.Add(frame);
                            }
                        }
                    }

                    mesh.ClearBlendShapes();
                    for (int i = 0; i < existing.Count; i++)
                    {
                        var frame = existing[i];
                        mesh.AddBlendShapeFrame(frame.Name, frame.Weight, frame.Positions, frame.Normals, frame.Tangents);
                    }
                }

                // Stream winning shapes into mesh using reusable per-vertex scratch buffers to keep peak memory O(vertexCount).
                int vertexCount = mesh.vertexCount;
                var posScratch = new Vector3[vertexCount];
                var nrmScratch = new Vector3[vertexCount];

                for (int w = 0; w < winningList.Count; w++)
                {
                    var (input, shape, normalMatrix) = winningList[w];
                    current = input;
                    var binding = input.VertexBinding;
                    var map = binding.VertexMap;
                    var matrix = binding.SpaceContract.CanonicalToUnityMeshLocal;
                    bool divergent = false;

                    foreach (var source in shape.Frames)
                    {
                        float weight = (float)source.Weight;
                        using (var lease = source.Payload.Acquire(input.PayloadSource))
                        {
                            var payload = lease.Value;
                            var positions = Expand(payload?.DeltaPositions, map.TargetCount);
                            var normals = Expand(payload?.DeltaNormals, map.TargetCount);

                            Array.Clear(posScratch, 0, vertexCount);
                            if (normals != null) Array.Clear(nrmScratch, 0, vertexCount);

                            for (int u = 0; u < vertexCount; u++)
                            {
                                var row = map.GetRow(u);
                                if (row.Length == 0) continue;
                                Vector3d position = default, normal = default;
                                for (int j = 0; j < row.Length; j++)
                                {
                                    if (positions != null)
                                    {
                                        var delta = positions[row[j]];
                                        position += delta;
                                        for (int k = 0; !divergent && k < j; k++)
                                            divergent = (delta - positions[row[k]]).sqrMagnitude > 1e-8;
                                    }
                                    if (normals != null) normal += normals[row[j]];
                                }
                                posScratch[u] = UnitySpaceContractMath.TransformPositionDelta(matrix, position / row.Length);
                                if (normals != null) nrmScratch[u] = UnitySpaceContractMath.TransformNormalDelta(normalMatrix, normal / row.Length);
                            }

                            mesh.AddBlendShapeFrame(shape.Name, weight, posScratch, normals != null ? nrmScratch : null, null);
                        }
                    }

                    if (divergent) context.Diagnostics.Add(new ApplyDiagnostic(ApplyDiagnosticSeverity.Warning, "BS_APP_WELD_DIVERGENCE",
                        $"Welded control points have different position deltas in '{shape.Name}'.", input.PatchId, input.MeshId, FeatureTypeId));
                }

                return FeatureApplyResult.Applied();
            }
            catch (Exception ex)
            {
                return FeatureApplyResult.Failed(new ApplyDiagnostic(ApplyDiagnosticSeverity.Error, "BS_APP_FAILED",
                    ex.Message, current?.PatchId, current?.MeshId, FeatureTypeId));
            }
        }

        private static Vector3d[] Expand(IndexedBuffer<Vector3d> buffer, int count)
        {
            if (buffer == null) return null;
            if (buffer.Domain != IndexDomain.ControlPoint || buffer.DomainCount != count)
                throw new InvalidOperationException("Frame buffer does not match the control point domain.");
            if (buffer.IsEmpty) return null;
            var values = new Vector3d[count];
            for (int i = 0; i < buffer.Count; i++) values[buffer.Indices[i]] = buffer.Values[i];
            return values;
        }
    }
}
