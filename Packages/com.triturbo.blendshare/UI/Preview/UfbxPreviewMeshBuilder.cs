using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using UnityEngine;
using UnityEngine.Rendering;

namespace Triturbo.BlendShare.UI.Preview
{
    public readonly struct UfbxPreviewMeshBuildResult
    {
        public readonly Mesh Mesh;
        public readonly Mesh WireMesh;
        public readonly UfbxPreviewMeshBuildStatus Status;

        public bool Success => Mesh != null && Status == UfbxPreviewMeshBuildStatus.Success;

        public UfbxPreviewMeshBuildResult(
            Mesh mesh,
            Mesh wireMesh,
            UfbxPreviewMeshBuildStatus status)
        {
            Mesh = mesh;
            WireMesh = wireMesh;
            Status = status;
        }
    }

    public enum UfbxPreviewMeshBuildStatus
    {
        None = 0,
        Success = 1,
        NullMesh = 2,
        NoVertices = 3,
        NoFaces = 4,
        NoValidTriangles = 5
    }

    public readonly struct UfbxPreviewMeshBuildInput
    {
        public readonly IFbxMeshGeometry Source;
        public readonly Matrix4x4 Transform;
        public readonly FbxAffineSpaceTransform? PreTransform;

        public UfbxPreviewMeshBuildInput(
            IFbxMeshGeometry source,
            Matrix4x4 transform,
            FbxAffineSpaceTransform? preTransform = null)
        {
            Source = source;
            Transform = transform;
            PreTransform = preTransform;
        }
    }

