namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Describes the outcome of a mesh feature generation step.
    /// </summary>
    public enum MeshFeatureGenerationStatus
    {
        Succeeded,
        Skipped,
        Failed
    }

    /// <summary>
    /// Result returned by feature generators to report success, skipped work, or failure.
    /// </summary>
    public readonly struct MeshFeatureGenerationResult
    {
        public MeshFeatureGenerationStatus Status { get; }
        public string Message { get; }
        public bool Modified { get; }

        public bool Succeeded => Status == MeshFeatureGenerationStatus.Succeeded;
        public bool Failed => Status == MeshFeatureGenerationStatus.Failed;

        private MeshFeatureGenerationResult(
            MeshFeatureGenerationStatus status,
            string message,
            bool modified)
        {
            Status = status;
            Message = message;
            Modified = modified;
        }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        /// <param name="modified">Whether the generator modified the target mesh or FBX node.</param>
        /// <param name="message">Optional diagnostic message.</param>
        /// <returns>A successful generation result.</returns>
        public static MeshFeatureGenerationResult Success(bool modified = true, string message = null)
        {
            return new MeshFeatureGenerationResult(MeshFeatureGenerationStatus.Succeeded, message, modified);
        }

        /// <summary>
        /// Creates a skipped result for a feature that had no work to perform.
        /// </summary>
        /// <param name="message">Optional diagnostic message.</param>
        /// <returns>A skipped generation result.</returns>
        public static MeshFeatureGenerationResult Skipped(string message = null)
        {
            return new MeshFeatureGenerationResult(MeshFeatureGenerationStatus.Skipped, message, false);
        }

        /// <summary>
        /// Creates a failed result.
        /// </summary>
        /// <param name="message">Diagnostic message describing why generation failed.</param>
        /// <returns>A failed generation result.</returns>
        public static MeshFeatureGenerationResult FailedResult(string message)
        {
            return new MeshFeatureGenerationResult(MeshFeatureGenerationStatus.Failed, message, false);
        }
    }
}
