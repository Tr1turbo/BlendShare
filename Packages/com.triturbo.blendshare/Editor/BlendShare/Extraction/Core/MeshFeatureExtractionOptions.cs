using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Fbx;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Base options for enabling and selecting extraction work for a feature.
    /// </summary>
    public abstract class MeshFeatureExtractionOptions
    {
        public abstract string FeatureId { get; }
        public bool Enabled = true;

        /// <summary>
        /// Checks whether this feature has work selected for the requested meshes.
        /// </summary>
        /// <param name="meshes">Mesh requests available to extraction.</param>
        /// <returns><c>true</c> when this enabled feature should extract at least one mesh.</returns>
        public virtual bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled &&
                   (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                   .Any(request => ShouldExtractMesh(request));
        }

        /// <summary>
        /// Checks whether this feature should run for one mesh request.
        /// </summary>
        /// <param name="mesh">Mesh request to inspect.</param>
        /// <returns><c>true</c> when this enabled feature should run for the mesh.</returns>
        public virtual bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            return Enabled && mesh != null;
        }
    }

    /// <summary>
    /// Type-keyed collection of feature extraction options for one extraction run.
    /// </summary>
    public sealed class MeshFeatureExtractionOptionsSet
    {
        private readonly Dictionary<Type, MeshFeatureExtractionOptions> optionsByType = new();
        private readonly Dictionary<string, MeshFeatureSourceOffset> sourceOffsetByMesh = new(StringComparer.Ordinal);

        public IEnumerable<MeshFeatureExtractionOptions> All => optionsByType.Values;

        public MeshFeatureSourceOffset GetSourceOffset(string path)
        {
            string key = BuildMeshKey(path);
            if (string.IsNullOrEmpty(key))
            {
                return new MeshFeatureSourceOffset();
            }

            if (!sourceOffsetByMesh.TryGetValue(key, out var offset))
            {
                offset = new MeshFeatureSourceOffset();
                sourceOffsetByMesh[key] = offset;
            }

            return offset;
        }

        public void Set<TOptions>(TOptions options)
            where TOptions : MeshFeatureExtractionOptions
        {
            Set(typeof(TOptions), options);
        }

        public void Set(Type optionsType, MeshFeatureExtractionOptions options)
        {
            if (optionsType == null)
            {
                return;
            }

            if (options == null)
            {
                optionsByType.Remove(optionsType);
                return;
            }

            if (!optionsType.IsInstanceOfType(options))
            {
                throw new ArgumentException(
                    $"Options instance '{options.GetType().FullName}' is not assignable to '{optionsType.FullName}'.",
                    nameof(options));
            }

            optionsByType[optionsType] = options;
        }

        public bool TryGet<TOptions>(out TOptions options)
            where TOptions : MeshFeatureExtractionOptions
        {
            if (optionsByType.TryGetValue(typeof(TOptions), out var raw))
            {
                options = raw as TOptions;
                return options != null;
            }

            options = null;
            return false;
        }

        public bool TryGet(Type optionsType, out MeshFeatureExtractionOptions options)
        {
            if (optionsType == null)
            {
                options = null;
                return false;
            }

            return optionsByType.TryGetValue(optionsType, out options);
        }

        private static string BuildMeshKey(string path)
        {
            return MeshFeatureExtractionSession.BuildMeshKey(path);
        }
    }

    public sealed class MeshFeatureSourceOffset
    {
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;

        public bool IsIdentity =>
            Position == Vector3.zero &&
            Rotation == Vector3.zero &&
            Scale == Vector3.one;

        public void Reset()
        {
            Position = Vector3.zero;
            Rotation = Vector3.zero;
            Scale = Vector3.one;
        }

        public Matrix4x4 ToUnityMatrix()
        {
            return Matrix4x4.TRS(Position, Quaternion.Euler(Rotation), SanitizeScale(Scale));
        }

        public FbxMatrix4x4 ToFbxMatrix()
        {
            Matrix4x4 matrix = ToUnityMatrix();
            return new FbxMatrix4x4(
                matrix[0, 0], matrix[1, 0], matrix[2, 0], matrix[3, 0],
                matrix[0, 1], matrix[1, 1], matrix[2, 1], matrix[3, 1],
                matrix[0, 2], matrix[1, 2], matrix[2, 2], matrix[3, 2],
                matrix[0, 3], matrix[1, 3], matrix[2, 3], matrix[3, 3]);
        }

        private static Vector3 SanitizeScale(Vector3 scale)
        {
            return new Vector3(
                Mathf.Approximately(scale.x, 0f) ? 1f : scale.x,
                Mathf.Approximately(scale.y, 0f) ? 1f : scale.y,
                Mathf.Approximately(scale.z, 0f) ? 1f : scale.z);
        }
    }
}
