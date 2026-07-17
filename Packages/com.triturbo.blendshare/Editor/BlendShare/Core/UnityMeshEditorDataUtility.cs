using System;
using Triturbo.BlendShare.Hashing;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

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

            if (mapping.m_UnityMesh == targetMesh)
            {
                return true;
            }

            return !string.IsNullOrEmpty(mapping.m_UnityVertexHash) &&
                   TryCalculatePositionHash(targetMesh, out string targetHash) &&
                   mapping.m_UnityVertexHash == targetHash;
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
