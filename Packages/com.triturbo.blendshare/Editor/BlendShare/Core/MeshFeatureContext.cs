using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Triturbo.BlendShare.Components;
using UnityEngine;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Identifies one ordered feature contribution and its serialized source context.
    /// </summary>
    public sealed class MeshFeatureHandle
    {
        internal MeshFeatureHandle(
            MeshFeatureObject feature,
            BlendShareObject patch,
            MeshDataObject meshData,
            string meshKey,
            int order,
            IEnumerable<BlendShareComponent> components = null,
            IEnumerable<UnityVertexMappingObject> mappingOverrides = null)
        {
            Feature = feature;
            Patch = patch;
            MeshData = meshData;
            MeshKey = MeshNodePath.Normalize(meshKey);
            Order = order;
            Components = (components ?? Array.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            MappingOverrides = (mappingOverrides ?? Array.Empty<UnityVertexMappingObject>())
                .Where(mapping => mapping != null)
                .ToArray();
        }

        /// <summary>
        /// Gets the serialized feature object contributed to the batch.
        /// </summary>
        public MeshFeatureObject Feature { get; }

        /// <summary>
        /// Gets the BlendShare patch that owns the feature contribution.
        /// </summary>
        public BlendShareObject Patch { get; }

        /// <summary>
        /// Gets the source mesh data that contains the feature contribution.
        /// </summary>
        public MeshDataObject MeshData { get; }

        /// <summary>
        /// Gets the normalized target mesh path for the contribution.
        /// </summary>
        public string MeshKey { get; }

        /// <summary>
        /// Gets the stable contribution order within the target mesh batch.
        /// </summary>
        public int Order { get; }
        internal IReadOnlyList<BlendShareComponent> Components { get; }
        internal IReadOnlyList<UnityVertexMappingObject> MappingOverrides { get; }
    }

    /// <summary>
    /// Target-global state shared by all Unity feature generators for one mesh.
    /// </summary>
    public sealed class UnityMeshFeatureContext
    {
        private readonly MeshFeatureHandle[] handles;
        private readonly Dictionary<MeshFeatureObject, MeshFeatureHandle> handlesByFeature;
        private readonly Dictionary<Type, object> featuresByType = new();
        private readonly HashSet<MeshFeatureHandle> handled = new();

        internal UnityMeshFeatureContext(
            UnityMeshGenerationSession session,
            string meshKey,
            Mesh originalMesh,
            Mesh workingMesh,
            SkinnedMeshRenderer targetRenderer,
            Transform targetRootTransform,
            IEnumerable<MeshFeatureHandle> handles)
        {
            Session = session;
            MeshKey = MeshNodePath.Normalize(meshKey);
            OriginalMesh = originalMesh;
            WorkingMesh = workingMesh;
            TargetRenderer = targetRenderer;
            TargetRootTransform = targetRootTransform ?? session?.TargetMeshes?.RootTransform;
            this.handles = (handles ?? Array.Empty<MeshFeatureHandle>())
                .Where(handle => handle?.Feature != null)
                .OrderBy(handle => handle.Order)
                .ToArray();
            handlesByFeature = new Dictionary<MeshFeatureObject, MeshFeatureHandle>(ReferenceComparer<MeshFeatureObject>.Instance);
            foreach (var handle in this.handles)
            {
                handlesByFeature[handle.Feature] = handle;
            }
        }

        /// <summary>
        /// Gets the generation session shared by all mesh batches in the current artifact.
        /// </summary>
        public UnityMeshGenerationSession Session { get; }

        /// <summary>
        /// Gets the normalized path identifying the target mesh in the generation session.
        /// </summary>
        public string MeshKey { get; }

        /// <summary>
        /// Gets the unmodified source mesh used for compatibility checks.
        /// </summary>
        public Mesh OriginalMesh { get; }

        /// <summary>
        /// Gets or sets the mesh containing the accumulated output of earlier feature generators.
        /// </summary>
        public Mesh WorkingMesh { get; set; }

        /// <summary>
        /// Gets the target renderer when the mesh belongs to a skinned hierarchy.
        /// </summary>
        public SkinnedMeshRenderer TargetRenderer { get; }

        /// <summary>
        /// Gets the transform used as the root for target-relative paths.
        /// </summary>
        public Transform TargetRootTransform { get; }

        /// <summary>
        /// Gets the ordered feature contributions assignable to <typeparamref name="TFeature"/>.
        /// </summary>
        /// <typeparam name="TFeature">Feature object type handled by the requesting generator.</typeparam>
        /// <returns>A cached read-only list in patch contribution order.</returns>
        public IReadOnlyList<TFeature> GetFeatures<TFeature>() where TFeature : MeshFeatureObject
        {
            if (featuresByType.TryGetValue(typeof(TFeature), out var cached))
            {
                return (IReadOnlyList<TFeature>)cached;
            }

            var features = MeshFeatureHandleUtility.GetDeduplicatedFeatures<TFeature>(handles, handled);

            featuresByType.Add(typeof(TFeature), features);
            return features;
        }

        /// <summary>
        /// Gets the source context associated with a feature contribution.
        /// </summary>
        /// <param name="feature">Feature object registered in this batch.</param>
        /// <returns>The handle for the feature contribution.</returns>
        /// <exception cref="ArgumentException">The feature is null or is not registered in this batch.</exception>
        public MeshFeatureHandle GetHandle(MeshFeatureObject feature)
        {
            if (feature == null || !handlesByFeature.TryGetValue(feature, out var handle))
            {
                throw new ArgumentException("Feature object is not registered in this mesh batch.", nameof(feature));
            }

            return handle;
        }

        /// <summary>
        /// Finds a stored vertex mapping compatible with the target mesh for a feature contribution.
        /// </summary>
        /// <param name="feature">Feature whose source mesh and mapping overrides should be used.</param>
        /// <param name="targetMesh">Target mesh to test for compatibility.</param>
        /// <returns>A compatible mapping, or <c>null</c> when none is available.</returns>
        public UnityVertexMappingObject GetMappingFor(MeshFeatureObject feature, Mesh targetMesh)
        {
            var handle = GetHandle(feature);
            var compatibilityMesh = targetMesh == WorkingMesh && OriginalMesh != null
                ? OriginalMesh
                : targetMesh;
            return handle.MappingOverrides
                       .FirstOrDefault(mapping => mapping.IsCompatibleWith(handle.MeshData, compatibilityMesh)) ??
                   (handle.MeshData?.m_Mappings ?? Array.Empty<UnityVertexMappingObject>())
                       .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(handle.MeshData, compatibilityMesh));
        }

        /// <summary>
        /// Gets the first authoring component of the requested type associated with a feature contribution.
        /// </summary>
        /// <typeparam name="T">Authoring component type.</typeparam>
        /// <param name="feature">Feature whose component context should be queried.</param>
        /// <returns>The first matching component, or <c>null</c>.</returns>
        public T GetComponent<T>(MeshFeatureObject feature) where T : BlendShareComponent
        {
            return GetHandle(feature).Components.OfType<T>().FirstOrDefault();
        }

        /// <summary>
        /// Gets all authoring components of the requested type associated with a feature contribution.
        /// </summary>
        /// <typeparam name="T">Authoring component type.</typeparam>
        /// <param name="feature">Feature whose component context should be queried.</param>
        /// <returns>The matching components.</returns>
        public IEnumerable<T> GetComponents<T>(MeshFeatureObject feature) where T : BlendShareComponent
        {
            return GetHandle(feature).Components.OfType<T>();
        }

        internal IReadOnlyList<MeshFeatureHandle> GetUnhandledHandles()
        {
            return handles.Where(handle => !handled.Contains(handle)).ToArray();
        }

        /// <summary>
        /// Gets an existing generated object for a session key or creates and registers one.
        /// </summary>
        /// <typeparam name="T">Generated Unity object type.</typeparam>
        /// <param name="key">Session-unique object key.</param>
        /// <param name="create">Factory used when the key has not been registered.</param>
        /// <returns>The existing or newly created object, or <c>null</c> without a session.</returns>
        public T GetOrCreateObject<T>(string key, Func<T> create) where T : UnityEngine.Object
        {
            return Session != null ? Session.GetOrCreateObject(key, create) : null;
        }

        /// <summary>
        /// Resolves a target-relative transform path through the shared session cache.
        /// </summary>
        /// <param name="path">Normalized or relative transform path.</param>
        /// <returns>The resolved transform, or <c>null</c> when the path is not found.</returns>
        public Transform ResolveTransform(string path)
        {
            return Session != null
                ? Session.ResolveTransform(TargetRootTransform, path)
                : MeshNodePath.FindRelativeTransform(TargetRootTransform, path);
        }

        /// <summary>
        /// Stores a resolved transform in the shared session cache.
        /// </summary>
        /// <param name="path">Transform path used as the cache key.</param>
        /// <param name="transform">Resolved transform.</param>
        public void CacheTransform(string path, Transform transform)
        {
            Session?.CacheTransform(path, transform);
        }

        /// <summary>
        /// Marks a session-wide generation step and reports whether it has not run before.
        /// </summary>
        /// <param name="key">Session-unique step key.</param>
        /// <returns><c>true</c> when the step should run.</returns>
        public bool MarkStepOnce(string key)
        {
            return Session == null || Session.MarkStepOnce(key);
        }

        /// <summary>
        /// Registers a generated Unity object with the shared session.
        /// </summary>
        /// <param name="key">Session-unique object key.</param>
        /// <param name="obj">Generated object to register.</param>
        public void AddObject(string key, UnityEngine.Object obj)
        {
            Session?.AddObject(key, obj);
        }
    }

