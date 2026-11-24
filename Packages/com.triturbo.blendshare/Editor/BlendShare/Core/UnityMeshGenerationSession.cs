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
    public sealed class UnityMeshGenerationSession : System.IDisposable
    {
        private readonly List<Object> generatedObjects = new();
        private readonly Dictionary<string, Object> generatedObjectsByKey = new();
        private readonly Dictionary<string, object> stateByKey = new();
        private readonly Dictionary<string, UnityMeshSkinBindingOutput> skinBindingsByMeshKey = new();
        private readonly Dictionary<string, UnityArmatureBoneData> armatureBonesByPath = new();
        private readonly Dictionary<string, BlendShareObject> armatureBoneSourcesByPath = new();
        private readonly Dictionary<FbxArmatureObject, FbxUnitySpaceConversion> armatureConversions = new();
        private readonly Dictionary<string, Matrix4x4> bindPosesByMeshAndBone = new();
        private readonly Dictionary<string, BlendShareObject> bindPoseSourcesByMeshAndBone = new();
        private readonly Dictionary<string, Transform> transformsByPath = new();
        private readonly List<UnityBoneBindingUse> boneBindingUses = new();
        private readonly Dictionary<string, Transform> boneTransformsByArtifactPath = new();
        private readonly Dictionary<Transform, string> artifactPathByBoneTransform = new();
        private readonly Dictionary<BlendShareBoneProxyBinding, string> artifactPathByBinding = new();
        private readonly HashSet<string> completedSteps = new();
        private UnityArmatureObject armature;
        internal UnityVertexMappingResolver Mappings { get; } = new();

        public Object TargetMeshContainer { get; }
        public IReadOnlyList<BlendShareObject> Patches { get; }
        public IReadOnlyList<BlendShareComponent> Components { get; }
        public UnityMeshTargetLookup TargetMeshes { get; }
        public IBlendShareProgress Progress { get; }
        public IReadOnlyList<Object> GeneratedObjects => generatedObjects;
        public IReadOnlyDictionary<string, UnityMeshSkinBindingOutput> SkinBindingsByMeshKey => skinBindingsByMeshKey;
        public UnityArmatureObject Armature => armature;
        internal IReadOnlyList<UnityBoneBindingUse> BoneBindingUses => boneBindingUses;
        internal IReadOnlyDictionary<string, Transform> BoneTransformsByArtifactPath =>
            boneTransformsByArtifactPath;
        internal IUnityGenerationPreparation Preparation { get; }
        internal UnityBonePathCollisionPolicy BonePathCollisionPolicy { get; }

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
            : this(
                targetMeshContainer,
                patches,
                targetMeshes,
                components,
                progress,
                null,
                UnityBonePathCollisionPolicy.Reject)
        {
        }

        internal UnityMeshGenerationSession(
            Object targetMeshContainer,
            IEnumerable<BlendShareObject> patches,
            UnityMeshTargetLookup targetMeshes,
            IEnumerable<BlendShareComponent> components,
            IBlendShareProgress progress,
            IUnityGenerationPreparation preparation = null,
            UnityBonePathCollisionPolicy bonePathCollisionPolicy = UnityBonePathCollisionPolicy.Reject)
        {
            TargetMeshContainer = targetMeshContainer;
            Patches = BlendSharePatchIdUtility.DeduplicateByPatchId(patches).ToArray();
            TargetMeshes = targetMeshes;
            Progress = BlendShareProgressUtility.Resolve(progress);
            Components = (components ?? System.Array.Empty<BlendShareComponent>())
                .Where(component => component != null)
                .Distinct()
                .ToArray();
            Preparation = preparation;
            BonePathCollisionPolicy = bonePathCollisionPolicy;
        }

        internal void AddBoneBindingUse(UnityBoneBindingUse use)
        {
            if (use != null)
            {
                use.Sequence = boneBindingUses.Count;
                boneBindingUses.Add(use);
            }
        }

        internal void ClearBoneBindingUses()
        {
            boneBindingUses.Clear();
            boneTransformsByArtifactPath.Clear();
            artifactPathByBoneTransform.Clear();
        }

        internal void ResolveBoneBindingUses()
        {
            boneTransformsByArtifactPath.Clear();
            artifactPathByBoneTransform.Clear();
            var reservedPaths = CollectExistingRendererBonePaths();
            foreach (var pair in reservedPaths)
            {
                boneTransformsByArtifactPath[pair.Key] = pair.Value;
            }
            var representatives = boneBindingUses
                .Where(use => use?.Transform != null)
                .GroupBy(use => use.Transform)
                .Select(group => group.OrderBy(use => use.Sequence).Last())
                .OrderBy(use => use.Sequence)
                .ToArray();
            var usedPaths = new HashSet<string>(reservedPaths.Keys, System.StringComparer.Ordinal);
            foreach (var use in representatives)
            {
                string artifactPath = ResolveBoneBindingArtifactPath(
                    use.Binding,
                    use.Transform,
                    use.FinalPath);
                bool pathCollision = usedPaths.Contains(artifactPath) &&
                                     (!boneTransformsByArtifactPath.TryGetValue(artifactPath, out var existing) ||
                                      existing != use.Transform);
                if (pathCollision && BonePathCollisionPolicy == UnityBonePathCollisionPolicy.Reject)
                {
                    Fail(
                        $"Distinct bone transforms resolve to the same final path '{artifactPath}'. " +
                        "Bind the source paths to one transform or use the NDMF build resolver.");
                    continue;
                }

                if (pathCollision && BonePathCollisionPolicy == UnityBonePathCollisionPolicy.VirtualizeForPreview)
                {
                    string parent = MeshNodePath.ParentPath(artifactPath);
                    string leaf = MeshNodePath.LeafName(artifactPath);
                    for (int counter = 1; counter < int.MaxValue; counter++)
                    {
                        string candidateLeaf = $"{leaf}_BlendSharePreview_{counter}";
                        string candidate = parent == MeshNodePath.Root
                            ? candidateLeaf
                            : $"{parent}/{candidateLeaf}";
                        candidate = MeshNodePath.Normalize(candidate);
                        if (usedPaths.Add(candidate))
                        {
                            artifactPath = candidate;
                            break;
                        }
                    }
                }
                else
                {
                    usedPaths.Add(artifactPath);
                }

                artifactPathByBoneTransform[use.Transform] = artifactPath;
                boneTransformsByArtifactPath[artifactPath] = use.Transform;
            }
        }

        private Dictionary<string, Transform> CollectExistingRendererBonePaths()
        {
            var result = new Dictionary<string, Transform>(System.StringComparer.Ordinal);
            var root = TargetMeshes?.RootTransform;
            if (root == null)
            {
                return result;
            }

            foreach (string meshKey in boneBindingUses
                         .Where(use => use != null)
                         .Select(use => use.MeshKey)
                         .Distinct(System.StringComparer.Ordinal))
            {
                if (!TargetMeshes.TryGetRenderer(meshKey, out var renderer) || renderer == null)
                {
                    continue;
                }

                foreach (var bone in renderer.bones ?? System.Array.Empty<Transform>())
                {
                    if (bone == null)
                    {
                        continue;
                    }

                    string path = MeshNodePath.Normalize(MeshNodePath.GetRelativePath(bone, root));
                    result[path] = bone;
                }
            }

            return result;
        }

        internal string ResolveBoneArtifactPath(Transform transform, string fallbackPath)
        {
            if (transform != null)
            {
                if (artifactPathByBoneTransform.TryGetValue(transform, out string artifactPath))
                {
                    return artifactPath;
                }
            }

            return MeshNodePath.Normalize(fallbackPath);
        }

        internal void SetBoneBindingArtifactPath(
            BlendShareBoneProxyBinding binding,
            string artifactPath)
        {
            if (binding != null && !string.IsNullOrWhiteSpace(artifactPath))
            {
                artifactPathByBinding[binding] = MeshNodePath.Normalize(artifactPath);
            }
        }

        internal string ResolveBoneBindingArtifactPath(
            BlendShareBoneProxyBinding binding,
            Transform transform,
            string fallbackPath)
        {
            return binding != null && artifactPathByBinding.TryGetValue(binding, out string artifactPath)
                ? artifactPath
                : ResolveBoneArtifactPath(transform, fallbackPath);
        }

        internal UnityBoneBindingUse ResolveBoneBindingUse(Transform transform)
        {
            return transform == null
                ? null
                : boneBindingUses
                    .Where(use => use?.Transform == transform)
                    .OrderBy(use => use.Sequence)
                    .LastOrDefault();
        }

        internal void RegisterBoneArtifactTransform(string artifactPath, Transform transform)
        {
            if (transform != null && !string.IsNullOrWhiteSpace(artifactPath))
            {
                boneTransformsByArtifactPath[MeshNodePath.Normalize(artifactPath)] = transform;
            }
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

        internal bool TryGetState<T>(string key, out T state) where T : class
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                stateByKey.TryGetValue(key, out var cached) &&
                cached is T typed)
            {
                state = typed;
                return true;
            }

            state = null;
            return false;
        }

        internal void SetState<T>(string key, T state) where T : class
        {
            if (!string.IsNullOrWhiteSpace(key) && state != null)
            {
                stateByKey[key] = state;
            }
        }

        internal IEnumerable<T> GetStates<T>() where T : class
        {
            return stateByKey.Values.OfType<T>();
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

        internal bool CanAddArmatureBones(
            IEnumerable<UnityArmatureBoneData> bones,
            BlendShareObject patch,
            out string error)
        {
            error = null;
            var pendingByPath = new Dictionary<string, UnityArmatureBoneData>();
            foreach (var bone in bones ?? System.Array.Empty<UnityArmatureBoneData>())
            {
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(bone.m_Path);
                bool hasRegisteredDefinition = armatureBonesByPath.TryGetValue(path, out var existing);
                if (!hasRegisteredDefinition)
                {
                    pendingByPath.TryGetValue(path, out existing);
                }

                if (existing != null &&
                    !boneTransformsByArtifactPath.ContainsKey(path) &&
                    !AreCompatibleBoneDefinitions(existing, bone))
                {
                    BlendShareObject existingPatch = patch;
                    if (hasRegisteredDefinition)
                    {
                        armatureBoneSourcesByPath.TryGetValue(path, out existingPatch);
                    }

                    error = $"Bone path '{path}' has incompatible definitions in patches " +
                            $"'{FormatPatchName(existingPatch)}' and '{FormatPatchName(patch)}'.";
                    return false;
                }

                if (!hasRegisteredDefinition && existing == null)
                {
                    pendingByPath.Add(path, bone);
                }
            }

            return true;
        }

        public void AddArmatureBones(IEnumerable<UnityArmatureBoneData> bones)
        {
            AddArmatureBones(bones, null);
        }

        internal void AddArmatureBones(IEnumerable<UnityArmatureBoneData> bones, BlendShareObject patch)
        {
            foreach (var bone in bones ?? System.Array.Empty<UnityArmatureBoneData>())
            {
                if (bone == null)
                {
                    continue;
                }

                string path = MeshNodePath.Normalize(bone.m_Path);
                if (boneTransformsByArtifactPath.ContainsKey(path))
                {
                    armatureBonesByPath[path] = bone;
                    armatureBoneSourcesByPath[path] = patch;
                }
                else if (!armatureBonesByPath.ContainsKey(path))
                {
                    armatureBonesByPath.Add(path, bone);
                    armatureBoneSourcesByPath[path] = patch;
                }
            }

            if (armatureBonesByPath.Count == 0)
            {
                return;
            }

            if (armature == null)
            {
                armature = ScriptableObject.CreateInstance<UnityArmatureObject>();
                armature.name = "Armature";
                AddObject("Armature", armature);
            }

            armature.SetBones(armatureBonesByPath.Values);
            EditorUtility.SetDirty(armature);
        }

        internal void Fail(string diagnostic)
        {
            FatalDiagnostic ??= diagnostic;
        }

        internal string FatalDiagnostic { get; private set; }

        internal bool RegisterBindPose(
            string meshKey,
            string bonePath,
            Matrix4x4 bindPose,
            BlendShareObject patch,
            bool replaceExisting,
            out string error)
        {
            string key = $"{MeshNodePath.Normalize(meshKey)}::{MeshNodePath.Normalize(bonePath)}";
            if (!replaceExisting &&
                bindPosesByMeshAndBone.TryGetValue(key, out var existing) &&
                !AreEquivalentMatrices(existing, bindPose))
            {
                bindPoseSourcesByMeshAndBone.TryGetValue(key, out var existingPatch);
                error = $"Bone path '{MeshNodePath.Normalize(bonePath)}' has incompatible bindposes " +
                        $"for mesh '{MeshNodePath.Normalize(meshKey)}' in patches " +
                        $"'{FormatPatchName(existingPatch)}' and '{FormatPatchName(patch)}'.";
                return false;
            }

            if (replaceExisting || !bindPosesByMeshAndBone.ContainsKey(key))
            {
                bindPosesByMeshAndBone[key] = bindPose;
                bindPoseSourcesByMeshAndBone[key] = patch;
            }

            error = null;
            return true;
        }

        internal bool RegisterArmatureConversion(
            FbxArmatureObject sourceArmature,
            FbxUnitySpaceConversion conversion,
            out string error)
        {
            error = null;
            if (sourceArmature == null)
            {
                return true;
            }

            if (armatureConversions.TryGetValue(sourceArmature, out var existing) &&
                !existing.ApproximatelyEquals(conversion))
            {
                error = $"Armature '{sourceArmature.name}' is referenced through incompatible FBX importer settings. " +
                        "All meshes contributing one shared armature must use the same import scale and Bake Axis Conversion setting.";
                return false;
            }

            armatureConversions[sourceArmature] = conversion;
            return true;
        }

        private static bool AreCompatibleBoneDefinitions(UnityArmatureBoneData first, UnityArmatureBoneData second)
        {
            if (first == null || second == null)
            {
                return first == second;
            }

            return Vector3.Distance(first.LocalTransform.Position, second.LocalTransform.Position) <= 0.0001f &&
                   Quaternion.Angle(first.LocalTransform.Rotation, second.LocalTransform.Rotation) <= 0.01f &&
                   Vector3.Distance(first.LocalTransform.Scale, second.LocalTransform.Scale) <= 0.0001f;
        }

        private static bool AreEquivalentMatrices(Matrix4x4 first, Matrix4x4 second)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(first[row, column] - second[row, column]) > 0.0001f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string FormatPatchName(BlendShareObject patch)
        {
            return patch != null ? patch.name : "<unknown>";
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

        public void Dispose()
        {
            Mappings.Dispose();
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
            Mappings.Dispose();
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

    internal enum UnityBonePathCollisionPolicy
    {
        Reject,
        VirtualizeForPreview
    }

    internal readonly struct UnityGenerationPreparationResult
    {
        private UnityGenerationPreparationResult(
            bool succeeded,
            bool requiresRecollection,
            string diagnostic)
        {
            Succeeded = succeeded;
            RequiresRecollection = requiresRecollection;
            Diagnostic = diagnostic;
        }

        internal bool Succeeded { get; }
        internal bool RequiresRecollection { get; }
        internal string Diagnostic { get; }

        internal static UnityGenerationPreparationResult Success(bool requiresRecollection)
        {
            return new UnityGenerationPreparationResult(true, requiresRecollection, null);
        }

        internal static UnityGenerationPreparationResult Failed(string diagnostic)
        {
            return new UnityGenerationPreparationResult(false, false, diagnostic);
        }
    }

    internal interface IUnityGenerationPreparation
    {
        UnityGenerationPreparationResult Prepare(UnityMeshGenerationSession session);
        void Complete(UnityMeshGenerationSession session);
    }

    internal sealed class UnityBoneTransformCapture : IUnityGenerationPreparation
    {
        private readonly Dictionary<string, Transform> transformsByPath = new();

        public IReadOnlyDictionary<string, Transform> TransformsByPath => transformsByPath;

        public UnityGenerationPreparationResult Prepare(UnityMeshGenerationSession session)
        {
            return UnityGenerationPreparationResult.Success(false);
        }

        public void Complete(UnityMeshGenerationSession session)
        {
            transformsByPath.Clear();
            foreach (var pair in session?.BoneTransformsByArtifactPath ??
                                 new Dictionary<string, Transform>())
            {
                transformsByPath[pair.Key] = pair.Value;
            }
        }
    }

    internal sealed class UnityBoneBindingUse
    {
        public UnityBoneBindingUse(
            string meshKey,
            int order,
            FbxArmatureObject armature,
            string sourceBonePath,
            BlendShareBoneProxy proxy,
            BlendShareBoneProxyBinding binding,
            Transform transform,
            string finalPath,
            Matrix4x4 bindPose)
        {
            MeshKey = MeshNodePath.Normalize(meshKey);
            Order = order;
            Armature = armature;
            SourceBonePath = MeshNodePath.Normalize(sourceBonePath);
            Proxy = proxy;
            Binding = binding;
            Transform = transform;
            FinalPath = MeshNodePath.Normalize(finalPath);
            BindPose = bindPose;
        }

        public string MeshKey { get; }
        public int Order { get; }
        internal int Sequence { get; set; }
        public FbxArmatureObject Armature { get; }
        public string SourceBonePath { get; }
        public BlendShareBoneProxy Proxy { get; }
        public BlendShareBoneProxyBinding Binding { get; }
        public Transform Transform { get; }
        public string FinalPath { get; }
        public Matrix4x4 BindPose { get; }
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
