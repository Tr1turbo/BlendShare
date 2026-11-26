using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Mapping
{
    /// <summary>
    /// Single blendshape keyframe weight (in Unity percent, e.g. <c>50f</c> or <c>100f</c>) paired with per-vertex position deltas.
    /// </summary>
    public readonly struct UnityBlendShapeFrame
    {
        /// <summary>Frame target weight in percent (e.g. <c>100f</c> for full weight).</summary>
        public float Weight { get; }

        /// <summary>Per-vertex position deltas aligned with the owning mesh's vertex array.</summary>
        public Vector3[] DeltaVertices { get; }

        /// <summary>Creates a blendshape frame snapshot.</summary>
        public UnityBlendShapeFrame(float weight, Vector3[] deltaVertices)
        {
            Weight = weight;
            DeltaVertices = deltaVertices;
        }
    }

    /// <summary>
    /// Lazy provider for Unity mesh blendshape vertex deltas (both single-frame and multi-keyframe in-between channels)
    /// used only when geometric and UV0 matching leave ambiguous polygon corners or coincident vertices.
    /// Implementations may borrow a live Unity <see cref="Mesh"/> reference and must not outlive the mapping call.
    /// </summary>
    public interface IUnityBlendShapeSnapshot
    {
        /// <summary>Names of single-frame weight-100 blendshapes available on the Unity mesh.</summary>
        IReadOnlyList<string> SingleFrameShapeNames { get; }

        /// <summary>Names of all blendshape channels (single-frame and multi-frame) available on the Unity mesh.</summary>
        IReadOnlyList<string> ShapeNames { get; }

        /// <summary>Attempts to read the per-vertex position deltas for a single-frame weight-100 blendshape.</summary>
        bool TryGetSingleFrameDeltaVertices(string shapeName, out Vector3[] deltaVertices);

        /// <summary>Attempts to read all ordered keyframes (weights and per-vertex position deltas) for a blendshape channel.</summary>
        bool TryGetShapeFrames(string shapeName, out UnityBlendShapeFrame[] frames);
    }

    /// <summary>
    /// Plain CPU geometry view of a Unity <see cref="Mesh"/> used by inverse and mesh-to-mesh mappers.
    /// Decouples pure mapping algorithms from Unity native mesh readability constraints.
    /// Note: when <see cref="BlendShapes"/> wraps a live <see cref="Mesh"/>, this snapshot is scoped to the active mapping call.
    /// </summary>
    public sealed class UnityMeshSnapshot
    {
        /// <summary>Mesh vertex positions in Unity local space.</summary>
        public Vector3[] Vertices { get; }

        /// <summary>
        /// Flattened triangle vertex indices across all supported submeshes (length is a multiple of 3).
        /// Quad submeshes (<see cref="MeshTopology.Quads"/>) are expanded into two triangles per quad.
        /// </summary>
        public int[] Triangles { get; }

        /// <summary>Cumulative triangle index offsets per submesh (length <c>submeshCount + 1</c>).</summary>
        public int[] SubmeshTriangleOffsets { get; }

        /// <summary>Optional UV0 coordinates aligned with <see cref="Vertices"/>, or <c>null</c> when unavailable.</summary>
        public Vector2[] Uv0 { get; }

        /// <summary>Optional vertex normals aligned with <see cref="Vertices"/>, or <c>null</c> when unavailable.</summary>
        public Vector3[] Normals { get; }

        /// <summary>Optional lazy provider for Unity blendshape deltas.</summary>
        public IUnityBlendShapeSnapshot BlendShapes { get; }

        /// <summary>Number of triangles in <see cref="Triangles"/>.</summary>
        public int TriangleCount => Triangles.Length / 3;

        /// <summary>Creates a new mesh snapshot.</summary>
        public UnityMeshSnapshot(
            Vector3[] vertices,
            int[] triangles,
            int[] submeshTriangleOffsets = null,
            Vector2[] uv0 = null,
            IUnityBlendShapeSnapshot blendShapes = null,
            Vector3[] normals = null)
        {
            Vertices = vertices ?? Array.Empty<Vector3>();
            Triangles = triangles ?? Array.Empty<int>();
            SubmeshTriangleOffsets = submeshTriangleOffsets ?? new[] { 0, Triangles.Length / 3 };
            Uv0 = uv0 != null && uv0.Length == Vertices.Length ? uv0 : null;
            Normals = normals != null && normals.Length == Vertices.Length ? normals : null;
            BlendShapes = blendShapes;
        }
    }
}
