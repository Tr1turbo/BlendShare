using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Hashing;
using Triturbo.BlendShare.Mapping;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Reads imported mesh data through editor APIs and controls readability on generated meshes.
    /// </summary>
    public static class UnityMeshEditorDataUtility
    {
        private const string IsReadableProperty = "m_IsReadable";

        /// <summary>
        /// Gets vertex positions, using the editor read-only mesh data API when normal CPU access is disabled.
        /// </summary>
        public static bool TryGetVertices(Mesh mesh, out Vector3[] vertices)
        {
            vertices = Array.Empty<Vector3>();
            if (mesh == null)
            {
                return false;
            }

            if (mesh.isReadable)
            {
                try
                {
                    vertices = mesh.vertices;
                    return vertices != null && vertices.Length == mesh.vertexCount;
                }
                catch (Exception)
                {
                    vertices = Array.Empty<Vector3>();
                    return false;
                }
            }

            try
            {
                using (var meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    if (meshDataArray.Length != 1 || meshDataArray[0].vertexCount != mesh.vertexCount)
                    {
                        return false;
                    }

                    using (var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
                    {
                        meshDataArray[0].GetVertices(positions);
                        vertices = positions.ToArray();
                    }
                }

                return vertices.Length == mesh.vertexCount;
            }
            catch (Exception)
            {
                vertices = Array.Empty<Vector3>();
                return false;
            }
        }

        /// <summary>
        /// Tries to calculate a position hash without treating unavailable vertex data as an empty mesh.
        /// </summary>
        public static bool TryCalculatePositionHash(Mesh mesh, out string hash)
        {
            hash = string.Empty;
            if (!TryGetVertices(mesh, out var vertices))
            {
                return false;
            }

            hash = UnityVertexPositionHash.CalculateVertices(vertices);
            return true;
        }

        /// <summary>
        /// Checks a stored mapping against a Unity mesh using editor-safe vertex acquisition.
        /// </summary>
        public static bool IsMappingCompatible(
            UnityVertexMappingObject mapping,
            MeshDataObject meshData,
            Mesh targetMesh)
        {
            if (mapping == null || !mapping.m_IsValid || targetMesh == null ||
                mapping.m_UnityVertexCount != targetMesh.vertexCount ||
                !mapping.MatchesFbxControlPointCount(meshData?.FbxControlPointCount ?? -1))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(mapping.m_UnityVertexHash))
            {
                return TryCalculatePositionHash(targetMesh, out string targetHash) &&
                       mapping.m_UnityVertexHash == targetHash;
            }

            return mapping.m_UnityMesh == targetMesh;
        }

        /// <summary>
        /// Reads vertices, triangulated submesh indices, optional UV0, and a lazy blendshape provider
        /// from a Unity <see cref="Mesh"/> using editor-safe read-only mesh data when needed.
        /// </summary>
        public static bool TryReadMeshSnapshot(Mesh mesh, out UnityMeshSnapshot snapshot)
        {
            snapshot = null;
            if (mesh == null || mesh.vertexCount <= 0)
            {
                return false;
            }

            if (!TryGetVertices(mesh, out var vertices))
            {
                return false;
            }

            if (!TryGetTrianglesAndUv0(mesh, vertices.Length, out var triangles, out var submeshOffsets, out var uv0))
            {
                return false;
            }

            snapshot = new UnityMeshSnapshot(
                vertices,
                triangles,
                submeshOffsets,
                uv0,
                new UnityMeshBlendShapeSnapshot(mesh));
            return true;
        }

        private static bool TryGetTrianglesAndUv0(
            Mesh mesh,
            int vertexCount,
            out int[] triangles,
            out int[] submeshOffsets,
            out Vector2[] uv0)
        {
            triangles = Array.Empty<int>();
            submeshOffsets = new[] { 0 };
            uv0 = null;

            int subMeshCount = mesh.subMeshCount;
            var flatTriangles = new List<int>();
            submeshOffsets = new int[subMeshCount + 1];

            if (mesh.isReadable)
            {
                try
                {
                    for (int s = 0; s < subMeshCount; s++)
                    {
                        submeshOffsets[s] = flatTriangles.Count / 3;
                        var topology = mesh.GetTopology(s);
                        if (topology == MeshTopology.Triangles)
                        {
                            var idx = mesh.GetTriangles(s);
                            if (idx != null) flatTriangles.AddRange(idx);
                        }
                        else if (topology == MeshTopology.Quads)
                        {
                            var idx = mesh.GetIndices(s);
                            AppendQuadsAsTriangles(idx, flatTriangles);
                        }
                    }

                    submeshOffsets[subMeshCount] = flatTriangles.Count / 3;
                    triangles = flatTriangles.ToArray();

                    var meshUv = mesh.uv;
                    if (meshUv != null && meshUv.Length == vertexCount)
                    {
                        uv0 = meshUv;
                    }

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            try
            {
                using (var meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    if (meshDataArray.Length != 1)
                    {
                        return false;
                    }

                    var data = meshDataArray[0];
                    for (int s = 0; s < data.subMeshCount; s++)
                    {
                        submeshOffsets[s] = flatTriangles.Count / 3;
                        var desc = data.GetSubMesh(s);
                        if (desc.indexCount <= 0 ||
                            (desc.topology != MeshTopology.Triangles && desc.topology != MeshTopology.Quads))
                        {
                            continue;
                        }

                        using (var subIndices = new NativeArray<int>(desc.indexCount, Allocator.Temp))
                        {
                            data.GetIndices(subIndices, s);
                            if (desc.topology == MeshTopology.Triangles)
                            {
                                for (int i = 0; i < subIndices.Length; i++)
                                {
                                    flatTriangles.Add(subIndices[i]);
                                }
                            }
                            else if (desc.topology == MeshTopology.Quads)
                            {
                                AppendQuadsAsTriangles(subIndices, flatTriangles);
                            }
                        }
                    }

                    submeshOffsets[subMeshCount] = flatTriangles.Count / 3;
                    triangles = flatTriangles.ToArray();

                    if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
                    {
                        using (var nativeUv = new NativeArray<Vector2>(vertexCount, Allocator.Temp))
                        {
                            data.GetUVs(0, nativeUv);
                            uv0 = nativeUv.ToArray();
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AppendQuadsAsTriangles(int[] quadIndices, List<int> destination)
        {
            if (quadIndices == null) return;
            for (int i = 0; i + 3 < quadIndices.Length; i += 4)
            {
                int a = quadIndices[i];
                int b = quadIndices[i + 1];
                int c = quadIndices[i + 2];
                int d = quadIndices[i + 3];
                destination.Add(a);
                destination.Add(b);
                destination.Add(c);
                destination.Add(a);
                destination.Add(c);
                destination.Add(d);
            }
        }

        private static void AppendQuadsAsTriangles(NativeArray<int> quadIndices, List<int> destination)
        {
            for (int i = 0; i + 3 < quadIndices.Length; i += 4)
            {
                int a = quadIndices[i];
                int b = quadIndices[i + 1];
                int c = quadIndices[i + 2];
                int d = quadIndices[i + 3];
                destination.Add(a);
                destination.Add(b);
                destination.Add(c);
                destination.Add(a);
                destination.Add(c);
                destination.Add(d);
            }
        }

        private sealed class UnityMeshBlendShapeSnapshot : IUnityBlendShapeSnapshot
        {
            private readonly Mesh mesh;
            private readonly Dictionary<string, int> singleFrameIndexByName;
            private readonly string[] singleFrameNames;

            public IReadOnlyList<string> SingleFrameShapeNames => singleFrameNames;

            public UnityMeshBlendShapeSnapshot(Mesh mesh)
            {
                this.mesh = mesh;
                int count = mesh != null ? mesh.blendShapeCount : 0;
                if (count == 0)
                {
                    singleFrameIndexByName = new Dictionary<string, int>(0);
                    singleFrameNames = Array.Empty<string>();
                    return;
                }

                singleFrameIndexByName = new Dictionary<string, int>(count, StringComparer.Ordinal);
                var names = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    if (mesh.GetBlendShapeFrameCount(i) != 1 ||
                        !Mathf.Approximately(mesh.GetBlendShapeFrameWeight(i, 0), 100f))
                    {
                        continue;
                    }

                    string name = mesh.GetBlendShapeName(i);
                    if (!string.IsNullOrEmpty(name) && !singleFrameIndexByName.ContainsKey(name))
                    {
                        singleFrameIndexByName.Add(name, i);
                        names.Add(name);
                    }
                }

                singleFrameNames = names.ToArray();
            }

            public bool TryGetSingleFrameDeltaVertices(string shapeName, out Vector3[] deltaVertices)
            {
                deltaVertices = null;
                if (mesh == null || string.IsNullOrEmpty(shapeName) ||
                    !singleFrameIndexByName.TryGetValue(shapeName, out int shapeIndex))
                {
                    return false;
                }

                try
                {
                    deltaVertices = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(shapeIndex, 0, deltaVertices, null, null);
                    return true;
                }
                catch (Exception)
                {
                    deltaVertices = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Tries to mark an editor-generated mesh readable without modifying its source importer.
        /// </summary>
        public static bool TryEnableReadability(Mesh mesh)
        {
            if (mesh == null || mesh.isReadable)
            {
                return mesh != null;
            }

            try
            {
                // Mesh.isReadable has no public setter. Generated meshes retain the imported
                // flag when cloned, so update the serialized clone without touching its importer.
                var serializedMesh = new SerializedObject(mesh);
                var isReadable = serializedMesh.FindProperty(IsReadableProperty);
                if (isReadable == null)
                {
                    return false;
                }

                isReadable.boolValue = true;
                serializedMesh.ApplyModifiedPropertiesWithoutUndo();
                return mesh.isReadable;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
