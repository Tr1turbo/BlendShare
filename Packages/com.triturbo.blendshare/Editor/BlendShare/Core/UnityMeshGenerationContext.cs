using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Components;
using Triturbo.BlendShare.Features.SkinWeights;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Shared state for one Unity mesh generation run.
    /// </summary>
    public sealed class UnityMeshGenerationSession
    {
        private readonly List<Object> generatedObjects = new();
        private readonly Dictionary<string, Object> generatedObjectsByKey = new();
        private readonly Dictionary<string, UnityMeshSkinBindingOutput> skinBindingsByMeshKey = new();
        private readonly Dictionary<string, ArmatureBoneData> armatureBonesByPath = new();
        private readonly Dictionary<string, Transform> transformsByPath = new();
        private readonly HashSet<string> completedSteps = new();
        private ArmatureObject armature;

        public Object TargetMeshContainer { get; }
        public IReadOnlyList<BlendShareObject> Patches { get; }
        public IReadOnlyList<BlendShareComponent> Components { get; }
        public UnityMeshTargetLookup TargetMeshes { get; }
        public IBlendShareProgress Progress { get; }
        public IReadOnlyList<Object> GeneratedObjects => generatedObjects;
        public IReadOnlyDictionary<string, UnityMeshSkinBindingOutput> SkinBindingsByMeshKey => skinBindingsByMeshKey;
        public ArmatureObject Armature => armature;

        /// <summary>
        /// Creates a generation session for a target mesh container and a set of BlendShare assets.
        /// </summary>
        /// <param name="targetMeshContainer">FBX asset, generated mesh asset, or other Unity object that contains target meshes.</param>
        /// <param name="patches">BlendShare patches being applied.</param>
        /// <param name="targetMeshes">Lookup table for target meshes.</param>
        public UnityMeshGenerationSession(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            UnityMeshTargetLookup targetMeshes,
            IEnumerable<BlendShareComponent> components = null,
            IBlendShareProgress progress = null)
        {
            TargetMeshContainer = targetMeshContainer;
            Patches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            TargetMeshes = targetMeshes;
            Progress = BlendShareProgressUtility.Resolve(progress);
            Components = (components ?? System.Array.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// Builds the stable mesh key used while stacking generated results.
        /// </summary>
        /// <param name="meshData">Stored mesh data.</param>
        /// <returns>The stored mesh path.</returns>
        public static string BuildMeshKey(MeshDataObject meshData)
        {
            if (meshData == null)
            {
                return MeshNodePath.Root;
            }

            return MeshNodePath.Normalize(meshData.m_Path);
        }

        /// <summary>
        /// Formats the target object name for diagnostics.
        /// </summary>
        /// <returns>The target object name, or a generic fallback.</returns>
        public string FormatTargetName()
        {
            return TargetMeshContainer != null ? TargetMeshContainer.name : "target";
        }

        /// <summary>
        /// Finds and caches a target hierarchy transform by normalized path.
        /// </summary>
        public Transform ResolveTransform(Transform root, string path)
        {
            string normalizedPath = MeshNodePath.Normalize(path);
            if (transformsByPath.TryGetValue(normalizedPath, out var cached) && cached != null)
            {
                return cached;
            }

            var transform = MeshNodePath.FindRelativeTransform(root, normalizedPath);
            if (transform != null)
            {
                transformsByPath[normalizedPath] = transform;
            }

            return transform;
        }

        /// <summary>
        /// Registers a target hierarchy transform so later mesh generators reuse it.
        /// </summary>
        public void CacheTransform(string path, Transform transform)
        {
            if (transform == null)
            {
                return;
            }

            transformsByPath[MeshNodePath.Normalize(path)] = transform;
        }

        public bool MarkStepOnce(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && completedSteps.Add(key);
        }

        public void SetSkinBinding(string meshKey, string rootBonePath, IEnumerable<string> bonePaths)
        {
            string normalizedKey = MeshNodePath.Normalize(meshKey);
            skinBindingsByMeshKey[normalizedKey] = new UnityMeshSkinBindingOutput(rootBonePath, bonePaths);
        }

        public bool TryGetSkinBinding(string meshKey, out UnityMeshSkinBindingOutput binding)
        {
            return skinBindingsByMeshKey.TryGetValue(MeshNodePath.Normalize(meshKey), out binding);
        }

        public void AddArmatureBones(IEnumerable<ArmatureBoneData> bones)
        {
            foreach (var bone in bones ?? System.Array.Empty<ArmatureBoneData>())
            {
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(bone.m_Path);
                if (!armatureBonesByPath.ContainsKey(path))
                {
                    armatureBonesByPath.Add(path, bone);
                }
            }

            if (armatureBonesByPath.Count == 0)
            {
                return;
            }

            if (armature == null)
            {
                armature = ScriptableObject.CreateInstance<ArmatureObject>();
                armature.name = "Armature";
                AddObject("Armature", armature);
            }

            armature.SetBones(armatureBonesByPath.Values);
            EditorUtility.SetDirty(armature);
        }

        /// <summary>
        /// Tracks an object produced by a generator. Persistence happens after the session completes.
        /// </summary>
        /// <param name="key">Session-unique key for this generated object.</param>
        /// <param name="generatedObject">Object created by a generator pass.</param>
        /// <returns>The object tracked by the session, or <c>null</c> when no object was supplied.</returns>
        public Object AddObject(string key, Object generatedObject)
        {
            if (generatedObject == null)
            {
                return null;
            }

            string objectKey = BuildGeneratedObjectKey(generatedObject.GetType(), key, generatedObject.name);
            if (generatedObjectsByKey.TryGetValue(objectKey, out var existing))
            {
                return existing;
            }

            var objectToTrack = EditorUtility.IsPersistent(generatedObject)
                ? Object.Instantiate(generatedObject)
                : generatedObject;
            objectToTrack.name = string.IsNullOrWhiteSpace(generatedObject.name)
                ? generatedObject.GetType().Name
                : generatedObject.name;

            if (!generatedObjects.Contains(objectToTrack))
            {
                generatedObjects.Add(objectToTrack);
            }

            generatedObjectsByKey[objectKey] = objectToTrack;
            EditorUtility.SetDirty(objectToTrack);
            return objectToTrack;
        }

        /// <summary>
        /// Gets a shared object for this generation session, or creates it when missing.
        /// </summary>
        /// <typeparam name="T">Generated Unity object type.</typeparam>
        /// <param name="key">Session-unique key for this generated object.</param>
        /// <param name="create">Factory used when no object has been registered for the key.</param>
        /// <returns>The session-shared generated object.</returns>
        public T GetOrCreateObject<T>(string key, System.Func<T> create)
            where T : Object
        {
            string objectKey = BuildGeneratedObjectKey(typeof(T), key, null);
            if (generatedObjectsByKey.TryGetValue(objectKey, out var cached))
            {
                return cached as T;
            }

            var created = create != null ? create() : null;
            if (created == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(created.name))
            {
                created.name = string.IsNullOrWhiteSpace(key) ? typeof(T).Name : key;
            }

            return AddObject(key, created) as T;
        }

        public void DestroyGeneratedObjects()
        {
            foreach (var generatedObject in generatedObjects)
            {
                if (generatedObject == null || AssetDatabase.Contains(generatedObject))
                {
                    continue;
                }

                Object.DestroyImmediate(generatedObject);
            }

            generatedObjects.Clear();
            generatedObjectsByKey.Clear();
        }

        private static string BuildGeneratedObjectKey(System.Type type, string key, string fallbackName)
        {
            string name = string.IsNullOrWhiteSpace(key) ? fallbackName : key;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = type.Name;
            }

            return $"{type.FullName ?? type.Name}::{name}";
        }
    }

    /// <summary>
    /// Renderer skin binding generated alongside a Unity mesh.
    /// </summary>
    public sealed class UnityMeshSkinBindingOutput
    {
        public string RootBonePath { get; }
        public string[] BonePaths { get; }

        public UnityMeshSkinBindingOutput(string rootBonePath, IEnumerable<string> bonePaths)
        {
            RootBonePath = MeshNodePath.Normalize(rootBonePath);
            BonePaths = bonePaths?
                .Select(MeshNodePath.Normalize)
                .ToArray() ?? System.Array.Empty<string>();
        }
    }

    /// <summary>
    /// Context passed to generators while applying a feature to a Unity mesh.
    /// </summary>
    public sealed class UnityMeshGenerationContext
    {
        private readonly HashSet<MeshFeatureObject> handledFeatures = new();

        public UnityMeshGenerationSession Session { get; }
        public BlendShareObject Patch { get; }
        public MeshDataObject MeshData { get; }
        public string MeshKey { get; }
        public Mesh OriginalMesh { get; }
        public Mesh WorkingMesh { get; set; }
        public SkinnedMeshRenderer TargetRenderer { get; }
        public Transform TargetRootTransform { get; }
        public IReadOnlyList<BlendShareComponent> Components { get; }
        public IReadOnlyList<UnityVertexMappingObject> MappingOverrides { get; }
        public IReadOnlyList<MeshFeatureObject> Features =>
            MeshData != null ? MeshData.Features : System.Array.Empty<MeshFeatureObject>();
        public bool HasUnhandledFeatures => Features.Any(feature => feature != null && !handledFeatures.Contains(feature));

        /// <summary>
        /// Creates a Unity mesh generation context.
        /// </summary>
        /// <param name="session">Parent generation session.</param>
        /// <param name="patch">BlendShare patch currently being applied.</param>
        /// <param name="meshData">Stored mesh data currently being generated.</param>
        /// <param name="originalMesh">Original target mesh for the current mesh pass.</param>
        /// <param name="workingMesh">Mutable mesh instance that generators update.</param>
        /// <param name="targetRenderer">Target renderer for GameObject-backed generation, when available.</param>
        /// <param name="targetRootTransform">Target avatar/root transform for scene-backed generation, when available.</param>
        public UnityMeshGenerationContext(
            UnityMeshGenerationSession session,
            BlendShareObject patch,
            MeshDataObject meshData,
            Mesh originalMesh,
            Mesh workingMesh,
            SkinnedMeshRenderer targetRenderer = null,
            Transform targetRootTransform = null,
            string meshKey = null,
            IEnumerable<BlendShareComponent> components = null,
            IEnumerable<UnityVertexMappingObject> mappingOverrides = null)
        {
            Session = session;
            Patch = patch;
            MeshData = meshData;
            MeshKey = MeshNodePath.Normalize(meshKey ?? UnityMeshGenerationSession.BuildMeshKey(meshData));
            OriginalMesh = originalMesh;
            WorkingMesh = workingMesh;
            TargetRenderer = targetRenderer;
            TargetRootTransform = targetRootTransform ?? session?.TargetMeshes?.RootTransform;
            Components = (components ?? session?.Components ?? System.Array.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            MappingOverrides = (mappingOverrides ?? System.Array.Empty<UnityVertexMappingObject>())
                .Where(mapping => mapping != null)
                .ToArray();
        }

        /// <summary>
        /// Gets and claims the stored feature object for this mesh, if present.
        /// </summary>
        /// <typeparam name="TFeature">Feature object type to retrieve.</typeparam>
        /// <returns>The stored feature object, or <c>null</c> when the mesh does not contain that feature.</returns>
        public TFeature GetFeature<TFeature>() where TFeature : MeshFeatureObject
        {
            var feature = MeshData != null ? MeshData.GetFeature<TFeature>() : null;
            if (feature != null)
            {
                handledFeatures.Add(feature);
            }

            return feature;
        }

        public T GetComponent<T>() where T : BlendShareComponent
        {
            return Components.OfType<T>().FirstOrDefault();
        }

        public IEnumerable<T> GetComponents<T>() where T : BlendShareComponent
        {
            return Components.OfType<T>();
        }

        /// <summary>
        /// Gets stored feature objects that no generation pass claimed from this context.
        /// </summary>
        /// <returns>Unhandled feature objects for diagnostics.</returns>
        public IEnumerable<MeshFeatureObject> GetUnhandledFeatures()
        {
            return Features.Where(feature => feature != null && !handledFeatures.Contains(feature));
        }

        /// <summary>
        /// Gets a shared generated object from this session.
        /// </summary>
        public T GetOrCreateObject<T>(string key, System.Func<T> create)
            where T : Object
        {
            return Session != null ? Session.GetOrCreateObject(key, create) : null;
        }

        public Transform ResolveTransform(string path)
        {
            return Session != null
                ? Session.ResolveTransform(TargetRootTransform, path)
                : MeshNodePath.FindRelativeTransform(TargetRootTransform, path);
        }

        public void CacheTransform(string path, Transform transform)
        {
            Session?.CacheTransform(path, transform);
        }

        public bool MarkStepOnce(string key)
        {
            return Session == null || Session.MarkStepOnce(key);
        }

        /// <summary>
        /// Tracks a generated object in this session. Persistence happens after generation finishes.
        /// </summary>
        public void AddObject(string key, Object obj)
        {
            Session?.AddObject(key, obj);
        }

        public UnityVertexMappingObject GetMappingFor(Mesh targetMesh)
        {
            return (MappingOverrides ?? System.Array.Empty<UnityVertexMappingObject>())
                   .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(MeshData, targetMesh)) ??
                   (MeshData?.m_Mappings ?? System.Array.Empty<UnityVertexMappingObject>())
                   .FirstOrDefault(mapping => mapping != null && mapping.IsCompatibleWith(MeshData, targetMesh));
        }

    }

    /// <summary>
    /// Resolves target Unity meshes by stored mesh path.
    /// </summary>
    public sealed class UnityMeshTargetLookup
    {
        private readonly Dictionary<string, Mesh> meshesByPath = new();
        private readonly Dictionary<string, SkinnedMeshRenderer> renderersByPath = new();
        private readonly List<Mesh> meshes = new();

        public IReadOnlyList<Mesh> Meshes => meshes;
        public Transform RootTransform { get; private set; }

        private UnityMeshTargetLookup() { }

        /// <summary>
        /// Builds a mesh lookup from a Unity object that owns target meshes.
        /// </summary>
        /// <param name="targetMeshContainer">Asset or object containing target meshes.</param>
        /// <returns>A lookup instance, or <c>null</c> when the container cannot provide target meshes.</returns>
        public static UnityMeshTargetLookup Create(Object targetMeshContainer)
        {
            if (targetMeshContainer == null)
            {
                return null;
            }

            var lookup = new UnityMeshTargetLookup();
            if (targetMeshContainer is GameObject root)
            {
                lookup.AddRendererPaths(root);
            }
            else
            {
                string path = AssetDatabase.GetAssetPath(targetMeshContainer);
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Mesh mesh)
                    {
                        // Generated mesh subassets use their name to store the canonical node path.
                        lookup.AddMeshPath(mesh, mesh.name);
                    }
                }
            }

            return lookup;
        }

        /// <summary>
        /// Builds a mesh lookup from an explicit mesh collection.
        /// </summary>
        /// <param name="sourceMeshes">Meshes whose names already contain the path identifier.</param>
        /// <returns>A lookup instance.</returns>
        public static UnityMeshTargetLookup Create(IEnumerable<Mesh> sourceMeshes)
        {
            var lookup = new UnityMeshTargetLookup();
            foreach (var mesh in sourceMeshes ?? Enumerable.Empty<Mesh>())
            {
                // Explicit mesh collections are only valid when their mesh names store node paths
                // from a previously generated BlendShare asset.
                lookup.AddMeshPath(mesh, mesh != null ? mesh.name : null);
            }

            return lookup;
        }

        /// <summary>
        /// Finds the target mesh for stored mesh data.
        /// </summary>
        /// <param name="meshData">Stored mesh data to resolve.</param>
        /// <param name="mesh">Resolved target mesh.</param>
        /// <returns><c>true</c> when a target mesh was found.</returns>
        public bool TryGetMesh(MeshDataObject meshData, out Mesh mesh)
        {
            string path = MeshNodePath.Normalize(meshData?.m_Path);
            return TryGetMesh(path, out mesh);
        }

        public bool TryGetMesh(string path, out Mesh mesh)
        {
            mesh = null;
            string normalizedPath = MeshNodePath.Normalize(path);
            if (meshesByPath.TryGetValue(normalizedPath, out mesh))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the target renderer for stored mesh data.
        /// </summary>
        /// <param name="meshData">Stored mesh data to resolve.</param>
        /// <param name="renderer">Resolved target renderer.</param>
        /// <returns><c>true</c> when a scene/prefab renderer was found.</returns>
        public bool TryGetRenderer(MeshDataObject meshData, out SkinnedMeshRenderer renderer)
        {
            string path = MeshNodePath.Normalize(meshData?.m_Path);
            return TryGetRenderer(path, out renderer);
        }

        public bool TryGetRenderer(string path, out SkinnedMeshRenderer renderer)
        {
            renderer = null;
            string normalizedPath = MeshNodePath.Normalize(path);
            if (renderersByPath.TryGetValue(normalizedPath, out renderer))
            {
                return true;
            }

            return false;
        }

        public string GetResolutionError(MeshDataObject meshData)
        {
            string path = MeshNodePath.Normalize(meshData?.m_Path);
            if (meshesByPath.ContainsKey(path))
            {
                return null;
            }

            return $"Node path '{path}' was not found.";
        }


        private void AddMeshPath(Mesh mesh, string path)
        {
            if (mesh == null)
            {
                return;
            }

            if (!meshes.Contains(mesh))
            {
                meshes.Add(mesh);
            }

            string normalizedPath = MeshNodePath.Normalize(path);
            if (!meshesByPath.ContainsKey(normalizedPath))
            {
                meshesByPath.Add(normalizedPath, mesh);
            }
        }

        private void AddRendererPaths(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            RootTransform = root.transform;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                string rendererPath = MeshNodePath.GetRelativePath(renderer.transform, root.transform);
                AddMeshPath(mesh, rendererPath);
                if (!renderersByPath.ContainsKey(rendererPath))
                {
                    renderersByPath.Add(rendererPath, renderer);
                }
            }
        }
    }

}
