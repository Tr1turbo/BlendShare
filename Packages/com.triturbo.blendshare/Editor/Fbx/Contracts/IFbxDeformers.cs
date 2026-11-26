using System;
using System.Collections.Generic;

namespace Triturbo.BlendShare.Fbx
{
    /// <summary>A deformer associated with mesh geometry.</summary>
    public interface IFbxDeformer : IFbxElement
    {
        /// <summary>Geometry to which this deformer data belongs.</summary>
        IFbxMeshGeometry OwnerMesh { get; }
    }

    /// <summary>A skin containing per-bone influence clusters.</summary>
    public interface IFbxSkinDeformer : IFbxDeformer
    {
        /// <summary>Cluster count.</summary>
        int ClusterCount { get; }
        /// <summary>Influence clusters associated with this skin.</summary>
        IReadOnlyList<IFbxSkinCluster> Clusters { get; }
    }

    /// <summary>Bind transforms and sparse control point weights for one bone.</summary>
    public interface IFbxSkinCluster : IFbxElement
    {
        /// <summary>Geometry to which this deformer data belongs.</summary>
        IFbxMeshGeometry OwnerMesh { get; }
        /// <summary>Owner skin.</summary>
        IFbxSkinDeformer OwnerSkin { get; }
        /// <summary>The linked scene bone, or null for a detached or unresolved bone.</summary>
        IFbxNode BoneNode { get; }
        /// <summary>The linked bone path, retained for detached geometry.</summary>
        string BonePath { get; }
        /// <summary>Weight normalization/accumulation link mode authored on this FBX cluster.</summary>
        FbxClusterLinkMode LinkMode { get; }
        /// <summary>Number of paired control point indices and skin weights.</summary>
        int WeightCount { get; }
        /// <summary>Mesh bind transform in world space.</summary>
        FbxMatrix4x4 MeshBindWorld { get; }
        /// <summary>Bone bind transform in world space.</summary>
        FbxMatrix4x4 BindToWorld { get; }
        /// <summary>Bind transform from the mesh node to the bone.</summary>
        FbxMatrix4x4 MeshNodeToBone { get; }
        /// <summary>Bind transform from mesh geometry space to bone space.</summary>
        FbxMatrix4x4 GeometryToBone { get; }

        /// <summary>Copies control point indices into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyControlPointIndices(Span<int> destination, out int written);
        /// <summary>Copies weights into the supplied span.</summary>
        /// <returns>True on success, with the element count in written. Insufficient capacity fails before any writes.</returns>
        bool TryCopyWeights(Span<double> destination, out int written);
    }

    /// <summary>A collection of blend shape channels.</summary>
    public interface IFbxBlendDeformer : IFbxDeformer
    {
        /// <summary>Channel count.</summary>
        int ChannelCount { get; }
        /// <summary>Channels controlled by this blend deformer.</summary>
        IReadOnlyList<IFbxBlendChannel> Channels { get; }
    }

    /// <summary>A blend channel with ordered target keyframes.</summary>
    public interface IFbxBlendChannel : IFbxElement
    {
        /// <summary>Geometry to which this deformer data belongs.</summary>
        IFbxMeshGeometry OwnerMesh { get; }
        /// <summary>Blend deformer containing this channel or target.</summary>
        IFbxBlendDeformer OwnerDeformer { get; }
        /// <summary>Keyframe count.</summary>
        int KeyframeCount { get; }
        /// <summary>Ordered targets in this blend channel.</summary>
        IReadOnlyList<IFbxBlendKeyframe> Keyframes { get; }
    }

    /// <summary>A target shape and its weights within a blend channel.</summary>
    public interface IFbxBlendKeyframe
    {
        /// <summary>Channel weight at which this target is fully reached, in ufbx units where 1.0 is full strength.</summary>
        double TargetWeight { get; }
        /// <summary>Evaluated contribution of this keyframe in ufbx weight units.</summary>
        double EffectiveWeight { get; }
        /// <summary>Shape.</summary>
        IFbxBlendShape Shape { get; }
    }

    /// <summary>Sparse position, normal and weight data for a blend target.</summary>
    public interface IFbxBlendShape : IFbxElement
    {
        /// <summary>Geometry to which this deformer data belongs.</summary>
        IFbxMeshGeometry OwnerMesh { get; }
        /// <summary>Blend deformer containing this channel or target.</summary>
        IFbxBlendDeformer OwnerDeformer { get; }
        /// <summary>Owner channel.</summary>
        IFbxBlendChannel OwnerChannel { get; }
        /// <summary>Number of sparse control point entries in this target.</summary>
        int OffsetCount { get; }
        /// <summary>Whether this target includes a normal offset stream.</summary>
        bool HasNormalOffsets { get; }
        /// <summary>Whether this target contains explicit weights for its sparse offsets.</summary>
        bool HasOffsetWeights { get; }

        /// <summary>Copies the selected sparse streams in matching order. Pass an empty span to skip a stream. When OffsetCount is greater than zero, at least one destination span must be non-empty; passing four empty spans returns false with written set to zero. Insufficient capacity fails before any writes.</summary>
        bool TryCopyOffsets(
            Span<int> destinationControlPoints,
            Span<Vector3d> destinationPositionOffsets,
            Span<Vector3d> destinationNormalOffsets,
            Span<double> destinationOffsetWeights,
            out int written);
    }

    /// <summary>Shared deformer selection helpers across extraction, Unity raw seeding, and FBX export.</summary>
    public static class FbxDeformerUtility
    {
        /// <summary>
        /// Selects the primary <see cref="IFbxSkinDeformer"/> on <paramref name="mesh"/> using the canonical
        /// three-tier priority order:
        /// (1) first skin with a linked bone cluster carrying at least one weight entry,
        /// (2) first skin with any linked bone cluster,
        /// (3) first skin deformer on the mesh, or <c>null</c> when no skin deformers exist.
        /// </summary>
        public static IFbxSkinDeformer FindPrimarySkinDeformer(IFbxMeshGeometry mesh)
        {
            if (mesh?.SkinDeformers == null || mesh.SkinDeformers.Count == 0)
            {
                return null;
            }

            IFbxSkinDeformer firstSkin = null;
            IFbxSkinDeformer fallbackWithLinkedCluster = null;
            for (int i = 0; i < mesh.SkinDeformers.Count; i++)
            {
                var skin = mesh.SkinDeformers[i];
                if (skin == null)
                {
                    continue;
                }

                firstSkin ??= skin;
                if (skin.Clusters == null)
                {
                    continue;
                }

                for (int c = 0; c < skin.Clusters.Count; c++)
                {
                    var cl = skin.Clusters[c];
                    if (cl == null || (cl.BoneNode == null && string.IsNullOrEmpty(cl.BonePath)))
                    {
                        continue;
                    }

                    fallbackWithLinkedCluster ??= skin;
                    if (cl.WeightCount > 0)
                    {
                        return skin;
                    }
                }
            }

            return fallbackWithLinkedCluster ?? firstSkin;
        }
    }
}
