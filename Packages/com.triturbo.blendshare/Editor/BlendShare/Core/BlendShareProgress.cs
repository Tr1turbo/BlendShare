using System;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    public interface IBlendShareProgress
    {
        bool Report(string title, string detail, float progress, bool cancelable);
    }

    public sealed class BlendShareOperationCanceledException : OperationCanceledException
    {
        public BlendShareOperationCanceledException()
            : base(BlendShareProgressUtility.CanceledMessage)
        {
        }
    }

    public static class BlendShareProgressUtility
    {
        public const string CanceledMessage = "BlendShare operation cancelled.";

        private static readonly IBlendShareProgress NullProgress = new NullBlendShareProgress();

        public static IBlendShareProgress Resolve(IBlendShareProgress progress)
        {
            return progress ?? NullProgress;
        }

        public static void Report(
            IBlendShareProgress progress,
            string title,
            string detail,
            float value,
            bool cancelable)
        {
            if (Resolve(progress).Report(title, detail, value, cancelable))
            {
                throw new BlendShareOperationCanceledException();
            }
        }

        private sealed class NullBlendShareProgress : IBlendShareProgress
        {
            public bool Report(string title, string detail, float progress, bool cancelable)
            {
                return false;
            }
        }
    }

    public sealed class BlendShareEditorProgress : IBlendShareProgress, IDisposable
    {
        private readonly string defaultTitle;
        private bool disposed;

        private BlendShareEditorProgress(string defaultTitle)
        {
            this.defaultTitle = defaultTitle;
        }

        public static BlendShareEditorProgress Create(string defaultTitle)
        {
            return new BlendShareEditorProgress(defaultTitle);
        }

        public bool Report(string title, string detail, float progress, bool cancelable)
        {
            if (disposed)
            {
                return false;
            }

            string displayTitle = string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
            float displayProgress = Mathf.Clamp01(progress);
            if (cancelable)
            {
                return EditorUtility.DisplayCancelableProgressBar(displayTitle, detail, displayProgress);
            }

            EditorUtility.DisplayProgressBar(displayTitle, detail, displayProgress);
            return false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            EditorUtility.ClearProgressBar();
        }
    }
}
