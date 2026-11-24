using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>Compact attribute values indexed by face corner. An index of -1 marks a missing value.</summary>
    public interface IFbxIndexedAttribute<T> where T : unmanaged
    {
        /// <summary>Whether the source mesh contains this attribute.</summary>
        bool Exists { get; }
        /// <summary>Whether all corners of a control point share a single attribute value.</summary>
        bool UniquePerVertex { get; }
        /// <summary>Number of compact attribute values before corner expansion.</summary>
        int ValueCount { get; }
        /// <summary>Number of entries in the face-corner index stream.</summary>
        int CornerCount { get; }
        /// <summary>Number of separately retained W components.</summary>
        int ComponentWCount { get; }

        /// <summary>Copies compact attribute values in index order.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyValues(Span<T> destination, out int written);
        /// <summary>Copies compact value indices in face-corner order. A missing index is -1.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyCornerIndices(Span<int> destination, out int written);
        /// <summary>Expands values in face-corner order, filling missing indices with zero.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyPerCornerValues(Span<T> destination, out int written);
        /// <summary>Copies the optional W component stream in compact value order.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyValuesW(Span<double> destination, out int written);
    }

    /// <summary>Texture coordinates and tangent basis for one UV layer.</summary>
    public interface IFbxUvSet
    {
        /// <summary>Source index of this attribute set.</summary>
        int SetIndex { get; }
        /// <summary>Name.</summary>
        string Name { get; }
        /// <summary>Texture coordinates indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector2d> Uvs { get; }
        /// <summary>Tangent vectors indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector3d> Tangents { get; }
        /// <summary>Bitangent vectors indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector3d> Bitangents { get; }
    }

    /// <summary>Indexed corner colors for one color layer.</summary>
    public interface IFbxColorSet
    {
        /// <summary>Source index of this attribute set.</summary>
        int SetIndex { get; }
        /// <summary>Name.</summary>
        string Name { get; }
        /// <summary>RGBA values indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector4d> Colors { get; }
    }

    /// <summary>Faces grouped into one material partition.</summary>
    public interface IFbxMeshPart
    {
        /// <summary>Index of this material partition in the mesh.</summary>
        int PartIndex { get; }
        /// <summary>Number of polygons.</summary>
        int FaceCount { get; }
        /// <summary>Number of triangles after polygon triangulation.</summary>
        int TriangleCount { get; }
        /// <summary>Copies face indices into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceIndices(Span<int> destination, out int written);
    }

    /// <summary>Control point and polygon connectivity in loaded face-corner order.</summary>
    public interface IFbxMeshTopology
    {
        /// <summary>Number of mesh control points, including unused points.</summary>
        int ControlPointCount { get; }
        /// <summary>Number of polygons.</summary>
        int FaceCount { get; }
        /// <summary>Total number of polygon corners in the flattened topology.</summary>
        int FaceCornerCount { get; }
        /// <summary>Copies the number of corners in each polygon.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceSizes(Span<int> destination, out int written);
        /// <summary>Copies one control point index per corner in loaded face order.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceCornerControlPoints(Span<int> destination, out int written);
    }

    /// <summary>Control point geometry and deformers shared by scene meshes and detached snapshots.</summary>
    public interface IFbxMeshGeometry : IFbxMeshTopology
    {
        /// <summary>Scene-local element identifier. Its value is meaningful only in the source scene.</summary>
        long Id { get; }
        /// <summary>Name.</summary>
        string Name { get; }
        /// <summary>Copies control points into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyControlPoints(Span<Vector3d> destination, out int written);
        /// <summary>Gathers one representative normal per control point from its first incident corner.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFirstCornerNormals(Span<Vector3d> destination, out int written);
        /// <summary>Gathers one representative tangent per control point from its first incident corner.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFirstCornerTangents(Span<Vector3d> destination, out int written);
        /// <summary>All supported deformers attached to this geometry.</summary>
        IReadOnlyList<IFbxDeformer> Deformers { get; }
        /// <summary>Skin deformers attached to this geometry.</summary>
        IReadOnlyList<IFbxSkinDeformer> SkinDeformers { get; }
        /// <summary>Blend deformers attached to this geometry.</summary>
        IReadOnlyList<IFbxBlendDeformer> BlendDeformers { get; }
    }

    /// <summary>A scene mesh with instances, indexed attributes and material partitions.</summary>
    public interface IFbxMesh : IFbxMeshGeometry, IFbxNodeAttribute
    {
        /// <summary>Scene-local element identifier. Its value is meaningful only in the source scene.</summary>
        new long Id { get; }
        /// <summary>Name.</summary>
        new string Name { get; }
        /// <summary>First instance of this mesh, or null when the mesh has no instances.</summary>
        IFbxNode PrimaryNode { get; }
        /// <summary>Number of triangles after polygon triangulation.</summary>
        int TriangleCount { get; }
        /// <summary>Largest number of triangles produced by any single face.</summary>
        int MaxFaceTriangles { get; }
        /// <summary>Edge count.</summary>
        int EdgeCount { get; }

        /// <summary>Whether the mesh supplies face smoothing.</summary>
        bool HasFaceSmoothing { get; }
        /// <summary>Whether the mesh supplies face smoothing groups.</summary>
        bool HasFaceSmoothingGroups { get; }
        /// <summary>Whether the mesh supplies face materials.</summary>
        bool HasFaceMaterials { get; }
        /// <summary>Whether the mesh supplies face groups.</summary>
        bool HasFaceGroups { get; }
        /// <summary>Whether the mesh supplies face holes.</summary>
        bool HasFaceHoles { get; }
        /// <summary>Whether the mesh supplies edge smoothing.</summary>
        bool HasEdgeSmoothing { get; }
        /// <summary>Whether the mesh supplies edge creases.</summary>
        bool HasEdgeCreases { get; }
        /// <summary>Whether the mesh supplies edge visibility.</summary>
        bool HasEdgeVisibility { get; }

        /// <summary>Normal vectors indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector3d> Normals { get; }
        /// <summary>Tangent vectors indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector3d> Tangents { get; }
        /// <summary>Bitangent vectors indexed by polygon corner.</summary>
        IFbxIndexedAttribute<Vector3d> Bitangents { get; }
        /// <summary>Vertex crease weights indexed by polygon corner.</summary>
        IFbxIndexedAttribute<double> VertexCreases { get; }
        /// <summary>UV sets ordered by source set index.</summary>
        IReadOnlyList<IFbxUvSet> UvSets { get; }
        /// <summary>Vertex color sets ordered by source set index.</summary>
        IReadOnlyList<IFbxColorSet> ColorSets { get; }

        /// <summary>Material slots in source order.</summary>
        IReadOnlyList<IFbxMaterial> Materials { get; }
        /// <summary>Face partitions associated with material slots.</summary>
        IReadOnlyList<IFbxMeshPart> MaterialParts { get; }
        /// <summary>Number of entries in the material part usage-order table (includes unused trailing parts per ufbx).</summary>
        int MaterialPartUsageCount { get; }
        /// <summary>Copies material part indices ordered by first face use.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyMaterialPartUsageOrder(Span<int> destination, out int written);

        /// <summary>Copies the starting index of each face in the flattened corner stream.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceIndexBegins(Span<int> destination, out int written);
        /// <summary>Copies the first incident corner index per control point, or -1 for an unused point.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyVertexFirstCorners(Span<int> destination, out int written);

        /// <summary>Copies face smoothing into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceSmoothing(Span<byte> destination, out int written);
        /// <summary>Copies the original unsigned 32-bit smoothing mask per face.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceSmoothingGroups(Span<uint> destination, out int written);
        /// <summary>Copies face materials into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceMaterials(Span<int> destination, out int written);
        /// <summary>Copies face groups into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceGroups(Span<int> destination, out int written);
        /// <summary>Copies face holes into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyFaceHoles(Span<byte> destination, out int written);
        /// <summary>Copies edges into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyEdges(Span<FbxEdge> destination, out int written);
        /// <summary>Copies edge smoothing into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyEdgeSmoothing(Span<byte> destination, out int written);
        /// <summary>Copies edge creases into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyEdgeCreases(Span<double> destination, out int written);
        /// <summary>Copies edge visibility into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyEdgeVisibility(Span<byte> destination, out int written);
    }
}
