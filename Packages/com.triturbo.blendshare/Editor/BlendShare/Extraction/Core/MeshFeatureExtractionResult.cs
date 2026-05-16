namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Describes the outcome of a mesh feature extraction step.
    /// </summary>
    public enum MeshFeatureExtractionStatus
    {
        Succeeded,
        Skipped,
        Failed
    }

    /// <summary>
    /// Result returned by feature extractors to report success, skipped work, or failure.
    /// </summary>
    public readonly struct MeshFeatureExtractionResult
    {
        public MeshFeatureExtractionStatus Status { get; }
        public string Message { get; }
        public bool Succeeded => Status == MeshFeatureExtractionStatus.Succeeded;

        private MeshFeatureExtractionResult(MeshFeatureExtractionStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public static MeshFeatureExtractionResult Success()
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Succeeded, null);
        }

        public static MeshFeatureExtractionResult Skipped(string message = null)
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Skipped, message);
        }

        public static MeshFeatureExtractionResult Failed(string message)
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Failed, message);
        }
    }
}
