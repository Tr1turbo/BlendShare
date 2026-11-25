using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>A named element identified within its source scene.</summary>
    public interface IFbxElement
    {
        /// <summary>Scene-local element identifier. Its value is meaningful only in the source scene.</summary>
        long Id { get; }
        /// <summary>Name.</summary>
        string Name { get; }
        /// <summary>Element type.</summary>
        FbxElementType ElementType { get; }
    }

    /// <summary>A loaded scene owning its elements until disposal.</summary>
    public interface IFbxScene : IDisposable
    {
        /// <summary>Asset path.</summary>
        string AssetPath { get; }
        /// <summary>Whether the underlying scene has been released.</summary>
        bool IsDisposed { get; }
        /// <summary>Metadata.</summary>
        FbxSceneMetadata Metadata { get; }
        /// <summary>Target axes requested at load time, or null when unspecified.</summary>
        FbxCoordinateAxes? RequestedTargetAxes { get; }
        /// <summary>Requested target unit in meters, or null when unspecified.</summary>
        double? RequestedTargetUnitMeters { get; }
        /// <summary>Root node.</summary>
        IFbxNode RootNode { get; }
        /// <summary>Nodes belonging to this scene.</summary>
        IReadOnlyList<IFbxNode> Nodes { get; }
        /// <summary>Distinct mesh elements in the scene.</summary>
        IReadOnlyList<IFbxMesh> Meshes { get; }
        /// <summary>Material elements belonging to the scene.</summary>
        IReadOnlyList<IFbxMaterial> Materials { get; }

        /// <summary>Returns the node at the given hierarchy path, or null if absent.</summary>
        IFbxNode FindNodeByPath(string path);
        /// <summary>Returns the mesh at the given node path, or null if absent.</summary>
        IFbxMesh FindMeshByNodePath(string nodePath);
    }

    /// <summary>A material referenced by a mesh or node.</summary>
    public interface IFbxMaterial : IFbxElement { }
}
