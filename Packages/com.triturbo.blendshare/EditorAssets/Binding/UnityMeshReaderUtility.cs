using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Mapping;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Triturbo.BlendShare.EditorAssets.Binding
{
    /// <summary>
    /// Editor-safe Unity <see cref="Mesh"/> reader using <see cref="MeshUtility.AcquireReadOnlyMeshData"/>
    /// for non-readable meshes so binding and application never require modifying source ModelImporter Read/Write flags.
    /// </summary>
    public static class UnityMeshReaderUtility
    {
        private const string k_IsReadableProperty = "m_IsReadable";

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
                catch
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
            catch
            {
                vertices = Array.Empty<Vector3>();
                return false;
            }
        }

        public static bool TryGetNormals(Mesh mesh, out Vector3[] normals)
        {
            normals = Array.Empty<Vector3>();
            if (mesh == null)
            {
                return false;
            }

            if (mesh.isReadable)
            {
                try
                {
                    normals = mesh.normals;
                    return normals != null && normals.Length == mesh.vertexCount;
                }
                catch
                {
                    normals = Array.Empty<Vector3>();
                    return false;
                }
            }

            try
            {
                using (var meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    if (meshDataArray.Length != 1 || !meshDataArray[0].HasVertexAttribute(VertexAttribute.Normal))
                    {
                        return false;
                    }

                    using (var nativeNormals = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
                    {
                        meshDataArray[0].GetNormals(nativeNormals);
                        normals = nativeNormals.ToArray();
                    }
                }

                return normals.Length == mesh.vertexCount;
            }
            catch
            {
                normals = Array.Empty<Vector3>();
                return false;
            }
        }

        /// <summary>
        /// Reads vertices, triangulated submesh indices, optional UV0, and a lazy blendshape snapshot
        /// from a Unity <see cref="Mesh"/>, transparently supporting non-readable meshes.
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

        /// <summary>
        /// Marks an in-memory/generated cloned mesh readable without altering any source ModelImporter asset.
        /// </summary>
        public static bool TryEnableReadability(Mesh mesh)
        {
            if (mesh == null || mesh.isReadable)
            {
                return mesh != null;
            }

            try
            {
                var serializedMesh = new SerializedObject(mesh);
                var isReadable = serializedMesh.FindProperty(k_IsReadableProperty);
                if (isReadable == null)
                {
                    return false;
                }

                isReadable.boolValue = true;
                serializedMesh.ApplyModifiedPropertiesWithoutUndo();
                return mesh.isReadable;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates a readable deep clone of <paramref name="sourceMesh"/> safe for mutation by Unity appliers,
        /// even when <paramref name="sourceMesh"/> is non-readable.
        /// </summary>
        public static Mesh CloneReadable(Mesh sourceMesh)
        {
            if (sourceMesh == null)
            {
                throw new ArgumentNullException(nameof(sourceMesh));
            }

            var clone = UnityEngine.Object.Instantiate(sourceMesh);
            clone.name = sourceMesh.name;
            if (!clone.isReadable && !TryEnableReadability(clone))
            {
                UnityEngine.Object.DestroyImmediate(clone);
                throw new InvalidOperationException($"Unable to enable readability on cloned mesh '{sourceMesh.name}'.");
            }

            return clone;
        }

        private static bool TryGetTrianglesAndUv0(
            Mesh mesh,
            int vertexCount,
            out int[] triangles,
            out int[] submeshOffsets,
            out Vector2[] uv0)
        {
            triangles = Array.Empty<int>();
            int subMeshCount = Math.Max(0, mesh.subMeshCount);
            submeshOffsets = new int[subMeshCount + 1];
            uv0 = null;

            var flatTriangles = new List<int>();
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
                            AppendQuadsAsTriangles(mesh.GetIndices(s), flatTriangles);
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
                catch
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
                            else
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
            catch
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
            private readonly Mesh m_Mesh;
            private readonly Dictionary<string, int> m_SingleFrameIndexByName;
            private readonly string[] m_SingleFrameNames;

            public IReadOnlyList<string> SingleFrameShapeNames => m_SingleFrameNames;

            public UnityMeshBlendShapeSnapshot(Mesh mesh)
            {
                m_Mesh = mesh;
                int count = mesh != null ? mesh.blendShapeCount : 0;
                if (count == 0)
                {
                    m_SingleFrameIndexByName = new Dictionary<string, int>(0);
                    m_SingleFrameNames = Array.Empty<string>();
                    return;
                }

                m_SingleFrameIndexByName = new Dictionary<string, int>(count, StringComparer.Ordinal);
                var names = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    if (mesh.GetBlendShapeFrameCount(i) != 1 ||
                        !Mathf.Approximately(mesh.GetBlendShapeFrameWeight(i, 0), 100f))
                    {
                        continue;
                    }

                    string name = mesh.GetBlendShapeName(i);
                    if (!string.IsNullOrEmpty(name) && !m_SingleFrameIndexByName.ContainsKey(name))
                    {
                        m_SingleFrameIndexByName.Add(name, i);
                        names.Add(name);
                    }
                }

                m_SingleFrameNames = names.ToArray();
            }

            public bool TryGetSingleFrameDeltaVertices(string shapeName, out Vector3[] deltaVertices)
            {
                deltaVertices = null;
                if (m_Mesh == null || string.IsNullOrEmpty(shapeName) ||
                    !m_SingleFrameIndexByName.TryGetValue(shapeName, out int shapeIndex))
                {
                    return false;
                }

                try
                {
                    deltaVertices = new Vector3[m_Mesh.vertexCount];
                    m_Mesh.GetBlendShapeFrameVertices(shapeIndex, 0, deltaVertices, null, null);
                    return true;
                }
                catch
                {
                    deltaVertices = null;
                    return false;
                }
            }
        }
    }
}
