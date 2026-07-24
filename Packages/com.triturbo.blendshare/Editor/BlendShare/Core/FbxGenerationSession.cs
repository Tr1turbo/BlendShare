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

        internal IEnumerable<T> GetStates<T>() where T : class
        {
            return stateByKey.Values.OfType<T>();
        }
    }
}
#endif