#if ENABLE_FBX_SDK
    /// <summary>
    /// Target-global state shared by all FBX feature generators for one mesh node.
    /// </summary>
    public sealed class FbxMeshFeatureContext
    {
        private readonly MeshFeatureHandle[] handles;
        private readonly Dictionary<MeshFeatureObject, MeshFeatureHandle> handlesByFeature;
        private readonly Dictionary<Type, object> featuresByType = new();
        private readonly HashSet<MeshFeatureHandle> handled = new();

        internal FbxMeshFeatureContext(
            FbxGenerationSession session,
            string meshKey,
            FbxNode node,
            IEnumerable<MeshFeatureHandle> handles)
        {
            Session = session;
            MeshKey = MeshNodePath.Normalize(meshKey);
            Node = node;
            this.handles = (handles ?? Array.Empty<MeshFeatureHandle>())
                .Where(handle => handle?.Feature != null)
                .OrderBy(handle => handle.Order)
                .ToArray();
            handlesByFeature = new Dictionary<MeshFeatureObject, MeshFeatureHandle>(ReferenceComparer<MeshFeatureObject>.Instance);
            foreach (var handle in this.handles)
            {
                handlesByFeature[handle.Feature] = handle;
            }
        }

        /// <summary>
        /// Gets the generation session shared by all FBX mesh batches.
        /// </summary>
        public FbxGenerationSession Session { get; }

        /// <summary>
        /// Gets the normalized path identifying the target FBX mesh node.
        /// </summary>
        public string MeshKey { get; }

        /// <summary>
        /// Gets the target FBX node.
        /// </summary>
        public FbxNode Node { get; }

        /// <summary>
        /// Gets the mesh attribute attached to <see cref="Node"/>.
        /// </summary>
        public FbxMesh TargetMesh => Node?.GetMesh();

        /// <summary>
        /// Gets the root node of the FBX generation scene.
        /// </summary>
        public FbxNode RootNode => Session?.RootNode;

        /// <summary>
        /// Gets the ordered feature contributions assignable to <typeparamref name="TFeature"/>.
        /// </summary>
        /// <typeparam name="TFeature">Feature object type handled by the requesting generator.</typeparam>
        /// <returns>A cached read-only list in patch contribution order.</returns>
        public IReadOnlyList<TFeature> GetFeatures<TFeature>() where TFeature : MeshFeatureObject
        {
            if (featuresByType.TryGetValue(typeof(TFeature), out var cached))
            {
                return (IReadOnlyList<TFeature>)cached;
            }

            var features = MeshFeatureHandleUtility.GetDeduplicatedFeatures<TFeature>(handles, handled);

            featuresByType.Add(typeof(TFeature), features);
            return features;
        }

        /// <summary>
        /// Gets the source context associated with a feature contribution.
        /// </summary>
        /// <param name="feature">Feature object registered in this batch.</param>
        /// <returns>The handle for the feature contribution.</returns>
        /// <exception cref="ArgumentException">The feature is null or is not registered in this batch.</exception>
        public MeshFeatureHandle GetHandle(MeshFeatureObject feature)
        {
            if (feature == null || !handlesByFeature.TryGetValue(feature, out var handle))
            {
                throw new ArgumentException("Feature object is not registered in this FBX mesh batch.", nameof(feature));
            }

            return handle;
        }

        internal IReadOnlyList<MeshFeatureHandle> GetUnhandledHandles()
        {
            return handles.Where(handle => !handled.Contains(handle)).ToArray();
        }
    }
