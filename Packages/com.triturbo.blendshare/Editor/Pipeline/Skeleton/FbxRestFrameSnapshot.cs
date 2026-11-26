using System;
using System.Collections.Generic;
using Triturbo.BlendShare.Fbx;

namespace Triturbo.BlendShare.Pipeline.Skeleton
{
    /// <summary>
    /// Captures a clean <see cref="SkeletonRestFrame"/> from an <see cref="IFbxScene"/> by seeding
    /// bone rest globals from <see cref="IFbxSkinCluster.BindToWorld"/> across all scene meshes
    /// and propagating unskinned nodes (<c>FbxNull</c>, <c>&lt;bone&gt;_end</c>, <c>Armature</c>) top-down.
    /// </summary>
    public static class FbxRestFrameSnapshot
    {
        /// <summary>
        /// Captures the rest-frame snapshot for <paramref name="scene"/>.
        /// When <paramref name="priorityMeshNodePaths"/> is non-empty, clusters on those meshes receive priority
        /// tiers <c>0..1</c> (weighted vs zero-weight), while other scene meshes receive tiers <c>2..3</c>.
        /// </summary>
        public static SkeletonRestFrame Capture(
            IFbxScene scene,
            IReadOnlyCollection<string> priorityMeshNodePaths = null,
            double translationToleranceMeters = 1e-4d)
        {
            var builder = new SkeletonRestFrameBuilder();
            if (scene == null || scene.IsDisposed)
            {
                return builder.Build();
            }

            HashSet<string> prioritySet = null;
            if (priorityMeshNodePaths != null && priorityMeshNodePaths.Count > 0)
            {
                prioritySet = new HashSet<string>(StringComparer.Ordinal);
                foreach (string p in priorityMeshNodePaths)
                {
                    string normalized = FbxNameUtility.NormalizePath(p);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        prioritySet.Add(normalized);
                    }
                }
            }

            var livePaths = new HashSet<string>(StringComparer.Ordinal);
            if (scene.Nodes != null)
            {
                for (int i = 0; i < scene.Nodes.Count; i++)
                {
                    RegisterNodeChain(builder, livePaths, scene.Nodes[i]);
                }
            }

            if (scene.Meshes != null)
            {
                for (int m = 0; m < scene.Meshes.Count; m++)
                {
                    var mesh = scene.Meshes[m];
                    if (mesh == null)
                    {
                        continue;
                    }

                    RegisterNodeChain(builder, livePaths, mesh.PrimaryNode);
                    if (mesh.Instances != null)
                    {
                        for (int n = 0; n < mesh.Instances.Count; n++)
                        {
                            RegisterNodeChain(builder, livePaths, mesh.Instances[n]);
                        }
                    }

                    if (mesh.SkinDeformers == null || mesh.SkinDeformers.Count == 0)
                    {
                        continue;
                    }

                    int baseTier = (prioritySet == null || prioritySet.Count == 0 || IsPriorityMesh(mesh, prioritySet))
                        ? 0
                        : 2;
                    string meshLabel = ResolveMeshLabel(mesh);

                    for (int s = 0; s < mesh.SkinDeformers.Count; s++)
                    {
                        var skin = mesh.SkinDeformers[s];
                        if (skin?.Clusters == null)
                        {
                            continue;
                        }

                        for (int c = 0; c < skin.Clusters.Count; c++)
                        {
                            var cl = skin.Clusters[c];
                            if (cl == null)
                            {
                                continue;
                            }

                            RegisterNodeChain(builder, livePaths, cl.BoneNode);

                            string bonePath = cl.BoneNode != null
                                ? FbxNameUtility.NormalizePath(cl.BoneNode.Path)
                                : FbxNameUtility.NormalizePath(cl.BonePath);
                            if (string.IsNullOrEmpty(bonePath) || !livePaths.Contains(bonePath))
                            {
                                continue;
                            }

                            int tier = baseTier + (cl.WeightCount > 0 ? 0 : 1);
                            builder.AddBindSeed(bonePath, cl.BindToWorld, tier, $"{meshLabel}[skin:{s}]");
                        }
                    }
                }
            }

            double unitMeters = scene.Metadata.UnitMeters > 0d ? scene.Metadata.UnitMeters : 1d;
            return builder.Build(unitMeters: unitMeters, translationToleranceMeters: translationToleranceMeters);
        }

        private static void RegisterNodeChain(
            SkeletonRestFrameBuilder builder,
            HashSet<string> livePaths,
            IFbxNode node)
        {
            var curr = node;
            while (curr != null && !curr.IsRoot)
            {
                string path = FbxNameUtility.NormalizePath(curr.Path);
                if (!string.IsNullOrEmpty(path) && livePaths.Add(path))
                {
                    var raw = curr.RawTransform;
                    Vector3d scale = !raw.LclScaling.IsZero() ? raw.LclScaling : curr.LocalTransform.Scale;
                    if (scale.IsZero())
                    {
                        scale = Vector3d.one;
                    }

                    builder.AddLiveNode(
                        path,
                        curr.IsBone,
                        curr.NodeToWorldMatrix,
                        curr.NodeToParentMatrix,
                        raw.InheritMode,
                        raw.OriginalInheritMode,
                        scale);
                }

                curr = curr.Parent;
            }
        }

        private static bool IsPriorityMesh(IFbxMesh mesh, HashSet<string> prioritySet)
        {
            if (mesh.PrimaryNode != null && prioritySet.Contains(FbxNameUtility.NormalizePath(mesh.PrimaryNode.Path)))
            {
                return true;
            }

            if (mesh.Instances != null)
            {
                for (int i = 0; i < mesh.Instances.Count; i++)
                {
                    var inst = mesh.Instances[i];
                    if (inst != null && prioritySet.Contains(FbxNameUtility.NormalizePath(inst.Path)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ResolveMeshLabel(IFbxMesh mesh)
        {
            if (mesh?.PrimaryNode != null && !string.IsNullOrEmpty(mesh.PrimaryNode.Path))
            {
                return FbxNameUtility.NormalizePath(mesh.PrimaryNode.Path);
            }

            return mesh?.Name ?? string.Empty;
        }
    }
}