    /// <summary>
    /// Builds Unity solid and barycentric wireframe preview <see cref="Mesh"/> assets directly from ufbx geometry.
    /// Supports an optional <see cref="FbxAffineSpaceTransform"/> pre-transform (<c>W</c>) so cross-unit/cross-axis
    /// Source meshes render directly in Origin raw space.
    /// </summary>
    public static class UfbxPreviewMeshBuilder
    {
        public static UfbxPreviewMeshBuildResult Build(
            IEnumerable<UfbxPreviewMeshBuildInput> inputs,
            string name)
        {
            var buildInputs = inputs?.Where(input => input.Source != null).ToArray() ??
                              Array.Empty<UfbxPreviewMeshBuildInput>();
            if (buildInputs.Length == 0)
            {
                return new UfbxPreviewMeshBuildResult(
                    null,
                    null,
                    UfbxPreviewMeshBuildStatus.NullMesh);
            }

            var meshCombines = new List<CombineInstance>(buildInputs.Length);
            var wireCombines = new List<CombineInstance>(buildInputs.Length);
            var temporaryMeshes = new List<Mesh>(buildInputs.Length * 2);
            UfbxPreviewMeshBuildStatus failureStatus = UfbxPreviewMeshBuildStatus.NoValidTriangles;
            try
            {
                foreach (var input in buildInputs)
                {
                    var result = Build(input.Source, input.PreTransform);
                    if (!result.Success)
                    {
                        failureStatus = result.Status;
                        continue;
                    }

                    temporaryMeshes.Add(result.Mesh);
                    temporaryMeshes.Add(result.WireMesh);
                    meshCombines.Add(new CombineInstance
                    {
                        mesh = result.Mesh,
                        transform = input.Transform
                    });
                    wireCombines.Add(new CombineInstance
                    {
                        mesh = result.WireMesh,
                        transform = input.Transform
                    });
                }

                if (meshCombines.Count == 0)
                {
                    return new UfbxPreviewMeshBuildResult(
                        null,
                        null,
                        failureStatus);
                }

                var mesh = CombineMeshes(meshCombines, name, "Mesh");
                var wireMesh = CombineMeshes(wireCombines, name, "Wire");
                return new UfbxPreviewMeshBuildResult(
                    mesh,
                    wireMesh,
                    UfbxPreviewMeshBuildStatus.Success);
            }
            finally
            {
                foreach (var mesh in temporaryMeshes)
                {
                    if (mesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

        public static UfbxPreviewMeshBuildResult Build(
            IFbxMeshGeometry source,
            FbxAffineSpaceTransform? preTransform = null)
        {
            if (source == null)
            {
                return new UfbxPreviewMeshBuildResult(null, null, UfbxPreviewMeshBuildStatus.NullMesh);
            }

            var vertices = source.GetControlPoints();
            if (vertices == null || vertices.Length == 0)
            {
                return new UfbxPreviewMeshBuildResult(null, null, UfbxPreviewMeshBuildStatus.NoVertices);
            }

            int[] faceSizes = source.GetFaceSizes();
            int[] faceIndices = source.GetFaceCornerControlPoints();
            if (faceSizes == null || faceSizes.Length == 0 || faceIndices == null || faceIndices.Length == 0)
            {
                return new UfbxPreviewMeshBuildResult(null, null, UfbxPreviewMeshBuildStatus.NoFaces);
            }

            bool applyPre = preTransform.HasValue && preTransform.Value.IsValid && !preTransform.Value.IsIdentity;
            bool flipWinding = applyPre && ComputeUpper3x3Determinant(preTransform.Value.Offset) < 0d;

            var unityVertices = new List<Vector3>(vertices.Length);
            if (applyPre)
            {
                var xform = preTransform.Value;
                for (int i = 0; i < vertices.Length; i++)
                {
                    unityVertices.Add(xform.TransformPoint(vertices[i]).ToVector3());
                }
            }
            else
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    unityVertices.Add(vertices[i].ToVector3());
                }
            }

            var triangles = new List<int>(faceIndices.Length);
            var wireVertices = new List<Vector3>(faceIndices.Length * 3);
            var wireBarycentrics = new List<Vector3>(faceIndices.Length * 3);
            var wireEdgeMasks = new List<Vector3>(faceIndices.Length * 3);
            var wireTriangles = new List<int>(faceIndices.Length * 3);
            int offset = 0;
            for (int faceIndex = 0; faceIndex < faceSizes.Length; faceIndex++)
            {
                int size = faceSizes[faceIndex];
                if (size < 3 || offset + size > faceIndices.Length || !FaceIndicesAreValid(faceIndices, offset, size, vertices.Length))
                {
                    offset += Mathf.Max(size, 0);
                    continue;
                }

                for (int i = 1; i < size - 1; i++)
                {
                    int first = faceIndices[offset];
                    int second = faceIndices[offset + i];
                    int third = faceIndices[offset + i + 1];
                    if (flipWinding)
                    {
                        triangles.Add(first);
                        triangles.Add(third);
                        triangles.Add(second);
                    }
                    else
                    {
                        triangles.Add(first);
                        triangles.Add(second);
                        triangles.Add(third);
                    }

                    int wireIndex = wireVertices.Count;
                    wireVertices.Add(unityVertices[first]);
                    wireVertices.Add(unityVertices[second]);
                    wireVertices.Add(unityVertices[third]);
                    wireBarycentrics.Add(Vector3.right);
                    wireBarycentrics.Add(Vector3.up);
                    wireBarycentrics.Add(Vector3.forward);

                    var edgeMask = new Vector3(
                        1f,
                        i == size - 2 ? 1f : 0f,
                        i == 1 ? 1f : 0f);
                    wireEdgeMasks.Add(edgeMask);
                    wireEdgeMasks.Add(edgeMask);
                    wireEdgeMasks.Add(edgeMask);
                    wireTriangles.Add(wireIndex);
                    wireTriangles.Add(wireIndex + 1);
                    wireTriangles.Add(wireIndex + 2);
                }

                offset += size;
            }

            if (triangles.Count == 0)
            {
                return new UfbxPreviewMeshBuildResult(null, null, UfbxPreviewMeshBuildStatus.NoValidTriangles);
            }

            var mesh = CreateMesh(source, "Mesh", unityVertices.Count);
            mesh.SetVertices(unityVertices);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var wireMesh = CreateMesh(source, "Wire", wireVertices.Count);
            wireMesh.SetVertices(wireVertices);
            wireMesh.SetUVs(0, wireBarycentrics);
            wireMesh.SetUVs(1, wireEdgeMasks);
            wireMesh.SetTriangles(wireTriangles, 0, true);
            wireMesh.RecalculateBounds();

            return new UfbxPreviewMeshBuildResult(mesh, wireMesh, UfbxPreviewMeshBuildStatus.Success);
        }

        private static double ComputeUpper3x3Determinant(FbxMatrix4x4 m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                   m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                   m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }

        private static Mesh CreateMesh(IFbxMeshGeometry source, string suffix, int vertexCount)
        {
            return new Mesh
            {
                name = string.IsNullOrEmpty(source.Name) ? $"UFBX Preview {suffix}" : $"UFBX Preview {source.Name} {suffix}",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
        }

        private static Mesh CombineMeshes(
            IReadOnlyList<CombineInstance> combines,
            string name,
            string suffix)
        {
            var mesh = new Mesh
            {
                name = $"UFBX Preview {name} {suffix}",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32
            };
            mesh.CombineMeshes(combines.ToArray(), true, true, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool FaceIndicesAreValid(int[] indices, int offset, int size, int vertexCount)
        {
            for (int i = 0; i < size; i++)
            {
                int index = indices[offset + i];
                if (index < 0 || index >= vertexCount)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