#endif

    internal static class MeshFeatureHandleUtility
    {
        internal static IReadOnlyList<TFeature> GetDeduplicatedFeatures<TFeature>(
            IEnumerable<MeshFeatureHandle> handles,
            ISet<MeshFeatureHandle> handled)
            where TFeature : MeshFeatureObject
        {
            var selected = new List<MeshFeatureHandle>();
            foreach (var handle in handles ?? Array.Empty<MeshFeatureHandle>())
            {
                if (handle?.Feature is not TFeature)
                {
                    continue;
                }

                handled?.Add(handle);
                int slot = -1;
                for (int index = 0; index < selected.Count; index++)
                {
                    if (IsSameLogicalPatch(selected[index], handle))
                    {
                        slot = index;
                        break;
                    }
                }

                if (slot >= 0)
                {
                    selected[slot] = handle;
                    continue;
                }

                selected.Add(handle);
            }

            var features = new List<TFeature>(selected.Count);
            foreach (var handle in selected)
            {
                features.Add((TFeature)handle.Feature);
            }

            return features;
        }

        private static bool IsSameLogicalPatch(MeshFeatureHandle first, MeshFeatureHandle second)
        {
            string firstId = first.Patch?.m_PatchId ?? string.Empty;
            string secondId = second.Patch?.m_PatchId ?? string.Empty;
            if (!string.IsNullOrEmpty(firstId) || !string.IsNullOrEmpty(secondId))
            {
                return string.Equals(firstId, secondId, StringComparison.Ordinal);
            }

            return first.Patch != null || second.Patch != null
                ? ReferenceEquals(first.Patch, second.Patch)
                : ReferenceEquals(first.Feature, second.Feature);
        }
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T x, T y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }
}
