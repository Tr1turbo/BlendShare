using System.Collections.Generic;
using System.Linq;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
using Triturbo.BlendShare.Fbx.Ufbx;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Shared state for one FBX feature generation request.
    /// </summary>
    public sealed class FbxGenerationSession
    {
        private readonly Dictionary<string, object> stateByKey = new();

        public FbxNode RootNode { get; }
        public float ImportScale { get; }
        public UfbxScene ReaderScene { get; }
        public IBlendShareProgress Progress { get; }

        public FbxGenerationSession(
            FbxNode rootNode,
            float importScale,
            UfbxScene readerScene = null,
            IBlendShareProgress progress = null)
        {
            RootNode = rootNode;
            ImportScale = importScale == 0f ? 1f : importScale;
            ReaderScene = readerScene;
            Progress = BlendShareProgressUtility.Resolve(progress);
        }

        public bool TryGetState<T>(string key, out T state) where T : class
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

        public void SetState<T>(string key, T state) where T : class
        {
            if (string.IsNullOrWhiteSpace(key) || state == null)
            {
                return;
            }

            stateByKey[key] = state;
        }
    }

    /// <summary>
    /// Context passed to generators while applying or removing a feature on an FBX mesh node.
    /// </summary>
    public sealed class FbxGenerationContext
    {
        private readonly HashSet<MeshFeatureObject> handledFeatures = new();

        public BlendShareObject Share { get; }
        public MeshDataObject MeshData { get; }
        public FbxNode Node { get; }
        public FbxGenerationSession Session { get; }
        public bool RemoveInAllDeformer { get; }
        public IReadOnlyList<MeshFeatureObject> Features =>
            MeshData != null ? MeshData.Features : System.Array.Empty<MeshFeatureObject>();
        public bool HasUnhandledFeatures => Features.Any(feature => feature != null && !handledFeatures.Contains(feature));
        public FbxMesh TargetMesh => Node?.GetMesh();
        public FbxNode RootNode => Session?.RootNode;

        public FbxGenerationContext(
            BlendShareObject share,
            MeshDataObject meshData,
            FbxNode node,
            bool removeInAllDeformer = true,
            FbxGenerationSession session = null)
        {
            Share = share;
            MeshData = meshData;
            Node = node;
            Session = session;
            RemoveInAllDeformer = removeInAllDeformer;
        }

        public TFeature GetFeature<TFeature>() where TFeature : MeshFeatureObject
        {
            var feature = MeshData != null ? MeshData.GetFeature<TFeature>() : null;
            if (feature != null)
            {
                handledFeatures.Add(feature);
            }

            return feature;
        }

        public IEnumerable<MeshFeatureObject> GetUnhandledFeatures()
        {
            return Features.Where(feature => feature != null && !handledFeatures.Contains(feature));
        }
    }
}
#endif
